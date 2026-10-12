using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>
/// M9 受控自定义命令与动作沉淀纯规则（CustomCommandRules）的单元测试：
/// 入口可用性判定、命令与输出脱敏截断、结果归类、命令标识、持久化差异对比、
/// 沉淀校验、动作定义构建与沉淀脚本、文件名与来源说明。
/// </summary>
public class CustomCommandRulesTests
{
    // ═══════════════ 入口可用性判定 ═══════════════

    /// <summary>四项条件全部满足时入口可用，且无不可用原因。</summary>
    [Fact]
    public void EvaluateAvailability_全部满足时可用()
    {
        var result = CustomCommandRules.EvaluateAvailability(
            isAdministrator: true,
            advancedModeEnabled: true,
            mediaUnlocked: true,
            writable: true,
            canStart: true);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Reason);
    }

    /// <summary>任一条件不满足即不可用，且给出对应中文原因；判定顺序为管理员 → 启动自检 → 解锁 → 可写 → 高级模式。</summary>
    [Theory]
    [InlineData(false, true, true, true, true, "仅管理员模式可用")]
    [InlineData(true, true, true, true, false, "启动自检未通过")]
    [InlineData(true, true, false, true, true, "介质尚未解锁")]
    [InlineData(true, true, true, false, true, "介质不可写")]
    [InlineData(true, false, true, true, true, "高级模式未开启")]
    public void EvaluateAvailability_条件不满足时禁用并给出原因(
        bool isAdministrator,
        bool advancedModeEnabled,
        bool mediaUnlocked,
        bool writable,
        bool canStart,
        string expectedKeyword)
    {
        var result = CustomCommandRules.EvaluateAvailability(
            isAdministrator, advancedModeEnabled, mediaUnlocked, writable, canStart);

        Assert.False(result.IsAvailable);
        Assert.NotNull(result.Reason);
        Assert.Contains(expectedKeyword, result.Reason);
    }

    /// <summary>判定顺序：非管理员且其余全不满足时，优先报告“仅管理员模式可用”。</summary>
    [Fact]
    public void EvaluateAvailability_非管理员优先报告()
    {
        var result = CustomCommandRules.EvaluateAvailability(false, false, false, false, false);
        Assert.Contains("仅管理员模式可用", result.Reason);
    }

    // ═══════════════ 命令与输出脱敏、截断 ═══════════════

    /// <summary>命令原文中的凭据类参数值被替换为掩码，并去除首尾空白。</summary>
    [Fact]
    public void MaskCommand_脱敏并去除首尾空白()
    {
        var masked = CustomCommandRules.MaskCommand("  net use \\\\host\\share /user:admin password=P@ssw0rd  ");
        Assert.DoesNotContain("P@ssw0rd", masked);
        Assert.Contains(LedgerRules.CredentialMask, masked);
        Assert.Equal(masked, masked.Trim());
    }

    /// <summary>空白命令返回空串。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskCommand_空白返回空串(string? command)
        => Assert.Equal(string.Empty, CustomCommandRules.MaskCommand(command));

    /// <summary>输出为空或空白时摘要为空串。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void SummarizeOutput_空白输出返回空串(string? text)
        => Assert.Equal(string.Empty, CustomCommandRules.SummarizeOutput(text));

    /// <summary>输出行数超过上限时按行截断，并追加截断说明（含总行数）。</summary>
    [Fact]
    public void SummarizeOutput_按行截断()
    {
        var text = string.Join("\n", Enumerable.Range(1, 60).Select(index => $"line-{index}"));
        var summary = CustomCommandRules.SummarizeOutput(text);

        Assert.Contains("输出已截断，共 60 行", summary);
        Assert.Contains("line-40", summary);
        Assert.DoesNotContain("line-41", summary);
    }

    /// <summary>单行超长输出按字符数上限截断，并追加截断说明。</summary>
    [Fact]
    public void SummarizeOutput_按字符截断()
    {
        var text = new string('x', 5000);
        var summary = CustomCommandRules.SummarizeOutput(text);

        Assert.Contains("输出已截断，共 1 行", summary);
        Assert.True(summary.Length < 5000);
    }

    // ═══════════════ 结果归类 ═══════════════

    /// <summary>结果归类：取消优先于超时，超时优先于退出码；非零退出码为失败，零退出码为成功（自定义命令无“需重启”语义）。</summary>
    [Theory]
    [InlineData(true, true, 5, ActionOutcome.Cancelled)]
    [InlineData(false, true, 5, ActionOutcome.TimedOut)]
    [InlineData(false, false, 1, ActionOutcome.Failed)]
    [InlineData(false, false, 0, ActionOutcome.Success)]
    public void ClassifyOutcome_按优先级归类(bool canceled, bool timedOut, int exitCode, ActionOutcome expected)
        => Assert.Equal(expected, CustomCommandRules.ClassifyOutcome(canceled, timedOut, exitCode));

    // ═══════════════ 命令标识与身份 / 来源说明 ═══════════════

    /// <summary>命令标识形如 cmd-yyyyMMdd-HHmmss-xxxx。</summary>
    [Fact]
    public void BuildCommandId_格式正确()
    {
        var at = new DateTimeOffset(2026, 10, 12, 19, 30, 0, TimeSpan.FromHours(8));
        var id = CustomCommandRules.BuildCommandId(at, "ab12");
        Assert.Equal("cmd-20261012-193000-ab12", id);
    }

    /// <summary>执行身份与来源说明的中文名称。</summary>
    [Fact]
    public void DescribeExecutionAs_与DescribeOrigin_中文名称()
    {
        Assert.Equal("管理员", CustomCommandRules.DescribeExecutionAs(PrivilegeLevel.Administrator));
        Assert.Equal("标准用户", CustomCommandRules.DescribeExecutionAs(PrivilegeLevel.Standard));
        Assert.Equal("内置动作库", CustomCommandRules.DescribeOrigin(null));
        Assert.Equal("内置动作库", CustomCommandRules.DescribeOrigin("   "));
        Assert.Equal(CustomCommandRules.SedimentSource, CustomCommandRules.DescribeOrigin(CustomCommandRules.SedimentSource));
    }

    /// <summary>持久化变更类型的中文名称。</summary>
    [Fact]
    public void DescribePersistenceKind_中文名称()
    {
        Assert.Equal("系统服务", CustomCommandRules.DescribePersistenceKind(PersistenceChangeKind.Service));
        Assert.Equal("计划任务", CustomCommandRules.DescribePersistenceKind(PersistenceChangeKind.ScheduledTask));
        Assert.Equal("开机自启动项", CustomCommandRules.DescribePersistenceKind(PersistenceChangeKind.StartupItem));
    }

    // ═══════════════ 持久化变更差异对比 ═══════════════

    /// <summary>对比前后快照得出新增与移除明细（忽略大小写、排序、去重）。</summary>
    [Fact]
    public void DetectPersistenceChanges_得出新增与移除()
    {
        var before = new PersistenceSnapshot(
            PersistenceChangeKind.Service,
            new[] { "svc-a", "svc-b", "SVC-C" },
            true,
            string.Empty);
        var after = new PersistenceSnapshot(
            PersistenceChangeKind.Service,
            new[] { "svc-a", "svc-b", "svc-d", "svc-c" },
            true,
            string.Empty);

        var changes = CustomCommandRules.DetectPersistenceChanges(before, after);

        Assert.Single(changes);
        var change = changes[0];
        Assert.False(change.IsUnknown);
        Assert.True(change.HasChanges);
        Assert.Equal(new[] { "svc-d" }, change.Added);
        Assert.Empty(change.Removed);
    }

    /// <summary>两侧一致时无变化。</summary>
    [Fact]
    public void DetectPersistenceChanges_无变化()
    {
        var before = new PersistenceSnapshot(PersistenceChangeKind.ScheduledTask, new[] { "\\T1" }, true, string.Empty);
        var after = new PersistenceSnapshot(PersistenceChangeKind.ScheduledTask, new[] { "\\T1" }, true, string.Empty);

        var change = CustomCommandRules.DetectPersistenceChanges(before, after)[0];

        Assert.False(change.IsUnknown);
        Assert.False(change.HasChanges);
    }

    /// <summary>任一侧采集失败时标注“未知”，且不视为变化、新增加移除均为空。</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void DetectPersistenceChanges_采集失败标注未知(bool beforeAvailable, bool afterAvailable)
    {
        var before = new PersistenceSnapshot(
            PersistenceChangeKind.StartupItem,
            beforeAvailable ? new[] { "item" } : Array.Empty<string>(),
            beforeAvailable,
            beforeAvailable ? string.Empty : "拒绝访问");
        var after = new PersistenceSnapshot(
            PersistenceChangeKind.StartupItem,
            afterAvailable ? new[] { "item" } : Array.Empty<string>(),
            afterAvailable,
            afterAvailable ? string.Empty : "拒绝访问");

        var change = CustomCommandRules.DetectPersistenceChanges(before, after)[0];

        Assert.True(change.IsUnknown);
        Assert.False(change.HasChanges);
        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.Contains("未知", change.Note);
    }

    // ═══════════════ 沉淀请求校验 ═══════════════

    /// <summary>构造一个合法的沉淀请求，便于测试。</summary>
    private static ActionSedimentRequest Request(
        string sourceCommand = "Get-Service | Where-Object { $_.Status -eq 'Stopped' }",
        string name = "重启打印后台服务",
        string description = "重启打印机后台处理服务以恢复打印队列。",
        RiskLevel risk = RiskLevel.High,
        bool? requiresRestart = false,
        bool? canRollback = false,
        string rollbackNote = "",
        int timeoutSeconds = 600)
        => new()
        {
            SourceCommand = sourceCommand,
            Name = name,
            Description = description,
            Risk = risk,
            RequiresRestart = requiresRestart,
            CanRollback = canRollback,
            RollbackNote = rollbackNote,
            TimeoutSeconds = timeoutSeconds
        };

    /// <summary>合法请求通过校验。</summary>
    [Fact]
    public void ValidateSediment_合法请求通过()
        => Assert.True(CustomCommandRules.ValidateSediment(Request()).IsValid);

    /// <summary>名称 / 说明 / 源命令必填，风险不得低于“高”，需重启与可回滚必须明确。</summary>
    [Fact]
    public void ValidateSediment_必填与风险校验()
    {
        var invalid = Request(
            sourceCommand: "  ",
            name: "  ",
            description: "  ",
            risk: RiskLevel.Medium,
            requiresRestart: null,
            canRollback: null);

        var validation = CustomCommandRules.ValidateSediment(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Problems, problem => problem.Contains("动作名称必填"));
        Assert.Contains(validation.Problems, problem => problem.Contains("动作说明必填"));
        Assert.Contains(validation.Problems, problem => problem.Contains("源命令不能为空"));
        Assert.Contains(validation.Problems, problem => problem.Contains("风险级别不得低于"));
        Assert.Contains(validation.Problems, problem => problem.Contains("是否需重启"));
        Assert.Contains(validation.Problems, problem => problem.Contains("是否可回滚"));
    }

    /// <summary>源命令含 {{ }} 占位符时拒绝（会被动作库误判为参数占位符）。</summary>
    [Fact]
    public void ValidateSediment_源命令含占位符拒绝()
    {
        var validation = CustomCommandRules.ValidateSediment(
            Request(sourceCommand: "Set-Service {{服务名}} -StartupType Automatic"));
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Problems, problem => problem.Contains("占位符"));
    }

    /// <summary>选择“可回滚”时必须填写回滚说明。</summary>
    [Fact]
    public void ValidateSediment_可回滚必须填写说明()
    {
        var validation = CustomCommandRules.ValidateSediment(Request(canRollback: true, rollbackNote: "  "));
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Problems, problem => problem.Contains("回滚说明"));

        var ok = CustomCommandRules.ValidateSediment(Request(canRollback: true, rollbackNote: "恢复原启动类型为手动"));
        Assert.True(ok.IsValid);
    }

    /// <summary>超时必须为正数秒。</summary>
    [Fact]
    public void ValidateSediment_超时必须为正数()
    {
        var validation = CustomCommandRules.ValidateSediment(Request(timeoutSeconds: 0));
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Problems, problem => problem.Contains("超时"));
    }

    // ═══════════════ 动作定义构建与沉淀脚本 ═══════════════

    /// <summary>构建的动作定义标识形如 custom.001、风险不低于“高”、来源为“自定义沉淀”、需管理员。</summary>
    [Fact]
    public void BuildActionDescriptor_标识与来源()
    {
        var request = Request(risk: RiskLevel.Low);
        var descriptor = CustomCommandRules.BuildActionDescriptor(request, 1);

        Assert.Equal("custom.001", descriptor.ActionId);
        Assert.Equal(RiskLevel.High, descriptor.Risk); // 低于“高”被钳制为“高”
        Assert.Equal(CustomCommandRules.SedimentSource, descriptor.Source);
        Assert.True(descriptor.RequiresAdministrator);
        Assert.Equal(request.Name, descriptor.Name);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Script));
    }

    /// <summary>沉淀脚本包裹源命令并实现统一退出码约定（非零退出码透传、异常记 1、其余记 0）。</summary>
    [Fact]
    public void BuildSedimentScript_退出码约定()
    {
        var script = CustomCommandRules.BuildSedimentScript("Get-Date");

        Assert.Contains("Get-Date", script);
        Assert.Contains("$LASTEXITCODE", script);
        Assert.Contains("exit 0", script);
        Assert.Contains("exit 1", script);
        Assert.Contains("catch", script);
    }

    /// <summary>直接执行与沉淀共用的脚本一致（保证退出码语义一致）。</summary>
    [Fact]
    public void BuildSedimentScript_与动作脚本一致()
    {
        var request = Request(sourceCommand: "Get-Date");
        var descriptor = CustomCommandRules.BuildActionDescriptor(request, 3);
        Assert.Equal(CustomCommandRules.BuildSedimentScript(request.SourceCommand), descriptor.Script);
    }

    // ═══════════════ 沉淀文件名 ═══════════════

    /// <summary>文件名由名称的 ASCII 片段与序号构成，保证唯一（避免覆盖既有沉淀文件）。</summary>
    [Fact]
    public void BuildSedimentFileName_含Slug与序号()
    {
        Assert.Equal("custom-restart-spooler-001.json", CustomCommandRules.BuildSedimentFileName("Restart Spooler", 1));
        Assert.Equal("custom-restart-spooler-002.json", CustomCommandRules.BuildSedimentFileName("Restart Spooler", 2));
    }

    /// <summary>名称不含 ASCII 字母数字时回退为 action。</summary>
    [Fact]
    public void BuildSedimentFileName_中文名回退()
        => Assert.Equal("custom-action-007.json", CustomCommandRules.BuildSedimentFileName("重启打印服务", 7));
}