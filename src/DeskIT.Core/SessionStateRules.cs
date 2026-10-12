namespace DeskIT.Core;

/// <summary>
/// 诊断会话状态机的可单元测试纯规则：合法迁移判定、终态识别、中文说明与结论映射。
/// 状态机由 DeskIT.Windows 的诊断编排服务驱动，界面只读取当前状态。
/// </summary>
public static class SessionStateRules
{
    /// <summary>
    /// 判断状态是否为终态（不可再迁移）。
    /// </summary>
    /// <param name="state">会话状态。</param>
    /// <returns>终态返回 true。</returns>
    public static bool IsTerminal(SessionState state) => state is
        SessionState.Closed or
        SessionState.Resolved or
        SessionState.PendingRestart or
        SessionState.Unresolved or
        SessionState.Interrupted;

    /// <summary>
    /// 判断是否允许从一种状态迁移到另一种状态。
    /// 非终态允许重复进入自身（幂等，如再次诊断）；终态不可再迁移。
    /// </summary>
    /// <param name="from">当前状态。</param>
    /// <param name="to">目标状态。</param>
    /// <returns>允许迁移返回 true。</returns>
    public static bool CanTransition(SessionState from, SessionState to)
    {
        if (IsTerminal(from))
        {
            return false;
        }

        if (from == to)
        {
            // 非终态允许重复进入自身（幂等，如再次采集、再次诊断）；“新建”是初始态，重复进入无意义。
            return from != SessionState.New;
        }

        if (to == SessionState.Interrupted)
        {
            // 任意非终态均可被异常中断（如介质移除）。
            return true;
        }

        return from switch
        {
            SessionState.New => to is SessionState.Collecting or SessionState.Diagnosing,
            SessionState.Collecting => to is SessionState.Diagnosing,
            SessionState.Diagnosing => to is SessionState.Collecting or SessionState.Acting or SessionState.Closed
                or SessionState.Resolved or SessionState.PendingRestart or SessionState.Unresolved,
            SessionState.Acting => to is SessionState.Diagnosing or SessionState.Closed
                or SessionState.Resolved or SessionState.PendingRestart or SessionState.Unresolved,
            _ => false
        };
    }

    /// <summary>
    /// 列出某状态允许迁移到的全部状态（按界面使用顺序）。
    /// </summary>
    /// <param name="from">当前状态。</param>
    /// <returns>允许的目标状态列表；终态返回空列表。</returns>
    public static IReadOnlyList<SessionState> AllowedNext(SessionState from)
    {
        var candidates = new[]
        {
            SessionState.New,
            SessionState.Collecting,
            SessionState.Diagnosing,
            SessionState.Acting,
            SessionState.Closed,
            SessionState.Resolved,
            SessionState.PendingRestart,
            SessionState.Unresolved,
            SessionState.Interrupted
        };

        return candidates.Where(state => CanTransition(from, state)).ToList();
    }

    /// <summary>
    /// 把会话结论倾向映射为对应的终态。
    /// </summary>
    /// <param name="conclusion">会话结论倾向。</param>
    /// <returns>对应的终态。</returns>
    public static SessionState ToSessionState(SessionConclusion conclusion) => conclusion switch
    {
        SessionConclusion.Resolved => SessionState.Resolved,
        SessionConclusion.PendingRestart => SessionState.PendingRestart,
        SessionConclusion.Interrupted => SessionState.Interrupted,
        _ => SessionState.Unresolved
    };

    /// <summary>会话状态的中文名称。</summary>
    /// <param name="state">会话状态。</param>
    /// <returns>中文名称。</returns>
    public static string Describe(SessionState state) => state switch
    {
        SessionState.New => "新建",
        SessionState.Collecting => "采集中",
        SessionState.Diagnosing => "诊断中",
        SessionState.Acting => "执行动作中",
        SessionState.Closed => "已结束",
        SessionState.Resolved => "已解决",
        SessionState.PendingRestart => "待重启生效",
        SessionState.Unresolved => "未解决",
        _ => "已中断"
    };
}