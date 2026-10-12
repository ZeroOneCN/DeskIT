using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>记录与多机台账纯规则（LedgerRules）的单元测试。</summary>
public class LedgerRulesTests
{
    /// <summary>固定时区偏移，保证时间断言稳定。</summary>
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    /// <summary>构造一个默认合法的处理记录，供各用例按需覆盖字段。</summary>
    /// <param name="recordId">记录标识。</param>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="machineId">机器标识。</param>
    /// <param name="source">机器标识来源。</param>
    /// <param name="hostName">计算机名。</param>
    /// <param name="operatorInfo">处理人。</param>
    /// <param name="category">问题类别。</param>
    /// <param name="tags">自定义标签。</param>
    /// <param name="symptom">员工现象描述。</param>
    /// <param name="scenes">诊断结论集合。</param>
    /// <param name="actions">动作条目集合。</param>
    /// <param name="conclusion">会话结论。</param>
    /// <param name="reason">未解决原因。</param>
    /// <param name="startedAt">开始时间。</param>
    /// <param name="finishedAt">结束时间。</param>
    /// <returns>处理记录。</returns>
    private static HandlingRecord NewRecord(
        string recordId = "rec-1",
        string sessionId = "sess-1",
        string machineId = "M-1",
        MachineIdSource source = MachineIdSource.HardwareSerial,
        string hostName = "PC-1",
        OperatorInfo? operatorInfo = null,
        ProblemCategory category = ProblemCategory.Network,
        IReadOnlyList<string>? tags = null,
        string symptom = "",
        IReadOnlyList<DiagnosisSceneResult>? scenes = null,
        IReadOnlyList<LedgerActionEntry>? actions = null,
        SessionConclusion conclusion = SessionConclusion.Resolved,
        UnresolvedReason? reason = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null)
    {
        var start = startedAt ?? new DateTimeOffset(2026, 10, 12, 19, 0, 0, Offset);
        return new HandlingRecord
        {
            RecordId = recordId,
            SessionId = sessionId,
            MachineId = machineId,
            MachineIdSource = source,
            HostName = hostName,
            Operator = operatorInfo ?? new OperatorInfo("E1001", "张小明"),
            Category = category,
            Tags = tags ?? Array.Empty<string>(),
            SymptomDescription = symptom,
            Scenes = scenes ?? Array.Empty<DiagnosisSceneResult>(),
            Actions = actions ?? Array.Empty<LedgerActionEntry>(),
            Conclusion = conclusion,
            UnresolvedReason = reason,
            Privilege = PrivilegeLevel.Administrator,
            StartedAt = start,
            FinishedAt = finishedAt ?? start.AddMinutes(30),
            ReportFileName = "report.html"
        };
    }

    /// <summary>枚举中文说明：问题类别、未解决原因与会话结论均能正确映射。</summary>
    [Fact]
    public void Describe_中文说明正确()
    {
        Assert.Equal("卡慢", LedgerRules.DescribeCategory(ProblemCategory.Slowness));
        Assert.Equal("打印与共享", LedgerRules.DescribeCategory(ProblemCategory.PrintingSharing));
        Assert.Equal("其他", LedgerRules.DescribeCategory(ProblemCategory.Other));
        Assert.Equal("需更换部件或联系厂商", LedgerRules.DescribeUnresolvedReason(UnresolvedReason.NeedPartOrVendor));
        Assert.Equal("其他", LedgerRules.DescribeUnresolvedReason(UnresolvedReason.Other));
        Assert.Equal("已解决", LedgerRules.DescribeConclusion(SessionConclusion.Resolved));
        Assert.Equal("待重启生效", LedgerRules.DescribeConclusion(SessionConclusion.PendingRestart));
        Assert.Equal("未解决", LedgerRules.DescribeConclusion(SessionConclusion.Unresolved));
        Assert.Equal("已中断", LedgerRules.DescribeConclusion(SessionConclusion.Interrupted));
    }

    /// <summary>诊断场景与问题类别一一对应。</summary>
    [Fact]
    public void MapSceneToCategory_一一对应()
    {
        Assert.Equal(ProblemCategory.Slowness, LedgerRules.MapSceneToCategory(DiagnosisScene.Slowness));
        Assert.Equal(ProblemCategory.AccountPrivilege, LedgerRules.MapSceneToCategory(DiagnosisScene.AccountPrivilege));
    }

