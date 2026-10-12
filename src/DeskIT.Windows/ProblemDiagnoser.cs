using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>一次问题诊断的请求参数。</summary>
/// <param name="Scenes">本次要诊断的场景（按展示顺序）。</param>
/// <param name="Privilege">当前权限级别；只读模式下需要管理员的诊断项标记为“未执行（需要管理员权限）”。</param>
/// <param name="ItemTimeout">单项诊断超时；超时项记“未知（超时）”，不阻塞其他项。</param>
/// <param name="DataDirectory">U 盘数据目录（用于读取历史快照做趋势比较）。</param>
public sealed record DiagnosisRequest(
    IReadOnlyList<DiagnosisScene> Scenes,
    PrivilegeLevel Privilege,
    TimeSpan ItemTimeout,
    string DataDirectory);

/// <summary>
/// 场景化问题诊断器（M3）：把员工口述的模糊现象转换成有四态结论的诊断项。
/// </summary>
public interface IProblemDiagnoser
{
    /// <summary>
    /// 执行一次问题诊断。
    /// </summary>
    /// <param name="request">诊断请求参数。</param>
    /// <param name="progress">诊断进度上报（以诊断项为单位）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>诊断报告（含各场景结论与统计）。</returns>
    Task<DiagnosisReport> DiagnoseAsync(
        DiagnosisRequest request,
        IProgress<DiagnosisProgress>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 单个诊断项的输出：四态结论、依据说明、原始依据与建议动作。
/// </summary>
/// <param name="Conclusion">四态结论。</param>
/// <param name="Reason">结论依据说明。</param>
/// <param name="Evidence">原始依据清单。</param>
/// <param name="SuggestedActionIds">建议动作标识（关联 M4，仅为建议）。</param>
internal sealed record DiagnosisItemOutput(
    DiagnosisConclusion Conclusion,
    string Reason,
    IReadOnlyList<DiagnosisEvidence> Evidence,
    IReadOnlyList<string> SuggestedActionIds)
{
    /// <summary>构造“正常”结论。</summary>
    /// <param name="reason">依据说明。</param>
    /// <param name="evidence">原始依据。</param>
    /// <returns>诊断项输出。</returns>
    public static DiagnosisItemOutput Normal(string reason, params DiagnosisEvidence[] evidence)
        => new(DiagnosisConclusion.Normal, reason, evidence, Array.Empty<string>());

    /// <summary>构造“异常”结论。</summary>
    /// <param name="reason">依据说明。</param>
    /// <param name="evidence">原始依据。</param>
    /// <returns>诊断项输出。</returns>
    public static DiagnosisItemOutput Abnormal(string reason, params DiagnosisEvidence[] evidence)
        => new(DiagnosisConclusion.Abnormal, reason, evidence, Array.Empty<string>());

    /// <summary>构造“提示”结论。</summary>
    /// <param name="reason">依据说明。</param>
    /// <param name="evidence">原始依据。</param>
    /// <returns>诊断项输出。</returns>
    public static DiagnosisItemOutput Hint(string reason, params DiagnosisEvidence[] evidence)
        => new(DiagnosisConclusion.Hint, reason, evidence, Array.Empty<string>());

    /// <summary>构造“未知”结论（无法判定时必须使用，并说明原因）。</summary>
    /// <param name="reason">无法判定的原因。</param>
    /// <param name="evidence">原始依据。</param>
    /// <returns>诊断项输出。</returns>
    public static DiagnosisItemOutput Unknown(string reason, params DiagnosisEvidence[] evidence)
        => new(DiagnosisConclusion.Unknown, reason, evidence, Array.Empty<string>());

    /// <summary>附加建议动作标识。</summary>
    /// <param name="actionIds">建议动作标识。</param>
    /// <returns>带建议动作的诊断项输出。</returns>
    public DiagnosisItemOutput WithActions(params string[] actionIds) => this with { SuggestedActionIds = actionIds };
}

/// <summary>
/// 一个诊断项（M3 的最小执行单位）。每项独立执行、独立超时、独立失败，任一项失败不阻塞其他项。
/// </summary>
/// <param name="Scene">所属场景。</param>
/// <param name="FindingId">诊断项稳定标识。</param>
/// <param name="Name">诊断项名称。</param>
/// <param name="RequiresAdministrator">是否需要管理员权限；只读模式下该项标记为未执行。</param>
/// <param name="Run">诊断动作。</param>
/// <param name="Timeout">该项专用超时；为 null 时使用请求的单项超时。</param>
internal sealed record DiagnosisItem(
    DiagnosisScene Scene,
    string FindingId,
    string Name,
    bool RequiresAdministrator,
    Func<DiagnosisItemOutput> Run,
    TimeSpan? Timeout = null);

/// <summary>诊断过程中的共享上下文：数据目录、历史快照与 PowerShell 执行器。</summary>
internal sealed class DiagnosisContext
{
    /// <summary>U 盘数据目录。</summary>
    public required string DataDirectory { get; init; }

    /// <summary>计算机名。</summary>
    public string HostName { get; } = Environment.MachineName;

    /// <summary>当前机器的历史快照（按时间由旧到新）；无历史时为空集合。</summary>
    public IReadOnlyList<MachineSnapshot> RecentSnapshots { get; init; } = Array.Empty<MachineSnapshot>();

    /// <summary>Windows PowerShell 5.1 执行器（用于事件日志、计划任务等只读查询）。</summary>
    public IPowerShellExecutor PowerShell { get; init; } = new PowerShell51Executor();
}

/// <summary>
/// Windows 问题诊断器实现。场景并行、场景内诊断项串行；每项带独立超时。
/// 全部检查均为本机只读检查（WMI / 注册表 / 网络接口 / 系统内置只读命令 / PowerShell 只读查询），不发起外网调用。
/// </summary>
public sealed class WindowsProblemDiagnoser : IProblemDiagnoser
{
    private readonly ISnapshotStore _store;
    private readonly IPowerShellExecutor _powerShell;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 初始化问题诊断器。
    /// </summary>
    /// <param name="store">档案与快照存储；为 null 时使用文件实现。</param>
    /// <param name="powerShell">PowerShell 执行器；为 null 时使用 5.1 实现。</param>
    /// <param name="clock">时间源，便于测试；为 null 时使用本地当前时间。</param>
    public WindowsProblemDiagnoser(
        ISnapshotStore? store = null,
        IPowerShellExecutor? powerShell = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? new SnapshotStore();
        _powerShell = powerShell ?? new PowerShell51Executor();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public async Task<DiagnosisReport> DiagnoseAsync(
        DiagnosisRequest request,
        IProgress<DiagnosisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var context = BuildContext(request);
        var allItems = WindowsDiagnosisItems.Build(context);
        var totalItems = allItems.Count(item => request.Scenes.Contains(item.Scene));
        var counter = new ProgressCounter();
        var startedAt = _clock();

        // 场景并行、场景内串行：既压缩总耗时，又避免同一场景内多项同时发起 WMI 或事件日志查询。
        var sceneTasks = request.Scenes
            .Select(scene =>
            {
                var sceneItems = allItems.Where(item => item.Scene == scene).ToList();
                return Task.Run(
                    () => RunScene(scene, sceneItems, request, progress, totalItems, counter, cancellationToken),
                    cancellationToken);
            })
            .ToList();

        var scenes = await Task.WhenAll(sceneTasks).ConfigureAwait(false);

        return new DiagnosisReport(scenes, startedAt, _clock());
    }

    /// <summary>
    /// 构造诊断上下文：解析当前机器的历史快照（按计算机名匹配既有档案，避免额外 WMI 查询）。
    /// </summary>
    /// <param name="request">诊断请求。</param>
    /// <returns>诊断上下文。</returns>
    private DiagnosisContext BuildContext(DiagnosisRequest request)
    {
        var snapshots = Array.Empty<MachineSnapshot>();

        try
        {
            var profile = _store.LoadAllProfiles(request.DataDirectory)
                .FirstOrDefault(p => string.Equals(p.HostName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

            if (profile is not null)
            {
                snapshots = _store.LoadSnapshots(request.DataDirectory, profile.MachineId).ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 历史快照不可读不影响诊断：趋势类诊断项将如实记为“未知”。
        }

        return new DiagnosisContext
        {
            DataDirectory = request.DataDirectory,
            RecentSnapshots = snapshots,
            PowerShell = _powerShell
        };
    }

    /// <summary>
    /// 执行单个场景：场景内逐项执行并上报进度。
    /// </summary>
    /// <param name="scene">场景标识。</param>
    /// <param name="items">该场景的诊断项。</param>
    /// <param name="request">诊断请求。</param>
    /// <param name="progress">进度上报。</param>
    /// <param name="totalItems">本次诊断项总数。</param>
    /// <param name="counter">已完成项计数。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>场景诊断结果。</returns>
    private static DiagnosisSceneResult RunScene(
        DiagnosisScene scene,
        IReadOnlyList<DiagnosisItem> items,
        DiagnosisRequest request,
        IProgress<DiagnosisProgress>? progress,
        int totalItems,
        ProgressCounter counter,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var findings = new List<DiagnosisFinding>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(RunItem(item, request.Privilege, request.ItemTimeout));
            progress?.Report(new DiagnosisProgress(scene, item.Name, counter.Next(), totalItems));
        }

        stopwatch.Stop();
        return new DiagnosisSceneResult(scene, findings, stopwatch.Elapsed);
    }

    /// <summary>
    /// 执行单个诊断项：只读模式下跳过需管理员项；按超时与异常如实归因，不向调用方抛出异常。
    /// </summary>
    /// <param name="item">诊断项。</param>
    /// <param name="privilege">当前权限级别。</param>
    /// <param name="defaultTimeout">默认单项超时。</param>
    /// <returns>诊断结论。</returns>
    private static DiagnosisFinding RunItem(DiagnosisItem item, PrivilegeLevel privilege, TimeSpan defaultTimeout)
    {
        if (item.RequiresAdministrator && privilege != PrivilegeLevel.Administrator)
        {
            return new DiagnosisFinding(
                item.FindingId,
                item.Scene,
                item.Name,
                DiagnosisExecutionStatus.SkippedNeedsAdministrator,
                DiagnosisConclusion.Unknown,
                "未执行（需要管理员权限）：该项需要读取受保护数据，只读模式下跳过。",
                Array.Empty<DiagnosisEvidence>(),
                Array.Empty<string>(),
                TimeSpan.Zero);
        }

        var timeout = item.Timeout ?? defaultTimeout;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var task = Task.Run(item.Run);
            if (!task.Wait(timeout))
            {
                return Failure(item, DiagnosisExecutionStatus.Executed, DiagnosisConclusion.Unknown,
                    $"未知（超时）：该项在 {timeout.TotalSeconds:0} 秒内未返回结果。", Array.Empty<DiagnosisEvidence>(), stopwatch.Elapsed);
            }

            var output = task.Result;
            return new DiagnosisFinding(
                item.FindingId,
                item.Scene,
                item.Name,
                DiagnosisExecutionStatus.Executed,
                output.Conclusion,
                output.Reason,
                output.Evidence,
                output.SuggestedActionIds,
                stopwatch.Elapsed);
        }
        catch (AggregateException aggregate) when (aggregate.InnerException is not null)
        {
            return Classify(item, aggregate.InnerException, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return Classify(item, ex, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// 把诊断异常归类为结论：权限拒绝记为“未知（无权限）”，对象缺失记为“不适用”，其余记为“未知（检查失败）”。
    /// </summary>
    /// <param name="item">诊断项。</param>
    /// <param name="exception">诊断异常。</param>
    /// <param name="elapsed">已耗时。</param>
    /// <returns>诊断结论。</returns>
    private static DiagnosisFinding Classify(DiagnosisItem item, Exception exception, TimeSpan elapsed)
    {
        if (AccessDenial.IsDenied(exception))
        {
            return Failure(item, DiagnosisExecutionStatus.Executed, DiagnosisConclusion.Unknown,
                "未知（无权限）：无法读取该项所需的数据（权限或策略拒绝）。", Array.Empty<DiagnosisEvidence>(), elapsed);
        }

        if (AccessDenial.IsNotApplicable(exception))
        {
            return Failure(item, DiagnosisExecutionStatus.NotApplicable, DiagnosisConclusion.Unknown,
                "不适用：目标系统不存在该项所需的对象或组件。", Array.Empty<DiagnosisEvidence>(), elapsed);
        }

        return Failure(item, DiagnosisExecutionStatus.Executed, DiagnosisConclusion.Unknown,
            $"未知（检查失败）：{exception.Message}", Array.Empty<DiagnosisEvidence>(), elapsed);
    }

    /// <summary>构造一条失败/跳过结论。</summary>
    /// <param name="item">诊断项。</param>
    /// <param name="status">执行状态。</param>
    /// <param name="conclusion">结论。</param>
    /// <param name="reason">原因。</param>
    /// <param name="evidence">依据。</param>
    /// <param name="elapsed">耗时。</param>
    /// <returns>诊断结论。</returns>
    private static DiagnosisFinding Failure(
        DiagnosisItem item,
        DiagnosisExecutionStatus status,
        DiagnosisConclusion conclusion,
        string reason,
        IReadOnlyList<DiagnosisEvidence> evidence,
        TimeSpan elapsed)
        => new(item.FindingId, item.Scene, item.Name, status, conclusion, reason, evidence, Array.Empty<string>(), elapsed);

    /// <summary>诊断项完成计数（跨场景并行时保证进度序号唯一）。</summary>
    private sealed class ProgressCounter
    {
        private int _value;

        /// <summary>递增并返回新的已完成项数量。</summary>
        /// <returns>已完成项数量。</returns>
        public int Next() => Interlocked.Increment(ref _value);
    }
}