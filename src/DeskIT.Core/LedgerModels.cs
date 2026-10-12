using System.Text.Json.Serialization;

namespace DeskIT.Core;

/// <summary>
/// 处理记录的问题主类别（M7）。与 M3 的 <see cref="DiagnosisScene"/> 一一对应，并额外提供“其他”。
/// 一条记录只落一个主类别，可另附自定义标签。
/// </summary>
public enum ProblemCategory
{
    /// <summary>卡慢。</summary>
    Slowness = 0,

    /// <summary>网络。</summary>
    Network = 1,

    /// <summary>打印与共享。</summary>
    PrintingSharing = 2,

    /// <summary>磁盘与存储。</summary>
    DiskStorage = 3,

    /// <summary>启动与系统。</summary>
    StartupSystem = 4,

    /// <summary>软件。</summary>
    Software = 5,

    /// <summary>账号与权限。</summary>
    AccountPrivilege = 6,

    /// <summary>其他：不属于以上场景的问题。</summary>
    Other = 7
}

/// <summary>
/// 会话结论为“未解决”时必须填写的原因类别。
/// PRD 未枚举具体项，此处按现场常见情形设计一组合理类别，供记录与统计使用。
/// </summary>
public enum UnresolvedReason
{
    /// <summary>需更换部件或联系厂商。</summary>
    NeedPartOrVendor = 0,

    /// <summary>需网络或服务器侧配合。</summary>
    NeedNetworkOrServer = 1,

    /// <summary>需用户继续观察（本次已做处置，效果待观察）。</summary>
    NeedUserObservation = 2,

    /// <summary>权限或策略限制（受限于目标机策略或权限）。</summary>
    PermissionOrPolicy = 3,

    /// <summary>需先备份数据（为避免数据丢失暂不处置）。</summary>
    NeedBackupFirst = 4,

    /// <summary>无法复现（现场未复现问题，无法定位）。</summary>
    CannotReproduce = 5,

    /// <summary>其他原因（需在说明中补充）。</summary>
    Other = 6
}

/// <summary>
/// 处理人（工号 + 姓名）。两者均必填、不可只填其一；工号用于责任追溯与对齐，一律保留原值。
/// </summary>
/// <param name="EmployeeId">工号。</param>
/// <param name="Name">姓名。</param>
public sealed record OperatorInfo(string EmployeeId, string Name)
{
    /// <summary>面向界面的展示文本，如“张三（E1001）”。</summary>
    [JsonIgnore]
    public string DisplayText => $"{Name}（{EmployeeId}）";
}

/// <summary>
/// 处理记录中的一个执行动作条目（由 <see cref="ActionExecutionResult"/> 转换而来）。
/// 输出摘要已按脱敏规则处理（凭据类参数替换为掩码）。
/// </summary>
/// <param name="ActionId">动作标识。</param>
/// <param name="ActionName">动作名称。</param>
/// <param name="Risk">风险级别。</param>
/// <param name="Outcome">结果归类。</param>
/// <param name="DurationSeconds">执行耗时（秒）。</param>
/// <param name="OutputSummary">输出摘要（已脱敏）。</param>
public sealed record LedgerActionEntry(
    string ActionId,
    string ActionName,
    RiskLevel Risk,
    ActionOutcome Outcome,
    double DurationSeconds,
    string OutputSummary)
{
    /// <summary>
    /// 把一次动作执行结果转换为处理记录条目，输出摘要在写入前经凭据掩码处理。
    /// </summary>
    /// <param name="result">动作执行结果。</param>
    /// <returns>处理记录中的动作条目。</returns>
    public static LedgerActionEntry FromExecution(ActionExecutionResult result)
        => new(
            result.ActionId,
            result.ActionName,
            result.Risk,
            result.Outcome,
            result.Duration.TotalSeconds,
            LedgerRules.MaskCredentials(result.StandardOutput));
}

