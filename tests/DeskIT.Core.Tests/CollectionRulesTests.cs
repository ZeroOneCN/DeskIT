using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>采集规则（CollectionRules）的单元测试：可选分组、分组状态归约、快照标识与保留份数、字段可读文本。</summary>
public class CollectionRulesTests
{
    /// <summary>仅账号与环境、已装软件、安全与更新可关闭；系统、硬件、存储、网络为必采项。</summary>
    [Fact]
    public void IsOptional_仅三类分组可关闭()
    {
        Assert.True(CollectionRules.IsOptional(CollectionGroup.AccountEnvironment));
        Assert.True(CollectionRules.IsOptional(CollectionGroup.InstalledSoftware));
        Assert.True(CollectionRules.IsOptional(CollectionGroup.SecurityUpdate));

        Assert.False(CollectionRules.IsOptional(CollectionGroup.System));
        Assert.False(CollectionRules.IsOptional(CollectionGroup.Hardware));
        Assert.False(CollectionRules.IsOptional(CollectionGroup.Storage));
        Assert.False(CollectionRules.IsOptional(CollectionGroup.Network));
    }

    /// <summary>基础采集不含已装软件全量清单；完整采集包含；被关闭的分组一律排除。</summary>
    [Fact]
    public void ResolveActiveGroups_基础采集排除软件清单且尊重单项关闭()
    {
        var basic = CollectionRules.ResolveActiveGroups(CollectionMode.Basic, disabledGroups: null);
        Assert.DoesNotContain(CollectionGroup.InstalledSoftware, basic);
        Assert.Contains(CollectionGroup.System, basic);
        Assert.Equal(6, basic.Count);

        var full = CollectionRules.ResolveActiveGroups(CollectionMode.Full, disabledGroups: null);
        Assert.Contains(CollectionGroup.InstalledSoftware, full);
        Assert.Equal(7, full.Count);

        var disabled = CollectionRules.ResolveActiveGroups(
            CollectionMode.Full,
            new HashSet<CollectionGroup> { CollectionGroup.AccountEnvironment, CollectionGroup.SecurityUpdate });
        Assert.DoesNotContain(CollectionGroup.AccountEnvironment, disabled);
        Assert.DoesNotContain(CollectionGroup.SecurityUpdate, disabled);
        Assert.Contains(CollectionGroup.InstalledSoftware, disabled);
        Assert.Equal(5, disabled.Count);
    }

    /// <summary>分组状态归约：无误记已采集、有超时记超时、有无权限或失败记部分可用、被跳过记已跳过。</summary>
    [Fact]
    public void ResolveGroupStatus_按字段状态归约()
    {
        var ok = new List<CollectedField> { new("CPU", "i7", FieldStatus.Collected) };
        Assert.Equal(CollectionGroupStatus.Collected, CollectionRules.ResolveGroupStatus(ok));

        var notApplicable = new List<CollectedField> { new("电池", null, FieldStatus.NotApplicable) };
        Assert.Equal(CollectionGroupStatus.Collected, CollectionRules.ResolveGroupStatus(notApplicable));

        var timeout = new List<CollectedField>
        {
            new("CPU", "i7", FieldStatus.Collected),
            new("显卡", null, FieldStatus.Timeout)
        };
        Assert.Equal(CollectionGroupStatus.Timeout, CollectionRules.ResolveGroupStatus(timeout));

        var partial = new List<CollectedField>
        {
            new("BitLocker", null, FieldStatus.NoPermission),
            new("防火墙", "已启用", FieldStatus.Collected)
        };
        Assert.Equal(CollectionGroupStatus.Partial, CollectionRules.ResolveGroupStatus(partial));

        Assert.Equal(
            CollectionGroupStatus.Skipped,
            CollectionRules.ResolveGroupStatus(new List<CollectedField>(), skipped: true));
    }

    /// <summary>超时优先于无权限：同一分组既有超时又有无权限时，整体记为超时。</summary>
    [Fact]
    public void ResolveGroupStatus_超时优先于无权限()
    {
        var fields = new List<CollectedField>
        {
            new("A", null, FieldStatus.NoPermission),
            new("B", null, FieldStatus.Timeout)
        };
        Assert.Equal(CollectionGroupStatus.Timeout, CollectionRules.ResolveGroupStatus(fields));
    }

