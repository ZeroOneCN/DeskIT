using System.Globalization;
using System.Text;

namespace DeskIT.Core;

/// <summary>
/// M5 软件安装与卸载的可单元测试纯规则：包完整性校验、架构与空间检查、与已装清单的匹配、
/// 安装器退出码归因，以及把“某个包的一次操作”转换为可复用 M4 执行器的动作定义。
/// 本类不依赖任何 Windows API；文件哈希计算、注册表读取与脚本执行由 DeskIT.Windows 提供。
/// </summary>
public static class PackageRules
{
    /// <summary>软件包目录名（U 盘 <c>packages/</c>）。</summary>
    public const string PackagesFolderName = "packages";

    /// <summary>包操作默认超时秒数（30 分钟）：安装类动作可能长时间无输出。</summary>
    public const int DefaultTimeoutSeconds = 1800;

    /// <summary>
    /// 判定包完整性：文件不存在优先于哈希判定；未声明哈希视为“来源不可验证”。
    /// </summary>
    /// <param name="fileExists">安装程序文件是否存在。</param>
    /// <param name="declaredSha256">描述文件声明的 SHA-256。</param>
    /// <param name="actualSha256">实际计算出的 SHA-256。</param>
    /// <returns>完整性状态。</returns>
    public static PackageIntegrityStatus EvaluateIntegrity(
        bool fileExists,
        string? declaredSha256,
        string? actualSha256)
    {
        if (!fileExists)
        {
            return PackageIntegrityStatus.FileMissing;
        }

        if (string.IsNullOrWhiteSpace(declaredSha256))
        {
            return PackageIntegrityStatus.HashNotDeclared;
        }

        return string.Equals(NormalizeHash(declaredSha256), NormalizeHash(actualSha256), StringComparison.Ordinal)
            ? PackageIntegrityStatus.Verified
            : PackageIntegrityStatus.HashMismatch;
    }

    /// <summary>该完整性状态下是否允许执行：仅“校验通过”允许。</summary>
    /// <param name="status">完整性状态。</param>
    /// <returns>允许执行返回 true。</returns>
    public static bool CanExecute(PackageIntegrityStatus status) => status == PackageIntegrityStatus.Verified;

