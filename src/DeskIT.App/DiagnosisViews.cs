using DeskIT.Core;

namespace DeskIT.App;

/// <summary>诊断依据的界面视图。</summary>
/// <param name="Label">依据名称。</param>
/// <param name="Display">依据显示文本。</param>
internal sealed record DiagnosisEvidenceView(string Label, string Display);

/// <summary>一条诊断结论的界面视图（供列表与详情面板绑定）。</summary>
/// <param name="FindingId">诊断项标识。</param>
/// <param name="SceneName">场景中文名。</param>
/// <param name="Name">诊断项名称。</param>
/// <param name="Conclusion">四态结论（用于徽标配色）。</param>
/// <param name="ConclusionText">结论中文文本。</param>
/// <param name="StatusText">执行状态中文文本。</param>
/// <param name="Reason">结论依据说明。</param>
/// <param name="ElapsedText">耗时文本。</param>
/// <param name="Evidence">依据清单。</param>
/// <param name="SuggestedActionsText">建议动作文本。</param>
/// <param name="HeaderText">列表中的标题文本（场景 · 诊断项）。</param>
/// <param name="ShowAdminHint">是否提示“以管理员身份重启后继续”。</param>
internal sealed record DiagnosisFindingView(
    string FindingId,
    string SceneName,
    string Name,
    DiagnosisConclusion Conclusion,
    string ConclusionText,
    string StatusText,
    string Reason,
    string ElapsedText,
    IReadOnlyList<DiagnosisEvidenceView> Evidence,
    string SuggestedActionsText,
    string HeaderText,
    bool ShowAdminHint);