    /// <summary>快照标识包含日期时间前缀且两次生成互不相同。</summary>
    [Fact]
    public void NewSnapshotId_格式与唯一性()
    {
        var at = new DateTimeOffset(2026, 10, 12, 19, 30, 0, TimeSpan.FromHours(8));
        var id = CollectionRules.NewSnapshotId(at, "ab12");
        Assert.Equal("snap-20261012-193000-ab12", id);

        var a = CollectionRules.NewSnapshotId(at);
        var b = CollectionRules.NewSnapshotId(at);
        Assert.NotEqual(a, b);
        Assert.StartsWith("snap-20261012-193000-", a);
    }

    /// <summary>快照保留：不超过 5 份时全部保留，超过时删除最旧的，仅保留最新 5 份。</summary>
    [Fact]
    public void SelectSnapshotIdsToTrim_仅保留最新五份()
    {
        var three = new List<string> { "s1", "s2", "s3" };
        Assert.Empty(CollectionRules.SelectSnapshotIdsToTrim(three));

        var five = new List<string> { "s1", "s2", "s3", "s4", "s5" };
        Assert.Empty(CollectionRules.SelectSnapshotIdsToTrim(five));

        var seven = new List<string> { "s1", "s2", "s3", "s4", "s5", "s6", "s7" };
        var trimmed = CollectionRules.SelectSnapshotIdsToTrim(seven);
        Assert.Equal(new[] { "s1", "s2" }, trimmed);
    }

    /// <summary>字段可读文本：正常取值显示值，其余显示对应状态说明。</summary>
    [Fact]
    public void DescribeField_正常取值显示值其余显示状态()
    {
        Assert.Equal("i7-1165G7", CollectionRules.DescribeField(new("CPU", "i7-1165G7", FieldStatus.Collected)));
        Assert.Equal("无权限读取", CollectionRules.DescribeField(new("BitLocker", null, FieldStatus.NoPermission)));
        Assert.Equal("未知（超时）", CollectionRules.DescribeField(new("显卡", null, FieldStatus.Timeout)));
        Assert.Equal("不适用", CollectionRules.DescribeField(new("电池", null, FieldStatus.NotApplicable)));
        Assert.Equal("未知（读取失败）", CollectionRules.DescribeField(new("SMART", null, FieldStatus.Failed)));
    }

    /// <summary>
    /// 快照与档案必须可经 System.Text.Json 往返序列化：采集结果需落盘到 U 盘数据目录并读回。
    /// </summary>
    [Fact]
    public void 快照与档案可Json往返()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var snapshot = new MachineSnapshot(
            "snap-20261012-193000-ab12",
            "SN-12345",
            MachineIdSource.HardwareSerial,
            "PC-001",
            new DateTimeOffset(2026, 10, 12, 19, 30, 0, TimeSpan.FromHours(8)),
            CollectionMode.Full,
            new List<CollectionGroupResult>
            {
                new(
                    CollectionGroup.System,
                    CollectionGroupStatus.Collected,
                    TimeSpan.FromSeconds(2),
                    new List<CollectedField> { new("计算机名", "PC-001", FieldStatus.Collected) },
                    new List<string>())
            });

        var json = JsonSerializer.Serialize(snapshot, options);
        var restored = JsonSerializer.Deserialize<MachineSnapshot>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.SnapshotId, restored!.SnapshotId);
        Assert.Equal(MachineIdSource.HardwareSerial, restored.MachineIdSource);
        Assert.Equal(CollectionMode.Full, restored.Mode);
        Assert.Single(restored.Groups);
        Assert.Equal("计算机名", restored.Groups[0].Fields[0].Name);

        var profile = new MachineProfile(
            "SN-12345",
            MachineIdSource.HardwareSerial,
            "PC-001",
            snapshot.CollectedAt,
            snapshot.CollectedAt,
            1,
            new List<string> { snapshot.SnapshotId });

        var profileJson = JsonSerializer.Serialize(profile, options);
        var restoredProfile = JsonSerializer.Deserialize<MachineProfile>(profileJson, options);

        Assert.NotNull(restoredProfile);
        Assert.Equal("SN-12345", restoredProfile!.MachineId);
        Assert.Equal(1, restoredProfile.ProcessingCount);
        Assert.Single(restoredProfile.SnapshotIds);
    }
}