using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DeskIT.Core;
using DeskIT.Windows;

namespace DeskIT.App;

/// <summary>结论单选项（值 + 中文文本 + 是否必须填写原因）。</summary>
/// <param name="Conclusion">会话结论值。</param>
/// <param name="Text">中文文本。</param>
/// <param name="RequiresReason">是否必须选择未解决原因。</param>
internal sealed record LedgerConclusionOption(SessionConclusion Conclusion, string Text, bool RequiresReason);

/// <summary>问题类别单选项（值 + 中文文本）。</summary>
/// <param name="Category">问题类别值。</param>
/// <param name="Text">中文文本。</param>
internal sealed record LedgerCategoryOption(ProblemCategory Category, string Text);

/// <summary>未解决原因选项（值 + 中文文本）。</summary>
/// <param name="Reason">未解决原因值。</param>
/// <param name="Text">中文文本。</param>
internal sealed record LedgerUnresolvedReasonOption(UnresolvedReason Reason, string Text);

/// <summary>处理记录中一条动作的展示行。</summary>
/// <param name="ActionText">动作名称（标识）。</param>
/// <param name="Outcome">结果归类（用于徽标配色）。</param>
/// <param name="OutcomeText">结果中文文本。</param>
/// <param name="DurationText">耗时文本。</param>
internal sealed record LedgerActionRowView(string ActionText, ActionOutcome Outcome, string OutcomeText, string DurationText);

/// <summary>
/// 一条处理记录的界面视图（用于目标机档案与台账总览列表）。
/// </summary>
internal sealed class LedgerRecordView
{
    /// <summary>
    /// 初始化处理记录视图。
    /// </summary>
    /// <param name="record">处理记录。</param>
    /// <param name="isConflict">是否被标记为机器标识冲突（需人工确认）。</param>
    public LedgerRecordView(HandlingRecord record, bool isConflict)
    {
        Record = record;
        RecordId = record.RecordId;
        HeaderText = $"{LedgerRules.DescribeCategory(record.Category)} · {LedgerRules.DescribeConclusion(record.Conclusion)}";
        Category = record.Category;
        CategoryText = LedgerRules.DescribeCategory(record.Category);
        Conclusion = record.Conclusion;
        ConclusionText = LedgerRules.DescribeConclusion(record.Conclusion);
        TimeText = $"处理时间：{record.FinishedAt:yyyy-MM-dd HH:mm:ss} · 耗时 {record.Duration.TotalSeconds:0.0} 秒";
        OperatorText = $"处理人：{record.Operator.DisplayText} · 权限：{(record.Privilege == PrivilegeLevel.Administrator ? "管理员" : "只读")}";
        MachineText = $"{record.HostName} · {record.MachineId}";
        TagText = record.Tags.Count == 0 ? string.Empty : "标签：" + string.Join("、", record.Tags);
        HasTags = record.Tags.Count > 0;
        IsConflict = isConflict;
        ConflictText = isConflict ? "需人工确认：同一机器标识存在时间区间重叠的记录" : string.Empty;

        ActionSummaryText = record.Actions.Count == 0
            ? "本次未执行修复动作。"
            : "执行动作：" + string.Join("、", record.Actions.Select(
                action => $"{action.ActionName}[{ActionRules.DescribeOutcome(action.Outcome)}]"));

        Actions = record.Actions
            .Select(action => new LedgerActionRowView(
                $"{action.ActionName}（{action.ActionId}）",
                action.Outcome,
                ActionRules.DescribeOutcome(action.Outcome),
                $"耗时 {action.DurationSeconds:0.0} 秒"))
            .ToList();

        DetailText = BuildDetailText(record);
        HasDetail = DetailText.Length > 0;
        ReportName = record.ReportFileName;
    }

    /// <summary>处理记录原始对象。</summary>
    public HandlingRecord Record { get; }

    /// <summary>记录标识。</summary>
    public string RecordId { get; }

    /// <summary>列表标题（问题类别 · 结论）。</summary>
    public string HeaderText { get; }

    /// <summary>问题类别。</summary>
    public ProblemCategory Category { get; }

    /// <summary>问题类别中文文本。</summary>
    public string CategoryText { get; }

    /// <summary>结论（用于徽标配色）。</summary>
    public SessionConclusion Conclusion { get; }

    /// <summary>结论中文文本。</summary>
    public string ConclusionText { get; }

