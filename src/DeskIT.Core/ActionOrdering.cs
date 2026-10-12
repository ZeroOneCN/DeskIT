namespace DeskIT.Core;

/// <summary>动作排序规则：按风险升序排列，且需重启的动作统一排到最后。</summary>
public static class ActionOrdering
{
    /// <summary>
    /// 对动作列表排序：先按“是否需重启”分组（不需重启的在前，需重启的统一排到最后），
    /// 组内再按风险级别从只读到不可逆升序排列。相同排序键保持输入顺序（稳定排序）。
    /// </summary>
    /// <param name="actions">待排序的动作集合，允许为 null。</param>
    /// <returns>排序后的新列表；输入为 null 时返回空列表。</returns>
    public static IReadOnlyList<ActionDescriptor> OrderActions(IEnumerable<ActionDescriptor>? actions)
    {
        if (actions is null)
        {
            return Array.Empty<ActionDescriptor>();
        }

        return actions
            .OrderBy(a => a.RequiresRestart) // false(0) 在前；true(1) 需重启的排最后
            .ThenBy(a => (int)a.Risk)        // 组内按风险升序
            .ToList();
    }
}