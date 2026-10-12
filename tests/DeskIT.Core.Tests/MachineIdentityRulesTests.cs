using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>机器标识与档案归并规则（MachineIdentityRules）的单元测试。</summary>
public class MachineIdentityRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 19, 30, 0, TimeSpan.FromHours(8));

    /// <summary>有效序列号直接使用；空白、占位串、全同字符与过短值一律视为不可用。</summary>
    [Fact]
    public void NormalizeHardwareSerial_剔除占位串与无效值()
    {
        Assert.Equal("PF2ABC12", MachineIdentityRules.NormalizeHardwareSerial("  PF2ABC12 "));
        Assert.Equal("SN-99881", MachineIdentityRules.NormalizeHardwareSerial("SN-99881"));

        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial(null));
        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial("   "));
        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial("To be filled by O.E.M."));
        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial("Default string"));
        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial("System Serial Number"));
        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial("00000000"));
        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial("0"));
        Assert.Null(MachineIdentityRules.NormalizeHardwareSerial("AB"));
    }

    /// <summary>序列号可用时机器标识取序列号，来源为硬件序列号。</summary>
    [Fact]
    public void ResolveIdentity_优先使用硬件序列号()
    {
        var identity = MachineIdentityRules.ResolveIdentity(
            new DeviceIdentityFacts("PF2ABC12", "PC-001"),
            existingProfiles: null,
            Now);

        Assert.Equal("PF2ABC12", identity.MachineId);
        Assert.Equal(MachineIdSource.HardwareSerial, identity.Source);
    }

    /// <summary>序列号不可用时回退为“主机名 + 首次采集时间”，来源标注为回退。</summary>
    [Fact]
    public void ResolveIdentity_序列号不可用时回退并标注来源()
    {
        var identity = MachineIdentityRules.ResolveIdentity(
            new DeviceIdentityFacts("To be filled by O.E.M.", "PC-002"),
            existingProfiles: null,
            Now);

        Assert.Equal("PC-002|20261012193000", identity.MachineId);
        Assert.Equal(MachineIdSource.Fallback, identity.Source);
    }

    /// <summary>回退标识必须复用同一主机名的既有档案，否则同一台机器会被重复建档。</summary>
    [Fact]
    public void ResolveIdentity_回退时复用同主机名既有档案()
    {
        var existing = new MachineProfile(
            "PC-002|20250101090000",
            MachineIdSource.Fallback,
            "PC-002",
            new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.FromHours(8)),
            1,
            new List<string> { "snap-20250101-090000-aaaa" });

        var identity = MachineIdentityRules.ResolveIdentity(
            new DeviceIdentityFacts(null, "pc-002"),
            new[] { existing },
            Now);

        Assert.Equal("PC-002|20250101090000", identity.MachineId);
        Assert.Equal(MachineIdSource.Fallback, identity.Source);
    }

    /// <summary>首次识别新建档案：处理次数为 1，并提示“首次识别”。</summary>
    [Fact]
    public void Merge_首次识别新建档案()
    {
        var snapshot = MakeSnapshot("snap-20261012-193000-ab12");

        var merge = MachineIdentityRules.Merge(null, snapshot);

        Assert.True(merge.IsNewMachine);
        Assert.Equal(0, merge.PreviousProcessingCount);
        Assert.Equal(1, merge.Profile.ProcessingCount);
        Assert.Equal(snapshot.CollectedAt, merge.Profile.FirstCollectedAt);
        Assert.Single(merge.Profile.SnapshotIds);
        Assert.Contains("首次识别", merge.Notice);
    }

    /// <summary>再次采集归并既有档案：处理次数累加、追加快照、保留首次采集时间，并提示此前处理次数。</summary>
    [Fact]
    public void Merge_归并既有档案并累加次数()
    {
        var first = MachineIdentityRules.Merge(null, MakeSnapshot("snap-20261012-193000-ab12")).Profile;
        var second = MakeSnapshot("snap-20261012-194500-cd34");

        var merge = MachineIdentityRules.Merge(first, second);

        Assert.False(merge.IsNewMachine);
        Assert.Equal(1, merge.PreviousProcessingCount);
        Assert.Equal(2, merge.Profile.ProcessingCount);
        Assert.Equal(first.FirstCollectedAt, merge.Profile.FirstCollectedAt);
        Assert.Equal(second.CollectedAt, merge.Profile.LastCollectedAt);
        Assert.Equal(new[] { "snap-20261012-193000-ab12", "snap-20261012-194500-cd34" }, merge.Profile.SnapshotIds);
        Assert.Contains("此前处理过 1 次", merge.Notice);
    }

    /// <summary>重复归并同一快照标识不应重复计入处理次数。</summary>
    [Fact]
    public void Merge_同一快照不重复追加快照标识()
    {
        var snapshot = MakeSnapshot("snap-20261012-193000-ab12");
        var first = MachineIdentityRules.Merge(null, snapshot).Profile;

        var merge = MachineIdentityRules.Merge(first, snapshot);

        Assert.Single(merge.Profile.SnapshotIds);
        Assert.Equal(2, merge.Profile.ProcessingCount);
    }

    /// <summary>构造一台机器的测试快照。</summary>
    /// <param name="snapshotId">快照标识。</param>
    /// <returns>含单个分组的快照。</returns>
    private static MachineSnapshot MakeSnapshot(string snapshotId) => new(
        snapshotId,
        "SN-12345",
        MachineIdSource.HardwareSerial,
        "PC-001",
        new DateTimeOffset(2026, 10, 12, 19, 30, 0, TimeSpan.FromHours(8)),
        CollectionMode.Basic,
        new List<CollectionGroupResult>
        {
            new(
                CollectionGroup.System,
                CollectionGroupStatus.Collected,
                TimeSpan.FromSeconds(1),
                new List<CollectedField> { new("计算机名", "PC-001", FieldStatus.Collected) },
                new List<string>())
        });
}