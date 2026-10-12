using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>报告生成器（LedgerReportBuilder）的单元测试：自包含 HTML、Markdown 与 CSV 内容。</summary>
public class LedgerReportBuilderTests
{
    /// <summary>固定时区偏移，保证时间断言稳定。</summary>
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    /// <summary>构造一个含诊断结论与动作的处理记录，供报告生成用例使用。</summary>
    /// <param name="desensitizeHint">为 true 时处理人姓名与动作输出含可脱敏内容。</param>
    /// <param name="interrupted">是否以“已中断”结束。</param>
    /// <returns>处理记录。</returns>
    private static HandlingRecord NewRecord(bool desensitizeHint = true, bool interrupted = false)
    {
        var finding = new DiagnosisFinding(
            "net.dns",
            DiagnosisScene.Network,
            "DNS 解析",
            DiagnosisExecutionStatus.Executed,
            DiagnosisConclusion.Abnormal,
            "解析失败",
            new[] { new DiagnosisEvidence("上游服务器", desensitizeHint ? "password=abc" : "10.0.0.1") },
            new[] { "net.reset-dns" },
            TimeSpan.FromSeconds(1));
        var scene = new DiagnosisSceneResult(DiagnosisScene.Network, new[] { finding }, TimeSpan.FromSeconds(1));

        var start = new DateTimeOffset(2026, 10, 12, 19, 0, 0, Offset);
        return new HandlingRecord
        {
            RecordId = "rec-20261012-193000-ab12",
            SessionId = "sess-20261012-193000-ab12",
            MachineId = "SN-ABC-123",
            MachineIdSource = MachineIdSource.HardwareSerial,
            HostName = "PC-1",
            Operator = new OperatorInfo("E1001", "张小明"),
            Category = ProblemCategory.Network,
            Tags = new[] { "打印机", "外设" },
            SymptomDescription = "上不了网,经常掉线",
            Scenes = new[] { scene },
            Actions = new[]
            {
                new LedgerActionEntry("net.reset-dns", "重置 DNS", RiskLevel.Medium, ActionOutcome.Success, 2, desensitizeHint ? "password=secret ok" : "ok")
            },
            Conclusion = interrupted ? SessionConclusion.Interrupted : SessionConclusion.Unresolved,
            UnresolvedReason = interrupted ? null : UnresolvedReason.CannotReproduce,
            UnresolvedNote = "现场未复现",
            Privilege = PrivilegeLevel.Administrator,
            StartedAt = start,
            FinishedAt = start.AddMinutes(20),
            ReportFileName = "SN-ABC-123_20261012-192000_sess-20261012-193000-ab12.html",
            InterruptReason = interrupted ? "现场临时中止" : null
        };
    }

    /// <summary>HTML 报告自包含：无脚本、无外链，含中文结论与脱敏后的姓名。</summary>
    [Fact]
    public void BuildHtml_自包含且脱敏()
    {
        var html = LedgerReportBuilder.BuildHtml(NewRecord(), desensitized: true);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("lang=\"zh-CN\"", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("脱敏版本", html);
        Assert.Contains("张*明", html);
        Assert.DoesNotContain("张小明", html);
        Assert.Contains(LedgerRules.CredentialMask, html);
        Assert.DoesNotContain("secret", html);
        Assert.Contains("DNS 解析", html);
        Assert.Contains("无法复现", html);
    }

    /// <summary>HTML 报告完整版本保留姓名原文。</summary>
    [Fact]
    public void BuildHtml_完整版本保留姓名()
    {
        var html = LedgerReportBuilder.BuildHtml(NewRecord(), desensitized: false);
        Assert.Contains("张小明", html);
        Assert.Contains("完整版本", html);
    }

    /// <summary>“已中断”记录在 HTML 报告中给出显式注意段。</summary>
    [Fact]
    public void BuildHtml_中断记录给出注意段()
    {
        var html = LedgerReportBuilder.BuildHtml(NewRecord(interrupted: true), desensitized: true);
        Assert.Contains("已中断", html);
        Assert.Contains("现场临时中止", html);
    }

    /// <summary>Markdown 报告含章节标题，且脱敏后不含原文姓名。</summary>
    [Fact]
    public void BuildMarkdown_含章节且脱敏()
    {
        var markdown = LedgerReportBuilder.BuildMarkdown(NewRecord(), desensitized: true);

        Assert.Contains("# DeskIT 处理记录报告", markdown);
        Assert.Contains("## 诊断汇总", markdown);
        Assert.Contains("## 执行动作清单", markdown);
        Assert.Contains("张*明", markdown);
        Assert.DoesNotContain("张小明", markdown);
        Assert.Contains(LedgerRules.CredentialMask, markdown);
    }

    /// <summary>CSV 台账：首行表头，逐条一行，含逗号的字段被双引号包裹。</summary>
    [Fact]
    public void BuildCsv_表头与转义()
    {
        var csv = LedgerReportBuilder.BuildCsv(new[] { NewRecord() });
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("记录标识,会话标识,机器标识", lines[0]);
        Assert.Contains("rec-20261012-193000-ab12", lines[1]);
        Assert.Contains("\"上不了网,经常掉线\"", lines[1]);
    }

    /// <summary>CSV 台账：多条记录时行数为记录数加表头。</summary>
    [Fact]
    public void BuildCsv_多条记录逐行输出()
    {
        var csv = LedgerReportBuilder.BuildCsv(new[] { NewRecord(), NewRecord() });
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }
}