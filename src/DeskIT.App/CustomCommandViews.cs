using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeskIT.Core;

namespace DeskIT.App;

/// <summary>M9 命令历史列表项的界面视图：由一条命令执行记录派生，用于列表展示与“另存为修复动作”。</summary>
internal sealed class CustomCommandHistoryView
{
    /// <summary>初始化命令历史视图。</summary>
    /// <param name="record">命令执行记录。</param>
    public CustomCommandHistoryView(CustomCommandRecord record)
    {
        Record = record;
    }

    /// <summary>命令执行记录（“另存为修复动作”以其中的命令原文作为源命令）。</summary>
    public CustomCommandRecord Record { get; }

    /// <summary>命令标识。</summary>
    public string CommandId => Record.CommandId;

    /// <summary>执行时间文本。</summary>
    public string TimeText => Record.ExecutedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>命令原文（已脱敏）。</summary>
    public string CommandText => Record.CommandText;

    /// <summary>结果归类（用于徽标配色）。</summary>
    public ActionOutcome Outcome => Record.Outcome;

    /// <summary>结果中文文本。</summary>
    public string OutcomeText => ActionRules.DescribeOutcome(Record.Outcome);

    /// <summary>退出码 / 耗时 / 身份 / 超时等明细文本。</summary>
    public string DetailText
        => $"退出码 {Record.ExitCode} · 耗时 {Record.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture)} 秒 · 身份 {Record.ExecutedAs}"
           + (Record.TimedOut ? " · 已超时终止" : string.Empty);

    /// <summary>输出摘要；无输出时为提示文本。</summary>
    public string OutputText => string.IsNullOrWhiteSpace(Record.OutputSummary) ? "（无标准输出）" : Record.OutputSummary;

    /// <summary>持久化变更文本；无变更或未知时给出相应说明。</summary>
    public string PersistenceText => CustomCommandViewFactory.DescribePersistence(Record.PersistenceChanges);

    /// <summary>是否存在需展示的持久化变更（含“未知”）。</summary>
    public bool HasPersistenceChanges
        => Record.PersistenceChanges.Any(change => change.IsUnknown || change.HasChanges);
}

/// <summary>M9 自定义命令界面的文本格式化工厂（纯展示逻辑，无副作用）。</summary>
internal static class CustomCommandViewFactory
{
    /// <summary>
    /// 把持久化变更清单格式化为多行文本：每行“类型：新增 …；移除 …”，未知时标注“未知（原因）”。
    /// </summary>
    /// <param name="changes">持久化变更清单。</param>
    /// <returns>多行文本；无有效内容时为空串。</returns>
    public static string DescribePersistence(IReadOnlyList<PersistenceChange> changes)
    {
        if (changes.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var change in changes)
        {
            var kind = CustomCommandRules.DescribePersistenceKind(change.Kind);
            if (change.IsUnknown)
            {
                lines.Add($"{kind}：未知{change.Note}");
                continue;
            }

            if (!change.HasChanges)
            {
                continue;
            }

            var detail = new List<string>();
            if (change.Added.Count > 0)
            {
                detail.Add("新增 " + string.Join("、", change.Added));
            }

            if (change.Removed.Count > 0)
            {
                detail.Add("移除 " + string.Join("、", change.Removed));
            }

            lines.Add($"{kind}：" + string.Join("；", detail));
        }

        return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
    }

    /// <summary>
    /// 把命令执行记录集合转换为历史列表视图（按执行时间倒序，最新在前）。
    /// </summary>
    /// <param name="records">命令执行记录集合。</param>
    /// <returns>历史视图列表。</returns>
    public static IReadOnlyList<CustomCommandHistoryView> BuildHistory(IEnumerable<CustomCommandRecord> records)
        => records
            .OrderByDescending(record => record.ExecutedAt)
            .Select(record => new CustomCommandHistoryView(record))
            .ToList();
}