using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskIT.Core;
using DeskIT.Windows;

// WPF 的 System.Windows.Controls 也定义了 MediaState（媒体播放状态），此处显式别名到本产品的介质状态。
using MediaState = DeskIT.Core.MediaState;

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
    private readonly IMediaVault _vault;
    private readonly IAuditStore _auditStore;
    private readonly IMediaMigration _mediaMigration;
    private readonly ITempWorkspace _tempWorkspace;
    private readonly MediaWatchdog _watchdog;
    private readonly System.Windows.Threading.DispatcherTimer _autoLockTimer;
    private readonly IStartupSelfCheckService _selfCheckService;

    private readonly string _programDirectory;
    private readonly string _dataDirectory;

    private readonly IDeviceCollectionService _collectionService;
    private CancellationTokenSource? _collectionCancellation;
    private bool _collecting;

    private readonly IProblemDiagnosisService _diagnosisService;
    private CancellationTokenSource? _diagnosisCancellation;
    private bool _diagnosing;

    private readonly IActionLibraryLoader _actionLibraryLoader = new ActionLibraryLoader();
    private readonly IActionExecutor _actionExecutor;
    private readonly ObservableCollection<ActionResultView> _actionResults = new();
    private ActionLibrary? _actionLibrary;
    private IReadOnlyList<ActionItemView> _actionViews = Array.Empty<ActionItemView>();
    private IReadOnlyDictionary<string, string> _actionNameById = new Dictionary<string, string>();
    private CancellationTokenSource? _actionCancellation;
    private bool _acting;
    private string _lastSessionId = string.Empty;

    private readonly IPackageLibraryLoader _packageLibraryLoader = new PackageLibraryLoader();
    private readonly IPackageService _packageService;
    private readonly ObservableCollection<PackageResultView> _packageResults = new();
    private PackageCatalog? _packageCatalog;
    private IReadOnlyList<InstalledSoftwareEntry> _installedSoftware = Array.Empty<InstalledSoftwareEntry>();
    private IReadOnlyList<PackageItemView> _packageViews = Array.Empty<PackageItemView>();
    private IReadOnlyList<InstalledSoftwareView> _installedViews = Array.Empty<InstalledSoftwareView>();
    private PackageItemView? _selectedPackage;
    private string _installedProblem = string.Empty;
    private CancellationTokenSource? _packageCancellation;
    private bool _packaging;

    private readonly IAccountDirectoryQuery _accountQuery = new AccountDirectoryQuery();
    private CancellationTokenSource? _accountCancellation;
    private bool _queryingAccounts;

    private PrivilegeLevel _privilege;
    private bool _advancedModeEnabled;
    private StartupVerdict? _verdict;
    private bool _activationNoticeShown;

    /// <summary>
    /// 界面是否已完成初始化（构造函数末尾置为 true）。
    /// XAML 解析期间（InitializeComponent）控件会把默认值变更事件一并触发，
    /// 此时后面的具名控件字段仍为 null；各事件处理函数据此提前返回，避免启动即崩溃。
    /// </summary>
    private bool _viewReady;

    // ═══════════════════ M8 介质安全与数据保护 ═══════════════════
    private MediaSettings _mediaSettings = new();
    private MediaState _mediaState = MediaState.NotInitialized;
    private bool _mediaUnlocked;
    private bool _mediaGateReady;
    private bool _sessionInterrupted;
    private DateTimeOffset? _lastActivityAt;
    private DateTimeOffset? _cooldownUntil;

    // ═══════════════════ M7 记录与多机台账 ═══════════════════
    private readonly ILedgerStore _ledgerStore;
    private readonly IReportWriter _reportWriter = new ReportWriter();
    private readonly ISnapshotStore _snapshotStore;

    private OperatorInfo? _currentOperator;
    private string _ledgerSessionId = string.Empty;
    private DateTimeOffset? _ledgerSessionStartedAt;
    private IReadOnlyList<DiagnosisSceneResult> _ledgerScenes = Array.Empty<DiagnosisSceneResult>();
    private readonly List<ActionExecutionResult> _rawActionResults = new();
    private bool _ledgerSessionActive;

    private IReadOnlyList<HandlingRecord> _ledgerRecords = Array.Empty<HandlingRecord>();
    private IReadOnlyList<HandlingRecord> _ledgerFilteredRecords = Array.Empty<HandlingRecord>();
    private IReadOnlySet<string> _ledgerConflictRecordIds = new HashSet<string>(StringComparer.Ordinal);
    private bool _ledgerLoaded;
    private bool _ledgerBusy;

    // ═══════════════════ M9 受控自定义命令与动作沉淀 ═══════════════════
    private readonly ICustomCommandStore _customCommandStore;
    private readonly ICustomCommandService _customCommandService;
    private readonly IActionSedimentStore _actionSedimentStore = new ActionSedimentStore();
    private readonly ObservableCollection<CustomCommandHistoryView> _customHistory = new();
    private readonly List<CustomCommandRecord> _rawCustomCommands = new();
    private readonly List<PersistenceChange> _rawPersistenceChanges = new();
    private CancellationTokenSource? _customCommandCancellation;
    private bool _customCommandRunning;
    private bool _customCommandRecordingDisabled;
    private bool _customHistoryLoaded;
    private string? _pendingCustomCommand;

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
        _vault = new MediaVault(_mediaStateStore);
        _auditStore = new AuditStore(_vault);
        _mediaMigration = new MediaMigration(_mediaStateStore);
        _tempWorkspace = new TempWorkspace();
        _watchdog = new MediaWatchdog(new MediaWritabilityProbe(), FlushSessionFiles);
        _watchdog.MediaUnavailable += OnMediaUnavailable;
        _autoLockTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _autoLockTimer.Tick += OnAutoLockTick;
        _selfCheckService = new StartupSelfCheckService(
            new ProgramIntegrityVerifier(),
            new DataDirectoryProbe(),
            new DriveSpaceProvider(),
            new WindowsOsVersionInfoProvider());

        _programDirectory = AppContext.BaseDirectory;
        _dataDirectory = Path.Combine(_programDirectory, "data");

        // M8：数据目录内的档案、快照、会话、动作备份、台账全部经介质金库加密落盘。
        _snapshotStore = new SnapshotStore(_vault);
        _ledgerStore = new LedgerStore(_vault);
        _actionExecutor = new ActionExecutor(
            backupStore: new ActionBackupStore(_vault),
            vault: _vault);
        _collectionService = new DeviceCollectionService(store: _snapshotStore);
        _diagnosisService = new ProblemDiagnosisService(
            new WindowsProblemDiagnoser(_snapshotStore),
            new DiagnosisStore(_vault),
            _snapshotStore);
        _packageService = new PackageService(actionExecutor: _actionExecutor);

        // M9：受控自定义命令执行与强制记录（命令历史加密追加 + 审计事件）；用于执行前后比对持久化项差异。
        _customCommandStore = new CustomCommandStore(_vault);
        _customCommandService = new CustomCommandService(
            store: _customCommandStore,
            auditStore: _auditStore,
            probe: new PersistenceProbe());

        InitializeComponent();

        ActionResultList.ItemsSource = _actionResults;
        PackageResultList.ItemsSource = _packageResults;
        CustomHistoryList.ItemsSource = _customHistory;

        // M7：结论 / 类别 / 未解决原因 / 导出选项为静态清单，构造时即填充，避免进入页签时闪空。
        LedgerCategoryCombo.ItemsSource = LedgerViewFactory.BuildCategoryOptions();
        LedgerCategoryCombo.SelectedIndex = 0;
        LedgerUnresolvedReasonCombo.ItemsSource = LedgerViewFactory.BuildUnresolvedReasonOptions();
        LedgerUnresolvedReasonCombo.SelectedIndex = 0;
        LedgerFilterCategoryCombo.ItemsSource = LedgerViewFactory.BuildCategoryFilterOptions();
        LedgerFilterCategoryCombo.SelectedIndex = 0;
        LedgerExportScopeCombo.ItemsSource = LedgerViewFactory.BuildExportScopeOptions();
        LedgerExportScopeCombo.SelectedIndex = 0;
        LedgerExportFormatCombo.ItemsSource = LedgerViewFactory.BuildExportFormatOptions();
        LedgerExportFormatCombo.SelectedIndex = 0;

        _guard.ActivationRequested += OnActivationRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;

        // 自动锁定：记录最后活动时间（鼠标与键盘事件）；计时器每秒刷新冷却倒计时并检查自动锁定。
        PreviewMouseMove += OnUserActivity;
        PreviewKeyDown += OnUserKeyActivity;
        InitForgetWarningText.Text = MediaRules.DescribeForgetWarning();
        _autoLockTimer.Start();

        // 界面元素已全部就绪：此后才允许事件处理函数操作具名控件。
        _viewReady = true;
    }

    /// <summary>窗口加载后执行启动自检并初始化介质门禁（未初始化 / 未解锁前覆盖全窗口）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunSelfCheckAsync();
        InitializeMediaGate();
    }

    /// <summary>窗口关闭时解除订阅、停止探测、清理临时工作目录并记录审计。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _guard.ActivationRequested -= OnActivationRequested;
        _watchdog.MediaUnavailable -= OnMediaUnavailable;
        _watchdog.Stop();
        _autoLockTimer.Stop();
        _collectionCancellation?.Cancel();
        _diagnosisCancellation?.Cancel();
        _actionCancellation?.Cancel();
        _packageCancellation?.Cancel();
        _accountCancellation?.Cancel();
        _customCommandCancellation?.Cancel();

        // 退出时清理目标机临时工作目录（PRD 7.4）；清理结果记入审计（介质已锁定时静默跳过）。
        try
        {
            var cleanup = _tempWorkspace.Cleanup();
            if (cleanup.Success)
            {
                AppendAudit(AuditEventKind.TempCleanup, "退出时已清理临时工作目录。");
            }
            else
            {
                AppendAudit(
                    AuditEventKind.TempCleanup,
                    $"临时工作目录未完全清理：{string.Join("；", cleanup.Reasons)}",
                    string.Join("；", cleanup.LeftoverPaths));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响退出。
        }
    }

    // ═══════════════════ M8 介质安全与数据保护（接线） ═══════════════════

    /// <summary>用户鼠标活动：刷新自动锁定计时起点（不打断输入）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">鼠标事件参数。</param>
    private void OnUserActivity(object sender, MouseEventArgs e) => MarkActivity();

    /// <summary>用户键盘活动：刷新自动锁定计时起点。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">键盘事件参数。</param>
    private void OnUserKeyActivity(object sender, KeyEventArgs e) => MarkActivity();

    /// <summary>记录最近一次活动时间（仅在已解锁时计时，锁定时不计）。</summary>
    private void MarkActivity()
    {
        if (_mediaUnlocked)
        {
            _lastActivityAt = DateTimeOffset.Now;
        }
    }

    /// <summary>自动锁定计时器回调：刷新解锁界面冷却倒计时；已解锁且无操作超时则自动锁定。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnAutoLockTick(object? sender, EventArgs e)
    {
        RefreshUnlockCooldownCountdown();

        if (!_mediaUnlocked)
        {
            return;
        }

        if (MediaRules.ShouldAutoLock(_lastActivityAt, DateTimeOffset.Now, _mediaSettings.AutoLockMinutes))
        {
            HandleMediaLocked("已因长时间无操作自动锁定介质：请重新输入主密码解锁。");
        }
    }

    /// <summary>初始化介质门禁：未初始化显示初始化向导；已初始化显示解锁面板。</summary>
    private void InitializeMediaGate()
    {
        _mediaSettings = _mediaStateStore.GetSettings(_dataDirectory);
        _mediaGateReady = false;

        // 补齐解锁面板的持久化失败计数与冷却（重启后仍生效）。
        _cooldownUntil = _mediaStateStore.GetCooldownUntil(_dataDirectory);

        var descriptor = _vault.ReadDescriptor(_dataDirectory);
        MediaGate.Visibility = Visibility.Visible;

        if (descriptor is null)
        {
            _mediaState = MediaState.NotInitialized;
            MediaInitPanel.Visibility = Visibility.Visible;
            MediaUnlockPanel.Visibility = Visibility.Collapsed;
            MediaGateSummaryText.Text = "当前介质尚未初始化：请设置主密码并登记处理人（一次性设置，随介质保存）。";
        }
        else
        {
            _mediaState = MediaState.Locked;
            MediaInitPanel.Visibility = Visibility.Collapsed;
            MediaUnlockPanel.Visibility = Visibility.Visible;
            UnlockMediaIdText.Text = string.IsNullOrWhiteSpace(descriptor.MediaId)
                ? "介质标识：无法读取（不影响解锁）"
                : $"介质标识：{descriptor.MediaId}";
            UnlockFailedText.Text = $"当前连续失败次数：{_mediaStateStore.GetUnlockFailures(_dataDirectory)}";
            MediaGateSummaryText.Text = "介质已初始化：请输入主密码解锁后使用。未解锁前无法查看台账、执行动作与导出，只读诊断/采集按只读语义允许。";
            RefreshMigrationNotice();
            RefreshUnlockCooldownCountdown();
        }

        RefreshMediaStatus();
        FooterStatus.Text = _mediaState == MediaState.NotInitialized
            ? "介质尚未初始化：请先完成初始化并设置主密码。"
            : "介质已锁定：请输入主密码解锁后再使用。";
    }

    /// <summary>刷新解锁界面的冷却倒计时；冷却结束即恢复“解锁”按钮可用。</summary>
    private void RefreshUnlockCooldownCountdown()
    {
        if (_mediaGateReady)
        {
            return;
        }

        if (_cooldownUntil is { } until && until > DateTimeOffset.Now)
        {
            var remaining = until - DateTimeOffset.Now;
            UnlockCooldownText.Text = $"连续失败次数过多：请等待 {remaining.TotalSeconds:0} 秒后再试（期间不提供绕过方式）。";
            UnlockButton.IsEnabled = false;
        }
        else
        {
            _cooldownUntil = null;
            UnlockCooldownText.Text = string.Empty;
            UnlockButton.IsEnabled = true;
        }
    }

    /// <summary>点击“初始化介质并解锁”：校验主密码与处理人后完成初始化。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnInitializeMediaClick(object sender, RoutedEventArgs e)
    {
        var password = InitPasswordBox.Password;
        var validation = MediaRules.ValidateMasterPassword(password);
        if (!validation.IsValid)
        {
            InitMessageText.Text = validation.Message;
            return;
        }

        if (!string.Equals(password, InitPasswordConfirmBox.Password, StringComparison.Ordinal))
        {
            InitMessageText.Text = "两次输入的主密码不一致，请重新输入。";
            return;
        }

        if (InitConfirmCheck.IsChecked != true)
        {
            InitMessageText.Text = "请先勾选“我已了解：忘记主密码将无法恢复数据”。";
            return;
        }

        var operatorInfo = new OperatorInfo(InitOperatorIdBox.Text.Trim(), InitOperatorNameBox.Text.Trim());
        if (!LedgerRules.IsCompleteOperator(operatorInfo))
        {
            InitMessageText.Text = "初始化必须登记处理人：工号与姓名两项均需填写。";
            return;
        }

        MediaInitializationResult result;
        try
        {
            result = _vault.Initialize(_dataDirectory, password, operatorInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InitMessageText.Text = $"初始化失败（介质不可写）：{ex.Message}";
            return;
        }

        if (!result.Success)
        {
            InitMessageText.Text = result.Message;
            return;
        }

        InitPasswordBox.Clear();
        InitPasswordConfirmBox.Clear();
        InitMessageText.Text = result.Message;
        ApplyUnlockSuccess(
            "介质初始化完成，已解锁并进入主界面。",
            AuditEventKind.MediaInitialized,
            "介质初始化：设置主密码并登记处理人。");
    }

    /// <summary>点击“解锁介质”：校验主密码并维护失败计数与冷却。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnUnlockMediaClick(object sender, RoutedEventArgs e)
    {
        if (_cooldownUntil is { } until && until > DateTimeOffset.Now)
        {
            RefreshUnlockCooldownCountdown();
            return;
        }

        MediaUnlockResult result;
        try
        {
            result = _vault.Unlock(_dataDirectory, UnlockPasswordBox.Password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UnlockFailedText.Text = $"解锁失败（介质不可读）：{ex.Message}";
            return;
        }

        if (!result.Success)
        {
            _cooldownUntil = result.Cooldown > TimeSpan.Zero ? DateTimeOffset.Now + result.Cooldown : _vault.CooldownUntil;
            UnlockPasswordBox.Clear();
            UnlockFailedText.Text = result.Message;
            RefreshUnlockCooldownCountdown();
            return;
        }

        UnlockPasswordBox.Clear();
        _cooldownUntil = null;
        ApplyUnlockSuccess("解锁成功，已进入主界面。", AuditEventKind.UnlockSucceeded, "主密码校验通过，介质已解锁。");
    }

    /// <summary>解锁面板密码框回车即触发解锁。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">键盘事件参数。</param>
    private void OnUnlockPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnUnlockMediaClick(sender, e);
        }
    }

    /// <summary>
    /// 解锁成功后的统一处理：进入主界面、启动拔出保护与自动锁定、刷新设置与状态，并记录审计。
    /// </summary>
    /// <param name="message">面向 IT 人员的中文提示。</param>
    /// <param name="kind">对应的审计事件类型。</param>
    /// <param name="summary">审计摘要。</param>
    private void ApplyUnlockSuccess(string message, AuditEventKind kind, string summary)
    {
        _mediaUnlocked = true;
        _mediaGateReady = true;
        _sessionInterrupted = false;
        _mediaState = MediaState.Unlocked;
        _lastActivityAt = DateTimeOffset.Now;

        MediaGate.Visibility = Visibility.Collapsed;
        MediaInitPanel.Visibility = Visibility.Collapsed;
        MediaUnlockPanel.Visibility = Visibility.Collapsed;

        _mediaSettings = _mediaStateStore.GetSettings(_dataDirectory);
        _currentOperator = _mediaStateStore.GetCurrentOperator(_dataDirectory);

        // 目标机临时工作目录（仅临时目录，退出清理）。
        try
        {
            _tempWorkspace.Create(string.IsNullOrWhiteSpace(_ledgerSessionId) ? "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") : _ledgerSessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无法创建临时目录不阻断使用。
        }

        _watchdog.Start(_dataDirectory);

        AppendAudit(kind, summary, message);
        RefreshMediaStatus();
        RefreshSettingsView();
        RefreshMigrationNotice();
        FooterStatus.Text = message;

        // 解锁后按需刷新当前页数据（避免启动时读取加密数据）。
        if (MainTabs.SelectedIndex == 4)
        {
            EnsurePackagesLoaded();
        }
        else if (MainTabs.SelectedIndex == 6)
        {
            EnsureLedgerLoaded();
        }
        else if (MainTabs.SelectedIndex == 8)
        {
            RefreshCustomCommandAvailability();
            EnsureCustomHistoryLoaded();
        }
    }

    /// <summary>锁定介质并把窗口切回解锁门禁（自动锁定、手动锁定、安全移除共用）。</summary>
    /// <param name="message">面向 IT 人员的中文提示。</param>
    private void HandleMediaLocked(string message)
    {
        _vault.Lock();
        _mediaUnlocked = false;
        _mediaGateReady = false;
        _mediaState = MediaState.Locked;

        _watchdog.Stop();
        _tempWorkspace.Cleanup();

        MediaInitPanel.Visibility = Visibility.Collapsed;
        MediaUnlockPanel.Visibility = Visibility.Visible;
        MediaGateSummaryText.Text = message;
        UnlockPasswordBox.Clear();
        UnlockFailedText.Text = string.Empty;
        _cooldownUntil = _mediaStateStore.GetCooldownUntil(_dataDirectory);
        RefreshUnlockCooldownCountdown();
        MediaGate.Visibility = Visibility.Visible;
        RefreshMediaStatus();
        FooterStatus.Text = message;
        RefreshCustomCommandAvailability();
    }

    /// <summary>点击“立即锁定介质”：主动锁定并回到解锁门禁。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnLockMediaClick(object sender, RoutedEventArgs e)
        => HandleMediaLocked("已手动锁定介质：请重新输入主密码解锁。");

    /// <summary>点击“安全移除介质”：存在未结束会话时阻止，否则锁定并提示可拔出。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSafeEjectClick(object sender, RoutedEventArgs e)
    {
        var state = CurrentSessionState();
        if (MediaRules.ShouldBlockSafeEject(state))
        {
            MessageBox.Show(
                this,
                $"当前会话尚未结束（{SessionStateRules.Describe(state)}）：请先在“记录与台账（M7）”完成或放弃会话，再安全移除介质。",
                "安全移除介质",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "确认安全移除介质？\n将立即锁定介质并停止写入，之后可直接拔出 U 盘。",
            "安全移除介质",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        AppendAudit(AuditEventKind.SessionInterruptedOrRemoved, "安全移除介质：已锁定并停止写入。");
        HandleMediaLocked("已完成安全移除：现在可以拔出 U 盘。");
        FooterStatus.Text = "已完成安全移除：现在可以拔出 U 盘。";
    }

    /// <summary>介质看门狗回调（拔出保护）：立即停止后续动作、保留已完成结果、标记会话“已中断”并提示。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">介质不可用事件参数。</param>
    private void OnMediaUnavailable(object? sender, MediaUnavailableEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_sessionInterrupted)
            {
                return;
            }

            _sessionInterrupted = true;

            // 立即停止后续动作：取消所有进行中的采集 / 诊断 / 动作 / 安装。
            _collectionCancellation?.Cancel();
            _diagnosisCancellation?.Cancel();
            _actionCancellation?.Cancel();
            _packageCancellation?.Cancel();

            _mediaState = MediaState.ReadOnlyOrUnwritable;
            RefreshMediaStatus();

            // 保留已完成结果：不清空已采集 / 已诊断 / 已执行结果；仅标记会话“已中断”。
            AppendAudit(
                AuditEventKind.SessionInterruptedOrRemoved,
                "介质不可写或已被移除：会话已标记为“已中断”。",
                e.Message);
            ShowBanner(e.Message);
            FooterStatus.Text = e.Message;

            MessageBox.Show(this, e.Message, "介质保护", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    /// <summary>会话文件 flush 回调：本产品的台账 / 快照 / 会话 / 审计均以同步方式写出，无文件流缓冲残留。</summary>
    private static void FlushSessionFiles()
    {
        // 所有加密数据文件均以同步方式写出（File.WriteAllText / File.AppendAllText），不保留缓冲流，无需额外 flush。
    }

    /// <summary>刷新右下角常驻介质状态指示。</summary>
    private void RefreshMediaStatus()
    {
        MediaStatusPill.Tag = _mediaState;
        MediaStatusText.Tag = _mediaState;
    }

    /// <summary>刷新“介质与设置（M8）”页：介质状态、介质级设置、高级模式与处理人。</summary>
    private void RefreshSettingsView()
    {
        _mediaSettings = _mediaStateStore.GetSettings(_dataDirectory);
        SettingsAutoLockBox.Text = _mediaSettings.AutoLockMinutes.ToString(CultureInfo.InvariantCulture);
        SettingsDesensitizeCheck.IsChecked = _mediaSettings.UiDesensitized;
        SettingsDataDirectoryText.Text = _dataDirectory;

        var descriptor = _vault.ReadDescriptor(_dataDirectory);
        SettingsMediaIdText.Text = descriptor is null
            ? "（尚未初始化）"
            : string.IsNullOrWhiteSpace(descriptor.MediaId) ? "（无法读取）" : descriptor.MediaId;

        SettingsAdvancedStateText.Text = _advancedModeEnabled
            ? "当前：已开启（随介质保存，跨机、跨启动持续有效）。"
            : "当前：关闭（开启需主密码验证并二次确认）。";
        SettingsAdvancedButton.Content = _advancedModeEnabled ? "关闭高级模式" : "开启高级模式";

        _currentOperator = _mediaStateStore.GetCurrentOperator(_dataDirectory);
        SettingsOperatorText.Text = _currentOperator is null
            ? "介质尚未登记处理人（可在“记录与台账（M7）”登记）。"
            : $"当前登记处理人：{_currentOperator.DisplayText}";

        var operators = _mediaStateStore.ListOperators(_dataDirectory);
        SettingsOperatorCombo.ItemsSource = operators;
        SettingsOperatorCombo.SelectedIndex = operators.Count > 0 ? 0 : -1;

        RefreshMigrationNotice();
    }

    /// <summary>刷新介质迁移提示（数据目录被复制到新介质时明示“已更换介质”）。</summary>
    private void RefreshMigrationNotice()
    {
        var currentId = _mediaMigration.ResolveCurrentMediaId(_dataDirectory);
        var result = _mediaMigration.DetectMediaChange(_dataDirectory, currentId);

        SettingsMigrationText.Text = result.Message;
        MigrationNoticeText.Text = result.MediaChanged ? result.Message : string.Empty;
        MigrationNoticeText.Visibility = result.MediaChanged ? Visibility.Visible : Visibility.Collapsed;

        if (result.MediaChanged)
        {
            AppendAudit(AuditEventKind.MediaChanged, "已更换介质：数据目录被复制到新介质。", result.Message);
        }
    }

    /// <summary>写入审计事件（介质未解锁或写入失败时静默跳过，不阻断主流程）。</summary>
    /// <param name="kind">事件类型。</param>
    /// <param name="summary">事件摘要。</param>
    /// <param name="detail">事件明细（不得包含凭证明文）。</param>
    /// <param name="sessionId">关联会话标识；未关联时为空串。</param>
    private void AppendAudit(AuditEventKind kind, string summary, string detail = "", string sessionId = "")
    {
        if (!_mediaUnlocked)
        {
            return;
        }

        try
        {
            var operatorInfo = _mediaStateStore.GetCurrentOperator(_dataDirectory);
            var now = DateTimeOffset.Now;
            var auditEvent = new AuditEvent
            {
                EventId = MediaRules.NewAuditEventId(now),
                Kind = kind,
                At = now,
                ComputerName = Environment.MachineName,
                OperatorEmployeeId = operatorInfo?.EmployeeId ?? string.Empty,
                OperatorName = operatorInfo?.Name ?? string.Empty,
                SessionId = sessionId,
                Summary = summary,
                Detail = detail
            };
            _auditStore.Append(_dataDirectory, auditEvent);
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            // 审计写入失败不阻断主流程。
        }
    }

    /// <summary>按当前运行态推断会话状态（用于安全移除门禁：会话未结束则阻止移除）。</summary>
    /// <returns>当前会话状态。</returns>
    private SessionState CurrentSessionState()
    {
        if (_sessionInterrupted)
        {
            return SessionState.Interrupted;
        }

        if (_collecting)
        {
            return SessionState.Collecting;
        }

        if (_diagnosing)
        {
            return SessionState.Diagnosing;
        }

        if (_acting || _packaging || _customCommandRunning)
        {
            return SessionState.Acting;
        }

        // 已建立但未结束的会话属于非终态，阻止安全移除。
        return _ledgerSessionActive ? SessionState.New : SessionState.Closed;
    }

    /// <summary>写入门禁：未解锁时提示并阻止（查看台账、执行动作、安装卸载、导出均需解锁）。</summary>
    /// <returns>已解锁返回 true；否则提示并返回 false。</returns>
    private bool EnsureUnlocked()
    {
        if (_mediaUnlocked)
        {
            return true;
        }

        MessageBox.Show(this, "介质尚未解锁：请先输入主密码解锁后再操作。", "介质安全", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>点击“保存设置”：把自动锁定与界面脱敏写入介质级设置。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        if (!int.TryParse(SettingsAutoLockBox.Text.Trim(), out var minutes) || minutes < 0 || minutes > 240)
        {
            SettingsMessageText.Text = "自动锁定分钟数需为 0~240 的整数（0 表示不自动锁定）。";
            return;
        }

        var settings = new MediaSettings
        {
            AutoLockMinutes = minutes,
            UiDesensitized = SettingsDesensitizeCheck.IsChecked == true
        };

        try
        {
            _mediaStateStore.SetSettings(_dataDirectory, settings);
            _mediaSettings = settings;
            SettingsMessageText.Text = "设置已保存到介质（data/operators.json），跨机、跨启动有效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsMessageText.Text = $"保存失败（介质可能不可写）：{ex.Message}";
        }
    }

    /// <summary>点击“开启 / 关闭高级模式”：开启需主密码验证 + 风险明示 + 二次确认，变化记入审计。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnToggleAdvancedFromSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        if (_verdict is null || !_verdict.IsAdministrator)
        {
            SettingsAdvancedMessageText.Text = "仅管理员模式可开启高级模式。";
            return;
        }

        if (_advancedModeEnabled)
        {
            var confirmClose = MessageBox.Show(
                this,
                "确认关闭高级模式？关闭后无法执行任意系统命令。",
                "关闭高级模式",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmClose != MessageBoxResult.Yes)
            {
                return;
            }

            _mediaStateStore.SetAdvancedModeEnabled(_dataDirectory, false);
            _advancedModeEnabled = false;
            AppendAudit(AuditEventKind.AdvancedModeChanged, "高级模式已关闭。");
            SettingsAdvancedMessageText.Text = "高级模式已关闭。";
            await RunSelfCheckAsync();
            RefreshSettingsView();
            return;
        }

        var password = SettingsPasswordBox.Password;
        var unlock = _vault.Unlock(_dataDirectory, password);
        if (!unlock.Success)
        {
            SettingsAdvancedMessageText.Text = $"主密码验证失败：{unlock.Message}";
            AppendAudit(AuditEventKind.UnlockFailed, "高级模式开启：主密码验证失败。");
            return;
        }

        SettingsPasswordBox.Clear();

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
        _advancedModeEnabled = true;
        AppendAudit(AuditEventKind.AdvancedModeChanged, "高级模式已开启。");
        SettingsAdvancedMessageText.Text = "高级模式已开启。";
        await RunSelfCheckAsync();
        RefreshSettingsView();
    }

    /// <summary>点击“切换为所选处理人”：二次确认后切换并记入审计。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSwitchOperatorClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        if (SettingsOperatorCombo.SelectedItem is not OperatorInfo target)
        {
            SettingsOperatorMessageText.Text = "请先选择一个已登记的处理人（可在“记录与台账（M7）”登记新处理人）。";
            return;
        }

        if (_currentOperator is not null && SameOperator(_currentOperator, target))
        {
            SettingsOperatorMessageText.Text = $"当前处理人已是 {target.DisplayText}，无需切换。";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"确认将介质当前处理人切换为 {target.DisplayText}？\n切换将记入审计。",
            "切换处理人",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _mediaStateStore.SetCurrentOperator(_dataDirectory, target);
            _currentOperator = target;
            AppendAudit(AuditEventKind.OperatorSwitched, $"处理人已切换为 {target.DisplayText}。");
            SettingsOperatorMessageText.Text = $"已切换处理人为 {target.DisplayText}。";
            RefreshOperatorFields();
            RefreshSettingsView();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsOperatorMessageText.Text = $"切换失败（介质可能不可写）：{ex.Message}";
        }
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

        // 问题诊断入口（M3）：任何模式均可诊断；仅当“不再开始新会话”（空间 <200 MB）或启动被阻止时禁用。
        var canDiagnose = verdict.CanStart && capabilities.CanDiagnose && capabilities.CanStartNewSession;
        DiagnoseSceneButton.IsEnabled = canDiagnose;
        DiagnoseAllButton.IsEnabled = canDiagnose;
        if (!canDiagnose)
        {
            DiagnosisProgressText.Text = verdict.CanStart
                ? "介质可用空间低于 200 MB：已停止开始新的诊断会话，请先归档或更换介质。"
                : "启动自检未通过，暂不能进行问题诊断。";
        }
        else if (!_diagnosing && DiagnosisSessionText.Text == "尚未诊断。")
        {
            DiagnosisProgressText.Text = "诊断项可在列表中出现异常时持续刷新；异常与提示项始终置顶显示。";
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

        // 修复动作库（M4）：加载动作定义并按当前运行条件计算可用性。
        EnsureActionLibraryLoaded();

        // 软件安装卸载（M5）与账号与权限（M6）：刷新可用性；软件包在首次进入页签时按需加载。
        if (_packageCatalog is not null)
        {
            RenderPackageStatus();
        }

        UpdatePackageAvailability();
        UpdateAccountAvailability();
        RefreshCustomCommandAvailability();
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
            DiagnoseSceneButton.IsEnabled = false;
            DiagnoseAllButton.IsEnabled = false;
            ReloadPackagesButton.IsEnabled = false;
            RefreshInstalledButton.IsEnabled = false;
            QueryAccountsButton.IsEnabled = false;
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
    /// 切换高级模式：开启需主密码验证 + 风险明示 + 二次确认；关闭无需确认。变化记入审计。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnToggleAdvancedClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        if (_verdict is null || !_verdict.IsAdministrator)
        {
            return;
        }

        if (_advancedModeEnabled)
        {
            _mediaStateStore.SetAdvancedModeEnabled(_dataDirectory, false);
            _advancedModeEnabled = false;
            AppendAudit(AuditEventKind.AdvancedModeChanged, "高级模式已关闭。");
            await RunSelfCheckAsync();
            return;
        }

        // M8：开启高级模式需先通过主密码验证（仅内存使用，不落盘）。
        var password = PromptMasterPassword();
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        var verify = _vault.Unlock(_dataDirectory, password);
        if (!verify.Success)
        {
            AppendAudit(AuditEventKind.UnlockFailed, "高级模式开启：主密码验证失败。");
            MessageBox.Show(this, $"主密码验证失败：{verify.Message}", "主密码验证", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _advancedModeEnabled = true;
        AppendAudit(AuditEventKind.AdvancedModeChanged, "高级模式已开启。");
        await RunSelfCheckAsync();
    }

    /// <summary>弹出主密码验证窗口，返回用户输入的主密码；取消或留空时返回 null。</summary>
    /// <returns>主密码或 null。</returns>
    private string? PromptMasterPassword()
    {
        var window = new Window
        {
            Title = "主密码验证",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "开启高级模式需要验证主密码（主密码仅内存使用，不写入介质）。",
            TextWrapping = TextWrapping.Wrap
        });

        var box = new PasswordBox { Height = 28, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(box);

        var confirmButton = new Button
        {
            Content = "验证并继续",
            Height = 30,
            MinWidth = 120,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        panel.Children.Add(confirmButton);

        var password = string.Empty;
        confirmButton.Click += (_, _) =>
        {
            password = box.Password;
            window.DialogResult = true;
        };

        window.Content = panel;
        return window.ShowDialog() == true && !string.IsNullOrEmpty(password) ? password : null;
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

        // M7：采集即建立/更新本次处理会话上下文（机器标识用于台账归并与档案匹配）。
        EstablishSessionFromCollection(snapshot);
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

    /// <summary>点击“诊断勾选场景”：仅诊断界面勾选的场景。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnDiagnoseSceneClick(object sender, RoutedEventArgs e)
        => await RunDiagnosisAsync(allScenes: false);

    /// <summary>点击“全场景诊断”：按展示顺序诊断全部 7 个场景。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnDiagnoseAllClick(object sender, RoutedEventArgs e)
        => await RunDiagnosisAsync(allScenes: true);

    /// <summary>点击“中止诊断”：终止本次诊断；为保证会话完整，本次结果不写入 U 盘。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelDiagnoseClick(object sender, RoutedEventArgs e)
    {
        _diagnosisCancellation?.Cancel();
        DiagnosisProgressText.Text = "正在中止诊断…";
    }

    /// <summary>
    /// 执行一次问题诊断并写入会话记录。
    /// </summary>
    /// <param name="allScenes">是否执行全场景诊断。</param>
    /// <returns>无返回值的异步任务。</returns>
    private async Task RunDiagnosisAsync(bool allScenes)
    {
        if (_diagnosing)
        {
            return;
        }

        if (_verdict is null || !_verdict.CanStart)
        {
            MessageBox.Show(this, "启动自检未通过，暂不能进行问题诊断。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_verdict.Capabilities.CanStartNewSession)
        {
            MessageBox.Show(
                this,
                "介质可用空间低于 200 MB，已停止开始新的诊断会话。请先归档报告与台账，或更换介质。",
                "DeskIT",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var scenes = DiagnosisRules.SelectScenes(allScenes, BuildSelectedScenes());
        if (scenes.Count == 0)
        {
            MessageBox.Show(
                this,
                "请至少勾选一个诊断场景，或直接使用“全场景诊断”。",
                "DeskIT",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _diagnosing = true;
        SetDiagnosingBusy(true);
        DiagnosisFindingList.ItemsSource = null;
        DiagnoseProgressBar.Value = 0;
        DiagnosisSessionText.Text = "正在诊断…";
        DiagnosisProgressText.Text =
            $"即将诊断 {scenes.Count} 个场景（全部为只读检查，需管理员的项将标记为未执行）。";

        var request = new DiagnosisRequest(scenes, _privilege, DiagnosisRules.DefaultItemTimeout, _dataDirectory);
        var progress = new Progress<DiagnosisProgress>(OnDiagnosisProgress);

        _diagnosisCancellation = new CancellationTokenSource();

        try
        {
            var outcome = await _diagnosisService.DiagnoseAndStoreAsync(
                _dataDirectory,
                request,
                progress,
                _diagnosisCancellation.Token);

            RenderDiagnosis(outcome);
        }
        catch (OperationCanceledException)
        {
            DiagnosisSessionText.Text = "已中止诊断。";
            DiagnosisProgressText.Text = "已中止诊断，本次结果未写入会话记录。";
        }
        catch (Exception ex)
        {
            DiagnosisSessionText.Text = "诊断失败。";
            DiagnosisProgressText.Text = "诊断失败，未能写入会话记录。";
            MessageBox.Show(this, $"问题诊断失败：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _diagnosing = false;
            _diagnosisCancellation?.Dispose();
            _diagnosisCancellation = null;
            SetDiagnosingBusy(false);
        }
    }

    /// <summary>汇总界面上被勾选的诊断场景（取消勾选即不诊断该场景）。</summary>
    /// <returns>被勾选的场景集合。</returns>
    private IReadOnlySet<DiagnosisScene> BuildSelectedScenes()
    {
        var selected = new HashSet<DiagnosisScene>();
        AddIfChecked(DiagSceneSlownessCheck, DiagnosisScene.Slowness, selected);
        AddIfChecked(DiagSceneNetworkCheck, DiagnosisScene.Network, selected);
        AddIfChecked(DiagScenePrintingCheck, DiagnosisScene.PrintingSharing, selected);
        AddIfChecked(DiagSceneDiskCheck, DiagnosisScene.DiskStorage, selected);
        AddIfChecked(DiagSceneStartupCheck, DiagnosisScene.StartupSystem, selected);
        AddIfChecked(DiagSceneSoftwareCheck, DiagnosisScene.Software, selected);
        AddIfChecked(DiagSceneAccountCheck, DiagnosisScene.AccountPrivilege, selected);
        return selected;
    }

    /// <summary>勾选框为选中时把对应场景加入集合。</summary>
    /// <param name="box">场景勾选框。</param>
    /// <param name="scene">对应场景。</param>
    /// <param name="target">目标集合。</param>
    private static void AddIfChecked(System.Windows.Controls.CheckBox box, DiagnosisScene scene, ISet<DiagnosisScene> target)
    {
        if (box.IsChecked == true)
        {
            target.Add(scene);
        }
    }

    /// <summary>上报诊断进度（以诊断项为单位）。</summary>
    /// <param name="progress">进度信息。</param>
    private void OnDiagnosisProgress(DiagnosisProgress progress)
    {
        DiagnosisProgressText.Text =
            $"诊断中：{DiagnosisRules.DescribeScene(progress.Scene)} · {progress.ItemName}（{progress.CompletedItems}/{progress.TotalItems}）";
        DiagnoseProgressBar.Value = progress.TotalItems == 0
            ? 0
            : progress.CompletedItems * 100.0 / progress.TotalItems;
    }

    /// <summary>
    /// 渲染诊断结果：结论按“异常置顶”排序展示，并给出会话标识、状态与统计摘要。
    /// </summary>
    /// <param name="outcome">诊断与入库结果。</param>
    private void RenderDiagnosis(ProblemDiagnosisOutcome outcome)
    {
        var report = outcome.Report;
        var summary = report.Summary;

        // 记录本次诊断会话标识，供后续修复动作执行记录关联。
        _lastSessionId = outcome.Session.SessionId;

        DiagnosisFindingList.ItemsSource = DiagnosisRules.SortFindings(report.AllFindings)
            .Select(BuildFindingView)
            .ToList();

        DiagnosisSessionText.Text =
            $"会话 {outcome.Session.SessionId} · 状态：{SessionStateRules.Describe(outcome.Session.State)} · " +
            DiagnosisRules.DescribeSummary(summary);
        DiagnosisProgressText.Text =
            $"诊断完成：{report.Scenes.Count} 个场景、{summary.TotalCount} 个诊断项，耗时 {report.FinishedAt - report.StartedAt:hh\\:mm\\:ss}。";
        DiagnoseProgressBar.Value = 100;

        // M7：把诊断会话标识与结论写入本次处理会话上下文，作为台账记录来源。
        EstablishSessionFromDiagnosis(outcome);
    }

    /// <summary>把诊断结论转换为界面视图。</summary>
    /// <param name="finding">诊断结论。</param>
    /// <returns>供列表与详情面板绑定的视图对象。</returns>
    private DiagnosisFindingView BuildFindingView(DiagnosisFinding finding)
    {
        var sceneName = DiagnosisRules.DescribeScene(finding.Scene);
        var evidence = finding.Evidence
            .Select(item => new DiagnosisEvidenceView(item.Label, string.IsNullOrWhiteSpace(item.Value) ? "（无）" : item.Value!))
            .ToList();

        return new DiagnosisFindingView(
            finding.FindingId,
            sceneName,
            finding.Name,
            finding.Conclusion,
            DiagnosisRules.DescribeConclusion(finding.Conclusion),
            DiagnosisRules.DescribeStatus(finding.Status),
            finding.Reason,
            $"耗时 {finding.Elapsed.TotalSeconds:0.0} 秒",
            evidence,
            DescribeSuggestedActions(finding),
            $"{sceneName} · {finding.Name}",
            finding.Status == DiagnosisExecutionStatus.SkippedNeedsAdministrator);
    }

    /// <summary>
    /// 把诊断项建议的动作标识映射为“动作名称（标识）”；动作库未收录的标识原样展示。
    /// </summary>
    /// <param name="finding">诊断结论。</param>
    /// <returns>建议动作文本。</returns>
    private string DescribeSuggestedActions(DiagnosisFinding finding)
    {
        if (finding.SuggestedActionIds.Count == 0)
        {
            return "暂无建议动作。";
        }

        return string.Join("、", finding.SuggestedActionIds.Select(actionId =>
            _actionNameById.TryGetValue(actionId, out var name) ? $"{name}（{actionId}）" : actionId));
    }

    /// <summary>切换诊断忙碌态（诊断期间禁用触发与场景勾选，启用中止按钮）。</summary>
    /// <param name="busy">是否正在诊断。</param>
    private void SetDiagnosingBusy(bool busy)
    {
        DiagnoseSceneButton.IsEnabled = !busy;
        DiagnoseAllButton.IsEnabled = !busy;
        CancelDiagnoseButton.IsEnabled = busy;
        DiagSceneSlownessCheck.IsEnabled = !busy;
        DiagSceneNetworkCheck.IsEnabled = !busy;
        DiagScenePrintingCheck.IsEnabled = !busy;
        DiagSceneDiskCheck.IsEnabled = !busy;
        DiagSceneStartupCheck.IsEnabled = !busy;
        DiagSceneSoftwareCheck.IsEnabled = !busy;
        DiagSceneAccountCheck.IsEnabled = !busy;
    }

    /// <summary>
    /// 确保动作库已加载：首次调用读取 U 盘 <c>actions/</c> 定义、按规则排序并构建视图；
    /// 随后刷新定义问题提示与列表。
    /// </summary>
    private void EnsureActionLibraryLoaded()
    {
        if (_actionLibrary is null)
        {
            _actionLibrary = _actionLibraryLoader.Load(_programDirectory);
            _actionNameById = _actionLibrary.Actions
                .ToDictionary(action => action.ActionId, action => action.Name, StringComparer.OrdinalIgnoreCase);
            _actionViews = ActionOrdering.OrderActions(_actionLibrary.Actions)
                .Select(action => new ActionItemView(action, CanRunAction(action, out var reason), reason))
                .ToList();
        }

        var problems = _actionLibrary.Problems;
        ActionProblemText.Text = problems.Count == 0
            ? string.Empty
            : "动作定义问题：" + string.Join(" ", problems);
        ActionProblemText.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ApplyActionFilter();
        UpdateActionLibraryText();
    }

    /// <summary>
    /// 判断动作在当前运行条件下是否可执行，并给出不可执行原因。
    /// 只读诊断模式、启动自检未通过或介质可用空间不足时，动作库全部或部分禁用。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <param name="reason">不可执行原因；可执行时为 null。</param>
    /// <returns>可执行返回 true。</returns>
    private bool CanRunAction(ActionDescriptor action, out string? reason)
    {
        if (_verdict is null || !_verdict.CanStart)
        {
            reason = "启动自检未通过：修复动作库已禁用。";
            return false;
        }

        if (!ActionRules.CanRunByPrivilege(action, _privilege))
        {
            reason = "只读诊断模式：该项需要管理员权限，当前已禁用。";
            return false;
        }

        if (!_verdict.Capabilities.CanFix)
        {
            reason = "当前运行条件下不允许执行修复动作。";
            return false;
        }

        if (!_verdict.Capabilities.CanStartNewSession)
        {
            reason = "介质可用空间低于 200 MB：已停止开始新的执行会话，请先归档或更换介质。";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>按搜索关键词过滤动作清单（名称 / 影响 / 标识 / 场景，忽略大小写）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnActionSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_actionLibrary is null)
        {
            return;
        }

        ApplyActionFilter();
        UpdateActionLibraryText();
    }

    /// <summary>把当前搜索关键词应用到动作清单。</summary>
    private void ApplyActionFilter()
    {
        var keyword = ActionSearchBox?.Text?.Trim() ?? string.Empty;
        ActionList.ItemsSource = keyword.Length == 0
            ? _actionViews
            : _actionViews.Where(view => Matches(view, keyword)).ToList();
    }

    /// <summary>判断动作视图是否命中搜索关键词。</summary>
    /// <param name="view">动作视图。</param>
    /// <param name="keyword">关键词。</param>
    /// <returns>命中返回 true。</returns>
    private static bool Matches(ActionItemView view, string keyword)
        => view.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || view.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || view.ActionId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || view.SceneName.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>刷新动作库状态文案与“执行勾选动作”按钮可用性。</summary>
    private void UpdateActionLibraryText()
    {
        if (_actionLibrary is null)
        {
            return;
        }

        var total = _actionViews.Count;
        var shown = (ActionList.ItemsSource as IEnumerable<ActionItemView>)?.Count() ?? total;
        var runnable = _actionViews.Count(view => view.CanRun);
        var status = runnable > 0 ? $"可执行 {runnable} / {total} 项" : "当前模式下全部禁用";
        var filterNote = shown == total ? string.Empty : $"（搜索命中 {shown} 项）";

        ActionLibraryText.Text = $"动作库：共 {total} 项，{status}{filterNote}。";
        RunActionsButton.IsEnabled = !_acting && runnable > 0;
    }

    /// <summary>点击“执行勾选动作”：校验并确认后按顺序执行勾选动作。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnRunActionsClick(object sender, RoutedEventArgs e)
    {
        if (_acting)
        {
            return;
        }

        if (!EnsureUnlocked())
        {
            return;
        }

        if (_verdict is null || !_verdict.CanStart)
        {
            MessageBox.Show(this, "启动自检未通过，暂不能执行修复动作。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_verdict.IsAdministrator)
        {
            MessageBox.Show(this, "当前为只读诊断模式，修复动作库全部禁用。请先以管理员身份重新启动。",
                "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = _actionViews.Where(view => view.IsSelected && view.CanRun).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先勾选至少一个要执行的动作。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var missing = selected
            .Where(view => ActionRules.MissingParameters(view.Action, view.ParameterValues()).Count > 0)
            .ToList();
        if (missing.Count > 0)
        {
            var detail = string.Join("；", missing.Select(view =>
                $"{view.Name}（缺少：{string.Join("、", ActionRules.MissingParameters(view.Action, view.ParameterValues()))}）"));
            MessageBox.Show(this, $"以下动作缺少必填参数，请填写后再执行：\n{detail}",
                "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmActions(selected))
        {
            return;
        }

        await RunActionsAsync(selected);
    }

    /// <summary>点击“中止后续动作”：终止当前动作并跳过其余勾选动作。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelActionsClick(object sender, RoutedEventArgs e)
    {
        _actionCancellation?.Cancel();
        ActionProgressText.Text = "正在中止：将终止当前动作，并跳过其余勾选动作…";
    }

    /// <summary>
    /// 执行前的确认流程：不可逆动作逐项输入确认词，高风险动作逐项二次确认，
    /// 最后以汇总区明示本次将执行的变更（含“可能中断会话”提示）。
    /// </summary>
    /// <param name="selected">本次勾选且可执行的动作清单（已按执行顺序排列）。</param>
    /// <returns>全部确认通过返回 true；任一环节取消返回 false。</returns>
    private bool ConfirmActions(IReadOnlyList<ActionItemView> selected)
    {
        foreach (var view in selected.Where(item => item.IsIrreversible))
        {
            if (!PromptConfirmWord(view))
            {
                return false;
            }
        }

        foreach (var view in selected.Where(item => item.IsHighRisk && !item.IsIrreversible))
        {
            var answer = MessageBox.Show(
                this,
                $"即将执行高风险动作：{view.Name}\n\n影响：{view.Summary}\n回滚：{view.RollbackText}\n\n确认执行该项？",
                "高风险动作确认（逐项）",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        var summary = new StringBuilder();
        summary.AppendLine("本次将执行以下变更（按执行顺序，需重启的排最后）：");
        foreach (var view in selected)
        {
            summary.Append("· [").Append(ActionRules.DescribeRisk(view.Risk)).Append("] ").Append(view.Name);
            if (view.RequiresRestart)
            {
                summary.Append("（需重启生效）");
            }

            summary.AppendLine();
        }

        if (selected.Any(view => view.Action.MayInterruptSession))
        {
            summary.AppendLine();
            summary.AppendLine("提示：本次包含可能中断当前会话的动作（如重置网络），执行后连接可能短暂中断。");
        }

        summary.AppendLine();
        summary.Append("确认执行？");

        var confirmed = MessageBox.Show(
            this,
            summary.ToString(),
            "执行修复动作（汇总确认）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return confirmed == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 弹出不可逆动作确认窗口：要求输入确认词后方可继续。
    /// </summary>
    /// <param name="view">不可逆动作视图。</param>
    /// <returns>确认词正确且点击“确认执行”返回 true；否则返回 false。</returns>
    private bool PromptConfirmWord(ActionItemView view)
    {
        var window = new Window
        {
            Title = "不可逆动作确认",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = $"即将执行不可逆动作：{view.Name}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = view.Summary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"该动作不可回滚。请输入确认词 {ActionRules.IrreversibleConfirmWord} 以继续：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });

        var input = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button { Content = "取消", Width = 90, Height = 30, IsCancel = true };
        var confirm = new Button
        {
            Content = "确认执行",
            Width = 110,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
            IsEnabled = false
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);

        input.TextChanged += (_, _) =>
            confirm.IsEnabled = string.Equals(
                input.Text.Trim(), ActionRules.IrreversibleConfirmWord, StringComparison.Ordinal);
        cancel.Click += (_, _) => window.DialogResult = false;
        confirm.Click += (_, _) => window.DialogResult = true;

        window.Content = panel;
        return window.ShowDialog() == true;
    }

    /// <summary>
    /// 按顺序执行勾选动作：逐项调用统一执行器，实时追加结果；中止后跳过其余动作。
    /// </summary>
    /// <param name="selected">本次勾选且可执行的动作清单（已按执行顺序排列）。</param>
    /// <returns>无返回值的异步任务。</returns>
    private async Task RunActionsAsync(IReadOnlyList<ActionItemView> selected)
    {
        _acting = true;
        SetActingBusy(true);
        _actionResults.Clear();
        ActionProgressBar.Value = 0;
        ActionProgressText.Text = $"即将执行 {selected.Count} 项动作（只读与低风险动作无需确认）。";

        _actionCancellation = new CancellationTokenSource();
        var token = _actionCancellation.Token;
        var completed = 0;

        try
        {
            foreach (var view in selected)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                ActionProgressText.Text = $"正在执行：{view.Name}（{completed + 1}/{selected.Count}）";

                var result = await _actionExecutor.ExecuteAsync(
                    view.Action,
                    view.ParameterValues(),
                    _privilege,
                    _dataDirectory,
                    _lastSessionId,
                    token);

                _actionResults.Add(BuildActionResultView(result));
                _rawActionResults.Add(result);
                completed++;

                if (result.Outcome == ActionOutcome.Cancelled)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"执行修复动作时发生异常：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            var cancelled = token.IsCancellationRequested;
            ActionProgressBar.Value = selected.Count == 0 ? 0 : completed * 100.0 / selected.Count;
            ActionProgressText.Text = cancelled
                ? $"已中止：已完成 {completed} / {selected.Count} 项，其余动作未执行。"
                : $"执行完成：共 {completed} 项动作（详见右侧结果）。";

            _acting = false;
            _actionCancellation?.Dispose();
            _actionCancellation = null;
            SetActingBusy(false);
            UpdateActionLibraryText();

            // M8：动作执行记入审计（不含任何凭证明文）。
            AppendAudit(
                AuditEventKind.ActionExecuted,
                cancelled ? $"已中止：完成 {completed} / {selected.Count} 项修复动作。" : $"已执行 {completed} 项修复动作。",
                string.Join("；", selected.Take(completed).Select(view => view.Name)),
                _lastSessionId);
        }
    }

    /// <summary>把动作执行结果转换为界面视图。</summary>
    /// <param name="result">动作执行结果。</param>
    /// <returns>供结果列表绑定的视图对象。</returns>
    private static ActionResultView BuildActionResultView(ActionExecutionResult result)
    {
        var detail = new StringBuilder();
        detail.Append("风险：").Append(ActionRules.DescribeRisk(result.Risk));
        detail.Append(" · 退出码：").Append(result.ExitCode.ToString(CultureInfo.InvariantCulture));
        detail.Append(" · 耗时 ").Append(result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" 秒");
        if (result.TimedOut)
        {
            detail.Append(" · 已超时终止");
        }

        var output = result.StandardOutput.Trim();
        var error = result.StandardError.Trim();
        if (!string.IsNullOrEmpty(result.Reason))
        {
            error = error.Length == 0 ? result.Reason! : result.Reason + "\n" + error;
        }

        return new ActionResultView(
            result.ActionName,
            result.Outcome,
            ActionRules.DescribeOutcome(result.Outcome),
            detail.ToString(),
            output,
            error,
            output.Length > 0,
            error.Length > 0);
    }

    /// <summary>切换动作执行忙碌态（执行期间禁用触发、搜索与清单，启用中止按钮）。</summary>
    /// <param name="busy">是否正在执行。</param>
    private void SetActingBusy(bool busy)
    {
        RunActionsButton.IsEnabled = !busy;
        CancelActionsButton.IsEnabled = busy;
        ActionList.IsEnabled = !busy;
        ActionSearchBox.IsEnabled = !busy;
    }

    // ═══════════════════ M5 软件安装与卸载 ═══════════════════

    /// <summary>页签切换：首次进入“软件安装卸载（M5）”时按需加载软件包目录（避免拖慢启动自检）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 解析阶段 TabControl 会为默认选中项触发一次本事件，此时界面尚未初始化完毕。
        if (!_viewReady || !ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        if (MainTabs.SelectedIndex == 4)
        {
            EnsurePackagesLoaded();
        }
        else if (MainTabs.SelectedIndex == 6)
        {
            // M7：首次进入“记录与台账”页时按需加载台账，避免拖慢启动。
            EnsureLedgerLoaded();
        }
        else if (MainTabs.SelectedIndex == 7)
        {
            // M8：进入“介质与设置”页时刷新介质状态、设置、高级模式与处理人。
            RefreshSettingsView();
        }
        else if (MainTabs.SelectedIndex == 8)
        {
            // M9：进入“自定义命令”页时刷新入口可用性并按需加载命令历史（避免拖慢启动）。
            RefreshCustomCommandAvailability();
            EnsureCustomHistoryLoaded();
        }
    }

    /// <summary>
    /// 确保软件包目录已加载：首次读取目标机已装软件清单、加载 U 盘 <c>packages/</c> 并校验哈希，
    /// 随后构建列表与状态文案。
    /// </summary>
    private void EnsurePackagesLoaded()
    {
        if (_packageCatalog is not null)
        {
            return;
        }

        _installedSoftware = ReadInstalledSoftwareSafely();
        try
        {
            _packageCatalog = _packageLibraryLoader.Load(_programDirectory);
        }
        catch (Exception ex)
        {
            _packageCatalog = new PackageCatalog(
                Array.Empty<SoftwarePackage>(),
                new[] { $"软件包目录加载失败：{ex.Message}" });
        }

        RebuildPackageLists();
        RenderPackageStatus();
        UpdatePackageOperationText();
    }

    /// <summary>读取目标机已装软件清单（只读）；读取失败时如实记录问题，不阻断其余功能。</summary>
    /// <returns>已装软件条目；读取失败时返回空清单。</returns>
    private IReadOnlyList<InstalledSoftwareEntry> ReadInstalledSoftwareSafely()
    {
        try
        {
            _installedProblem = string.Empty;
            return _packageService.ReadInstalledSoftware();
        }
        catch (Exception ex)
        {
            _installedProblem = $"已装软件清单读取失败：{ex.Message}（可稍后点击“刷新已装软件”重试）。";
            return Array.Empty<InstalledSoftwareEntry>();
        }
    }

    /// <summary>按当前已装清单重建软件包与已装软件两个列表（保留已选中的软件包）。</summary>
    private void RebuildPackageLists()
    {
        var packages = _packageCatalog?.Packages ?? (IReadOnlyList<SoftwarePackage>)Array.Empty<SoftwarePackage>();
        _packageViews = PackageViewFactory.BuildPackages(packages, _installedSoftware);
        _installedViews = PackageViewFactory.BuildInstalled(_installedSoftware);

        PackageList.ItemsSource = _packageViews;
        ApplyInstalledFilter();

        var selectedId = _selectedPackage?.PackageId;
        _selectedPackage = selectedId is null
            ? null
            : _packageViews.FirstOrDefault(view => string.Equals(view.PackageId, selectedId, StringComparison.OrdinalIgnoreCase));
        PackageList.SelectedItem = _selectedPackage;
    }

    /// <summary>按搜索关键词过滤已装软件清单（按名称匹配，忽略大小写）。</summary>
    private void ApplyInstalledFilter()
    {
        var keyword = InstalledSearchBox?.Text?.Trim() ?? string.Empty;
        InstalledList.ItemsSource = keyword.Length == 0
            ? _installedViews
            : _installedViews.Where(view => view.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>刷新软件包与已装清单的状态文案与问题提示。</summary>
    private void RenderPackageStatus()
    {
        var packages = _packageCatalog?.Packages ?? (IReadOnlyList<SoftwarePackage>)Array.Empty<SoftwarePackage>();
        var verified = packages.Count(package => package.CanExecute);
        PackageStatusText.Text = _packageCatalog is null
            ? "尚未加载软件包。进入本页签后会自动加载。"
            : $"软件包：共 {packages.Count} 个，其中校验通过 {verified} 个；目标机已装软件 {_installedSoftware.Count} 项。";

        var problems = new List<string>();
        if (_packageCatalog is not null)
        {
            problems.AddRange(_packageCatalog.Problems);
        }

        if (_installedProblem.Length > 0)
        {
            problems.Add(_installedProblem);
        }

        PackageProblemText.Text = string.Join("\n", problems);
        PackageProblemText.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点击“重新加载软件包”：重新读取 <c>packages/</c> 并重新校验每个安装程序的 SHA-256。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnReloadPackagesClick(object sender, RoutedEventArgs e)
    {
        if (_packaging)
        {
            return;
        }

        SetPackageBusy(true);
        PackageProgressBar.Value = 0;
        PackageStatusText.Text = "正在重新加载软件包并校验 SHA-256…";

        try
        {
            _installedSoftware = await Task.Run(ReadInstalledSoftwareSafely);
            _packageCatalog = await Task.Run(() => _packageLibraryLoader.Load(_programDirectory));
            RebuildPackageLists();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"重新加载软件包失败：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetPackageBusy(false);
            RenderPackageStatus();
            UpdatePackageOperationText();
        }
    }

    /// <summary>点击“刷新已装软件”：重新读取目标机注册表中的已装软件清单并刷新匹配结果。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnRefreshInstalledClick(object sender, RoutedEventArgs e)
    {
        if (_packaging)
        {
            return;
        }

        SetPackageBusy(true);
        PackageStatusText.Text = "正在刷新目标机已装软件清单…";

        try
        {
            _installedSoftware = await Task.Run(ReadInstalledSoftwareSafely);
            RebuildPackageLists();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"刷新已装软件失败：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetPackageBusy(false);
            RenderPackageStatus();
            UpdatePackageOperationText();
        }
    }

    /// <summary>已装软件搜索框文本变化时重新过滤清单。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnInstalledSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_viewReady)
        {
            return;
        }

        ApplyInstalledFilter();
    }

    /// <summary>软件包列表选中项变化时刷新操作区与执行前检查预览。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnPackageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady)
        {
            return;
        }

        _selectedPackage = PackageList.SelectedItem as PackageItemView;
        UpdatePackageOperationText();
    }

    /// <summary>操作类型（安装 / 升级 / 卸载）切换时重新计算执行前检查预览。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnPackageOperationChanged(object sender, RoutedEventArgs e)
    {
        // XAML 解析阶段 RadioButton 的默认选中会触发本事件，此时操作区控件尚未创建。
        if (!_viewReady)
        {
            return;
        }

        UpdatePackageOperationText();
    }

    /// <summary>读取当前选中的操作类型。</summary>
    /// <returns>操作类型（安装 / 升级 / 卸载）。</returns>
    private PackageOperationKind CurrentPackageKind()
    {
        if (PackageUninstallRadio?.IsChecked == true)
        {
            return PackageOperationKind.Uninstall;
        }

        return PackageUpgradeRadio?.IsChecked == true ? PackageOperationKind.Upgrade : PackageOperationKind.Install;
    }

    /// <summary>刷新操作区：显示目标包、执行前检查（阻断项与提示项）与执行按钮可用性。</summary>
    private void UpdatePackageOperationText()
    {
        if (_selectedPackage is null)
        {
            PackageOperationTargetText.Text = "未选择软件包。";
            PackagePreflightText.Text = "请选择左侧软件包以查看执行前检查结果。";
            UpdatePackageAvailability();
            return;
        }

        var descriptor = _selectedPackage.Package.Descriptor;
        PackageOperationTargetText.Text = $"{descriptor.Name} {descriptor.Version}（{descriptor.PackageId}）";

        var kind = CurrentPackageKind();
        var preflight = _packageService.Preflight(_selectedPackage.Package, kind, _installedSoftware, _privilege);

        var lines = new List<string>
        {
            $"操作：{PackageRules.DescribeOperationKind(kind)} · {_selectedPackage.MatchDetailText}"
        };

        lines.AddRange(preflight.Blockers.Select(item => "✘ " + item));
        lines.AddRange(preflight.Warnings.Select(item => "▲ " + item));

        if (preflight.CanProceed)
        {
            lines.Add(preflight.Warnings.Count == 0 ? "✔ 检查通过，可以执行。" : "▲ 检查通过，但请先确认以上提示。");
        }
        else
        {
            lines.Add("✘ 存在阻断项，已禁止执行。");
        }

        PackagePreflightText.Text = string.Join("\n", lines);
        UpdatePackageAvailability();
    }

    /// <summary>
    /// 判断当前运行条件是否允许执行软件安装 / 卸载，并给出原因。
    /// 只读诊断模式、启动自检未通过或介质可用空间不足时禁止执行。
    /// </summary>
    /// <param name="reason">禁止原因；允许时为 null。</param>
    /// <returns>允许返回 true。</returns>
    private bool CanOperatePackages(out string? reason)
    {
        if (_verdict is null || !_verdict.CanStart)
        {
            reason = "启动自检未通过：软件安装与卸载已禁用。";
            return false;
        }

        if (!_verdict.IsAdministrator)
        {
            reason = "只读诊断模式：软件安装与卸载需要管理员权限，当前已禁用。";
            return false;
        }

        if (!_verdict.Capabilities.CanInstall)
        {
            reason = "当前运行条件下不允许安装 / 卸载软件。";
            return false;
        }

        if (!_verdict.Capabilities.CanStartNewSession)
        {
            reason = "介质可用空间低于 200 MB：已停止开始新的执行会话，请先归档或更换介质。";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>刷新软件包区按钮可用性（执行需选中校验通过的包，且当前条件允许）。</summary>
    private void UpdatePackageAvailability()
    {
        var canOperate = CanOperatePackages(out _);
        ReloadPackagesButton.IsEnabled = !_packaging;
        RefreshInstalledButton.IsEnabled = !_packaging;
        RunPackageButton.IsEnabled = !_packaging
            && canOperate
            && _selectedPackage is not null
            && _selectedPackage.CanExecute;
    }

    /// <summary>切换软件包操作忙碌态（加载 / 执行期间禁用触发与选择，启用中止按钮）。</summary>
    /// <param name="busy">是否忙碌。</param>
    private void SetPackageBusy(bool busy)
    {
        _packaging = busy;
        ReloadPackagesButton.IsEnabled = !busy;
        RefreshInstalledButton.IsEnabled = !busy;
        PackageList.IsEnabled = !busy;
        InstalledList.IsEnabled = !busy;
        InstalledSearchBox.IsEnabled = !busy;
        PackageInstallRadio.IsEnabled = !busy;
        PackageUpgradeRadio.IsEnabled = !busy;
        PackageUninstallRadio.IsEnabled = !busy;
        PackageForceReinstallCheck.IsEnabled = !busy;
        CancelPackageButton.IsEnabled = busy;
        UpdatePackageAvailability();
    }

    /// <summary>点击“执行操作”：确认后执行一次包操作（安装 / 升级 / 卸载）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnRunPackageClick(object sender, RoutedEventArgs e)
    {
        if (_packaging || _selectedPackage is null)
        {
            return;
        }

        if (!EnsureUnlocked())
        {
            return;
        }

        if (!CanOperatePackages(out var reason))
        {
            MessageBox.Show(this, reason ?? "当前不允许执行软件安装 / 卸载。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var kind = CurrentPackageKind();
        var forceReinstall = PackageForceReinstallCheck.IsChecked == true;
        var preflight = _packageService.Preflight(_selectedPackage.Package, kind, _installedSoftware, _privilege);

        if (!preflight.CanProceed)
        {
            MessageBox.Show(
                this,
                "存在阻断项，不能执行：\n" + string.Join("\n", preflight.Blockers),
                "DeskIT",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmPackageOperation(_selectedPackage, kind, forceReinstall, preflight))
        {
            return;
        }

        await RunPackageOperationAsync(_selectedPackage, kind, forceReinstall);
    }

    /// <summary>点击“中止”：终止当前包操作；安装器可能处于不确定状态，需人工核对。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelPackageClick(object sender, RoutedEventArgs e)
    {
        _packageCancellation?.Cancel();
        PackageStatusText.Text = "正在中止本次包操作…";
    }

    /// <summary>
    /// 包操作前的确认流程：先汇总确认（含提示项），卸载或强制重装再追加一次高风险确认。
    /// </summary>
    /// <param name="view">目标软件包视图。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="forceReinstall">是否强制重装。</param>
    /// <param name="preflight">执行前检查结果。</param>
    /// <returns>全部确认通过返回 true。</returns>
    private bool ConfirmPackageOperation(
        PackageItemView view,
        PackageOperationKind kind,
        bool forceReinstall,
        PackagePreflight preflight)
    {
        var descriptor = view.Package.Descriptor;
        var summary = new StringBuilder();
        summary.AppendLine($"即将对目标机执行：{PackageRules.DescribeOperationKind(kind)} {descriptor.Name} {descriptor.Version}");
        summary.AppendLine(view.MatchDetailText);
        summary.AppendLine($"安装程序：{view.Package.Check.InstallerPath}");

        if (kind == PackageOperationKind.Uninstall)
        {
            summary.AppendLine("将使用该软件自身的卸载入口执行卸载；程序不会强制删除安装目录与注册表残留。");
        }

        foreach (var warning in preflight.Warnings)
        {
            summary.AppendLine("▲ " + warning);
        }

        summary.AppendLine();
        summary.Append("确认执行？");

        var confirmed = MessageBox.Show(
            this,
            summary.ToString(),
            $"确认{PackageRules.DescribeOperationKind(kind)}（第 1 步：汇总确认）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.Yes)
        {
            return false;
        }

        if (kind != PackageOperationKind.Uninstall && !forceReinstall)
        {
            return true;
        }

        var risk = MessageBox.Show(
            this,
            kind == PackageOperationKind.Uninstall
                ? "卸载属于高风险且不可回滚的操作，确认继续？"
                : "强制重装将覆盖目标机现有安装（需二次确认），确认继续？",
            "高风险操作确认（第 2 步）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return risk == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 执行一次包操作：调用包服务（内部复用 M4 统一执行器），追加结果并刷新已装清单与匹配。
    /// </summary>
    /// <param name="view">目标软件包视图。</param>
    /// <param name="kind">操作类型。</param>
    /// <param name="forceReinstall">是否强制重装。</param>
    /// <returns>无返回值的异步任务。</returns>
    private async Task RunPackageOperationAsync(PackageItemView view, PackageOperationKind kind, bool forceReinstall)
    {
        SetPackageBusy(true);
        PackageProgressBar.Value = 0;
        PackageStatusText.Text =
            $"正在执行：{PackageRules.DescribeOperationKind(kind)} {view.Name} {view.Version}"
            + "（安装类动作可能长时间无输出，请耐心等待）…";

        _packageCancellation = new CancellationTokenSource();

        try
        {
            var result = await _packageService.ExecuteAsync(
                view.Package,
                kind,
                _installedSoftware,
                _privilege,
                forceReinstall,
                _dataDirectory,
                _lastSessionId,
                _packageCancellation.Token);

            _packageResults.Insert(0, PackageViewFactory.BuildResult(result));
            PackageProgressBar.Value = 100;
            PackageStatusText.Text =
                $"操作完成：{PackageRules.DescribeOperationKind(kind)} {view.Name} → {result.Outcome.Description}";

            _installedSoftware = await Task.Run(ReadInstalledSoftwareSafely);
            RebuildPackageLists();
        }
        catch (OperationCanceledException)
        {
            PackageStatusText.Text = "已中止本次包操作；安装器可能处于不确定状态，请人工核对该软件的实际状态。";
        }
        catch (Exception ex)
        {
            PackageStatusText.Text = "包操作失败。";
            MessageBox.Show(this, $"执行包操作时发生异常：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetPackageBusy(false);
            _packageCancellation?.Dispose();
            _packageCancellation = null;
            UpdatePackageOperationText();
        }
    }

    // ═══════════════════ M6 账号与权限辅助 ═══════════════════

    /// <summary>点击“查询账号与权限”：执行一次只读查询并渲染结果。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnQueryAccountsClick(object sender, RoutedEventArgs e)
    {
        if (_queryingAccounts)
        {
            return;
        }

        if (_verdict is null || !_verdict.CanStart)
        {
            MessageBox.Show(this, "启动自检未通过，暂不能查询账号与权限。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _queryingAccounts = true;
        SetAccountBusy(true);
        AccountProgressBar.Value = 10;
        AccountSummaryText.Text = "正在查询账号与权限（只读）…";
        AccountQueryText.Text = string.Empty;
        AccountAttentionText.Visibility = Visibility.Collapsed;
        AccountProblemsText.Visibility = Visibility.Collapsed;

        _accountCancellation = new CancellationTokenSource();

        try
        {
            var snapshot = await _accountQuery.QueryAsync(_privilege, _accountCancellation.Token);
            RenderAccountSnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            AccountSummaryText.Text = "已中止查询。";
            AccountProgressBar.Value = 0;
        }
        catch (Exception ex)
        {
            AccountSummaryText.Text = "查询失败。";
            AccountProgressBar.Value = 0;
            MessageBox.Show(this, $"账号与权限查询失败：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _queryingAccounts = false;
            _accountCancellation?.Dispose();
            _accountCancellation = null;
            SetAccountBusy(false);
        }
    }

    /// <summary>点击“中止查询”：终止本次账号与权限只读查询。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelAccountQueryClick(object sender, RoutedEventArgs e)
    {
        _accountCancellation?.Cancel();
        AccountSummaryText.Text = "正在中止查询…";
    }

    /// <summary>把账号与权限查询结果渲染到界面（账号列表、管理员组、事实项、凭据残留与问题）。</summary>
    /// <param name="snapshot">只读查询结果。</param>
    private void RenderAccountSnapshot(AccountSnapshot snapshot)
    {
        var view = AccountViewFactory.Build(snapshot);

        AccountList.ItemsSource = view.Accounts;
        AdministratorList.ItemsSource = view.Administrators;
        AccountFactList.ItemsSource = view.Facts;
        AccountCredentialList.ItemsSource = view.SharedCredentials;
        AccountCredentialEmptyText.Visibility = view.HasCredentials ? Visibility.Collapsed : Visibility.Visible;

        AccountSummaryText.Text = view.SummaryText;
        AccountQueryText.Text = view.QueryText;

        AccountAttentionText.Text = view.AttentionText;
        AccountAttentionText.Visibility = view.AttentionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        AccountProblemsText.Text = string.Join("\n", view.Problems);
        AccountProblemsText.Visibility = view.HasProblems ? Visibility.Visible : Visibility.Collapsed;

        AccountProgressBar.Value = 100;
    }

    /// <summary>刷新账号与权限查询按钮可用性（只读查询，启动自检通过即可用）。</summary>
    private void UpdateAccountAvailability()
    {
        QueryAccountsButton.IsEnabled = !_queryingAccounts && _verdict is not null && _verdict.CanStart;
    }

    /// <summary>切换账号查询忙碌态（查询期间禁用触发，启用中止按钮）。</summary>
    /// <param name="busy">是否忙碌。</param>
    private void SetAccountBusy(bool busy)
    {
        QueryAccountsButton.IsEnabled = !busy && _verdict is not null && _verdict.CanStart;
        CancelAccountQueryButton.IsEnabled = busy;
    }

    // ═══════════════════ M7 记录与多机台账 ═══════════════════

    /// <summary>
    /// 采集完成时建立/更新本次处理会话上下文（机器标识由档案归并确定）。
    /// </summary>
    /// <param name="snapshot">本次采集生成的快照。</param>
    private void EstablishSessionFromCollection(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BeginSessionIfNeeded();
        RefreshLedgerSessionUi();
    }

    /// <summary>
    /// 诊断完成时把诊断会话标识、结论写入本次处理会话上下文，作为台账记录来源。
    /// </summary>
    /// <param name="outcome">诊断与入库结果。</param>
    private void EstablishSessionFromDiagnosis(ProblemDiagnosisOutcome outcome)
    {
        BeginSessionIfNeeded();

        // 诊断会话标识优先作为记录会话标识（记录需可回溯到 M3 会话）。
        if (!string.IsNullOrWhiteSpace(outcome.Session.SessionId))
        {
            _ledgerSessionId = outcome.Session.SessionId;
        }

        _ledgerScenes = outcome.Report.Scenes;
        RefreshLedgerSessionUi();
    }

    /// <summary>
    /// 若本次尚无活动会话则新建一个：生成会话标识与开始时间，并清空上一会话的动作缓存。
    /// </summary>
    private void BeginSessionIfNeeded()
    {
        if (_ledgerSessionActive)
        {
            return;
        }

        var startedAt = DateTimeOffset.Now;
        _ledgerSessionStartedAt = startedAt;
        _ledgerSessionId = DiagnosisRules.NewSessionId(startedAt);
        _ledgerScenes = Array.Empty<DiagnosisSceneResult>();
        _rawActionResults.Clear();
        _rawCustomCommands.Clear();
        _rawPersistenceChanges.Clear();
        _ledgerSessionActive = true;
    }

    /// <summary>
    /// 刷新“本次会话”页的会话提示与表单可用性，并同步处理人字段与历史提示。
    /// </summary>
    private void RefreshLedgerSessionUi()
    {
        var (machineId, source, hostName) = ResolveCurrentMachine();

        if (_ledgerSessionActive && !string.IsNullOrWhiteSpace(machineId))
        {
            LedgerFormPanel.IsEnabled = true;
            FinishLedgerButton.IsEnabled = true;
            var started = _ledgerSessionStartedAt ?? DateTimeOffset.Now;
            LedgerSessionInfoText.Text =
                $"会话 {_ledgerSessionId} · 机器 {hostName}（{machineId}，来源：{MachineIdSourceText.Describe(source)}）· "
                + $"权限：{(_privilege == PrivilegeLevel.Administrator ? "管理员" : "只读（标准用户）")} · 开始于 {started:yyyy-MM-dd HH:mm:ss}";
            LedgerFinishStatusText.Text = "确认结论与问题类别后点击“完成会话并产出报告”，记录将写入台账。";
        }
        else if (_ledgerSessionActive)
        {
            LedgerFormPanel.IsEnabled = false;
            FinishLedgerButton.IsEnabled = false;
            LedgerSessionInfoText.Text = "本次处理尚未建立机器标识：请先在“设备信息采集（M2）”完成一次采集，再回到本页完成会话。";
        }
        else
        {
            LedgerFormPanel.IsEnabled = false;
            FinishLedgerButton.IsEnabled = false;
            LedgerSessionInfoText.Text = "尚未建立会话：请先在“设备信息采集（M2）”“问题诊断（M3）”中开始一次处理。";
        }

        RefreshOperatorFields();
        RefreshCurrentMachineHistory();
    }

    /// <summary>
    /// 从介质读取当前登记处理人并预填表单（仅在对应输入框为空时填充，避免打断现场输入）。
    /// </summary>
    private void RefreshOperatorFields()
    {
        _currentOperator = _mediaStateStore.GetCurrentOperator(_dataDirectory);

        if (_currentOperator is null)
        {
            LedgerOperatorHintText.Text = "介质尚未登记处理人：请填写工号与姓名，并点击“登记为介质处理人”。";
            return;
        }

        if (string.IsNullOrWhiteSpace(LedgerOperatorIdBox.Text))
        {
            LedgerOperatorIdBox.Text = _currentOperator.EmployeeId;
        }

        if (string.IsNullOrWhiteSpace(LedgerOperatorNameBox.Text))
        {
            LedgerOperatorNameBox.Text = _currentOperator.Name;
        }

        LedgerOperatorHintText.Text = $"介质当前登记处理人：{_currentOperator.DisplayText}。修改后点击“登记为介质处理人”可更新介质登记。";
    }

    /// <summary>
    /// 解析当前处理机器的标识、来源与计算机名（按计算机名匹配既有档案；无档案时返回空标识）。
    /// </summary>
    /// <returns>机器标识、标识来源与计算机名。</returns>
    private (string Id, MachineIdSource Source, string HostName) ResolveCurrentMachine()
    {
        var hostName = Environment.MachineName;
        try
        {
            var profile = _snapshotStore.LoadAllProfiles(_dataDirectory)
                .FirstOrDefault(p => string.Equals(p.HostName, hostName, StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
            {
                return (profile.MachineId, profile.MachineIdSource, profile.HostName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取档案失败时按“尚未建立机器标识”处理，不阻断界面。
        }

        return (string.Empty, MachineIdSource.Fallback, hostName);
    }

    /// <summary>
    /// 刷新“目标机档案”页：展示当前处理机器的历史记录（按处理时间倒序）与历史提示。
    /// </summary>
    private void RefreshCurrentMachineHistory()
    {
        var (machineId, _, hostName) = ResolveCurrentMachine();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            MachineRecordList.ItemsSource = null;
            MachineHistoryNoticeText.Text = "尚未建立机器标识：请先完成一次设备信息采集。";
            LedgerHistoryNoticeText.Visibility = Visibility.Collapsed;
            ViewLastHandledButton.IsEnabled = false;
            UpdateLedgerReuseHint();
            return;
        }

        var records = _ledgerRecords
            .Where(record => string.Equals(record.MachineId, machineId, StringComparison.Ordinal))
            .OrderByDescending(record => record.FinishedAt)
            .ToList();

        MachineRecordList.ItemsSource = records
            .Select(record => new LedgerRecordView(record, _ledgerConflictRecordIds.Contains(record.RecordId)))
            .ToList();

        if (records.Count == 0)
        {
            MachineHistoryNoticeText.Text = $"机器 {hostName}（{machineId}）在本 U 盘尚无历史处理记录。";
            LedgerHistoryNoticeText.Visibility = Visibility.Collapsed;
            ViewLastHandledButton.IsEnabled = false;
            UpdateLedgerReuseHint();
            return;
        }

        var latest = records[0];
        MachineHistoryNoticeText.Text =
            $"本机历史处理 {records.Count} 次 · 最近一次 {latest.FinishedAt:yyyy-MM-dd HH:mm:ss}"
            + $"（{LedgerRules.DescribeCategory(latest.Category)} · {LedgerRules.DescribeConclusion(latest.Conclusion)}）。";
        LedgerHistoryNoticeText.Text =
            $"该机器此前处理过 {records.Count} 次，最近结论：{LedgerRules.DescribeConclusion(latest.Conclusion)}。可点击“查看上次处理”。";
        LedgerHistoryNoticeText.Visibility = Visibility.Visible;
        ViewLastHandledButton.IsEnabled = true;
        UpdateLedgerReuseHint();
    }

    /// <summary>
    /// 刷新“问题类别”卡片下的历史沿用提示（展示该机器最近一次的类别与结论，便于沿用或调整）。
    /// </summary>
    private void UpdateLedgerReuseHint()
    {
        var (machineId, _, _) = ResolveCurrentMachine();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            LedgerReuseHintText.Visibility = Visibility.Collapsed;
            return;
        }

        var latest = _ledgerRecords
            .Where(record => string.Equals(record.MachineId, machineId, StringComparison.Ordinal))
            .OrderByDescending(record => record.FinishedAt)
            .FirstOrDefault();

        if (latest is null)
        {
            LedgerReuseHintText.Visibility = Visibility.Collapsed;
            return;
        }

        LedgerReuseHintText.Text =
            $"该机器最近一次：类别 {LedgerRules.DescribeCategory(latest.Category)}、结论 {LedgerRules.DescribeConclusion(latest.Conclusion)}；"
            + $"当前选择类别：{CurrentCategoryText()}。";
        LedgerReuseHintText.Visibility = Visibility.Visible;
    }

    /// <summary>取当前选择的问题类别中文文本（未选时按“其他”）。</summary>
    /// <returns>问题类别中文文本。</returns>
    private string CurrentCategoryText()
        => (LedgerCategoryCombo.SelectedItem as LedgerCategoryOption)?.Text ?? LedgerRules.DescribeCategory(ProblemCategory.Other);

    /// <summary>结论单选变化：选中“未解决”时展开未解决原因与说明。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnLedgerConclusionChanged(object sender, RoutedEventArgs e)
    {
        // XAML 解析阶段“已解决”单选按钮的默认选中会触发本事件。
        if (!_viewReady)
        {
            return;
        }

        LedgerUnresolvedPanel.Visibility = LedgerConclusionUnresolvedRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>问题类别变化：刷新历史沿用提示。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnLedgerCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady)
        {
            return;
        }

        UpdateLedgerReuseHint();
    }

    /// <summary>员工现象描述变化：刷新字数计数。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSymptomChanged(object sender, TextChangedEventArgs e)
    {
        if (!_viewReady)
        {
            return;
        }

        LedgerSymptomCountText.Text = $"{LedgerSymptomBox.Text.Length} / {LedgerRules.SymptomMaxLength}";
    }

    /// <summary>点击“查看上次处理”：切换到“目标机档案”子页查看本机历史记录。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnViewLastHandledClick(object sender, RoutedEventArgs e)
    {
        if (ViewLastHandledButton.IsEnabled)
        {
            LedgerTabs.SelectedIndex = 1;
        }
    }

    /// <summary>点击“登记为介质处理人”：校验后二次确认并登记到 data/operators.json。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSaveOperatorClick(object sender, RoutedEventArgs e)
    {
        var candidate = new OperatorInfo(LedgerOperatorIdBox.Text.Trim(), LedgerOperatorNameBox.Text.Trim());
        if (!LedgerRules.IsCompleteOperator(candidate))
        {
            MessageBox.Show(this, "处理人工号与姓名均必填。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_currentOperator is not null && SameOperator(_currentOperator, candidate))
        {
            LedgerOperatorHintText.Text = $"介质当前登记处理人已是 {candidate.DisplayText}，无需重复登记。";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"确认将 {candidate.DisplayText} 登记为介质处理人？\n登记信息随介质保存，跨机、跨启动持续有效。",
            "DeskIT",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _mediaStateStore.SetCurrentOperator(_dataDirectory, candidate);
            _currentOperator = candidate;
            LedgerOperatorHintText.Text = $"已登记介质处理人：{candidate.DisplayText}（工号与姓名已保存到 data/operators.json）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"登记失败（介质可能不可写）：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 点击“完成会话并产出报告”：校验记录 → 静默产出脱敏报告 → 追加写入台账；介质不可写时阻止结束。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnFinishSessionClick(object sender, RoutedEventArgs e)
    {
        if (_ledgerBusy)
        {
            return;
        }

        if (!EnsureUnlocked())
        {
            return;
        }

        if (_verdict is null || !_verdict.CanStart)
        {
            MessageBox.Show(this, "启动自检未通过，暂不能完成会话并写入台账。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_ledgerSessionActive)
        {
            MessageBox.Show(this, "本次尚未建立会话：请先进行设备信息采集或问题诊断。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (machineId, machineSource, hostName) = ResolveCurrentMachine();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            MessageBox.Show(this, "本次处理尚未建立机器标识：请先在“设备信息采集（M2）”完成一次采集。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var operatorInfo = new OperatorInfo(LedgerOperatorIdBox.Text.Trim(), LedgerOperatorNameBox.Text.Trim());
        if (!LedgerRules.IsCompleteOperator(operatorInfo))
        {
            MessageBox.Show(this, "处理人工号与姓名均必填（可在上方点击“登记为介质处理人”保存到介质）。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_currentOperator is not null && !SameOperator(_currentOperator, operatorInfo))
        {
            var confirm = MessageBox.Show(
                this,
                $"处理人 {operatorInfo.DisplayText} 与介质当前登记（{_currentOperator.DisplayText}）不一致。\n本次记录将按当前填写写入（仅影响本次记录），是否继续？",
                "DeskIT",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var conclusion = CurrentConclusion();
        UnresolvedReason? reason = null;
        if (conclusion == SessionConclusion.Unresolved)
        {
            if (LedgerUnresolvedReasonCombo.SelectedItem is not LedgerUnresolvedReasonOption reasonOption)
            {
                MessageBox.Show(this, "结论为“未解决”时必须选择未解决原因。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            reason = reasonOption.Reason;
        }

        var now = DateTimeOffset.Now;
        var draft = new HandlingRecord
        {
            RecordId = LedgerRules.NewRecordId(now),
            SessionId = _ledgerSessionId,
            MachineId = machineId,
            MachineIdSource = machineSource,
            HostName = hostName,
            Operator = operatorInfo,
            Category = (LedgerCategoryCombo.SelectedItem as LedgerCategoryOption)?.Category ?? ProblemCategory.Other,
            Tags = ParseTags(LedgerTagsBox.Text),
            SymptomDescription = LedgerSymptomBox.Text.Trim(),
            Scenes = _ledgerScenes,
            Actions = _rawActionResults.Select(LedgerActionEntry.FromExecution).ToList(),
            CustomCommands = _rawCustomCommands.Select(record => record.ToLedgerEntry()).ToList(),
            PersistenceChanges = _rawPersistenceChanges.ToList(),
            Conclusion = conclusion,
            UnresolvedReason = reason,
            UnresolvedNote = LedgerUnresolvedNoteBox.Text.Trim(),
            Privilege = _privilege,
            StartedAt = _ledgerSessionStartedAt ?? now,
            FinishedAt = now,
            ReportFileName = string.Empty
        };

        // 先按命名规则给出预期报告名，报告成功产出后以实际文件名（含版本号）覆盖，保证台账与文件一致。
        draft = draft with { ReportFileName = LedgerRules.BuildReportFileName(draft) + ".html" };

        var validation = LedgerRules.Validate(draft);
        if (!validation.IsValid)
        {
            MessageBox.Show(this, "记录校验未通过：\n" + string.Join("\n", validation.Problems), "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ledgerBusy = true;
        SetLedgerBusy(true);
        try
        {
            var writeResult = _reportWriter.WriteReports(_dataDirectory, draft);
            var record = writeResult.Success && !string.IsNullOrWhiteSpace(writeResult.HtmlFileName)
                ? draft with { ReportFileName = writeResult.HtmlFileName! }
                : draft;

            _ledgerStore.Append(_dataDirectory, record);

            LedgerFinishStatusText.Text =
                $"已写入台账：{record.RecordId} · {LedgerRules.DescribeCategory(record.Category)} · {LedgerRules.DescribeConclusion(record.Conclusion)}"
                + $" · {record.FinishedAt:yyyy-MM-dd HH:mm:ss}（本次 {record.Actions.Count} 个动作）。";

            LedgerReportResultText.Text = writeResult.Success
                ? $"脱敏报告已产出：{record.ReportFileName}（HTML + Markdown，位于 U 盘 reports 目录）。"
                : writeResult.Error ?? "报告生成失败（记录已写入，未阻断）。";
            LedgerReportResultText.Foreground = new SolidColorBrush(
                writeResult.Success ? Color.FromRgb(0x16, 0xA3, 0x4A) : Color.FromRgb(0xB4, 0x53, 0x09));
            LedgerReportResultText.Visibility = Visibility.Visible;

            // M8：会话产出报告记入审计（报告名与记录号，不含凭证明文）。
            AppendAudit(
                AuditEventKind.ReportProduced,
                $"会话 {record.SessionId} 已写入台账并产出脱敏报告。",
                $"记录 {record.RecordId}；报告 {(writeResult.Success ? record.ReportFileName : "未产出")}；动作 {record.Actions.Count} 个。",
                record.SessionId);

            EndLedgerSession();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 介质不可写（或已拔出）：阻止结束，保留会话以便处理后重试。
            MessageBox.Show(
                this,
                $"写入台账失败（介质可能不可写或已拔出），本次会话未结束：{ex.Message}",
                "DeskIT",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _ledgerBusy = false;
            SetLedgerBusy(false);
        }
    }

    /// <summary>按单选按钮取当前选择的会话结论（默认“已解决”）；会话被异常中断时固定为“已中断”。</summary>
    /// <returns>会话结论。</returns>
    private SessionConclusion CurrentConclusion()
    {
        if (_sessionInterrupted)
        {
            return SessionConclusion.Interrupted;
        }

        if (LedgerConclusionRestartRadio.IsChecked == true)
        {
            return SessionConclusion.PendingRestart;
        }

        return LedgerConclusionUnresolvedRadio.IsChecked == true
            ? SessionConclusion.Unresolved
            : SessionConclusion.Resolved;
    }

    /// <summary>把标签文本框拆分为标签清单（支持中英文逗号、分号、顿号与空格分隔并去重）。</summary>
    /// <param name="text">标签文本。</param>
    /// <returns>标签清单；空白时为空清单。</returns>
    private static IReadOnlyList<string> ParseTags(string text)
        => string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(
                    new[] { ',', '，', ';', '；', '、', ' ' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>判断两个处理人是否为同一人（工号忽略大小写、姓名区分大小写）。</summary>
    /// <param name="left">处理人一。</param>
    /// <param name="right">处理人二。</param>
    /// <returns>完全相同返回 true。</returns>
    private static bool SameOperator(OperatorInfo left, OperatorInfo right)
        => string.Equals(left.EmployeeId, right.EmployeeId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    /// <summary>结束本次会话：清空会话缓存与表单，重新加载台账并刷新界面。</summary>
    private void EndLedgerSession()
    {
        _ledgerSessionActive = false;
        _sessionInterrupted = false;
        _ledgerSessionStartedAt = null;
        _ledgerScenes = Array.Empty<DiagnosisSceneResult>();
        _rawActionResults.Clear();
        _rawCustomCommands.Clear();
        _rawPersistenceChanges.Clear();

        LedgerSymptomBox.Clear();
        LedgerTagsBox.Clear();
        LedgerUnresolvedNoteBox.Clear();
        LedgerConclusionResolvedRadio.IsChecked = true;
        LedgerUnresolvedPanel.Visibility = Visibility.Collapsed;
        LedgerCategoryCombo.SelectedIndex = 0;

        ReloadLedgerFromDisk();
        RefreshReportList();
        RefreshLedgerSessionUi();
    }

    /// <summary>切换 M7 台账忙碌态（写台账 / 导出期间禁用触发按钮）。</summary>
    /// <param name="busy">是否忙碌。</param>
    private void SetLedgerBusy(bool busy)
    {
        FinishLedgerButton.IsEnabled = !busy && _ledgerSessionActive;
        SaveOperatorButton.IsEnabled = !busy;
        ExportLedgerButton.IsEnabled = !busy;
        ReloadLedgerButton.IsEnabled = !busy;
    }

    /// <summary>
    /// 确保台账已加载（首次进入本页时读取 data\ledger.db），随后刷新会话界面与报告列表。
    /// </summary>
    private void EnsureLedgerLoaded()
    {
        if (!_ledgerLoaded)
        {
            ReloadLedgerFromDisk();
            _ledgerLoaded = true;
        }

        RefreshLedgerSessionUi();
        RefreshReportList();
    }

    /// <summary>点击“重新加载台账”：从磁盘重读台账并刷新界面与报告列表。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnReloadLedgerClick(object sender, RoutedEventArgs e)
    {
        ReloadLedgerFromDisk();
        RefreshLedgerSessionUi();
        RefreshReportList();
    }

    /// <summary>
    /// 从磁盘读取全部台账记录，并检测机器标识冲突；读取失败时按空台账处理。
    /// </summary>
    private void ReloadLedgerFromDisk()
    {
        try
        {
            _ledgerRecords = _ledgerStore.LoadAll(_dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ledgerRecords = Array.Empty<HandlingRecord>();
            LedgerFinishStatusText.Text = $"台账读取失败：{ex.Message}";
        }

        var conflicts = LedgerRules.DetectMachineIdConflicts(_ledgerRecords);
        _ledgerConflictRecordIds = conflicts
            .SelectMany(conflict => new[] { conflict.FirstRecordId, conflict.SecondRecordId })
            .ToHashSet(StringComparer.Ordinal);

        if (conflicts.Count == 0)
        {
            LedgerConflictText.Visibility = Visibility.Collapsed;
        }
        else
        {
            LedgerConflictText.Text =
                $"检测到 {conflicts.Count} 组机器标识冲突（同一标识存在时间区间重叠的记录，可能为克隆镜像，需人工确认）："
                + string.Join("；", conflicts.Select(conflict => $"{conflict.MachineId}：{conflict.FirstRecordId} 与 {conflict.SecondRecordId}"));
            LedgerConflictText.Visibility = Visibility.Visible;
        }

        ApplyLedgerFilter();
    }

    /// <summary>点击“应用筛选”：按当前筛选条件过滤台账并刷新总览。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnApplyLedgerFilterClick(object sender, RoutedEventArgs e) => ApplyLedgerFilter();

    /// <summary>点击“重置”：清空全部筛选条件并重新展示全部记录。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnResetLedgerFilterClick(object sender, RoutedEventArgs e)
    {
        LedgerFilterCategoryCombo.SelectedIndex = 0;
        LedgerFilterOperatorBox.Clear();
        LedgerFilterFromPicker.SelectedDate = null;
        LedgerFilterToPicker.SelectedDate = null;
        ApplyLedgerFilter();
    }

    /// <summary>
    /// 按筛选控件过滤台账记录（类别 / 处理人工号 / 处理时间闭区间）并渲染总览。
    /// </summary>
    private void ApplyLedgerFilter()
    {
        var category = (LedgerFilterCategoryCombo.SelectedItem as LedgerCategoryFilterOption)?.Category;
        var from = LedgerFilterFromPicker.SelectedDate is { } fromDate
            ? new DateTimeOffset(fromDate.Date)
            : (DateTimeOffset?)null;
        var to = LedgerFilterToPicker.SelectedDate is { } toDate
            ? new DateTimeOffset(toDate.Date.AddDays(1).AddTicks(-1))
            : (DateTimeOffset?)null;

        _ledgerFilteredRecords = LedgerRules.Filter(_ledgerRecords, category, LedgerFilterOperatorBox.Text, from, to);
        RenderLedgerOverview(_ledgerFilteredRecords);
    }

    /// <summary>渲染台账总览：统计文本、机器列表与选中机器的记录列表。</summary>
    /// <param name="records">当前筛选结果。</param>
    private void RenderLedgerOverview(IReadOnlyList<HandlingRecord> records)
    {
        var statView = LedgerViewFactory.BuildStatistics(LedgerRules.BuildStatistics(records));
        LedgerStatText.Text = statView.TotalText;
        LedgerStatCategoryText.Text = statView.CategoryText;
        LedgerStatOperatorText.Text = statView.OperatorText;
        LedgerStatUnresolvedText.Text = statView.UnresolvedText;

        var machines = LedgerRules.GroupByMachine(records)
            .Select(ledger => LedgerViewFactory.BuildMachine(ledger, _ledgerConflictRecordIds))
            .ToList();
        LedgerMachineList.ItemsSource = machines;

        if (machines.Count > 0)
        {
            LedgerMachineList.SelectedIndex = 0;
        }
        else
        {
            LedgerRecordList.ItemsSource = null;
        }
    }

    /// <summary>选中机器变化：把该机器的记录展示到右侧记录列表。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnLedgerMachineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady)
        {
            return;
        }

        LedgerRecordList.ItemsSource = (LedgerMachineList.SelectedItem as LedgerMachineView)?.Records;
    }

    /// <summary>点击“导出…”：按选定范围与格式导出台账或单条记录报告。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnExportLedgerClick(object sender, RoutedEventArgs e)
    {
        if (_ledgerBusy)
        {
            return;
        }

        if (!EnsureUnlocked())
        {
            return;
        }

        var scope = (LedgerExportScopeCombo.SelectedItem as LedgerExportScopeOption)?.Scope ?? LedgerExportScope.All;
        var format = (LedgerExportFormatCombo.SelectedItem as LedgerExportFormatOption)?.Format ?? LedgerExportFormat.Csv;
        var records = ResolveExportRecords(scope);
        if (records.Count == 0)
        {
            LedgerExportStatusText.Text = "所选范围没有可导出的记录。";
            return;
        }

        var reportsRoot = ResolveReportsRoot();
        Directory.CreateDirectory(reportsRoot);

        if (format == LedgerExportFormat.Csv)
        {
            var csvDialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = reportsRoot,
                FileName = $"ledger-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                Filter = "CSV 文件 (*.csv)|*.csv"
            };
            if (csvDialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                File.WriteAllText(csvDialog.FileName, LedgerReportBuilder.BuildCsv(records), new UTF8Encoding(true));
                LedgerExportStatusText.Text = $"已导出 {records.Count} 条台账记录：{csvDialog.FileName}";
                AppendAudit(AuditEventKind.Export, $"已导出台账 {records.Count} 条（CSV）。", csvDialog.FileName, _ledgerSessionId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LedgerExportStatusText.Text = $"导出失败：{ex.Message}";
            }

            return;
        }

        // HTML / Markdown：取范围内最近一条记录的脱敏报告（静默产出固定脱敏）。
        var latest = records.OrderByDescending(record => record.FinishedAt).First();
        var isHtml = format == LedgerExportFormat.Html;
        var content = isHtml
            ? LedgerReportBuilder.BuildHtml(latest, desensitized: true)
            : LedgerReportBuilder.BuildMarkdown(latest, desensitized: true);

        var extension = isHtml ? "html" : "md";
        var reportDialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = reportsRoot,
            FileName = $"{LedgerRules.BuildReportFileName(latest)}.{extension}",
            Filter = isHtml ? "HTML 文件 (*.html)|*.html" : "Markdown 文件 (*.md)|*.md"
        };
        if (reportDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(reportDialog.FileName, content, new UTF8Encoding(true));
            LedgerExportStatusText.Text = $"已导出记录 {latest.RecordId} 的报告：{reportDialog.FileName}";
            AppendAudit(AuditEventKind.Export, $"已导出记录 {latest.RecordId} 的报告（{extension}）。", reportDialog.FileName, latest.SessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LedgerExportStatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>按导出范围取记录集合。</summary>
    /// <param name="scope">导出范围。</param>
    /// <returns>范围内的记录集合。</returns>
    private IReadOnlyList<HandlingRecord> ResolveExportRecords(LedgerExportScope scope) => scope switch
    {
        LedgerExportScope.CurrentFiltered => _ledgerFilteredRecords,
        LedgerExportScope.CurrentMachine => ExportCurrentMachineRecords(),
        _ => _ledgerRecords
    };

    /// <summary>取当前处理机器的全部记录（用于导出“当前处理机器”范围）。</summary>
    /// <returns>当前机器的记录集合；无机器标识时为空集合。</returns>
    private IReadOnlyList<HandlingRecord> ExportCurrentMachineRecords()
    {
        var (machineId, _, _) = ResolveCurrentMachine();
        return string.IsNullOrWhiteSpace(machineId)
            ? Array.Empty<HandlingRecord>()
            : _ledgerRecords.Where(record => string.Equals(record.MachineId, machineId, StringComparison.Ordinal)).ToList();
    }

    /// <summary>解析报告目录（reports 与 data 目录同级）。</summary>
    /// <returns>报告目录绝对路径。</returns>
    private string ResolveReportsRoot()
    {
        var trimmed = Path.TrimEndingDirectorySeparator(_dataDirectory);
        var parent = Path.GetDirectoryName(trimmed);
        var baseDirectory = string.IsNullOrEmpty(parent) ? trimmed : parent;
        return Path.Combine(baseDirectory, "reports");
    }

    /// <summary>点击“刷新列表”：重新读取 reports 目录下的报告文件清单。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnRefreshReportsClick(object sender, RoutedEventArgs e) => RefreshReportList();

    /// <summary>刷新报告查看器的文件清单（按修改时间倒序）。</summary>
    private void RefreshReportList()
    {
        try
        {
            LedgerReportList.ItemsSource = _reportWriter.ListReports(_dataDirectory)
                .Select(LedgerViewFactory.BuildReportFile)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LedgerReportList.ItemsSource = null;
            LedgerExportStatusText.Text = $"报告列表读取失败：{ex.Message}";
        }
    }

    /// <summary>点击“用浏览器打开”：用系统默认程序打开选中的报告文件。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnOpenReportClick(object sender, RoutedEventArgs e)
    {
        if (LedgerReportList.SelectedItem is LedgerReportFileView file)
        {
            OpenWithShell(file.FullPath);
            return;
        }

        MessageBox.Show(this, "请先在报告列表中选择一个报告文件。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>点击“打开所在文件夹”：打开选中报告所在目录（未选中时打开 reports 目录）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnOpenReportFolderClick(object sender, RoutedEventArgs e)
    {
        var directory = LedgerReportList.SelectedItem is LedgerReportFileView file
            ? Path.GetDirectoryName(file.FullPath) ?? ResolveReportsRoot()
            : ResolveReportsRoot();

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"创建报告目录失败：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OpenWithShell(directory);
    }

    /// <summary>以系统外壳打开文件或目录（离线操作，不发起任何网络请求）。</summary>
    /// <param name="path">文件或目录绝对路径。</param>
    private static void OpenWithShell(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show($"无法打开：{ex.Message}", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ═══════════════════ M9 受控自定义命令与动作沉淀（接线） ═══════════════════

    /// <summary>下拉选项：以中文文本展示任意取值（用于沉淀弹窗的枚举与三态选择）。</summary>
    /// <typeparam name="T">取值类型。</typeparam>
    private sealed record ChoiceOption<T>(string Text, T Value)
    {
        /// <summary>返回中文展示文本（供下拉列表直接显示）。</summary>
        /// <returns>中文文本。</returns>
        public override string ToString() => Text;
    }

    /// <summary>按当前运行条件评估自定义命令入口可用性（管理员 + 启动自检 + 已解锁可写 + 高级模式）。</summary>
    /// <returns>可用性判定结果。</returns>
    private CustomCommandAvailability EvaluateCustomAvailability()
        => CustomCommandRules.EvaluateAvailability(
            isAdministrator: _verdict?.IsAdministrator == true,
            advancedModeEnabled: _advancedModeEnabled,
            mediaUnlocked: _mediaUnlocked,
            writable: _mediaUnlocked && _mediaState != MediaState.ReadOnlyOrUnwritable,
            canStart: _verdict?.CanStart == true);

    /// <summary>判断自定义命令当前是否可用（在运行条件之外，还要求“强制记录可用”）。</summary>
    /// <returns>可用返回 true。</returns>
    private bool IsCustomCommandAvailable()
        => EvaluateCustomAvailability().IsAvailable && !_customCommandRecordingDisabled;

    /// <summary>刷新自定义命令页的入口可用性、禁用原因横幅与按钮状态。</summary>
    private void RefreshCustomCommandAvailability()
    {
        var availability = EvaluateCustomAvailability();
        var available = availability.IsAvailable && !_customCommandRecordingDisabled;

        CustomAvailabilityBanner.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        CustomAvailabilityText.Text = available
            ? string.Empty
            : _customCommandRecordingDisabled
                ? "强制记录不可用：命令历史或审计写入失败，已停止后续自定义命令执行（命令必须记录，不提供“不记录执行”的路径）。"
                : availability.Reason ?? "自定义命令当前不可用。";

        CustomRunButton.IsEnabled = available && !_customCommandRunning;
        CustomCommandBox.IsEnabled = available && !_customCommandRunning;
        CustomMultilineCheck.IsEnabled = available && !_customCommandRunning;
        CustomCancelButton.IsEnabled = _customCommandRunning;

        CustomRunHintText.Text = available
            ? "按 Enter（单行模式）或点击“执行命令”后，需在下方确认影响与身份方可执行。"
            : "入口不可用：处理上方提示后方可使用。";
    }

    /// <summary>切换单行 / 多行命令输入模式。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCustomMultilineChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewReady)
        {
            return;
        }

        var multiline = CustomMultilineCheck.IsChecked == true;
        CustomCommandBox.AcceptsReturn = multiline;
        CustomCommandBox.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        CustomCommandBox.Height = multiline ? 92 : 30;
        CustomCommandBox.VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    /// <summary>命令输入框回车：单行模式下按 Enter 直接进入执行前确认。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">键盘事件参数。</param>
    private void OnCustomCommandBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CustomMultilineCheck.IsChecked != true)
        {
            e.Handled = true;
            BeginCustomCommandConfirmation();
        }
    }

    /// <summary>点击“执行命令”：进入执行前的“影响与身份”确认。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnRunCustomCommandClick(object sender, RoutedEventArgs e) => BeginCustomCommandConfirmation();

    /// <summary>
    /// 进入执行前的“影响与身份”确认：校验入口可用性与命令非空，展示完整命令、执行身份与影响提示。
    /// 每次执行都需确认（不提供“本次不再确认”的路径）。
    /// </summary>
    private void BeginCustomCommandConfirmation()
    {
        if (_customCommandRunning)
        {
            return;
        }

        if (!EnsureUnlocked())
        {
            return;
        }

        var availability = EvaluateCustomAvailability();
        if (!availability.IsAvailable || _customCommandRecordingDisabled)
        {
            MessageBox.Show(
                this,
                _customCommandRecordingDisabled
                    ? "强制记录不可用：命令历史或审计写入失败，已停止后续自定义命令执行。"
                    : availability.Reason ?? "自定义命令当前不可用。",
                "DeskIT",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RefreshCustomCommandAvailability();
            return;
        }

        var command = CustomCommandBox.Text.Trim();
        if (command.Length == 0)
        {
            MessageBox.Show(this, "请输入要执行的命令。", "DeskIT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _pendingCustomCommand = command;
        CustomConfirmCommandText.Text = "命令：" + command;
        CustomConfirmIdentityText.Text =
            $"执行身份：{CustomCommandRules.DescribeExecutionAs(_privilege)}"
            + $" · 会话：{(string.IsNullOrWhiteSpace(_ledgerSessionId) ? "未建立会话（不关联会话）" : _ledgerSessionId)}";
        CustomConfirmImpactText.Text =
            "影响提示：该命令将以当前身份在目标机直接执行，可能修改系统状态且影响不可预知；"
            + "执行记录与输出（已脱敏）将强制写入介质并生成审计事件。请确认命令内容无误。";
        CustomConfirmPanel.Visibility = Visibility.Visible;
        CustomRunButton.IsEnabled = false;
        CustomCommandBox.IsEnabled = false;
        CustomMultilineCheck.IsEnabled = false;
    }

    /// <summary>点击“确认执行”：按已确认的命令执行一次自定义命令。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnConfirmCustomCommandClick(object sender, RoutedEventArgs e)
    {
        var command = _pendingCustomCommand;
        if (command is null || _customCommandRunning)
        {
            return;
        }

        _pendingCustomCommand = null;
        HideCustomConfirmPanel();
        await RunCustomCommandAsync(command);
    }

    /// <summary>点击确认区的“取消”：放弃本次执行。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelCustomConfirmClick(object sender, RoutedEventArgs e)
    {
        _pendingCustomCommand = null;
        HideCustomConfirmPanel();
        CustomResultText.Text = "已取消：未执行该命令。";
    }

    /// <summary>隐藏执行前确认区并恢复输入区可用性。</summary>
    private void HideCustomConfirmPanel()
    {
        CustomConfirmPanel.Visibility = Visibility.Collapsed;
        RefreshCustomCommandAvailability();
    }

    /// <summary>点击“中止”：终止正在执行的自定义命令（归类为“已取消”）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelCustomCommandClick(object sender, RoutedEventArgs e)
    {
        _customCommandCancellation?.Cancel();
        CustomResultText.Text = "正在中止：将终止当前命令…";
    }

    /// <summary>
    /// 执行一条受控自定义命令：经执行器运行并采集执行前后的持久化项差异，强制记录（命令历史 + 审计）。
    /// 记录失败时标记“强制记录不可用”并停止后续执行；记录成功时计入本次会话台账（不计入常规动作成功率）。
    /// </summary>
    /// <param name="command">命令原文（未包装）。</param>
    /// <returns>无返回值的异步任务。</returns>
    private async Task RunCustomCommandAsync(string command)
    {
        _customCommandRunning = true;
        _customCommandCancellation?.Dispose();
        _customCommandCancellation = new CancellationTokenSource();
        var token = _customCommandCancellation.Token;

        SetCustomBusy(true);
        CustomResultText.Text = "正在执行命令并采集持久化项基线（服务 / 计划任务 / 自启动项）…";
        CustomPersistenceText.Text = string.Empty;
        CustomOutputText.Text = string.Empty;

        CustomCommandResult result;
        try
        {
            var context = new CustomCommandContext(
                _dataDirectory,
                _ledgerSessionId,
                _privilege,
                _currentOperator,
                Environment.MachineName);

            result = await _customCommandService.ExecuteAsync(command, context, token);
        }
        catch (Exception ex)
        {
            CustomResultText.Text = $"执行自定义命令时发生异常：{ex.Message}";
            _customCommandRunning = false;
            _customCommandCancellation?.Dispose();
            _customCommandCancellation = null;
            SetCustomBusy(false);
            RefreshCustomCommandAvailability();
            return;
        }

        var record = result.Record;
        CustomResultText.Text =
            $"结果：{ActionRules.DescribeOutcome(record.Outcome)} · 退出码 {record.ExitCode}"
            + $" · 耗时 {record.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture)} 秒 · 身份 {record.ExecutedAs}"
            + (record.TimedOut ? " · 已超时终止" : string.Empty);
        CustomOutputText.Text = BuildCustomOutputText(record);
        CustomPersistenceText.Text = CustomCommandViewFactory.DescribePersistence(record.PersistenceChanges);

        if (result.RecordingSucceeded)
        {
            _rawCustomCommands.Add(record);
            _rawPersistenceChanges.AddRange(record.PersistenceChanges);
            _customHistoryLoaded = true;
            ReloadCustomHistoryFromDisk();
        }
        else
        {
            _customCommandRecordingDisabled = true;
            MessageBox.Show(
                this,
                result.RecordingError ?? "强制记录失败：已停止后续自定义命令执行。",
                "DeskIT",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        _customCommandRunning = false;
        _customCommandCancellation?.Dispose();
        _customCommandCancellation = null;
        SetCustomBusy(false);
        RefreshCustomCommandAvailability();
    }

    /// <summary>把命令执行的标准输出与标准错误合并为可展示文本。</summary>
    /// <param name="record">命令执行记录。</param>
    /// <returns>输出文本；无输出时给出提示。</returns>
    private static string BuildCustomOutputText(CustomCommandRecord record)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(record.OutputSummary))
        {
            builder.Append(record.OutputSummary);
        }

        if (!string.IsNullOrWhiteSpace(record.ErrorSummary))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine("[标准错误]");
            builder.Append(record.ErrorSummary);
        }

        return builder.Length == 0 ? "（无输出）" : builder.ToString();
    }

    /// <summary>切换自定义命令忙碌态（执行期间禁用触发与输入，启用中止按钮）。</summary>
    /// <param name="busy">是否忙碌。</param>
    private void SetCustomBusy(bool busy)
    {
        _customCommandRunning = busy;
        var available = IsCustomCommandAvailable();
        CustomRunButton.IsEnabled = !busy && available;
        CustomCancelButton.IsEnabled = busy;
        CustomCommandBox.IsEnabled = !busy && available;
        CustomMultilineCheck.IsEnabled = !busy && available;
    }

    /// <summary>点击“重新加载历史”：从磁盘重读命令历史。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnReloadCustomHistoryClick(object sender, RoutedEventArgs e) => ReloadCustomHistoryFromDisk();

    /// <summary>确保命令历史已加载（首次进入本页时读取 data\custom\commands.db，避免拖慢启动）。</summary>
    private void EnsureCustomHistoryLoaded()
    {
        if (_customHistoryLoaded)
        {
            return;
        }

        ReloadCustomHistoryFromDisk();
        _customHistoryLoaded = true;
    }

    /// <summary>
    /// 从磁盘重读命令历史并刷新列表；读取成功即视为“强制记录可用”（清除记录失败禁用标记，提供恢复路径）。
    /// </summary>
    private void ReloadCustomHistoryFromDisk()
    {
        if (!_mediaUnlocked)
        {
            _customHistory.Clear();
            CustomHistoryStatusText.Text = "介质未解锁：无法读取命令历史。";
            return;
        }

        try
        {
            var records = _customCommandStore.LoadAll(_dataDirectory);
            _customHistory.Clear();
            foreach (var view in CustomCommandViewFactory.BuildHistory(records))
            {
                _customHistory.Add(view);
            }

            _customCommandRecordingDisabled = false;
            CustomHistoryStatusText.Text = records.Count == 0 ? "暂无命令历史。" : $"共 {records.Count} 条命令历史。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaLockedException or MediaIntegrityException)
        {
            CustomHistoryStatusText.Text = $"命令历史读取失败：{ex.Message}";
        }
    }

    /// <summary>点击“另存为修复动作”：弹出沉淀弹窗；保存成功后刷新 M4 动作库使其立即可复用。</summary>
    /// <param name="sender">事件源（其 Tag 为命令历史视图）。</param>
    /// <param name="e">事件参数。</param>
    private void OnSedimentFromHistoryClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked())
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: CustomCommandHistoryView view })
        {
            return;
        }

        if (PromptSediment(view.Record))
        {
            // 沉淀保存成功后刷新 M4 动作库（清空缓存后重新加载 actions 目录）。
            _actionLibrary = null;
            EnsureActionLibraryLoaded();
        }
    }

    /// <summary>
    /// 弹出“另存为修复动作”窗口：填写名称、说明、风险级别、是否需重启、是否可回滚等；
    /// 校验不通过时在窗口内逐条列出字段级问题。保存成功返回 true。
    /// </summary>
    /// <param name="record">命令执行记录（其脱敏命令原文作为源命令）。</param>
    /// <returns>保存成功返回 true；取消或保存失败返回 false。</returns>
    private bool PromptSediment(CustomCommandRecord record)
    {
        var window = new Window
        {
            Title = "另存为修复动作",
            Width = 580,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = "把该命令沉淀为正式修复动作：写入 U 盘 actions 目录（不覆盖既有文件）；保存后返回“修复动作（M4）”页即可见。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
        });

        panel.Children.Add(BuildDialogLabel("源命令（作为动作脚本，不得含 {{ }} 占位符）"));
        panel.Children.Add(new TextBox
        {
            Text = record.CommandText,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0)
        });

        panel.Children.Add(BuildDialogLabel("动作名称（必填）"));
        var nameBox = new TextBox { Height = 28, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(nameBox);

        panel.Children.Add(BuildDialogLabel("动作说明（影响与注意事项，必填）"));
        var descriptionBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 60,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(descriptionBox);

        panel.Children.Add(BuildDialogLabel("风险级别（不得低于“高”，必选）"));
        var riskCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                new ChoiceOption<RiskLevel>("高", RiskLevel.High),
                new ChoiceOption<RiskLevel>("不可逆", RiskLevel.Irreversible)
            },
            SelectedIndex = 0,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(riskCombo);

        panel.Children.Add(BuildDialogLabel("所属问题场景"));
        var sceneCombo = new ComboBox
        {
            ItemsSource = BuildSceneOptions(),
            SelectedIndex = 4,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(sceneCombo);

        panel.Children.Add(BuildDialogLabel("是否需重启生效（必选）"));
        var restartCombo = new ComboBox
        {
            ItemsSource = BuildTriStateOptions(),
            SelectedIndex = 0,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(restartCombo);

        panel.Children.Add(BuildDialogLabel("是否可回滚（必选；选择“可回滚”时须填写回滚说明）"));
        var rollbackCombo = new ComboBox
        {
            ItemsSource = BuildTriStateOptions(),
            SelectedIndex = 0,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(rollbackCombo);

        panel.Children.Add(BuildDialogLabel("回滚说明"));
        var rollbackNoteBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 50,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsEnabled = false,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(rollbackNoteBox);

        panel.Children.Add(BuildDialogLabel("单项执行超时（秒）"));
        var timeoutBox = new TextBox
        {
            Text = ActionRules.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            Height = 28,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(timeoutBox);

        rollbackCombo.SelectionChanged += (_, _) =>
            rollbackNoteBox.IsEnabled = SelectedTriState(rollbackCombo) == true;

        var message = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button { Content = "取消", Width = 90, Height = 30, IsCancel = true };
        var save = new Button
        {
            Content = "保存",
            Width = 110,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        cancel.Click += (_, _) => window.DialogResult = false;
        save.Click += (_, _) =>
        {
            var timeout = int.TryParse(timeoutBox.Text.Trim(), out var parsedTimeout) ? parsedTimeout : 0;

            var request = new ActionSedimentRequest
            {
                SourceCommand = record.CommandText,
                Name = nameBox.Text.Trim(),
                Description = descriptionBox.Text.Trim(),
                Risk = SelectedRisk(riskCombo),
                RequiresRestart = SelectedTriState(restartCombo),
                CanRollback = SelectedTriState(rollbackCombo),
                RollbackNote = rollbackNoteBox.Text.Trim(),
                Scene = SelectedScene(sceneCombo),
                TimeoutSeconds = timeout
            };

            var validation = CustomCommandRules.ValidateSediment(request);
            if (!validation.IsValid)
            {
                message.Text = "请修正以下问题：\n" + string.Join("\n", validation.Problems.Select(problem => "· " + problem));
                message.Visibility = Visibility.Visible;
                return;
            }

            var result = _actionSedimentStore.Save(_programDirectory, request);
            if (!result.Success)
            {
                var details = result.Problems.Count == 0
                    ? result.Message
                    : result.Message + "\n" + string.Join("\n", result.Problems.Select(problem => "· " + problem));
                message.Text = details;
                message.Visibility = Visibility.Visible;
                return;
            }

            MessageBox.Show(this, result.Message, "另存为修复动作", MessageBoxButton.OK, MessageBoxImage.Information);
            window.DialogResult = true;
        };

        window.Content = panel;
        return window.ShowDialog() == true;
    }

    /// <summary>构建沉淀弹窗的字段标题文本块。</summary>
    /// <param name="text">标题文本。</param>
    /// <returns>文本块。</returns>
    private static TextBlock BuildDialogLabel(string text)
        => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51))
        };

    /// <summary>构建问题场景下拉选项（7 个场景，取其中文名）。</summary>
    /// <returns>场景选项列表。</returns>
    private static IReadOnlyList<ChoiceOption<DiagnosisScene>> BuildSceneOptions()
        => new[]
        {
            new ChoiceOption<DiagnosisScene>(DiagnosisRules.DescribeScene(DiagnosisScene.Slowness), DiagnosisScene.Slowness),
            new ChoiceOption<DiagnosisScene>(DiagnosisRules.DescribeScene(DiagnosisScene.Network), DiagnosisScene.Network),
            new ChoiceOption<DiagnosisScene>(DiagnosisRules.DescribeScene(DiagnosisScene.PrintingSharing), DiagnosisScene.PrintingSharing),
            new ChoiceOption<DiagnosisScene>(DiagnosisRules.DescribeScene(DiagnosisScene.DiskStorage), DiagnosisScene.DiskStorage),
            new ChoiceOption<DiagnosisScene>(DiagnosisRules.DescribeScene(DiagnosisScene.StartupSystem), DiagnosisScene.StartupSystem),
            new ChoiceOption<DiagnosisScene>(DiagnosisRules.DescribeScene(DiagnosisScene.Software), DiagnosisScene.Software),
            new ChoiceOption<DiagnosisScene>(DiagnosisRules.DescribeScene(DiagnosisScene.AccountPrivilege), DiagnosisScene.AccountPrivilege)
        };

    /// <summary>构建“是否”三态下拉选项（请选择 / 否 / 是）。</summary>
    /// <returns>三态选项列表。</returns>
    private static IReadOnlyList<ChoiceOption<bool?>> BuildTriStateOptions()
        => new[]
        {
            new ChoiceOption<bool?>("请选择", null),
            new ChoiceOption<bool?>("否", false),
            new ChoiceOption<bool?>("是", true)
        };

    /// <summary>取三态下拉的当前取值（选择“请选择”时为 null）。</summary>
    /// <param name="combo">下拉框。</param>
    /// <returns>三态取值。</returns>
    private static bool? SelectedTriState(ComboBox combo)
        => combo.SelectedItem is ChoiceOption<bool?> option ? option.Value : null;

    /// <summary>取风险级别下拉的当前取值（未选时按“高”处理）。</summary>
    /// <param name="combo">下拉框。</param>
    /// <returns>风险级别。</returns>
    private static RiskLevel SelectedRisk(ComboBox combo)
        => combo.SelectedItem is ChoiceOption<RiskLevel> option ? option.Value : RiskLevel.High;

    /// <summary>取场景下拉的当前取值（未选时按“启动与系统”处理）。</summary>
    /// <param name="combo">下拉框。</param>
    /// <returns>问题场景。</returns>
    private static DiagnosisScene SelectedScene(ComboBox combo)
        => combo.SelectedItem is ChoiceOption<DiagnosisScene> option ? option.Value : DiagnosisScene.StartupSystem;
}