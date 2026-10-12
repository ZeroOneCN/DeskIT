using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 软件安装与卸载编排（M5）：执行前检查（完整性、架构、可用空间、已装匹配、卸载入口），
/// 校验通过后把该操作转换为声明式动作并交由 M4 统一执行器执行，最后重新读取已装清单确认结果。
/// 只读诊断模式下全部禁止；来源不可验证（未声明哈希、哈希不匹配、文件缺失）的包一律禁止执行。
/// </summary>
public interface IPackageService
{
    /// <summary>
    /// 读取目标机已安装软件清单（只读）。
    /// </summary>
    /// <returns>已安装软件条目。</returns>
    IReadOnlyList<InstalledSoftwareEntry> ReadInstalledSoftware();

    /// <summary>
    /// 执行前的检查：不满足时给出明确原因，允许 IT 人员决定是否继续（存在阻断项时不得执行）。
    /// </summary>
    /// <param name="package">目标软件包。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="installed">目标机已装软件清单。</param>
    /// <param name="privilege">当前权限级别。</param>
    /// <returns>检查结果。</returns>
    PackagePreflight Preflight(
        SoftwarePackage package,
        PackageOperationKind kind,
        IReadOnlyList<InstalledSoftwareEntry> installed,
        PrivilegeLevel privilege);

