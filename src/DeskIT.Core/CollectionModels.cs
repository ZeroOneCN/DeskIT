namespace DeskIT.Core;

/// <summary>采集模式。</summary>
public enum CollectionMode
{
    /// <summary>基础采集：启动流程自动触发，默认 ≤ 60 秒，不含已装软件全量清单。</summary>
    Basic = 0,

    /// <summary>完整采集：由 IT 人员手动触发，含已装软件全量清单，耗时更长。</summary>
    Full = 1
}

/// <summary>采集分组，对应 PRD M2“采集内容与字段”表。</summary>
public enum CollectionGroup
{
    /// <summary>系统：计算机名、系统版本、安装日期、架构、时区、激活状态、最近更新日期。</summary>
    System = 0,

    /// <summary>硬件：厂商与型号、序列号、CPU、内存与插槽占用、显卡、电池健康。</summary>
    Hardware = 1,

    /// <summary>存储：各分区容量/可用空间/文件系统、磁盘类型、SMART 健康摘要。</summary>
    Storage = 2,

    /// <summary>网络：网卡与状态、IP/DNS/网关、代理、VPN 适配器、域或工作组、Wi-Fi 网络名。</summary>
    Network = 3,

    /// <summary>账号与环境：当前登录用户、本地账号、管理员组成员、UAC 级别、是否加入域。</summary>
    AccountEnvironment = 4,

    /// <summary>已装软件：名称、版本、发布者、安装日期、安装位置。</summary>
    InstalledSoftware = 5,

    /// <summary>安全与更新：防病毒与实时防护、BitLocker、防火墙、更新服务、待安装更新数。</summary>
    SecurityUpdate = 6
}

/// <summary>单个采集字段（或采集项）的取值状态。</summary>
public enum FieldStatus
{
    /// <summary>已正常取得。</summary>
    Collected = 0,

    /// <summary>无权限读取（策略或权限拒绝），不中断其他项。</summary>
    NoPermission = 1,

    /// <summary>未知（超时）：该项超出规定时限未返回。</summary>
    Timeout = 2,

    /// <summary>不适用：目标机不存在该对象（如台式机无电池、系统无该组件）。</summary>
    NotApplicable = 3,

    /// <summary>未知（读取失败）：查询出错但无法归为权限或超时。</summary>
    Failed = 4
}

/// <summary>采集分组的整体状态。</summary>
public enum CollectionGroupStatus
{
    /// <summary>已采集：全部字段正常取得。</summary>
    Collected = 0,

    /// <summary>部分可用：存在无权限或读取失败的字段，其余仍可用。</summary>
    Partial = 1,

    /// <summary>已跳过：由 IT 人员关闭该分组，或分组整体不适用。</summary>
    Skipped = 2,

    /// <summary>超时：分组内至少有一项超时未返回。</summary>
    Timeout = 3
}

/// <summary>机器标识来源。硬件序列号不可得时必须回退，并在档案中标注。</summary>
public enum MachineIdSource
{
    /// <summary>硬件序列号类标识（首选）。</summary>
    HardwareSerial = 0,

    /// <summary>回退标识：主机名 + 首次采集时间（虚拟机或部分 OEM 机型无可用序列号）。</summary>
    Fallback = 1
}

/// <summary>
/// 一个采集项的取值结果。字段名与值均面向 IT 人员可读；未取到值时以状态如实说明原因。
/// </summary>
/// <param name="Name">字段名称，如“序列号”“内存容量”。</param>
/// <param name="Value">字段值；未取到时为 null。</param>
/// <param name="Status">取值状态。</param>
public sealed record CollectedField(string Name, string? Value, FieldStatus Status)
{
    /// <summary>该字段是否可直接用于诊断与对比。</summary>
    public bool IsUsable => Status == FieldStatus.Collected && !string.IsNullOrWhiteSpace(Value);
}

/// <summary>一个采集分组的采集结果。</summary>
/// <param name="Group">分组标识。</param>
/// <param name="Status">分组整体状态。</param>
/// <param name="Elapsed">分组采集耗时。</param>
/// <param name="Fields">分组内的字段清单。</param>
/// <param name="Details">明细行（如已装软件清单），无明细时为空集合。</param>
public sealed record CollectionGroupResult(
    CollectionGroup Group,
    CollectionGroupStatus Status,
    TimeSpan Elapsed,
    IReadOnlyList<CollectedField> Fields,
    IReadOnlyList<string> Details);

