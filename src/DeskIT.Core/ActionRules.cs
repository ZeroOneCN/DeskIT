using System.Text.RegularExpressions;

namespace DeskIT.Core;

/// <summary>
/// M4 修复动作的可单元测试纯规则：风险分级与确认要求、结果归类、参数绑定与前置条件判定。
/// 本类不依赖任何 Windows API；实际脚本执行与服务/路径探测由 DeskIT.Windows 提供。
/// </summary>
public static class ActionRules
{
    /// <summary>单项动作默认超时秒数（PRD：单项动作默认超时 10 分钟）。</summary>
    public const int DefaultTimeoutSeconds = 600;

    /// <summary>不可逆动作的确认词（PRD：不可逆动作须输入确认词）。</summary>
    public const string IrreversibleConfirmWord = "DELETE";

    /// <summary>脚本中参数占位符的匹配式：<c>{{参数名}}</c>。</summary>
    private static readonly Regex TokenPattern = new(@"\{\{([A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);

    /// <summary>逻辑参数名 → 中文说明。</summary>
    private static readonly IReadOnlyDictionary<string, string> LogicalParameters = new Dictionary<string, string>
    {
        ["BackupFile"] = "回滚原始数据的保存路径",
        ["DataDir"] = "U 盘数据目录"
    };

    /// <summary>
    /// 判断动作按风险级别所需的确认方式：不可逆=输入确认词、高=逐项确认、中=汇总确认、低/只读=仅汇总明示。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <returns>确认方式。</returns>
    public static ActionConfirmationKind ConfirmationFor(ActionDescriptor action) => action.Risk switch
    {
        RiskLevel.Irreversible => ActionConfirmationKind.ConfirmWord,
        RiskLevel.High => ActionConfirmationKind.PerAction,
        RiskLevel.Medium => ActionConfirmationKind.BatchSummary,
        _ => ActionConfirmationKind.None
    };

    /// <summary>风险级别的中文名称（只读 / 低 / 中 / 高 / 不可逆）。</summary>
    /// <param name="risk">风险级别。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeRisk(RiskLevel risk) => risk switch
    {
        RiskLevel.ReadOnly => "只读",
        RiskLevel.Low => "低",
        RiskLevel.Medium => "中",
        RiskLevel.High => "高",
        _ => "不可逆"
    };

    /// <summary>动作结果的中文名称。</summary>
    /// <param name="outcome">结果归类。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeOutcome(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Success => "成功",
        ActionOutcome.SuccessPendingRestart => "成功（需重启生效）",
        ActionOutcome.Failed => "失败",
        ActionOutcome.Skipped => "已跳过",
        ActionOutcome.Cancelled => "已取消",
        _ => "已超时"
    };

    /// <summary>确认方式的中文说明。</summary>
    /// <param name="kind">确认方式。</param>
    /// <returns>中文说明。</returns>
    public static string DescribeConfirmation(ActionConfirmationKind kind) => kind switch
    {
        ActionConfirmationKind.ConfirmWord => $"逐项二次确认，并需输入确认词“{IrreversibleConfirmWord}”",
        ActionConfirmationKind.PerAction => "逐项二次确认，写明影响与不可逆性",
        ActionConfirmationKind.BatchSummary => "汇总区确认，并提示会话可能中断",
        _ => "无需逐项确认，仅汇总区明示"
    };

    /// <summary>
    /// 动作在当前权限下是否可执行：只读诊断模式（非管理员）下，需管理员权限的动作全部禁用。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <param name="privilege">当前权限级别。</param>
    /// <returns>可执行返回 true。</returns>
    public static bool CanRunByPrivilege(ActionDescriptor action, PrivilegeLevel privilege)
        => !action.RequiresAdministrator || privilege == PrivilegeLevel.Administrator;

