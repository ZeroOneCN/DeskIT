namespace DeskIT.Core;

/// <summary>
/// 启动阶段的可单元测试纯规则：空间阈值、系统受支持性、完整性结论、能力推导与最终判定。
/// 本类不依赖任何 Windows API，仅做判定，具体探测由 DeskIT.Windows 提供原始数据。
/// </summary>
public static class StartupRules
{
    /// <summary>可开始新会话的介质可用空间下限（200 MB）。</summary>
    public const long MinimumFreeBytesForNewSession = 200L * 1024 * 1024;

    /// <summary>触发归档提示的介质可用空间阈值（500 MB）。</summary>
    public const long ArchiveHintFreeBytes = 500L * 1024 * 1024;

    /// <summary>Windows 10 1809 的内部版本号，作为最低支持版本。</summary>
    public const int MinimumWindowsBuild = 17763;

    /// <summary>
    /// 按剩余空间评估介质可用空间状态。
    /// </summary>
    /// <param name="freeBytes">介质剩余可用字节数，负值按 0 处理。</param>
    /// <returns>空间评估结果：不足（&lt;200 MB）、归档提示（200~500 MB）、充足（≥500 MB）。</returns>
    public static SpaceAssessment EvaluateSpace(long freeBytes)
    {
        if (freeBytes < MinimumFreeBytesForNewSession)
        {
            return SpaceAssessment.Insufficient;
        }

        return freeBytes < ArchiveHintFreeBytes ? SpaceAssessment.ArchiveHint : SpaceAssessment.Normal;
    }

    /// <summary>
    /// 判定目标系统版本是否受支持：仅 Windows 10 1809+ / Windows 11 的 x64。
    /// </summary>
    /// <param name="major">主版本号（Windows 10/11 均为 10）。</param>
    /// <param name="build">内部版本号。</param>
    /// <param name="is64Bit">是否为 64 位操作系统。</param>
    /// <returns>受支持返回 Supported；Win7/8/8.1、32 位或过低版本返回 Unsupported。</returns>
    public static OsSupport EvaluateOsSupport(int major, int build, bool is64Bit)
    {
        if (!is64Bit)
        {
            return OsSupport.Unsupported;
        }

        // Windows 7（6.1）/ 8（6.2）/ 8.1（6.3）一律不支持。
        if (major != 10)
        {
            return OsSupport.Unsupported;
        }

        return build >= MinimumWindowsBuild ? OsSupport.Supported : OsSupport.Unsupported;
    }

    /// <summary>
    /// 依据清单比对结果给出程序完整性结论。
    /// </summary>
    /// <param name="manifestPresent">清单文件是否存在。</param>
    /// <param name="total">清单中登记的文件总数。</param>
    /// <param name="matched">哈希比对通过的文件数。</param>
    /// <returns>缺失清单或空清单记为警示；全部匹配记为通过；存在不匹配记为失败（阻断启动）。</returns>
    public static SelfCheckOutcome EvaluateIntegrity(bool manifestPresent, int total, int matched)
    {
        if (!manifestPresent || total <= 0)
        {
            // 开发期或未生成清单时不阻断启动，但如实提示。
            return SelfCheckOutcome.Warn;
        }

        return matched == total ? SelfCheckOutcome.Pass : SelfCheckOutcome.Fail;
    }

    /// <summary>
    /// 由权限级别、系统受支持性、介质空间与高级模式开关推导本次会话能力集合。
    /// </summary>
    /// <param name="privilege">当前权限级别。</param>
    /// <param name="osSupport">目标系统受支持性。</param>
    /// <param name="space">介质可用空间评估。</param>
    /// <param name="advancedModeEnabled">高级模式是否已开启（介质级记住，需主密码验证）。</param>
    /// <returns>能力集合，含受限原因清单。</returns>
    public static CapabilitySet DeriveCapabilities(
        PrivilegeLevel privilege,
        OsSupport osSupport,
        SpaceAssessment space,
        bool advancedModeEnabled)
    {
        var isAdministrator = privilege == PrivilegeLevel.Administrator;
        var isSupported = osSupport == OsSupport.Supported;
        var canChange = isAdministrator && isSupported;

        var restrictions = new List<string>();
        if (!isAdministrator)
        {
            restrictions.Add("只读诊断模式：修复动作库全部禁用（鼠标悬停可查看原因）。");
        }

        if (!isSupported)
        {
            restrictions.Add("目标系统不受支持：已进入受限启动，仅允许只读采集与导出，动作库禁用。");
        }

        switch (space)
        {
            case SpaceAssessment.Insufficient:
                restrictions.Add("介质可用空间低于 200 MB：不再开始新会话，请先归档或更换介质。");
                break;
            case SpaceAssessment.ArchiveHint:
                restrictions.Add("介质可用空间低于 500 MB：建议尽快归档报告与台账。");
                break;
        }

        return new CapabilitySet(
            CanCollect: true,
            CanDiagnose: true,
            CanFix: canChange,
            CanInstall: canChange,
            CanCustomCommand: canChange && advancedModeEnabled,
            CanStartNewSession: space != SpaceAssessment.Insufficient,
            Restrictions: restrictions);
    }

    /// <summary>
    /// 汇总各项自检结论，生成启动判定结果。
    /// </summary>
    /// <param name="privilege">当前权限级别。</param>
    /// <param name="osSupport">目标系统受支持性。</param>
    /// <param name="space">介质可用空间评估。</param>
    /// <param name="advancedModeEnabled">高级模式是否已开启。</param>
    /// <param name="integrity">程序完整性自检结论。</param>
    /// <returns>启动判定结果；完整性失败时 CanStart=false 并给出阻断原因。</returns>
    public static StartupVerdict BuildVerdict(
        PrivilegeLevel privilege,
        OsSupport osSupport,
        SpaceAssessment space,
        bool advancedModeEnabled,
        SelfCheckOutcome integrity)
    {
        var capabilities = DeriveCapabilities(privilege, osSupport, space, advancedModeEnabled);
        var canStart = integrity != SelfCheckOutcome.Fail;

        var blockReason = canStart
            ? null
            : "程序文件完整性校验未通过：可能被篡改或复制损坏，已阻止启动，请重新从可信来源部署。";

        return new StartupVerdict(canStart, privilege, osSupport, space, capabilities, blockReason);
    }
}