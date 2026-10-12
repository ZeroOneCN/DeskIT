using System.Globalization;
using System.Text;

namespace DeskIT.Core;

/// <summary>
/// M7 报告生成器（纯字符串生成，不依赖文件系统与任何外部资源）。
/// 生成的 HTML 自包含（内联 CSS、无外部字体 / 图片 / 脚本），可直接双击用浏览器离线打开；
/// 同时提供 Markdown 与 CSV 版本。默认产出脱敏版本（见 <see cref="LedgerRules.Desensitize"/>）。
/// </summary>
public static class LedgerReportBuilder
{
    /// <summary>报告标题。</summary>
    private const string ReportTitle = "处理记录报告";

    /// <summary>
    /// 生成自包含 HTML 报告。
    /// </summary>
    /// <param name="record">处理记录。</param>
    /// <param name="desensitized">是否输出脱敏版本（遮蔽处理人姓名、掩码凭据）。</param>
    /// <returns>完整的 HTML 文档文本（简体中文）。</returns>
    public static string BuildHtml(HandlingRecord record, bool desensitized)
    {
        var source = desensitized ? LedgerRules.Desensitize(record) : record;
        var summary = source.DiagnosisSummary;
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-CN\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\" />");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.Append("<title>DeskIT ").Append(ReportTitle).Append(" - ").Append(Encode(source.MachineId)).AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(":root{--ink:#111827;--muted:#6b7280;--line:#e5e7eb;--bg:#f9fafb;--accent:#1d4ed8;--warn:#b45309;--danger:#b91c1c;}");
        builder.AppendLine("*{box-sizing:border-box;}");
        builder.AppendLine("body{margin:0;background:var(--bg);color:var(--ink);font-family:\"Microsoft YaHei\",\"PingFang SC\",\"Hiragino Sans GB\",sans-serif;line-height:1.6;}");
        builder.AppendLine(".wrap{max-width:960px;margin:0 auto;padding:24px;}");
        builder.AppendLine(".cover{background:#fff;border:1px solid var(--line);border-radius:10px;padding:24px;}");
        builder.AppendLine(".brand{font-size:13px;color:var(--accent);font-weight:600;letter-spacing:1px;}");
        builder.AppendLine("h1{font-size:24px;margin:8px 0 4px;}");
        builder.AppendLine(".meta{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:6px 24px;margin-top:14px;font-size:13px;}");
        builder.AppendLine(".meta div{color:var(--muted);}");
        builder.AppendLine(".meta b{color:var(--ink);font-weight:600;}");
        builder.AppendLine(".pill{display:inline-block;padding:2px 10px;border-radius:10px;font-size:12px;color:#fff;margin-right:8px;}");
        builder.AppendLine(".pill.ok{background:#16a34a;}.pill.pending{background:#7c3aed;}.pill.bad{background:#dc2626;}.pill.interrupt{background:#6b7280;}.pill.mask{background:#2563eb;}");
        builder.AppendLine("section{background:#fff;border:1px solid var(--line);border-radius:10px;padding:18px 20px;margin-top:16px;}");
        builder.AppendLine("section>h2{font-size:16px;margin:0 0 10px;}");
        builder.AppendLine("table{width:100%;border-collapse:collapse;font-size:13px;}");
        builder.AppendLine("th,td{border:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top;}");
        builder.AppendLine("th{background:#f3f4f6;font-weight:600;}");
        builder.AppendLine(".muted{color:var(--muted);font-size:12px;}");
        builder.AppendLine(".evidence{color:var(--muted);font-size:12px;margin:4px 0 0;}");
        builder.AppendLine(".note{background:#fffbeb;border:1px solid #fde68a;border-radius:8px;padding:12px 14px;margin-top:16px;color:var(--warn);}");
        builder.AppendLine(".danger{background:#fef2f2;border:1px solid #fecaca;border-radius:8px;padding:12px 14px;margin-top:16px;color:var(--danger);}");
        builder.AppendLine("footer{color:var(--muted);font-size:12px;margin:20px 2px 6px;}");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<div class=\"wrap\">");

        // 封面
        builder.AppendLine("<header class=\"cover\">");
        builder.Append("<div class=\"brand\">DeskIT 处理记录报告</div>");
        builder.Append("<div style=\"margin-top:6px;\">");
        builder.Append("<span class=\"pill ").Append(ConclusionClass(source.Conclusion)).Append("\">")
            .Append(Encode(LedgerRules.DescribeConclusion(source.Conclusion))).Append("</span>");
        if (desensitized)
        {
            builder.Append("<span class=\"pill mask\">脱敏版本</span>");
        }

        builder.AppendLine("</div>");
        builder.Append("<h1>").Append(Encode(source.HostName)).Append(" · ").Append(Encode(source.MachineId)).AppendLine("</h1>");
        builder.AppendLine("<div class=\"meta\">");
        AppendMeta(builder, "机器标识", $"{source.MachineId}（{MachineIdSourceText.Describe(source.MachineIdSource)}）");
        AppendMeta(builder, "处理人", source.Operator.DisplayText);
        AppendMeta(builder, "问题类别", LedgerRules.DescribeCategory(source.Category));
        AppendMeta(builder, "权限级别", source.Privilege == PrivilegeLevel.Administrator ? "管理员" : "只读（标准用户）");
        AppendMeta(builder, "处理时间", source.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        AppendMeta(builder, "处理耗时", $"{source.Duration.TotalSeconds:0.0} 秒");
        AppendMeta(builder, "自定义标签", source.Tags.Count == 0 ? "（无）" : string.Join("、", source.Tags));
        AppendMeta(builder, "记录标识", source.RecordId);
        builder.AppendLine("</div>");
        builder.AppendLine("</header>");

        // 中断标注：避免读者误以为处理已完成
        if (source.IsInterrupted)
        {
            builder.AppendLine("<div class=\"danger\">");
            builder.Append("<b>注意：本次会话以“已中断”结束，处理尚未完成。</b><br />");
            builder.Append("<span>中断原因：").Append(Encode(string.IsNullOrWhiteSpace(source.InterruptReason) ? "未记录（现场未提供）" : source.InterruptReason!)).AppendLine("</span><br />");
            builder.AppendLine("<span>保留范围：诊断结论与已执行动作清单保留至中断时刻；未完成部分（如最终结论确认、剩余动作）不在本报告范围内，请结合现场情况判断。</span>");
            builder.AppendLine("</div>");
        }

        BuildHtmlDiagnosis(builder, source, summary);
        BuildHtmlActions(builder, source);
        BuildHtmlCustomCommands(builder, source);
        BuildHtmlPersistence(builder, source);
        BuildHtmlConclusion(builder, source);
        BuildHtmlSupplements(builder, source);

        builder.Append("<footer>本报告由 DeskIT 自动生成（")
            .Append(desensitized ? "脱敏版本" : "完整版本")
            .Append("）· 生成时间 ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine("</footer>");
        builder.AppendLine("</div>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");

        return builder.ToString();
    }

    /// <summary>
    /// 生成 Markdown 报告。
    /// </summary>
    /// <param name="record">处理记录。</param>
    /// <param name="desensitized">是否输出脱敏版本。</param>
    /// <returns>Markdown 文本（简体中文）。</returns>
    public static string BuildMarkdown(HandlingRecord record, bool desensitized)
    {
        var source = desensitized ? LedgerRules.Desensitize(record) : record;
        var summary = source.DiagnosisSummary;
        var builder = new StringBuilder();

        builder.Append("# DeskIT 处理记录报告 - ").Append(source.HostName).AppendLine();
        builder.AppendLine();
        builder.Append("- 结论：").Append(LedgerRules.DescribeConclusion(source.Conclusion))
            .Append(desensitized ? "（脱敏版本）" : string.Empty).AppendLine();
        builder.Append("- 机器标识：").Append(source.MachineId).Append("（").Append(MachineIdSourceText.Describe(source.MachineIdSource)).AppendLine("）");
        builder.Append("- 处理人：").Append(source.Operator.DisplayText).AppendLine();
        builder.Append("- 问题类别：").Append(LedgerRules.DescribeCategory(source.Category)).AppendLine();
        builder.Append("- 自定义标签：").Append(source.Tags.Count == 0 ? "（无）" : string.Join("、", source.Tags)).AppendLine();
        builder.Append("- 权限级别：").Append(source.Privilege == PrivilegeLevel.Administrator ? "管理员" : "只读（标准用户）").AppendLine();
        builder.Append("- 处理时间：").Append(source.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).AppendLine();
        builder.Append("- 处理耗时：").Append(source.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).AppendLine(" 秒");
        builder.Append("- 记录标识：").Append(source.RecordId).AppendLine();
        builder.AppendLine();

        if (source.IsInterrupted)
        {
            builder.Append("> 注意：本次会话以“已中断”结束，处理尚未完成。").AppendLine();
            builder.Append("> 中断原因：").Append(string.IsNullOrWhiteSpace(source.InterruptReason) ? "未记录（现场未提供）" : source.InterruptReason).AppendLine();
            builder.Append("> 保留范围：诊断结论与已执行动作清单保留至中断时刻，未完成部分不在本报告范围内。").AppendLine();
            builder.AppendLine();
        }

        builder.Append("## 诊断汇总").AppendLine();
        builder.AppendLine();
        builder.Append(DiagnosisRules.DescribeSummary(summary)).AppendLine();
        builder.AppendLine();
        builder.Append("| 结论 | 项数 |").AppendLine();
        builder.Append("| --- | --- |").AppendLine();
        builder.Append("| 异常 | ").Append(summary.AbnormalCount).Append(" |").AppendLine();
        builder.Append("| 提示 | ").Append(summary.HintCount).Append(" |").AppendLine();
        builder.Append("| 未知 | ").Append(summary.UnknownCount).Append(" |").AppendLine();
        builder.Append("| 正常 | ").Append(summary.NormalCount).Append(" |").AppendLine();
        builder.Append("| 不适用 | ").Append(summary.NotApplicableCount).Append(" |").AppendLine();
        builder.Append("| 未执行（需管理员权限） | ").Append(summary.SkippedNeedsAdministratorCount).Append(" |").AppendLine();
        builder.Append("| 合计 | ").Append(summary.TotalCount).Append(" |").AppendLine();
        builder.AppendLine();

        var findings = DiagnosisRules.SortFindings(source.Scenes.SelectMany(scene => scene.Findings));
        builder.Append("## 逐项依据（").Append(findings.Count).Append(" 项）").AppendLine();
        builder.AppendLine();
        if (findings.Count == 0)
        {
            builder.AppendLine("（本次未执行诊断项。）");
        }
        else
        {
            builder.Append("| 场景 | 诊断项 | 结论 | 执行状态 | 依据说明 |").AppendLine();
            builder.Append("| --- | --- | --- | --- | --- |").AppendLine();
            foreach (var finding in findings)
            {
                builder.Append("| ").Append(EscapeCell(DiagnosisRules.DescribeScene(finding.Scene)))
                    .Append(" | ").Append(EscapeCell(finding.Name))
                    .Append(" | ").Append(EscapeCell(DiagnosisRules.DescribeConclusion(finding.Conclusion)))
                    .Append(" | ").Append(EscapeCell(DiagnosisRules.DescribeStatus(finding.Status)))
                    .Append(" | ").Append(EscapeCell(finding.Reason))
                    .Append(" |").AppendLine();
            }
        }

        builder.AppendLine();
        builder.Append("## 执行动作清单（").Append(source.Actions.Count).Append(" 项）").AppendLine();
        builder.AppendLine();
        if (source.Actions.Count == 0)
        {
            builder.AppendLine("（本次未执行修复动作。）");
        }
        else
        {
            builder.Append("| 动作 | 风险 | 结果 | 耗时(秒) | 输出摘要 |").AppendLine();
            builder.Append("| --- | --- | --- | --- | --- |").AppendLine();
            foreach (var action in source.Actions)
            {
                builder.Append("| ").Append(EscapeCell($"{action.ActionName}（{action.ActionId}）"))
                    .Append(" | ").Append(EscapeCell(ActionRules.DescribeRisk(action.Risk)))
                    .Append(" | ").Append(EscapeCell(ActionRules.DescribeOutcome(action.Outcome)))
                    .Append(" | ").Append(action.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(EscapeCell(OneLine(action.OutputSummary)))
                    .Append(" |").AppendLine();
            }
        }

        builder.AppendLine();
        builder.Append("## 自定义命令清单（").Append(source.CustomCommands.Count).Append(" 项）").AppendLine();
        builder.AppendLine();
        if (source.CustomCommands.Count == 0)
        {
            builder.AppendLine("（本次未使用自定义命令。）");
        }
        else
        {
            builder.Append("| 命令 | 执行身份 | 退出码 | 耗时(秒) | 输出摘要 |").AppendLine();
            builder.Append("| --- | --- | --- | --- | --- |").AppendLine();
            foreach (var command in source.CustomCommands)
            {
                builder.Append("| ").Append(EscapeCell(command.CommandText))
                    .Append(" | ").Append(EscapeCell(command.ExecutedAs))
                    .Append(" | ").Append(command.ExitCode.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(command.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(EscapeCell(OneLine(command.OutputSummary)))
                    .Append(" |").AppendLine();
            }
        }

        builder.AppendLine();
        builder.Append("## 处理后系统持久化变更（").Append(source.PersistenceChanges.Count).Append(" 类）").AppendLine();
        builder.AppendLine();
        if (source.PersistenceChanges.Count == 0)
        {
            builder.AppendLine("（本次未检测到持久化变更。）");
        }
        else
        {
            builder.Append("| 类型 | 变更 | 明细 |").AppendLine();
            builder.Append("| --- | --- | --- |").AppendLine();
            foreach (var change in source.PersistenceChanges)
            {
                builder.Append("| ").Append(EscapeCell(CustomCommandRules.DescribePersistenceKind(change.Kind)))
                    .Append(" | ").Append(EscapeCell(DescribePersistenceChange(change)))
                    .Append(" | ").Append(EscapeCell(DescribePersistenceDetail(change)))
                    .Append(" |").AppendLine();
            }
        }

        builder.AppendLine();
        builder.Append("## 结论与原因").AppendLine();
        builder.AppendLine();
        builder.Append("- 结论：").Append(LedgerRules.DescribeConclusion(source.Conclusion)).AppendLine();
        if (source.Conclusion == SessionConclusion.Unresolved)
        {
            builder.Append("- 未解决原因：").Append(source.UnresolvedReason is null
                ? "（未填写）"
                : LedgerRules.DescribeUnresolvedReason(source.UnresolvedReason.Value)).AppendLine();
            builder.Append("- 未解决说明：").Append(string.IsNullOrWhiteSpace(source.UnresolvedNote) ? "（无）" : source.UnresolvedNote).AppendLine();
        }

        builder.AppendLine();
        builder.Append("## 补充说明（").Append(source.Supplements.Count).Append(" 条）").AppendLine();
        builder.AppendLine();
        if (source.Supplements.Count == 0)
        {
            builder.AppendLine("（暂无补充说明。）");
        }
        else
        {
            foreach (var supplement in source.Supplements)
            {
                builder.Append("- ").Append(supplement.AddedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append(" · ").Append(supplement.Operator.DisplayText).Append("：").Append(OneLine(supplement.Text)).AppendLine();
            }
        }

        builder.AppendLine();
        builder.Append("> 本报告由 DeskIT 自动生成（").Append(desensitized ? "脱敏版本" : "完整版本").Append("）。").AppendLine();

        return builder.ToString();
    }

    /// <summary>
    /// 生成 CSV 台账（用于导出）。默认可对记录集合先脱敏再传入；本方法按传入记录原样输出。
    /// </summary>
    /// <param name="records">处理记录集合。</param>
    /// <returns>CSV 文本（含表头，UTF-8 书写时带 BOM 便于 Excel 识别中文）。</returns>
    public static string BuildCsv(IEnumerable<HandlingRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("记录标识,会话标识,机器标识,标识来源,计算机名,处理人工号,处理人姓名,问题类别,标签,员工现象描述,结论,未解决原因,权限级别,开始时间,结束时间,耗时秒,报告文件,动作数,补充说明数");

        foreach (var record in records)
        {
            var cells = new[]
            {
                record.RecordId,
                record.SessionId,
                record.MachineId,
                MachineIdSourceText.Describe(record.MachineIdSource),
                record.HostName,
                record.Operator.EmployeeId,
                record.Operator.Name,
                LedgerRules.DescribeCategory(record.Category),
                string.Join("、", record.Tags),
                record.SymptomDescription,
                LedgerRules.DescribeConclusion(record.Conclusion),
                record.UnresolvedReason is null ? string.Empty : LedgerRules.DescribeUnresolvedReason(record.UnresolvedReason.Value),
                record.Privilege == PrivilegeLevel.Administrator ? "管理员" : "只读",
                record.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                record.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                record.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                record.ReportFileName,
                record.Actions.Count.ToString(CultureInfo.InvariantCulture),
                record.Supplements.Count.ToString(CultureInfo.InvariantCulture)
            };

            builder.AppendLine(string.Join(",", Array.ConvertAll(cells, CsvCell)));
        }

        return builder.ToString();
    }

    /// <summary>生成“诊断汇总与逐项依据”HTML 段。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="record">处理记录。</param>
    /// <param name="summary">诊断结论统计。</param>
    private static void BuildHtmlDiagnosis(StringBuilder builder, HandlingRecord record, DiagnosisSummary summary)
    {
        builder.AppendLine("<section>");
        builder.AppendLine("<h2>诊断汇总与逐项依据</h2>");
        builder.Append("<p>").Append(Encode(DiagnosisRules.DescribeSummary(summary))).AppendLine("</p>");
        builder.AppendLine("<table><thead><tr><th>场景</th><th>诊断项</th><th>结论</th><th>执行状态</th><th>依据说明</th></tr></thead><tbody>");

        var findings = DiagnosisRules.SortFindings(record.Scenes.SelectMany(scene => scene.Findings));
        if (findings.Count == 0)
        {
            builder.AppendLine("<tr><td colspan=\"5\" class=\"muted\">本次未执行诊断项。</td></tr>");
        }
        else
        {
            foreach (var finding in findings)
            {
                builder.Append("<tr><td>").Append(Encode(DiagnosisRules.DescribeScene(finding.Scene))).Append("</td><td>")
                    .Append(Encode(finding.Name)).Append("</td><td>")
                    .Append(Encode(DiagnosisRules.DescribeConclusion(finding.Conclusion))).Append("</td><td>")
                    .Append(Encode(DiagnosisRules.DescribeStatus(finding.Status))).Append("</td><td>")
                    .Append(Encode(finding.Reason));
                foreach (var evidence in finding.Evidence)
                {
                    builder.Append("<div class=\"evidence\">").Append(Encode(evidence.Label)).Append("：")
                        .Append(Encode(string.IsNullOrWhiteSpace(evidence.Value) ? "（无）" : evidence.Value!)).Append("</div>");
                }

                builder.AppendLine("</td></tr>");
            }
        }

        builder.AppendLine("</tbody></table>");
        builder.AppendLine("</section>");
    }

    /// <summary>生成“执行动作清单”HTML 段。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="record">处理记录。</param>
    private static void BuildHtmlActions(StringBuilder builder, HandlingRecord record)
    {
        builder.Append("<section><h2>执行动作清单（").Append(record.Actions.Count).AppendLine(" 项）</h2>");
        builder.AppendLine("<table><thead><tr><th>动作</th><th>风险</th><th>结果</th><th>耗时(秒)</th><th>输出摘要</th></tr></thead><tbody>");
        if (record.Actions.Count == 0)
        {
            builder.AppendLine("<tr><td colspan=\"5\" class=\"muted\">本次未执行修复动作。</td></tr>");
        }
        else
        {
            foreach (var action in record.Actions)
            {
                builder.Append("<tr><td>").Append(Encode($"{action.ActionName}（{action.ActionId}）"))
                    .Append("</td><td>").Append(Encode(ActionRules.DescribeRisk(action.Risk)))
                    .Append("</td><td>").Append(Encode(ActionRules.DescribeOutcome(action.Outcome)))
                    .Append("</td><td>").Append(action.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(Encode(OneLine(action.OutputSummary))).AppendLine("</td></tr>");
            }
        }

        builder.AppendLine("</tbody></table></section>");
    }

    /// <summary>生成“自定义命令清单”HTML 段（M9 使用，本步通常为空）。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="record">处理记录。</param>
    private static void BuildHtmlCustomCommands(StringBuilder builder, HandlingRecord record)
    {
        builder.Append("<section><h2>自定义命令清单（").Append(record.CustomCommands.Count).AppendLine(" 项）</h2>");
        builder.AppendLine("<table><thead><tr><th>命令</th><th>执行身份</th><th>退出码</th><th>耗时(秒)</th><th>输出摘要</th></tr></thead><tbody>");
        if (record.CustomCommands.Count == 0)
        {
            builder.AppendLine("<tr><td colspan=\"5\" class=\"muted\">本次未使用自定义命令。</td></tr>");
        }
        else
        {
            foreach (var command in record.CustomCommands)
            {
                builder.Append("<tr><td>").Append(Encode(command.CommandText))
                    .Append("</td><td>").Append(Encode(command.ExecutedAs))
                    .Append("</td><td>").Append(command.ExitCode.ToString(CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(command.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(Encode(OneLine(command.OutputSummary))).AppendLine("</td></tr>");
            }
        }

        builder.AppendLine("</tbody></table></section>");
    }

    /// <summary>生成“处理后系统持久化变更”HTML 段（M9：自定义命令执行前后对比服务 / 计划任务 / 自启动项）。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="record">处理记录。</param>
    private static void BuildHtmlPersistence(StringBuilder builder, HandlingRecord record)
    {
        builder.Append("<section><h2>处理后系统持久化变更（").Append(record.PersistenceChanges.Count).AppendLine(" 类）</h2>");
        if (record.PersistenceChanges.Count == 0)
        {
            builder.AppendLine("<p class=\"muted\">本次未检测到持久化变更（未使用自定义命令或前后无差异）。</p>");
        }
        else
        {
            builder.AppendLine("<table><thead><tr><th>类型</th><th>变更</th><th>明细</th></tr></thead><tbody>");
            foreach (var change in record.PersistenceChanges)
            {
                builder.Append("<tr><td>").Append(Encode(CustomCommandRules.DescribePersistenceKind(change.Kind)))
                    .Append("</td><td>").Append(Encode(DescribePersistenceChange(change)))
                    .Append("</td><td>").Append(Encode(DescribePersistenceDetail(change))).AppendLine("</td></tr>");
            }

            builder.AppendLine("</tbody></table>");
        }

        builder.AppendLine("</section>");
    }

    /// <summary>生成“结论与原因”HTML 段。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="record">处理记录。</param>
    private static void BuildHtmlConclusion(StringBuilder builder, HandlingRecord record)
    {
        builder.AppendLine("<section><h2>结论与原因</h2>");
        builder.Append("<p>结论：<b>").Append(Encode(LedgerRules.DescribeConclusion(record.Conclusion))).AppendLine("</b></p>");
        if (record.Conclusion == SessionConclusion.Unresolved)
        {
            builder.Append("<p>未解决原因：").Append(Encode(record.UnresolvedReason is null
                ? "（未填写）"
                : LedgerRules.DescribeUnresolvedReason(record.UnresolvedReason.Value))).AppendLine("</p>");
            builder.Append("<p>未解决说明：").Append(Encode(string.IsNullOrWhiteSpace(record.UnresolvedNote) ? "（无）" : record.UnresolvedNote)).AppendLine("</p>");
        }

        builder.AppendLine("</section>");
    }

    /// <summary>生成“补充说明”HTML 段。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="record">处理记录。</param>
    private static void BuildHtmlSupplements(StringBuilder builder, HandlingRecord record)
    {
        builder.Append("<section><h2>补充说明（").Append(record.Supplements.Count).AppendLine(" 条）</h2>");
        if (record.Supplements.Count == 0)
        {
            builder.AppendLine("<p class=\"muted\">暂无补充说明。</p>");
        }
        else
        {
            builder.AppendLine("<table><thead><tr><th>时间</th><th>追加人</th><th>说明</th></tr></thead><tbody>");
            foreach (var supplement in record.Supplements)
            {
                builder.Append("<tr><td>").Append(supplement.AddedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(Encode(supplement.Operator.DisplayText))
                    .Append("</td><td>").Append(Encode(OneLine(supplement.Text))).AppendLine("</td></tr>");
            }

            builder.AppendLine("</tbody></table>");
        }

        builder.AppendLine("</section>");
    }

    /// <summary>把一类持久化变更概括为“新增 N 项 / 移除 N 项”或“未知”。</summary>
    /// <param name="change">持久化变更。</param>
    /// <returns>概括文本。</returns>
    private static string DescribePersistenceChange(PersistenceChange change)
        => change.IsUnknown
            ? "未知"
            : $"新增 {change.Added.Count} 项 / 移除 {change.Removed.Count} 项";

    /// <summary>列出持久化变更的明细（新增与移除的项名）。</summary>
    /// <param name="change">持久化变更。</param>
    /// <returns>明细文本。</returns>
    private static string DescribePersistenceDetail(PersistenceChange change)
    {
        if (change.IsUnknown)
        {
            return string.IsNullOrWhiteSpace(change.Note) ? "（采集失败，无法判定）" : change.Note;
        }

        if (!change.HasChanges)
        {
            return "（无变化）";
        }

        var parts = new List<string>();
        if (change.Added.Count > 0)
        {
            parts.Add("新增：" + string.Join("、", change.Added));
        }

        if (change.Removed.Count > 0)
        {
            parts.Add("移除：" + string.Join("、", change.Removed));
        }

        return string.Join("；", parts);
    }

    /// <summary>追加一个封面的“名称 / 取值”元数据项。</summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="name">名称。</param>
    /// <param name="value">取值。</param>
    private static void AppendMeta(StringBuilder builder, string name, string value)
        => builder.Append("<div>").Append(Encode(name)).Append("：<b>").Append(Encode(value)).AppendLine("</b></div>");

    /// <summary>结论对应的封面徽标样式类。</summary>
    /// <param name="conclusion">会话结论。</param>
    /// <returns>CSS 类名。</returns>
    private static string ConclusionClass(SessionConclusion conclusion) => conclusion switch
    {
        SessionConclusion.Resolved => "ok",
        SessionConclusion.PendingRestart => "pending",
        SessionConclusion.Unresolved => "bad",
        _ => "interrupt"
    };

    /// <summary>把多行文本压成单行，便于在表格中展示。</summary>
    /// <param name="text">原始文本。</param>
    /// <returns>以空格替换换行后的单行文本。</returns>
    private static string OneLine(string text)
        => text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();

    /// <summary>HTML 转义，避免记录内容破坏报告结构。</summary>
    /// <param name="text">原始文本；可能为 null。</param>
    /// <returns>转义后的文本。</returns>
    private static string Encode(string? text)
        => (text ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");

    /// <summary>Markdown 表格单元格转义（竖线与换行）。</summary>
    /// <param name="text">原始文本；可能为 null。</param>
    /// <returns>转义后的单元格文本。</returns>
    private static string EscapeCell(string? text)
        => OneLine(text ?? string.Empty).Replace("|", "\\|");

    /// <summary>CSV 单元格转义（含逗号、引号或换行时以双引号包裹并转义引号）。</summary>
    /// <param name="text">原始文本；可能为 null。</param>
    /// <returns>转义后的单元格文本。</returns>
    private static string CsvCell(string? text)
    {
        var value = text ?? string.Empty;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}