using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 修复动作统一执行器（M4）：集中处理参数校验、权限与前置条件判断、脚本绑定与执行、
/// 超时 / 取消 / 输出截断与结果归类，并把审计与回滚信息写入 U 盘备份区。
/// 各动作脚本因此只需描述“做什么”，无需各自实现这些通用逻辑。
/// </summary>
public interface IActionExecutor
{
    /// <summary>
    /// 执行一个修复动作。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <param name="parameterValues">IT 人员填写的参数取值（未提供时使用默认值）。</param>
    /// <param name="privilege">当前权限级别；只读模式下需管理员的动作直接标记“已跳过”。</param>
    /// <param name="dataDirectory">U 盘数据目录（备份区与逻辑参数 DataDir 的来源）。</param>
    /// <param name="sessionId">所属会话标识；未关联会话时传空串。</param>
    /// <param name="cancellationToken">取消标记；取消时终止脚本并归类为“已取消”，不向调用方抛出异常。</param>
    /// <returns>动作执行结果。</returns>
    Task<ActionExecutionResult> ExecuteAsync(
        ActionDescriptor action,
        IReadOnlyDictionary<string, string> parameterValues,
        PrivilegeLevel privilege,
        string dataDirectory,
        string sessionId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IActionExecutor" />
public sealed class ActionExecutor : IActionExecutor
{
    private readonly IPowerShellExecutor _powerShell;
    private readonly IActionPreconditionChecker _preconditionChecker;
    private readonly IActionBackupStore _backupStore;
    private readonly IMediaVault? _vault;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 初始化动作执行器。
    /// </summary>
    /// <param name="powerShell">PowerShell 5.1 执行器；为 null 时使用内置实现。</param>
    /// <param name="preconditionChecker">前置条件检查器；为 null 时使用 Windows 实现。</param>
    /// <param name="backupStore">备份区存储；为 null 时使用文件实现。</param>
    /// <param name="vault">介质金库（用于加密脚本产出的回滚原始数据文件）；可为 null。</param>
    /// <param name="clock">时间源，便于测试；为 null 时使用本地当前时间。</param>
    public ActionExecutor(
        IPowerShellExecutor? powerShell = null,
        IActionPreconditionChecker? preconditionChecker = null,
        IActionBackupStore? backupStore = null,
        IMediaVault? vault = null,
        Func<DateTimeOffset>? clock = null)
    {
        _powerShell = powerShell ?? new PowerShell51Executor();
        _preconditionChecker = preconditionChecker ?? new ActionPreconditionChecker();
        _backupStore = backupStore ?? new ActionBackupStore(vault);
        _vault = vault;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDescriptor action,
        IReadOnlyDictionary<string, string> parameterValues,
        PrivilegeLevel privilege,
        string dataDirectory,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var executedAt = _clock();

        // 权限：只读诊断模式下，需管理员权限的动作全部禁用。
        if (!ActionRules.CanRunByPrivilege(action, privilege))
        {
            return Skipped(action, parameterValues, "已跳过（只读诊断模式）：该项需要管理员权限，只读模式下不可执行。");
        }

        var declared = ResolveParameters(action, parameterValues);

        var missing = ActionRules.MissingParameters(action, declared);
        if (missing.Count > 0)
        {
            return Skipped(action, declared, $"已跳过（缺少必填参数）：{string.Join("、", missing)}。");
        }

        // 前置条件：不满足即跳过，并给出原因，不阻断其他动作。
        var unmet = _preconditionChecker.Unmet(action);
        if (unmet.Count > 0)
        {
            return Skipped(action, declared, $"已跳过（前置条件不满足）：{string.Join("；", unmet.Select(item => item.Description))}。");
        }

        var runId = BuildRunId(executedAt, action.ActionId);
        var backupDirectory = _backupStore.PrepareDirectory(dataDirectory);
        var rollbackDataFile = Path.Combine(backupDirectory, $"{runId}.rollback.json");

        var values = new Dictionary<string, string>(declared, StringComparer.Ordinal)
        {
            ["BackupFile"] = rollbackDataFile,
            ["DataDir"] = dataDirectory
        };

        var script = Wrap(ActionRules.BindParameters(action.Script ?? string.Empty, values));

        var execution = await _powerShell
            .ExecuteAsync(script, TimeSpan.FromSeconds(action.TimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        var canceled = cancellationToken.IsCancellationRequested;
        var outcome = ActionRules.ClassifyOutcome(canceled, execution.TimedOut, execution.ExitCode, action.RequiresRestart);

        var result = new ActionExecutionResult(
            action.ActionId,
            action.Name,
            action.Risk,
            outcome,
            execution.ExitCode,
            execution.StandardOutput,
            execution.StandardError,
            execution.Duration,
            execution.TimedOut,
            null,
            ToValues(declared));

        // 备份区内容经介质金库加密：脚本产出的回滚原始数据文件在落盘后整文件加密。
        ProtectRollbackFile(rollbackDataFile);

        Persist(dataDirectory, action, result, sessionId, executedAt, runId, rollbackDataFile);

        return result;
    }

    /// <summary>
    /// 把脚本产出的回滚原始数据文件整文件加密；未配置金库或加密失败时保持原文件（不影响动作结果）。
    /// </summary>
    /// <param name="rollbackDataFile">回滚原始数据文件路径。</param>
    private void ProtectRollbackFile(string rollbackDataFile)
    {
        if (_vault is null || !File.Exists(rollbackDataFile))
        {
            return;
        }

        try
        {
            _vault.ProtectFile(rollbackDataFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaLockedException)
        {
            // 备份区加密失败不影响动作本身：结果已如实反映执行情况。
        }
    }

    /// <summary>
    /// 把动作声明的参数解析为最终取值：优先使用 IT 人员填写值，其次使用默认值。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <param name="provided">IT 人员填写的取值。</param>
    /// <returns>参数名 → 取值（未填写且无默认值时为空串）。</returns>
    private static Dictionary<string, string> ResolveParameters(
        ActionDescriptor action,
        IReadOnlyDictionary<string, string> provided)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in action.Parameters)
        {
            if (provided.TryGetValue(parameter.Name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                values[parameter.Name] = value;
            }
            else
            {
                values[parameter.Name] = parameter.DefaultValue ?? string.Empty;
            }
        }

        return values;
    }

    /// <summary>把参数字典转换为有序取值清单，供展示与落盘。</summary>
    /// <param name="declared">参数名 → 取值。</param>
    /// <returns>参数取值清单。</returns>
    private static IReadOnlyList<ActionParameterValue> ToValues(IReadOnlyDictionary<string, string> declared)
        => declared.Select(pair => new ActionParameterValue(pair.Key, pair.Value)).ToList();

    /// <summary>构造“已跳过”结果（不发生系统变更，仍如实呈现原因）。</summary>
    /// <param name="action">动作定义。</param>
    /// <param name="parameters">参数取值。</param>
    /// <param name="reason">跳过原因。</param>
    /// <returns>动作执行结果。</returns>
    private static ActionExecutionResult Skipped(
        ActionDescriptor action,
        IReadOnlyDictionary<string, string> parameters,
        string reason)
        => new(
            action.ActionId,
            action.Name,
            action.Risk,
            ActionOutcome.Skipped,
            -1,
            string.Empty,
            string.Empty,
            TimeSpan.Zero,
            false,
            reason,
            ToValues(parameters));

    /// <summary>构造执行标识：时间戳 + 动作标识 + 短随机后缀，避免同一秒内多次执行互相覆盖。</summary>
    /// <param name="executedAt">执行时间。</param>
    /// <param name="actionId">动作标识。</param>
    /// <returns>执行标识。</returns>
    private static string BuildRunId(DateTimeOffset executedAt, string actionId)
        => $"{executedAt:yyyyMMdd-HHmmss}-{actionId}-{Guid.NewGuid().ToString("N")[..4]}";

    /// <summary>
    /// 把动作脚本包装为统一的退出码约定：脚本未显式退出时，最后一个外部命令的非零退出码视为失败；
    /// 终止性错误（含 -ErrorAction Stop 抛出）一律记为失败（退出码 1）；其余记为成功（退出码 0）。
    /// 这样各动作脚本无需各自实现退出码处理。
    /// </summary>
    /// <param name="script">已绑定参数的脚本文本。</param>
    /// <returns>包装后的脚本。</returns>
    private static string Wrap(string script)
        => "try {\n"
           + script + "\n"
           + "  if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n"
           + "  exit 0\n"
           + "} catch {\n"
           + "  Write-Error $_.Exception.Message\n"
           + "  exit 1\n"
           + "}\n";

    /// <summary>
    /// 把本次执行的审计与回滚信息写入 U 盘备份区；写入失败不改变动作结果（结果已如实反映真实执行情况）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="action">动作定义。</param>
    /// <param name="result">执行结果。</param>
    /// <param name="sessionId">所属会话标识。</param>
    /// <param name="executedAt">执行时间。</param>
    /// <param name="runId">执行标识。</param>
    /// <param name="rollbackDataFile">回滚原始数据文件路径。</param>
    private void Persist(
        string dataDirectory,
        ActionDescriptor action,
        ActionExecutionResult result,
        string sessionId,
        DateTimeOffset executedAt,
        string runId,
        string rollbackDataFile)
    {
        var record = new ActionBackupRecord(
            runId,
            action.ActionId,
            action.Name,
            action.Risk,
            result.Outcome,
            result.ExitCode,
            result.TimedOut,
            result.Duration,
            executedAt,
            Environment.MachineName,
            sessionId ?? string.Empty,
            result.Parameters,
            action.CanRollback,
            action.RollbackNote,
            File.Exists(rollbackDataFile) ? rollbackDataFile : null,
            result.StandardOutput,
            result.StandardError);

        try
        {
            _backupStore.Save(dataDirectory, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份区不可写不影响动作本身：结果已如实反映执行情况，界面会提示写入失败。
        }
    }
}