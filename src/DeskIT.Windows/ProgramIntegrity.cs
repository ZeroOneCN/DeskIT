using System.Security.Cryptography;
using System.Text.Json;

namespace DeskIT.Windows;

/// <summary>程序文件完整性校验报告。</summary>
/// <param name="ManifestPresent">清单文件 manifest.json 是否存在。</param>
/// <param name="Total">清单中登记的文件总数。</param>
/// <param name="Matched">哈希比对通过的文件数。</param>
/// <param name="Mismatches">不匹配或缺失的文件相对路径清单。</param>
public sealed record IntegrityReport(bool ManifestPresent, int Total, int Matched, IReadOnlyList<string> Mismatches);

/// <summary>程序文件完整性校验器（M1 自检项：本地清单哈希比对）。</summary>
public interface IProgramIntegrityVerifier
{
    /// <summary>
    /// 依据程序目录下的 manifest.json 逐文件比对 SHA-256。
    /// </summary>
    /// <param name="programDirectory">程序所在目录（以可执行文件所在目录为基准）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>完整性校验报告。</returns>
    Task<IntegrityReport> VerifyAsync(string programDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于 manifest.json 的完整性校验器。
/// 清单格式：{ "version": "1.0.0", "files": [ { "path": "DeskIT.exe", "sha256": "..." } ] }
/// </summary>
public sealed class ProgramIntegrityVerifier : IProgramIntegrityVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public async Task<IntegrityReport> VerifyAsync(
        string programDirectory,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(programDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new IntegrityReport(ManifestPresent: false, Total: 0, Matched: 0, Mismatches: Array.Empty<string>());
        }

        ManifestDocument? document;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            document = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return new IntegrityReport(ManifestPresent: true, Total: 0, Matched: 0, Mismatches: Array.Empty<string>());
        }

        var entries = document?.Files ?? new List<ManifestFileEntry>();
        if (entries.Count == 0)
        {
            return new IntegrityReport(ManifestPresent: true, Total: 0, Matched: 0, Mismatches: Array.Empty<string>());
        }

        var matched = 0;
        var mismatches = new List<string>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256))
            {
                continue;
            }

            var relativePath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(programDirectory, relativePath);

            if (!File.Exists(fullPath))
            {
                mismatches.Add(entry.Path);
                continue;
            }

            var actualHash = await ComputeSha256Async(fullPath, cancellationToken);
            if (string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
            }
            else
            {
                mismatches.Add(entry.Path);
            }
        }

        return new IntegrityReport(ManifestPresent: true, Total: entries.Count, Matched: matched, Mismatches: mismatches);
    }

    /// <summary>计算指定文件的 SHA-256 十六进制摘要（大写）。</summary>
    /// <param name="filePath">文件绝对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>十六进制摘要字符串。</returns>
    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes);
    }

    private sealed class ManifestDocument
    {
        public string? Version { get; set; }

        public List<ManifestFileEntry> Files { get; set; } = new();
    }

    private sealed class ManifestFileEntry
    {
        public string? Path { get; set; }

        public string? Sha256 { get; set; }
    }
}