namespace DeskIT.Core;

/// <summary>
/// M2 采集相关的可单元测试纯规则：可选分组与关闭、分组状态归约、字段可读文本、快照标识与保留份数。
/// 本类不依赖任何 Windows API，实际查询由 DeskIT.Windows 提供原始数据。
/// </summary>
public static class CollectionRules
{
    /// <summary>单项采集项默认超时：超时即记为“未知（超时）”，且不阻塞其他项。</summary>
    public static readonly TimeSpan DefaultItemTimeout = TimeSpan.FromSeconds(15);

    /// <summary>基础采集的耗时预算（PRD M2：默认 ≤ 60 秒）。</summary>
    public static readonly TimeSpan BasicCollectionBudget = TimeSpan.FromSeconds(60);

    /// <summary>单个档案保留的快照份数上限（PRD M2：保留历史快照 5 份）。</summary>
    public const int MaxRetainedSnapshots = 5;

    /// <summary>分组展示顺序（与 PRD M2“采集内容与字段”表一致）。</summary>
    public static readonly IReadOnlyList<CollectionGroup> AllGroups = new[]
    {
        CollectionGroup.System,
        CollectionGroup.Hardware,
        CollectionGroup.Storage,
        CollectionGroup.Network,
        CollectionGroup.AccountEnvironment,
        CollectionGroup.InstalledSoftware,
        CollectionGroup.SecurityUpdate
    };

    /// <summary>可由 IT 人员单项关闭的分组（PRD M2：账号与环境、已装软件、安全与更新 可关闭）。</summary>
    public static readonly IReadOnlyList<CollectionGroup> OptionalGroups = new[]
    {
        CollectionGroup.AccountEnvironment,
        CollectionGroup.InstalledSoftware,
        CollectionGroup.SecurityUpdate
    };

    /// <summary>
    /// 判断分组是否可由 IT 人员关闭。
    /// </summary>
    /// <param name="group">分组标识。</param>
    /// <returns>可关闭返回 true；系统、硬件、存储、网络为必采项，返回 false。</returns>
    public static bool IsOptional(CollectionGroup group) => OptionalGroups.Contains(group);

    /// <summary>
    /// 计算本次实际执行的分组：排除被关闭的分组；基础采集不包含已装软件全量清单。
    /// </summary>
    /// <param name="mode">采集模式。</param>
    /// <param name="disabledGroups">被 IT 人员关闭的分组集合（可为 null）。</param>
    /// <returns>按展示顺序排列的待采集分组。</returns>
    public static IReadOnlyList<CollectionGroup> ResolveActiveGroups(
        CollectionMode mode,
        IReadOnlySet<CollectionGroup>? disabledGroups)
    {
        var result = new List<CollectionGroup>();
        foreach (var group in AllGroups)
        {
            if (disabledGroups is not null && disabledGroups.Contains(group))
            {
                continue;
            }

            // 基础采集默认不含已装软件全量清单（慢项），完整采集才纳入。
            if (mode == CollectionMode.Basic && group == CollectionGroup.InstalledSoftware)
            {
                continue;
            }

            result.Add(group);
        }

        return result;
    }

    /// <summary>
    /// 依据分组内字段状态归约分组整体状态。
    /// </summary>
    /// <param name="fields">分组内的字段结果。</param>
    /// <param name="skipped">该分组是否被跳过（未采集）。</param>
    /// <returns>分组整体状态。</returns>
    public static CollectionGroupStatus ResolveGroupStatus(IReadOnlyList<CollectedField> fields, bool skipped = false)
    {
        if (skipped)
        {
            return CollectionGroupStatus.Skipped;
        }

        if (fields.Count == 0 || fields.All(f => f.Status == FieldStatus.NotApplicable))
        {
            // 无字段或全部不适用，按“不适用”归入已采集（不构成异常）。
            return CollectionGroupStatus.Collected;
        }

        if (fields.Any(f => f.Status == FieldStatus.Timeout))
        {
            return CollectionGroupStatus.Timeout;
        }

        if (fields.Any(f => f.Status is FieldStatus.NoPermission or FieldStatus.Failed))
        {
            return CollectionGroupStatus.Partial;
        }

        return CollectionGroupStatus.Collected;
    }

    /// <summary>
    /// 生成快照标识：形如 snap-20261012-193000-ab12，含随机后缀以保证同一秒内多次采集仍唯一。
    /// </summary>
    /// <param name="collectedAt">采集完成时间。</param>
    /// <param name="suffix">随机后缀（4 位十六进制），未提供时自动生成。</param>
    /// <returns>快照标识。</returns>
    public static string NewSnapshotId(DateTimeOffset collectedAt, string? suffix = null)
    {
        var tail = suffix ?? Guid.NewGuid().ToString("N")[..4];
        return $"snap-{collectedAt:yyyyMMdd-HHmmss}-{tail}";
    }

    /// <summary>
    /// 选出需要清理的快照标识，使档案内保留的最新快照不超过指定份数。
    /// </summary>
    /// <param name="snapshotIdsOldestFirst">档案内快照标识（按时间由旧到新）。</param>
    /// <param name="maxKeep">保留份数上限。</param>
    /// <returns>应删除的快照标识（最旧的优先）。</returns>
    public static IReadOnlyList<string> SelectSnapshotIdsToTrim(
        IReadOnlyList<string> snapshotIdsOldestFirst,
        int maxKeep = MaxRetainedSnapshots)
    {
        if (maxKeep < 0)
        {
            maxKeep = 0;
        }

        var overflow = snapshotIdsOldestFirst.Count - maxKeep;
        if (overflow <= 0)
        {
            return Array.Empty<string>();
        }

        return snapshotIdsOldestFirst.Take(overflow).ToList();
    }

    /// <summary>
    /// 把字段结果转换为界面可读文本：正常取值显示值，其余显示状态说明。
    /// </summary>
    /// <param name="field">字段结果。</param>
    /// <returns>面向 IT 人员的显示文本。</returns>
    public static string DescribeField(CollectedField field) => field.Status switch
    {
        FieldStatus.NoPermission => "无权限读取",
        FieldStatus.Timeout => "未知（超时）",
        FieldStatus.NotApplicable => "不适用",
        FieldStatus.Failed => "未知（读取失败）",
        _ => field.Value ?? "—"
    };

    /// <summary>分组的中文名称。</summary>
    /// <param name="group">分组标识。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeGroup(CollectionGroup group) => group switch
    {
        CollectionGroup.System => "系统",
        CollectionGroup.Hardware => "硬件",
        CollectionGroup.Storage => "存储",
        CollectionGroup.Network => "网络",
        CollectionGroup.AccountEnvironment => "账号与环境",
        CollectionGroup.InstalledSoftware => "已装软件",
        _ => "安全与更新"
    };

    /// <summary>分组状态的中文名称。</summary>
    /// <param name="status">分组状态。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeGroupStatus(CollectionGroupStatus status) => status switch
    {
        CollectionGroupStatus.Collected => "已采集",
        CollectionGroupStatus.Partial => "部分可用",
        CollectionGroupStatus.Skipped => "已跳过",
        _ => "超时"
    };

    /// <summary>采集模式的中文名称。</summary>
    /// <param name="mode">采集模式。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeMode(CollectionMode mode)
        => mode == CollectionMode.Basic ? "基础采集" : "完整采集";
}