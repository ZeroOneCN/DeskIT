namespace DeskIT.Core;

/// <summary>软件包适用的系统架构。</summary>
public enum PackageArchitecture
{
    /// <summary>不限制架构（x86 / x64 均可安装）。</summary>
    Any = 0,

    /// <summary>仅 64 位系统。</summary>
    X64 = 1,

    /// <summary>仅 32 位系统（64 位系统亦可运行）。</summary>
    X86 = 2
}

/// <summary>软件包（安装程序）的完整性状态。</summary>
public enum PackageIntegrityStatus
{
    /// <summary>校验通过：文件存在且 SHA-256 与描述文件一致。</summary>
    Verified = 0,

    /// <summary>哈希不匹配：文件被替换或描述文件过期，禁止执行。</summary>
    HashMismatch = 1,

    /// <summary>文件缺失：描述文件存在但安装程序不在 U 盘，禁止执行。</summary>
    FileMissing = 2,

    /// <summary>未声明校验哈希：来源不可验证，默认禁止执行。</summary>
    HashNotDeclared = 3
}

/// <summary>软件包与目标机已装软件的匹配状态。</summary>
public enum PackageMatchStatus
{
    /// <summary>未安装：目标机已装清单中没有该软件。</summary>
    NotInstalled = 0,

    /// <summary>已安装且版本一致。</summary>
    InstalledSameVersion = 1,

    /// <summary>已安装但版本不一致（可执行升级）。</summary>
    InstalledDifferentVersion = 2,

    /// <summary>已安装，但版本无法比较。</summary>
    InstalledUnknownVersion = 3
}

/// <summary>对一个软件包可执行的操作类型。</summary>
public enum PackageOperationKind
{
    /// <summary>安装。</summary>
    Install = 0,

    /// <summary>升级（已装旧版本时以新包覆盖安装）。</summary>
    Upgrade = 1,

    /// <summary>卸载。</summary>
    Uninstall = 2
}

/// <summary>安装 / 卸载程序退出码的归因类别（PRD M5 异常处理）。</summary>
public enum InstallerOutcomeKind
{
    /// <summary>成功。</summary>
    Success = 0,

    /// <summary>成功但需重启才生效。</summary>
    SuccessPendingRestart = 1,

    /// <summary>另一个安装 / 更新正在进行。</summary>
    AlreadyInProgress = 2,

    /// <summary>被系统或安全策略阻止。</summary>
    BlockedByPolicy = 3,

    /// <summary>被安全软件拦截（进程被杀或返回访问拒绝类错误）。</summary>
    BlockedBySecuritySoftware = 4,

    /// <summary>致命错误（权限不足、安装包不完整等）。</summary>
    FatalError = 5,

    /// <summary>版本冲突：已安装另一个版本。</summary>
    VersionConflict = 6,

    /// <summary>未知退出码。</summary>
    Unknown = 7
}

/// <summary>安装 / 卸载退出码的归因结果。</summary>
/// <param name="Kind">归因类别。</param>
/// <param name="Success">是否视为成功（含“成功但需重启”）。</param>
/// <param name="RequiresRestart">是否需要重启后生效。</param>
/// <param name="Description">面向 IT 人员的原因说明。</param>
public sealed record InstallerOutcome(
    InstallerOutcomeKind Kind,
    bool Success,
    bool RequiresRestart,
    string Description);

/// <summary>
/// 软件包描述文件（U 盘 <c>packages/&lt;包标识&gt;.json</c>）。
/// 由 IT 管理员随安装包一同放入，声明名称、版本、静默参数与校验哈希；
/// 程序启动时按 <see cref="Sha256"/> 校验安装程序，不匹配的包标记为“不可用”并禁止执行。
/// </summary>
public sealed class SoftwarePackageDescriptor
{
    /// <summary>包稳定标识（形如 7zip-23.01），用于生成动作标识与记录关联。</summary>
    public required string PackageId { get; init; }

    /// <summary>软件名称（面向 IT 人员展示）。</summary>
    public required string Name { get; init; }

    /// <summary>该包携带的软件版本。</summary>
    public required string Version { get; init; }

    /// <summary>发布者（用于与已装清单匹配，避免同名不同厂商误判）。</summary>
    public string Publisher { get; init; } = string.Empty;

    /// <summary>适用架构。</summary>
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Any;

    /// <summary>适用系统范围说明（仅作展示与人工核对，不参与自动判定）。</summary>
    public string? SupportedOs { get; init; }

    /// <summary>安装程序文件名（相对 <c>packages/</c> 目录）。</summary>
    public required string InstallerFileName { get; init; }

