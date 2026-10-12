namespace DeskIT.Core;

/// <summary>
/// 介质（U 盘）的当前状态（PRD M8“结果与状态变化”五态）。
/// </summary>
public enum MediaState
{
    /// <summary>未初始化：介质尚未设置主密码，需要先完成初始化。</summary>
    NotInitialized = 0,

    /// <summary>已锁定：介质已初始化，但本次尚未解锁，禁止查看台账、执行动作与导出。</summary>
    Locked = 1,

    /// <summary>已解锁：主密码校验通过，可正常读写加密数据。</summary>
    Unlocked = 2,

    /// <summary>只读或不可写：介质写保护或损坏，仅允许只读诊断与查看，禁止执行动作与结束会话。</summary>
    ReadOnlyOrUnwritable = 3,

    /// <summary>校验失败：台账数据完整性校验未通过，提示可能损坏，提供“从备份文件恢复”入口，不自动清空。</summary>
    IntegrityFailed = 4
}

/// <summary>
/// 介质头信息（随 <c>data\operators.json</c> 明文保存，不含任何敏感内容）。
/// 仅用于识别介质与派生、校验主密码；<see cref="VerifierBase64"/> 只是密码校验值，绝不保存主密码本身。
/// </summary>
/// <param name="MediaId">介质标识（由卷序列号等本机可得信息生成，不参与密钥派生、不绑定机器）。</param>
/// <param name="InitializedAt">初始化时间。</param>
/// <param name="KdfAlgorithm">密钥派生算法名（如 PBKDF2-HMAC-SHA256）。</param>
/// <param name="Iterations">密钥派生迭代次数。</param>
/// <param name="SaltBase64">随机盐的 Base64 字符串（16 字节）。</param>
/// <param name="VerifierBase64">密码校验值的 Base64 字符串（由派生密钥计算，仅用于校验，非密码本身）。</param>
public sealed record MediaDescriptor(
    string MediaId,
    DateTimeOffset InitializedAt,
    string KdfAlgorithm,
    int Iterations,
    string SaltBase64,
    string VerifierBase64);

/// <summary>
/// 介质级设置（随 <c>data\operators.json</c> 保存，跨机跨启动有效）。
/// </summary>
public sealed record MediaSettings
{
    /// <summary>无操作自动锁定的分钟数，默认 10 分钟。</summary>
    public int AutoLockMinutes { get; init; } = MediaRules.DefaultAutoLockMinutes;

    /// <summary>界面脱敏开关，默认开启（用于投屏或示教场景）。</summary>
    public bool UiDesensitized { get; init; } = true;
}

/// <summary>
/// 解锁结果。
/// </summary>
/// <param name="Success">是否解锁成功。</param>
/// <param name="State">解锁后的介质状态。</param>
/// <param name="Message">面向 IT 人员的中文提示。</param>
/// <param name="FailedAttempts">当前连续失败次数。</param>
/// <param name="Cooldown">本次失败后需强制等待的时长（成功或未达阈值时为 <see cref="TimeSpan.Zero"/>）。</param>
public sealed record MediaUnlockResult(
    bool Success,
    MediaState State,
    string Message,
    int FailedAttempts,
    TimeSpan Cooldown);

/// <summary>
/// 介质初始化结果。
/// </summary>
/// <param name="Success">是否初始化成功。</param>
/// <param name="Descriptor">初始化成功后生成的介质头信息；失败时为 null。</param>
/// <param name="Message">面向 IT 人员的中文提示。</param>
public sealed record MediaInitializationResult(bool Success, MediaDescriptor? Descriptor, string Message);

/// <summary>
/// 主密码校验结果。
/// </summary>
/// <param name="IsValid">是否符合规则。</param>
/// <param name="Message">未通过时的中文错误说明；通过时为空串。</param>
public sealed record MasterPasswordValidation(bool IsValid, string Message);

/// <summary>
/// 介质迁移检测结果。
/// </summary>
/// <param name="MediaChanged">介质标识与登记值不一致（即数据目录被复制到了新介质）时为 true。</param>
/// <param name="Message">面向 IT 人员的中文提示（含对旧介质数据安全处置的建议）。</param>
public sealed record MediaMigrationResult(bool MediaChanged, string Message);

/// <summary>
/// 审计事件类型（PRD M8/M9）。审计事件一律不记录任何凭证明文。
/// </summary>
public enum AuditEventKind
{
    /// <summary>介质初始化。</summary>
    MediaInitialized = 0,

    /// <summary>解锁成功。</summary>
    UnlockSucceeded = 1,

    /// <summary>解锁失败。</summary>
    UnlockFailed = 2,

    /// <summary>导出台账或报告。</summary>
    Export = 3,

    /// <summary>执行修复动作。</summary>
    ActionExecuted = 4,

    /// <summary>执行受控自定义命令（M9）。</summary>
    CustomCommandExecuted = 5,

    /// <summary>异常移除 / 会话中断。</summary>
    SessionInterruptedOrRemoved = 6,

    /// <summary>高级模式开关变化。</summary>
    AdvancedModeChanged = 7,

    /// <summary>处理人切换。</summary>
    OperatorSwitched = 8,

    /// <summary>报告产出。</summary>
    ReportProduced = 9,

    /// <summary>临时文件清理结果。</summary>
    TempCleanup = 10,

    /// <summary>介质迁移检测（数据目录被复制到新介质）。</summary>
    MediaChanged = 11
}

/// <summary>
/// 一条审计事件（PRD M8：解锁、失败、导出、动作执行、异常移除等均产生审计事件）。
/// 处理人工号与姓名保留原值；不含任何凭证明文。
/// </summary>
public sealed record AuditEvent
{
    /// <summary>事件标识（形如 aud-20261012-193000-ab12）。</summary>
    public required string EventId { get; init; }

    /// <summary>事件类型。</summary>
    public required AuditEventKind Kind { get; init; }

    /// <summary>事件发生时间。</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>计算机名（发生事件的目标机）。</summary>
    public required string ComputerName { get; init; }

    /// <summary>处理人工号。</summary>
    public string OperatorEmployeeId { get; init; } = string.Empty;

    /// <summary>处理人姓名。</summary>
    public string OperatorName { get; init; } = string.Empty;

    /// <summary>关联的会话标识；无关联时为空串。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>事件摘要（一句话说明发生了什么）。</summary>
    public required string Summary { get; init; }

    /// <summary>事件明细（可选的补充信息，不得包含凭证明文）。</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// 临时工作目录清理结果（PRD 7.4：退出时删除目标机临时目录中的本次运行工作目录，失败时提示路径）。
/// </summary>
/// <param name="Success">是否全部清理成功。</param>
/// <param name="LeftoverPaths">未能删除的路径清单。</param>
/// <param name="Reasons">与 <see cref="LeftoverPaths"/> 一一对应的失败原因。</param>
public sealed record TempCleanupResult(
    bool Success,
    IReadOnlyList<string> LeftoverPaths,
    IReadOnlyList<string> Reasons);