using System.Text.Json;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 账号与权限的只读查询（M6）：本机账号列表、本地管理员组成员、UAC 与登录 / 密码策略、
/// 共享凭据残留、域与时间源状态。全部为只读查询，不产生任何系统变更；
/// 变更类操作（解锁、启用 / 停用、重命名、清理凭据）由 M4 动作库显式选择并二次确认后执行。
/// </summary>
public interface IAccountDirectoryQuery
{
    /// <summary>
    /// 执行一次账号与权限只读查询。
    /// </summary>
    /// <param name="privilege">当前权限级别；只读模式下部分项可能读取不到，如实记录为问题。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>只读查询结果。</returns>
    Task<AccountSnapshot> QueryAsync(PrivilegeLevel privilege, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountDirectoryQuery" />
public sealed class AccountDirectoryQuery : IAccountDirectoryQuery
{
    /// <summary>查询超时秒数。</summary>
    private const int QueryTimeoutSeconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPowerShellExecutor _powerShell;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 初始化查询器。
    /// </summary>
    /// <param name="powerShell">PowerShell 5.1 执行器；为 null 时使用内置实现。</param>
    /// <param name="clock">时间源，便于测试；为 null 时使用本地当前时间。</param>
    public AccountDirectoryQuery(IPowerShellExecutor? powerShell = null, Func<DateTimeOffset>? clock = null)
    {
        _powerShell = powerShell ?? new PowerShell51Executor();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// 只读查询脚本：以 JSON 一次性返回账号、管理员组成员、凭据残留与运行环境；
    /// 每块独立 try/catch，单项失败不影响其余项，并把失败原因收敛到 problems。
    /// </summary>
    private const string QueryScript = """
        $result = [ordered]@{
            accounts       = @()
            administrators = @()
            credentials    = @()
            problems       = @()
            environment    = [ordered]@{}
        }

        $lockMap = @{}
        try {
            Get-CimInstance -ClassName Win32_UserAccount -Filter 'LocalAccount=True' -ErrorAction Stop | ForEach-Object {
                $lockMap[$_.Name] = [bool]$_.Lockout
            }
        } catch { }

        try {
            foreach ($u in (Get-LocalUser -ErrorAction Stop)) {
                $passwordLastSet = $null
                if ($u.PasswordLastSet) { $passwordLastSet = $u.PasswordLastSet.ToString('yyyy-MM-dd HH:mm:ss') }
                $lastLogon = $null
                if ($u.LastLogon) { $lastLogon = $u.LastLogon.ToString('yyyy-MM-dd HH:mm:ss') }
                $locked = $false
                if ($lockMap.ContainsKey($u.Name)) { $locked = [bool]$lockMap[$u.Name] }
                $result.accounts += [ordered]@{
                    name            = [string]$u.Name
                    fullName        = [string]$u.FullName
                    enabled         = [bool]$u.Enabled
                    lockedOut       = $locked
                    passwordExpires = [bool]$u.PasswordExpires
                    passwordLastSet = $passwordLastSet
                    lastLogon       = $lastLogon
                    source          = [string]$u.PrincipalSource
                }
            }
        } catch {
            $result.problems += ('本地账号读取失败：' + $_.Exception.Message)
        }

        try {
            $adminGroup = Get-LocalGroup -SID 'S-1-5-32-544' -ErrorAction Stop
            foreach ($member in (Get-LocalGroupMember -Group $adminGroup.Name -ErrorAction Stop)) {
                $result.administrators += [string]$member.Name
            }
        } catch {
            $result.problems += ('本地管理员组成员读取失败：' + $_.Exception.Message)
        }

        try {
            $output = cmdkey /list | Out-String
            foreach ($line in ($output -split '(`r`n|`n)')) {
                if ($line -match '^(目标|Target)\s*:\s*(.+)$') { $result.credentials += $matches[2].Trim() }
            }
        } catch {
            $result.problems += ('共享凭据读取失败：' + $_.Exception.Message)
        }

        try {
            $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
            $result.environment['partOfDomain'] = [bool]$computer.PartOfDomain
            $result.environment['domain'] = [string]$computer.Domain
        } catch {
            $result.problems += ('域信息读取失败：' + $_.Exception.Message)
        }

        try {
            $policy = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction Stop
            $result.environment['enableLua'] = [int]$policy.EnableLUA
            $result.environment['consentPromptBehaviorAdmin'] = [int]$policy.ConsentPromptBehaviorAdmin
            $result.environment['promptOnSecureDesktop'] = [int]$policy.PromptOnSecureDesktop
        } catch {
            $result.problems += ('UAC 设置读取失败：' + $_.Exception.Message)
        }

        try {
            $result.environment['passwordPolicyOutput'] = (net accounts | Out-String)
        } catch {
            $result.problems += ('密码与锁定策略读取失败：' + $_.Exception.Message)
        }

        $result.environment['localTime'] = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')

        try {
            $result.environment['timeSource'] = (w32tm /query /source | Out-String).Trim()
        } catch {
            $result.environment['timeSource'] = ''
        }

        try {
            $event = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-GroupPolicy' } -MaxEvents 1 -ErrorAction Stop
            $result.environment['lastPolicyApplied'] = $event.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
        } catch {
            $result.environment['lastPolicyApplied'] = ''
        }

        $result | ConvertTo-Json -Depth 6 -Compress
        """;

    /// <inheritdoc />
    public async Task<AccountSnapshot> QueryAsync(
        PrivilegeLevel privilege,
        CancellationToken cancellationToken = default)
    {
        var readOnly = privilege != PrivilegeLevel.Administrator;
        var execution = await _powerShell
            .ExecuteAsync(QueryScript, TimeSpan.FromSeconds(QueryTimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        var problems = new List<string>();
        var now = _clock();

        if (execution.TimedOut)
        {
            problems.Add($"账号与权限查询超时（{QueryTimeoutSeconds} 秒），本次结果不完整。");
            return new AccountSnapshot { QueriedAt = now, ReadOnlyMode = readOnly, Problems = problems };
        }

        AccountQueryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<AccountQueryDocument>(execution.StandardOutput, JsonOptions);
        }
        catch (JsonException ex)
        {
            problems.Add($"账号与权限查询结果解析失败：{ex.Message}");
            document = null;
        }

        if (document is null)
        {
            if (!string.IsNullOrWhiteSpace(execution.StandardError))
            {
                problems.Add($"查询脚本返回错误：{execution.StandardError.Trim()}");
            }

            problems.Add("未能取得账号与权限信息，请以管理员身份重新启动后重试。");
            return new AccountSnapshot { QueriedAt = now, ReadOnlyMode = readOnly, Problems = problems };
        }

        problems.AddRange(document.Problems ?? new List<string>());

        var accounts = (document.Accounts ?? new List<AccountDto>())
            .Where(account => !string.IsNullOrWhiteSpace(account.Name))
            .Select(account => new LocalAccountInfo(
                account.Name!.Trim(),
                NullIfEmpty(account.FullName),
                account.Enabled,
                account.LockedOut,
                account.PasswordExpires,
                NullIfEmpty(account.PasswordLastSet),
                NullIfEmpty(account.LastLogon),
                DescribePrincipalSource(account.Source)))
            .OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var facts = BuildFacts(document.Environment, problems);

        return new AccountSnapshot
        {
            QueriedAt = now,
            ReadOnlyMode = readOnly,
            Accounts = accounts,
            Administrators = (document.Administrators ?? new List<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Facts = facts,
            SharedCredentials = (document.Credentials ?? new List<string>())
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .Select(target => target.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Problems = problems
        };
    }

    /// <summary>
    /// 把运行环境整理为界面展示的只读事实项（域状态、UAC、策略、时间与时间源、策略应用结果）。
    /// </summary>
    /// <param name="environment">查询到的运行环境；为 null 时返回空列表。</param>
    /// <param name="problems">问题清单，用于补充说明无法读取的项。</param>
    /// <returns>事实项清单。</returns>
    private static List<AccountFact> BuildFacts(EnvironmentDto? environment, List<string> problems)
    {
        var facts = new List<AccountFact>();
        if (environment is null)
        {
            return facts;
        }

        facts.Add(new AccountFact(
            "计算机域状态",
            environment.PartOfDomain == true
                ? $"已加入域：{NullIfEmpty(environment.Domain) ?? "（域未知）"}"
                : "工作组环境（未加入域）；域侧操作仅做只读诊断，不执行任何域侧变更。"));

        var uac = AccountRules.DescribeUac(
            environment.EnableLua,
            environment.ConsentPromptBehaviorAdmin,
            environment.PromptOnSecureDesktop);
        facts.Add(new AccountFact("UAC 提示级别", uac.Risk is null ? uac.Level : $"{uac.Level}；{uac.Risk}"));

        var policyOutput = environment.PasswordPolicyOutput;
        if (!string.IsNullOrWhiteSpace(policyOutput)
            && (policyOutput.Contains("拒绝访问", StringComparison.Ordinal)
                || policyOutput.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("密码与锁定策略读取需要管理员权限，相关项记“未知”。");
        }
        else if (!string.IsNullOrWhiteSpace(policyOutput))
        {
            facts.Add(new AccountFact("密码与锁定策略", AccountRules.DescribePasswordPolicy(AccountRules.ParsePasswordPolicy(policyOutput))));
        }

        facts.Add(new AccountFact("本机时间", $"{environment.LocalTime ?? "未知"}（账号问题常源于机器时间偏差，请与标准时间核对）"));
        facts.Add(new AccountFact(
            "时间源状态",
            string.IsNullOrWhiteSpace(environment.TimeSource) ? "未知（时间服务未运行或不可用）" : environment.TimeSource!));
        facts.Add(new AccountFact(
            "最近一次策略应用",
            string.IsNullOrWhiteSpace(environment.LastPolicyApplied) ? "未知（未找到策略应用事件）" : environment.LastPolicyApplied!));

        return facts;
    }

    /// <summary>把账号来源枚举值翻译为中文说明。</summary>
    /// <param name="source">来源取值。</param>
    /// <returns>中文说明；未知时返回 null。</returns>
    private static string? DescribePrincipalSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        return source.Trim() switch
        {
            "Local" => "本地账号",
            "MicrosoftAccount" => "Microsoft 账户",
            "AzureAD" => "Azure AD 账户",
            "Unknown" => null,
            _ => source.Trim()
        };
    }

    /// <summary>空白字符串归一为 null，避免界面出现空值噪声。</summary>
    /// <param name="value">原始文本。</param>
    /// <returns>非空文本或 null。</returns>
    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>查询脚本的 JSON 文档结构。</summary>
    private sealed class AccountQueryDocument
    {
        /// <summary>本地账号列表。</summary>
        public List<AccountDto>? Accounts { get; set; }

        /// <summary>本地管理员组成员。</summary>
        public List<string>? Administrators { get; set; }

        /// <summary>共享凭据残留目标。</summary>
        public List<string>? Credentials { get; set; }

        /// <summary>查询过程中的问题说明。</summary>
        public List<string>? Problems { get; set; }

        /// <summary>运行环境摘要。</summary>
        public EnvironmentDto? Environment { get; set; }
    }

    /// <summary>账号条目。</summary>
    private sealed class AccountDto
    {
        /// <summary>账号名。</summary>
        public string? Name { get; set; }

        /// <summary>全名。</summary>
        public string? FullName { get; set; }

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; }

        /// <summary>是否锁定。</summary>
        public bool LockedOut { get; set; }

        /// <summary>密码是否有有效期。</summary>
        public bool PasswordExpires { get; set; }

        /// <summary>最近设置密码时间。</summary>
        public string? PasswordLastSet { get; set; }

        /// <summary>最近登录时间。</summary>
        public string? LastLogon { get; set; }

        /// <summary>账号来源。</summary>
        public string? Source { get; set; }
    }

    /// <summary>运行环境摘要。</summary>
    private sealed class EnvironmentDto
    {
        /// <summary>是否加入域。</summary>
        public bool? PartOfDomain { get; set; }

        /// <summary>域 / 工作组名称。</summary>
        public string? Domain { get; set; }

        /// <summary>EnableLUA 取值。</summary>
        public int? EnableLua { get; set; }

        /// <summary>ConsentPromptBehaviorAdmin 取值。</summary>
        public int? ConsentPromptBehaviorAdmin { get; set; }

        /// <summary>PromptOnSecureDesktop 取值。</summary>
        public int? PromptOnSecureDesktop { get; set; }

        /// <summary>net accounts 原始输出。</summary>
        public string? PasswordPolicyOutput { get; set; }

        /// <summary>本机时间。</summary>
        public string? LocalTime { get; set; }

        /// <summary>时间源。</summary>
        public string? TimeSource { get; set; }

        /// <summary>最近一次策略应用时间。</summary>
        public string? LastPolicyApplied { get; set; }
    }
}