namespace DeskIT.Core;

/// <summary>
/// 修复动作的参数声明。执行前由 IT 人员填写；脚本中以 <c>{{参数名}}</c> 引用，
/// 展开为一个经过转义的 PowerShell 双引号字符串字面量（详见 <see cref="ActionRules.BindParameters"/>）。
/// </summary>
/// <param name="Name">参数名（在脚本中以 <c>{{名称}}</c> 引用）。</param>
/// <param name="Description">参数说明（面向 IT 人员）。</param>
/// <param name="DefaultValue">默认值；为空时表示必须由 IT 人员填写。</param>
public sealed record ActionParameter(string Name, string Description, string? DefaultValue = null)
{
    /// <summary>是否必须填写（无默认值即必须填写）。</summary>
    public bool IsRequired => string.IsNullOrWhiteSpace(DefaultValue);
}

/// <summary>动作前置条件的类型。不满足的前置条件会使该项标记“已跳过”，但不阻断其他动作。</summary>
public enum ActionPreconditionKind
{
    /// <summary>指定路径（文件或目录）存在；Value 为路径，支持环境变量。</summary>
    PathExists = 0,

    /// <summary>指定 Windows 服务存在；Value 为服务名。</summary>
    ServiceExists = 1
}

/// <summary>动作前置条件：执行前检查，不满足即“已跳过”并给出原因。</summary>
/// <param name="Kind">条件类型。</param>
/// <param name="Value">条件取值（路径或服务名）；按类型解释。</param>
/// <param name="Description">不满足时向 IT 人员展示的说明。</param>
public sealed record ActionPrecondition(ActionPreconditionKind Kind, string? Value, string Description);

/// <summary>动作按其风险级别对应的确认要求（PRD M4 风险分级表）。</summary>
public enum ActionConfirmationKind
{
    /// <summary>无需逐项确认，仅在“本次将执行的变更”汇总区明示。</summary>
    None = 0,

    /// <summary>汇总区确认，并明确提示会话可能中断（中风险）。</summary>
    BatchSummary = 1,

    /// <summary>逐项二次确认，写明影响与不可逆性（高风险）。</summary>
    PerAction = 2,

    /// <summary>逐项二次确认 + 输入确认词（不可逆）。</summary>
    ConfirmWord = 3
}

/// <summary>
/// 修复动作的声明式定义（对应 U 盘 <c>actions/</c> 下的 JSON 文件）。
/// 风险级别、是否需重启、是否可回滚均为其固有属性，由程序内置，用户不可下调。
/// </summary>
public sealed class ActionDescriptor
{
    /// <summary>动作稳定标识（形如 network.reset-dns），诊断建议与记录均按此关联。</summary>
    public required string ActionId { get; init; }

    /// <summary>动作名称（面向 IT 人员的可读名称）。</summary>
    public required string Name { get; init; }

    /// <summary>风险级别（固有属性）。</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>所属问题场景（用于按场景分组的动作列表）。</summary>
    public DiagnosisScene Scene { get; init; } = DiagnosisScene.Slowness;

    /// <summary>影响说明：这项动作会做什么、影响范围如何。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>执行后是否需要重启目标机才能生效。</summary>
    public bool RequiresRestart { get; init; }

    /// <summary>是否可回滚（可回滚动作会在执行前保存原值到备份区）。</summary>
    public bool CanRollback { get; init; }

    /// <summary>回滚说明：如何按备份信息恢复；不可回滚时说明原因。</summary>
    public string? RollbackNote { get; init; }

    /// <summary>是否需要管理员权限；只读诊断模式下需管理员的动作全部禁用。</summary>
    public bool RequiresAdministrator { get; init; } = true;

    /// <summary>单项执行超时（秒）；默认 600 秒（PRD：单项动作默认超时 10 分钟）。</summary>
    public int TimeoutSeconds { get; init; } = ActionRules.DefaultTimeoutSeconds;

    /// <summary>执行过程中是否可能中断当前会话（如重置网络栈会断开网络）。</summary>
    public bool MayInterruptSession { get; init; }