    /// <summary>姓名遮蔽：1 字原样、2 字遮蔽名、3 字及以上保留首末字。</summary>
    [Fact]
    public void MaskOperatorName_按字数遮蔽()
    {
        Assert.Equal(string.Empty, LedgerRules.MaskOperatorName(null));
        Assert.Equal("张", LedgerRules.MaskOperatorName("张"));
        Assert.Equal("张*", LedgerRules.MaskOperatorName("张三"));
        Assert.Equal("张*明", LedgerRules.MaskOperatorName("张小明"));
        Assert.Equal("欧**明", LedgerRules.MaskOperatorName("欧阳小明"));
    }

    /// <summary>凭据掩码：键名与其余文本保留，仅替换参数值。</summary>
    [Fact]
    public void MaskCredentials_仅掩码参数值()
    {
        Assert.Equal("password=******", LedgerRules.MaskCredentials("password=secret"));
        Assert.Equal("passwd: ******", LedgerRules.MaskCredentials("passwd: hunter2"));
        Assert.Equal("pwd=******", LedgerRules.MaskCredentials("pwd=abc"));
        Assert.Equal("cmdkey /pass:******", LedgerRules.MaskCredentials("cmdkey /pass:xyz"));
        Assert.Equal("/password:******", LedgerRules.MaskCredentials("/password:q"));
        Assert.Equal("net use -p ******", LedgerRules.MaskCredentials("net use -p secret"));
        Assert.Equal("tool --password ******", LedgerRules.MaskCredentials("tool --password value"));
        Assert.Equal(string.Empty, LedgerRules.MaskCredentials(null));
        Assert.Equal("no credentials here", LedgerRules.MaskCredentials("no credentials here"));
    }

    /// <summary>处理人完整性：工号与姓名均需非空白。</summary>
    [Fact]
    public void IsCompleteOperator_两项均需填写()
    {
        Assert.True(LedgerRules.IsCompleteOperator(new OperatorInfo("E1", "张三")));
        Assert.False(LedgerRules.IsCompleteOperator(new OperatorInfo("", "张三")));
        Assert.False(LedgerRules.IsCompleteOperator(new OperatorInfo("E1", "  ")));
        Assert.False(LedgerRules.IsCompleteOperator(null));
    }

    /// <summary>记录校验：合法记录通过；关键字段缺失与“未解决无原因”被拦下。</summary>
    [Fact]
    public void Validate_关键字段校验()
    {
        Assert.True(LedgerRules.Validate(NewRecord()).IsValid);

        var noMachine = NewRecord(machineId: string.Empty);
        Assert.False(LedgerRules.Validate(noMachine).IsValid);
        Assert.Contains(LedgerRules.Validate(noMachine).Problems, problem => problem.Contains("机器标识"));

        var noReason = NewRecord(conclusion: SessionConclusion.Unresolved);
        Assert.False(LedgerRules.Validate(noReason).IsValid);
        Assert.Contains(LedgerRules.Validate(noReason).Problems, problem => problem.Contains("未解决原因"));

        var badOperator = NewRecord(operatorInfo: new OperatorInfo("E1", string.Empty));
        Assert.False(LedgerRules.Validate(badOperator).IsValid);

        var longSymptom = NewRecord(symptom: new string('x', LedgerRules.SymptomMaxLength + 1));
        Assert.False(LedgerRules.Validate(longSymptom).IsValid);
    }

    /// <summary>记录校验：结束时间早于开始时间被拦下。</summary>
    [Fact]
    public void Validate_结束时间早于开始时间不通过()
    {
        var start = new DateTimeOffset(2026, 10, 12, 19, 0, 0, Offset);
        var record = NewRecord(startedAt: start, finishedAt: start.AddMinutes(-1));
        Assert.False(LedgerRules.Validate(record).IsValid);
    }

