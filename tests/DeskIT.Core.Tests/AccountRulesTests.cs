using System.Collections.Generic;
using DeskIT.Core;

namespace DeskIT.Core.Tests;

/// <summary>M6 账号与权限纯规则测试：账号状态文案与统计、密码策略解析、UAC 可读化。</summary>
public sealed class AccountRulesTests
{
    [Fact]
    public void 账号状态文案覆盖停用锁定与永不过期()
    {
        var account = new LocalAccountInfo("zhangsan", "张三", false, true, false, "2025-01-01", null, "本地");
        var text = AccountRules.DescribeStatus(account);

        Assert.Contains("已停用", text);
        Assert.Contains("已锁定", text);
        Assert.Contains("密码永不过期", text);
        Assert.Contains("从未登录", text);
    }

    [Fact]
    public void 最近登录为空即视为从未登录()
    {
        Assert.True(AccountRules.IsNeverLoggedOn(new LocalAccountInfo("a", null, true, false, true, null, null, null)));
        Assert.False(AccountRules.IsNeverLoggedOn(new LocalAccountInfo("a", null, true, false, true, null, "2026-01-01", null)));
    }

    [Fact]
    public void 账号统计与摘要()
    {
        var accounts = new List<LocalAccountInfo>
        {
            new("a", null, true, false, true, null, "2026-01-01", null),
            new("b", null, false, false, true, null, "2026-01-01", null),
            new("c", null, true, true, true, null, "2026-01-01", null),
            new("d", null, true, false, true, null, null, null)
        };

        var summary = AccountRules.Summarize(accounts);

        Assert.Equal(4, summary.Total);
        Assert.Equal(3, summary.Enabled);
        Assert.Equal(1, summary.Disabled);
        Assert.Equal(1, summary.Locked);
        Assert.Equal(1, summary.NeverLoggedOn);
        Assert.True(summary.HasAttention);
        Assert.Contains("共 4 个本地账号", AccountRules.DescribeSummary(summary));
    }

    [Fact]
    public void 无异常账号时不需要关注()
    {
        var accounts = new List<LocalAccountInfo>
        {
            new("a", null, true, false, true, null, "2026-01-01", null)
        };

        Assert.False(AccountRules.Summarize(accounts).HasAttention);
    }

    [Fact]
    public void 解析中文net_accounts输出()
    {
        const string output = """
            强制用户在时间到期之后多久必须注销?:   从不
            密码最短使用期限(天):                   0
            密码最长使用期限(天):                   42
            密码长度最小值:                         8
            保持的密码历史记录长度:                  None
            锁定阈值:                               5
            锁定持续时间(分钟):                     30
            锁定观察窗口(分钟):                     30
            计算机角色:                             WORKSTATION
            """;

        var policy = AccountRules.ParsePasswordPolicy(output);

        Assert.Equal(8, policy.MinPasswordLength);
        Assert.Equal(42, policy.MaxPasswordAgeDays);
        Assert.Equal(0, policy.MinPasswordAgeDays);
        Assert.Equal(5, policy.LockoutThreshold);
        Assert.Equal(30, policy.LockoutDurationMinutes);
        Assert.Equal(30, policy.LockoutObservationWindowMinutes);
    }

    [Fact]
    public void 解析英文net_accounts输出并把从不记为未限制()
    {
        const string output = """
            Force user logoff how long after time expires?: Never
            Minimum password age (days): 0
            Maximum password age (days): Never
            Minimum password length: 0
            Length of password history maintained: None
            Lockout threshold: Never
            Lockout duration (minutes): 30
            Lockout observation window (minutes): 30
            """;

        var policy = AccountRules.ParsePasswordPolicy(output);

        Assert.Equal(0, policy.MinPasswordLength);
        Assert.Null(policy.MaxPasswordAgeDays);
        Assert.Null(policy.LockoutThreshold);
        Assert.Equal(30, policy.LockoutDurationMinutes);
        Assert.Contains("从不锁定", AccountRules.DescribePasswordPolicy(policy));
    }

    [Fact]
    public void 空输出返回全空策略()
    {
        var policy = AccountRules.ParsePasswordPolicy(null);

        Assert.Null(policy.MinPasswordLength);
        Assert.Null(policy.LockoutThreshold);
        Assert.Contains("未限制", AccountRules.DescribePasswordPolicy(policy));
    }

    [Fact]
    public void UAC关闭时给出风险提示()
    {
        var description = AccountRules.DescribeUac(0, 5, 1);

        Assert.Equal("已关闭", description.Level);
        Assert.NotNull(description.Risk);
        Assert.Contains("安全风险", description.Risk!);
    }

    [Fact]
    public void UAC提示行为可读化()
    {
        Assert.Contains("默认", AccountRules.DescribeUac(1, 5, 1).Level);
        Assert.Contains("安全桌面", AccountRules.DescribeUac(1, 2, 1).Level);
        Assert.Contains("静默提权", AccountRules.DescribeUac(1, 0, 1).Level);
        Assert.Contains("未知", AccountRules.DescribeUac(null, null, null).Level);
        Assert.Null(AccountRules.DescribeUac(1, 5, 0).Risk);
    }
}