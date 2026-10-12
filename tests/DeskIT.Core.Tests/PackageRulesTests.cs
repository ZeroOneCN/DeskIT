using System.Collections.Generic;
using DeskIT.Core;

namespace DeskIT.Core.Tests;

/// <summary>M5 软件包纯规则测试：完整性校验、架构与空间检查、已装匹配、退出码归因与动作构建。</summary>
public sealed class PackageRulesTests
{
    /// <summary>构造一个用于测试的包描述（仅覆盖默认值即可运行）。</summary>
    /// <param name="version">包携带版本。</param>
    /// <param name="sha256">声明的校验哈希。</param>
    /// <param name="publisher">发布者。</param>
    /// <returns>包描述。</returns>
    private static SoftwarePackageDescriptor Descriptor(
        string version = "1.0.0",
        string? sha256 = "ABCDEF",
        string publisher = "")
        => new()
        {
            PackageId = "sample",
            Name = "示例软件",
            Version = version,
            Publisher = publisher,
            InstallerFileName = "installer.exe",
            InstallerArguments = "/quiet /norestart",
            Sha256 = sha256
        };

    [Fact]
    public void 文件缺失优先判定为文件缺失()
    {
        Assert.Equal(PackageIntegrityStatus.FileMissing, PackageRules.EvaluateIntegrity(false, null, null));
        Assert.Equal(PackageIntegrityStatus.FileMissing, PackageRules.EvaluateIntegrity(false, "AA", "AA"));
    }

    [Fact]
    public void 未声明哈希视为来源不可验证()
    {
        Assert.Equal(PackageIntegrityStatus.HashNotDeclared, PackageRules.EvaluateIntegrity(true, "  ", "AA"));
        Assert.False(PackageRules.CanExecute(PackageIntegrityStatus.HashNotDeclared));
    }

    [Fact]
    public void 哈希不匹配时拒绝执行()
    {
        Assert.Equal(PackageIntegrityStatus.HashMismatch, PackageRules.EvaluateIntegrity(true, "AA", "BB"));
        Assert.False(PackageRules.CanExecute(PackageIntegrityStatus.HashMismatch));
    }

    [Fact]
    public void 哈希大小写与分隔符不敏感且校验通过时才允许执行()
    {
        Assert.Equal(PackageIntegrityStatus.Verified, PackageRules.EvaluateIntegrity(true, "ab-cd ef", "ABCDEF"));
        Assert.True(PackageRules.CanExecute(PackageIntegrityStatus.Verified));
    }

    [Fact]
    public void 架构兼容判定()
    {
        Assert.True(PackageRules.ArchitectureCompatible(PackageArchitecture.Any, false));
        Assert.False(PackageRules.ArchitectureCompatible(PackageArchitecture.X64, false));
        Assert.True(PackageRules.ArchitectureCompatible(PackageArchitecture.X64, true));
        Assert.True(PackageRules.ArchitectureCompatible(PackageArchitecture.X86, true));
        Assert.Equal("64 位", PackageRules.DescribeArchitecture(PackageArchitecture.X64));
    }

    [Fact]
    public void 可用空间不足即不满足()
    {
        Assert.True(PackageRules.DiskSpaceSufficient(500L * 1024 * 1024, 200));
        Assert.False(PackageRules.DiskSpaceSufficient(100L * 1024 * 1024, 200));
        Assert.True(PackageRules.DiskSpaceSufficient(0, 0));
    }

    [Fact]
    public void 版本比较优先按数字版本()
    {
        Assert.True(PackageRules.CompareVersions("1.10.0", "1.9.0") > 0);
        Assert.Equal(0, PackageRules.CompareVersions("1.0", "1.0.0.0"));
        Assert.True(PackageRules.CompareVersions("1.0", "2.0") < 0);
    }

    [Fact]
    public void 已装匹配按名称与发布者判定()
    {
        var installed = new List<InstalledSoftwareEntry>
        {
            new("示例软件", "示例公司", "1.0.0", "uninstall.exe")
        };

        Assert.Equal(
            PackageMatchStatus.InstalledSameVersion,
            PackageRules.MatchInstalled(Descriptor(version: "1.0.0", publisher: "示例公司"), installed));

        Assert.Equal(
            PackageMatchStatus.InstalledDifferentVersion,
            PackageRules.MatchInstalled(Descriptor(version: "2.0.0", publisher: "示例公司"), installed));

        Assert.Equal(
            PackageMatchStatus.NotInstalled,
            PackageRules.MatchInstalled(Descriptor(publisher: "其他公司"), installed));

        Assert.Equal(
            PackageMatchStatus.NotInstalled,
            PackageRules.MatchInstalled(Descriptor(), new List<InstalledSoftwareEntry>()));
    }