    /// <summary>PowerShell 5.1 脚本入口（内联文本；以 <c>{{参数名}}</c> 引用参数）。</summary>
    public string? Script { get; init; }

    /// <summary>前置条件清单。</summary>
    public IReadOnlyList<ActionPrecondition> Preconditions { get; init; } = Array.Empty<ActionPrecondition>();

    /// <summary>参数清单。</summary>
    public IReadOnlyList<ActionParameter> Parameters { get; init; } = Array.Empty<ActionParameter>();

    /// <summary>
    /// 动作来源（可选）。为空表示内置动作库；M9 动作沉淀写入的动作标记为“自定义沉淀”。
    /// 既有 <c>actions/*.json</c> 不包含该字段时按内置动作处理，不影响解析。
    /// </summary>
    public string? Source { get; init; }

    /// <summary>高风险及以上（含不可逆）：需逐项二次确认。</summary>
    public bool IsHighRisk => Risk >= RiskLevel.High;

    /// <summary>不可逆：确认时须输入确认词。</summary>
    public bool IsIrreversible => Risk == RiskLevel.Irreversible;
}

/// <summary>一次动作执行时填写的一个参数取值。</summary>
/// <param name="Name">参数名。</param>
/// <param name="Value">取值。</param>
public sealed record ActionParameterValue(string Name, string Value);

/// <summary>一次动作执行的结果（不落盘部分，供界面展示）。</summary>
/// <param name="ActionId">动作标识。</param>
/// <param name="ActionName">动作名称。</param>
/// <param name="Risk">风险级别。</param>
/// <param name="Outcome">结果归类。</param>
/// <param name="ExitCode">脚本退出码；超时或取消时为 -1。</param>
/// <param name="StandardOutput">标准输出（已按 200 行上限截断）。</param>
/// <param name="StandardError">标准错误。</param>
/// <param name="Duration">执行耗时。</param>
/// <param name="TimedOut">是否超时被终止。</param>
/// <param name="Reason">跳过 / 失败等原因说明；正常执行时为 null。</param>
/// <param name="Parameters">本次使用的参数取值。</param>
public sealed record ActionExecutionResult(
    string ActionId,
    string ActionName,
    RiskLevel Risk,
    ActionOutcome Outcome,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut,
    string? Reason,
    IReadOnlyList<ActionParameterValue> Parameters);

/// <summary>
/// 动作执行后写入 U 盘备份区的记录：审计信息 + 输出摘要 + 回滚信息位置。
/// 该目录后续由 M8 统一加密；本阶段先以可读文件形式落盘，便于现场审计与回退。
/// </summary>
/// <param name="RunId">本次执行标识（形如 20261012-193000-network.reset-dns）。</param>
/// <param name="ActionId">动作标识。</param>
/// <param name="ActionName">动作名称。</param>
/// <param name="Risk">风险级别。</param>
/// <param name="Outcome">结果归类。</param>
/// <param name="ExitCode">脚本退出码。</param>
/// <param name="TimedOut">是否超时。</param>
/// <param name="Duration">执行耗时。</param>
/// <param name="ExecutedAt">执行时间。</param>
/// <param name="MachineName">目标机计算机名。</param>
/// <param name="SessionId">所属会话标识；未关联会话时为空串。</param>
/// <param name="Parameters">本次使用的参数取值。</param>
/// <param name="CanRollback">是否可回滚。</param>
/// <param name="RollbackNote">回滚说明。</param>
/// <param name="RollbackDataFile">回滚原始数据的文件路径；脚本未产出时为空。</param>
/// <param name="OutputSummary">标准输出摘要（截断后）。</param>
/// <param name="ErrorSummary">标准错误摘要。</param>
public sealed record ActionBackupRecord(
    string RunId,
    string ActionId,
    string ActionName,
    RiskLevel Risk,
    ActionOutcome Outcome,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    DateTimeOffset ExecutedAt,
    string MachineName,
    string SessionId,
    IReadOnlyList<ActionParameterValue> Parameters,
    bool CanRollback,
    string? RollbackNote,
    string? RollbackDataFile,
    string OutputSummary,
    string ErrorSummary);