using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 一次受控自定义命令执行所需的上下文（M9）。
/// </summary>
/// <param name="DataDirectory">U 盘数据目录（命令历史与审计的落盘位置）。</param>
/// <param name="SessionId">所属会话标识；无会话时为空串。</param>
/// <param name="Privilege">当前权限级别（写入“执行身份”）。</param>
/// <param name="Operator">当前登记处理人（写入审计事件）；可为空。</param>
/// <param name="MachineName">目标机计算机名。</param>
public sealed record CustomCommandContext(
    string DataDirectory,
    string SessionId,
    PrivilegeLevel Privilege,
    OperatorInfo? Operator,
    string MachineName);

/// <summary>
/// 受控自定义命令执行服务（M9）：管理员 + 高级模式 + 介质已解锁且可写下执行任意命令，
/// 并<b>强制记录</b>——写入命令历史（加密追加）与审计事件；任一处写入失败即返回记录失败。
/// </summary>
public interface ICustomCommandService
{
    /// <summary>
    /// 执行一条自定义命令：执行前后采集持久化项基线并对比差异，结果归类后强制记录。
    /// </summary>
    /// <param name="commandText">命令原文（未包装；服务内部包装为带退出码约定的脚本）。</param>
    /// <param name="context">执行上下文。</param>
    /// <param name="cancellationToken">取消标记；取消时终止脚本并归类为“已取消”。</param>
    /// <returns>执行与记录结果；记录失败时 <see cref="CustomCommandResult.RecordingSucceeded"/> 为 false。</returns>
    Task<CustomCommandResult> ExecuteAsync(
        string commandText,
        CustomCommandContext context,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICustomCommandService" />
public sealed class CustomCommandService : ICustomCommandService
{
    private readonly IPowerShellExecutor _powerShell;
    private readonly ICustomCommandStore _store;
    private readonly IAuditStore _auditStore;
    private readonly IPersistenceProbe _probe;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 初始化受控自定义命令服务。
    /// </summary>
    /// <param name="powerShell">PowerShell 5.1 执行器；为 null 时使用内置实现。</param>
    /// <param name="store">命令历史存储；为 null 时使用文件实现。</param>
    /// <param name="auditStore">审计存储；为 null 时使用文件实现。</param>
    /// <param name="probe">持久化项只读探测；为 null 时使用 Windows 实现。</param>
    /// <param name="clock">时间源，便于测试；为 null 时使用本地当前时间。</param>
    public CustomCommandService(
        IPowerShellExecutor? powerShell = null,
        ICustomCommandStore? store = null,
        IAuditStore? auditStore = null,
        IPersistenceProbe? probe = null,
        Func<DateTimeOffset>? clock = null)
    {
        _powerShell = powerShell ?? new PowerShell51Executor();
        _store = store ?? new CustomCommandStore();
        _auditStore = auditStore ?? new AuditStore();
        _probe = probe ?? new PersistenceProbe(powerShell);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public async Task<CustomCommandResult> ExecuteAsync(
        string commandText,
        CustomCommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var executedAt = _clock();
        var trimmed = (commandText ?? string.Empty).Trim();
        var maskedCommand = CustomCommandRules.MaskCommand(trimmed);

        // 执行前后采集持久化项基线（放到后台线程，避免阻塞界面；采集失败按“不可用”处理）。
        var before = await Task.Run(() => CaptureAll(_probe), CancellationToken.None).ConfigureAwait(false);

        var script = CustomCommandRules.BuildSedimentScript(trimmed);
        var execution = await _powerShell
            .ExecuteAsync(script, TimeSpan.FromSeconds(CustomCommandRules.DefaultTimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        var after = await Task.Run(() => CaptureAll(_probe), CancellationToken.None).ConfigureAwait(false);

        var changes = new List<PersistenceChange>();
        for (var i = 0; i < before.Count && i < after.Count; i++)
        {
            changes.AddRange(CustomCommandRules.DetectPersistenceChanges(before[i], after[i]));
        }

        var canceled = cancellationToken.IsCancellationRequested;
        var outcome = CustomCommandRules.ClassifyOutcome(canceled, execution.TimedOut, execution.ExitCode);

        var record = new CustomCommandRecord
        {
            CommandId = CustomCommandRules.BuildCommandId(executedAt),
            ExecutedAt = executedAt,
            CommandText = maskedCommand,
            ExecutedAs = CustomCommandRules.DescribeExecutionAs(context.Privilege),
            Outcome = outcome,
            ExitCode = execution.ExitCode,
            TimedOut = execution.TimedOut,
            DurationSeconds = execution.Duration.TotalSeconds,
            OutputSummary = CustomCommandRules.MaskCommand(CustomCommandRules.SummarizeOutput(execution.StandardOutput)),
            ErrorSummary = CustomCommandRules.MaskCommand(CustomCommandRules.SummarizeOutput(execution.StandardError)),
            PersistenceChanges = changes,
            SessionId = context.SessionId ?? string.Empty,
            OperatorEmployeeId = context.Operator?.EmployeeId ?? string.Empty,
            OperatorName = context.Operator?.Name ?? string.Empty,
            MachineName = string.IsNullOrWhiteSpace(context.MachineName) ? Environment.MachineName : context.MachineName
        };

        // 强制记录之一：命令历史（加密追加）。
        try
        {
            _store.Append(context.DataDirectory, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or MediaLockedException or MediaIntegrityException or NotSupportedException)
        {
            return new CustomCommandResult(record, false, $"命令历史写入失败，已阻止后续执行：{ex.Message}");
        }

        // 强制记录之二：审计事件。审计失败同样视为记录失败（不提供“不记录执行”的路径）。
        try
        {
            _auditStore.Append(context.DataDirectory, BuildAuditEvent(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or MediaLockedException or MediaIntegrityException or NotSupportedException)
        {
            return new CustomCommandResult(record, false, $"审计事件写入失败，已阻止后续执行：{ex.Message}");
        }

        return new CustomCommandResult(record, true, null);
    }

    /// <summary>按固定顺序采集三类持久化项快照（服务 / 计划任务 / 自启动项）。</summary>
    /// <param name="probe">持久化项探测。</param>
    /// <returns>三类快照（顺序固定，便于前后对比一一对应）。</returns>
    private static IReadOnlyList<PersistenceSnapshot> CaptureAll(IPersistenceProbe probe)
        => new[]
        {
            probe.CaptureServices(),
            probe.CaptureScheduledTasks(),
            probe.CaptureStartupItems()
        };

    /// <summary>依据命令记录构建审计事件（凭据已脱敏，不含任何明文密码）。</summary>
    /// <param name="record">命令执行记录。</param>
    /// <returns>审计事件。</returns>
    private static AuditEvent BuildAuditEvent(CustomCommandRecord record)
        => new()
        {
            EventId = MediaRules.NewAuditEventId(record.ExecutedAt),
            Kind = AuditEventKind.CustomCommandExecuted,
            At = record.ExecutedAt,
            ComputerName = record.MachineName,
            OperatorEmployeeId = record.OperatorEmployeeId,
            OperatorName = record.OperatorName,
            SessionId = record.SessionId,
            Summary = $"执行自定义命令（{record.ExecutedAs}）：退出码 {record.ExitCode}，耗时 {record.DurationSeconds:0.0} 秒。",
            Detail = $"命令：{record.CommandText}"
        };
}