using System.Text;
using System.Text.RegularExpressions;

namespace DeskIT.Core;

/// <summary>
/// M7 记录与多机台账的可单元测试纯规则。
/// 本类不依赖任何 Windows API：涵盖枚举中文说明、姓名遮蔽与凭据掩码、记录校验、
/// 统计与筛选、按机器归并与标识冲突检测、报告文件名生成与记录脱敏。
/// </summary>
public static class LedgerRules
{
    /// <summary>员工现象描述的字数上限。</summary>
    public const int SymptomMaxLength = 500;

    /// <summary>凭据类参数值的掩码文本。</summary>
    public const string CredentialMask = "******";

    /// <summary>
    /// 凭据类参数的正则（保守且可复盘）：
    /// ① <c>password=xxx</c> / <c>passwd: xxx</c> / <c>pwd xxx</c> 形式；
    /// ② <c>cmdkey /pass:xxx</c> / <c>/password:xxx</c> 形式；
    /// ③ <c>-p xxx</c> / <c>--password xxx</c> 空白分隔形式。
    /// 仅替换参数“值”，键名与其余文本保留原文，便于复盘。
    /// </summary>
    private static readonly Regex[] CredentialPatterns =
    {
        new(@"(?i)\b((?:password|passwd|pwd)\s*[:=]\s*)(""[^""]*""|'[^']*'|\S+)", RegexOptions.Compiled),
        new(@"(?i)(/pass(?:word)?\s*[:=]\s*)(""[^""]*""|'[^']*'|\S+)", RegexOptions.Compiled),
        new(@"(?i)((?:^|\s)(?:--password|-p)\s+)(""[^""]*""|'[^']*'|\S+)", RegexOptions.Compiled)
    };

    /// <summary>
    /// 生成一条处理记录标识：形如 rec-20261012-193000-ab12，含随机后缀保证同一秒内唯一。
    /// </summary>
    /// <param name="createdAt">记录创建时间。</param>
    /// <param name="suffix">随机后缀（4 位十六进制）；未提供时自动生成。</param>
    /// <returns>记录标识。</returns>
    public static string NewRecordId(DateTimeOffset createdAt, string? suffix = null)
    {
        var tail = suffix ?? Guid.NewGuid().ToString("N")[..4];
        return $"rec-{createdAt:yyyyMMdd-HHmmss}-{tail}";
    }

    /// <summary>问题类别的中文名称。</summary>
    /// <param name="category">问题类别。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeCategory(ProblemCategory category) => category switch
    {
        ProblemCategory.Slowness => "卡慢",
        ProblemCategory.Network => "网络",
        ProblemCategory.PrintingSharing => "打印与共享",
        ProblemCategory.DiskStorage => "磁盘与存储",
        ProblemCategory.StartupSystem => "启动与系统",
        ProblemCategory.Software => "软件",
        ProblemCategory.AccountPrivilege => "账号与权限",
        _ => "其他"
    };

    /// <summary>未解决原因的中文名称。</summary>
    /// <param name="reason">未解决原因。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeUnresolvedReason(UnresolvedReason reason) => reason switch
    {
        UnresolvedReason.NeedPartOrVendor => "需更换部件或联系厂商",
        UnresolvedReason.NeedNetworkOrServer => "需网络或服务器侧配合",
        UnresolvedReason.NeedUserObservation => "需用户继续观察",
        UnresolvedReason.PermissionOrPolicy => "权限或策略限制",
        UnresolvedReason.NeedBackupFirst => "需先备份数据",
        UnresolvedReason.CannotReproduce => "无法复现",
        _ => "其他"
    };

    /// <summary>会话结论的中文名称。</summary>
    /// <param name="conclusion">会话结论。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeConclusion(SessionConclusion conclusion) => conclusion switch
    {
        SessionConclusion.Resolved => "已解决",
        SessionConclusion.PendingRestart => "待重启生效",
        SessionConclusion.Unresolved => "未解决",
        _ => "已中断"
    };

