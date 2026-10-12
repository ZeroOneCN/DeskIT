using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>一次诊断并入库的结果：诊断会话记录与诊断报告。</summary>
/// <param name="Session">诊断会话记录（含状态机推进后的最终状态）。</param>
/// <param name="Report">诊断报告（含各场景结论与统计）。</param>
public sealed record ProblemDiagnosisOutcome(DiagnosisSession Session, DiagnosisReport Report);

/// <summary>
/// 问题诊断编排（M3）：推进会话状态机 → 执行诊断 → 把结论与依据写入 U 盘会话记录。
/// </summary>
public interface IProblemDiagnosisService
{
    /// <summary>
    /// 执行一次问题诊断并写入会话记录。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="request">诊断请求参数。</param>
    /// <param name="progress">诊断进度上报。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>诊断与入库结果。</returns>
    Task<ProblemDiagnosisOutcome> DiagnoseAndStoreAsync(
        string dataDirectory,
        DiagnosisRequest request,
        IProgress<DiagnosisProgress>? progress,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IProblemDiagnosisService" />
public sealed class ProblemDiagnosisService : IProblemDiagnosisService
{
    private readonly IProblemDiagnoser _diagnoser;
    private readonly IDiagnosisStore _store;
    private readonly ISnapshotStore _snapshotStore;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 初始化诊断编排服务。
    /// </summary>
    /// <param name="diagnoser">问题诊断器；为 null 时使用 Windows 实现。</param>
    /// <param name="store">会话存储；为 null 时使用文件实现。</param>
    /// <param name="snapshotStore">档案与快照存储（用于把会话归并到目标机档案）；为 null 时使用文件实现。</param>
    /// <param name="clock">时间源，便于测试；为 null 时使用本地当前时间。</param>
    public ProblemDiagnosisService(
        IProblemDiagnoser? diagnoser = null,
        IDiagnosisStore? store = null,
        ISnapshotStore? snapshotStore = null,
        Func<DateTimeOffset>? clock = null)
    {
        _diagnoser = diagnoser ?? new WindowsProblemDiagnoser();
        _store = store ?? new DiagnosisStore();
        _snapshotStore = snapshotStore ?? new SnapshotStore();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public async Task<ProblemDiagnosisOutcome> DiagnoseAndStoreAsync(
        string dataDirectory,
        DiagnosisRequest request,
        IProgress<DiagnosisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var createdAt = _clock();

        // 状态机：新建 → 诊断中 → 已结束。非法迁移立即暴露为错误，避免状态被静默破坏。
        var state = Advance(SessionState.New, SessionState.Diagnosing);

        var report = await _diagnoser.DiagnoseAsync(request, progress, cancellationToken).ConfigureAwait(false);

        state = Advance(state, SessionState.Closed);

        var session = new DiagnosisSession(
            DiagnosisRules.NewSessionId(createdAt),
            ResolveMachineId(dataDirectory),
            Environment.MachineName,
            state,
            createdAt,
            _clock(),
            report.Scenes);

        _store.Save(dataDirectory, session);

        return new ProblemDiagnosisOutcome(session, report);
    }

    /// <summary>
    /// 按计算机名匹配既有档案，得到本次会话所属的机器标识；无档案时返回空串（表示尚未采集）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>机器标识；无档案时为空串。</returns>
    private string ResolveMachineId(string dataDirectory)
    {
        try
        {
            var profile = _snapshotStore.LoadAllProfiles(dataDirectory)
                .FirstOrDefault(p => string.Equals(p.HostName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
            return profile?.MachineId ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 推进会话状态；非法迁移抛出异常以便尽早发现接线错误。
    /// </summary>
    /// <param name="from">当前状态。</param>
    /// <param name="to">目标状态。</param>
    /// <returns>目标状态。</returns>
    private static SessionState Advance(SessionState from, SessionState to)
    {
        if (!SessionStateRules.CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"非法的会话状态迁移：{SessionStateRules.Describe(from)} → {SessionStateRules.Describe(to)}。");
        }

        return to;
    }
}