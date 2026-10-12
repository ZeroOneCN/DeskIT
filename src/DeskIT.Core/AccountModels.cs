namespace DeskIT.Core;

/// <summary>目标机上一个本地账号的只读信息（M6 查询）。</summary>
/// <param name="Name">账号名。</param>
/// <param name="FullName">全名（描述）；无则为 null。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="LockedOut">是否处于锁定状态（密码错误次数超限）。</param>
/// <param name="PasswordExpires">密码是否设置了有效期；false 表示“密码永不过期”。</param>
/// <param name="PasswordLastSet">最近一次设置密码的时间文本；未知为 null。</param>
/// <param name="LastLogon">最近一次登录时间文本；从未登录或不可读为 null。</param>
/// <param name="Source">账号来源（本地 / Microsoft 账户 / Azure AD）；未知为 null。</param>
public sealed record LocalAccountInfo(
    string Name,
    string? FullName,
    bool Enabled,
    bool LockedOut,
    bool PasswordExpires,
    string? PasswordLastSet,
    string? LastLogon,
    string? Source);

/// <summary>一条“名称 → 取值”的只读事实项（UAC 设置、域信息、时间源、策略应用结果等）。</summary>
/// <param name="Name">事实项名称。</param>
/// <param name="Value">取值（已可读化）。</param>
public sealed record AccountFact(string Name, string Value);

/// <summary>账号与权限的只读查询结果（M6）。查询本身不产生任何系统变更。</summary>
public sealed class AccountSnapshot
{
    /// <summary>查询时间。</summary>
    public DateTimeOffset QueriedAt { get; init; }

    /// <summary>本次查询是否处于只读模式（标准用户）：锁定状态等部分信息可能读取不到。</summary>
    public bool ReadOnlyMode { get; init; }

    /// <summary>本地账号列表。</summary>
    public IReadOnlyList<LocalAccountInfo> Accounts { get; init; } = Array.Empty<LocalAccountInfo>();

    /// <summary>本地管理员组成员。</summary>
    public IReadOnlyList<string> Administrators { get; init; } = Array.Empty<string>();

    /// <summary>UAC、登录 / 密码策略、域信息、时间源等事实项。</summary>
    public IReadOnlyList<AccountFact> Facts { get; init; } = Array.Empty<AccountFact>();

    /// <summary>共享凭据残留（凭据管理器中缓存的网络凭据目标）。</summary>
    public IReadOnlyList<string> SharedCredentials { get; init; } = Array.Empty<string>();

    /// <summary>查询过程中的问题说明（无权限、不适用、超时等）；无问题为空。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

/// <summary>本地账号统计摘要（用于界面顶部的概览与异常提示）。</summary>
/// <param name="Total">账号总数。</param>
/// <param name="Enabled">已启用数量。</param>
/// <param name="Disabled">已停用数量。</param>
/// <param name="Locked">处于锁定状态的数量。</param>
/// <param name="NeverLoggedOn">从未登录的数量。</param>
public sealed record AccountSummary(int Total, int Enabled, int Disabled, int Locked, int NeverLoggedOn)
{
    /// <summary>是否存在值得关注的账号（被锁定或从未登录）。</summary>
    public bool HasAttention => Locked > 0 || NeverLoggedOn > 0;
}

/// <summary>由 <c>net accounts</c> 输出解析出的登录 / 密码策略摘要。</summary>
/// <param name="MinPasswordLength">密码长度最小值；未知为 null。</param>
/// <param name="MaxPasswordAgeDays">密码最长使用期限（天）；null 表示“从不”或未知。</param>
/// <param name="MinPasswordAgeDays">密码最短使用期限（天）；未知为 null。</param>
/// <param name="LockoutThreshold">锁定阈值（次）；null 表示“从不”或未知。</param>
/// <param name="LockoutDurationMinutes">锁定持续时间（分钟）；未知为 null。</param>
/// <param name="LockoutObservationWindowMinutes">锁定观察窗口（分钟）；未知为 null。</param>
public sealed record PasswordPolicy(
    int? MinPasswordLength,
    int? MaxPasswordAgeDays,
    int? MinPasswordAgeDays,
    int? LockoutThreshold,
    int? LockoutDurationMinutes,
    int? LockoutObservationWindowMinutes);

/// <summary>UAC 提示行为的可读化结果。</summary>
/// <param name="Level">提示级别说明。</param>
/// <param name="Risk">风险提示；正常时为 null。</param>
public sealed record UacDescription(string Level, string? Risk);