    /// <summary>
    /// 执行一次包操作（安装 / 升级 / 卸载）。
    /// </summary>
    /// <param name="package">目标软件包。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="installed">操作前的已装软件清单（用于定位卸载入口与匹配）。</param>
    /// <param name="privilege">当前权限级别。</param>
    /// <param name="forceReinstall">已安装同版本时是否强制重装。</param>
    /// <param name="dataDirectory">U 盘数据目录（备份区来源）。</param>
    /// <param name="sessionId">所属会话标识；未关联会话时传空串。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>包操作结果。</returns>
    Task<PackageOperationResult> ExecuteAsync(
        SoftwarePackage package,
        PackageOperationKind kind,
        IReadOnlyList<InstalledSoftwareEntry> installed,
        PrivilegeLevel privilege,
        bool forceReinstall,
        string dataDirectory,
        string sessionId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPackageService" />
public sealed class PackageService : IPackageService
{
    private readonly IInstalledSoftwareReader _installedReader;
    private readonly IActionExecutor _actionExecutor;
    private readonly IDriveSpaceProvider _driveSpaceProvider;
    private readonly Func<bool> _is64BitOs;

    /// <summary>
    /// 初始化包服务。
    /// </summary>
    /// <param name="installedReader">已装软件读取器；为 null 时使用注册表实现。</param>
    /// <param name="actionExecutor">M4 统一动作执行器；为 null 时使用内置实现。</param>
    /// <param name="driveSpaceProvider">卷可用空间探测；为 null 时使用内置实现。</param>
    /// <param name="is64BitOs">目标机是否为 64 位系统；为 null 时读取运行环境。</param>
    public PackageService(
        IInstalledSoftwareReader? installedReader = null,
        IActionExecutor? actionExecutor = null,
        IDriveSpaceProvider? driveSpaceProvider = null,
        Func<bool>? is64BitOs = null)
    {
        _installedReader = installedReader ?? new InstalledSoftwareReader();
        _actionExecutor = actionExecutor ?? new ActionExecutor();
        _driveSpaceProvider = driveSpaceProvider ?? new DriveSpaceProvider();
        _is64BitOs = is64BitOs ?? (() => Environment.Is64BitOperatingSystem);
    }

    /// <inheritdoc />
    public IReadOnlyList<InstalledSoftwareEntry> ReadInstalledSoftware() => _installedReader.Read();

    /// <inheritdoc />
    public PackagePreflight Preflight(
        SoftwarePackage package,
        PackageOperationKind kind,
        IReadOnlyList<InstalledSoftwareEntry> installed,
        PrivilegeLevel privilege)
    {
        var descriptor = package.Descriptor;
        var blockers = new List<string>();
        var warnings = new List<string>();

        // 权限：软件安装 / 卸载仅管理员模式可用（PRD M5 权限）。
        if (privilege != PrivilegeLevel.Administrator)
        {
            blockers.Add("只读诊断模式：软件安装与卸载需要管理员权限，当前已禁用。");
        }

        // 完整性：来源不可验证一律禁止执行（PRD M5 处理规则与异常）。
        if (!package.CanExecute)
        {
            blockers.Add(PackageRules.DescribeIntegrityRemedy(package.Check.Status));
        }

        // 系统架构。
        if (!PackageRules.ArchitectureCompatible(descriptor.Architecture, _is64BitOs()))
        {
            blockers.Add($"该包仅适用于 {PackageRules.DescribeArchitecture(descriptor.Architecture)}系统，当前目标机不满足。");
        }

        // 目标机系统盘可用空间。
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        long freeBytes;
        try
        {
            freeBytes = _driveSpaceProvider.GetAvailableFreeBytes(systemRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            freeBytes = -1;
        }

        if (freeBytes >= 0 && !PackageRules.DiskSpaceSufficient(freeBytes, descriptor.MinFreeDiskMb))
        {
            blockers.Add(
                $"目标机系统盘可用空间不足：需要至少 {descriptor.MinFreeDiskMb} MB，当前可用 {PackageRules.FormatBytes(freeBytes)}。");
        }

        var match = PackageRules.MatchInstalled(descriptor, installed);
        var matchText = DescribeMatchDetail(descriptor, installed, match);

        switch (kind)
        {
            case PackageOperationKind.Install when match == PackageMatchStatus.InstalledSameVersion:
                warnings.Add($"{matchText}；如需覆盖安装请使用“强制重装”（需二次确认）。");
                break;

            case PackageOperationKind.Install when match != PackageMatchStatus.NotInstalled:
                warnings.Add($"{matchText}；将直接以新包覆盖安装，无需先卸载。");
                break;

            case PackageOperationKind.Upgrade when match == PackageMatchStatus.NotInstalled:
                blockers.Add("目标机上未检测到该软件，无法执行升级；请改用“安装”。");
                break;

            case PackageOperationKind.Upgrade when match == PackageMatchStatus.InstalledSameVersion:
                warnings.Add($"{matchText}；版本已一致，升级不会改变版本号。");
                break;

            case PackageOperationKind.Uninstall when match == PackageMatchStatus.NotInstalled:
                blockers.Add("目标机上未检测到该软件，无需卸载。");
                break;

            case PackageOperationKind.Uninstall:
            {
                var entry = FindInstalled(descriptor, installed);
                if (!string.IsNullOrWhiteSpace(descriptor.UninstallerFileName))
                {
                    var uninstallerPath = Path.Combine(
                        Path.GetDirectoryName(package.Check.InstallerPath) ?? string.Empty,
                        descriptor.UninstallerFileName!);
                    if (!File.Exists(uninstallerPath))
                    {
                        blockers.Add("该包声明的卸载程序不在 U 盘 packages 目录，无法执行卸载。");
                    }
                }
                else if (string.IsNullOrWhiteSpace(entry?.UninstallString))
                {
                    blockers.Add("该软件未提供卸载入口，需人工处理（程序不强制删除目录与注册表残留）。");
                }
                else
                {
                    warnings.Add("将使用目标机登记的自带卸载入口执行卸载。");
                }

                if (match == PackageMatchStatus.InstalledDifferentVersion)
                {
                    warnings.Add($"{matchText}；卸载的是目标机当前实际安装的版本。");
                }

                break;
            }
        }

        return new PackagePreflight(blockers.Count == 0, match, blockers, warnings);
    }

    /// <inheritdoc />
    public async Task<PackageOperationResult> ExecuteAsync(
        SoftwarePackage package,
        PackageOperationKind kind,
        IReadOnlyList<InstalledSoftwareEntry> installed,
        PrivilegeLevel privilege,
        bool forceReinstall,
        string dataDirectory,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var descriptor = package.Descriptor;
        var preflight = Preflight(package, kind, installed, privilege);

        if (!preflight.CanProceed)
        {
            var reason = string.Join("；", preflight.Blockers);
            return BuildResult(
                descriptor,
                kind,
                PackageRules.ClassifyInstallerExitCode(-1) with
                {
                    Kind = InstallerOutcomeKind.Unknown,
                    Description = $"未执行：{reason}"
                },
                BuildSkippedExecution(descriptor, kind, reason),
                null,
                null);
        }

        var entry = kind == PackageOperationKind.Uninstall ? FindInstalled(descriptor, installed) : null;
        var context = BuildContext(package, kind, entry, forceReinstall);
        var action = PackageRules.BuildOperationAction(descriptor, context);

        var execution = await _actionExecutor
            .ExecuteAsync(action, new Dictionary<string, string>(StringComparer.Ordinal), privilege, dataDirectory, sessionId, cancellationToken)
            .ConfigureAwait(false);

        var outcome = ClassifyOutcome(execution);
        var after = ReadVersionAfter(kind, descriptor, execution, outcome);
        var residue = kind == PackageOperationKind.Uninstall && outcome.Success ? DescribeResidue(entry) : null;

        return BuildResult(descriptor, kind, outcome, execution, after, residue);
    }

    /// <summary>
    /// 操作成功后重新读取已装清单，确认安装后版本或提示卸载后仍可检测到。
    /// </summary>
    /// <param name="kind">操作类型。</param>
    /// <param name="descriptor">包描述。</param>
    /// <param name="execution">底层执行结果。</param>
    /// <param name="outcome">安装器归因结果。</param>
    /// <returns>操作后读取到的版本；无有效信息时返回 null。</returns>
    private string? ReadVersionAfter(
        PackageOperationKind kind,
        SoftwarePackageDescriptor descriptor,
        ActionExecutionResult execution,
        InstallerOutcome outcome)
    {
        if (!outcome.Success || execution.Outcome is ActionOutcome.Cancelled or ActionOutcome.TimedOut)
        {
            return null;
        }

        if (kind == PackageOperationKind.Uninstall)
        {
            var stillInstalled = FindInstalled(descriptor, _installedReader.Read());
            return stillInstalled is null ? null : $"卸载后仍检测到：{stillInstalled.Name} {stillInstalled.Version}";
        }

        var entry = FindInstalled(descriptor, _installedReader.Read());
        return entry?.Version;
    }

    /// <summary>
    /// 卸载后检查登记项中的安装目录是否仍然存在；存在时仅提示残留位置，不自动删除（避免误删用户数据）。
    /// </summary>
    /// <param name="entry">卸载前的已装条目。</param>
    /// <returns>残留提示；无残留或无法判断时返回 null。</returns>
    private static string? DescribeResidue(InstalledSoftwareEntry? entry)
    {
        var location = entry?.InstallLocation;
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        try
        {
            return Directory.Exists(location) ? $"检测到残留目录：{location}（程序不会自动删除，请人工确认后处理）。" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 构建包操作上下文：安装与升级使用包内安装程序，卸载优先使用包内卸载程序，其次使用目标机登记项。
    /// </summary>
    /// <param name="package">目标软件包。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="entry">卸载时匹配到的已装条目。</param>
    /// <param name="forceReinstall">是否强制重装。</param>
    /// <returns>操作上下文。</returns>
    private static PackageActionContext BuildContext(
        SoftwarePackage package,
        PackageOperationKind kind,
        InstalledSoftwareEntry? entry,
        bool forceReinstall)
    {
        var descriptor = package.Descriptor;

        if (kind != PackageOperationKind.Uninstall)
        {
            return new PackageActionContext(kind, package.Check.InstallerPath, descriptor.InstallerArguments, null, forceReinstall);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.UninstallerFileName))
        {
            var uninstallerPath = Path.Combine(
                Path.GetDirectoryName(package.Check.InstallerPath) ?? string.Empty,
                descriptor.UninstallerFileName!);
            return new PackageActionContext(kind, uninstallerPath, descriptor.UninstallerArguments, null, false);
        }

        return new PackageActionContext(kind, null, null, entry?.UninstallString, false);
    }

    /// <summary>
    /// 把底层动作执行结果归因为安装器结果：取消 / 超时 / 跳过按原因如实说明，其余按退出码归因
    /// （因此“成功但需重启”的 3010 / 1641 不会被误判为失败）。
    /// </summary>
    /// <param name="execution">底层动作执行结果。</param>
    /// <returns>安装器归因结果。</returns>
    private static InstallerOutcome ClassifyOutcome(ActionExecutionResult execution)
    {
        if (execution.Outcome == ActionOutcome.Cancelled)
        {
            return new InstallerOutcome(InstallerOutcomeKind.Unknown, false, false, "已取消：安装器被中止，可能处于不确定状态，请人工核对。");
        }

        if (execution.Outcome == ActionOutcome.TimedOut)
        {
            return new InstallerOutcome(InstallerOutcomeKind.Unknown, false, false, "已超时：安装器超出单项超时被终止，请人工核对安装是否实际完成。");
        }

        if (execution.Outcome == ActionOutcome.Skipped)
        {
            return new InstallerOutcome(InstallerOutcomeKind.Unknown, false, false, execution.Reason ?? "已跳过。");
        }

        return PackageRules.ClassifyInstallerExitCode(execution.ExitCode);
    }

    /// <summary>为“未执行”构造一个跳过结果，供界面统一展示原因。</summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="reason">跳过原因。</param>
    /// <returns>动作执行结果。</returns>
    private static ActionExecutionResult BuildSkippedExecution(
        SoftwarePackageDescriptor descriptor,
        PackageOperationKind kind,
        string reason)
        => new(
            $"package.{kind.ToString().ToLowerInvariant()}.{descriptor.PackageId}",
            $"{PackageRules.DescribeOperationKind(kind)} {descriptor.Name} {descriptor.Version}",
            PackageRules.RiskFor(kind, false),
            ActionOutcome.Skipped,
            -1,
            string.Empty,
            string.Empty,
            TimeSpan.Zero,
            false,
            reason,
            Array.Empty<ActionParameterValue>());

    /// <summary>组装包操作结果。</summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="outcome">安装器归因结果。</param>
    /// <param name="execution">底层动作执行结果。</param>
    /// <param name="installedVersionAfter">操作后读取到的版本或提示。</param>
    /// <param name="residueNote">残留提示。</param>
    /// <returns>包操作结果。</returns>
    private static PackageOperationResult BuildResult(
        SoftwarePackageDescriptor descriptor,
        PackageOperationKind kind,
        InstallerOutcome outcome,
        ActionExecutionResult execution,
        string? installedVersionAfter,
        string? residueNote)
        => new(
            descriptor.PackageId,
            descriptor.Name,
            descriptor.Version,
            kind,
            outcome,
            execution,
            installedVersionAfter,
            residueNote);

    /// <summary>在已装清单中定位该软件对应的条目（名称匹配，双方声明发布者时要求一致）。</summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="installed">已装软件清单。</param>
    /// <returns>匹配到的条目；未匹配返回 null。</returns>
    private static InstalledSoftwareEntry? FindInstalled(
        SoftwarePackageDescriptor descriptor,
        IReadOnlyList<InstalledSoftwareEntry> installed)
    {
        var matchName = descriptor.MatchName.Trim();
        var candidates = installed
            .Where(entry => string.Equals(entry.Name.Trim(), matchName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Publisher))
        {
            var matched = candidates.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(entry.Publisher)
                && (entry.Publisher!.Contains(descriptor.Publisher, StringComparison.OrdinalIgnoreCase)
                    || descriptor.Publisher.Contains(entry.Publisher, StringComparison.OrdinalIgnoreCase)));
            if (matched is not null)
            {
                return matched;
            }
        }

        return candidates[0];
    }

    /// <summary>生成匹配详情文本（含目标机上的实际版本）。</summary>
    /// <param name="descriptor">包描述。</param>
    /// <param name="installed">已装软件清单。</param>
    /// <param name="match">匹配状态。</param>
    /// <returns>形如“已安装（版本不一致）：目标机 1.0.0”。</returns>
    private static string DescribeMatchDetail(
        SoftwarePackageDescriptor descriptor,
        IReadOnlyList<InstalledSoftwareEntry> installed,
        PackageMatchStatus match)
    {
        if (match == PackageMatchStatus.NotInstalled)
        {
            return "目标机未安装该软件";
        }

        var entry = FindInstalled(descriptor, installed);
        var version = entry?.Version;
        return string.IsNullOrWhiteSpace(version)
            ? $"目标机{PackageRules.DescribeMatch(match)}"
            : $"目标机{PackageRules.DescribeMatch(match)}（{version}）";
    }
}