using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>一次设备信息采集的请求参数。</summary>
/// <param name="Mode">采集模式（基础 / 完整）。</param>
/// <param name="Privilege">当前权限级别（管理员模式可补齐受保护项）。</param>
/// <param name="DisabledGroups">被 IT 人员单项关闭的可选分组。</param>
/// <param name="ItemTimeout">单采集项超时；超时项记为“未知（超时）”，不阻塞其他项。</param>
public sealed record CollectionRequest(
    CollectionMode Mode,
    PrivilegeLevel Privilege,
    IReadOnlySet<CollectionGroup>? DisabledGroups,
    TimeSpan ItemTimeout);

/// <summary>
/// 设备信息采集器（M2）：以只读方式采集系统、硬件、存储、网络、账号与环境、已装软件、安全与更新共 7 组信息，
/// 结果先写入 U 盘数据目录，不向目标机磁盘写入任何采集产物。
/// </summary>
public interface IDeviceInfoCollector
{
    /// <summary>
    /// 执行一次设备信息采集。
    /// </summary>
    /// <param name="request">采集请求参数。</param>
    /// <param name="progress">采集进度上报（以采集项为单位）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>采集结果（机器标识原始事实 + 各分组结果）。</returns>
    Task<DeviceCollectionResult> CollectAsync(
        CollectionRequest request,
        IProgress<CollectionProgress>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>单个采集项的输出：字段结果（可在界面上直接展示）与明细行（如已装软件清单）。</summary>
/// <param name="Fields">字段结果。</param>
/// <param name="Details">明细行；无明细时为空集合。</param>
internal sealed record CollectionItemOutput(IReadOnlyList<CollectedField> Fields, IReadOnlyList<string> Details)
{
    /// <summary>以单个已采集值构造输出。</summary>
    /// <param name="name">字段名。</param>
    /// <param name="value">字段值。</param>
    /// <returns>采集输出。</returns>
    public static CollectionItemOutput Value(string name, string? value)
        => new(
            new[] { value is null ? new CollectedField(name, null, FieldStatus.NotApplicable) : new CollectedField(name, value, FieldStatus.Collected) },
            Array.Empty<string>());

    /// <summary>以显式状态构造单字段输出。</summary>
    /// <param name="name">字段名。</param>
    /// <param name="value">字段值。</param>
    /// <param name="status">取值状态。</param>
    /// <returns>采集输出。</returns>
    public static CollectionItemOutput Status(string name, string? value, FieldStatus status)
        => new(new[] { new CollectedField(name, value, status) }, Array.Empty<string>());

    /// <summary>以多个字段构造输出。</summary>
    /// <param name="fields">字段结果。</param>
    /// <returns>采集输出。</returns>
    public static CollectionItemOutput Of(params CollectedField[] fields)
        => new(fields, Array.Empty<string>());

    /// <summary>以“摘要字段 + 明细行”构造输出（用于清单类采集项）。</summary>
    /// <param name="name">摘要字段名。</param>
    /// <param name="summary">摘要字段值。</param>
    /// <param name="details">明细行。</param>
    /// <returns>采集输出。</returns>
    public static CollectionItemOutput List(string name, string summary, IEnumerable<string> details)
        => new(
            new[] { new CollectedField(name, summary, FieldStatus.Collected) },
            details.ToList());
}

/// <summary>
/// 一个采集项（M2 的最小采集单位）。每项独立执行、独立超时、独立失败，任一项失败不阻塞其他项。
/// </summary>
/// <param name="Group">所属分组。</param>
/// <param name="Name">采集项名称（面向 IT 人员）。</param>
/// <param name="FieldNames">该项产出的字段名，用于超时或失败时如实标注。</param>
/// <param name="Collect">采集动作。</param>
internal sealed record CollectionItem(
    CollectionGroup Group,
    string Name,
    IReadOnlyList<string> FieldNames,
    Func<CollectionItemOutput> Collect);

/// <summary>采集过程中的共享上下文：机器标识事实与跨项缓存。</summary>
internal sealed class CollectionContext
{
    /// <summary>计算机名。</summary>
    public string HostName { get; } = Environment.MachineName;

    /// <summary>硬件序列号（由“整机标识”采集项填充，供机器标识使用）。</summary>
    public string? HardwareSerial { get; set; }
}

/// <summary>
/// Windows 设备信息采集器实现。分组并行、组内采集项串行；每项带独立超时。
/// 全部查询均为本机只读查询（WMI / 注册表 / 网络接口 / 卷信息），不使用远程协议。
/// </summary>
public sealed class WindowsDeviceInfoCollector : IDeviceInfoCollector
{
    /// <inheritdoc />
    public async Task<DeviceCollectionResult> CollectAsync(
        CollectionRequest request,
        IProgress<CollectionProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var activeGroups = CollectionRules.ResolveActiveGroups(request.Mode, request.DisabledGroups);

        var context = new CollectionContext();
        var allItems = WindowsCollectionItems.Build(context, request.Privilege);
        var selectedItems = allItems.Where(item => activeGroups.Contains(item.Group)).ToList();

        var totalItems = selectedItems.Count;
        var counter = new ProgressCounter();

        // 分组并行、组内串行：既压缩总耗时，又避免同一分组内多项同时发起 WMI 查询。
        var groupTasks = activeGroups
            .Select(group =>
            {
                var groupItems = selectedItems.Where(item => item.Group == group).ToList();
                return Task.Run(
                    () => CollectGroup(group, groupItems, request.ItemTimeout, progress, totalItems, counter, cancellationToken),
                    cancellationToken);
            })
            .ToList();

        var results = await Task.WhenAll(groupTasks).ConfigureAwait(false);

        var facts = new DeviceIdentityFacts(context.HardwareSerial, context.HostName);
        return new DeviceCollectionResult(facts, results);
    }

    /// <summary>
    /// 采集单个分组：组内逐项执行并上报进度。
    /// </summary>
    /// <param name="group">分组标识。</param>
    /// <param name="items">该分组的采集项。</param>
    /// <param name="itemTimeout">单项超时。</param>
    /// <param name="progress">进度上报。</param>
    /// <param name="totalItems">本次采集项总数。</param>
    /// <param name="counter">已完成项计数。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>分组采集结果。</returns>
    private static CollectionGroupResult CollectGroup(
        CollectionGroup group,
        IReadOnlyList<CollectionItem> items,
        TimeSpan itemTimeout,
        IProgress<CollectionProgress>? progress,
        int totalItems,
        ProgressCounter counter,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var fields = new List<CollectedField>();
        var details = new List<string>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var output = RunItem(item, itemTimeout);
            fields.AddRange(output.Fields);
            details.AddRange(output.Details);

            progress?.Report(new CollectionProgress(group, item.Name, counter.Next(), totalItems));
        }

        stopwatch.Stop();

        return new CollectionGroupResult(
            group,
            CollectionRules.ResolveGroupStatus(fields),
            stopwatch.Elapsed,
            fields,
            details);
    }

    /// <summary>
    /// 执行单个采集项，按超时与异常如实归因，不向调用方抛出异常。
    /// </summary>
    /// <param name="item">采集项。</param>
    /// <param name="timeout">单项超时。</param>
    /// <returns>采集输出；超时或失败时按字段名标注状态。</returns>
    private static CollectionItemOutput RunItem(CollectionItem item, TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(item.Collect);
            if (!task.Wait(timeout))
            {
                return Fallback(item, FieldStatus.Timeout);
            }

            return task.Result;
        }
        catch (AggregateException aggregate) when (aggregate.InnerException is not null)
        {
            return Fallback(item, ClassifyException(aggregate.InnerException));
        }
        catch (Exception ex)
        {
            return Fallback(item, ClassifyException(ex));
        }
    }

