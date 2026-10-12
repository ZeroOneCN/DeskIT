namespace DeskIT.Core;

/// <summary>会话结论推导规则。</summary>
public static class SessionConclusionRules
{
    /// <summary>
    /// 依据本次会话内所有动作的执行结果推导会话结论。
    /// 规则优先级：
    /// ① 存在“成功但需重启”⇒ 待重启生效（重启后才能确认最终结果）；
    /// ② 存在“失败”或“已超时”⇒ 未解决；
    /// ③ 存在“成功”且无失败 ⇒ 已解决；
    /// ④ 其余情况（全部跳过/取消，或没有任何动作）⇒ 未解决。
    /// </summary>
    /// <param name="outcomes">本次会话内所有动作的执行结果集合，允许为 null。</param>
    /// <returns>推导出的会话结论倾向。</returns>
    public static SessionConclusion Derive(IEnumerable<ActionOutcome>? outcomes)
    {
        IReadOnlyCollection<ActionOutcome> list =
            outcomes as IReadOnlyCollection<ActionOutcome> ?? outcomes?.ToList() ?? new List<ActionOutcome>();

        if (list.Contains(ActionOutcome.SuccessPendingRestart))
        {
            return SessionConclusion.PendingRestart;
        }

        if (list.Contains(ActionOutcome.Failed) || list.Contains(ActionOutcome.TimedOut))
        {
            return SessionConclusion.Unresolved;
        }

        if (list.Contains(ActionOutcome.Success))
        {
            return SessionConclusion.Resolved;
        }

        return SessionConclusion.Unresolved;
    }
}