/// <summary>
/// 自定义命令条目（M9 使用）。本步仅定义模型并在记录中保留空清单，保证跨模块字段一致。
/// 命令原文与输出均须先经凭据脱敏再写入。
/// </summary>
/// <param name="CommandText">命令原文（已脱敏）。</param>
/// <param name="ExecutedAs">执行身份（如管理员 / 标准用户）。</param>
/// <param name="ExitCode">退出码。</param>
/// <param name="DurationSeconds">执行耗时（秒）。</param>
/// <param name="OutputSummary">输出摘要（已脱敏）。</param>
public sealed record LedgerCustomCommandEntry(
    string CommandText,
    string ExecutedAs,
    int ExitCode,
    double DurationSeconds,
    string OutputSummary);

/// <summary>
/// 处理结束后追加的补充说明。只追加、不改写历史记录，带时间戳与追加人。
/// </summary>
/// <param name="AddedAt">追加时间。</param>
/// <param name="Operator">追加人（工号 + 姓名）。</param>
/// <param name="Text">补充说明正文。</param>
public sealed record RecordSupplement(DateTimeOffset AddedAt, OperatorInfo Operator, string Text);

/// <summary>
/// 一条处理记录（对应 PRD M7“处理记录字段表”）。写入台账后即视为不可修改，后续只能追加补充说明。
/// </summary>
public sealed record HandlingRecord
{
    /// <summary>记录标识（形如 rec-20261012-193000-ab12）。</summary>
    public required string RecordId { get; init; }

    /// <summary>会话标识（关联 M3 诊断会话；无诊断时为采集/动作会话标识）。</summary>
    public required string SessionId { get; init; }

    /// <summary>机器标识（采集阶段自动获得，不可手改）。</summary>
    public required string MachineId { get; init; }

    /// <summary>机器标识来源（硬件序列号 / 回退）。</summary>
    public required MachineIdSource MachineIdSource { get; init; }

    /// <summary>计算机名。</summary>
    public required string HostName { get; init; }

    /// <summary>处理人（工号 + 姓名）。</summary>
    public required OperatorInfo Operator { get; init; }

    /// <summary>问题主类别。</summary>
    public required ProblemCategory Category { get; init; }

    /// <summary>自定义标签（如“打印机”“外设”）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>员工现象描述（≤ 500 字）。</summary>
    public string SymptomDescription { get; init; } = string.Empty;

    /// <summary>诊断结论集合（含结论与依据，自动写入）。</summary>
    public IReadOnlyList<DiagnosisSceneResult> Scenes { get; init; } = Array.Empty<DiagnosisSceneResult>();

    /// <summary>执行动作清单（含结果与耗时，自动写入）。</summary>
    public IReadOnlyList<LedgerActionEntry> Actions { get; init; } = Array.Empty<LedgerActionEntry>();

    /// <summary>自定义命令清单（M9 使用，本步为空清单）。</summary>
    public IReadOnlyList<LedgerCustomCommandEntry> CustomCommands { get; init; } = Array.Empty<LedgerCustomCommandEntry>();

    /// <summary>处理后系统持久化变更（M9：自定义命令执行前后对比服务 / 计划任务 / 自启动项得到的差异）。</summary>
    public IReadOnlyList<PersistenceChange> PersistenceChanges { get; init; } = Array.Empty<PersistenceChange>();

    /// <summary>结论（已解决 / 待重启生效 / 未解决 / 已中断）。</summary>
    public required SessionConclusion Conclusion { get; init; }

    /// <summary>未解决原因（结论为“未解决”时必填，其余可空）。</summary>
    public UnresolvedReason? UnresolvedReason { get; init; }

    /// <summary>未解决说明（结论为“未解决”时的补充说明；可为空）。</summary>
    public string UnresolvedNote { get; init; } = string.Empty;

    /// <summary>本次会话的权限级别（自动写入）。</summary>
    public required PrivilegeLevel Privilege { get; init; }

    /// <summary>处理开始时间。</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>处理结束时间。</summary>
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>自动产出的报告文件名（HTML，相对 reports 目录）。</summary>
    public required string ReportFileName { get; init; }

    /// <summary>中断原因（会话以“已中断”结束时记录；正常结束为 null）。</summary>
    public string? InterruptReason { get; init; }

    /// <summary>补充说明清单（只追加）。</summary>
    public IReadOnlyList<RecordSupplement> Supplements { get; init; } = Array.Empty<RecordSupplement>();

    /// <summary>处理耗时（由开始 / 结束时间推导，不单独存储以避免不一致）。</summary>
    [JsonIgnore]
    public TimeSpan Duration => FinishedAt - StartedAt;

