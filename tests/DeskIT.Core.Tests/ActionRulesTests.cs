using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>修复动作纯规则（ActionRules）的单元测试：确认分级、结果归类、参数绑定与前置条件判定。</summary>
public class ActionRulesTests
{
    /// <summary>构造一个动作定义，便于测试。</summary>
    private static ActionDescriptor Action(
        RiskLevel risk,
        bool requiresRestart = false,
        bool requiresAdministrator = true,
        string? script = null,
        IReadOnlyList<ActionParameter>? parameters = null,
        IReadOnlyList<ActionPrecondition>? preconditions = null)
        => new()
        {
            ActionId = "test.action",
            Name = "测试动作",
            Risk = risk,
            RequiresRestart = requiresRestart,
            RequiresAdministrator = requiresAdministrator,
            Script = script,
            Parameters = parameters ?? Array.Empty<ActionParameter>(),
            Preconditions = preconditions ?? Array.Empty<ActionPrecondition>()
        };

    /// <summary>风险分级决定确认方式：不可逆=确认词、高=逐项、中=汇总、低=仅汇总明示。</summary>
    [Theory]
    [InlineData(RiskLevel.Irreversible, ActionConfirmationKind.ConfirmWord)]
    [InlineData(RiskLevel.High, ActionConfirmationKind.PerAction)]
    [InlineData(RiskLevel.Medium, ActionConfirmationKind.BatchSummary)]
    [InlineData(RiskLevel.Low, ActionConfirmationKind.None)]
    [InlineData(RiskLevel.ReadOnly, ActionConfirmationKind.None)]
    public void ConfirmationFor_按风险级别返回确认方式(RiskLevel risk, ActionConfirmationKind expected)
        => Assert.Equal(expected, ActionRules.ConfirmationFor(Action(risk)));

    /// <summary>高风险与不可逆动作的标识。</summary>
    [Fact]
    public void 高风险与不可逆标识()
    {
        Assert.True(Action(RiskLevel.High).IsHighRisk);
        Assert.True(Action(RiskLevel.Irreversible).IsHighRisk);
        Assert.True(Action(RiskLevel.Irreversible).IsIrreversible);
        Assert.False(Action(RiskLevel.Medium).IsHighRisk);
        Assert.False(Action(RiskLevel.High).IsIrreversible);
    }

    /// <summary>只读诊断模式下，需管理员的动作全部禁用；无需管理员（只读级）的动作仍可用。</summary>
    [Fact]
    public void CanRunByPrivilege_只读模式禁用需管理员动作()
    {
        Assert.False(ActionRules.CanRunByPrivilege(Action(RiskLevel.Low), PrivilegeLevel.Standard));
        Assert.True(ActionRules.CanRunByPrivilege(Action(RiskLevel.Low), PrivilegeLevel.Administrator));
        Assert.True(ActionRules.CanRunByPrivilege(Action(RiskLevel.Low, requiresAdministrator: false), PrivilegeLevel.Standard));
    }

    /// <summary>结果归类：取消 > 超时 > 非零退出码失败 > 成功（需重启 / 已生效）。</summary>
    [Theory]
    [InlineData(false, false, 0, false, ActionOutcome.Success)]
    [InlineData(false, false, 0, true, ActionOutcome.SuccessPendingRestart)]
    [InlineData(false, false, 1, false, ActionOutcome.Failed)]
    [InlineData(false, true, 0, false, ActionOutcome.TimedOut)]
    [InlineData(true, true, 0, false, ActionOutcome.Cancelled)]
    public void ClassifyOutcome_按优先级归类(bool canceled, bool timedOut, int exitCode, bool requiresRestart, ActionOutcome expected)
        => Assert.Equal(expected, ActionRules.ClassifyOutcome(canceled, timedOut, exitCode, requiresRestart));

    /// <summary>参数绑定：占位符替换为双引号字面量，且转义反引号、双引号与美元符号。</summary>
    [Fact]
    public void BindParameters_替换为转义字面量()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProcessName"] = "no\"te$pad`"
        };

        var bound = ActionRules.BindParameters("Stop-Process -Name {{ProcessName}}", values);

        Assert.Equal("Stop-Process -Name \"no`\"te`$pad``\"", bound);
    }

    /// <summary>参数绑定：未提供取值的占位符保持原样。</summary>
    [Fact]
    public void BindParameters_未提供取值时保持占位符()
    {
        var bound = ActionRules.BindParameters("Get-Item {{Path}}", new Dictionary<string, string>());
        Assert.Equal("Get-Item {{Path}}", bound);
    }

    /// <summary>必填参数缺失时能被检出；有默认值的参数不算缺失。</summary>
    [Fact]
    public void MissingParameters_检出必填项缺失()
    {
        var action = Action(
            RiskLevel.Medium,
            parameters: new[]
            {
                new ActionParameter("Printer", "打印机名称"),
                new ActionParameter("Drive", "盘符", "C:\\")
            });

        var missing = ActionRules.MissingParameters(action, new Dictionary<string, string>());
        Assert.Equal(new[] { "Printer" }, missing);

        var provided = ActionRules.MissingParameters(
            action,
            new Dictionary<string, string> { ["Printer"] = "HP" });
        Assert.Empty(provided);
    }

    /// <summary>提取脚本占位符：去重并保持首次出现顺序。</summary>
    [Fact]
    public void ExtractTokens_去重且保持顺序()
    {
        var tokens = ActionRules.ExtractTokens("a {{P1}} b {{P2}} c {{P1}} {{BackupFile}}");
        Assert.Equal(new[] { "P1", "P2", "BackupFile" }, tokens);
    }

    /// <summary>未声明的占位符会被检出，用于动作定义加载校验。</summary>
    [Fact]
    public void UnresolvedTokens_检出未声明占位符()
    {
        var action = Action(
            RiskLevel.Low,
            script: "Get-Item {{Path}} | Set-Content {{BackupFile}}",
            parameters: new[] { new ActionParameter("Path", "路径") });

        // Path 已声明、BackupFile 为逻辑参数，均应有来源。
        Assert.Empty(ActionRules.UnresolvedTokens(action));

        var bad = Action(
            RiskLevel.Low,
            script: "Get-Item {{Unknown}}",
            parameters: new[] { new ActionParameter("Path", "路径") });
        Assert.Equal(new[] { "Unknown" }, ActionRules.UnresolvedTokens(bad));
    }

    /// <summary>前置条件判定委托给探测函数：只要有一个未满足即计入未满足清单。</summary>
    [Fact]
    public void UnmetPreconditions_委托探测函数()
    {
        var action = Action(
            RiskLevel.Low,
            preconditions: new[]
            {
                new ActionPrecondition(ActionPreconditionKind.PathExists, "C:\\Temp", "临时目录不存在"),
                new ActionPrecondition(ActionPreconditionKind.ServiceExists, "Spooler", "打印后台处理服务不存在")
            });

        var unmet = ActionRules.UnmetPreconditions(
            action,
            precondition => precondition.Kind == ActionPreconditionKind.PathExists);

        Assert.Single(unmet);
        Assert.Equal(ActionPreconditionKind.ServiceExists, unmet[0].Kind);
    }
}