    /// <summary>规范化哈希文本：去除空白与连字符并转为大写，便于比较与展示。</summary>
    /// <param name="hash">原始哈希文本。</param>
    /// <returns>规范化后的哈希；为空时返回空串。</returns>
    public static string NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(hash.Length);
        foreach (var ch in hash)
        {
            if (ch is ' ' or '-' or ':' || ch == '\t')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    /// <summary>完整性状态的中文说明。</summary>
    /// <param name="status">完整性状态。</param>
    /// <returns>中文说明。</returns>
    public static string DescribeIntegrity(PackageIntegrityStatus status) => status switch
    {
        PackageIntegrityStatus.Verified => "校验通过",
        PackageIntegrityStatus.HashMismatch => "哈希不匹配",
        PackageIntegrityStatus.FileMissing => "文件缺失",
        _ => "未声明哈希"
    };

    /// <summary>
    /// 完整性状态对应的处置说明（不可用时说明原因与下一步动作）。
    /// </summary>
    /// <param name="status">完整性状态。</param>
    /// <returns>处置说明；校验通过时为空串。</returns>
    public static string DescribeIntegrityRemedy(PackageIntegrityStatus status) => status switch
    {
        PackageIntegrityStatus.Verified => string.Empty,
        PackageIntegrityStatus.HashMismatch => "安装程序与描述文件声明的哈希不一致，已禁止执行；请重新部署该包。",
        PackageIntegrityStatus.FileMissing => "安装程序不在 U 盘 packages 目录，已禁止执行；请先把安装程序同步到 U 盘。",
        _ => "描述文件未声明校验哈希，来源不可验证，已禁止执行；请补充 sha256 后重新部署。"
    };

    /// <summary>
    /// 架构是否兼容：Any 兼容全部；X64 需要 64 位系统；X86 在两种系统上均可安装。
    /// </summary>
    /// <param name="architecture">包适用架构。</param>
    /// <param name="targetIs64Bit">目标机是否为 64 位系统。</param>
    /// <returns>兼容返回 true。</returns>
    public static bool ArchitectureCompatible(PackageArchitecture architecture, bool targetIs64Bit)
        => architecture switch
        {
            PackageArchitecture.X64 => targetIs64Bit,
            _ => true
        };

    /// <summary>架构的中文说明。</summary>
    /// <param name="architecture">包适用架构。</param>
    /// <returns>中文说明。</returns>
    public static string DescribeArchitecture(PackageArchitecture architecture) => architecture switch
    {
        PackageArchitecture.X64 => "64 位",
        PackageArchitecture.X86 => "32 位",
        _ => "不限"
    };

    /// <summary>可用空间是否满足包要求的最小值。</summary>
    /// <param name="freeBytes">目标盘可用字节数。</param>
    /// <param name="minFreeDiskMb">要求的最小可用空间（MB）。</param>
    /// <returns>满足返回 true。</returns>
    public static bool DiskSpaceSufficient(long freeBytes, int minFreeDiskMb)
        => minFreeDiskMb <= 0 || freeBytes >= (long)minFreeDiskMb * 1024 * 1024;

    /// <summary>
    /// 比较两个版本号：按点分数字逐段比较（缺省段视为 0，因此 1.0 与 1.0.0.0 视为相同），
    /// 存在非数字段（如 1.0.0-beta）时回退为忽略大小写的字符串比较。
    /// </summary>
    /// <param name="left">左侧版本。</param>
    /// <param name="right">右侧版本。</param>
    /// <returns>left 小于 / 等于 / 大于 right 时分别返回负数 / 0 / 正数。</returns>
    public static int CompareVersions(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(left))
        {
            return -1;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return 1;
        }

        if (TryParseVersionParts(left, out var leftParts) && TryParseVersionParts(right, out var rightParts))
        {
            for (var index = 0; index < leftParts.Length; index++)
            {
                var comparison = leftParts[index].CompareTo(rightParts[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }

        return string.Compare(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把版本号解析为固定 4 段（最多取前 4 段，不足补 0）；存在非数字段时返回 false。
    /// </summary>
    /// <param name="text">版本号文本。</param>
    /// <param name="parts">解析结果（固定长度 4）。</param>
    /// <returns>解析成功返回 true。</returns>
    private static bool TryParseVersionParts(string text, out int[] parts)
    {
        parts = new int[4];
        var segments = text.Trim().Split('.');
        if (segments.Length == 0 || segments.Length > 4)
        {
            return false;
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (!int.TryParse(segments[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < 0)
            {
                return false;
            }

            parts[index] = value;
        }

        return true;
    }

    /// <summary>
    /// 把包与目标机已装软件清单做匹配：先按显示名精确匹配（忽略大小写与首尾空白），
    /// 双方都声明了发布者时再要求发布者一致，避免同名不同厂商误判。
    /// </summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="installed">目标机已装软件清单。</param>
    /// <returns>匹配状态。</returns>
    public static PackageMatchStatus MatchInstalled(
        SoftwarePackageDescriptor descriptor,
        IReadOnlyList<InstalledSoftwareEntry> installed)
    {
        var matchName = descriptor.MatchName.Trim();
        var candidates = installed
            .Where(entry => string.Equals(entry.Name.Trim(), matchName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return PackageMatchStatus.NotInstalled;
        }

        var publishersMatch = candidates.Where(entry => PublisherMatches(descriptor.Publisher, entry.Publisher)).ToList();
        if (publishersMatch.Count > 0)
        {
            candidates = publishersMatch;
        }
        else if (!string.IsNullOrWhiteSpace(descriptor.Publisher)
                 && candidates.All(entry => !string.IsNullOrWhiteSpace(entry.Publisher)))
        {
            // 包与已装条目都声明了发布者但互不匹配：视为同名不同厂商，不判定为“已安装”。
            return PackageMatchStatus.NotInstalled;
        }

        if (string.IsNullOrWhiteSpace(descriptor.Version))
        {
            return PackageMatchStatus.InstalledUnknownVersion;
        }

        var comparison = candidates
            .Select(entry => CompareVersions(entry.Version, descriptor.Version))
            .ToList();

        return comparison.Any(result => result == 0)
            ? PackageMatchStatus.InstalledSameVersion
            : PackageMatchStatus.InstalledDifferentVersion;
    }

    /// <summary>匹配状态的中文说明。</summary>
    /// <param name="status">匹配状态。</param>
    /// <returns>中文说明。</returns>
    public static string DescribeMatch(PackageMatchStatus status) => status switch
    {
        PackageMatchStatus.InstalledSameVersion => "已安装（版本一致）",
        PackageMatchStatus.InstalledDifferentVersion => "已安装（版本不一致）",
        PackageMatchStatus.InstalledUnknownVersion => "已安装（版本未知）",
        _ => "未安装"
    };

    /// <summary>操作类型的中文说明。</summary>
    /// <param name="kind">操作类型。</param>
    /// <returns>中文说明。</returns>
    public static string DescribeOperationKind(PackageOperationKind kind) => kind switch
    {
        PackageOperationKind.Upgrade => "升级",
        PackageOperationKind.Uninstall => "卸载",
        _ => "安装"
    };

    /// <summary>
    /// 安装器退出码归因（PRD M5 异常处理）：0 为成功，3010 / 1641 为“成功但需重启”，
    /// 其余按常见安装器错误码给出原因类别；未知码如实记为“未知”而不误判为成功。
    /// </summary>
    /// <param name="exitCode">安装器退出码。</param>
    /// <returns>归因结果。</returns>
    public static InstallerOutcome ClassifyInstallerExitCode(int exitCode) => exitCode switch
    {
        0 => new InstallerOutcome(InstallerOutcomeKind.Success, true, false, "安装器返回成功（0）。"),
        3010 => new InstallerOutcome(InstallerOutcomeKind.SuccessPendingRestart, true, true, "安装 / 卸载成功，需重启后生效（3010）。"),
        1641 => new InstallerOutcome(InstallerOutcomeKind.SuccessPendingRestart, true, true, "安装 / 卸载成功，安装器已请求重启（1641）。"),
        1618 => new InstallerOutcome(InstallerOutcomeKind.AlreadyInProgress, false, false, "另一个安装 / 更新正在进行（1618），请等待其完成或重启后重试。"),
        1625 => new InstallerOutcome(InstallerOutcomeKind.BlockedByPolicy, false, false, "被系统策略阻止（1625），请联系系统管理员放行后再试。"),
        1260 => new InstallerOutcome(InstallerOutcomeKind.BlockedByPolicy, false, false, "被安全策略阻止（1260），无法在本机执行该安装。"),
        1603 => new InstallerOutcome(InstallerOutcomeKind.FatalError, false, false, "安装失败（1603）：常见原因为权限不足、被安全软件拦截或安装包不完整；程序不会自动关闭安全软件。"),
        1638 => new InstallerOutcome(InstallerOutcomeKind.VersionConflict, false, false, "已安装另一个版本（1638），请先卸载旧版本后再安装。"),
        1619 or 1620 => new InstallerOutcome(InstallerOutcomeKind.FatalError, false, false, $"安装包无法打开（{exitCode}），可能已损坏或被占用。"),
        5 => new InstallerOutcome(InstallerOutcomeKind.BlockedBySecuritySoftware, false, false, "安装器返回访问拒绝（5），可能被安全软件或权限策略拦截。"),
        -1 => new InstallerOutcome(InstallerOutcomeKind.Unknown, false, false, "未能取得安装器退出码（超时、被中止或进程启动失败）。"),
        _ => new InstallerOutcome(InstallerOutcomeKind.Unknown, false, false, $"未知退出码 {exitCode}，请人工核对安装是否实际生效。")
    };

    /// <summary>
    /// 计算包操作的风险级别：卸载与强制重装为高风险（需逐项二次确认），安装与升级为中风险（汇总确认）。
    /// </summary>
    /// <param name="kind">操作类型。</param>
    /// <param name="forceReinstall">是否强制重装。</param>
    /// <returns>风险级别。</returns>
    public static RiskLevel RiskFor(PackageOperationKind kind, bool forceReinstall)
        => kind == PackageOperationKind.Uninstall || forceReinstall ? RiskLevel.High : RiskLevel.Medium;

    /// <summary>
    /// 把“某个包的一次操作”转换为声明式动作定义，交由 M4 统一执行器执行
    /// （参数绑定、权限与前置条件判断、超时 / 取消 / 输出截断 / 结果归类与备份区落盘均复用 M4 实现）。
    /// </summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="context">操作上下文。</param>
    /// <returns>动作定义。</returns>
    public static ActionDescriptor BuildOperationAction(
        SoftwarePackageDescriptor descriptor,
        PackageActionContext context)
    {
        var script = BuildScript(context);
        var actionId = $"package.{context.Kind.ToString().ToLowerInvariant()}.{descriptor.PackageId}";
        var name = $"{DescribeOperationKind(context.Kind)} {descriptor.Name} {descriptor.Version}";
        var risk = RiskFor(context.Kind, context.ForceReinstall);

        return new ActionDescriptor
        {
            ActionId = actionId,
            Name = name,
            Risk = risk,
            Scene = DiagnosisScene.Software,
            Summary = BuildSummary(descriptor, context),
            RequiresRestart = false,
            CanRollback = context.Kind != PackageOperationKind.Uninstall,
            RollbackNote = context.Kind == PackageOperationKind.Uninstall
                ? "卸载后需重新安装同一安装包才能恢复；卸载不会删除用户数据目录。"
                : "如需回退，可在本页对同一软件执行卸载操作。",
            RequiresAdministrator = true,
            TimeoutSeconds = descriptor.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : descriptor.TimeoutSeconds,
            MayInterruptSession = false,
            Script = script
        };
    }

    /// <summary>
    /// 生成包操作的 PowerShell 5.1 脚本：以静默参数启动安装 / 卸载程序并等待，
    /// 原样返回安装器退出码（由调用方按 <see cref="ClassifyInstallerExitCode"/> 归因）。
    /// </summary>
    /// <param name="context">操作上下文。</param>
    /// <returns>脚本文本。</returns>
    private static string BuildScript(PackageActionContext context)
    {
        // 卸载且包未携带专用卸载程序时，使用目标机卸载登记项中的卸载命令。
        if (context.Kind == PackageOperationKind.Uninstall && string.IsNullOrWhiteSpace(context.ExecutablePath))
        {
            var command = ActionRules.ToPowerShellLiteral(context.UninstallCommand ?? string.Empty);
            return "$command = " + command + "\n"
                   + "$process = Start-Process -FilePath $env:ComSpec -ArgumentList ('/c ' + $command) -Wait -PassThru\n"
                   + "Write-Output ('卸载器退出码：' + $process.ExitCode)\n"
                   + "exit $process.ExitCode";
        }

        var executable = ActionRules.ToPowerShellLiteral(context.ExecutablePath ?? string.Empty);
        var arguments = string.IsNullOrWhiteSpace(context.Arguments)
            ? string.Empty
            : " -ArgumentList " + ActionRules.ToPowerShellLiteral(context.Arguments!);

        return "$process = Start-Process -FilePath " + executable + arguments + " -Wait -PassThru\n"
               + "Write-Output ('安装器退出码：' + $process.ExitCode)\n"
               + "exit $process.ExitCode";
    }

    /// <summary>生成动作影响说明：写明操作对象、版本、静默参数与强制重装情况。</summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="context">操作上下文。</param>
    /// <returns>影响说明。</returns>
    private static string BuildSummary(SoftwarePackageDescriptor descriptor, PackageActionContext context)
    {
        var arguments = string.IsNullOrWhiteSpace(context.Arguments) ? "（未提供静默参数，可能出现交互窗口）" : context.Arguments!;
        var kind = DescribeOperationKind(context.Kind);
        var force = context.ForceReinstall ? "（强制重装：覆盖已装同版本）" : string.Empty;

        return $"{kind} {descriptor.Name} {descriptor.Version}{force}；静默参数：{arguments}；"
               + "安装器在目标机本机执行，不访问外网；执行结果与输出写入 U 盘备份区。";
    }

    /// <summary>
    /// 判断两个发布者是否视为同一来源：任一方未声明时不做限制，双方均声明时要求包含关系（忽略大小写）。
    /// </summary>
    /// <param name="declared">包描述中的发布者。</param>
    /// <param name="installed">已装清单中的发布者。</param>
    /// <returns>视为同一来源返回 true。</returns>
    private static bool PublisherMatches(string? declared, string? installed)
    {
        if (string.IsNullOrWhiteSpace(declared) || string.IsNullOrWhiteSpace(installed))
        {
            return false;
        }

        var left = declared.Trim();
        var right = installed.Trim();
        return left.Contains(right, StringComparison.OrdinalIgnoreCase)
               || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把字节数格式化为便于阅读的文本（与采集页保持一致的展示口径）。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>如“476.9 GB”“512 MB”。</returns>
    public static string FormatBytes(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (bytes >= gb)
        {
            return (bytes / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        if (bytes >= mb)
        {
            return (bytes / mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes >= kb)
        {
            return (bytes / kb).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString("0", CultureInfo.InvariantCulture) + " B";
    }
}