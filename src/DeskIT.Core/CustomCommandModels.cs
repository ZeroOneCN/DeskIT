namespace DeskIT.Core;

/// <summary>
/// 受控自定义命令入口的可用性判定结果（M9）。
/// 仅当“管理员模式 + 高级模式已开启 + 介质已解锁且可写 + 启动自检通过”四项同时满足时可用。
/// </summary>
/// <param name="IsAvailable">是否可用。</param>
/// <param name="Reason">不可用原因（面向 IT 人员的中文说明）；可用时为 null。</param>
public sealed record CustomCommandAvailability(bool IsAvailable, string? Reason);

/// <summary>
/// 持久化变更类型（M9）：执行自定义命令前后对比的三类“目标机持久化项”。
/// 仅用于对比差异，不修改这些项；命令本身的影响由命令自行决定。
/// </summary>
public enum PersistenceChangeKind
{
    /// <summary>系统服务（HKLM\SYSTEM\CurrentControlSet\Services 下的子键名）。</summary>
    Service = 0,

    /// <summary>计划任务（任务计划程序中的任务路径 + 名称）。</summary>
    ScheduledTask = 1,

    /// <summary>开机自启动项（Win32_StartupCommand 的项名）。</summary>
    StartupItem = 2
}

/// <summary>
/// 某一类持久化项的一次采集快照。采集失败（权限拒绝、命名空间缺失等）时 <see cref="IsAvailable"/> 为 false，
/// 该情形在差异对比中一律标注“未知”，绝不假装“无变化”。
/// </summary>
/// <param name="Kind">持久化项类型。</param>
/// <param name="Names">该项名称集合（采集失败时为空）。</param>
/// <param name="IsAvailable">本次采集是否成功。</param>
/// <param name="Message">采集失败原因；成功时为空串。</param>
public sealed record PersistenceSnapshot(
    PersistenceChangeKind Kind,
    IReadOnlyList<string> Names,
    bool IsAvailable,
    string Message);

/// <summary>
/// 某一类持久化项的变更明细（新增 / 移除）。采集任一侧失败时 <see cref="IsUnknown"/> 为 true，
/// 此时新增与移除均为空，<see cref="Note"/> 说明“未知”原因。
/// </summary>
/// <param name="Kind">持久化项类型。</param>
/// <param name="Added">新增名称（按名称排序）。</param>
/// <param name="Removed">移除名称（按名称排序）。</param>
/// <param name="IsUnknown">是否因采集失败而无法判定。</param>
/// <param name="Note">补充说明（未知原因等）；无补充时为空串。</param>
public sealed record PersistenceChange(
    PersistenceChangeKind Kind,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    bool IsUnknown,
    string Note)
{
    /// <summary>是否存在实际变化（未知时不视为变化）。</summary>
    public bool HasChanges => !IsUnknown && (Added.Count > 0 || Removed.Count > 0);
}

/// <summary>
/// 一条自定义命令的执行记录（M9）。命令原文与输出摘要均已先经凭据脱敏再写入；
/// 记录写入命令历史（加密追加）并生成审计事件，二者缺一不可。
/// </summary>
public sealed record CustomCommandRecord
{
    /// <summary>命令标识（形如 cmd-20261012-193000-ab12）。</summary>
    public required string CommandId { get; init; }

    /// <summary>执行时间。</summary>
    public required DateTimeOffset ExecutedAt { get; init; }

    /// <summary>命令原文（已脱敏：凭据类参数值替换为掩码）。</summary>
    public required string CommandText { get; init; }

    /// <summary>执行身份（管理员 / 标准用户）。</summary>
    public required string ExecutedAs { get; init; }

    /// <summary>结果归类（复用 M4 的结果四态与“需重启”语义）。</summary>
    public required ActionOutcome Outcome { get; init; }

    /// <summary>退出码；超时或取消时为 -1。</summary>
    public required int ExitCode { get; init; }

    /// <summary>是否超时被终止。</summary>
    public bool TimedOut { get; init; }

