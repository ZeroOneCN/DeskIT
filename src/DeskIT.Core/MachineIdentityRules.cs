namespace DeskIT.Core;

/// <summary>
/// 机器标识与档案归并的可单元测试纯规则。
/// 机器标识优先取硬件序列号类信息；不可用（虚拟机、部分 OEM 机型常写入占位串）时
/// 回退为“主机名 + 首次采集时间”，并在档案中标注标识来源。
/// </summary>
public static class MachineIdentityRules
{
    /// <summary>主板/整机厂商常见的无意义序列号占位串，命中即视为不可用。</summary>
    private static readonly HashSet<string> PlaceholderSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "to be filled by o.e.m.",
        "to be filled by oem",
        "default string",
        "system serial number",
        "system serial",
        "serial number",
        "base board serial number",
        "none",
        "not specified",
        "not applicable",
        "n/a",
        "na",
        "unknown",
        "invalid",
        "oem",
        "filled by oem",
        "chassis serial number",
        "0",
        "1",
        "00000000",
        "0000000000",
        "ffffffff",
        "0123456789"
    };

    /// <summary>
    /// 规范化硬件序列号：去空白、剔除占位串与明显无效值。
    /// </summary>
    /// <param name="raw">原始序列号。</param>
    /// <returns>可用于标识的序列号；不可用时返回 null。</returns>
    public static string? NormalizeHardwareSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Length < 3 || PlaceholderSerials.Contains(value))
        {
            return null;
        }

        // 全为同一字符（如 000000）同样视为无效。
        if (value.Distinct().Count() == 1)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// 确定机器标识：硬件序列号可用时直接使用；否则回退，回退时优先复用同一主机名的既有回退档案，
    /// 以保证同一台机器多次采集归并到同一档案。
    /// </summary>
    /// <param name="facts">机器标识原始事实。</param>
    /// <param name="existingProfiles">既有档案（用于回退标识的复用匹配）。</param>
    /// <param name="collectedAt">本次采集时间（新建回退标识时作为“首次采集时间”）。</param>
    /// <returns>机器标识及其来源。</returns>
    public static MachineIdentity ResolveIdentity(
        DeviceIdentityFacts facts,
        IEnumerable<MachineProfile>? existingProfiles,
        DateTimeOffset collectedAt)
    {
        var serial = NormalizeHardwareSerial(facts.HardwareSerial);
        if (serial is not null)
        {
            return new MachineIdentity(serial, MachineIdSource.HardwareSerial);
        }

        var hostName = string.IsNullOrWhiteSpace(facts.HostName) ? "unknown-host" : facts.HostName.Trim();

        var reuse = existingProfiles?.FirstOrDefault(p =>
            p.MachineIdSource == MachineIdSource.Fallback &&
            string.Equals(p.HostName, hostName, StringComparison.OrdinalIgnoreCase));

        if (reuse is not null)
        {
            return new MachineIdentity(reuse.MachineId, MachineIdSource.Fallback);
        }

        return new MachineIdentity($"{hostName}|{collectedAt:yyyyMMddHHmmss}", MachineIdSource.Fallback);
    }

    /// <summary>
    /// 把一次快照归并进档案：已存在则累加处理次数并追加快照，不存在则新建档案。
    /// </summary>
    /// <param name="existing">既有档案；首次识别时为 null。</param>
    /// <param name="snapshot">本次采集生成的快照。</param>
    /// <returns>归并结果（归并后的档案、是否新建、归并前的处理次数）。</returns>
    public static MachineProfileMerge Merge(MachineProfile? existing, MachineSnapshot snapshot)
    {
        if (existing is null)
        {
            var created = new MachineProfile(
                snapshot.MachineId,
                snapshot.MachineIdSource,
                snapshot.HostName,
                snapshot.CollectedAt,
                snapshot.CollectedAt,
                ProcessingCount: 1,
                SnapshotIds: new List<string> { snapshot.SnapshotId });

            return new MachineProfileMerge(created, IsNewMachine: true, PreviousProcessingCount: 0);
        }

        var snapshotIds = existing.SnapshotIds.ToList();
        if (!snapshotIds.Contains(snapshot.SnapshotId))
        {
            snapshotIds.Add(snapshot.SnapshotId);
        }

        var merged = existing with
        {
            HostName = snapshot.HostName,
            LastCollectedAt = snapshot.CollectedAt,
            ProcessingCount = existing.ProcessingCount + 1,
            SnapshotIds = snapshotIds
        };

        return new MachineProfileMerge(merged, IsNewMachine: false, PreviousProcessingCount: existing.ProcessingCount);
    }
}