    /// <summary>静默安装参数（预置，执行时不弹出软件自身的交互界面）。</summary>
    public string? InstallerArguments { get; init; }

    /// <summary>专用卸载程序文件名（相对 <c>packages/</c> 目录）；为空时改用目标机的卸载登记项。</summary>
    public string? UninstallerFileName { get; init; }

    /// <summary>静默卸载参数。</summary>
    public string? UninstallerArguments { get; init; }

    /// <summary>
    /// 安装程序的 SHA-256（十六进制，大小写与分隔符不敏感）。
    /// 未声明即视为“来源不可验证”，该包默认禁止执行（PRD M5 处理规则）。
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>在目标机已装清单中用于匹配的显示名；为空时使用 <see cref="Name"/>。</summary>
    public string? UninstallEntryName { get; init; }

    /// <summary>安装前要求的最小可用磁盘空间（MB）。</summary>
    public int MinFreeDiskMb { get; init; }

    /// <summary>备注（面向 IT 人员，如适用范围、来源说明）。</summary>
    public string? Notes { get; init; }

    /// <summary>单项操作超时（秒）；默认 1800 秒（安装类动作可能长时间无输出）。</summary>
    public int TimeoutSeconds { get; init; } = PackageRules.DefaultTimeoutSeconds;

    /// <summary>在已装清单中匹配时使用的显示名。</summary>
    public string MatchName => string.IsNullOrWhiteSpace(UninstallEntryName) ? Name : UninstallEntryName!;
}

/// <summary>目标机上一条已安装软件（注册表 Uninstall 键读取，只读）。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Publisher">发布者。</param>
/// <param name="Version">版本。</param>
/// <param name="UninstallString">卸载命令（优先使用软件自身的卸载入口）。</param>
/// <param name="InstallLocation">安装位置；未知为 null（卸载后据此提示残留目录）。</param>
public sealed record InstalledSoftwareEntry(
    string Name,
    string? Publisher,
    string? Version,
    string? UninstallString,
    string? InstallLocation = null);

/// <summary>一个软件包的文件校验结果。</summary>
/// <param name="Status">完整性状态。</param>
/// <param name="InstallerPath">安装程序完整路径；文件缺失时为约定的期望路径。</param>
/// <param name="ActualSha256">实际计算出的 SHA-256；文件不存在时为 null。</param>
public sealed record PackageFileCheck(
    PackageIntegrityStatus Status,
    string InstallerPath,
    string? ActualSha256);

/// <summary>执行前的检查结果（PRD M5 处理规则：不满足时给出明确原因并允许 IT 人员决定是否继续）。</summary>
/// <param name="CanProceed">是否允许执行（存在阻断项时为 false）。</param>
/// <param name="Match">与目标机已装软件的匹配状态。</param>
/// <param name="Blockers">阻断项：出现即不得执行。</param>
/// <param name="Warnings">提示项：需 IT 人员确认后才继续。</param>
public sealed record PackagePreflight(
    bool CanProceed,
    PackageMatchStatus Match,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings);

/// <summary>构建包操作动作所需的上下文。</summary>
/// <param name="Kind">操作类型。</param>
/// <param name="ExecutablePath">安装 / 卸载程序完整路径；仅卸载且无专用卸载程序时为 null。</param>
/// <param name="Arguments">静默参数。</param>
/// <param name="UninstallCommand">目标机卸载登记项中的卸载命令（卸载且无专用卸载程序时使用）。</param>
/// <param name="ForceReinstall">已安装同版本时是否强制重装（需二次确认）。</param>
public sealed record PackageActionContext(
    PackageOperationKind Kind,
    string? ExecutablePath,
    string? Arguments,
    string? UninstallCommand,
    bool ForceReinstall);

/// <summary>一次包操作的结果。</summary>
/// <param name="PackageId">包标识。</param>
/// <param name="PackageName">软件名称。</param>
/// <param name="DeclaredVersion">包携带的版本。</param>
/// <param name="Kind">操作类型。</param>
/// <param name="Outcome">安装器归因结果。</param>
/// <param name="Execution">底层动作执行结果（复用 M4 执行器）。</param>
/// <param name="InstalledVersionAfter">操作后重新读取到的已装版本；未读取到为 null。</param>
/// <param name="ResidueNote">卸载后残留目录提示；无残留为 null。</param>
public sealed record PackageOperationResult(
    string PackageId,
    string PackageName,
    string DeclaredVersion,
    PackageOperationKind Kind,
    InstallerOutcome Outcome,
    ActionExecutionResult Execution,
    string? InstalledVersionAfter,
    string? ResidueNote);