    /// <summary>把诊断场景映射为问题类别（与 <see cref="ProblemCategory"/> 一一对应）。</summary>
    /// <param name="scene">诊断场景。</param>
    /// <returns>对应的问题类别。</returns>
    public static ProblemCategory MapSceneToCategory(DiagnosisScene scene) => scene switch
    {
        DiagnosisScene.Slowness => ProblemCategory.Slowness,
        DiagnosisScene.Network => ProblemCategory.Network,
        DiagnosisScene.PrintingSharing => ProblemCategory.PrintingSharing,
        DiagnosisScene.DiskStorage => ProblemCategory.DiskStorage,
        DiagnosisScene.StartupSystem => ProblemCategory.StartupSystem,
        DiagnosisScene.Software => ProblemCategory.Software,
        DiagnosisScene.AccountPrivilege => ProblemCategory.AccountPrivilege,
        _ => ProblemCategory.Other
    };

    /// <summary>
    /// 遮蔽处理人姓名：保留姓与名首字。
    /// 规则：1 字原样；2 字保留首字并整体遮蔽名（如“张三”→“张*”）；
    /// 3 字及以上保留首字与末字、中间以 * 遮蔽（如“张小明”→“张*明”、“欧阳小明”→“欧**明”）。
    /// 工号一律保留原值，不做遮蔽。
    /// </summary>
    /// <param name="name">原始姓名；可能为 null 或空。</param>
    /// <returns>遮蔽后的姓名。</returns>
    public static string MaskOperatorName(string? name)
    {
        var value = name ?? string.Empty;
        var length = value.Length;
        if (length <= 1)
        {
            return value;
        }

        if (length == 2)
        {
            return value[..1] + "*";
        }

        return value[..1] + new string('*', length - 2) + value[^1..];
    }

    /// <summary>
    /// 把文本中的凭据类参数值替换为掩码（见 <see cref="CredentialPatterns"/> 注释中的规则）。
    /// 键名与其余文本保留原文，保证可复盘；无命中时原样返回。
    /// </summary>
    /// <param name="text">待处理文本；可能为 null。</param>
    /// <returns>脱敏后的文本。</returns>
    public static string MaskCredentials(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        foreach (var pattern in CredentialPatterns)
        {
            result = pattern.Replace(result, match => match.Groups[1].Value + CredentialMask);
        }

        return result;
    }

    /// <summary>
    /// 判断处理人是否完整（工号与姓名两项均非空白）。
    /// </summary>
    /// <param name="operator">处理人；可能为 null。</param>
    /// <returns>两项均填写返回 true。</returns>
    public static bool IsCompleteOperator(OperatorInfo? @operator)
        => @operator is not null
           && !string.IsNullOrWhiteSpace(@operator.EmployeeId)
           && !string.IsNullOrWhiteSpace(@operator.Name);

