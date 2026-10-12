using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>诊断规则（DiagnosisRules）的单元测试：场景选择、异常置顶排序、结论统计、会话标识与可读文本。</summary>
public class DiagnosisRulesTests
{
    /// <summary>场景选择：全场景返回全部 7 个（按展示顺序）；按勾选返回子集。</summary>
    [Fact]
    public void SelectScenes_全场景与勾选子集()
    {
        var all = DiagnosisRules.SelectScenes(allScenes: true, selectedScenes: null);
        Assert.Equal(7, all.Count);
        Assert.Equal(DiagnosisScene.Slowness, all[0]);
        Assert.Equal(DiagnosisScene.AccountPrivilege, all[^1]);

        var selected = DiagnosisRules.SelectScenes(
            allScenes: false,
            new HashSet<DiagnosisScene> { DiagnosisScene.Network, DiagnosisScene.Slowness });
        Assert.Equal(new[] { DiagnosisScene.Slowness, DiagnosisScene.Network }, selected);
    }

    /// <summary>结论排序：异常 → 提示 → 未知 → 正常，且同档内按场景顺序稳定排列。</summary>
    [Fact]
    public void SortFindings_异常置顶且同档内稳定()
    {
        var findings = new List<DiagnosisFinding>
        {
            Finding("d", DiagnosisScene.DiskStorage, DiagnosisConclusion.Normal),
            Finding("a", DiagnosisScene.Network, DiagnosisConclusion.Unknown),
            Finding("b", DiagnosisScene.Network, DiagnosisConclusion.Abnormal),
            Finding("c", DiagnosisScene.Slowness, DiagnosisConclusion.Abnormal),
            Finding("e", DiagnosisScene.Software, DiagnosisConclusion.Hint)
        };

        var sorted = DiagnosisRules.SortFindings(findings);
        Assert.Equal(new[] { "c", "b", "e", "a", "d" }, sorted.Select(f => f.FindingId));
    }

    /// <summary>统计仅计入“已执行”项；不适用与未执行（需管理员）单独计数，不计入异常与未知。</summary>
    [Fact]
    public void BuildSummary_仅统计已执行项()
    {
        var scenes = new[]
        {
            new DiagnosisSceneResult(DiagnosisScene.Slowness, new List<DiagnosisFinding>
            {
                Finding("a", DiagnosisScene.Slowness, DiagnosisConclusion.Abnormal),
                Finding("b", DiagnosisScene.Slowness, DiagnosisConclusion.Normal),
                Skipped("c", DiagnosisScene.Slowness)
            }, TimeSpan.FromSeconds(1)),
            new DiagnosisSceneResult(DiagnosisScene.Network, new List<DiagnosisFinding>
            {
                Finding("d", DiagnosisScene.Network, DiagnosisConclusion.Hint),
                NotApplicable("e", DiagnosisScene.Network)
            }, TimeSpan.FromSeconds(1))
        };

        var summary = DiagnosisRules.BuildSummary(scenes);
        Assert.Equal(1, summary.AbnormalCount);
        Assert.Equal(1, summary.HintCount);
        Assert.Equal(0, summary.UnknownCount);
        Assert.Equal(1, summary.NormalCount);
        Assert.Equal(1, summary.NotApplicableCount);
        Assert.Equal(1, summary.SkippedNeedsAdministratorCount);
        Assert.Equal(5, summary.TotalCount);
    }

    /// <summary>会话标识格式正确，且同一时间多次生成仍唯一。</summary>
    [Fact]
    public void NewSessionId_格式与唯一性()
    {
        var at = new DateTimeOffset(2026, 10, 12, 19, 30, 0, TimeSpan.FromHours(8));
        var id = DiagnosisRules.NewSessionId(at, "ab12");
        Assert.Equal("sess-20261012-193000-ab12", id);

        var first = DiagnosisRules.NewSessionId(at);
        var second = DiagnosisRules.NewSessionId(at);
        Assert.NotEqual(first, second);
    }

