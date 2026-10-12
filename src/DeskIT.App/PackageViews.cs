using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeskIT.Core;
using DeskIT.Windows;

namespace DeskIT.App;

/// <summary>
/// 一个 U 盘软件包的界面视图：完整性校验、架构与空间要求、与目标机已装软件的匹配结果。
/// 仅作为展示与承载选中项的载体，安装 / 卸载的实际判定与执行由包服务负责。
/// </summary>
internal sealed class PackageItemView
{
    /// <summary>
    /// 初始化软件包视图。
    /// </summary>
    /// <param name="package">软件包（含完整性校验结果）。</param>
    /// <param name="match">与目标机已装软件的匹配状态。</param>
    /// <param name="installedVersion">目标机上匹配到的版本；未匹配到为 null。</param>
    public PackageItemView(SoftwarePackage package, PackageMatchStatus match, string? installedVersion)
    {
        Package = package;
        var descriptor = package.Descriptor;

        PackageId = descriptor.PackageId;
        Name = descriptor.Name;
        Version = descriptor.Version;
        HeaderText = $"{descriptor.Name} {descriptor.Version}";
        PublisherText = string.IsNullOrWhiteSpace(descriptor.Publisher)
            ? "发布者：未声明"
            : $"发布者：{descriptor.Publisher}";
        ArchitectureText = $"适用架构：{PackageRules.DescribeArchitecture(descriptor.Architecture)}";
        SupportedOsText = string.IsNullOrWhiteSpace(descriptor.SupportedOs)
            ? string.Empty
            : $"适用系统：{descriptor.SupportedOs}";
        HasSupportedOs = SupportedOsText.Length > 0;
        SizeText = package.InstallerSizeBytes > 0
            ? $"安装程序：{PackageRules.FormatBytes(package.InstallerSizeBytes)}"
            : "安装程序：未就绪";
        MinFreeDiskText = descriptor.MinFreeDiskMb > 0
            ? $"安装前要求可用空间：≥ {descriptor.MinFreeDiskMb} MB"
            : "安装前空间要求：未声明";
        TimeoutText = $"单项超时 {descriptor.TimeoutSeconds / 60.0:0.#} 分钟";
        ArgumentsText = string.IsNullOrWhiteSpace(descriptor.InstallerArguments)
            ? "静默参数：未声明"
            : $"静默参数：{descriptor.InstallerArguments}";

        Integrity = package.Check.Status;
        IntegrityText = PackageRules.DescribeIntegrity(package.Check.Status);
        IntegrityRemedy = PackageRules.DescribeIntegrityRemedy(package.Check.Status);
        HasRemedy = IntegrityRemedy.Length > 0;
        CanExecute = package.CanExecute;

        Match = match;
        MatchText = PackageRules.DescribeMatch(match);
        MatchDetailText = string.IsNullOrWhiteSpace(installedVersion)
            ? $"目标机：{MatchText}"
            : $"目标机：{MatchText}（{installedVersion}）";

        Notes = descriptor.Notes ?? string.Empty;
        HasNotes = Notes.Length > 0;
    }

    /// <summary>软件包（含完整性校验结果与安装程序路径）。</summary>
    public SoftwarePackage Package { get; }

    /// <summary>包标识。</summary>
    public string PackageId { get; }

    /// <summary>软件名称。</summary>
    public string Name { get; }

    /// <summary>包携带的版本。</summary>
    public string Version { get; }

    /// <summary>列表标题（名称 + 版本）。</summary>
    public string HeaderText { get; }

    /// <summary>发布者文本。</summary>
    public string PublisherText { get; }

    /// <summary>适用架构文本。</summary>
    public string ArchitectureText { get; }

    /// <summary>适用系统文本；未声明时为空。</summary>
    public string SupportedOsText { get; }

    /// <summary>是否声明了适用系统。</summary>
    public bool HasSupportedOs { get; }

    /// <summary>安装程序大小文本。</summary>
    public string SizeText { get; }

