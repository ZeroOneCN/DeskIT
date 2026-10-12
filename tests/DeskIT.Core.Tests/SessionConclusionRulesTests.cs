using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>会话结论推导规则（SessionConclusionRules）的单元测试。</summary>
public class SessionConclusionRulesTests
{
    /// <summary>全部动作成功（含只读动作成功）时，结论为“已解决”。</summary>
    [Fact]
    public void Derive_全部成功返回已解决()
    {
        Assert.Equal(
            SessionConclusion.Resolved,
            SessionConclusionRules.Derive(new[] { ActionOutcome.Success, ActionOutcome.Success }));
    }

    /// <summary>存在“成功但需重启”时，即使同时存在失败项，结论仍倾向“待重启生效”。</summary>
    [Fact]
    public void Derive_成功但需重启时倾向待重启生效()
    {
        Assert.Equal(
            SessionConclusion.PendingRestart,
            SessionConclusionRules.Derive(new[] { ActionOutcome.Success, ActionOutcome.SuccessPendingRestart }));

        Assert.Equal(
            SessionConclusion.PendingRestart,
            SessionConclusionRules.Derive(new[] { ActionOutcome.SuccessPendingRestart, ActionOutcome.Failed }));
    }

    /// <summary>存在失败或超时且无待重启项时，结论为“未解决”。</summary>
    [Fact]
    public void Derive_存在失败或超时返回未解决()
    {
        Assert.Equal(
            SessionConclusion.Unresolved,
            SessionConclusionRules.Derive(new[] { ActionOutcome.Success, ActionOutcome.Failed }));

        Assert.Equal(
            SessionConclusion.Unresolved,
            SessionConclusionRules.Derive(new[] { ActionOutcome.TimedOut }));
    }

    /// <summary>边界：全部动作被跳过/取消，或没有任何动作时，结论为“未解决”。</summary>
    [Fact]
    public void Derive_全部跳过取消或空输入返回未解决()
    {
        Assert.Equal(
            SessionConclusion.Unresolved,
            SessionConclusionRules.Derive(new[] { ActionOutcome.Skipped, ActionOutcome.Cancelled }));

        Assert.Equal(
            SessionConclusion.Unresolved,
            SessionConclusionRules.Derive(Array.Empty<ActionOutcome>()));
    }
}