    /// <summary>
    /// 校验一条处理记录的完整性：结论与问题类别必填、结论为“未解决”时未解决原因必填、
    /// 处理人工号与姓名必填、员工现象描述不超过 500 字、各标识非空且时间区间合法。
    /// </summary>
    /// <param name="record">待校验的处理记录。</param>
    /// <returns>校验结果（是否通过与问题清单）。</returns>
    public static LedgerRecordValidation Validate(HandlingRecord record)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(record.RecordId))
        {
            problems.Add("记录标识不能为空。");
        }

        if (string.IsNullOrWhiteSpace(record.SessionId))
        {
            problems.Add("会话标识不能为空。");
        }

        if (string.IsNullOrWhiteSpace(record.MachineId))
        {
            problems.Add("机器标识不能为空（会话尚未建立机器标识时不能写入台账）。");
        }

        if (string.IsNullOrWhiteSpace(record.HostName))
        {
            problems.Add("计算机名不能为空。");
        }

        if (!IsCompleteOperator(record.Operator))
        {
            problems.Add("处理人必须同时填写工号与姓名。");
        }

        if (!Enum.IsDefined(record.Category))
        {
            problems.Add("问题类别必填。");
        }

        if (!Enum.IsDefined(record.Conclusion))
        {
            problems.Add("结论必填。");
        }

        if (record.Conclusion == SessionConclusion.Unresolved && record.UnresolvedReason is null)
        {
            problems.Add("结论为“未解决”时必须选择未解决原因。");
        }

        if (record.SymptomDescription.Length > SymptomMaxLength)
        {
            problems.Add($"员工现象描述不能超过 {SymptomMaxLength} 字。");
        }

        if (record.FinishedAt < record.StartedAt)
        {
            problems.Add("处理结束时间不能早于开始时间。");
        }

        return new LedgerRecordValidation(problems.Count == 0, problems);
    }

    /// <summary>
    /// 汇总台账统计：记录总数、机器数、按问题类别计数、按处理人计数、未解决数与未解决占比。
    /// </summary>
    /// <param name="records">处理记录集合。</param>
    /// <returns>台账统计。</returns>
    public static LedgerStatistics BuildStatistics(IEnumerable<HandlingRecord> records)
    {
        var list = records.ToList();
        var total = list.Count;

        var categoryCounts = list
            .GroupBy(record => record.Category)
            .Select(group => new CategoryCount(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Category)
            .ToList();

        var operatorCounts = list
            .GroupBy(record => (record.Operator.EmployeeId, record.Operator.Name))
            .Select(group => new OperatorCount(group.Key.EmployeeId, group.Key.Name, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.EmployeeId, StringComparer.Ordinal)
            .ToList();

        var unresolved = list.Count(record => record.Conclusion == SessionConclusion.Unresolved);

        return new LedgerStatistics(
            total,
            list.Select(record => record.MachineId).Distinct(StringComparer.Ordinal).Count(),
            categoryCounts,
            operatorCounts,
            unresolved,
            total == 0 ? 0d : (double)unresolved / total);
    }

    /// <summary>
    /// 按条件筛选记录：问题类别、处理人工号（忽略大小写）、处理时间区间（按结束时间，闭区间）。
    /// 各条件为 null 时表示不限制。
    /// </summary>
    /// <param name="records">处理记录集合。</param>
    /// <param name="category">问题类别筛选。</param>
    /// <param name="operatorEmployeeId">处理人工号筛选。</param>
    /// <param name="from">起始时间（含）。</param>
    /// <param name="to">结束时间（含）。</param>
    /// <returns>命中条件的记录（保持原始顺序）。</returns>
    public static IReadOnlyList<HandlingRecord> Filter(
        IEnumerable<HandlingRecord> records,
        ProblemCategory? category = null,
        string? operatorEmployeeId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var query = records;

        if (category is not null)
        {
            query = query.Where(record => record.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(operatorEmployeeId))
        {
            var id = operatorEmployeeId.Trim();
            query = query.Where(record =>
                string.Equals(record.Operator.EmployeeId, id, StringComparison.OrdinalIgnoreCase));
        }

        if (from is not null)
        {
            query = query.Where(record => record.FinishedAt >= from);
        }

        if (to is not null)
        {
            query = query.Where(record => record.FinishedAt <= to);
        }

        return query.ToList();
    }

    /// <summary>
    /// 按机器标识归并记录：每台机器一个档案，档案内记录按处理时间倒序，档案按最近处理时间倒序。
    /// </summary>
    /// <param name="records">处理记录集合。</param>
    /// <returns>机器档案列表。</returns>
    public static IReadOnlyList<MachineLedger> GroupByMachine(IEnumerable<HandlingRecord> records)
        => records
            .GroupBy(record => record.MachineId, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(record => record.FinishedAt).ToList();
                var latest = ordered[0];
                return new MachineLedger(latest.MachineId, latest.MachineIdSource, latest.HostName, ordered);
            })
            .OrderByDescending(ledger => ledger.LastHandledAt)
            .ToList();

    /// <summary>
    /// 检测机器标识冲突：同一机器标识下存在处理时间区间重叠的两条记录（如两机克隆镜像）时，
    /// 标记“需人工确认”。两条记录并存不合并；返回全部存在重叠的记录对。
    /// </summary>
    /// <param name="records">处理记录集合。</param>
    /// <returns>冲突记录对列表（按机器标识与记录标识排序，稳定输出）。</returns>
    public static IReadOnlyList<MachineIdConflict> DetectMachineIdConflicts(IEnumerable<HandlingRecord> records)
    {
        var conflicts = new List<MachineIdConflict>();
        foreach (var group in records.GroupBy(record => record.MachineId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(record => record.StartedAt).ThenBy(record => record.RecordId, StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    if (Overlaps(ordered[i], ordered[j]))
                    {
                        conflicts.Add(new MachineIdConflict(ordered[i].MachineId, ordered[i].RecordId, ordered[j].RecordId));
                    }
                }
            }
        }

        return conflicts
            .OrderBy(item => item.MachineId, StringComparer.Ordinal)
            .ThenBy(item => item.FirstRecordId, StringComparer.Ordinal)
            .ThenBy(item => item.SecondRecordId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 生成报告文件名主干（不含扩展名）：[机器标识]_[处理时间]_[会话号]，并做 ASCII 安全化。
    /// </summary>
    /// <param name="record">处理记录。</param>
    /// <returns>ASCII 安全的文件名主干。</returns>
    public static string BuildReportFileName(HandlingRecord record)
    {
        var machine = SanitizeFileToken(record.MachineId);
        var time = record.FinishedAt.ToString("yyyyMMdd-HHmmss");
        var session = SanitizeFileToken(record.SessionId);
        return $"{machine}_{time}_{session}";
    }

    /// <summary>
    /// 生成记录的脱敏副本：遮蔽处理人姓名（工号保留）、对动作输出与自定义命令文本应用凭据掩码、
    /// 对诊断依据做凭据掩码。用于自动产出的脱敏报告（静默产出固定脱敏）。
    /// </summary>
    /// <param name="record">原始处理记录。</param>
    /// <returns>脱敏后的新记录。</returns>
    public static HandlingRecord Desensitize(HandlingRecord record) => record with
    {
        Operator = MaskOperator(record.Operator),
        Actions = record.Actions
            .Select(action => action with { OutputSummary = MaskCredentials(action.OutputSummary) })
            .ToList(),
        CustomCommands = record.CustomCommands
            .Select(command => command with
            {
                CommandText = MaskCredentials(command.CommandText),
                OutputSummary = MaskCredentials(command.OutputSummary)
            })
            .ToList(),
        // 持久化变更的项名一般不含凭据，但仍统一过一遍掩码，与其余字段保持一致。
        PersistenceChanges = record.PersistenceChanges
            .Select(change => change with
            {
                Added = change.Added.Select(MaskCredentials).ToList(),
                Removed = change.Removed.Select(MaskCredentials).ToList()
            })
            .ToList(),
        Scenes = MaskScenes(record.Scenes),
        Supplements = record.Supplements
            .Select(supplement => supplement with
            {
                Operator = MaskOperator(supplement.Operator),
                Text = MaskCredentials(supplement.Text)
            })
            .ToList()
    };

    /// <summary>把处理人姓名替换为遮蔽后的姓名（工号保留原值）。</summary>
    /// <param name="operator">处理人。</param>
    /// <returns>姓名被遮蔽的处理人。</returns>
    private static OperatorInfo MaskOperator(OperatorInfo @operator)
        => new(@operator.EmployeeId, MaskOperatorName(@operator.Name));

    /// <summary>对诊断依据的取值做凭据掩码。</summary>
    /// <param name="scenes">诊断结论集合。</param>
    /// <returns>依据经掩码后的新集合。</returns>
    private static IReadOnlyList<DiagnosisSceneResult> MaskScenes(IReadOnlyList<DiagnosisSceneResult> scenes)
        => scenes
            .Select(scene => scene with
            {
                Findings = scene.Findings
                    .Select(finding => finding with
                    {
                        Evidence = finding.Evidence
                            .Select(evidence => evidence with { Value = MaskCredentials(evidence.Value) })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();

    /// <summary>判断两条记录的处理时间区间是否重叠。</summary>
    /// <param name="left">记录一。</param>
    /// <param name="right">记录二。</param>
    /// <returns>重叠返回 true。</returns>
    private static bool Overlaps(HandlingRecord left, HandlingRecord right)
        => left.StartedAt < right.FinishedAt && right.StartedAt < left.FinishedAt;

    /// <summary>
    /// 把任意文本安全化为 ASCII 文件名片段：仅保留字母、数字、连字符与下划线，
    /// 其余字符替换为下划线并合并连续下划线。
    /// </summary>
    /// <param name="value">原始文本。</param>
    /// <returns>ASCII 安全的文件名片段；全非法时返回“machine”。</returns>
    private static string SanitizeFileToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "machine";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'
                ? ch
                : '_');
        }

        var collapsed = new StringBuilder(builder.Length);
        var previousUnderscore = false;
        foreach (var ch in builder.ToString())
        {
            if (ch == '_')
            {
                if (!previousUnderscore)
                {
                    collapsed.Append(ch);
                }

                previousUnderscore = true;
            }
            else
            {
                collapsed.Append(ch);
                previousUnderscore = false;
            }
        }

        var result = collapsed.ToString().Trim('_');
        return result.Length == 0 ? "machine" : result;
    }
}