    /// <summary>安装前空间要求文本。</summary>
    public string MinFreeDiskText { get; }

    /// <summary>单项超时文本。</summary>
    public string TimeoutText { get; }

    /// <summary>静默参数文本。</summary>
    public string ArgumentsText { get; }

    /// <summary>完整性状态（用于徽标配色）。</summary>
    public PackageIntegrityStatus Integrity { get; }

    /// <summary>完整性状态中文文本。</summary>
    public string IntegrityText { get; }

    /// <summary>完整性不可用时的处置说明；已通过校验时为空。</summary>
    public string IntegrityRemedy { get; }

    /// <summary>是否需要展示处置说明。</summary>
    public bool HasRemedy { get; }

    /// <summary>该包当前是否允许执行（仅校验通过允许）。</summary>
    public bool CanExecute { get; }

    /// <summary>与目标机已装软件的匹配状态（用于徽标配色）。</summary>
    public PackageMatchStatus Match { get; }

    /// <summary>匹配状态中文文本。</summary>
    public string MatchText { get; }

    /// <summary>匹配详情（含目标机实际版本）。</summary>
    public string MatchDetailText { get; }

    /// <summary>备注；无为空。</summary>
    public string Notes { get; }

    /// <summary>是否有备注。</summary>
    public bool HasNotes { get; }
}

/// <summary>目标机某条已安装软件的界面视图。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Publisher">发布者文本。</param>
/// <param name="Version">版本文本。</param>
/// <param name="LocationText">安装位置文本。</param>
/// <param name="HasUninstaller">是否登记了卸载入口。</param>
internal sealed record InstalledSoftwareView(
    string Name,
    string Publisher,
    string Version,
    string LocationText,
    bool HasUninstaller)
{
    /// <summary>列表标题（名称 + 版本）。</summary>
    public string HeaderText => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name}　{Version}";
}

/// <summary>一次包操作结果的界面视图。</summary>
/// <param name="HeaderText">标题（操作类型 + 软件名 + 版本）。</param>
/// <param name="Outcome">安装器归因类别（用于徽标配色）。</param>
/// <param name="OutcomeText">归因类别中文文本。</param>
/// <param name="Description">面向 IT 人员的原因说明。</param>
/// <param name="DetailText">退出码 / 耗时 / 超时等细节。</param>
/// <param name="AfterText">操作后版本或残留提示。</param>
/// <param name="HasAfterText">是否有操作后信息。</param>
/// <param name="OutputText">标准输出摘要。</param>
/// <param name="ErrorText">标准错误摘要。</param>
/// <param name="HasOutput">是否有标准输出。</param>
/// <param name="HasError">是否有标准错误。</param>
internal sealed record PackageResultView(
    string HeaderText,
    InstallerOutcomeKind Outcome,
    string OutcomeText,
    string Description,
    string DetailText,
    string AfterText,
    bool HasAfterText,
    string OutputText,
    string ErrorText,
    bool HasOutput,
    bool HasError);