    /// <summary>执行耗时（秒）。</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>标准输出摘要（已脱敏、已截断）。</summary>
    public string OutputSummary { get; init; } = string.Empty;

    /// <summary>标准错误摘要（已脱敏、已截断）。</summary>
    public string ErrorSummary { get; init; } = string.Empty;

    /// <summary>执行前后对比得到的持久化变更（服务 / 计划任务 / 自启动项）。</summary>
    public IReadOnlyList<PersistenceChange> PersistenceChanges { get; init; } = Array.Empty<PersistenceChange>();

    /// <summary>所属会话标识；无会话时为空串。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>处理人工号（保留原值）。</summary>
    public string OperatorEmployeeId { get; init; } = string.Empty;

    /// <summary>处理人姓名（保留原值）。</summary>
    public string OperatorName { get; init; } = string.Empty;

    /// <summary>目标机计算机名。</summary>
    public string MachineName { get; init; } = string.Empty;

    /// <summary>
    /// 把本记录转换为台账中的“自定义命令清单”条目（供 M7 处理记录使用）。
    /// </summary>
    /// <returns>处理记录中的自定义命令条目。</returns>
    public LedgerCustomCommandEntry ToLedgerEntry()
        => new(CommandText, ExecutedAs, ExitCode, DurationSeconds, OutputSummary);
}

/// <summary>
/// 一次受控自定义命令执行的完整结果（含“是否成功记录”状态）。
/// 记录失败（命令历史或审计写入失败）时 <see cref="RecordingSucceeded"/> 为 false，
/// 调用方必须据此阻止后续命令执行（M9 不提供“不记录执行”的路径）。
/// </summary>
/// <param name="Record">命令执行记录（命令文本与输出已脱敏）。</param>
/// <param name="RecordingSucceeded">强制记录（命令历史 + 审计）是否全部成功。</param>
/// <param name="RecordingError">记录失败原因；成功时为 null。</param>
public sealed record CustomCommandResult(
    CustomCommandRecord Record,
    bool RecordingSucceeded,
    string? RecordingError);

/// <summary>
/// “把一条自定义命令另存为正式修复动作”的请求（M9 动作沉淀）。
/// 风险级别不得低于“高”；是否需重启、是否可回滚必须明确（用可空布尔表达“未选择”）。
/// </summary>
public sealed record ActionSedimentRequest
{
    /// <summary>源命令原文（未包装；沉淀时会被包装为带退出码约定的脚本）。</summary>
    public required string SourceCommand { get; init; }

    /// <summary>动作名称（面向 IT 人员的可读名称）。</summary>
    public required string Name { get; init; }

    /// <summary>动作说明（影响与注意事项，必填）。</summary>
    public required string Description { get; init; }

    /// <summary>风险级别（必须 ≥ 高）。</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>是否需重启生效；null 表示未选择（校验不通过）。</summary>
    public bool? RequiresRestart { get; init; }

    /// <summary>是否可回滚；null 表示未选择（校验不通过）。</summary>
    public bool? CanRollback { get; init; }

    /// <summary>回滚说明；“可回滚”时必填。</summary>
    public string RollbackNote { get; init; } = string.Empty;

    /// <summary>所属问题场景（用于动作库按场景分组）。</summary>
    public DiagnosisScene Scene { get; init; } = DiagnosisScene.StartupSystem;

    /// <summary>单项执行超时（秒）。</summary>
    public int TimeoutSeconds { get; init; } = ActionRules.DefaultTimeoutSeconds;

    /// <summary>参数声明（可选；用于把命令中的可变部分做成占位符）。</summary>
    public IReadOnlyList<ActionParameter> Parameters { get; init; } = Array.Empty<ActionParameter>();
}

/// <summary>
/// 动作沉淀请求的校验结果。
/// </summary>
/// <param name="IsValid">是否通过校验。</param>
/// <param name="Problems">未通过项的问题说明（字段级）。</param>
public sealed record ActionSedimentValidation(bool IsValid, IReadOnlyList<string> Problems);