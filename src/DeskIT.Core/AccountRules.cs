using System.Globalization;

namespace DeskIT.Core;

/// <summary>
/// M6 账号与权限辅助的可单元测试纯规则：账号状态文案与统计、<c>net accounts</c> 输出解析、
/// UAC 提示级别可读化。本类不依赖任何 Windows API，也不产生任何系统变更。
/// </summary>
public static class AccountRules
{
    /// <summary>账号状态的中文描述：启用 / 停用 + 锁定 + 密码是否过期。</summary>
    /// <param name="account">本地账号信息。</param>
    /// <returns>形如“已启用 · 已锁定 · 密码永不过期”的说明。</returns>
    public static string DescribeStatus(LocalAccountInfo account)
    {
        var parts = new List<string> { account.Enabled ? "已启用" : "已停用" };

        if (account.LockedOut)
        {
            parts.Add("已锁定");
        }

        parts.Add(account.PasswordExpires ? "密码有有效期" : "密码永不过期");

        if (IsNeverLoggedOn(account))
        {
            parts.Add("从未登录");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>账号是否从未登录过（最近登录时间为空即视为从未登录）。</summary>
    /// <param name="account">本地账号信息。</param>
    /// <returns>从未登录返回 true。</returns>
    public static bool IsNeverLoggedOn(LocalAccountInfo account)
        => string.IsNullOrWhiteSpace(account.LastLogon);

    /// <summary>统计账号概览：总数、启用 / 停用、锁定与从未登录数量。</summary>
    /// <param name="accounts">本地账号列表。</param>
    /// <returns>统计摘要。</returns>
    public static AccountSummary Summarize(IReadOnlyList<LocalAccountInfo> accounts)
        => new(
            accounts.Count,
            accounts.Count(account => account.Enabled),
            accounts.Count(account => !account.Enabled),
            accounts.Count(account => account.LockedOut),
            accounts.Count(IsNeverLoggedOn));

    /// <summary>账号概览的中文说明。</summary>
    /// <param name="summary">统计摘要。</param>
    /// <returns>形如“共 5 个本地账号：启用 4、停用 1；锁定 1、从未登录 2”。</returns>
    public static string DescribeSummary(AccountSummary summary)
        => $"共 {summary.Total} 个本地账号：启用 {summary.Enabled}、停用 {summary.Disabled}；"
           + $"锁定 {summary.Locked}、从未登录 {summary.NeverLoggedOn}";

    /// <summary>
    /// 解析 <c>net accounts</c> 的输出为密码 / 锁定策略；中英文输出均支持，
    /// 数值缺失或为“从不 / Never / None”时对应字段记为 null（如实表示“未限制或未知”）。
    /// </summary>
    /// <param name="output">命令输出文本；为 null 或空时返回全 null 的策略。</param>
    /// <returns>解析出的策略摘要。</returns>
    public static PasswordPolicy ParsePasswordPolicy(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new PasswordPolicy(null, null, null, null, null, null);
        }

        int? minLength = null;
        int? maxAge = null;
        int? minAge = null;
        int? lockoutThreshold = null;
        int? lockoutDuration = null;
        int? lockoutWindow = null;

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.LastIndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var label = line[..separator].Trim();
            var value = ParseNumber(line[(separator + 1)..]);

            if (Contains(label, "密码长度最小值", "Minimum password length"))
            {
                minLength = value;
            }
            else if (Contains(label, "密码最长使用期限", "Maximum password age"))
            {
                maxAge = value;
            }
            else if (Contains(label, "密码最短使用期限", "Minimum password age"))
            {
                minAge = value;
            }
            else if (Contains(label, "锁定阈值", "Lockout threshold"))
            {
                lockoutThreshold = value;
            }
            else if (Contains(label, "锁定持续时间", "Lockout duration"))
            {
                lockoutDuration = value;
            }
            else if (Contains(label, "锁定观察窗口", "Lockout observation window"))
            {
                lockoutWindow = value;
            }
        }

        return new PasswordPolicy(minLength, maxAge, minAge, lockoutThreshold, lockoutDuration, lockoutWindow);
    }

    /// <summary>密码 / 锁定策略的中文说明。</summary>
    /// <param name="policy">策略摘要。</param>
    /// <returns>形如“密码最短长度 8；最长使用期限 42 天；锁定阈值 5 次”。</returns>
    public static string DescribePasswordPolicy(PasswordPolicy policy)
    {
        var parts = new List<string>
        {
            policy.MinPasswordLength is null ? "密码最短长度：未限制" : $"密码最短长度：{policy.MinPasswordLength}",
            policy.MaxPasswordAgeDays is null ? "最长使用期限：从不" : $"最长使用期限：{policy.MaxPasswordAgeDays} 天",
            policy.LockoutThreshold is null ? "锁定阈值：从不锁定" : $"锁定阈值：{policy.LockoutThreshold} 次"
        };

        if (policy.LockoutThreshold is not null && policy.LockoutDurationMinutes is not null)
        {
            parts.Add($"锁定时长：{policy.LockoutDurationMinutes} 分钟");
        }

        return string.Join("；", parts);
    }

    /// <summary>
    /// 把 UAC 的注册表取值可读化：关闭 UAC 时给出明确风险提示；
    /// 其余按 ConsentPromptBehaviorAdmin 的取值说明提示行为。
    /// </summary>
    /// <param name="enableLua">EnableLUA 取值；0 表示 UAC 已关闭。</param>
    /// <param name="consentPromptBehaviorAdmin">ConsentPromptBehaviorAdmin 取值；未知为 null。</param>
    /// <param name="promptOnSecureDesktop">PromptOnSecureDesktop 取值；未知为 null。</param>
    /// <returns>可读化结果。</returns>
    public static UacDescription DescribeUac(
        int? enableLua,
        int? consentPromptBehaviorAdmin,
        int? promptOnSecureDesktop)
    {
        if (enableLua == 0)
        {
            return new UacDescription("已关闭", "UAC 已关闭：应用可在无提示的情况下提升权限，存在安全风险，建议重新启用。");
        }

        var secureDesktop = promptOnSecureDesktop == 1 ? "（安全桌面）" : string.Empty;
        var level = consentPromptBehaviorAdmin switch
        {
            0 => "从不通知：管理员操作直接静默提权",
            1 => $"提示凭据{secureDesktop}",
            2 => $"提示同意{secureDesktop}",
            3 => "提示凭据",
            4 => "提示同意",
            5 => "仅非 Windows 二进制文件提示同意（默认）",
            null => "未知（未读取到提示行为设置）",
            _ => $"未知取值 {consentPromptBehaviorAdmin}"
        };

        return new UacDescription(level, null);
    }

    /// <summary>判断标签是否命中任一候选关键词（忽略大小写）。</summary>
    /// <param name="label">标签文本。</param>
    /// <param name="candidates">候选关键词。</param>
    /// <returns>命中返回 true。</returns>
    private static bool Contains(string label, params string[] candidates)
        => candidates.Any(candidate => label.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析策略数值：可解析为整数时返回该值；“从不 / Never / None / 未设置”等表示未限制的取值返回 null。
    /// </summary>
    /// <param name="text">取值文本。</param>
    /// <returns>数值或 null。</returns>
    private static int? ParseNumber(string text)
    {
        var value = text.Trim();
        if (value.Length == 0
            || value.Contains("从不", StringComparison.OrdinalIgnoreCase)
            || value.Contains("never", StringComparison.OrdinalIgnoreCase)
            || value.Contains("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}