    /// <summary>动作条目转换：输出摘要自动应用凭据掩码。</summary>
    [Fact]
    public void LedgerActionEntry_转换时掩码凭据()
    {
        var execution = new ActionExecutionResult(
            "net.reset-dns",
            "重置 DNS",
            RiskLevel.Medium,
            ActionOutcome.Success,
            0,
            "password=secret done",
            string.Empty,
            TimeSpan.FromSeconds(2),
            false,
            null,
            Array.Empty<ActionParameterValue>());

        var entry = LedgerActionEntry.FromExecution(execution);

        Assert.Equal("net.reset-dns", entry.ActionId);
        Assert.Equal("重置 DNS", entry.ActionName);
        Assert.Contains(LedgerRules.CredentialMask, entry.OutputSummary);
        Assert.DoesNotContain("secret", entry.OutputSummary);
    }

    /// <summary>统计：总数、机器数、按类别与处理人计数、未解决占比。</summary>
    [Fact]
    public void BuildStatistics_汇总计数()
    {
        var records = new[]
        {
            NewRecord(recordId: "rec-1", machineId: "M-1", category: ProblemCategory.Network),
            NewRecord(recordId: "rec-2", machineId: "M-1", category: ProblemCategory.Network, conclusion: SessionConclusion.Unresolved, reason: UnresolvedReason.CannotReproduce),
            NewRecord(recordId: "rec-3", machineId: "M-2", category: ProblemCategory.Slowness, operatorInfo: new OperatorInfo("E2", "李四"))
        };

        var statistics = LedgerRules.BuildStatistics(records);

        Assert.Equal(3, statistics.TotalRecords);
        Assert.Equal(2, statistics.MachineCount);
        Assert.Equal(1, statistics.UnresolvedCount);
        Assert.Equal(1d / 3, statistics.UnresolvedRatio, 3);
        Assert.Equal(new CategoryCount(ProblemCategory.Network, 2), statistics.CategoryCounts[0]);
        Assert.Contains(statistics.OperatorCounts, item => item.EmployeeId == "E2" && item.Count == 1);
    }

    /// <summary>统计：空集合时数量为 0、占比为 0。</summary>
    [Fact]
    public void BuildStatistics_空集合()
    {
        var statistics = LedgerRules.BuildStatistics(Array.Empty<HandlingRecord>());
        Assert.Equal(0, statistics.TotalRecords);
        Assert.Equal(0, statistics.MachineCount);
        Assert.Equal(0d, statistics.UnresolvedRatio);
    }

    /// <summary>筛选：类别、处理人工号（忽略大小写）与时间闭区间。</summary>
    [Fact]
    public void Filter_按条件筛选()
    {
        var start = new DateTimeOffset(2026, 10, 10, 9, 0, 0, Offset);
        var records = new[]
        {
            NewRecord(recordId: "rec-1", category: ProblemCategory.Network, operatorInfo: new OperatorInfo("E1001", "张三"), finishedAt: start),
            NewRecord(recordId: "rec-2", category: ProblemCategory.Slowness, operatorInfo: new OperatorInfo("E2002", "李四"), finishedAt: start.AddDays(1)),
            NewRecord(recordId: "rec-3", category: ProblemCategory.Network, operatorInfo: new OperatorInfo("E2002", "李四"), finishedAt: start.AddDays(2))
        };

        Assert.Equal(2, LedgerRules.Filter(records, category: ProblemCategory.Network).Count);
        Assert.Equal(2, LedgerRules.Filter(records, operatorEmployeeId: "e2002").Count);
        Assert.Single(LedgerRules.Filter(records, from: start, to: start.AddHours(12)));
        Assert.Single(LedgerRules.Filter(records, category: ProblemCategory.Network, operatorEmployeeId: "E2002"));
    }

    /// <summary>按机器归并：档案按最近处理时间倒序，档案内记录按处理时间倒序。</summary>
    [Fact]
    public void GroupByMachine_归并与排序()
    {
        var start = new DateTimeOffset(2026, 10, 10, 9, 0, 0, Offset);
        var records = new[]
        {
            NewRecord(recordId: "a-old", machineId: "A", hostName: "OLDPC", finishedAt: start),
            NewRecord(recordId: "a-new", machineId: "A", hostName: "NEWPC", finishedAt: start.AddHours(2)),
            NewRecord(recordId: "b-1", machineId: "B", finishedAt: start.AddHours(1))
        };

        var machines = LedgerRules.GroupByMachine(records);

        Assert.Equal(2, machines.Count);
        Assert.Equal("A", machines[0].MachineId);
        Assert.Equal(2, machines[0].ProcessingCount);
        Assert.Equal("a-new", machines[0].Records[0].RecordId);
        Assert.Equal("NEWPC", machines[0].HostName);
        Assert.Equal(SessionConclusion.Resolved, machines[0].LastConclusion);
    }

