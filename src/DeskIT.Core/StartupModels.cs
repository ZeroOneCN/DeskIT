namespace DeskIT.Core;

/// <summary>当前进程的权限级别（M1 权限判定结果）。</summary>
public enum PrivilegeLevel
{
    /// <summary>标准用户：仅可执行只读采集与诊断，修复动作库全部禁用。</summary>
    Standard = 0,

    /// <summary>管理员：额外可执行修复动作、软件安装/卸载，以及开启高级模式后的受控自定义命令。</summary>
    Administrator = 1
}

/// <summary>单个启动自检项的结果状态。</summary>
public enum SelfCheckOutcome
{
    /// <summary>通过：该项满足要求。</summary>
    Pass = 0,

    /// <summary>警示：不阻断启动，但需提示（如空间偏低、未生成清单）。</summary>
    Warn = 1,

    /// <summary>失败：阻断启动（如程序文件哈希不匹配）。</summary>
    Fail = 2
}

/// <summary>
/// 一条启动自检结果。
/// </summary>
/// <param name="Name">自检项名称（面向 IT 人员的可读名称）。</param>
/// <param name="Outcome">结果状态。</param>
/// <param name="Detail">说明文本，用于如实呈现判定依据。</param>
public sealed record SelfCheckItem(string Name, SelfCheckOutcome Outcome, string Detail);

/// <summary>介质可用空间评估（对应 PRD 9.3：200 MB 下限、500 MB 归档提示）。</summary>
public enum SpaceAssessment
{
    /// <summary>充足：不低于 500 MB，可正常开始新会话。</summary>
    Normal = 0,

    /// <summary>偏低：介于 200 MB 与 500 MB，额外提示归档，但仍可开始新会话。</summary>
    ArchiveHint = 1,

    /// <summary>不足：低于 200 MB，不再开始新会话。</summary>
    Insufficient = 2
}

/// <summary>目标系统版本受支持性判定结果。</summary>
public enum OsSupport
{
    /// <summary>受支持：Windows 10 1809+ / Windows 11 的 x64。</summary>
    Supported = 0,

    /// <summary>不受支持：进入受限启动，允许只读采集与导出，动作库禁用。</summary>
    Unsupported = 1
}

/// <summary>
/// 本次会话的能力集合。由权限级别、系统受支持性与介质空间共同推导，作为“本次可做什么”的唯一依据。
/// </summary>
/// <param name="CanCollect">是否可采集设备信息（只读，任何模式均可）。</param>
/// <param name="CanDiagnose">是否可执行场景化诊断（只读，任何模式均可）。</param>
/// <param name="CanFix">是否可执行修复动作（需管理员模式且系统受支持）。</param>
/// <param name="CanInstall">是否可执行软件包安装/卸载（同 CanFix）。</param>
/// <param name="CanCustomCommand">是否可使用受控自定义命令（需管理员模式 + 高级模式已开启 + 系统受支持）。</param>
/// <param name="CanStartNewSession">是否允许开始新的诊断会话（介质空间不低于 200 MB）。</param>
/// <param name="Restrictions">受限原因清单，用于界面如实呈现。</param>
public sealed record CapabilitySet(
    bool CanCollect,
    bool CanDiagnose,
    bool CanFix,
    bool CanInstall,
    bool CanCustomCommand,
    bool CanStartNewSession,
    IReadOnlyList<string> Restrictions)
{
    /// <summary>是否处于只读诊断模式（不能执行任何变更类动作）。</summary>
    public bool IsReadOnlyMode => !CanFix && !CanInstall;
}

/// <summary>
/// 启动阶段的最终判定结果：是否允许进入程序，以及本次会话的权限级别与能力集合。
/// </summary>
/// <param name="CanStart">是否允许进入程序（程序完整性校验失败时为 false）。</param>
/// <param name="Privilege">本次会话的权限级别。</param>
/// <param name="OsSupport">目标系统受支持性。</param>
/// <param name="Space">介质可用空间评估。</param>
/// <param name="Capabilities">本次会话能力集合。</param>
/// <param name="BlockReason">阻止启动的原因（仅有 CanStart=false 时非空）。</param>
public sealed record StartupVerdict(
    bool CanStart,
    PrivilegeLevel Privilege,
    OsSupport OsSupport,
    SpaceAssessment Space,
    CapabilitySet Capabilities,
    string? BlockReason)
{
    /// <summary>是否处于管理员模式。</summary>
    public bool IsAdministrator => Privilege == PrivilegeLevel.Administrator;
}