    [Fact]
    public void 未声明发布者时仅按名称匹配()
    {
        var installed = new List<InstalledSoftwareEntry>
        {
            new("示例软件", "任意公司", "1.0.0", null)
        };

        Assert.Equal(
            PackageMatchStatus.InstalledSameVersion,
            PackageRules.MatchInstalled(Descriptor(version: "1.0.0"), installed));
    }

    [Fact]
    public void 包版本为空时记为版本未知()
    {
        var installed = new List<InstalledSoftwareEntry>
        {
            new("示例软件", null, "1.0.0", null)
        };

        Assert.Equal(
            PackageMatchStatus.InstalledUnknownVersion,
            PackageRules.MatchInstalled(Descriptor(version: ""), installed));
    }

    [Theory]
    [InlineData(0, InstallerOutcomeKind.Success, true, false)]
    [InlineData(3010, InstallerOutcomeKind.SuccessPendingRestart, true, true)]
    [InlineData(1641, InstallerOutcomeKind.SuccessPendingRestart, true, true)]
    [InlineData(1618, InstallerOutcomeKind.AlreadyInProgress, false, false)]
    [InlineData(1625, InstallerOutcomeKind.BlockedByPolicy, false, false)]
    [InlineData(1260, InstallerOutcomeKind.BlockedByPolicy, false, false)]
    [InlineData(1603, InstallerOutcomeKind.FatalError, false, false)]
    [InlineData(1638, InstallerOutcomeKind.VersionConflict, false, false)]
    [InlineData(5, InstallerOutcomeKind.BlockedBySecuritySoftware, false, false)]
    [InlineData(-1, InstallerOutcomeKind.Unknown, false, false)]
    [InlineData(9999, InstallerOutcomeKind.Unknown, false, false)]
    public void 安装器退出码归因(int exitCode, InstallerOutcomeKind kind, bool success, bool restart)
    {
        var outcome = PackageRules.ClassifyInstallerExitCode(exitCode);
        Assert.Equal(kind, outcome.Kind);
        Assert.Equal(success, outcome.Success);
        Assert.Equal(restart, outcome.RequiresRestart);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Description));
    }

    [Fact]
    public void 卸载与强制重装为高风险()
    {
        Assert.Equal(RiskLevel.Medium, PackageRules.RiskFor(PackageOperationKind.Install, false));
        Assert.Equal(RiskLevel.High, PackageRules.RiskFor(PackageOperationKind.Install, true));
        Assert.Equal(RiskLevel.High, PackageRules.RiskFor(PackageOperationKind.Uninstall, false));
    }

    [Fact]
    public void 安装动作构建为可复用M4定义且占位符全部有来源()
    {
        var action = PackageRules.BuildOperationAction(
            Descriptor(),
            new PackageActionContext(PackageOperationKind.Install, @"E:\DeskIT\packages\installer.exe", "/quiet /norestart", null, false));

        Assert.Equal("package.install.sample", action.ActionId);
        Assert.Equal(RiskLevel.Medium, action.Risk);
        Assert.Equal(DiagnosisScene.Software, action.Scene);
        Assert.True(action.RequiresAdministrator);
        Assert.Contains("Start-Process", action.Script);
        Assert.Contains("/quiet /norestart", action.Script);
        Assert.Empty(ActionRules.UnresolvedTokens(action));
    }

    [Fact]
    public void 静默参数中的美元符号会被转义避免被当作脚本执行()
    {
        var action = PackageRules.BuildOperationAction(
            Descriptor(),
            new PackageActionContext(PackageOperationKind.Install, @"C:\a.exe", "/S \"$env:TEMP\"", null, false));

        Assert.Contains("`$env:TEMP", action.Script);
    }

    [Fact]
    public void 无专用卸载程序时使用目标机卸载登记项()
    {
        var action = PackageRules.BuildOperationAction(
            Descriptor(),
            new PackageActionContext(PackageOperationKind.Uninstall, null, null, "MsiExec.exe /X{GUID}", false));

        Assert.Equal("package.uninstall.sample", action.ActionId);
        Assert.Equal(RiskLevel.High, action.Risk);
        Assert.Contains("$env:ComSpec", action.Script);
        Assert.Contains("MsiExec.exe /X{GUID}", action.Script);
        Assert.False(action.CanRollback);
        Assert.Empty(ActionRules.UnresolvedTokens(action));
    }

    [Fact]
    public void 完整性处置说明覆盖三种不可用原因()
    {
        Assert.Equal(string.Empty, PackageRules.DescribeIntegrityRemedy(PackageIntegrityStatus.Verified));
        Assert.Contains("重新部署", PackageRules.DescribeIntegrityRemedy(PackageIntegrityStatus.HashMismatch));
        Assert.Contains("同步到 U 盘", PackageRules.DescribeIntegrityRemedy(PackageIntegrityStatus.FileMissing));
        Assert.Contains("来源不可验证", PackageRules.DescribeIntegrityRemedy(PackageIntegrityStatus.HashNotDeclared));
    }
}