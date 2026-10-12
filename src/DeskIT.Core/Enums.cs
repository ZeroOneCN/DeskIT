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