    /// <summary>四态与执行状态的中文文本符合约定。</summary>
    [Fact]
    public void Describe_文案符合约定()
    {
        Assert.Equal("正常", DiagnosisRules.DescribeConclusion(DiagnosisConclusion.Normal));
        Assert.Equal("异常", DiagnosisRules.DescribeConclusion(DiagnosisConclusion.Abnormal));
        Assert.Equal("提示", DiagnosisRules.DescribeConclusion(DiagnosisConclusion.Hint));
        Assert.Equal("未知", DiagnosisRules.DescribeConclusion(DiagnosisConclusion.Unknown));

        Assert.Equal("未执行（需要管理员权限）", DiagnosisRules.DescribeStatus(DiagnosisExecutionStatus.SkippedNeedsAdministrator));
        Assert.Equal("不适用", DiagnosisRules.DescribeStatus(DiagnosisExecutionStatus.NotApplicable));
        Assert.Equal("已执行", DiagnosisRules.DescribeStatus(DiagnosisExecutionStatus.Executed));

        Assert.Equal("卡慢", DiagnosisRules.DescribeScene(DiagnosisScene.Slowness));
        Assert.Equal("账号与权限", DiagnosisRules.DescribeScene(DiagnosisScene.AccountPrivilege));
    }

    /// <summary>诊断会话可经 System.Text.Json 往返序列化（结论与依据需一并写入 U 盘）。</summary>
    [Fact]
    public void DiagnosisSession_可序列化往返()
    {
        var session = new DiagnosisSession(
            "sess-20261012-193000-ab12",
            "WO-4|20261012125821",
            "WO-4",
            SessionState.Closed,
            new DateTimeOffset(2026, 10, 12, 19, 30, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 10, 12, 19, 31, 0, TimeSpan.FromHours(8)),
            new[]
            {
                new DiagnosisSceneResult(DiagnosisScene.Network, new List<DiagnosisFinding>
                {
                    new(
                        "network.gateway",
                        DiagnosisScene.Network,
                        "网关连通性",
                        DiagnosisExecutionStatus.Executed,
                        DiagnosisConclusion.Hint,
                        "默认网关未响应 ping",
                        new List<DiagnosisEvidence> { new("默认网关", "192.168.1.1") },
                        new List<string> { "network.reset-stack" },
                        TimeSpan.FromSeconds(2))
                }, TimeSpan.FromSeconds(3))
            });

        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };

        var json = JsonSerializer.Serialize(session, options);
        var restored = JsonSerializer.Deserialize<DiagnosisSession>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(session.SessionId, restored!.SessionId);
        Assert.Equal(SessionState.Closed, restored.State);
        var finding = restored.Scenes[0].Findings[0];
        Assert.Equal(DiagnosisConclusion.Hint, finding.Conclusion);
        Assert.Equal(DiagnosisExecutionStatus.Executed, finding.Status);
        Assert.Equal("192.168.1.1", finding.Evidence[0].Value);
        Assert.Equal(new[] { "network.reset-stack" }, finding.SuggestedActionIds);
    }

    /// <summary>构造一条“已执行”的结论。</summary>
    /// <param name="id">结论标识。</param>
    /// <param name="scene">场景。</param>
    /// <param name="conclusion">四态结论。</param>
    /// <returns>结论。</returns>
    private static DiagnosisFinding Finding(string id, DiagnosisScene scene, DiagnosisConclusion conclusion)
        => new(
            id,
            scene,
            id,
            DiagnosisExecutionStatus.Executed,
            conclusion,
            "依据说明",
            Array.Empty<DiagnosisEvidence>(),
            Array.Empty<string>(),
            TimeSpan.FromSeconds(1));

    /// <summary>构造一条“未执行（需要管理员权限）”的结论。</summary>
    /// <param name="id">结论标识。</param>
    /// <param name="scene">场景。</param>
    /// <returns>结论。</returns>
    private static DiagnosisFinding Skipped(string id, DiagnosisScene scene)
        => new(
            id,
            scene,
            id,
            DiagnosisExecutionStatus.SkippedNeedsAdministrator,
            DiagnosisConclusion.Unknown,
            "未执行（需要管理员权限）",
            Array.Empty<DiagnosisEvidence>(),
            Array.Empty<string>(),
            TimeSpan.Zero);

    /// <summary>构造一条“不适用”的结论。</summary>
    /// <param name="id">结论标识。</param>
    /// <param name="scene">场景。</param>
    /// <returns>结论。</returns>
    private static DiagnosisFinding NotApplicable(string id, DiagnosisScene scene)
        => new(
            id,
            scene,
            id,
            DiagnosisExecutionStatus.NotApplicable,
            DiagnosisConclusion.Unknown,
            "不适用",
            Array.Empty<DiagnosisEvidence>(),
            Array.Empty<string>(),
            TimeSpan.Zero);
}