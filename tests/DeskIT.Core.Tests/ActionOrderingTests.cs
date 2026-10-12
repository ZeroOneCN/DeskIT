using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>动作排序规则（ActionOrdering）的单元测试。</summary>
public class ActionOrderingTests
{
    /// <summary>构造一个动作描述对象，便于测试。</summary>
    private static ActionDescriptor Action(string name, RiskLevel risk, bool requiresRestart = false)
        => new() { Name = name, Risk = risk, RequiresRestart = requiresRestart };

    /// <summary>混合风险场景：不需重启的动作按风险升序在前，需重启的动作统一排到最后。</summary>
    [Fact]
    public void OrderActions_按风险升序且需重启动作排到最后()
    {
        var input = new[]
        {
            Action("需重启-低", RiskLevel.Low, requiresRestart: true),
            Action("高", RiskLevel.High),
            Action("只读", RiskLevel.ReadOnly),
            Action("不可逆", RiskLevel.Irreversible),
            Action("中", RiskLevel.Medium),
            Action("需重启-不可逆", RiskLevel.Irreversible, requiresRestart: true)
        };

        var ordered = ActionOrdering.OrderActions(input);

        Assert.Equal(
            new[] { "只读", "中", "高", "不可逆", "需重启-低", "需重启-不可逆" },
            ordered.Select(a => a.Name));
    }

    /// <summary>边界：全部动作都需重启时，仍需按风险升序排列。</summary>
    [Fact]
    public void OrderActions_全部需重启时仍按风险升序()
    {
        var input = new[]
        {
            Action("B-高", RiskLevel.High, requiresRestart: true),
            Action("A-低", RiskLevel.Low, requiresRestart: true),
            Action("C-不可逆", RiskLevel.Irreversible, requiresRestart: true)
        };

        var ordered = ActionOrdering.OrderActions(input);

        Assert.Equal(new[] { "A-低", "B-高", "C-不可逆" }, ordered.Select(a => a.Name));
    }
}