namespace DeskIT.Core;

/// <summary>目标机信息快照骨架。同一台机器多次处理应归并到同一档案。</summary>
public sealed class TargetMachineInfo
{
    /// <summary>机器稳定标识（优先取硬件序列号类信息）。</summary>
    public string? MachineId { get; init; }

    /// <summary>主机名。</summary>
    public string? HostName { get; init; }

    /// <summary>操作系统版本。</summary>
    public string? OsVersion { get; init; }

    /// <summary>首次采集时间。</summary>
    public DateTimeOffset FirstCollectedAt { get; init; }
}

/// <summary>处理记录骨架，对应一次诊断会话。</summary>
public sealed class ProcessingRecord
{
    /// <summary>所属诊断会话标识。</summary>
    public required string SessionId { get; init; }

    /// <summary>目标机信息。</summary>
    public required TargetMachineInfo Target { get; init; }

    /// <summary>处理人工号（与姓名共同构成一个完整身份）。</summary>
    public required string OperatorId { get; init; }

    /// <summary>处理人姓名。</summary>
    public required string OperatorName { get; init; }

    /// <summary>会话结论。</summary>
    public SessionConclusion Conclusion { get; init; }

    /// <summary>会话结束时间。</summary>
    public DateTimeOffset ClosedAt { get; init; }
}