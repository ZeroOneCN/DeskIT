using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>启动自检的输入参数。</summary>
/// <param name="ProgramDirectory">程序所在目录（以可执行文件所在目录为基准）。</param>
/// <param name="DataDirectory">U 盘数据目录。</param>
/// <param name="Privilege">当前权限级别。</param>
/// <param name="IsFirstInstance">当前进程是否为唯一实例。</param>
/// <param name="AdvancedModeEnabled">高级模式是否已开启（介质级记住）。</param>
public sealed record StartupSelfCheckRequest(
    string ProgramDirectory,
    string DataDirectory,
    PrivilegeLevel Privilege,
    bool IsFirstInstance,
    bool AdvancedModeEnabled);

/// <summary>启动自检结果：逐项自检状态与最终启动判定。</summary>
/// <param name="Items">自检项结果清单（顺序与 PRD M1 一致）。</param>
/// <param name="Verdict">启动判定结果。</param>
public sealed record StartupSelfCheckResult(IReadOnlyList<SelfCheckItem> Items, StartupVerdict Verdict);

/// <summary>启动自检服务：逐项执行 6 项自检并汇总为启动判定。</summary>
public interface IStartupSelfCheckService
{
    /// <summary>
    /// 执行启动自检。
    /// </summary>
    /// <param name="request">自检输入参数。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>自检结果（逐项状态 + 启动判定）。</returns>
    Task<StartupSelfCheckResult> RunAsync(StartupSelfCheckRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 启动自检服务实现。负责采集原始事实（哈希、可写性、空间、系统版本），
/// 判定逻辑一律委托给 DeskIT.Core 的 StartupRules，保证规则可单元测试。
/// </summary>
public sealed class StartupSelfCheckService : IStartupSelfCheckService
{
    private readonly IProgramIntegrityVerifier _integrityVerifier;
    private readonly IDataDirectoryProbe _dataDirectoryProbe;
    private readonly IDriveSpaceProvider _driveSpaceProvider;
    private readonly IOsVersionInfoProvider _osVersionInfoProvider;

    /// <summary>初始化启动自检服务。</summary>
    /// <param name="integrityVerifier">程序完整性校验器。</param>
    /// <param name="dataDirectoryProbe">数据目录读写探测器。</param>
    /// <param name="driveSpaceProvider">介质可用空间提供者。</param>
    /// <param name="osVersionInfoProvider">系统版本信息提供者。</param>
    public StartupSelfCheckService(
        IProgramIntegrityVerifier integrityVerifier,
        IDataDirectoryProbe dataDirectoryProbe,
        IDriveSpaceProvider driveSpaceProvider,
        IOsVersionInfoProvider osVersionInfoProvider)
    {
        _integrityVerifier = integrityVerifier;
        _dataDirectoryProbe = dataDirectoryProbe;
        _driveSpaceProvider = driveSpaceProvider;
        _osVersionInfoProvider = osVersionInfoProvider;
    }

    /// <inheritdoc />
    public async Task<StartupSelfCheckResult> RunAsync(
        StartupSelfCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        var items = new List<SelfCheckItem>();

        // ① 程序文件完整性
        var integrity = await _integrityVerifier.VerifyAsync(request.ProgramDirectory, cancellationToken);
        var integrityOutcome = StartupRules.EvaluateIntegrity(integrity.ManifestPresent, integrity.Total, integrity.Matched);
        items.Add(new SelfCheckItem("程序完整性", integrityOutcome, DescribeIntegrity(integrity)));

        // ② 数据目录可读写
        var writable = await _dataDirectoryProbe.CanReadWriteAsync(request.DataDirectory);
        items.Add(new SelfCheckItem(
            "数据目录可写",
            writable ? SelfCheckOutcome.Pass : SelfCheckOutcome.Fail,
            writable ? $"{request.DataDirectory} 可读写" : $"{request.DataDirectory} 不可读写（介质写保护或权限不足）"));

        // ③ 介质可用空间
        var freeBytes = _driveSpaceProvider.GetAvailableFreeBytes(request.DataDirectory);
        var space = StartupRules.EvaluateSpace(freeBytes);
        items.Add(new SelfCheckItem("介质可用空间", SpaceOutcome(space), DescribeSpace(space, freeBytes)));

        // ④ 目标系统版本
        var os = _osVersionInfoProvider.GetVersion();
        var osSupport = StartupRules.EvaluateOsSupport(os.Major, os.Build, os.Is64Bit);
        items.Add(new SelfCheckItem(
            "目标系统版本",
            osSupport == OsSupport.Supported ? SelfCheckOutcome.Pass : SelfCheckOutcome.Fail,
            osSupport == OsSupport.Supported
                ? os.DisplayName
                : $"{os.DisplayName}，仅支持 Windows 10 1809+ / Windows 11 的 x64"));

        // ⑤ 当前权限
        items.Add(new SelfCheckItem(
            "当前权限",
            request.Privilege == PrivilegeLevel.Administrator ? SelfCheckOutcome.Pass : SelfCheckOutcome.Warn,
            request.Privilege == PrivilegeLevel.Administrator
                ? "管理员模式：可执行修复动作"
                : "标准用户：仅可执行只读采集与诊断"));

        // ⑥ 并发实例
        items.Add(new SelfCheckItem(
            "并发实例",
            SelfCheckOutcome.Pass,
            request.IsFirstInstance ? "本机未检测到其他运行中的实例" : "本机已有实例在运行"));

        var verdict = StartupRules.BuildVerdict(
            request.Privilege,
            osSupport,
            space,
            request.AdvancedModeEnabled,
            integrityOutcome);

        return new StartupSelfCheckResult(items, verdict);
    }

    /// <summary>描述程序完整性自检结果。</summary>
    /// <param name="report">完整性校验报告。</param>
    /// <returns>面向 IT 人员的说明文本。</returns>
    private static string DescribeIntegrity(IntegrityReport report)
    {
        if (!report.ManifestPresent)
        {
            return "未找到 manifest.json，跳过哈希比对（开发构建或未生成清单）";
        }

        if (report.Total == 0)
        {
            return "清单内容为空，跳过哈希比对";
        }

        if (report.Mismatches.Count == 0)
        {
            return $"清单哈希一致（{report.Matched}/{report.Total} 文件）";
        }

        var preview = string.Join("、", report.Mismatches.Take(3));
        var suffix = report.Mismatches.Count > 3 ? " 等" : string.Empty;
        return $"哈希不匹配（{report.Matched}/{report.Total} 文件），可疑文件：{preview}{suffix}";
    }

    /// <summary>将空间评估映射为自检结果状态。</summary>
    /// <param name="assessment">空间评估结果。</param>
    /// <returns>充足记为通过；归档提示与不足记为警示。</returns>
    private static SelfCheckOutcome SpaceOutcome(SpaceAssessment assessment)
        => assessment == SpaceAssessment.Normal ? SelfCheckOutcome.Pass : SelfCheckOutcome.Warn;

    /// <summary>描述介质可用空间自检结果。</summary>
    /// <param name="assessment">空间评估结果。</param>
    /// <param name="freeBytes">剩余可用字节数。</param>
    /// <returns>面向 IT 人员的说明文本。</returns>
    private static string DescribeSpace(SpaceAssessment assessment, long freeBytes)
    {
        var size = FormatBytes(freeBytes);
        return assessment switch
        {
            SpaceAssessment.Insufficient => $"剩余 {size}，低于 200 MB，不再开始新会话",
            SpaceAssessment.ArchiveHint => $"剩余 {size}，低于 500 MB，建议尽快归档",
            _ => $"剩余 {size}"
        };
    }

    /// <summary>把字节数格式化为便于阅读的字符串。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>如 “6.4 GB” / “320 MB”。</returns>
    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.0} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0} MB";
        }

        return $"{bytes / kb:0} KB";
    }
}