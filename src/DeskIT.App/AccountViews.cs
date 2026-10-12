using System.Collections.Generic;
using System.Linq;
using DeskIT.Core;

namespace DeskIT.App;

/// <summary>目标机上一个本地账号的界面视图（只读展示）。</summary>
internal sealed class AccountRowView
{
    /// <summary>
    /// 初始化账号视图。
    /// </summary>
    /// <param name="account">本地账号只读信息。</param>
    public AccountRowView(LocalAccountInfo account)
    {
        Account = account;
        Name = account.Name;
        FullNameText = string.IsNullOrWhiteSpace(account.FullName) ? string.Empty : account.FullName!;
        HasFullName = FullNameText.Length > 0;
        StatusText = AccountRules.DescribeStatus(account);
        SourceText = string.IsNullOrWhiteSpace(account.Source) ? "来源未知" : account.Source!;
        LastLogonText = AccountRules.IsNeverLoggedOn(account)
            ? "最近登录：从未登录"
            : $"最近登录：{account.LastLogon}";
        PasswordText = string.IsNullOrWhiteSpace(account.PasswordLastSet)
            ? "最近设置密码：未知"
            : $"最近设置密码：{account.PasswordLastSet}";
        IsLocked = account.LockedOut;
        IsDisabled = !account.Enabled;
    }

    /// <summary>本地账号只读信息。</summary>
    public LocalAccountInfo Account { get; }

    /// <summary>账号名。</summary>
    public string Name { get; }

    /// <summary>全名文本；无为空。</summary>
    public string FullNameText { get; }

    /// <summary>是否有全名。</summary>
    public bool HasFullName { get; }

    /// <summary>状态说明（启用 / 停用、锁定、密码有效期、是否从未登录）。</summary>
    public string StatusText { get; }

    /// <summary>账号来源文本。</summary>
    public string SourceText { get; }

    /// <summary>最近登录文本。</summary>
    public string LastLogonText { get; }

    /// <summary>最近设置密码文本。</summary>
    public string PasswordText { get; }

    /// <summary>是否处于锁定状态（界面高亮用）。</summary>
    public bool IsLocked { get; }

    /// <summary>是否已停用（界面高亮用）。</summary>
    public bool IsDisabled { get; }
}

/// <summary>账号与权限只读查询结果的界面视图。</summary>
/// <param name="SummaryText">账号概览文本。</param>
/// <param name="AttentionText">需要关注提示；无关注项时为空。</param>
/// <param name="QueryText">查询时间与模式文本。</param>
/// <param name="Accounts">账号视图清单。</param>
/// <param name="Administrators">本地管理员组成员。</param>
/// <param name="Facts">UAC、域、策略、时间源等事实项。</param>
/// <param name="SharedCredentials">共享凭据残留目标。</param>
/// <param name="Problems">查询问题说明。</param>
/// <param name="HasProblems">是否有问题说明。</param>
/// <param name="HasCredentials">是否有共享凭据残留。</param>
internal sealed record AccountSnapshotView(
    string SummaryText,
    string AttentionText,
    string QueryText,
    IReadOnlyList<AccountRowView> Accounts,
    IReadOnlyList<string> Administrators,
    IReadOnlyList<AccountFact> Facts,
    IReadOnlyList<string> SharedCredentials,
    IReadOnlyList<string> Problems,
    bool HasProblems,
    bool HasCredentials);

/// <summary>账号查询结果到界面视图的转换辅助。</summary>
internal static class AccountViewFactory
{
    /// <summary>
    /// 把只读查询结果转换为界面视图。
    /// </summary>
    /// <param name="snapshot">账号与权限只读查询结果。</param>
    /// <returns>界面视图。</returns>
    public static AccountSnapshotView Build(AccountSnapshot snapshot)
    {
        var summary = AccountRules.Summarize(snapshot.Accounts);
        var attention = summary.HasAttention
            ? $"需关注：锁定 {summary.Locked} 个、从未登录 {summary.NeverLoggedOn} 个（可能为闲置账号）。"
            : string.Empty;

        var mode = snapshot.ReadOnlyMode
            ? "只读模式（标准用户）：部分项可能读取不到，如实记“未知”。"
            : "管理员模式：可读取全部只读项。";

        return new AccountSnapshotView(
            AccountRules.DescribeSummary(summary),
            attention,
            $"查询时间 {snapshot.QueriedAt:yyyy-MM-dd HH:mm:ss} · {mode}",
            snapshot.Accounts.Select(account => new AccountRowView(account)).ToList(),
            snapshot.Administrators.ToList(),
            snapshot.Facts.ToList(),
            snapshot.SharedCredentials.ToList(),
            snapshot.Problems.ToList(),
            snapshot.Problems.Count > 0,
            snapshot.SharedCredentials.Count > 0);
    }
}