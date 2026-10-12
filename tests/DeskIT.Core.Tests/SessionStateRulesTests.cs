using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>会话状态机规则（SessionStateRules）的单元测试：合法迁移、终态、结论映射与可读文本。</summary>
public class SessionStateRulesTests
{
    /// <summary>主流程迁移合法：新建 → 采集中 → 诊断中 → 执行动作中 → 已结束。</summary>
    [Fact]
    public void CanTransition_主流程合法()
    {
        Assert.True(SessionStateRules.CanTransition(SessionState.New, SessionState.Collecting));
        Assert.True(SessionStateRules.CanTransition(SessionState.Collecting, SessionState.Diagnosing));
        Assert.True(SessionStateRules.CanTransition(SessionState.Diagnosing, SessionState.Acting));
        Assert.True(SessionStateRules.CanTransition(SessionState.Acting, SessionState.Closed));
    }

    /// <summary>不允许跨越式迁移：新建不能直接进入执行动作中。</summary>
    [Fact]
    public void CanTransition_不允许跨越式迁移()
    {
        Assert.False(SessionStateRules.CanTransition(SessionState.New, SessionState.Acting));
        Assert.False(SessionStateRules.CanTransition(SessionState.Collecting, SessionState.Acting));
    }

    /// <summary>任意非终态均可被中断（如介质移除）。</summary>
    [Fact]
    public void CanTransition_非终态均可中断()
    {
        Assert.True(SessionStateRules.CanTransition(SessionState.New, SessionState.Interrupted));
        Assert.True(SessionStateRules.CanTransition(SessionState.Collecting, SessionState.Interrupted));
        Assert.True(SessionStateRules.CanTransition(SessionState.Diagnosing, SessionState.Interrupted));
        Assert.True(SessionStateRules.CanTransition(SessionState.Acting, SessionState.Interrupted));
    }

    /// <summary>终态不可再迁移；非终态允许重复进入自身（幂等）。</summary>
    [Fact]
    public void CanTransition_终态封闭且非终态幂等()
    {
        Assert.True(SessionStateRules.IsTerminal(SessionState.Closed));
        Assert.True(SessionStateRules.IsTerminal(SessionState.Resolved));
        Assert.True(SessionStateRules.IsTerminal(SessionState.PendingRestart));
        Assert.True(SessionStateRules.IsTerminal(SessionState.Unresolved));
        Assert.True(SessionStateRules.IsTerminal(SessionState.Interrupted));

        Assert.False(SessionStateRules.CanTransition(SessionState.Closed, SessionState.Diagnosing));
        Assert.False(SessionStateRules.CanTransition(SessionState.Resolved, SessionState.Acting));

        Assert.True(SessionStateRules.CanTransition(SessionState.Diagnosing, SessionState.Diagnosing));
        Assert.False(SessionStateRules.CanTransition(SessionState.New, SessionState.New));
    }

    /// <summary>允许的下一状态列表与迁移判定一致，且终态返回空列表。</summary>
    [Fact]
    public void AllowedNext_与迁移判定一致()
    {
        var next = SessionStateRules.AllowedNext(SessionState.New);
        Assert.Contains(SessionState.Collecting, next);
        Assert.Contains(SessionState.Diagnosing, next);
        Assert.DoesNotContain(SessionState.Acting, next);

        Assert.Empty(SessionStateRules.AllowedNext(SessionState.Closed));
    }

    /// <summary>会话结论倾向映射到对应终态。</summary>
    [Fact]
    public void ToSessionState_结论映射()
    {
        Assert.Equal(SessionState.Resolved, SessionStateRules.ToSessionState(SessionConclusion.Resolved));
        Assert.Equal(SessionState.PendingRestart, SessionStateRules.ToSessionState(SessionConclusion.PendingRestart));
        Assert.Equal(SessionState.Unresolved, SessionStateRules.ToSessionState(SessionConclusion.Unresolved));
        Assert.Equal(SessionState.Interrupted, SessionStateRules.ToSessionState(SessionConclusion.Interrupted));
    }

    /// <summary>状态中文名称符合约定。</summary>
    [Fact]
    public void Describe_文案符合约定()
    {
        Assert.Equal("诊断中", SessionStateRules.Describe(SessionState.Diagnosing));
        Assert.Equal("已中断", SessionStateRules.Describe(SessionState.Interrupted));
        Assert.Equal("待重启生效", SessionStateRules.Describe(SessionState.PendingRestart));
    }
}