using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeskIT.Core;
using DeskIT.Windows;

namespace DeskIT.App;

/// <summary>
/// 主窗口（M1 便携启动页）。三块内容：启动自检结果、当前权限与提权入口、本次能力摘要。
/// 所有判定逻辑委托给 DeskIT.Core 的 StartupRules 与 DeskIT.Windows 的自检服务，本类只负责展示与交互。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ISingleInstanceGuard _guard;
    private readonly IPrivilegeInspector _privilegeInspector;
    private readonly IElevationLauncher _elevationLauncher;
    private readonly IMediaStateStore _mediaStateStore;
    private readonly IStartupSelfCheckService _selfCheckService;

    private readonly string _programDirectory;
    private readonly string _dataDirectory;

    private readonly IDeviceCollectionService _collectionService = new DeviceCollectionService();
    private CancellationTokenSource? _collectionCancellation;
    private bool _collecting;

    private PrivilegeLevel _privilege;
    private bool _advancedModeEnabled;
    private StartupVerdict? _verdict;
    private bool _activationNoticeShown;

    /// <summary>
    /// 初始化主窗口。
    /// </summary>
    /// <param name="guard">单实例守卫，用于接收其他实例的“激活窗口”请求。</param>
    public MainWindow(ISingleInstanceGuard guard)
    {
        _guard = guard;
        _privilegeInspector = new WindowsPrivilegeInspector();
        _elevationLauncher = new WindowsElevationLauncher();
        _mediaStateStore = new MediaStateStore();
        _selfCheckService = new StartupSelfCheckService(
            new ProgramIntegrityVerifier(),
            new DataDirectoryProbe(),
            new DriveSpaceProvider(),
            new WindowsOsVersionInfoProvider());

        _programDirectory = AppContext.BaseDirectory;
        _dataDirectory = Path.Combine(_programDirectory, "data");

        InitializeComponent();

        _guard.ActivationRequested += OnActivationRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>窗口加载后执行启动自检并填充界面。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunSelfCheckAsync();
    }

    /// <summary>窗口关闭时解除单实例事件订阅。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _guard.ActivationRequested -= OnActivationRequested;
        _collectionCancellation?.Cancel();
    }

    /// <summary>收到其他实例的激活请求：恢复并前置窗口，并提示已存在的实例。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnActivationRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();

            if (!_activationNoticeShown)
            {
                _activationNoticeShown = true;
                MessageBox.Show(
                    this,
                    "DeskIT 已在运行，已为你切换到正在使用的窗口。",
                    "DeskIT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        });
    }

    /// <summary>
    /// 执行启动自检并刷新界面。
    /// </summary>
    /// <returns>无返回值的异步任务。</returns>
    private async Task RunSelfCheckAsync()
    {
        SetBusy(true);

        _privilege = _privilegeInspector.IsAdministrator() ? PrivilegeLevel.Administrator : PrivilegeLevel.Standard;
        _advancedModeEnabled = _mediaStateStore.GetAdvancedModeEnabled(_dataDirectory);

        var request = new StartupSelfCheckRequest(
            _programDirectory,
            _dataDirectory,
            _privilege,
            _guard.IsFirstInstance,
            _advancedModeEnabled);

        var result = await _selfCheckService.RunAsync(request);
        _verdict = result.Verdict;

        Render(result);
        SetBusy(false);
    }

    /// <summary>把自检结果渲染到界面。</summary>
    /// <param name="result">启动自检结果。</param>
    private void Render(StartupSelfCheckResult result)
    {
        var verdict = result.Verdict;

        SelfCheckList.ItemsSource = result.Items;

        PrivilegePill.Text = verdict.IsAdministrator ? "权限：管理员模式" : "权限：标准用户（只读）";
        AdvancedPill.Text = _advancedModeEnabled ? "高级模式：已开启" : "高级模式：关闭";

        // 顶部横幅：阻断启动（红）或受限启动（红）
        if (!verdict.CanStart)
        {
            ShowBanner(verdict.BlockReason ?? "启动被阻止。");
        }
        else if (verdict.OsSupport == OsSupport.Unsupported)
        {
            ShowBanner("目标系统不受支持：已进入受限启动，仅允许只读采集与导出，动作库禁用。");
        }
        else
        {
            Banner.Visibility = Visibility.Collapsed;
        }

        // 提权入口：仅标准用户可用
        var isStandard = !verdict.IsAdministrator;
        ElevateButton.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyButton.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.IsEnabled = isStandard && verdict.CanStart;
        ReadOnlyButton.IsEnabled = isStandard && verdict.CanStart;

        PrivilegeHint.Text = verdict.IsAdministrator
            ? "当前为管理员模式：可执行修复动作、软件安装/卸载；开启高级模式后可使用受控自定义命令。"
            : "当前为标准用户：可采集与诊断，修复动作库全部禁用。需要执行修复时，请选择“以管理员身份重新启动”。";

        // 能力摘要
        var capabilities = verdict.Capabilities;
        CapabilityList.ItemsSource = new List<string>
        {
            Mark(capabilities.CanCollect, "采集设备信息"),
            Mark(capabilities.CanDiagnose, "场景化问题诊断"),
            Mark(capabilities.CanFix, "执行修复动作"),
            Mark(capabilities.CanInstall, "安装 / 卸载软件包"),
            Mark(capabilities.CanCustomCommand, "受控自定义命令（需管理员 + 高级模式）"),
            Mark(capabilities.CanStartNewSession, "开始新的诊断会话")
        };

        RestrictionList.ItemsSource = capabilities.Restrictions;
        RestrictionTitle.Visibility = capabilities.Restrictions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 设备信息采集入口（M2）：任何模式均可采集；仅当“不再开始新会话”（空间 <200 MB）或启动被阻止时禁用。
        var canCollect = verdict.CanStart && capabilities.CanCollect && capabilities.CanStartNewSession;
        CollectBasicButton.IsEnabled = canCollect;
        CollectFullButton.IsEnabled = canCollect;
        if (!canCollect)
        {
            CollectionProgressText.Text = verdict.CanStart
                ? "介质可用空间低于 200 MB：已停止开始新的采集会话，请先归档或更换介质。"
                : "启动自检未通过，暂不能采集设备信息。";
        }

        // 高级模式开关
        AdvancedButton.IsEnabled = verdict.IsAdministrator && verdict.CanStart;
        AdvancedButton.Content = _advancedModeEnabled ? "关闭高级模式" : "开启高级模式";
        AdvancedHint.Text = _advancedModeEnabled
            ? "已开启：随介质保存，跨机、跨启动持续有效；可随时关闭（关闭不需密码）。"
            : "默认关闭：开启后可执行任意系统命令，风险自担；开关状态随介质保存。";

        // 页脚状态
        FooterStatus.Text = !verdict.CanStart
            ? "启动自检未通过，程序已被阻止启动。"
            : isStandard
                ? "启动自检完成。当前为只读诊断模式；如需执行修复，请先以管理员身份重新启动。"
                : "启动自检完成。当前为管理员模式，可执行采集、诊断与修复。";
    }

    /// <summary>生成带勾选标记的能力条目文本。</summary>
    /// <param name="enabled">能力是否可用。</param>
    /// <param name="label">能力名称。</param>
    /// <returns>如“✔ 执行修复动作”或“✘ 执行修复动作”。</returns>
    private static string Mark(bool enabled, string label) => enabled ? $"✔ {label}" : $"✘ {label}";

    /// <summary>显示顶部提示横幅。</summary>
    /// <param name="text">横幅文本。</param>
    private void ShowBanner(string text)
    {
        BannerText.Text = text;
        Banner.Visibility = Visibility.Visible;
    }

    /// <summary>切换忙碌态并更新页脚。</summary>
    /// <param name="busy">是否忙碌。</param>
    private void SetBusy(bool busy)
    {
        if (busy)
        {
            FooterStatus.Text = "正在执行启动自检…";
            ElevateButton.IsEnabled = false;
            ReadOnlyButton.IsEnabled = false;
            AdvancedButton.IsEnabled = false;
            CollectBasicButton.IsEnabled = false;
            CollectFullButton.IsEnabled = false;
        }
    }

    /// <summary>点击“以管理员身份重新启动”：发起提权；用户取消则如实回退只读模式。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnElevateClick(object sender, RoutedEventArgs e)
    {
        if (_elevationLauncher.TryRelaunchElevated())
        {
            // 已成功拉起管理员实例，当前实例退出，避免两个实例并存。
            Application.Current.Shutdown();
            return;
        }

        MessageBox.Show(
            this,
            "已取消提权（或系统策略不允许提权）。程序将保持只读诊断模式，可继续采集与诊断。",
            "DeskIT",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        await RunSelfCheckAsync();
    }

    /// <summary>点击“继续只读诊断模式”：确认当前为标准用户并保持只读能力。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnReadOnlyClick(object sender, RoutedEventArgs e)
    {
        FooterStatus.Text = "已选择只读诊断模式：可采集与诊断，修复动作库全部禁用。";
    }

    /// <summary>
    /// 切换高级模式：开启需风险明示 + 二次确认；关闭无需确认。
    /// 主密码验证将在介质加密（M8）落地后接入，本阶段先实现介质级开关与二次确认。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnToggleAdvancedClick(object sender, RoutedEventArgs e)
    {
        if (_verdict is null || !_verdict.IsAdministrator)
        {
            return;
        }

        if (_advancedModeEnabled)
        {
            _mediaStateStore.SetAdvancedModeEnabled(_dataDirectory, false);
            await RunSelfCheckAsync();
            return;
        }

        var risk = MessageBox.Show(
            this,
            "开启高级模式后可执行任意系统命令，可能对目标机造成不可逆影响，风险自担。是否继续？",
            "开启高级模式（第 1 步：风险明示）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (risk != MessageBoxResult.Yes)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "确认开启高级模式？该状态将随介质保存，跨机、跨启动持续有效。",
            "开启高级模式（第 2 步：二次确认）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _mediaStateStore.SetAdvancedModeEnabled(_dataDirectory, true);
        await RunSelfCheckAsync();
    }

    /// <summary>点击“开始基础采集”：以启动时自动触发的默认范围采集（≤60 秒，不含已装软件全量清单）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnCollectBasicClick(object sender, RoutedEventArgs e)
        => await RunCollectionAsync(CollectionMode.Basic);

    /// <summary>点击“开始完整采集”：在勾选范围内采集，含已装软件全量清单（耗时更长）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnCollectFullClick(object sender, RoutedEventArgs e)
        => await RunCollectionAsync(CollectionMode.Full);

    /// <summary>点击“中止采集”：终止本次采集；为保证快照完整，本次结果不写入 U 盘。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelCollectClick(object sender, RoutedEventArgs e)
    {
        _collectionCancellation?.Cancel();
        CollectionProgressText.Text = "正在中止采集…";
    }

    /// <summary>
    /// 执行一次设备信息采集并入库。
    /// </summary>
    /// <param name="mode">采集模式。</param>
    /// <returns>无返回值的异步任务。</returns>
    private async Task RunCollectionAsync(CollectionMode mode)
    {
        if (_collecting)
        {
            return;
        }

        if (_verdict is null || !_verdict.CanStart)
        {
            MessageBox.Show(this, "启动自检未通过，暂不能采集设备信息。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_verdict.Capabilities.CanStartNewSession)
        {
            MessageBox.Show(
                this,
                "介质可用空间低于 200 MB，已停止开始新的采集会话。请先归档报告与台账，或更换介质。",
                "DeskIT",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _collecting = true;
        SetCollectingBusy(true);
        CollectionGroupList.ItemsSource = null;
        CollectProgressBar.Value = 0;
        CollectionProgressText.Text = $"正在执行{CollectionRules.DescribeMode(mode)}…";
        MachineCard.Visibility = Visibility.Collapsed;

        var request = new CollectionRequest(
            mode,
            _privilege,
            BuildDisabledGroups(),
            CollectionRules.DefaultItemTimeout);
        var progress = new Progress<CollectionProgress>(OnCollectionProgress);

        _collectionCancellation = new CancellationTokenSource();

        try
        {
            var outcome = await _collectionService.CollectAndStoreAsync(
                _dataDirectory,
                request,
                progress,
                _collectionCancellation.Token);

            RenderCollection(outcome);
        }
        catch (OperationCanceledException)
        {
            CollectionProgressText.Text = "已中止采集，本次结果未写入快照。";
        }
        catch (Exception ex)
        {
            CollectionProgressText.Text = "采集失败，未能写入快照。";
            MessageBox.Show(this, $"设备信息采集失败：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _collecting = false;
            _collectionCancellation?.Dispose();
            _collectionCancellation = null;
            SetCollectingBusy(false);
        }
    }

    /// <summary>汇总界面上被取消勾选的可选分组（取消勾选即单项关闭）。</summary>
    /// <returns>被关闭的分组集合。</returns>
    private IReadOnlySet<CollectionGroup> BuildDisabledGroups()
    {
        var disabled = new HashSet<CollectionGroup>();
        if (IncludeAccountCheck.IsChecked != true)
        {
            disabled.Add(CollectionGroup.AccountEnvironment);
        }

        if (IncludeSoftwareCheck.IsChecked != true)
        {
            disabled.Add(CollectionGroup.InstalledSoftware);
        }

        if (IncludeSecurityCheck.IsChecked != true)
        {
            disabled.Add(CollectionGroup.SecurityUpdate);
        }

        return disabled;
    }

    /// <summary>上报采集进度（以采集项为单位）。</summary>
    /// <param name="progress">进度信息。</param>
    private void OnCollectionProgress(CollectionProgress progress)
    {
        CollectionProgressText.Text =
            $"采集中：{CollectionRules.DescribeGroup(progress.Group)} · {progress.ItemName}（{progress.CompletedItems}/{progress.TotalItems}）";
        CollectProgressBar.Value = progress.TotalItems == 0
            ? 0
            : progress.CompletedItems * 100.0 / progress.TotalItems;
    }

    /// <summary>
    /// 渲染采集结果：机器标识与档案提示，以及各分组摘要卡片（未采集的可选分组显示“已跳过”）。
    /// </summary>
    /// <param name="outcome">采集与归并结果。</param>
    private void RenderCollection(DeviceCollectionOutcome outcome)
    {
        var snapshot = outcome.Snapshot;

        MachineCard.Visibility = Visibility.Visible;
        MachineIdentityText.Text = $"机器标识：{snapshot.MachineId}（来源：{MachineIdSourceText.Describe(snapshot.MachineIdSource)}）";
        MachineProfileText.Text = outcome.Profile.Notice;
        MachineSnapshotText.Text =
            $"快照：{snapshot.SnapshotId} · {CollectionRules.DescribeMode(snapshot.Mode)} · " +
            $"{snapshot.CollectedAt:yyyy-MM-dd HH:mm:ss} · 计算机名 {snapshot.HostName} · 该档案累计 {outcome.Profile.Profile.ProcessingCount} 次采集";

        var views = new List<CollectionGroupView>();
        foreach (var group in CollectionRules.AllGroups)
        {
            var result = snapshot.Groups.FirstOrDefault(g => g.Group == group);
            if (result is null)
            {
                views.Add(new CollectionGroupView(
                    CollectionRules.DescribeGroup(group),
                    CollectionGroupStatus.Skipped,
                    CollectionRules.DescribeGroupStatus(CollectionGroupStatus.Skipped),
                    string.Empty,
                    Array.Empty<CollectionFieldView>(),
                    Array.Empty<string>()));
                continue;
            }

            views.Add(new CollectionGroupView(
                CollectionRules.DescribeGroup(group),
                result.Status,
                CollectionRules.DescribeGroupStatus(result.Status),
                $"耗时 {result.Elapsed.TotalSeconds:0.0} 秒",
                result.Fields
                    .Select(field => new CollectionFieldView(field.Name, CollectionRules.DescribeField(field)))
                    .ToList(),
                result.Details));
        }

        CollectionGroupList.ItemsSource = views;

        var incomplete = snapshot.Groups.Sum(group => group.Fields.Count(
            field => field.Status is FieldStatus.NoPermission or FieldStatus.Timeout or FieldStatus.Failed));
        CollectionProgressText.Text = incomplete == 0
            ? $"采集完成：{CollectionRules.DescribeMode(snapshot.Mode)}，{snapshot.Groups.Count} 个分组全部字段读取正常。"
            : $"采集完成：{CollectionRules.DescribeMode(snapshot.Mode)}，{snapshot.Groups.Count} 个分组，其中 {incomplete} 项未能完整读取（详见各分组状态）。";
        CollectProgressBar.Value = 100;
    }

    /// <summary>切换采集忙碌态（采集期间禁用触发与范围勾选，启用中止按钮）。</summary>
    /// <param name="busy">是否正在采集。</param>
    private void SetCollectingBusy(bool busy)
    {
        CollectBasicButton.IsEnabled = !busy;
        CollectFullButton.IsEnabled = !busy;
        CancelCollectButton.IsEnabled = busy;
        IncludeAccountCheck.IsEnabled = !busy;
        IncludeSoftwareCheck.IsEnabled = !busy;
        IncludeSecurityCheck.IsEnabled = !busy;
    }
}