    /// <summary>机器标识冲突：时间区间重叠时标记冲突，不重叠不标记。</summary>
    [Fact]
    public void DetectMachineIdConflicts_仅重叠时标记()
    {
        var baseTime = new DateTimeOffset(2026, 10, 12, 10, 0, 0, Offset);
        var overlapping = new[]
        {
            NewRecord(recordId: "r1", machineId: "M", startedAt: baseTime, finishedAt: baseTime.AddHours(1)),
            NewRecord(recordId: "r2", machineId: "M", startedAt: baseTime.AddMinutes(30), finishedAt: baseTime.AddHours(2))
        };
        var conflict = Assert.Single(LedgerRules.DetectMachineIdConflicts(overlapping));
        Assert.Equal("M", conflict.MachineId);

        var separate = new[]
        {
            NewRecord(recordId: "r1", machineId: "M", startedAt: baseTime, finishedAt: baseTime.AddHours(1)),
            NewRecord(recordId: "r2", machineId: "M", startedAt: baseTime.AddHours(2), finishedAt: baseTime.AddHours(3))
        };
        Assert.Empty(LedgerRules.DetectMachineIdConflicts(separate));
    }

    /// <summary>报告文件名主干：机器标识与会话标识做 ASCII 安全化，处理时间格式化。</summary>
    [Fact]
    public void BuildReportFileName_命名与安全化()
    {
        var record = NewRecord(
            machineId: "host name|123",
            sessionId: "sess-20261012-193000-ab12",
            finishedAt: new DateTimeOffset(2026, 10, 12, 19, 30, 0, Offset));

        var name = LedgerRules.BuildReportFileName(record);

        Assert.Equal("host_name_123_20261012-193000_sess-20261012-193000-ab12", name);
    }

    /// <summary>脱敏：处理人姓名被遮蔽（工号保留）、动作输出与诊断依据中的凭据被掩码。</summary>
    [Fact]
    public void Desensitize_遮蔽姓名与凭据()
    {
        var finding = new DiagnosisFinding(
            "net.dns",
            DiagnosisScene.Network,
            "DNS 解析",
            DiagnosisExecutionStatus.Executed,
            DiagnosisConclusion.Abnormal,
            "解析失败",
            new[] { new DiagnosisEvidence("上游服务器", "password=abc") },
            Array.Empty<string>(),
            TimeSpan.FromSeconds(1));
        var scene = new DiagnosisSceneResult(DiagnosisScene.Network, new[] { finding }, TimeSpan.FromSeconds(1));

        var record = NewRecord(
            operatorInfo: new OperatorInfo("E1001", "张小明"),
            scenes: new[] { scene },
            actions: new[] { new LedgerActionEntry("net.reset-dns", "重置 DNS", RiskLevel.Medium, ActionOutcome.Success, 2, "password=secret ok") });

        var masked = LedgerRules.Desensitize(record);

        Assert.Equal("E1001", masked.Operator.EmployeeId);
        Assert.Equal("张*明", masked.Operator.Name);
        Assert.Equal("password=****** ok", masked.Actions[0].OutputSummary);
        Assert.Equal("password=******", masked.Scenes[0].Findings[0].Evidence[0].Value);
        Assert.Equal("net.dns", masked.Scenes[0].Findings[0].FindingId);
        Assert.Equal("张小明", record.Operator.Name);
    }

    /// <summary>记录标识：形如 rec-yyyyMMdd-HHmmss-xxxx，且同一时刻多次生成互不相同。</summary>
    [Fact]
    public void NewRecordId_格式与唯一性()
    {
        var time = new DateTimeOffset(2026, 10, 12, 19, 30, 0, Offset);
        Assert.Equal("rec-20261012-193000-ab12", LedgerRules.NewRecordId(time, "ab12"));
        Assert.StartsWith("rec-20261012-193000-", LedgerRules.NewRecordId(time));
        Assert.NotEqual(LedgerRules.NewRecordId(time), LedgerRules.NewRecordId(time));
    }
}