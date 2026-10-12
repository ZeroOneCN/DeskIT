using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 自定义命令历史存储（M9）。落盘位置为 U 盘数据目录下的 <c>data\custom\commands.db</c>，
/// 采用仅追加（append-only）NDJSON：每行一条 JSON，经介质金库逐行加密后写入；单行损坏不影响其他行。
/// 记录为强制落盘：写入失败即视为记录失败（M9 不提供“不记录执行”的路径）。
/// </summary>
public interface ICustomCommandStore
{
    /// <summary>
    /// 追加一条自定义命令执行记录（只追加，不改写历史行）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="record">命令执行记录。</param>
    void Append(string dataDirectory, CustomCommandRecord record);

    /// <summary>
    /// 读取全部命令执行记录（按写入顺序）；文件不存在时返回空列表。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>命令执行记录列表。</returns>
    IReadOnlyList<CustomCommandRecord> LoadAll(string dataDirectory);
}

/// <inheritdoc cref="ICustomCommandStore" />
public sealed class CustomCommandStore : ICustomCommandStore
{
    private const string FolderName = "custom";
    private const string FileName = "commands.db";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IMediaVault? _vault;

    /// <summary>
    /// 初始化命令历史存储。
    /// </summary>
    /// <param name="vault">介质金库；为 null 时以明文落盘（仅供测试或未加密场景）。</param>
    public CustomCommandStore(IMediaVault? vault = null)
    {
        _vault = vault;
    }

    /// <inheritdoc />
    public void Append(string dataDirectory, CustomCommandRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var path = GetPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var line = JsonSerializer.Serialize(record, JsonOptions);
        var stored = _vault is null ? line : _vault.Protect(line);
        File.AppendAllText(path, stored + Environment.NewLine);
    }

    /// <inheritdoc />
    public IReadOnlyList<CustomCommandRecord> LoadAll(string dataDirectory)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path))
        {
            return Array.Empty<CustomCommandRecord>();
        }

        var records = new List<CustomCommandRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var plain = TryUnprotect(line);
            if (plain is null)
            {
                // 单行损坏或校验失败不影响其他行。
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<CustomCommandRecord>(plain, JsonOptions);
                if (parsed is not null)
                {
                    records.Add(parsed);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // 单行损坏不影响其他行。
            }
        }

        return records;
    }

    /// <summary>解密一行（若为信封）；未解锁、校验失败或解析异常时返回 null。</summary>
    /// <param name="line">文件中的一行。</param>
    /// <returns>明文 JSON 或 null。</returns>
    private string? TryUnprotect(string line)
    {
        if (_vault is null || !MediaRules.IsEnvelope(line))
        {
            return line;
        }

        try
        {
            return _vault.Unprotect(line);
        }
        catch (Exception ex) when (ex is MediaLockedException or MediaIntegrityException)
        {
            return null;
        }
    }

    /// <summary>命令历史文件路径（data\custom\commands.db）。</summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <returns>命令历史文件绝对路径。</returns>
    private static string GetPath(string dataDirectory) => Path.Combine(dataDirectory, FolderName, FileName);
}