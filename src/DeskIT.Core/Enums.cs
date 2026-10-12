namespace DeskIT.Core;

/// <summary>修复动作的风险级别，取值越大风险越高。</summary>
public enum RiskLevel
{
    /// <summary>只读：不产生任何变更，仅做查询或诊断。</summary>
    ReadOnly = 0,

    /// <summary>低风险：影响面小、易恢复的变更。</summary>
    Low = 1,

    /// <summary>中风险：影响系统或用户配置、通常可回滚的变更。</summary>
    Medium = 2,

    /// <summary>高风险：可能影响系统可用性或用户数据的变更，必须二次确认。</summary>
    High = 3,

    /// <summary>不可逆：执行后无法自动回滚的变更，必须二次确认并提示备份。</summary>
    Irreversible = 4
}

/// <summary>一次动作执行的结果分类。</summary>
public enum ActionOutcome
{
    /// <summary>成功：动作已完成且已生效。</summary>
    Success = 0,

    /// <summary>成功但需重启：动作已执行，需重启目标机后才完全生效。</summary>
    SuccessPendingRestart = 1,

    /// <summary>失败：执行过程中出错，未达成预期结果。</summary>
    Failed = 2,

    /// <summary>已跳过：前置条件不满足或用户选择不执行。</summary>
    Skipped = 3,

    /// <summary>已取消：由用户或系统主动中止。</summary>
    Cancelled = 4,

    /// <summary>已超时：超出规定执行时限而被终止。</summary>
    TimedOut = 5
}

/// <summary>诊断项的结论，仅此四种。无法判定时必须记为“未知”，不得记为“异常”。</summary>
public enum DiagnosisConclusion
{
    /// <summary>正常：检查项符合预期。</summary>
    Normal = 0,

    /// <summary>异常：检查项确实存在问题。</summary>
    Abnormal = 1,

    /// <summary>提示：未达异常标准但值得关注。</summary>
    Hint = 2,

    /// <summary>未知：无法判定。</summary>
    Unknown = 3
}

/// <summary>诊断会话状态机。</summary>
public enum SessionState
{
    /// <summary>新建：会话已创建，尚未开始采集。</summary>
    New = 0,

    /// <summary>采集中：正在采集设备信息。</summary>
    Collecting = 1,

    /// <summary>诊断中：正在执行场景化诊断。</summary>
    Diagnosing = 2,

    /// <summary>执行动作中：正在执行修复动作。</summary>
    Acting = 3,

    /// <summary>已结束（终态）：流程走完，等待填写结论。</summary>
    Closed = 4,

    /// <summary>已解决（终态）：问题已解决。</summary>
    Resolved = 5,

    /// <summary>待重启生效（终态）：需重启目标机后确认结果。</summary>
    PendingRestart = 6,

    /// <summary>未解决（终态）：问题未解决。</summary>
    Unresolved = 7,

    /// <summary>已中断（终态）：会话被异常中断（如介质移除）。</summary>
    Interrupted = 8
}

/// <summary>问题场景（PRD M3 首批 7 个场景），诊断项按场景组织。</summary>
public enum DiagnosisScene
{
    /// <summary>卡慢：CPU/内存/磁盘占用、启动项、电源计划等。</summary>
    Slowness = 0,

    /// <summary>网络：网卡、IP/DHCP、DNS、网关连通性、代理、VPN 残留。</summary>
    Network = 1,

    /// <summary>打印与共享：后台处理服务、打印队列、脱机打印机、共享映射与服务。</summary>
    PrintingSharing = 2,

    /// <summary>磁盘与存储：分区可用空间、空间增长趋势、临时文件、回收站、磁盘错误日志。</summary>
    DiskStorage = 3,

    /// <summary>启动与系统：开机耗时、计划任务、异常关机与蓝屏、驱动异常设备、系统还原。</summary>
    StartupSystem = 4,

    /// <summary>软件：无响应进程、同功能多版本冲突、最近安装时间线、软件残留目录。</summary>
    Software = 5,

    /// <summary>账号与权限：账号锁定、密码策略、管理员组成员、UAC、共享凭据、机器时间。</summary>
    AccountPrivilege = 6
}

/// <summary>诊断项的执行状态。四态结论表示“看到了什么”，执行状态表示“是否真的执行了”。</summary>
public enum DiagnosisExecutionStatus
{
    /// <summary>已执行：诊断项已实际运行并给出四态结论之一。</summary>
    Executed = 0,

    /// <summary>未执行（需要管理员权限）：只读模式下跳过，不计入异常统计。</summary>
    SkippedNeedsAdministrator = 1,

    /// <summary>不适用：诊断项对目标系统版本或对象不适用，不计入异常与未知统计。</summary>
    NotApplicable = 2
}

/// <summary>会话结束时的结论倾向。</summary>
public enum SessionConclusion
{
    /// <summary>已解决。</summary>
    Resolved = 0,

    /// <summary>待重启生效。</summary>
    PendingRestart = 1,

    /// <summary>未解决。</summary>
    Unresolved = 2,

    /// <summary>已中断。</summary>
    Interrupted = 3
}