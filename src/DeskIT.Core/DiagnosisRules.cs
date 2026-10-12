namespace DeskIT.Core;

/// <summary>
/// M3 诊断相关的可单元测试纯规则：场景顺序与名称、结论排序（异常置顶）、结论统计与会话标识生成。
/// 本类不依赖任何 Windows API，实际检查由 DeskIT.Windows 提供原始数据。
/// </summary>
public static class DiagnosisRules
{
    /// <summary>单项诊断项默认超时。诊断项多为本机只读查询，超时即记“未知（超时）”，不阻塞其他项。</summary>
    public static readonly TimeSpan DefaultItemTimeout = TimeSpan.FromSeconds(15);

    /// <summary>场景展示顺序（与 PRD M3“问题场景列表”一致）。</summary>
    public static readonly IReadOnlyList<DiagnosisScene> AllScenes = new[]
    {
        DiagnosisScene.Slowness,
        DiagnosisScene.Network,
        DiagnosisScene.PrintingSharing,
        DiagnosisScene.DiskStorage,
        DiagnosisScene.StartupSystem,
        DiagnosisScene.Software,
        DiagnosisScene.AccountPrivilege
    };

    /// <summary>
    /// 计算本次实际执行的场景。
    /// </summary>
    /// <param name="allScenes">是否执行全场景诊断。</param>
    /// <param name="selectedScenes">IT 人员勾选的场景集合（全场景诊断时可忽略）。</param>
    /// <returns>按展示顺序排列的待诊断场景。</returns>
    public static IReadOnlyList<DiagnosisScene> SelectScenes(
        bool allScenes,
        IReadOnlySet<DiagnosisScene>? selectedScenes)
    {
        if (allScenes)
        {
            return AllScenes;
        }

        var result = new List<DiagnosisScene>();
        foreach (var scene in AllScenes)
        {
            if (selectedScenes is not null && selectedScenes.Contains(scene))
            {
                result.Add(scene);
            }
        }

        return result;
    }

    /// <summary>
    /// 结论的展示优先级序号：异常 → 提示 → 未知 → 正常。
    /// </summary>
    /// <param name="conclusion">结论。</param>
    /// <returns>排序序号（越小越靠前）。</returns>
    public static int ConclusionRank(DiagnosisConclusion conclusion) => conclusion switch
    {
        DiagnosisConclusion.Abnormal => 0,
        DiagnosisConclusion.Hint => 1,
        DiagnosisConclusion.Unknown => 2,
        _ => 3
    };

    /// <summary>
    /// 按“异常置顶”规则排序结论：异常 → 提示 → 未知 → 正常；同档内按场景顺序、再按名称稳定排列。
    /// </summary>
    /// <param name="findings">待排序的结论集合。</param>
    /// <returns>排序后的结论列表；异常与提示项始终可见，不因数量多而被折叠。</returns>
    public static IReadOnlyList<DiagnosisFinding> SortFindings(IEnumerable<DiagnosisFinding> findings)
    {
        var sceneOrder = AllScenes
            .Select((scene, index) => (scene, index))
            .ToDictionary(pair => pair.scene, pair => pair.index);

        return findings
            .OrderBy(f => ConclusionRank(f.Conclusion))
            .ThenBy(f => sceneOrder.TryGetValue(f.Scene, out var index) ? index : int.MaxValue)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 统计诊断结论。仅“已执行”的项计入四态统计；不适用与未执行（需管理员）单独计数。
    /// </summary>
    /// <param name="scenes">各场景诊断结果。</param>
    /// <returns>结论统计。</returns>
    public static DiagnosisSummary BuildSummary(IReadOnlyList<DiagnosisSceneResult> scenes)
    {
        var findings = scenes.SelectMany(scene => scene.Findings).ToList();
        var executed = findings.Where(f => f.Status == DiagnosisExecutionStatus.Executed).ToList();

        return new DiagnosisSummary(
            AbnormalCount: executed.Count(f => f.Conclusion == DiagnosisConclusion.Abnormal),
            HintCount: executed.Count(f => f.Conclusion == DiagnosisConclusion.Hint),
            UnknownCount: executed.Count(f => f.Conclusion == DiagnosisConclusion.Unknown),
            NormalCount: executed.Count(f => f.Conclusion == DiagnosisConclusion.Normal),
            NotApplicableCount: findings.Count(f => f.Status == DiagnosisExecutionStatus.NotApplicable),
            SkippedNeedsAdministratorCount: findings.Count(f => f.Status == DiagnosisExecutionStatus.SkippedNeedsAdministrator),
            TotalCount: findings.Count);
    }

    /// <summary>
    /// 生成诊断会话标识：形如 sess-20261012-193000-ab12，含随机后缀以保证同一秒内多次诊断仍唯一。
    /// </summary>
    /// <param name="createdAt">会话创建时间。</param>
    /// <param name="suffix">随机后缀（4 位十六进制），未提供时自动生成。</param>
    /// <returns>会话标识。</returns>
    public static string NewSessionId(DateTimeOffset createdAt, string? suffix = null)
    {
        var tail = suffix ?? Guid.NewGuid().ToString("N")[..4];
        return $"sess-{createdAt:yyyyMMdd-HHmmss}-{tail}";
    }

    /// <summary>场景的中文名称。</summary>
    /// <param name="scene">场景标识。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeScene(DiagnosisScene scene) => scene switch
    {
        DiagnosisScene.Slowness => "卡慢",
        DiagnosisScene.Network => "网络",
        DiagnosisScene.PrintingSharing => "打印与共享",
        DiagnosisScene.DiskStorage => "磁盘与存储",
        DiagnosisScene.StartupSystem => "启动与系统",
        DiagnosisScene.Software => "软件",
        _ => "账号与权限"
    };

    /// <summary>四态结论的中文名称。</summary>
    /// <param name="conclusion">结论。</param>
    /// <returns>“正常 / 异常 / 提示 / 未知”。</returns>
    public static string DescribeConclusion(DiagnosisConclusion conclusion) => conclusion switch
    {
        DiagnosisConclusion.Normal => "正常",
        DiagnosisConclusion.Abnormal => "异常",
        DiagnosisConclusion.Hint => "提示",
        _ => "未知"
    };

    /// <summary>执行状态的中文名称。</summary>
    /// <param name="status">执行状态。</param>
    /// <returns>“已执行 / 未执行（需要管理员权限） / 不适用”。</returns>
    public static string DescribeStatus(DiagnosisExecutionStatus status) => status switch
    {
        DiagnosisExecutionStatus.SkippedNeedsAdministrator => "未执行（需要管理员权限）",
        DiagnosisExecutionStatus.NotApplicable => "不适用",
        _ => "已执行"
    };

    /// <summary>诊断结论统计的中文摘要，用于界面与报告。</summary>
    /// <param name="summary">结论统计。</param>
    /// <returns>如“本次发现 2 项异常、3 项提示、1 项未知”。</returns>
    public static string DescribeSummary(DiagnosisSummary summary)
    {
        var parts = new List<string>
        {
            $"本次发现 {summary.AbnormalCount} 项异常、{summary.HintCount} 项提示、{summary.UnknownCount} 项未知"
        };

        if (summary.SkippedNeedsAdministratorCount > 0)
        {
            parts.Add($"另有 {summary.SkippedNeedsAdministratorCount} 项需管理员权限未执行");
        }

        if (summary.NotApplicableCount > 0)
        {
            parts.Add($"{summary.NotApplicableCount} 项不适用");
        }

        return string.Join("；", parts) + "。";
    }
}