using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>启动规则（StartupRules）的单元测试：空间阈值、系统受支持性、完整性结论、能力推导与最终判定。</summary>
public class StartupRulesTests
{
    private const long Mb = 1024L * 1024;

    /// <summary>空间阈值：低于 200 MB 记为不足；200~500 MB 记为归档提示；不低于 500 MB 记为充足。</summary>
    [Fact]
    public void EvaluateSpace_按200与500MB阈值分级()
    {
        Assert.Equal(SpaceAssessment.Insufficient, StartupRules.EvaluateSpace(199 * Mb));
        Assert.Equal(SpaceAssessment.ArchiveHint, StartupRules.EvaluateSpace(200 * Mb));
        Assert.Equal(SpaceAssessment.ArchiveHint, StartupRules.EvaluateSpace(499 * Mb));
        Assert.Equal(SpaceAssessment.Normal, StartupRules.EvaluateSpace(500 * Mb));
        Assert.Equal(SpaceAssessment.Normal, StartupRules.EvaluateSpace(6 * 1024 * Mb));
    }

    /// <summary>仅 Windows 10 1809+ / Windows 11 的 x64 受支持；Win7/8.1、32 位、过低版本均不受支持。</summary>
    [Fact]
    public void EvaluateOsSupport_仅支持Win10_1809以上或Win11的x64()
    {
        Assert.Equal(OsSupport.Supported, StartupRules.EvaluateOsSupport(10, 17763, is64Bit: true));
        Assert.Equal(OsSupport.Supported, StartupRules.EvaluateOsSupport(10, 19045, is64Bit: true));
        Assert.Equal(OsSupport.Supported, StartupRules.EvaluateOsSupport(10, 22621, is64Bit: true));

        Assert.Equal(OsSupport.Unsupported, StartupRules.EvaluateOsSupport(10, 17762, is64Bit: true));
        Assert.Equal(OsSupport.Unsupported, StartupRules.EvaluateOsSupport(10, 22621, is64Bit: false));
        Assert.Equal(OsSupport.Unsupported, StartupRules.EvaluateOsSupport(6, 7601, is64Bit: true));
        Assert.Equal(OsSupport.Unsupported, StartupRules.EvaluateOsSupport(6, 9600, is64Bit: true));
    }

    /// <summary>完整性：全部匹配为通过；存在不匹配为失败（阻断启动）；清单缺失或为空为警示。</summary>
    [Fact]
    public void EvaluateIntegrity_区分通过失败与警示()
    {
        Assert.Equal(SelfCheckOutcome.Pass, StartupRules.EvaluateIntegrity(true, 12, 12));
        Assert.Equal(SelfCheckOutcome.Fail, StartupRules.EvaluateIntegrity(true, 12, 11));
        Assert.Equal(SelfCheckOutcome.Warn, StartupRules.EvaluateIntegrity(false, 0, 0));
        Assert.Equal(SelfCheckOutcome.Warn, StartupRules.EvaluateIntegrity(true, 0, 0));
    }

    /// <summary>管理员模式且系统受支持时可执行修复；标准用户或系统不受支持时一律禁用。</summary>
    [Fact]
    public void DeriveCapabilities_修复能力取决于权限与系统受支持性()
    {
        var admin = StartupRules.DeriveCapabilities(
            PrivilegeLevel.Administrator, OsSupport.Supported, SpaceAssessment.Normal, advancedModeEnabled: false);
        Assert.True(admin.CanFix);
        Assert.True(admin.CanInstall);
        Assert.False(admin.CanCustomCommand);
        Assert.False(admin.IsReadOnlyMode);

        var standard = StartupRules.DeriveCapabilities(
            PrivilegeLevel.Standard, OsSupport.Supported, SpaceAssessment.Normal, advancedModeEnabled: true);
        Assert.False(standard.CanFix);
        Assert.False(standard.CanInstall);
        Assert.False(standard.CanCustomCommand);
        Assert.True(standard.CanCollect);
        Assert.True(standard.CanDiagnose);
        Assert.True(standard.IsReadOnlyMode);

        var unsupported = StartupRules.DeriveCapabilities(
            PrivilegeLevel.Administrator, OsSupport.Unsupported, SpaceAssessment.Normal, advancedModeEnabled: true);
        Assert.False(unsupported.CanFix);
        Assert.False(unsupported.CanCustomCommand);
    }

    /// <summary>自定义命令需管理员模式 + 系统受支持 + 高级模式开启三者同时满足。</summary>
    [Fact]
    public void DeriveCapabilities_自定义命令需管理员与高级模式同时满足()
    {
        var enabled = StartupRules.DeriveCapabilities(
            PrivilegeLevel.Administrator, OsSupport.Supported, SpaceAssessment.Normal, advancedModeEnabled: true);
        Assert.True(enabled.CanCustomCommand);
    }

    /// <summary>介质空间不足时不允许开始新会话，并给出受限原因。</summary>
    [Fact]
    public void DeriveCapabilities_空间不足时禁止开始新会话()
    {
        var caps = StartupRules.DeriveCapabilities(
            PrivilegeLevel.Administrator, OsSupport.Supported, SpaceAssessment.Insufficient, advancedModeEnabled: false);

        Assert.False(caps.CanStartNewSession);
        Assert.Contains(caps.Restrictions, r => r.Contains("200 MB"));

        var hint = StartupRules.DeriveCapabilities(
            PrivilegeLevel.Administrator, OsSupport.Supported, SpaceAssessment.ArchiveHint, advancedModeEnabled: false);

        Assert.True(hint.CanStartNewSession);
        Assert.Contains(hint.Restrictions, r => r.Contains("500 MB"));
    }

    /// <summary>完整性失败时阻止启动并给出阻断原因；通过时允许启动且无阻断原因。</summary>
    [Fact]
    public void BuildVerdict_完整性失败时阻止启动()
    {
        var blocked = StartupRules.BuildVerdict(
            PrivilegeLevel.Standard, OsSupport.Supported, SpaceAssessment.Normal, false, SelfCheckOutcome.Fail);
        Assert.False(blocked.CanStart);
        Assert.NotNull(blocked.BlockReason);

        var ok = StartupRules.BuildVerdict(
            PrivilegeLevel.Standard, OsSupport.Supported, SpaceAssessment.Normal, false, SelfCheckOutcome.Pass);
        Assert.True(ok.CanStart);
        Assert.Null(ok.BlockReason);
        Assert.False(ok.IsAdministrator);
    }
}