    /// <summary>
    /// 把采集异常归类为字段状态：权限拒绝记为“无权限读取”，对象缺失记为“不适用”，其余记为“未知（读取失败）”。
    /// </summary>
    /// <param name="exception">采集异常。</param>
    /// <returns>字段状态。</returns>
    private static FieldStatus ClassifyException(Exception exception)
    {
        if (AccessDenial.IsDenied(exception))
        {
            return FieldStatus.NoPermission;
        }

        return AccessDenial.IsNotApplicable(exception) ? FieldStatus.NotApplicable : FieldStatus.Failed;
    }

    /// <summary>为采集项的每个字段生成统一的失败结果。</summary>
    /// <param name="item">采集项。</param>
    /// <param name="status">字段状态。</param>
    /// <returns>采集输出。</returns>
    private static CollectionItemOutput Fallback(CollectionItem item, FieldStatus status)
        => new(
            item.FieldNames.Select(name => new CollectedField(name, null, status)).ToList(),
            Array.Empty<string>());

    /// <summary>采集项完成计数（跨分组并行时保证进度序号唯一）。</summary>
    private sealed class ProgressCounter
    {
        private int _value;

        /// <summary>递增并返回新的已完成项数量。</summary>
        /// <returns>已完成项数量。</returns>
        public int Next() => Interlocked.Increment(ref _value);
    }
}