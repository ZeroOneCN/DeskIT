using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>一次采集并入库的结果：本次快照与档案归并信息。</summary>
/// <param name="Snapshot">本次采集生成的快照。</param>
/// <param name="Profile">档案归并结果（含“此前处理过 N 次”提示）。</param>
public sealed record DeviceCollectionOutcome(MachineSnapshot Snapshot, MachineProfileMerge Profile);

/// <summary>
/// 设备信息采集编排（M2）：采集 → 解析机器标识 → 归并档案 → 落盘（含快照保留）。
/// </summary>
public interface IDeviceCollectionService
{
    /// <summary>
    /// 采集设备信息并写入 U 盘数据目录。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="request">采集请求参数。</param>
    /// <param name="progress">采集进度上报。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>采集与归并结果。</returns>
    Task<DeviceCollectionOutcome> CollectAndStoreAsync(
        string dataDirectory,
        CollectionRequest request,
        IProgress<CollectionProgress>? progress,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDeviceCollectionService" />
public sealed class DeviceCollectionService : IDeviceCollectionService
{
    private readonly IDeviceInfoCollector _collector;
    private readonly ISnapshotStore _store;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 初始化采集编排服务。
    /// </summary>
    /// <param name="collector">设备信息采集器；为 null 时使用 Windows 实现。</param>
    /// <param name="store">档案与快照存储；为 null 时使用文件实现。</param>
    /// <param name="clock">时间源，便于测试；为 null 时使用本地当前时间。</param>
    public DeviceCollectionService(
        IDeviceInfoCollector? collector = null,
        ISnapshotStore? store = null,
        Func<DateTimeOffset>? clock = null)
    {
        _collector = collector ?? new WindowsDeviceInfoCollector();
        _store = store ?? new SnapshotStore();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public async Task<DeviceCollectionOutcome> CollectAndStoreAsync(
        string dataDirectory,
        CollectionRequest request,
        IProgress<CollectionProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var collected = await _collector.CollectAsync(request, progress, cancellationToken).ConfigureAwait(false);

        var now = _clock();
        var profiles = _store.LoadAllProfiles(dataDirectory);

        // 机器标识：硬件序列号优先；不可用时回退，并复用同一主机名的既有回退档案。
        var identity = MachineIdentityRules.ResolveIdentity(collected.Facts, profiles, now);

        var snapshot = new MachineSnapshot(
            CollectionRules.NewSnapshotId(now),
            identity.MachineId,
            identity.Source,
            collected.Facts.HostName,
            now,
            request.Mode,
            collected.Groups);

        var existing = profiles.FirstOrDefault(profile => profile.MachineId == identity.MachineId);
        var merge = MachineIdentityRules.Merge(existing, snapshot);

        _store.Save(dataDirectory, merge.Profile, snapshot);

        return new DeviceCollectionOutcome(snapshot, merge);
    }
}