/// <summary>软件包与已装软件视图的构造辅助（把领域模型转换为界面视图）。</summary>
internal static class PackageViewFactory
{
    /// <summary>
    /// 把软件包与已装清单转换为软件包视图（含匹配状态与目标机实际版本）。
    /// </summary>
    /// <param name="catalog">软件包目录。</param>
    /// <param name="installed">目标机已装软件清单。</param>
    /// <returns>软件包视图清单（按名称排序）。</returns>
    public static IReadOnlyList<PackageItemView> BuildPackages(
        IReadOnlyList<SoftwarePackage> catalog,
        IReadOnlyList<InstalledSoftwareEntry> installed)
        => catalog
            .Select(package => new PackageItemView(
                package,
                PackageRules.MatchInstalled(package.Descriptor, installed),
                FindInstalledVersion(package.Descriptor, installed)))
            .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 把已装软件清单转换为界面视图（仅用于展示与人工核对，名称匹配不校验发布者）。
    /// </summary>
    /// <param name="installed">目标机已装软件清单。</param>
    /// <returns>已装软件视图清单（按名称排序）。</returns>
    public static IReadOnlyList<InstalledSoftwareView> BuildInstalled(IReadOnlyList<InstalledSoftwareEntry> installed)
        => installed
            .Select(entry => new InstalledSoftwareView(
                entry.Name,
                string.IsNullOrWhiteSpace(entry.Publisher) ? "发布者未知" : entry.Publisher!,
                string.IsNullOrWhiteSpace(entry.Version) ? "版本未知" : entry.Version!,
                string.IsNullOrWhiteSpace(entry.InstallLocation) ? "安装位置未登记" : entry.InstallLocation!,
                !string.IsNullOrWhiteSpace(entry.UninstallString)))
            .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>在已装清单中按显示名定位版本（仅用于展示）。</summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="installed">已装软件清单。</param>
    /// <returns>版本文本；未匹配到为空。</returns>
    private static string? FindInstalledVersion(
        SoftwarePackageDescriptor descriptor,
        IReadOnlyList<InstalledSoftwareEntry> installed)
    {
        var matchName = descriptor.MatchName.Trim();
        return installed
            .FirstOrDefault(entry => string.Equals(entry.Name.Trim(), matchName, StringComparison.OrdinalIgnoreCase))
            ?.Version;
    }

    /// <summary>
    /// 把包操作结果转换为界面视图。
    /// </summary>
    /// <param name="result">包操作结果。</param>
    /// <returns>供结果区展示的视图对象。</returns>
    public static PackageResultView BuildResult(PackageOperationResult result)
    {
        var execution = result.Execution;
        var detail = $"退出码 {execution.ExitCode.ToString(CultureInfo.InvariantCulture)} · "
                     + $"耗时 {execution.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} 秒";
        if (execution.TimedOut)
        {
            detail += " · 已超时终止";
        }

        var after = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.InstalledVersionAfter))
        {
            after.Add($"操作后版本：{result.InstalledVersionAfter}");
        }

        if (!string.IsNullOrWhiteSpace(result.ResidueNote))
        {
            after.Add(result.ResidueNote!);
        }

        var output = execution.StandardOutput.Trim();
        var error = execution.StandardError.Trim();
        if (!string.IsNullOrEmpty(execution.Reason))
        {
            error = error.Length == 0 ? execution.Reason! : execution.Reason + "\n" + error;
        }

        var afterText = string.Join("\n", after);
        return new PackageResultView(
            $"[{PackageRules.DescribeOperationKind(result.Kind)}] {result.PackageName} {result.DeclaredVersion}",
            result.Outcome.Kind,
            DescribeOutcome(result.Outcome),
            result.Outcome.Description,
            detail,
            afterText,
            afterText.Length > 0,
            output,
            error,
            output.Length > 0,
            error.Length > 0);
    }

    /// <summary>安装器归因类别 + 是否需重启的中文文本。</summary>
    /// <param name="outcome">归因结果。</param>
    /// <returns>如“成功（需重启生效）”“被安全软件拦截”。</returns>
    private static string DescribeOutcome(InstallerOutcome outcome)
    {
        var text = outcome.Kind switch
        {
            InstallerOutcomeKind.Success => "成功",
            InstallerOutcomeKind.SuccessPendingRestart => "成功但需重启",
            InstallerOutcomeKind.AlreadyInProgress => "已有安装进行中",
            InstallerOutcomeKind.BlockedByPolicy => "被策略阻止",
            InstallerOutcomeKind.BlockedBySecuritySoftware => "被安全软件拦截",
            InstallerOutcomeKind.FatalError => "致命错误",
            InstallerOutcomeKind.VersionConflict => "版本冲突",
            _ => "结果未知"
        };

        if (outcome.RequiresRestart && outcome.Kind == InstallerOutcomeKind.Success)
        {
            text += "（需重启生效）";
        }

        return text;
    }
}