    /// <summary>
    /// 结果归类：取消优先于超时，超时优先于退出码；退出码非零即失败；退出码为零时按是否需重启细分为“成功 / 成功但需重启”。
    /// </summary>
    /// <param name="canceled">是否被取消（主动中止后续动作）。</param>
    /// <param name="timedOut">是否超时被终止。</param>
    /// <param name="exitCode">脚本退出码。</param>
    /// <param name="requiresRestart">该动作是否需重启生效。</param>
    /// <returns>结果归类。</returns>
    public static ActionOutcome ClassifyOutcome(bool canceled, bool timedOut, int exitCode, bool requiresRestart)
    {
        if (canceled)
        {
            return ActionOutcome.Cancelled;
        }

        if (timedOut)
        {
            return ActionOutcome.TimedOut;
        }

        if (exitCode != 0)
        {
            return ActionOutcome.Failed;
        }

        return requiresRestart ? ActionOutcome.SuccessPendingRestart : ActionOutcome.Success;
    }

    /// <summary>
    /// 列出未被满足的前置条件。判定委托给调用方提供的探测函数（Windows 侧实现），本方法保持纯逻辑。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <param name="isMet">前置条件探测函数：满足返回 true。</param>
    /// <returns>未被满足的前置条件清单；全部满足时为空。</returns>
    public static IReadOnlyList<ActionPrecondition> UnmetPreconditions(
        ActionDescriptor action,
        Func<ActionPrecondition, bool> isMet)
        => action.Preconditions.Where(precondition => !isMet(precondition)).ToList();

    /// <summary>
    /// 找出脚本引用但未提供取值的必填参数（含未声明的占位符）。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <param name="values">已提供的参数取值（含逻辑参数）。</param>
    /// <returns>缺失的参数名清单；无缺失时为空。</returns>
    public static IReadOnlyList<string> MissingParameters(
        ActionDescriptor action,
        IReadOnlyDictionary<string, string> values)
    {
        var required = action.Parameters
            .Where(parameter => parameter.IsRequired)
            .Select(parameter => parameter.Name);

        return required
            .Where(name => !values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    /// <summary>
    /// 提取脚本中引用的全部占位符名称（去重，按首次出现顺序）。
    /// </summary>
    /// <param name="script">PowerShell 脚本文本。</param>
    /// <returns>占位符名称清单。</returns>
    public static IReadOnlyList<string> ExtractTokens(string? script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return Array.Empty<string>();
        }

        var seen = new List<string>();
        foreach (Match match in TokenPattern.Matches(script))
        {
            var name = match.Groups[1].Value;
            if (!seen.Contains(name, StringComparer.Ordinal))
            {
                seen.Add(name);
            }
        }

        return seen;
    }

    /// <summary>
    /// 把参数取值绑定进脚本：把每个 <c>{{名称}}</c> 替换为一个经过转义的 PowerShell 双引号字符串字面量。
    /// 取值中的反引号、双引号与美元符号会被转义，避免参数内容被当作脚本执行。
    /// </summary>
    /// <param name="script">PowerShell 脚本文本。</param>
    /// <param name="values">参数取值（含逻辑参数）。</param>
    /// <returns>绑定后的脚本；未提供取值的占位符保持原样。</returns>
    public static string BindParameters(string script, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(script) || values.Count == 0)
        {
            return script;
        }

        var result = script;
        foreach (var pair in values)
        {
            result = result.Replace($"{{{{{pair.Key}}}}}", ToPowerShellLiteral(pair.Value), StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// 生成一个 PowerShell 字符串字面量：取值为 <c>O'Brien</c> 时得到 <c>"O'Brien"</c>；
    /// 反引号、双引号与美元符号会被转义，其余字符原样保留。
    /// </summary>
    /// <param name="value">原始取值。</param>
    /// <returns>可直接嵌入脚本的双引号字面量。</returns>
    public static string ToPowerShellLiteral(string value)
    {
        var escaped = value
            .Replace("`", "``", StringComparison.Ordinal)
            .Replace("\"", "`\"", StringComparison.Ordinal)
            .Replace("$", "`$", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }

    /// <summary>
    /// 校验脚本占位符是否全部有来源（声明的参数或逻辑参数）；用于动作定义加载时尽早发现定义错误。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <returns>无来源的占位符名称清单；无问题时为空。</returns>
    public static IReadOnlyList<string> UnresolvedTokens(ActionDescriptor action)
    {
        var known = new HashSet<string>(LogicalParameters.Keys, StringComparer.Ordinal);
        foreach (var parameter in action.Parameters)
        {
            known.Add(parameter.Name);
        }

        return ExtractTokens(action.Script)
            .Where(token => !known.Contains(token))
            .ToList();
    }
}