    /// <summary>处理时间与耗时文本。</summary>
    public string TimeText { get; }

    /// <summary>处理人与权限文本。</summary>
    public string OperatorText { get; }

    /// <summary>计算机名与机器标识文本。</summary>
    public string MachineText { get; }

    /// <summary>自定义标签文本；无为空。</summary>
    public string TagText { get; }

    /// <summary>是否有自定义标签。</summary>
    public bool HasTags { get; }

    /// <summary>是否被标记为机器标识冲突。</summary>
    public bool IsConflict { get; }

    /// <summary>冲突提示文本；无冲突为空。</summary>
    public string ConflictText { get; }

    /// <summary>执行动作摘要文本。</summary>
    public string ActionSummaryText { get; }

    /// <summary>执行动作明细行。</summary>
    public IReadOnlyList<LedgerActionRowView> Actions { get; }

    /// <summary>诊断依据明细文本（逐项）。</summary>
    public string DetailText { get; }

    /// <summary>是否有诊断依据明细。</summary>
    public bool HasDetail { get; }

    /// <summary>报告文件名。</summary>
    public string ReportName { get; }

    /// <summary>
    /// 把诊断逐项结论拼成可展开查看的依据明细文本。
    /// </summary>
    /// <param name="record">处理记录。</param>
    /// <returns>每行一项“场景 · 诊断项：结论（执行状态 / 说明）”；无诊断项时为空。</returns>
    private static string BuildDetailText(HandlingRecord record)
    {
        var findings = DiagnosisRules.SortFindings(record.Scenes.SelectMany(scene => scene.Findings));
        if (findings.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("当时的诊断依据（").Append(findings.Count).AppendLine(" 项）：");
        foreach (var finding in findings)
        {
            builder.Append("· ").Append(DiagnosisRules.DescribeScene(finding.Scene)).Append(" · ").Append(finding.Name)
                .Append("：").Append(DiagnosisRules.DescribeConclusion(finding.Conclusion))
                .Append("（").Append(DiagnosisRules.DescribeStatus(finding.Status));
            if (!string.IsNullOrWhiteSpace(finding.Reason))
            {
                builder.Append("，").Append(finding.Reason);
            }

            builder.AppendLine("）");
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>一台机器（目标机档案）的界面视图。</summary>
internal sealed class LedgerMachineView
{
    /// <summary>
    /// 初始化机器档案视图。
    /// </summary>
    /// <param name="ledger">机器档案。</param>
    /// <param name="conflictRecordIds">被标记为标识冲突的记录标识集合。</param>
    public LedgerMachineView(MachineLedger ledger, IReadOnlySet<string> conflictRecordIds)
    {
        MachineId = ledger.MachineId;
        HostName = ledger.HostName;
        HeaderText = $"{ledger.HostName} · {ledger.MachineId}";
        SummaryText =
            $"来源：{MachineIdSourceText.Describe(ledger.MachineIdSource)} · 处理 {ledger.ProcessingCount} 次 · "
            + $"首次 {ledger.FirstHandledAt:yyyy-MM-dd HH:mm} · 最近 {ledger.LastHandledAt:yyyy-MM-dd HH:mm}";
        LastConclusion = ledger.LastConclusion;
        LastConclusionText = "最近结论：" + LedgerRules.DescribeConclusion(ledger.LastConclusion);
        RecordCount = ledger.ProcessingCount;
        Records = ledger.Records
            .Select(record => new LedgerRecordView(record, conflictRecordIds.Contains(record.RecordId)))
            .ToList();
    }

    /// <summary>机器标识。</summary>
    public string MachineId { get; }

    /// <summary>计算机名。</summary>
    public string HostName { get; }

    /// <summary>列表标题（计算机名 · 机器标识）。</summary>
    public string HeaderText { get; }

    /// <summary>档案摘要文本。</summary>
    public string SummaryText { get; }

    /// <summary>最近结论（用于徽标配色）。</summary>
    public SessionConclusion LastConclusion { get; }

    /// <summary>最近结论文本。</summary>
    public string LastConclusionText { get; }

    /// <summary>处理次数。</summary>
    public int RecordCount { get; }

    /// <summary>该机器的记录视图（按处理时间倒序）。</summary>
    public IReadOnlyList<LedgerRecordView> Records { get; }
}

/// <summary>台账统计的界面文本。</summary>
/// <param name="TotalText">总数与机器数文本。</param>
/// <param name="CategoryText">按问题类别计数文本。</param>
/// <param name="OperatorText">按处理人计数文本。</param>
/// <param name="UnresolvedText">未解决数与占比文本。</param>
internal sealed record LedgerStatView(string TotalText, string CategoryText, string OperatorText, string UnresolvedText);

/// <summary>report 目录中一个报告文件的界面视图。</summary>
/// <param name="FileName">文件名。</param>
/// <param name="FullPath">绝对路径。</param>
/// <param name="SizeText">大小文本。</param>
/// <param name="TimeText">修改时间文本。</param>
internal sealed record LedgerReportFileView(string FileName, string FullPath, string SizeText, string TimeText);

/// <summary>台账导出的范围。</summary>
internal enum LedgerExportScope
{
    /// <summary>全部记录。</summary>
    All = 0,

    /// <summary>当前筛选结果。</summary>
    CurrentFiltered = 1,

    /// <summary>当前处理的机器。</summary>
    CurrentMachine = 2
}

/// <summary>台账导出范围选项（值 + 中文文本）。</summary>
/// <param name="Scope">导出范围。</param>
/// <param name="Text">中文文本。</param>
internal sealed record LedgerExportScopeOption(LedgerExportScope Scope, string Text);

/// <summary>台账导出的文件格式。</summary>
internal enum LedgerExportFormat
{
    /// <summary>CSV 台账表（范围内全部记录，一行一条）。</summary>
    Csv = 0,

    /// <summary>HTML 报告（自包含单文件，取范围内最近一条记录）。</summary>
    Html = 1,

    /// <summary>Markdown 报告（取范围内最近一条记录）。</summary>
    Markdown = 2
}

/// <summary>台账导出格式选项（值 + 中文文本）。</summary>
/// <param name="Format">导出格式。</param>
/// <param name="Text">中文文本。</param>
internal sealed record LedgerExportFormatOption(LedgerExportFormat Format, string Text);

/// <summary>台账筛选的类别选项（含“全部类别”）。</summary>
/// <param name="Category">问题类别；为 null 表示不限制。</param>
/// <param name="Text">中文文本。</param>
internal sealed record LedgerCategoryFilterOption(ProblemCategory? Category, string Text);

/// <summary>M7 台账视图的构造辅助（把领域模型转换为界面视图）。</summary>
internal static class LedgerViewFactory
{
    /// <summary>
    /// 构建结论单选项（已解决 / 待重启生效 / 未解决；未解决需填原因）。
    /// </summary>
    /// <returns>结论选项清单。</returns>
    public static IReadOnlyList<LedgerConclusionOption> BuildConclusionOptions() => new[]
    {
        new LedgerConclusionOption(SessionConclusion.Resolved, "已解决", false),
        new LedgerConclusionOption(SessionConclusion.PendingRestart, "待重启生效", false),
        new LedgerConclusionOption(SessionConclusion.Unresolved, "未解决", true)
    };

    /// <summary>
    /// 构建问题类别单选项（7 个场景 + 其他）。
    /// </summary>
    /// <returns>问题类别选项清单。</returns>
    public static IReadOnlyList<LedgerCategoryOption> BuildCategoryOptions()
    {
        var categories = new[]
        {
            ProblemCategory.Slowness,
            ProblemCategory.Network,
            ProblemCategory.PrintingSharing,
            ProblemCategory.DiskStorage,
            ProblemCategory.StartupSystem,
            ProblemCategory.Software,
            ProblemCategory.AccountPrivilege,
            ProblemCategory.Other
        };

        return categories.Select(category => new LedgerCategoryOption(category, LedgerRules.DescribeCategory(category))).ToList();
    }

    /// <summary>
    /// 构建未解决原因选项。
    /// </summary>
    /// <returns>未解决原因选项清单。</returns>
    public static IReadOnlyList<LedgerUnresolvedReasonOption> BuildUnresolvedReasonOptions()
    {
        var reasons = new[]
        {
            UnresolvedReason.NeedPartOrVendor,
            UnresolvedReason.NeedNetworkOrServer,
            UnresolvedReason.NeedUserObservation,
            UnresolvedReason.PermissionOrPolicy,
            UnresolvedReason.NeedBackupFirst,
            UnresolvedReason.CannotReproduce,
            UnresolvedReason.Other
        };

        return reasons.Select(reason => new LedgerUnresolvedReasonOption(reason, LedgerRules.DescribeUnresolvedReason(reason))).ToList();
    }

    /// <summary>
    /// 把一台机器档案转换为界面视图。
    /// </summary>
    /// <param name="ledger">机器档案。</param>
    /// <param name="conflictRecordIds">被标记为标识冲突的记录标识集合。</param>
    /// <returns>机器档案视图。</returns>
    public static LedgerMachineView BuildMachine(MachineLedger ledger, IReadOnlySet<string> conflictRecordIds)
        => new(ledger, conflictRecordIds);

    /// <summary>
    /// 把台账统计转换为界面文本。
    /// </summary>
    /// <param name="statistics">台账统计。</param>
    /// <returns>统计视图。</returns>
    public static LedgerStatView BuildStatistics(LedgerStatistics statistics)
    {
        var total = $"共 {statistics.TotalRecords} 条记录 · {statistics.MachineCount} 台机器";
        var categories = statistics.CategoryCounts.Count == 0
            ? "按问题类别：暂无记录。"
            : "按问题类别：" + string.Join("、", statistics.CategoryCounts.Select(
                item => $"{LedgerRules.DescribeCategory(item.Category)} {item.Count}"));
        var operators = statistics.OperatorCounts.Count == 0
            ? "按处理人：暂无记录。"
            : "按处理人：" + string.Join("、", statistics.OperatorCounts.Select(
                item => $"{item.Name}（{item.EmployeeId}） {item.Count}"));
        var unresolved = $"未解决 {statistics.UnresolvedCount} 条 · 占比 {statistics.UnresolvedRatio:P0}";
        return new LedgerStatView(total, categories, operators, unresolved);
    }

    /// <summary>
    /// 把 report 目录文件信息转换为界面视图。
    /// </summary>
    /// <param name="info">报告文件信息。</param>
    /// <returns>报告文件视图。</returns>
    public static LedgerReportFileView BuildReportFile(ReportFileInfo info)
        => new(info.FileName, info.FullPath, FormatSize(info.SizeBytes), $"修改时间：{info.ModifiedAt:yyyy-MM-dd HH:mm:ss}");

    /// <summary>
    /// 构建台账筛选的问题类别选项（首项“全部类别”表示不限制）。
    /// </summary>
    /// <returns>类别筛选选项清单。</returns>
    public static IReadOnlyList<LedgerCategoryFilterOption> BuildCategoryFilterOptions()
        => new[] { new LedgerCategoryFilterOption(null, "全部类别") }
            .Concat(BuildCategoryOptions().Select(option => new LedgerCategoryFilterOption(option.Category, option.Text)))
            .ToList();

    /// <summary>
    /// 构建台账导出范围选项。
    /// </summary>
    /// <returns>导出范围选项清单。</returns>
    public static IReadOnlyList<LedgerExportScopeOption> BuildExportScopeOptions() => new[]
    {
        new LedgerExportScopeOption(LedgerExportScope.All, "全部记录"),
        new LedgerExportScopeOption(LedgerExportScope.CurrentFiltered, "当前筛选结果"),
        new LedgerExportScopeOption(LedgerExportScope.CurrentMachine, "当前处理机器")
    };

    /// <summary>
    /// 构建台账导出格式选项（CSV 为多条台账表；HTML / Markdown 为单条记录报告）。
    /// </summary>
    /// <returns>导出格式选项清单。</returns>
    public static IReadOnlyList<LedgerExportFormatOption> BuildExportFormatOptions() => new[]
    {
        new LedgerExportFormatOption(LedgerExportFormat.Csv, "CSV 台账表（多条）"),
        new LedgerExportFormatOption(LedgerExportFormat.Html, "HTML 报告（单条）"),
        new LedgerExportFormatOption(LedgerExportFormat.Markdown, "Markdown 报告（单条）")
    };

    /// <summary>
    /// 把字节数格式化为可读文本（不依赖任何外部库）。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>如“1.2 MB”。</returns>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        string[] units = { "KB", "MB", "GB" };
        var unitIndex = -1;
        do
        {
            value /= 1024;
            unitIndex++;
        }
        while (value >= 1024 && unitIndex < units.Length - 1);

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unitIndex];
    }
}