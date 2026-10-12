using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>一个 U 盘软件包及其文件校验结果。</summary>
public sealed class SoftwarePackage
{
    /// <summary>包描述。</summary>
    public required SoftwarePackageDescriptor Descriptor { get; init; }

    /// <summary>安装程序的完整性校验结果。</summary>
    public required PackageFileCheck Check { get; init; }

    /// <summary>安装程序大小（字节）；文件缺失时为 0。</summary>
    public required long InstallerSizeBytes { get; init; }

    /// <summary>该包当前是否允许执行（仅“校验通过”允许）。</summary>
    public bool CanExecute => PackageRules.CanExecute(Check.Status);
}

/// <summary>软件包目录的加载结果。</summary>
/// <param name="Packages">已加载的软件包（含校验结果）。</param>
/// <param name="Problems">描述文件问题说明（缺目录、解析失败、标识重复、路径越界等）。</param>
public sealed record PackageCatalog(
    IReadOnlyList<SoftwarePackage> Packages,
    IReadOnlyList<string> Problems);

/// <summary>
/// 软件包库加载器（M5）：从 U 盘 <c>packages/</c> 目录读取包描述文件（JSON），
/// 逐个校验安装程序是否存在、SHA-256 是否与描述一致；不匹配的包标记为“不可用”并禁止执行。
/// 程序不提供仓库维护、版本同步与分发管理动作——由谁放包、多久更新一次由现场流程决定。
/// </summary>
public interface IPackageLibraryLoader
{
    /// <summary>
    /// 从指定基准目录下的 <c>packages/</c> 加载软件包目录。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录（U 盘根）；读取 <c>packages/*.json</c>。</param>
    /// <returns>软件包目录加载结果。</returns>
    PackageCatalog Load(string baseDirectory);
}

/// <summary>目标机已装软件清单的只读读取器（注册表 Uninstall 键）。</summary>
public interface IInstalledSoftwareReader
{
    /// <summary>
    /// 读取目标机已安装软件清单（只读，不产生任何变更）。
    /// </summary>
    /// <returns>已安装软件条目。</returns>
    IReadOnlyList<InstalledSoftwareEntry> Read();
}

/// <inheritdoc cref="IInstalledSoftwareReader" />
public sealed class InstalledSoftwareReader : IInstalledSoftwareReader
{
    /// <inheritdoc />
    public IReadOnlyList<InstalledSoftwareEntry> Read()
        => WindowsCollectionItems.EnumerateInstalledSoftware()
            .Select(entry => new InstalledSoftwareEntry(
                entry.Name,
                entry.Publisher,
                entry.Version,
                entry.UninstallString,
                entry.InstallLocation))
            .ToList();
}

/// <inheritdoc cref="IPackageLibraryLoader" />
public sealed class PackageLibraryLoader : IPackageLibraryLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public PackageCatalog Load(string baseDirectory)
    {
        var root = Path.Combine(baseDirectory, PackageRules.PackagesFolderName);
        var problems = new List<string>();

        if (!Directory.Exists(root))
        {
            return new PackageCatalog(
                Array.Empty<SoftwarePackage>(),
                new[] { $"未找到软件包目录：{PackageRules.PackagesFolderName}（应位于程序所在目录下）。" });
        }

        var rootFullPath = Path.GetFullPath(root);
        var packages = new List<SoftwarePackage>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(root, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            SoftwarePackageDescriptor? descriptor;

            try
            {
                var text = File.ReadAllText(file);
                descriptor = JsonSerializer.Deserialize<SoftwarePackageDescriptor>(text, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{fileName}：解析失败（{ex.Message}）。");
                continue;
            }

            if (descriptor is null)
            {
                problems.Add($"{fileName}：描述文件为空。");
                continue;
            }

            var problem = Validate(descriptor, seenIds);
            if (problem is not null)
            {
                problems.Add($"{fileName}：{problem}");
                continue;
            }

            if (!TryResolveInstallerPath(descriptor, rootFullPath, out var installerPath))
            {
                problems.Add($"{fileName}：安装程序路径越界（不得使用 packages 目录之外的路径）。");
                continue;
            }

            seenIds.Add(descriptor.PackageId);
            packages.Add(Build(descriptor, installerPath));
        }

        return new PackageCatalog(packages, problems);
    }

    /// <summary>
    /// 计算安装程序的校验结果：文件存在性与 SHA-256 一并判定。
    /// </summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="installerPath">安装程序完整路径。</param>
    /// <returns>软件包（含校验结果）。</returns>
    private static SoftwarePackage Build(SoftwarePackageDescriptor descriptor, string installerPath)
    {
        var fileExists = File.Exists(installerPath);
        string? actualHash = null;
        long size = 0;

        if (fileExists)
        {
            try
            {
                var info = new FileInfo(installerPath);
                size = info.Length;
                using var stream = File.OpenRead(installerPath);
                actualHash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 无法读取即按“文件缺失”处理（同样禁止执行，原因如实呈现）。
                fileExists = false;
                actualHash = null;
                size = 0;
            }
        }

        var status = PackageRules.EvaluateIntegrity(fileExists, descriptor.Sha256, actualHash);
        return new SoftwarePackage
        {
            Descriptor = descriptor,
            Check = new PackageFileCheck(status, installerPath, actualHash),
            InstallerSizeBytes = size
        };
    }

    /// <summary>
    /// 校验单个包描述；通过返回 null，否则返回问题说明。
    /// </summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="seenIds">已登记的包标识集合（用于查重）。</param>
    /// <returns>问题说明；无问题返回 null。</returns>
    private static string? Validate(SoftwarePackageDescriptor descriptor, ISet<string> seenIds)
    {
        if (string.IsNullOrWhiteSpace(descriptor.PackageId))
        {
            return "包标识为空。";
        }

        if (seenIds.Contains(descriptor.PackageId))
        {
            return $"包标识重复：{descriptor.PackageId}。";
        }

        if (string.IsNullOrWhiteSpace(descriptor.Name))
        {
            return $"{descriptor.PackageId}：软件名称为空。";
        }

        if (string.IsNullOrWhiteSpace(descriptor.Version))
        {
            return $"{descriptor.PackageId}：版本为空。";
        }

        if (string.IsNullOrWhiteSpace(descriptor.InstallerFileName))
        {
            return $"{descriptor.PackageId}：缺少安装程序文件名。";
        }

        return null;
    }

    /// <summary>
    /// 解析安装程序完整路径，并确保其位于 <c>packages/</c> 目录之内（防止描述文件引用越界路径）。
    /// </summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="rootFullPath">packages 目录完整路径。</param>
    /// <param name="installerPath">解析出的安装程序完整路径。</param>
    /// <returns>路径合法返回 true。</returns>
    private static bool TryResolveInstallerPath(
        SoftwarePackageDescriptor descriptor,
        string rootFullPath,
        out string installerPath)
    {
        installerPath = string.Empty;

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(rootFullPath, descriptor.InstallerFileName));
            var normalizedRoot = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
                ? rootFullPath
                : rootFullPath + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            installerPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}