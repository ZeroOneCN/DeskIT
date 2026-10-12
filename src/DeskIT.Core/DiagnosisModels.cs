namespace DeskIT.Core;

/// <summary>诊断依据的一条原始数据（数值或日志片段），用于向 IT 人员说明结论来源。</summary>
/// <param name="Label">依据名称，如“CPU 占用最高的进程”。</param>
/// <param name="Value">依据取值；不可得时为 null。</param>
public sealed record DiagnosisEvidence(string Label, string? Value);

/// <summary>
/// 一条诊断项的结论。结论只落在四态内；无法判定记“未知”并说明原因，绝不记为“异常”。
/// </summary>
/// <param name="FindingId">诊断项稳定标识（形如 slowness.cpu-top），用于关联建议动作与后续沉淀。</param>
/// <param name="Scene">所属问题场景。</param>
/// <param name="Name">诊断项名称（面向 IT 人员）。</param>
/// <param name="Status">执行状态（已执行 / 未执行需管理员 / 不适用）。</param>
/// <param name="Conclusion">四态结论之一。</param>
/// <param name="Reason">结论依据说明；未知时说明原因（无权限 / 超时 / 不支持）。</param>
/// <param name="Evidence">原始依据清单。</param>
/// <param name="SuggestedActionIds">建议的修复动作标识（关联 M4，仅为建议，需 IT 人员显式勾选）。</param>
/// <param name="Elapsed">该项诊断耗时。</param>
public sealed record DiagnosisFinding(
    string FindingId,
    DiagnosisScene Scene,
    string Name,
    DiagnosisExecutionStatus Status,
    DiagnosisConclusion Conclusion,
    string Reason,
    IReadOnlyList<DiagnosisEvidence> Evidence,
    IReadOnlyList<string> SuggestedActionIds,
    TimeSpan Elapsed)
{
    /// <summary>该项是否实际执行并给出结论。</summary>
    public bool IsExecuted => Status == DiagnosisExecutionStatus.Executed;
}

/// <summary>一个场景的诊断结果。</summary>
/// <param name="Scene">场景标识。</param>
/// <param name="Findings">该场景下的诊断项结论（未排序，展示前按异常置顶规则排序）。</param>
/// <param name="Elapsed">该场景整体耗时。</param>
public sealed record DiagnosisSceneResult(DiagnosisScene Scene, IReadOnlyList<DiagnosisFinding> Findings, TimeSpan Elapsed);

/// <summary>诊断结论统计。仅统计“已执行”的项；不适用与未执行（需管理员）单独计数，不计入异常与未知。</summary>
/// <param name="AbnormalCount">异常项数量。</param>
/// <param name="HintCount">提示项数量。</param>
/// <param name="UnknownCount">未知项数量。</param>
/// <param name="NormalCount">正常项数量。</param>
/// <param name="NotApplicableCount">不适用项数量。</param>
/// <param name="SkippedNeedsAdministratorCount">因只读模式未执行的项数量。</param>
/// <param name="TotalCount">诊断项总数。</param>
public sealed record DiagnosisSummary(
    int AbnormalCount,
    int HintCount,
    int UnknownCount,
    int NormalCount,
    int NotApplicableCount,
    int SkippedNeedsAdministratorCount,
    int TotalCount);

/// <summary>一次诊断的完整报告。</summary>
/// <param name="Scenes">各场景诊断结果。</param>
/// <param name="StartedAt">本次诊断开始时间。</param>
/// <param name="FinishedAt">本次诊断完成时间。</param>
public sealed record DiagnosisReport(
    IReadOnlyList<DiagnosisSceneResult> Scenes,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt)
{
    /// <summary>结论统计（由场景结果实时推导，不单独存储以避免不一致）。</summary>
    public DiagnosisSummary Summary => DiagnosisRules.BuildSummary(Scenes);

    /// <summary>本次诊断的全部结论（按场景顺序展开）。</summary>
    public IReadOnlyList<DiagnosisFinding> AllFindings => Scenes.SelectMany(scene => scene.Findings).ToList();
}

/// <summary>诊断进度：以“诊断项”为最小单位上报，异常置顶的列表可持续刷新。</summary>
/// <param name="Scene">所属场景。</param>
/// <param name="ItemName">当前完成的诊断项名称。</param>
/// <param name="CompletedItems">已完成的诊断项数量（含当前项）。</param>
/// <param name="TotalItems">本次诊断项总数。</param>
public sealed record DiagnosisProgress(DiagnosisScene Scene, string ItemName, int CompletedItems, int TotalItems);

/// <summary>
/// 诊断会话记录：诊断结论与依据一并写入 U 盘，作为处理记录与报告内容。
/// </summary>
/// <param name="SessionId">会话标识（形如 sess-20261012-193000-ab12）。</param>
/// <param name="MachineId">目标机机器标识；尚无法确定时为空串。</param>
/// <param name="HostName">计算机名。</param>
/// <param name="State">会话状态（由会话状态机推进）。</param>
/// <param name="CreatedAt">会话创建时间。</param>
/// <param name="UpdatedAt">最近一次状态变更时间。</param>
/// <param name="Scenes">本次会话下的诊断结果。</param>
public sealed record DiagnosisSession(
    string SessionId,
    string MachineId,
    string HostName,
    SessionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DiagnosisSceneResult> Scenes);