/// <summary>
/// 一次设备信息采集生成的快照。每次采集生成一次，独立保存，可跨快照对比（如软件版本、补丁级别变化）。
/// </summary>
/// <param name="SnapshotId">快照标识（形如 snap-20261012-193000-ab12）。</param>
/// <param name="MachineId">所属目标机的机器标识。</param>
/// <param name="MachineIdSource">机器标识来源。</param>
/// <param name="HostName">采集时计算机名。</param>
/// <param name="CollectedAt">采集完成时间。</param>
/// <param name="Mode">采集模式。</param>
/// <param name="Groups">各分组采集结果。</param>
public sealed record MachineSnapshot(
    string SnapshotId,
    string MachineId,
    MachineIdSource MachineIdSource,
    string HostName,
    DateTimeOffset CollectedAt,
    CollectionMode Mode,
    IReadOnlyList<CollectionGroupResult> Groups);

/// <summary>
/// 目标机档案：同一台机器多次处理归并到同一档案。
/// </summary>
/// <param name="MachineId">机器标识。</param>
/// <param name="MachineIdSource">机器标识来源。</param>
/// <param name="HostName">最近一次采集时的计算机名。</param>
/// <param name="FirstCollectedAt">首次采集时间。</param>
/// <param name="LastCollectedAt">最近一次采集时间。</param>
/// <param name="ProcessingCount">该机器累计采集（处理）次数。</param>
/// <param name="SnapshotIds">该档案下的快照标识（按时间由旧到新）。</param>
public sealed record MachineProfile(
    string MachineId,
    MachineIdSource MachineIdSource,
    string HostName,
    DateTimeOffset FirstCollectedAt,
    DateTimeOffset LastCollectedAt,
    int ProcessingCount,
    IReadOnlyList<string> SnapshotIds);

/// <summary>用于确定机器标识的原始事实。</summary>
/// <param name="HardwareSerial">硬件序列号类标识（不可得时为 null）。</param>
/// <param name="HostName">计算机名。</param>
public sealed record DeviceIdentityFacts(string? HardwareSerial, string HostName);

/// <summary>最终的机器标识及其来源。</summary>
/// <param name="MachineId">机器标识。</param>
/// <param name="Source">标识来源。</param>
public sealed record MachineIdentity(string MachineId, MachineIdSource Source);

/// <summary>档案归并结果：归并后的档案、是否新建，以及此前的处理次数（用于提示“此前处理过 N 次”）。</summary>
/// <param name="Profile">归并后的档案。</param>
/// <param name="IsNewMachine">是否首次识别该设备。</param>
/// <param name="PreviousProcessingCount">归并前的处理次数。</param>
public sealed record MachineProfileMerge(MachineProfile Profile, bool IsNewMachine, int PreviousProcessingCount)
{
    /// <summary>面向 IT 人员的提示文本。</summary>
    public string Notice => IsNewMachine
        ? $"首次识别该设备，已新建档案（标识来源：{MachineIdSourceText.Describe(Profile.MachineIdSource)}）。"
        : $"该机器此前处理过 {PreviousProcessingCount} 次，已归并到既有档案。";
}

/// <summary>实测得到的原始采集数据，并在其中确定机器标识。</summary>
/// <param name="Facts">机器标识原始事实。</param>
/// <param name="Groups">各分组采集结果。</param>
public sealed record DeviceCollectionResult(DeviceIdentityFacts Facts, IReadOnlyList<CollectionGroupResult> Groups);

/// <summary>采集进度：以“采集项”为最小单位上报，便于慢项分段显示进度。</summary>
/// <param name="Group">所属分组。</param>
/// <param name="ItemName">当前完成的采集项名称。</param>
/// <param name="CompletedItems">已完成的采集项数量（含当前项）。</param>
/// <param name="TotalItems">本次采集的采集项总数。</param>
public sealed record CollectionProgress(CollectionGroup Group, string ItemName, int CompletedItems, int TotalItems);

/// <summary>机器标识来源的可读文本。</summary>
public static class MachineIdSourceText
{
    /// <summary>把标识来源转换为中文说明。</summary>
    /// <param name="source">标识来源。</param>
    /// <returns>“硬件序列号”或“回退（主机名 + 首次采集时间）”。</returns>
    public static string Describe(MachineIdSource source)
        => source == MachineIdSource.HardwareSerial ? "硬件序列号" : "回退（主机名 + 首次采集时间）";
}