    /// <summary>诊断结论统计（由诊断结论集合实时推导，保证与诊断结果一致）。</summary>
    [JsonIgnore]
    public DiagnosisSummary DiagnosisSummary => DiagnosisRules.BuildSummary(Scenes);

    /// <summary>是否以“已中断”结束。</summary>
    [JsonIgnore]
    public bool IsInterrupted => Conclusion == SessionConclusion.Interrupted;
}

/// <summary>
/// 目标机档案（M7 复盘视图）：同一机器标识的记录归并到一起，记录按处理时间倒序。
/// </summary>
/// <param name="MachineId">机器标识。</param>
/// <param name="MachineIdSource">机器标识来源。</param>
/// <param name="HostName">最近一次处理时的计算机名。</param>
/// <param name="Records">该机器的处理记录（按处理时间倒序）。</param>
public sealed record MachineLedger(
    string MachineId,
    MachineIdSource MachineIdSource,
    string HostName,
    IReadOnlyList<HandlingRecord> Records)
{
    /// <summary>首次处理时间。</summary>
    public DateTimeOffset FirstHandledAt => Records.Min(record => record.FinishedAt);

    /// <summary>最近一次处理时间。</summary>
    public DateTimeOffset LastHandledAt => Records.Max(record => record.FinishedAt);

    /// <summary>累计处理次数。</summary>
    public int ProcessingCount => Records.Count;

    /// <summary>最近一次处理的结论。</summary>
    public SessionConclusion LastConclusion => Records.OrderByDescending(record => record.FinishedAt).First().Conclusion;
}

/// <summary>按问题类别的处理计数。</summary>
/// <param name="Category">问题类别。</param>
/// <param name="Count">处理次数。</param>
public sealed record CategoryCount(ProblemCategory Category, int Count);

/// <summary>按处理人的处理计数（工号 + 姓名）。</summary>
/// <param name="EmployeeId">工号。</param>
/// <param name="Name">姓名。</param>
/// <param name="Count">处理次数。</param>
public sealed record OperatorCount(string EmployeeId, string Name, int Count);

/// <summary>
/// 台账统计（P0 轻量，不依赖任何图表或在线资源）。
/// </summary>
/// <param name="TotalRecords">记录总数。</param>
/// <param name="MachineCount">机器数。</param>
/// <param name="CategoryCounts">按问题类别的计数（按计数倒序）。</param>
/// <param name="OperatorCounts">按处理人的计数（按计数倒序）。</param>
/// <param name="UnresolvedCount">未解决记录数。</param>
/// <param name="UnresolvedRatio">未解决占比（0~1；无记录时为 0）。</param>
public sealed record LedgerStatistics(
    int TotalRecords,
    int MachineCount,
    IReadOnlyList<CategoryCount> CategoryCounts,
    IReadOnlyList<OperatorCount> OperatorCounts,
    int UnresolvedCount,
    double UnresolvedRatio);

/// <summary>
/// 处理记录的完整性校验结果。
/// </summary>
/// <param name="IsValid">是否通过校验。</param>
/// <param name="Problems">未通过项的问题说明。</param>
public sealed record LedgerRecordValidation(bool IsValid, IReadOnlyList<string> Problems);

/// <summary>
/// 报告产出结果。报告生成失败时以 <see cref="Success"/> = false 返回，不抛出异常（“生成失败不阻断”）。
/// </summary>
/// <param name="Success">是否成功产出。</param>
/// <param name="HtmlFileName">HTML 文件名（相对 reports 目录）；失败时为 null。</param>
/// <param name="MarkdownFileName">Markdown 文件名；失败时为 null。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
public sealed record ReportWriteResult(bool Success, string? HtmlFileName, string? MarkdownFileName, string? Error);

/// <summary>
/// 同一机器标识出现时间区间重叠的两条记录：提示标识冲突并标记“需人工确认”，两条记录并存不合并。
/// </summary>
/// <param name="MachineId">发生冲突的机器标识。</param>
/// <param name="FirstRecordId">冲突记录之一。</param>
/// <param name="SecondRecordId">冲突记录之二。</param>
public sealed record MachineIdConflict(string MachineId, string FirstRecordId, string SecondRecordId);