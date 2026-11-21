using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamTranslator.Audio.Capture;
using StreamTranslator.Audio.Segmentation;
using StreamTranslator.App.Runtime;
using StreamTranslator.Core.Clipboard;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Diagnostics;
using StreamTranslator.Core.Subtitles;
using StreamTranslator.Core.Translation;
using StreamTranslator.Core.Worker;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PasswordBox = System.Windows.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace StreamTranslator.App;

public partial class MainWindow : FluentWindow
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyToggleCaption = 1001;
    private const int HotkeyToggleWindow = 1002;
    private const int HotkeyToggleLock = 1003;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly List<AudioDeviceInfo> _audioDevices = [];
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private FloatingSubtitleWindow? _floatingWindow;
    private SubtitleRuntime? _runtime;
    private HwndSource? _hwndSource;
    private bool _isRunning;
    private bool _isTransitioning;
    private bool _isClosing;
    private bool _readyToClose;
    private bool _sessionTranslationEnabled;
    // InitializeComponent 与 ApplySettingsToUi 阶段控件事件会连锁触发，
    // 抑制期内不调度自动保存，避免用默认值覆盖磁盘上的设置。
    private bool _suppressSettingChanges = true;
    private Snackbar? _copyFailureSnackbar;
    private Snackbar? _copySuccessSnackbar;
    private DispatcherTimer? _settingsSaveTimer;
    private DispatcherTimer? _copyStatusTimer;
    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);
    private FloatingWindowBounds? _floatingWindowBounds;
    private readonly TranslationIssueTracker _translationIssueTracker = new();
    private TranslationRuntimeStatus _translationRuntimeStatus = new("已关闭", "已关闭", 0, 0);
    private readonly ObservableCollection<SubtitleItem> _homeLiveMonitorItems = [];
    private const int MaxHomeMonitorItems = 4;
    private bool _syncingQuickControls;
    private bool _hasRuntimeError;
    private bool _hasTranslationIssue;
    private readonly object _activitySnapshotLock = new();
    private SubtitleActivitySnapshot _latestActivitySnapshot = new(SubtitleActivityState.Idle, 0, 0);
    private volatile bool _canRenderHomeLiveMetrics = true;
    private volatile bool _isFloatingWindowVisible;
    private double _pendingAudioLevel;
    private int _audioLevelUpdatePending;
    private long _lastAudioLevelDispatchTick;
    private int _lastDisplayedAudioPercent = -1;
    private string? _lastVoiceActivityBadgeKey;
    private readonly Dictionary<Border, PipelineNodeVisualState> _pipelineNodeStates = new();
    private readonly Action _applyPendingAudioLevelAction;
    private TrayIconManager? _trayIcon;
    private bool _forceExit;
    private bool _isFloatingLocked;
    private bool _testingAsrConnection;

    public MainWindow()
    {
        InitializeComponent();
        _applyPendingAudioLevelAction = ApplyPendingAudioLevel;
        HomeLiveMonitorList.ItemsSource = _homeLiveMonitorItems;
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        StateChanged += OnWindowVisibilityOrStateChanged;
        IsVisibleChanged += OnWindowVisibilityOrStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureDataDirectories();
            _settingsStore = new SettingsStore(Path.Combine(_dataDirectory, "settings.json"));
            _settings = await _settingsStore.LoadAsync();
            ApplySettingsToUi(_settings);
            ApplyThemeMode(_settings.Appearance.Theme);
            LoadAudioDevices(_settings.Audio.DeviceId);
            RegisterHotkeys();
            InitializeTrayIcon();
            ShowPage(HomePage);
            SetActiveNavigationItem("HomePage");
            AboutVersionText.Text = $"V{AppVersion()}";
            DataDirectoryText.Text = _dataDirectory;
            AboutLogsDirectoryText.Text = Path.Combine(_dataDirectory, "logs");
            AboutRuntimeEnvText.Text =
                $"OS       {RuntimeInformation.OSDescription.Trim()}{Environment.NewLine}" +
                $"Runtime  {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";
            UpdateApiKeyGuidance();
            ResetSubtitlePlaceholder();
            await LoadTodaySubtitleHistoryAsync();
            TryShowFloatingWindow();
            App.StartupCompleted = true;
        }
        catch (Exception ex)
        {
            AppendAppLog($"启动初始化失败: {ex.Message}");
            SetRuntimeError(ex.Message);
        }
        finally
        {
            _suppressSettingChanges = false;
            UpdateHomeQuickControlsEnabledState();
            UpdateHomeServiceSummary();
            RefreshPipelineVisualStates();
            UpdateFloatingPreviewStage();
            SyncTrayMenuState();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose)
        {
            return;
        }

        if (_isClosing)
        {
            // Shutdown is still draining; a second Alt+F4 must not close the
            // window early and orphan the worker processes.
            e.Cancel = true;
            return;
        }

        if (!_forceExit && CloseToTraySwitch?.IsChecked == true && _trayIcon is not null)
        {
            e.Cancel = true;
            HideToTray(showBalloon: true);
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        // 覆盖层同时阻断输入并说明正在收尾，避免"窗口卡死"的观感。
        ClosingOverlay.Visibility = Visibility.Visible;
        _settingsSaveTimer?.Stop();

        // Let an in-flight start/stop finish so we do not tear down a runtime
        // that is still wiring itself up. The transition is bounded by the worker
        // health-check timeouts; the extra cap is a safety net.
        var waited = 0;
        while (_isTransitioning && waited < 15000)
        {
            await Task.Delay(100);
            waited += 100;
        }

        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            AppendAppLog($"关闭时保存配置失败: {ex.Message}");
        }

        UnregisterHotkeys();

        try
        {
            await StopRuntimeAsync();
        }
        catch (Exception ex)
        {
            AppendAppLog($"关闭时停止 runtime 失败: {ex.Message}");
        }

        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        StateChanged -= OnWindowVisibilityOrStateChanged;
        IsVisibleChanged -= OnWindowVisibilityOrStateChanged;
        if (Application.Current is not null)
        {
            Application.Current.SessionEnding -= OnSessionEnding;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _floatingWindow?.Close();
        _readyToClose = true;
        Close();
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string pageName })
        {
            return;
        }

        var page = FindName(pageName) as UIElement;
        if (page is not null)
        {
            ShowPage(page);
            if (sender is NavigationViewItem selectedItem)
            {
                SetActiveNavigationItem(selectedItem);
            }
        }
    }

    private async void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        // The global hotkey bypasses StartStopButton.IsEnabled, so reentry during
        // a multi-second start/stop would overwrite _runtime and leak the old
        // capture and worker processes. Block it at the single user entry point.
        if (_isTransitioning || _isClosing)
        {
            return;
        }

        _isTransitioning = true;
        SetStartStopBusy(starting: !_isRunning);
        try
        {
            if (!_isRunning)
            {
                await StartRuntimeAsync();
            }
            else
            {
                await StopRuntimeAsync();
            }
        }
        finally
        {
            _isTransitioning = false;
            ClearStartStopBusy();
        }
    }

    private void SetStartStopBusy(bool starting)
    {
        StartStopProgressRing.Visibility = Visibility.Visible;
        StartStopIcon.Visibility = Visibility.Collapsed;
        StartStopText.Text = starting ? "启动中…" : "停止中…";
        StartStopButton.IsEnabled = false;
        UpdateHomeQuickControlsEnabledState();
        SyncTrayMenuState();
    }

    private void ClearStartStopBusy()
    {
        StartStopProgressRing.Visibility = Visibility.Collapsed;
        StartStopIcon.Visibility = Visibility.Visible;
        StartStopText.Text = _isRunning ? "停止字幕" : "开始字幕";
        StartStopIcon.Symbol = _isRunning ? SymbolRegular.Stop24 : SymbolRegular.Play24;
        StartStopButton.IsEnabled = true;
        UpdateHomeQuickControlsEnabledState();
        SyncTrayMenuState();
    }

    private async Task StartRuntimeAsync()
    {
        AppSettings? runtimeSettings = null;
        try
        {
            await SaveSettingsAsync();
            var validationErrors = AppSettingsValidator.ValidateForStart(_settings);
            if (validationErrors.Count > 0)
            {
                UpdateAsrValidationHint();
                var goToSettings = await ShowConfirmDialogAsync(
                    "无法开始字幕",
                    string.Join(Environment.NewLine, validationErrors),
                    "前往设置",
                    "取消",
                    "StartValidationDialog");
                if (goToSettings)
                {
                    OpenSettingsTab("Services");
                }

                return;
            }

            runtimeSettings = await ResolveRuntimeTranslationSettingsAsync();
            if (runtimeSettings is null)
            {
                return;
            }

            await StartRuntimeCoreAsync(runtimeSettings);
        }
        catch (TranslationStartupException ex) when (runtimeSettings?.Translation.IsEffectivelyEnabled == true)
        {
            await StopRuntimeAsync();
            AppendAppLog($"翻译 worker 启动失败: {ex.InnerException?.Message ?? ex.Message}");
            var sourceOnly = await ShowConfirmDialogAsync(
                "翻译暂不可用",
                $"翻译 worker 启动失败：{ex.InnerException?.Message ?? ex.Message}{Environment.NewLine}{Environment.NewLine}是否仅启动原文字幕？",
                "仅原文启动",
                "返回设置",
                "TranslationUnavailableDialog");
            if (!sourceOnly)
            {
                OpenSettingsTab("Services");
                return;
            }

            try
            {
                await StartRuntimeCoreAsync(runtimeSettings with
                {
                    Translation = runtimeSettings.Translation with { Enabled = false }
                });
            }
            catch (Exception sourceOnlyException)
            {
                StartStopButton.IsEnabled = true;
                await StopRuntimeAsync();
                AppendAppLog($"原文模式启动失败: {sourceOnlyException.Message}");
                SetRuntimeError(sourceOnlyException.Message);
            }
        }
        catch (Exception ex)
        {
            StartStopButton.IsEnabled = true;
            await StopRuntimeAsync();
            AppendAppLog($"字幕 runtime 启动失败: {ex.Message}");
            SetRuntimeError(ex.Message);
        }
    }

    private async Task StartRuntimeCoreAsync(AppSettings runtimeSettings)
    {
        ClearRuntimeError();
        _sessionTranslationEnabled = runtimeSettings.Translation.IsEffectivelyEnabled;
        _translationIssueTracker.MarkSuccess();
        _translationRuntimeStatus = new TranslationRuntimeStatus(
            _sessionTranslationEnabled ? "启动中" : "已关闭",
            _sessionTranslationEnabled ? "等待请求" : "已关闭",
            0,
            0);
        UpdateTranslationIssueUi(TranslationIssueState.Healthy);
        _runtime = new SubtitleRuntime(AppContext.BaseDirectory, _dataDirectory, runtimeSettings);
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _runtime.SubtitleReady += OnSubtitleReady;
        _runtime.SentenceUnitReady += OnSentenceUnitReady;
        _runtime.RuntimeError += OnRuntimeError;
        _runtime.AudioLevelChanged += OnAudioLevelChanged;
        _runtime.VadEndpointChanged += OnVadEndpointChanged;
        _runtime.TranslationReady += OnTranslationReady;
        _runtime.TranslationStatusChanged += OnTranslationStatusChanged;
        _runtime.TranslationTaskStatusChanged += OnTranslationTaskStatusChanged;
        _runtime.ActivityChanged += OnRuntimeActivityChanged;

        AudioStatusText.Text = "等待捕获";
        VadStatusText.Text = "加载中";
        WorkerStatusText.Text = "启动中";
        ApiStatusText.Text = "等待请求";
        SubtitleOutputStatusText.Text = "等待字幕";
        TranslationWorkerStatusText.Text = _sessionTranslationEnabled ? "启动中" : "已关闭";
        TranslationApiStatusText.Text = _sessionTranslationEnabled ? "等待请求" : "已关闭";
        HomeRuntimeSummaryText.Text = "启动中，正在连接音频与识别服务";
        SetStateBadge("启动中");
        UpdateVoiceActivityBadge(new SubtitleActivitySnapshot(SubtitleActivityState.Idle, 0, 0), isStarting: true);
        RefreshPipelineVisualStates();
        StartStopButton.IsEnabled = false;

        await _runtime.StartAsync();

        _isRunning = true;
        TranslationSettingsPanel.IsEnabled = false;
        HardMaxSegmentBox.IsEnabled = false;
        ClearHistoryButton.IsEnabled = false;
        UpdateHomeQuickControlsEnabledState();
        StartStopText.Text = "停止字幕";
        StartStopIcon.Symbol = SymbolRegular.Stop24;
        StartStopButton.IsEnabled = true;
        HomeRuntimeSummaryText.Text = "运行中，正在监听系统输出声音";
        SetStateBadge("运行中");
        UpdateHomeServiceSummary();
        UpdateVoiceActivityBadge(new SubtitleActivitySnapshot(SubtitleActivityState.Idle, 0, 0));
        RefreshPipelineVisualStates();
        AppendAppLog("字幕 runtime 已启动");
        TryShowFloatingWindow();
        _floatingWindow?.SetCaption("等待字幕...");
    }

    private async Task StopRuntimeAsync()
    {
        StartStopButton.IsEnabled = false;
        if (_runtime is not null)
        {
            _runtime.StatusChanged -= OnRuntimeStatusChanged;
            _runtime.SubtitleReady -= OnSubtitleReady;
            _runtime.SentenceUnitReady -= OnSentenceUnitReady;
            _runtime.RuntimeError -= OnRuntimeError;
            _runtime.AudioLevelChanged -= OnAudioLevelChanged;
            _runtime.VadEndpointChanged -= OnVadEndpointChanged;
            _runtime.TranslationReady -= OnTranslationReady;
            _runtime.TranslationStatusChanged -= OnTranslationStatusChanged;
            _runtime.TranslationTaskStatusChanged -= OnTranslationTaskStatusChanged;
            _runtime.ActivityChanged -= OnRuntimeActivityChanged;
            await _runtime.DisposeAsync();
            _runtime = null;
        }
        _floatingWindow?.UpdateActivity(new SubtitleActivitySnapshot(SubtitleActivityState.Idle, 0, 0));

        _isRunning = false;
        _sessionTranslationEnabled = false;
        _translationRuntimeStatus = new TranslationRuntimeStatus(
            _settings.Translation.IsEffectivelyEnabled ? "未启动" : "已关闭",
            _settings.Translation.IsEffectivelyEnabled ? "未测试" : "已关闭",
            0,
            0);
        UpdateTranslationIssueUi(_translationIssueTracker.MarkSuccess());
        AudioStatusText.Text = "未启动";
        VadStatusText.Text = "等待模型";
        WorkerStatusText.Text = "未启动";
        ApiStatusText.Text = "未测试";
        SubtitleOutputStatusText.Text = "等待字幕";
        TranslationWorkerStatusText.Text = _settings.Translation.IsEffectivelyEnabled ? "未启动" : "已关闭";
        TranslationApiStatusText.Text = _settings.Translation.IsEffectivelyEnabled ? "未测试" : "已关闭";
        ResetHomeAudioMeterUi();
        lock (_activitySnapshotLock)
        {
            _latestActivitySnapshot = new SubtitleActivitySnapshot(SubtitleActivityState.Idle, 0, 0);
        }
        HomeRuntimeSummaryText.Text = "未启动，等待开始";
        SetStateBadge("就绪");
        UpdateVoiceActivityBadge(new SubtitleActivitySnapshot(SubtitleActivityState.Idle, 0, 0));
        StartStopText.Text = "开始字幕";
        StartStopIcon.Symbol = SymbolRegular.Play24;
        StartStopButton.IsEnabled = true;
        TranslationSettingsPanel.IsEnabled = true;
        HardMaxSegmentBox.IsEnabled = true;
        ClearHistoryButton.IsEnabled = true;
        UpdateHomeQuickControlsEnabledState();
        UpdateHomeServiceSummary();
        RefreshPipelineVisualStates();
        AdaptiveVadStatusText.Text = "等待运行";
        UpdateVadEndpointModeUi();
        AppendAppLog("字幕 runtime 已停止");
    }

    private void OnShowFloatingWindowClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ToggleFloatingWindowVisibility();
        }
        catch (Exception ex)
        {
            SetRuntimeError(ex.Message);
            AppendAppLog($"悬浮窗操作失败: {ex}");
        }
    }

    private void OnToggleFloatingLockClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowFloatingWindow();
            _floatingWindow?.ToggleLocked();
        }
        catch (Exception ex)
        {
            SetRuntimeError(ex.Message);
            AppendAppLog($"悬浮窗操作失败: {ex}");
        }
    }

    private async void OnCopySelectedClick(object sender, RoutedEventArgs e)
    {
        if (SubtitleList.SelectedItem is SubtitleItem selected)
        {
            await TryCopyTextAsync(FormatSubtitleForCopy(selected), "选中字幕");
        }
    }

    private async void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var builder = new StringBuilder();
        foreach (var item in SubtitleList.Items.OfType<SubtitleItem>().Reverse())
        {
            builder.AppendLine(FormatSubtitleForCopy(item));
            builder.AppendLine();
        }

        await TryCopyTextAsync(builder.ToString(), "当天全部字幕");
    }

    private async void OnCopyRecentClick(object sender, RoutedEventArgs e)
    {
        var recent = SubtitleList.Items
            .OfType<SubtitleItem>()
            .Where(static item => !string.IsNullOrWhiteSpace(item.SourceText))
            .Take(10)
            .Reverse()
            .Select(FormatSubtitleForCopy);
        await TryCopyTextAsync(
            string.Join(Environment.NewLine + Environment.NewLine, recent),
            "最近字幕");
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var confirmed = await ShowConfirmDialogAsync(
            "确认清空历史",
            "清空后会删除当天字幕历史，无法从界面恢复。确定继续吗？",
            "清空",
            "取消",
            "ClearHistoryConfirmDialog",
            ControlAppearance.Danger);
        if (!confirmed)
        {
            return;
        }

        SubtitleList.Items.Clear();
        _homeLiveMonitorItems.Clear();
        UpdateHomeLiveMonitorEmptyState();
        var historyPath = Path.Combine(_dataDirectory, "subtitles", $"{DateTime.Now:yyyy-MM-dd}.jsonl");
        if (File.Exists(historyPath))
        {
            try
            {
                File.WriteAllText(historyPath, string.Empty);
            }
            catch (IOException ex)
            {
                ShowCopyStatus("清空失败，文件被占用", TimeSpan.FromSeconds(5));
                AppendAppLog($"清空历史失败: {ex.Message}");
                return;
            }
        }

        ResetSubtitlePlaceholder();
    }

    /// <summary>统一的确认对话框，替代系统 MessageBox 以保持 Fluent 视觉一致。</summary>
    private async Task<bool> ShowConfirmDialogAsync(
        string title,
        string message,
        string primaryText,
        string closeText,
        string automationId,
        ControlAppearance primaryAppearance = ControlAppearance.Primary)
    {
        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            PrimaryButtonAppearance = primaryAppearance
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string FormatSubtitleForCopy(SubtitleItem item)
    {
        var firstLine = $"{item.GeneratedTimeText}  {item.SourceText}";
        return string.IsNullOrWhiteSpace(item.TranslatedText)
            ? firstLine
            : $"{firstLine}{Environment.NewLine}          {item.TranslatedText}";
    }

    private void ResetSubtitlePlaceholder()
    {
        SubtitleList.Items.Clear();
        SubtitlePlaceholderText.Visibility = Visibility.Visible;
    }

    private void RemoveSubtitlePlaceholder()
    {
        SubtitlePlaceholderText.Visibility = Visibility.Collapsed;
    }

    private void OnOpenDataDirectoryClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(_dataDirectory);
    }

    private void OnOpenLogsDirectoryClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(Path.Combine(_dataDirectory, "logs"));
    }

    private async void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var diagnostics = DiagnosticsReportBuilder.Build(new DiagnosticsSnapshot
        {
            Version = $"V{AppVersion()}",
            OperatingSystem = Environment.OSVersion.ToString(),
            DataDirectory = _dataDirectory,
            AudioStatus = AudioStatusText.Text,
            VadStatus = VadStatusText.Text,
            AsrWorkerStatus = WorkerStatusText.Text,
            AsrApiStatus = ApiStatusText.Text,
            AsrModel = _settings.Asr.ActiveModel,
            AsrLanguage = GetSelectedLanguage(),
            AsrMaxConcurrency = (int)NumberValue(MaxConcurrencyBox, 2),
            TranslationEnabled = _sessionTranslationEnabled,
            TranslationWorkerStatus = _translationRuntimeStatus.WorkerStatus,
            TranslationApiStatus = _translationRuntimeStatus.ServiceStatus,
            TranslationProfile = _settings.Translation.ActiveProfile,
            TranslationTargetLanguage = _settings.Translation.TargetLanguage,
            TranslationQueueLength = _translationRuntimeStatus.QueueLength,
            TranslationQueuePeak = _translationRuntimeStatus.QueuePeak,
            TranslationRecentError = _translationIssueTracker.State.HasIssue
                ? _translationIssueTracker.State.Summary
                : null
        });
        await TryCopyTextAsync(diagnostics, "诊断信息");
    }

    private async Task TryCopyTextAsync(string text, string operation)
    {
        // SetDataObject with minimal internal retries so retry pacing stays under
        // ClipboardWritePolicy's control; Clipboard.SetText would add its own
        // ~1s of internal retries per attempt and delay failure feedback by seconds.
        var result = await ClipboardWritePolicy.TryWriteAsync(
            text,
            static value => System.Windows.Forms.Clipboard.SetDataObject(value, true, 1, 10));
        if (result.Succeeded)
        {
            ShowCopyStatus("已复制", TimeSpan.FromSeconds(3));
            if (SubtitleHistoryPage.Visibility != Visibility.Visible)
            {
                ShowCopySuccessToast(operation);
            }
            return;
        }

        ShowCopyStatus("剪贴板暂时不可用，请稍后重试", TimeSpan.FromSeconds(5));
        ShowCopyFailureToast();
        var error = result.Error;
        AppendAppLog(
            $"{operation}复制失败: {error?.GetType().Name ?? "UnknownException"}: {error?.Message ?? "未知错误"}");
    }

    private void ShowCopyStatus(string text, TimeSpan clearAfter)
    {
        SubtitleHistoryCopyStatusText.Text = text;
        if (_copyStatusTimer is null)
        {
            _copyStatusTimer = new DispatcherTimer();
            _copyStatusTimer.Tick += (_, _) =>
            {
                _copyStatusTimer!.Stop();
                SubtitleHistoryCopyStatusText.Text = "";
            };
        }

        _copyStatusTimer.Stop();
        _copyStatusTimer.Interval = clearAfter;
        _copyStatusTimer.Start();
    }

    private void ShowCopySuccessToast(string operation)
    {
        _copySuccessSnackbar ??= new Snackbar(RootSnackbarPresenter)
        {
            Title = "已复制到剪贴板",
            Appearance = ControlAppearance.Success,
            Icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24),
            Timeout = TimeSpan.FromSeconds(2.5),
            IsCloseButtonEnabled = false
        };
        _copySuccessSnackbar.Content = string.IsNullOrWhiteSpace(operation)
            ? "内容已复制到剪贴板。"
            : $"已复制{operation}。";
        _copySuccessSnackbar.Show();
    }

    private void ShowCopyFailureToast()
    {
        _copyFailureSnackbar ??= new Snackbar(RootSnackbarPresenter)
        {
            Title = "复制失败",
            Content = "暂时无法访问系统剪贴板，请稍后重试。",
            Appearance = ControlAppearance.Caution,
            Icon = new SymbolIcon(SymbolRegular.ClipboardError24),
            Timeout = TimeSpan.FromSeconds(5),
            IsCloseButtonEnabled = true
        };
        AutomationProperties.SetAutomationId(_copyFailureSnackbar, "ClipboardCopyFailureSnackbar");
        _copyFailureSnackbar.Show();
    }

    private void ShowPage(UIElement selectedPage)
    {
        var alreadyVisible = selectedPage.Visibility == Visibility.Visible;
        HomePage.Visibility = Visibility.Collapsed;
        SubtitleHistoryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        selectedPage.Visibility = Visibility.Visible;
        UpdateHomeLiveMetricsVisibility();
        if (!alreadyVisible)
        {
            PlayPageTransition(selectedPage);
        }
    }

    private static void PlayPageTransition(UIElement page)
    {
        var slide = new TranslateTransform(0, 16);
        page.RenderTransform = slide;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, duration) { EasingFunction = easing });
    }

    private void SetActiveNavigationItem(string pageName)
    {
        var selectedItem = NavigationItems()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), pageName, StringComparison.Ordinal));
        if (selectedItem is not null)
        {
            SetActiveNavigationItem(selectedItem);
        }
    }

    private void SetActiveNavigationItem(NavigationViewItem selectedItem)
    {
        foreach (var item in NavigationItems())
        {
            var isActive = ReferenceEquals(item, selectedItem);
            item.IsActive = isActive;
            AutomationProperties.SetItemStatus(item, isActive ? "Active" : "Inactive");
        }
    }

    private IEnumerable<NavigationViewItem> NavigationItems()
    {
        return MainNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .Concat(MainNavigation.FooterMenuItems.OfType<NavigationViewItem>());
    }

    private void OnRuntimeStatusChanged(object? sender, RuntimeStatusUpdate update)
    {
        // BeginInvoke keeps UI exceptions from propagating back into the
        // runtime's background tasks (and from blocking the audio pipeline).
        Dispatcher.BeginInvoke(() =>
        {
            switch (update.Category)
            {
                case RuntimeStatusCategory.AsrApi:
                    ApiStatusText.Text = update.Message;
                    break;
                case RuntimeStatusCategory.AsrWorker:
                    WorkerStatusText.Text = update.Message;
                    break;
                case RuntimeStatusCategory.SpeechDetection:
                    VadStatusText.Text = update.Message;
                    break;
                case RuntimeStatusCategory.AudioCapture:
                    AudioStatusText.Text = update.Message;
                    break;
            }

            RefreshPipelineVisualStates();
            AppendAppLog(update.Message);
        });
    }

    private void OnSubtitleReady(object? sender, SubtitleItem item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Floating window only — history and translation are now driven by
            // OnSentenceUnitReady (via TextSentenceAccumulator in SubtitleRuntime).
            if (!string.IsNullOrWhiteSpace(item.SourceText))
            {
                var translationPending = _sessionTranslationEnabled &&
                    !SourceLanguageDecision.ShouldSkip(
                        _settings.Asr.Language,
                        _settings.Translation.TargetLanguage,
                        item.SourceText);
                _floatingWindow?.PublishSource(item, translationPending);
            }
        });
    }

    private void OnSentenceUnitReady(object? sender, SubtitleItem unit)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var text = unit.SourceText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                RemoveSubtitlePlaceholder();
                if (string.Equals(unit.Type, "subtitle_revision", StringComparison.Ordinal))
                {
                    ApplySubtitleRevision(unit);
                }
                else
                {
                    SubtitleList.Items.Insert(0, unit);
                }

                SyncHomeLiveMonitorFromHistory();
                SubtitleOutputStatusText.Text = "输出中";
                RefreshPipelineVisualStates();
            }
        });
    }

    private void OnTranslationReady(object? sender, TranslationResultUpdate update)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateTranslationIssueUi(_translationIssueTracker.MarkSuccess());
            var match = SubtitleList.Items
                .OfType<SubtitleItem>()
                .Select((item, index) => new { item, index })
                .FirstOrDefault(entry =>
                    string.Equals(entry.item.UtteranceGroupId, update.UtteranceGroupId, StringComparison.Ordinal) &&
                    entry.item.Revision == update.SourceRevision);
            if (match is not null)
            {
                var translated = match.item with { TranslatedText = update.TranslatedText };
                SubtitleList.Items[match.index] = translated;
                SyncHomeLiveMonitorFromHistory();
            }
            _floatingWindow?.ApplyTranslation(update);
            SubtitleOutputStatusText.Text = "双语输出中";
            RefreshPipelineVisualStates();
        });
    }

    private void OnTranslationStatusChanged(object? sender, TranslationRuntimeStatus status)
    {
        _translationRuntimeStatus = status;
        Dispatcher.BeginInvoke(() =>
        {
            TranslationWorkerStatusText.Text = status.WorkerStatus;
            TranslationApiStatusText.Text = status.QueueLength > 0
                ? $"{status.ServiceStatus} · 队列 {status.QueueLength}"
                : status.ServiceStatus;
            RefreshPipelineVisualStates();
        });
    }

    private void OnTranslationTaskStatusChanged(object? sender, TranslationTaskStatusUpdate status)
    {
        var issue = _translationIssueTracker.Apply(status);
        Dispatcher.BeginInvoke(() =>
        {
            UpdateTranslationIssueUi(issue);
            _floatingWindow?.ClearTranslationPending(status);
            RefreshPipelineVisualStates();
        });
    }

    private void SetStateBadge(string text)
    {
        HomeStateBadgeText.Text = text;
        var (foregroundKey, dotKey, trayStatus) = text switch
        {
            "运行中" => ("AcousticLiveBrush", "AcousticLiveBrush", TrayStatus.Running),
            "启动中" => ("AcousticWarmBrush", "AcousticWarmBrush", TrayStatus.Starting),
            "错误" => ("AcousticPeakBrush", "AcousticPeakBrush", TrayStatus.Error),
            _ => ("AccentTextFillColorPrimaryBrush", "AcousticIdleBrush", TrayStatus.Ready)
        };
        HomeStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        HomeStateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
        _trayIcon?.UpdateStatus(trayStatus);
    }

    private void UpdateTranslationIssueUi(TranslationIssueState state)
    {
        _hasTranslationIssue = state.HasIssue;
        TranslationIssueText.Text = state.Summary;
        TranslationIssueText.Visibility = state.HasIssue ? Visibility.Visible : Visibility.Collapsed;
        UpdateHomeIssuesCardVisibility();
    }

    private void SetRuntimeError(string message)
    {
        _hasRuntimeError = !string.IsNullOrWhiteSpace(message) && !string.Equals(message.Trim(), "无", StringComparison.Ordinal);
        LastErrorText.Text = _hasRuntimeError ? message : "无";
        LastErrorText.Visibility = _hasRuntimeError ? Visibility.Visible : Visibility.Collapsed;
        UpdateHomeIssuesCardVisibility();
    }

    private void ClearRuntimeError()
    {
        _hasRuntimeError = false;
        LastErrorText.Text = "无";
        LastErrorText.Visibility = Visibility.Collapsed;
        UpdateHomeIssuesCardVisibility();
    }

    private void UpdateHomeIssuesCardVisibility()
    {
        var show = _hasRuntimeError || _hasTranslationIssue;
        HomeIssuesCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        if (_hasRuntimeError)
        {
            HomeIssuesTitleText.Text = "运行异常";
            HomeIssuesIcon.Symbol = SymbolRegular.ErrorCircle24;
            HomeIssuesIcon.SetResourceReference(Control.ForegroundProperty, "AcousticPeakBrush");
            HomeIssuesCard.SetResourceReference(Border.BackgroundProperty, "AcousticPeakSoftBrush");
            HomeIssuesCard.SetResourceReference(Border.BorderBrushProperty, "AcousticPeakBrush");
        }
        else
        {
            HomeIssuesTitleText.Text = "翻译服务提醒";
            HomeIssuesIcon.Symbol = SymbolRegular.Warning24;
            HomeIssuesIcon.SetResourceReference(Control.ForegroundProperty, "AcousticWarmBrush");
            HomeIssuesCard.SetResourceReference(Border.BackgroundProperty, "AcousticWarmSoftBrush");
            HomeIssuesCard.SetResourceReference(Border.BorderBrushProperty, "AcousticWarmBrush");
        }
    }

    private void OnDismissHomeIssueClick(object sender, RoutedEventArgs e)
    {
        ClearRuntimeError();
        if (_hasTranslationIssue)
        {
            UpdateTranslationIssueUi(_translationIssueTracker.MarkSuccess());
        }

        if (!_isRunning && !_isTransitioning)
        {
            SetStateBadge("就绪");
            HomeRuntimeSummaryText.Text = "未启动，等待开始";
        }

        RefreshPipelineVisualStates();
    }

    private void OnGoToHistoryClick(object sender, RoutedEventArgs e)
    {
        ShowPage(SubtitleHistoryPage);
        SetActiveNavigationItem("SubtitleHistoryPage");
    }

    private async void OnCopyHomeMonitorItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SubtitleItem item })
        {
            e.Handled = true;
            await TryCopyTextAsync(FormatSubtitleForCopy(item), "此条字幕");
        }
    }

    private async void OnCopyHomeMonitorRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SubtitleItem item })
        {
            await TryCopyTextAsync(FormatSubtitleForCopy(item), "此条字幕");
        }
    }

    private void SyncHomeLiveMonitorFromHistory()
    {
        _homeLiveMonitorItems.Clear();
        foreach (var item in SubtitleList.Items.OfType<SubtitleItem>().Take(MaxHomeMonitorItems))
        {
            _homeLiveMonitorItems.Add(item);
        }

        UpdateHomeLiveMonitorEmptyState();
    }

    private void UpdateHomeLiveMonitorEmptyState()
    {
        var hasItems = _homeLiveMonitorItems.Count > 0;
        HomeLiveMonitorEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        HomeLiveMonitorList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        var total = SubtitleList.Items.Count;
        HomeMonitorCountText.Text = total == 0
            ? "等待字幕"
            : total <= MaxHomeMonitorItems
                ? $"共 {total} 条"
                : $"显示最新 {MaxHomeMonitorItems} / 共 {total} 条";
    }

    private void UpdateHomeQuickControlsEnabledState()
    {
        if (HomeAudioDeviceComboBox is null ||
            HomeRefreshAudioDevicesButton is null ||
            HomeTranslationEnabledSwitch is null ||
            HomeTranslationTargetLanguageBox is null ||
            HomeTranslationQuickBar is null)
        {
            return;
        }

        var editable = !_isRunning && !_isTransitioning;
        HomeAudioDeviceComboBox.IsEnabled = editable;
        HomeRefreshAudioDevicesButton.IsEnabled = editable;
        HomeTranslationEnabledSwitch.IsEnabled = editable;
        HomeTranslationTargetLanguageBox.IsEnabled = editable;

        HomeAudioDeviceComboBox.ToolTip = editable
            ? "选择要监听的系统音频输出设备"
            : "运行中暂不可切换输出设备，停止字幕后可调整";
        HomeTranslationQuickBar.ToolTip = editable
            ? "快速开关字幕翻译或切换目标语言"
            : "运行中暂不可变更翻译开关或目标语言，停止字幕后可调整";
    }

    private void UpdateHomeServiceSummary()
    {
        if (HomeActiveServiceSummaryText is null || AsrProviderBox is null || TranslationEnabledSwitch is null)
        {
            return;
        }

        var asrName = GetSelectedAsrProvider() switch
        {
            "Zhipu" => "GLM-ASR-2512",
            "DeepSeek" => "DeepSeek ASR",
            "Custom" => string.IsNullOrWhiteSpace(AsrCustomModelBox?.Text)
                ? "自定义 ASR"
                : AsrCustomModelBox.Text.Trim(),
            _ => "MiMo ASR"
        };

        var targetLanguageLabel = TargetLanguageLabel(GetSelectedTranslationTargetLanguage());
        var activeProfile = SelectedTranslationProfile() ?? _settings.Translation.ActiveProfile;
        var translationSwitchOn = TranslationEnabledSwitch.IsChecked == true;

        string translationSummary;
        if (_isRunning)
        {
            translationSummary = _sessionTranslationEnabled
                ? $"翻译：{activeProfile?.Name ?? "已启用"} → {targetLanguageLabel}"
                : translationSwitchOn ? "翻译：仅原文" : "翻译：已关闭";
        }
        else if (!translationSwitchOn)
        {
            translationSummary = "翻译：已关闭";
        }
        else if (activeProfile is null)
        {
            translationSummary = "翻译：待选择模型";
        }
        else if (!TranslationProfileRules.IsValidated(activeProfile))
        {
            translationSummary = $"翻译：{activeProfile.Name}（未测试）→ {targetLanguageLabel}";
        }
        else
        {
            translationSummary = $"翻译：{activeProfile.Name} → {targetLanguageLabel}";
        }

        HomeActiveServiceSummaryText.Text = $"识别：{asrName} · {translationSummary}";

        if (!_isRunning && !_isTransitioning &&
            TranslationWorkerStatusText is not null &&
            TranslationApiStatusText is not null)
        {
            var hasProfile = activeProfile is not null;
            TranslationWorkerStatusText.Text = !translationSwitchOn
                ? "已关闭"
                : hasProfile ? "未启动" : "待配置";
            TranslationApiStatusText.Text = !translationSwitchOn
                ? "已关闭"
                : hasProfile ? "未测试" : "请选择模型";
        }
    }

    private static string TargetLanguageLabel(string targetLanguage) => targetLanguage.ToLowerInvariant() switch
    {
        "zh-hans" => "中文",
        "en" => "英语",
        "de" => "德语",
        "fr" => "法语",
        "ja" => "日语",
        _ => targetLanguage
    };

    private void UpdateVoiceActivityBadge(SubtitleActivitySnapshot snapshot, bool isStarting = false)
    {
        if (HomeVoiceActivityBadge is null || HomeVoiceActivityDot is null || HomeVoiceActivityText is null)
        {
            return;
        }

        string text;
        string dotKey;
        string foregroundKey;
        string backgroundKey;
        string borderKey;

        if (isStarting)
        {
            text = "启动中";
            dotKey = "AcousticWarmBrush";
            foregroundKey = "AcousticWarmBrush";
            backgroundKey = "AcousticWarmSoftBrush";
            borderKey = "AcousticWarmBrush";
        }
        else if (!_isRunning)
        {
            text = "未监听";
            dotKey = "AcousticIdleBrush";
            foregroundKey = "TextFillColorSecondaryBrush";
            backgroundKey = "SubtleFillColorSecondaryBrush";
            borderKey = "CardStrokeColorDefaultBrush";
        }
        else if (snapshot.State == SubtitleActivityState.ListeningSpeech)
        {
            text = "检测到人声";
            dotKey = "AcousticLiveBrush";
            foregroundKey = "AcousticLiveBrush";
            backgroundKey = "AcousticLiveSoftBrush";
            borderKey = "AcousticLiveBrush";
        }
        else if (snapshot.State == SubtitleActivityState.Processing)
        {
            text = snapshot.ActiveAsrRequests > 1
                ? $"识别中 ({snapshot.ActiveAsrRequests})"
                : "识别中";
            dotKey = "AcousticWarmBrush";
            foregroundKey = "AcousticWarmBrush";
            backgroundKey = "AcousticWarmSoftBrush";
            borderKey = "AcousticWarmBrush";
        }
        else
        {
            text = "静音待机";
            dotKey = "AcousticIdleBrush";
            foregroundKey = "TextFillColorSecondaryBrush";
            backgroundKey = "SubtleFillColorSecondaryBrush";
            borderKey = "CardStrokeColorDefaultBrush";
        }

        var badgeKey = $"{text}|{dotKey}";
        if (string.Equals(_lastVoiceActivityBadgeKey, badgeKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastVoiceActivityBadgeKey = badgeKey;
        HomeVoiceActivityText.Text = text;
        HomeVoiceActivityDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
        HomeVoiceActivityText.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        HomeVoiceActivityBadge.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        HomeVoiceActivityBadge.SetResourceReference(Border.BorderBrushProperty, borderKey);
    }

    private enum PipelineNodeVisualState
    {
        Idle,
        Active,
        BusyOrWarn,
        Error
    }

    private void RefreshPipelineVisualStates()
    {
        if (PipelineNodeAudio is null ||
            PipelineNodeVad is null ||
            PipelineNodeAsr is null ||
            PipelineNodeTranslation is null ||
            PipelineNodeOutput is null)
        {
            return;
        }

        ApplyPipelineNodeState(PipelineNodeAudio, PipelineDotAudio, ClassifyAudioNodeState(AudioStatusText.Text));
        ApplyPipelineNodeState(PipelineNodeVad, PipelineDotVad, ClassifyVadNodeState(VadStatusText.Text));
        ApplyPipelineNodeState(PipelineNodeAsr, PipelineDotAsr, ClassifyAsrNodeState(WorkerStatusText.Text, ApiStatusText.Text));
        ApplyPipelineNodeState(
            PipelineNodeTranslation,
            PipelineDotTranslation,
            ClassifyTranslationNodeState(TranslationWorkerStatusText.Text, TranslationApiStatusText.Text));
        ApplyPipelineNodeState(PipelineNodeOutput, PipelineDotOutput, ClassifyOutputNodeState(SubtitleOutputStatusText.Text));
    }

    private static PipelineNodeVisualState ClassifyAudioNodeState(string status)
    {
        if (ContainsAny(status, "错误", "失败", "异常"))
        {
            return PipelineNodeVisualState.Error;
        }

        if (ContainsAny(status, "等待", "中断", "切换"))
        {
            return PipelineNodeVisualState.BusyOrWarn;
        }

        return ContainsAny(status, "已启动", "已恢复")
            ? PipelineNodeVisualState.Active
            : PipelineNodeVisualState.Idle;
    }

    private static PipelineNodeVisualState ClassifyVadNodeState(string status)
    {
        if (ContainsAny(status, "错误", "失败", "异常"))
        {
            return PipelineNodeVisualState.Error;
        }

        if (ContainsAny(status, "加载中"))
        {
            return PipelineNodeVisualState.BusyOrWarn;
        }

        return ContainsAny(status, "已加载")
            ? PipelineNodeVisualState.Active
            : PipelineNodeVisualState.Idle;
    }

    private static PipelineNodeVisualState ClassifyAsrNodeState(string workerStatus, string apiStatus)
    {
        if (ContainsAny(workerStatus, "错误", "失败", "异常") ||
            ContainsAny(apiStatus, "错误", "失败", "异常"))
        {
            return PipelineNodeVisualState.Error;
        }

        if (ContainsAny(workerStatus, "启动中", "重启中"))
        {
            return PipelineNodeVisualState.BusyOrWarn;
        }

        return ContainsAny(workerStatus, "已启动", "已重启")
            ? PipelineNodeVisualState.Active
            : PipelineNodeVisualState.Idle;
    }

    private PipelineNodeVisualState ClassifyTranslationNodeState(string workerStatus, string apiStatus)
    {
        if (!_isRunning && !_isTransitioning)
        {
            return string.Equals(workerStatus, "待配置", StringComparison.Ordinal)
                ? PipelineNodeVisualState.BusyOrWarn
                : PipelineNodeVisualState.Idle;
        }

        if (!_sessionTranslationEnabled && string.Equals(workerStatus, "已关闭", StringComparison.Ordinal))
        {
            return PipelineNodeVisualState.Idle;
        }

        if (ContainsAny(workerStatus, "异常", "错误", "失败") ||
            ContainsAny(apiStatus, "错误", "失败", "停用"))
        {
            return PipelineNodeVisualState.Error;
        }

        if (_hasTranslationIssue ||
            _translationRuntimeStatus.QueueLength > 0 ||
            ContainsAny(workerStatus, "启动中", "重启中") ||
            ContainsAny(apiStatus, "熔断", "冷却"))
        {
            return PipelineNodeVisualState.BusyOrWarn;
        }

        return ContainsAny(workerStatus, "已启动", "已重启")
            ? PipelineNodeVisualState.Active
            : PipelineNodeVisualState.Idle;
    }

    private PipelineNodeVisualState ClassifyOutputNodeState(string status)
    {
        if (ContainsAny(status, "错误", "失败", "异常"))
        {
            return PipelineNodeVisualState.Error;
        }

        if (_isRunning && ContainsAny(status, "输出中"))
        {
            return PipelineNodeVisualState.Active;
        }

        return _isTransitioning
            ? PipelineNodeVisualState.BusyOrWarn
            : PipelineNodeVisualState.Idle;
    }

    private static bool ContainsAny(string? text, params string[] terms)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var term in terms)
        {
            if (text.Contains(term, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyPipelineNodeState(
        Border node,
        System.Windows.Shapes.Ellipse dot,
        PipelineNodeVisualState state)
    {
        if (_pipelineNodeStates.TryGetValue(node, out var previous) && previous == state)
        {
            return;
        }

        _pipelineNodeStates[node] = state;
        var (dotKey, backgroundKey, borderKey) = state switch
        {
            PipelineNodeVisualState.Active => ("AcousticLiveBrush", "AcousticLiveSoftBrush", "CardStrokeColorDefaultBrush"),
            PipelineNodeVisualState.BusyOrWarn => ("AcousticWarmBrush", "AcousticWarmSoftBrush", "AcousticWarmBrush"),
            PipelineNodeVisualState.Error => ("AcousticPeakBrush", "AcousticPeakSoftBrush", "AcousticPeakBrush"),
            _ => ("AcousticIdleBrush", "SubtleFillColorTransparentBrush", "CardStrokeColorDefaultBrush")
        };

        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
        node.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        node.SetResourceReference(Border.BorderBrushProperty, borderKey);
    }

    private void ApplySubtitleRevision(SubtitleItem revision)
    {
        var indexes = SubtitleList.Items
            .OfType<SubtitleItem>()
            .Select((item, index) => new { item, index })
            .Where(entry => SubtitleRevisionCoordinator.Replaces(revision, entry.item))
            .Select(static entry => entry.index)
            .ToArray();
        // 列表为倒序显示（最新在顶部），未匹配到旧条目时插入到顶部
        var insertIndex = indexes.Length == 0 ? 0 : indexes.Min();
        for (var index = indexes.Length - 1; index >= 0; index--)
        {
            SubtitleList.Items.RemoveAt(indexes[index]);
        }

        SubtitleList.Items.Insert(insertIndex, revision);
    }

    private async Task LoadTodaySubtitleHistoryAsync()
    {
        var store = new SubtitleHistoryStore(Path.Combine(_dataDirectory, "subtitles"));
        var items = await store.LoadLatestAsync(DateOnly.FromDateTime(DateTime.Now));
        if (items.Count == 0)
        {
            return;
        }

        RemoveSubtitlePlaceholder();
        foreach (var item in items)
        {
            SubtitleList.Items.Insert(0, item);
        }

        SyncHomeLiveMonitorFromHistory();
    }

    private void OnRuntimeError(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            SetRuntimeError(ex.Message);
            SetStateBadge("错误");
            HomeRuntimeSummaryText.Text = "运行异常，请查看问题信息";
            RefreshPipelineVisualStates();
            AppendAppLog($"错误: {ex.GetType().Name}: {ex.Message}");

            if (ex is RuntimeFatalException && _isRunning)
            {
                await StopRuntimeAsync();
                SetRuntimeError(ex.Message);
                SetStateBadge("错误");
                HomeRuntimeSummaryText.Text = "字幕已停止，请处理问题后重新开始";
                RefreshPipelineVisualStates();
            }
        });
    }

    private void OnAudioLevelChanged(object? sender, double level)
    {
        Volatile.Write(ref _pendingAudioLevel, level);
        if (!_canRenderHomeLiveMetrics)
        {
            return;
        }

        // 静默归零后不再重复向 UI 线程派发 0% 刷新
        if (level <= 0.5d && Volatile.Read(ref _lastDisplayedAudioPercent) == 0)
        {
            return;
        }

        // 节流至 ~20fps 且合并在途任务，避免低配置机器上音频线程堆满 UI Dispatcher 队列
        var now = Environment.TickCount64;
        if (level > 0.5d && now - Volatile.Read(ref _lastAudioLevelDispatchTick) < 50)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _audioLevelUpdatePending, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _lastAudioLevelDispatchTick, now);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, _applyPendingAudioLevelAction);
    }

    private void ApplyPendingAudioLevel()
    {
        Interlocked.Exchange(ref _audioLevelUpdatePending, 0);
        if (!_canRenderHomeLiveMetrics || !_isRunning)
        {
            return;
        }

        var level = Volatile.Read(ref _pendingAudioLevel);
        AudioLevelMeter.Level = level;
        var percent = level <= 0.5d ? 0 : (int)Math.Round(Math.Clamp(level, 0d, 100d));
        if (percent != Volatile.Read(ref _lastDisplayedAudioPercent))
        {
            Volatile.Write(ref _lastDisplayedAudioPercent, percent);
            AudioLevelBar.Value = percent;
            HomeAudioLevelText.Text = $"{percent}%";
        }
    }

    private void ResetHomeAudioMeterUi()
    {
        Volatile.Write(ref _pendingAudioLevel, 0d);
        Interlocked.Exchange(ref _audioLevelUpdatePending, 0);
        Volatile.Write(ref _lastDisplayedAudioPercent, 0);
        AudioLevelBar.Value = 0;
        AudioLevelMeter.Level = 0;
        AudioLevelMeter.IsSpeechActive = false;
        AudioLevelMeter.ResetPeak();
        HomeAudioLevelText.Text = "0%";
    }

    private void OnWindowVisibilityOrStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized &&
            !_isClosing &&
            MinimizeToTraySwitch?.IsChecked == true &&
            _trayIcon is not null)
        {
            HideToTray(showBalloon: false);
            return;
        }

        UpdateHomeLiveMetricsVisibility();
        SyncTrayMenuState();
    }

    private void OnWindowVisibilityOrStateChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateHomeLiveMetricsVisibility();
        SyncTrayMenuState();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIconManager();
            _trayIcon.ToggleMainWindowRequested += () => Dispatcher.BeginInvoke(ToggleMainWindowFromTray);
            _trayIcon.ToggleStartStopRequested += () => Dispatcher.BeginInvoke(() => OnStartStopClick(this, new RoutedEventArgs()));
            _trayIcon.ToggleFloatingWindowRequested += () => Dispatcher.BeginInvoke(ToggleFloatingWindowVisibility);
            _trayIcon.ToggleFloatingLockRequested += () => Dispatcher.BeginInvoke(() => OnToggleFloatingLockClick(this, new RoutedEventArgs()));
            _trayIcon.ExitRequested += () => Dispatcher.BeginInvoke(() =>
            {
                _forceExit = true;
                ShowFromTray();
                Close();
            });
            if (Application.Current is not null)
            {
                Application.Current.SessionEnding += OnSessionEnding;
            }
        }
        catch (Exception ex)
        {
            AppendAppLog($"系统托盘初始化失败（已降级运行）: {ex.Message}");
            _trayIcon = null;
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _forceExit = true;
    }

    private void ToggleMainWindowFromTray()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            HideToTray(showBalloon: false);
        }
        else
        {
            ShowFromTray();
        }
    }

    private void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        UpdateHomeLiveMetricsVisibility();
        SyncTrayMenuState();
    }

    private void HideToTray(bool showBalloon)
    {
        Hide();
        UpdateHomeLiveMetricsVisibility();
        SyncTrayMenuState();
        if (showBalloon)
        {
            _trayIcon?.ShowMinimizedBalloonOnce();
        }
    }

    private void SyncTrayMenuState()
    {
        _trayIcon?.UpdateMenuState(
            isMainWindowVisible: IsVisible && WindowState != WindowState.Minimized,
            isRunning: _isRunning,
            isTransitioning: _isTransitioning,
            isFloatingVisible: _floatingWindow?.IsVisible == true,
            isFloatingLocked: _isFloatingLocked);
    }

    private void UpdateHomeLiveMetricsVisibility()
    {
        var canRender = IsVisible &&
                        WindowState != WindowState.Minimized &&
                        HomePage?.Visibility == Visibility.Visible;
        var becameVisible = canRender && !_canRenderHomeLiveMetrics;
        _canRenderHomeLiveMetrics = canRender;

        if (!becameVisible)
        {
            return;
        }

        if (!_isRunning)
        {
            ResetHomeAudioMeterUi();
            return;
        }

        SubtitleActivitySnapshot latest;
        lock (_activitySnapshotLock)
        {
            latest = _latestActivitySnapshot;
        }

        AudioLevelMeter.IsSpeechActive = latest.State == SubtitleActivityState.ListeningSpeech;
        UpdateVoiceActivityBadge(latest);
        ApplyPendingAudioLevel();
    }

    private void OnRuntimeActivityChanged(object? sender, SubtitleActivitySnapshot snapshot)
    {
        lock (_activitySnapshotLock)
        {
            _latestActivitySnapshot = snapshot;
        }

        if (!_canRenderHomeLiveMetrics && !_isFloatingWindowVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_canRenderHomeLiveMetrics)
            {
                AudioLevelMeter.IsSpeechActive = snapshot.State == SubtitleActivityState.ListeningSpeech;
                UpdateVoiceActivityBadge(snapshot);
            }

            if (_floatingWindow is { IsVisible: true })
            {
                _floatingWindow.UpdateActivity(snapshot);
            }
        });
    }

    private void OnVadEndpointChanged(object? sender, VadEndpointRuntimeStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var label = VadEndpointModeLabel(status.Mode);
            var suffix = status.IsAdaptive ? "自适应" : "固定";
            CurrentVadEndpointText.Text = $"当前断句等待：{status.EffectiveEndSilenceMs}ms（{label}，{suffix}）";
            HomeVadBadgeText.Text = $"{label} {status.EffectiveEndSilenceMs}ms";
            AdaptiveVadStatusText.Text = status.IsAdaptive
                ? FormatAdaptiveVadStatus(status.Evaluation)
                : "固定值模式，不参与自适应";
        });
    }

    private void ShowFloatingWindow()
    {
        if (_floatingWindow is null)
        {
            _floatingWindow = new FloatingSubtitleWindow();
            _floatingWindow.VisibleGroupsChanged += OnFloatingVisibleGroupsChanged;
            _floatingWindow.BoundsChanged += OnFloatingWindowBoundsChanged;
            _floatingWindow.LockedChanged += OnFloatingWindowLockedChanged;
            _floatingWindow.AppearanceChanged += OnFloatingWindowAppearanceChanged;
            _floatingWindow.HideRequested += OnFloatingWindowHideRequested;
            _floatingWindow.OpenSettingsRequested += OnFloatingWindowOpenSettingsRequested;
            _floatingWindow.ApplyWindowBounds(
                _settings.SubtitleWindow.Left,
                _settings.SubtitleWindow.Top,
                _settings.SubtitleWindow.WindowWidth);
            _floatingWindow.Closed += (closedWindow, _) =>
            {
                if (closedWindow is FloatingSubtitleWindow floatingWindow)
                {
                    floatingWindow.VisibleGroupsChanged -= OnFloatingVisibleGroupsChanged;
                    floatingWindow.BoundsChanged -= OnFloatingWindowBoundsChanged;
                    floatingWindow.LockedChanged -= OnFloatingWindowLockedChanged;
                    floatingWindow.AppearanceChanged -= OnFloatingWindowAppearanceChanged;
                    floatingWindow.HideRequested -= OnFloatingWindowHideRequested;
                    floatingWindow.OpenSettingsRequested -= OnFloatingWindowOpenSettingsRequested;
                }
                _floatingWindow = null;
                UpdateFloatingWindowButtonState();
                UpdateFloatingLockButtonState(false);
            };
        }

        if (!_floatingWindow.IsVisible)
        {
            _floatingWindow.Show();
        }

        if (_floatingWindow.WindowState == WindowState.Minimized)
        {
            _floatingWindow.WindowState = WindowState.Normal;
        }

        _floatingWindow.Activate();

        _floatingWindow.ApplySettings(
            NumberValue(FloatingFontSizeBox, 18),
            (int)NumberValue(FloatingLinesBox, 2),
            FloatingOpacitySlider.Value,
            SelectedFloatingDisplayMode(),
            SelectedFloatingTheme());
        UpdateFloatingWindowButtonState();
    }

    private void OnFloatingVisibleGroupsChanged(IReadOnlyCollection<string> visibleGroupIds)
    {
        _runtime?.UpdateTranslationVisibleGroups(visibleGroupIds);
    }

    private void OnFloatingWindowBoundsChanged(FloatingWindowBounds bounds)
    {
        _floatingWindowBounds = bounds;
        ScheduleSettingsSave();
    }

    private void OnFloatingWindowLockedChanged(bool locked)
    {
        UpdateFloatingLockButtonState(locked);
    }

    private void OnFloatingWindowAppearanceChanged(FloatingAppearanceChange change)
    {
        var previousSuppress = _suppressSettingChanges;
        _suppressSettingChanges = true;
        try
        {
            FloatingFontSizeBox.Value = change.FontSize;
            SelectFloatingDisplayMode(change.DisplayMode);
            SelectFloatingTheme(change.Theme);
        }
        finally
        {
            _suppressSettingChanges = previousSuppress;
        }

        UpdateFloatingPreviewStage();
        ScheduleSettingsSave();
    }

    private void OnFloatingWindowHideRequested()
    {
        if (_floatingWindow is { IsVisible: true })
        {
            _floatingWindow.Hide();
            UpdateFloatingWindowButtonState();
        }
    }

    private void OnFloatingWindowOpenSettingsRequested()
    {
        ShowFromTray();
        OpenSettingsTab("Floating");
    }

    private void UpdateFloatingLockButtonState(bool locked)
    {
        _isFloatingLocked = locked;
        FloatingLockButton.Content = locked ? "解锁悬浮窗" : "锁定悬浮窗";
        FloatingLockButton.Icon = new SymbolIcon(locked ? SymbolRegular.LockOpen24 : SymbolRegular.LockClosed24);
        HomeFloatingLockText.Text = locked ? "已锁定" : "锁定";
        HomeFloatingLockIcon.Symbol = locked ? SymbolRegular.LockOpen24 : SymbolRegular.LockClosed24;
        HomeFloatingLockButton.ToolTip = locked
            ? "悬浮窗已锁定（点击穿透），点击或按 Ctrl+Alt+L 解锁"
            : "锁定悬浮窗（开启鼠标点击穿透，Ctrl+Alt+L）";
        SyncTrayMenuState();
    }

    private void TryShowFloatingWindow()
    {
        try
        {
            ShowFloatingWindow();
        }
        catch (Exception ex)
        {
            SetRuntimeError(ex.Message);
            AppendAppLog($"悬浮窗显示失败: {ex}");
        }
    }

    private void LoadAudioDevices(string selectedDeviceId)
    {
        _syncingQuickControls = true;
        try
        {
            _audioDevices.Clear();
            AudioDeviceComboBox.Items.Clear();
            HomeAudioDeviceComboBox.Items.Clear();
            var devices = _audioDeviceService.GetOutputDevices();
            var selectedIndex = -1;
            var defaultIndex = -1;

            foreach (var device in devices)
            {
                var index = _audioDevices.Count;
                _audioDevices.Add(device);
                var label = $"{device.Name}{(device.IsDefault ? " (默认)" : "")}";
                AudioDeviceComboBox.Items.Add(label);
                HomeAudioDeviceComboBox.Items.Add(label);

                if (device.IsDefault)
                {
                    defaultIndex = index;
                }

                if (string.Equals(device.Id, selectedDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = index;
                }
            }

            if (AudioDeviceComboBox.Items.Count > 0)
            {
                var targetIndex = selectedIndex >= 0 ? selectedIndex : Math.Max(defaultIndex, 0);
                AudioDeviceComboBox.SelectedIndex = targetIndex;
                HomeAudioDeviceComboBox.SelectedIndex = targetIndex;
            }

            UpdateCurrentAudioDeviceText();
        }
        catch (Exception ex)
        {
            SetRuntimeError($"音频设备加载失败: {ex.Message}");
            CurrentAudioDeviceText.Text = "加载失败";
        }
        finally
        {
            _syncingQuickControls = false;
        }
    }

    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingQuickControls)
        {
            return;
        }

        var index = AudioDeviceComboBox.SelectedIndex;
        _syncingQuickControls = true;
        try
        {
            if (index >= 0 && index < HomeAudioDeviceComboBox.Items.Count)
            {
                HomeAudioDeviceComboBox.SelectedIndex = index;
            }
        }
        finally
        {
            _syncingQuickControls = false;
        }

        UpdateCurrentAudioDeviceText();
        ScheduleSettingsSave();
    }

    private void OnHomeAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingQuickControls)
        {
            return;
        }

        var index = HomeAudioDeviceComboBox.SelectedIndex;
        _syncingQuickControls = true;
        try
        {
            if (index >= 0 && index < AudioDeviceComboBox.Items.Count)
            {
                AudioDeviceComboBox.SelectedIndex = index;
            }
        }
        finally
        {
            _syncingQuickControls = false;
        }

        // 在主页主动切到非默认输出设备时，自动关闭“跟随系统默认设备”，避免保存时仍回落到默认设备。
        if (!_suppressSettingChanges &&
            index >= 0 && index < _audioDevices.Count &&
            !_audioDevices[index].IsDefault &&
            FollowDefaultDeviceSwitch.IsChecked == true)
        {
            FollowDefaultDeviceSwitch.IsChecked = false;
        }

        UpdateCurrentAudioDeviceText();
        ScheduleSettingsSave();
    }

    private void OnRefreshAudioDevicesClick(object sender, RoutedEventArgs e)
    {
        LoadAudioDevices(SelectedAudioDeviceId());
    }

    private void OnVadEndpointModeChecked(object sender, RoutedEventArgs e)
    {
        UpdateVadEndpointModeUi();
        ScheduleSettingsSave();
    }

    private void OnFixedEndSilenceValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (FixedVadModeButton?.IsChecked == true)
        {
            UpdateVadEndpointModeUi();
        }

        ScheduleSettingsSave();
    }

    private void OnAnySettingChanged(object sender, RoutedEventArgs e)
    {
        ScheduleSettingsSave();
    }

    private void OnHotkeysEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingChanges)
        {
            return;
        }

        UnregisterHotkeys();
        if (HotkeysEnabledSwitch.IsChecked == true)
        {
            RegisterHotkeys();
        }

        ScheduleSettingsSave();
    }

    private void OnSettingsTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tabTag })
        {
            return;
        }

        ApplySettingsTabVisibility(tabTag);
    }

    private void OpenSettingsTab(string tabTag)
    {
        ShowPage(SettingsPage);
        SetActiveNavigationItem("SettingsPage");

        var tabButton = tabTag switch
        {
            "Audio" => SettingsTabAudioButton,
            "Floating" => SettingsTabFloatingButton,
            "General" => SettingsTabGeneralButton,
            _ => SettingsTabServicesButton
        };

        if (tabButton is not null)
        {
            tabButton.IsChecked = true;
        }

        ApplySettingsTabVisibility(tabTag);
    }

    private void ApplySettingsTabVisibility(string tabTag)
    {
        if (SettingsServicesPanel is null ||
            SettingsAudioPanel is null ||
            SettingsFloatingPanel is null ||
            SettingsGeneralPanel is null)
        {
            return;
        }

        SettingsServicesPanel.Visibility = tabTag == "Services" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAudioPanel.Visibility = tabTag == "Audio" ? Visibility.Visible : Visibility.Collapsed;
        SettingsFloatingPanel.Visibility = tabTag == "Floating" ? Visibility.Visible : Visibility.Collapsed;
        SettingsGeneralPanel.Visibility = tabTag == "General" ? Visibility.Visible : Visibility.Collapsed;

        if (SettingsActiveTabHintText is not null)
        {
            SettingsActiveTabHintText.Text = tabTag switch
            {
                "Audio" => "选择监听输出设备并调节 VAD 断句响应节奏",
                "Floating" => "实时预览并调整悬浮字幕窗的皮肤、排版与透明度",
                "General" => "管理主题外观、系统托盘行为、快捷键与诊断模式",
                _ => "配置语音识别服务与字幕翻译模型"
            };
        }

        if (tabTag == "Floating")
        {
            UpdateFloatingPreviewStage();
        }

        SettingsScrollViewer?.ScrollToHome();
    }

    private void OnNumberSettingChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        ScheduleSettingsSave();
    }

    /// <summary>设置变更后防抖持久化，避免崩溃或断电时丢失未保存的改动。</summary>
    private void ScheduleSettingsSave()
    {
        if (_suppressSettingChanges || _settingsStore is null || _isClosing)
        {
            return;
        }

        if (_settingsSaveTimer is null)
        {
            _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _settingsSaveTimer.Tick += async (_, _) =>
            {
                _settingsSaveTimer!.Stop();
                try
                {
                    await SaveSettingsAsync();
                }
                catch (Exception ex)
                {
                    AppendAppLog($"自动保存设置失败: {ex.Message}");
                }
            };
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void OnFloatingNumberSettingChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        ApplyFloatingSettingsLive();
        ScheduleSettingsSave();
    }

    private void OnFloatingAppearanceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingChanges)
        {
            return;
        }

        ApplyFloatingSettingsLive();
        ScheduleSettingsSave();
    }

    private void OnFloatingOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FloatingOpacityValueText is not null)
        {
            FloatingOpacityValueText.Text = $"{Math.Round(FloatingOpacitySlider.Value * 100)}%";
        }

        ApplyFloatingSettingsLive();
        ScheduleSettingsSave();
    }

    private void ApplyFloatingSettingsLive()
    {
        if (_suppressSettingChanges)
        {
            return;
        }

        UpdateFloatingPreviewStage();
        _floatingWindow?.ApplySettings(
            NumberValue(FloatingFontSizeBox, 18),
            (int)NumberValue(FloatingLinesBox, 2),
            FloatingOpacitySlider.Value,
            SelectedFloatingDisplayMode(),
            SelectedFloatingTheme());
    }

    private void OnPreviewBackdropChecked(object sender, RoutedEventArgs e)
    {
        if (PreviewBackdropDarkVideo is null ||
            PreviewBackdropLightPpt is null ||
            PreviewBackdropWallpaper is null ||
            sender is not RadioButton { Tag: string backdropTag })
        {
            return;
        }

        PreviewBackdropDarkVideo.Visibility = backdropTag == "DarkVideo" ? Visibility.Visible : Visibility.Collapsed;
        PreviewBackdropLightPpt.Visibility = backdropTag == "LightPpt" ? Visibility.Visible : Visibility.Collapsed;
        PreviewBackdropWallpaper.Visibility = backdropTag == "Wallpaper" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFloatingPreviewStage()
    {
        if (PreviewChromeBackgroundBrush is null ||
            PreviewEntryLatest is null ||
            PreviewEntryPrevious is null ||
            PreviewEntryOldest is null ||
            FloatingFontSizeBox is null ||
            FloatingLinesBox is null ||
            FloatingOpacitySlider is null)
        {
            return;
        }

        var fontSize = Math.Clamp(NumberValue(FloatingFontSizeBox, 18), 12d, 72d);
        var maxItems = Math.Clamp((int)NumberValue(FloatingLinesBox, 2), 1, 3);
        var userOpacity = Math.Clamp(FloatingOpacitySlider.Value, 0d, 0.95d);
        var displayMode = SelectedFloatingDisplayMode();
        var theme = SelectedFloatingTheme();

        DropShadowEffect textShadow;
        switch (theme)
        {
            case SubtitleOverlayTheme.HighContrast:
                PreviewChromeBackgroundBrush.Color = Color.FromRgb(0x09, 0x0B, 0x0F);
                PreviewChromeBackgroundBrush.Opacity = Math.Max(userOpacity, 0.86d);
                PreviewChromeBorderTopStop.Color = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
                PreviewChromeBorderBottomStop.Color = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
                PreviewChromeShadowEffect.Opacity = 0.62d;
                PreviewChromeShadowEffect.BlurRadius = 18d;
                textShadow = CreatePreviewTextShadow(6d, 1.5d, 0.95d);
                break;

            case SubtitleOverlayTheme.MinimalFloating:
                PreviewChromeBackgroundBrush.Color = Color.FromRgb(0x0B, 0x10, 0x17);
                PreviewChromeBackgroundBrush.Opacity = Math.Min(userOpacity, 0.22d);
                PreviewChromeBorderTopStop.Color = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
                PreviewChromeBorderBottomStop.Color = Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF);
                PreviewChromeShadowEffect.Opacity = PreviewChromeBackgroundBrush.Opacity > 0.04d ? 0.22d : 0d;
                PreviewChromeShadowEffect.BlurRadius = 10d;
                textShadow = CreatePreviewTextShadow(7d, 1.5d, 1.0d);
                break;

            default:
                PreviewChromeBackgroundBrush.Color = Color.FromRgb(0x0F, 0x14, 0x1B);
                PreviewChromeBackgroundBrush.Opacity = userOpacity;
                PreviewChromeBorderTopStop.Color = Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF);
                PreviewChromeBorderBottomStop.Color = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
                PreviewChromeShadowEffect.Opacity = userOpacity > 0.05d ? 0.46d : 0d;
                PreviewChromeShadowEffect.BlurRadius = 16d;
                textShadow = CreatePreviewTextShadow(5d, 1d, 0.88d);
                break;
        }

        PreviewEntryOldest.Visibility = maxItems >= 3 ? Visibility.Visible : Visibility.Collapsed;
        PreviewEntryPrevious.Visibility = maxItems >= 2 ? Visibility.Visible : Visibility.Collapsed;
        PreviewEntryLatest.Visibility = Visibility.Visible;

        // 按舞台尺寸等比例缩放预览字号，既真实反馈调大/调小变化又不撑爆预览容器
        var previewPrimarySize = Math.Clamp(fontSize * 0.74d, 11d, 24d);
        var previewSecondarySize = Math.Max(10d, previewPrimarySize * 0.84d);
        var whiteBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        var warmBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xF3, 0xC8));
        var softSourceBrush = CreateFrozenBrush(Color.FromArgb(0xB8, 0xE6, 0xEE, 0xF8));
        var softTranslationBrush = CreateFrozenBrush(Color.FromArgb(0xDC, 0xF0, 0xF4, 0xFA));

        ApplyPreviewRowPresentation(
            PreviewOldestPrimaryText,
            PreviewOldestSecondaryText,
            "Let's review the acoustic pipeline first.",
            "我们先快速回顾一下声学识别链路。",
            displayMode,
            previewPrimarySize,
            previewSecondarySize,
            whiteBrush,
            warmBrush,
            softSourceBrush,
            softTranslationBrush,
            textShadow);
        ApplyPreviewRowPresentation(
            PreviewPrevPrimaryText,
            PreviewPrevSecondaryText,
            "Adaptive VAD trims pauses automatically.",
            "自适应断句会根据语速动态调整停顿窗口。",
            displayMode,
            previewPrimarySize,
            previewSecondarySize,
            whiteBrush,
            warmBrush,
            softSourceBrush,
            softTranslationBrush,
            textShadow);
        ApplyPreviewRowPresentation(
            PreviewLatestPrimaryText,
            PreviewLatestSecondaryText,
            "Real-time bilingual captions stay clear on any screen.",
            "双语实时字幕在明暗画面下都能清晰易读。",
            displayMode,
            previewPrimarySize,
            previewSecondarySize,
            whiteBrush,
            warmBrush,
            softSourceBrush,
            softTranslationBrush,
            textShadow);
    }

    private static void ApplyPreviewRowPresentation(
        TextBlock primaryText,
        TextBlock secondaryText,
        string source,
        string translation,
        SubtitleDisplayMode mode,
        double primarySize,
        double secondarySize,
        Brush whiteBrush,
        Brush warmBrush,
        Brush softSourceBrush,
        Brush softTranslationBrush,
        DropShadowEffect shadow)
    {
        primaryText.Effect = shadow;
        secondaryText.Effect = shadow;

        switch (mode)
        {
            case SubtitleDisplayMode.BilingualTranslationFirst:
                primaryText.Text = translation;
                primaryText.FontSize = primarySize;
                primaryText.FontWeight = FontWeights.SemiBold;
                primaryText.Foreground = warmBrush;
                primaryText.Visibility = Visibility.Visible;

                secondaryText.Text = source;
                secondaryText.FontSize = secondarySize;
                secondaryText.FontWeight = FontWeights.Normal;
                secondaryText.Foreground = softSourceBrush;
                secondaryText.Visibility = Visibility.Visible;
                break;

            case SubtitleDisplayMode.TranslationOnly:
                primaryText.Text = translation;
                primaryText.FontSize = primarySize;
                primaryText.FontWeight = FontWeights.SemiBold;
                primaryText.Foreground = warmBrush;
                primaryText.Visibility = Visibility.Visible;
                secondaryText.Visibility = Visibility.Collapsed;
                break;

            case SubtitleDisplayMode.SourceOnly:
                primaryText.Text = source;
                primaryText.FontSize = primarySize;
                primaryText.FontWeight = FontWeights.SemiBold;
                primaryText.Foreground = whiteBrush;
                primaryText.Visibility = Visibility.Visible;
                secondaryText.Visibility = Visibility.Collapsed;
                break;

            default:
                primaryText.Text = source;
                primaryText.FontSize = primarySize;
                primaryText.FontWeight = FontWeights.SemiBold;
                primaryText.Foreground = whiteBrush;
                primaryText.Visibility = Visibility.Visible;

                secondaryText.Text = translation;
                secondaryText.FontSize = secondarySize;
                secondaryText.FontWeight = FontWeights.Normal;
                secondaryText.Foreground = softTranslationBrush;
                secondaryText.Visibility = Visibility.Visible;
                break;
        }
    }

    private static DropShadowEffect CreatePreviewTextShadow(double blurRadius, double shadowDepth, double opacity)
    {
        var effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = blurRadius,
            ShadowDepth = shadowDepth,
            Direction = 270,
            Opacity = opacity
        };
        effect.Freeze();
        return effect;
    }

    private SubtitleDisplayMode SelectedFloatingDisplayMode()
    {
        var tag = (FloatingDisplayModeBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<SubtitleDisplayMode>(tag, out var mode)
            ? mode
            : SubtitleDisplayMode.BilingualSourceFirst;
    }

    private void SelectFloatingDisplayMode(SubtitleDisplayMode mode)
    {
        if (FloatingDisplayModeBox is null)
        {
            return;
        }

        var match = FloatingDisplayModeBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal));
        FloatingDisplayModeBox.SelectedItem = match ?? FloatingDisplayModeBox.Items[0];
    }

    private SubtitleOverlayTheme SelectedFloatingTheme()
    {
        var tag = (FloatingThemeBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<SubtitleOverlayTheme>(tag, out var theme)
            ? theme
            : SubtitleOverlayTheme.CinemaGlass;
    }

    private void SelectFloatingTheme(SubtitleOverlayTheme theme)
    {
        if (FloatingThemeBox is null)
        {
            return;
        }

        var match = FloatingThemeBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), theme.ToString(), StringComparison.Ordinal));
        FloatingThemeBox.SelectedItem = match ?? FloatingThemeBox.Items[0];
    }

    private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingChanges)
        {
            return;
        }

        ApplyThemeMode(SelectedThemeMode());
        ScheduleSettingsSave();
    }

    private void ApplyThemeMode(AppThemeMode mode)
    {
        switch (mode)
        {
            case AppThemeMode.Light:
                SystemThemeWatcher.UnWatch(this);
                ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
                break;
            case AppThemeMode.Dark:
                SystemThemeWatcher.UnWatch(this);
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(false);
                SystemThemeWatcher.Watch(this);
                break;
        }

        ApplyAcousticThemeResources(ApplicationThemeManager.GetAppTheme());
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        Dispatcher.BeginInvoke(() => ApplyAcousticThemeResources(currentApplicationTheme));
    }

    private static void ApplyAcousticThemeResources(ApplicationTheme theme)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var isDark = theme == ApplicationTheme.Dark;
        resources["AcousticLiveBrush"] = CreateFrozenBrush(isDark ? Color.FromRgb(0x2D, 0xD4, 0xA3) : Color.FromRgb(0x0F, 0x9D, 0x76));
        resources["AcousticLiveSoftBrush"] = CreateFrozenBrush(isDark ? Color.FromArgb(0x22, 0x2D, 0xD4, 0xA3) : Color.FromArgb(0x18, 0x0F, 0x9D, 0x76));
        resources["AcousticWarmBrush"] = CreateFrozenBrush(isDark ? Color.FromRgb(0xF5, 0xB0, 0x41) : Color.FromRgb(0xD9, 0x77, 0x06));
        resources["AcousticWarmSoftBrush"] = CreateFrozenBrush(isDark ? Color.FromArgb(0x24, 0xF5, 0xB0, 0x41) : Color.FromArgb(0x1A, 0xD9, 0x77, 0x06));
        resources["AcousticPeakBrush"] = CreateFrozenBrush(isDark ? Color.FromRgb(0xF8, 0x71, 0x71) : Color.FromRgb(0xC4, 0x2B, 0x1C));
        resources["AcousticPeakSoftBrush"] = CreateFrozenBrush(isDark ? Color.FromArgb(0x24, 0xF8, 0x71, 0x71) : Color.FromArgb(0x18, 0xC4, 0x2B, 0x1C));
        resources["AcousticIdleBrush"] = CreateFrozenBrush(isDark ? Color.FromRgb(0x7A, 0x86, 0x99) : Color.FromRgb(0x88, 0x92, 0xA0));
        resources["AcousticMeterTrackBrush"] = CreateFrozenBrush(isDark ? Color.FromArgb(0x33, 0x94, 0xA3, 0xB8) : Color.FromArgb(0x26, 0x80, 0x88, 0x94));
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private AppThemeMode SelectedThemeMode()
    {
        return (ThemeModeComboBox.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "Light" => AppThemeMode.Light,
            "Dark" => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
    }

    private void SelectThemeMode(AppThemeMode mode)
    {
        ThemeModeComboBox.SelectedIndex = mode switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0
        };
    }

    private void OnGoToSettingsClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsTab("Services");
    }

    private void OnAsrCredentialLostFocus(object sender, RoutedEventArgs e)
    {
        UpdateAsrValidationHint();
        UpdateApiKeyGuidance();
        UpdateHomeServiceSummary();
        ScheduleSettingsSave();
    }

    private void OnAsrProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var provider = GetSelectedAsrProvider();
        var isCustom = provider == "Custom";

        AsrCustomBaseUrlPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        AsrCustomModelPanel.Visibility   = isCustom ? Visibility.Visible : Visibility.Collapsed;

        AsrProviderInfoText.Text = provider switch
        {
            "Mimo"     => $"接口  {AsrProviderDefaults.MimoBaseUrl}\n模型  {AsrProviderDefaults.MimoModel}",
            "Zhipu"    => $"接口  {AsrProviderDefaults.ZhipuBaseUrl}\n模型  {AsrProviderDefaults.ZhipuModel}",
            "DeepSeek" => $"接口  {AsrProviderDefaults.DeepSeekAsrEndpoint}\n语种自动识别，单段最长 120 秒、8 MiB",
            _          => ""
        };
        AsrProviderInfoText.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;

        LoadAsrSlotToUi(provider);
        if (AsrTestResultText is not null)
        {
            AsrTestResultText.Text = "";
            AsrTestResultText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        UpdateAsrValidationHint();
        UpdateApiKeyGuidance();
        UpdateHomeServiceSummary();
        ScheduleSettingsSave();
    }

    private async void OnTestAsrConnectionClick(object sender, RoutedEventArgs e)
    {
        if (_testingAsrConnection)
        {
            return;
        }

        UpdateAsrValidationHint();
        var provider = GetSelectedAsrProvider();
        var candidateAsr = BuildCurrentAsrSettings();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(candidateAsr.ActiveApiKey))
        {
            errors.Add("请先填写 API Key。");
        }

        if (provider == "Custom")
        {
            if (!Uri.TryCreate(candidateAsr.Custom.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                errors.Add("自定义 Base URL 必须是有效的 HTTP(S) 地址。");
            }

            if (string.IsNullOrWhiteSpace(candidateAsr.Custom.Model))
            {
                errors.Add("自定义模型名不能为空。");
            }
        }

        if (errors.Count > 0)
        {
            AsrTestResultText.Text = string.Join(" ", errors);
            AsrTestResultText.SetResourceReference(TextBlock.ForegroundProperty, "AcousticPeakBrush");
            return;
        }

        _testingAsrConnection = true;
        TestAsrConnectionButton.IsEnabled = false;
        AsrTestProgressRing.Visibility = Visibility.Visible;
        AsrTestResultText.Text = "正在发送测试音频探测连接...";
        AsrTestResultText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var started = Stopwatch.StartNew();
        try
        {
            var probeTimeoutMs = Math.Min(candidateAsr.TimeoutMs, 15000);
            await using var client = AsrWorkerClientFactory.Create(
                AppContext.BaseDirectory,
                _dataDirectory,
                candidateAsr,
                overrideTimeoutMs: probeTimeoutMs);
            await client.StartAsync();

            var audioBase64 = AsrWorkerClientFactory.CreateConnectionTestAudioBase64();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(probeTimeoutMs));
            var response = await client.TranscribeAsync(
                WorkerRequest.Transcribe(
                    $"asr-test-{Guid.NewGuid():N}",
                    0,
                    0,
                    480,
                    16000,
                    "auto",
                    audioBase64),
                cts.Token);
            await client.ShutdownAsync();
            started.Stop();

            var outcome = AsrResultClassifier.Classify(response);
            if (outcome is SubtitleStatus.Final or SubtitleStatus.NoSpeech)
            {
                var providerLabel = provider switch
                {
                    "Zhipu" => "GLM-ASR-2512",
                    "DeepSeek" => "DeepSeek ASR",
                    "Custom" => candidateAsr.Custom.Model.Trim(),
                    _ => "MiMo ASR"
                };
                AsrTestResultText.Text = $"连接成功 · {started.ElapsedMilliseconds} ms · {providerLabel} 服务响应正常";
                AsrTestResultText.SetResourceReference(TextBlock.ForegroundProperty, "AcousticLiveBrush");
                ScheduleSettingsSave();
            }
            else
            {
                AsrTestResultText.Text =
                    $"失败 · {response.ErrorKind ?? response.ErrorCode ?? "unknown"} · {response.ErrorMessage}";
                AsrTestResultText.SetResourceReference(TextBlock.ForegroundProperty, "AcousticPeakBrush");
            }
        }
        catch (OperationCanceledException)
        {
            AsrTestResultText.Text = "失败 · timeout · 连接测试超时，请检查网络或接口地址。";
            AsrTestResultText.SetResourceReference(TextBlock.ForegroundProperty, "AcousticPeakBrush");
        }
        catch (Exception ex)
        {
            AsrTestResultText.Text = $"失败 · connection · {ex.Message}";
            AsrTestResultText.SetResourceReference(TextBlock.ForegroundProperty, "AcousticPeakBrush");
        }
        finally
        {
            _testingAsrConnection = false;
            AsrTestProgressRing.Visibility = Visibility.Collapsed;
            TestAsrConnectionButton.IsEnabled = true;
        }
    }

    private AsrSettings BuildCurrentAsrSettings()
    {
        var provider = GetSelectedAsrProvider();
        return _settings.Asr with
        {
            ActiveProvider = provider,
            Mimo = provider == "Mimo"
                ? _settings.Asr.Mimo with { ApiKey = AsrApiKeyBox.Password }
                : _settings.Asr.Mimo,
            Zhipu = provider == "Zhipu"
                ? _settings.Asr.Zhipu with { ApiKey = AsrApiKeyBox.Password }
                : _settings.Asr.Zhipu,
            DeepSeek = provider == "DeepSeek"
                ? _settings.Asr.DeepSeek with { ApiKey = AsrApiKeyBox.Password }
                : _settings.Asr.DeepSeek,
            Custom = provider == "Custom"
                ? _settings.Asr.Custom with
                {
                    ApiKey = AsrApiKeyBox.Password,
                    BaseUrl = AsrCustomBaseUrlBox.Text.Trim(),
                    Model = AsrCustomModelBox.Text.Trim()
                }
                : _settings.Asr.Custom,
            Language = "auto",
            TimeoutMs = (int)NumberValue(TimeoutBox, 30000),
            MaxConcurrency = (int)NumberValue(MaxConcurrencyBox, 2)
        };
    }

    private void LoadAsrSlotToUi(string provider)
    {
        AsrApiKeyBox.Password = provider switch
        {
            "Zhipu"    => _settings.Asr.Zhipu.ApiKey,
            "DeepSeek" => _settings.Asr.DeepSeek.ApiKey,
            "Custom"   => _settings.Asr.Custom.ApiKey,
            _          => _settings.Asr.Mimo.ApiKey
        };

        if (provider == "Custom")
        {
            AsrCustomBaseUrlBox.Text = _settings.Asr.Custom.BaseUrl;
            AsrCustomModelBox.Text   = _settings.Asr.Custom.Model;
        }
    }

    private string GetSelectedAsrProvider()
    {
        return (AsrProviderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Mimo";
    }

    private void UpdateAsrValidationHint()
    {
        var messages = new List<string>();
        var provider = GetSelectedAsrProvider();

        if (string.IsNullOrWhiteSpace(AsrApiKeyBox.Password))
            messages.Add("API Key 不能为空。");

        if (provider == "Custom")
        {
            if (!Uri.TryCreate(AsrCustomBaseUrlBox.Text, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                messages.Add("自定义 Base URL 必须是有效的 HTTP(S) 地址。");

            if (string.IsNullOrWhiteSpace(AsrCustomModelBox.Text))
                messages.Add("自定义 Model 不能为空。");
        }

        AsrValidationText.Text = string.Join(" ", messages);
        AsrValidationText.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateApiKeyGuidance()
    {
        ApiKeyGuidanceBar.Visibility = string.IsNullOrWhiteSpace(AsrApiKeyBox.Password)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string AppVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void UpdateCurrentAudioDeviceText()
    {
        if (CurrentAudioDeviceText is null)
        {
            return;
        }

        var selectedIndex = AudioDeviceComboBox.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < AudioDeviceComboBox.Items.Count)
        {
            CurrentAudioDeviceText.Text = AudioDeviceComboBox.Items[selectedIndex]?.ToString() ?? "默认设备";
        }
        else
        {
            CurrentAudioDeviceText.Text = "默认设备";
        }
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        FollowDefaultDeviceSwitch.IsChecked = settings.Audio.FollowDefaultDevice;
        EndSilenceBox.Value = settings.Vad.EndSilenceMs;
        SelectVadEndpointMode(settings.Vad.EndpointMode);
        UpdateVadEndpointModeUi();
        MinSegmentBox.Value = settings.Vad.MinSegmentMs;
        HardMaxSegmentBox.Value = settings.Vad.HardMaxSegmentMs;
        DiagnosticsSwitch.IsChecked = settings.Diagnostics.Enabled;
        AsrProviderBox.SelectedItem = AsrProviderBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(), settings.Asr.ActiveProvider,
                StringComparison.Ordinal))
            ?? AsrProviderBox.Items.OfType<ComboBoxItem>().First();
        // OnAsrProviderChanged fires and loads the API key for the selected slot.
        TimeoutBox.Value = settings.Asr.TimeoutMs;
        MaxConcurrencyBox.Value = settings.Asr.MaxConcurrency;
        FloatingFontSizeBox.Value = settings.SubtitleWindow.FontSize;
        FloatingLinesBox.Value = settings.SubtitleWindow.MaxSubtitleItems;
        FloatingOpacitySlider.Value = settings.SubtitleWindow.Opacity;
        FloatingOpacityValueText.Text = $"{Math.Round(settings.SubtitleWindow.Opacity * 100)}%";
        SelectFloatingDisplayMode(settings.SubtitleWindow.DisplayMode);
        SelectFloatingTheme(settings.SubtitleWindow.Theme);
        HotkeysEnabledSwitch.IsChecked = settings.Hotkeys.Enabled;
        CloseToTraySwitch.IsChecked = settings.Tray.CloseToTray;
        MinimizeToTraySwitch.IsChecked = settings.Tray.MinimizeToTray;
        LanguageBox.Text = "自动检测 (auto)";
        SelectThemeMode(settings.Appearance.Theme);
        _syncingQuickControls = true;
        try
        {
            TranslationEnabledSwitch.IsChecked = settings.Translation.Enabled;
            HomeTranslationEnabledSwitch.IsChecked = settings.Translation.Enabled;
        }
        finally
        {
            _syncingQuickControls = false;
        }
        SelectTranslationTargetLanguage(settings.Translation.TargetLanguage);
        SentenceAccumulationLimitBox.Value = settings.Translation.SentenceAccumulationLimit;
        RefreshTranslationProfileList(settings.Translation.ActiveProfileId);
        UpdateFloatingPreviewStage();
    }

    private async Task SaveSettingsAsync()
    {
        if (_settingsStore is null)
        {
            return;
        }

        // 防抖自动保存与显式保存可能重叠；串行化写入避免 .tmp 文件互相踩踏。
        await _settingsSaveLock.WaitAsync();
        try
        {
            _settings = _settings with
            {
            // Keep the persisted document at the current schema.  Writing an older
            // schema here re-runs the newer ASR migrations on the next startup.
            SchemaVersion = 7,
            Audio = _settings.Audio with
            {
                DeviceId = FollowDefaultDeviceSwitch.IsChecked == true ? "default" : SelectedAudioDeviceId(),
                FollowDefaultDevice = FollowDefaultDeviceSwitch.IsChecked == true
            },
            Vad = _settings.Vad with
            {
                EndpointMode = SelectedVadEndpointMode(),
                EndSilenceMs = (int)NumberValue(EndSilenceBox, 400),
                MinSegmentMs = (int)NumberValue(MinSegmentBox, 900),
                HardMaxSegmentMs = (int)NumberValue(HardMaxSegmentBox, 10000)
            },
            Asr = _settings.Asr with
            {
                ActiveProvider = GetSelectedAsrProvider(),
                Mimo   = GetSelectedAsrProvider() == "Mimo"
                             ? _settings.Asr.Mimo with { ApiKey = AsrApiKeyBox.Password }
                             : _settings.Asr.Mimo,
                Zhipu  = GetSelectedAsrProvider() == "Zhipu"
                             ? _settings.Asr.Zhipu with { ApiKey = AsrApiKeyBox.Password }
                             : _settings.Asr.Zhipu,
                DeepSeek = GetSelectedAsrProvider() == "DeepSeek"
                             ? _settings.Asr.DeepSeek with { ApiKey = AsrApiKeyBox.Password }
                             : _settings.Asr.DeepSeek,
                Custom = GetSelectedAsrProvider() == "Custom"
                             ? _settings.Asr.Custom with
                               {
                                   ApiKey  = AsrApiKeyBox.Password,
                                   BaseUrl = AsrCustomBaseUrlBox.Text,
                                   Model   = AsrCustomModelBox.Text
                               }
                             : _settings.Asr.Custom,
                Language       = "auto",
                TimeoutMs      = (int)NumberValue(TimeoutBox, 30000),
                MaxConcurrency = (int)NumberValue(MaxConcurrencyBox, 2)
            },
            Translation = _settings.Translation with
            {
                Enabled = TranslationEnabledSwitch.IsChecked == true,
                TargetLanguage = GetSelectedTranslationTargetLanguage(),
                ActiveProfileId = SelectedTranslationProfile()?.Id,
                SentenceAccumulationLimit = (int)NumberValue(SentenceAccumulationLimitBox, 350)
            },
            SubtitleWindow = _settings.SubtitleWindow with
            {
                FontSize = NumberValue(FloatingFontSizeBox, 18),
                MaxSubtitleItems = (int)NumberValue(FloatingLinesBox, 2),
                Opacity = FloatingOpacitySlider.Value,
                DisplayMode = SelectedFloatingDisplayMode(),
                Theme = SelectedFloatingTheme(),
                Left = _floatingWindowBounds?.Left ?? _settings.SubtitleWindow.Left,
                Top = _floatingWindowBounds?.Top ?? _settings.SubtitleWindow.Top,
                WindowWidth = _floatingWindowBounds?.Width ?? _settings.SubtitleWindow.WindowWidth
            },
            Diagnostics = _settings.Diagnostics with
            {
                Enabled = DiagnosticsSwitch.IsChecked == true,
                SaveSegmentAudio = DiagnosticsSwitch.IsChecked == true,
                SaveVadTimeline = DiagnosticsSwitch.IsChecked == true
            },
            Hotkeys = _settings.Hotkeys with
            {
                Enabled = HotkeysEnabledSwitch.IsChecked == true
            },
            Appearance = _settings.Appearance with
            {
                Theme = SelectedThemeMode()
            },
            Tray = _settings.Tray with
            {
                CloseToTray = CloseToTraySwitch.IsChecked == true,
                MinimizeToTray = MinimizeToTraySwitch.IsChecked == true
            }
            };

            await _settingsStore.SaveAsync(_settings);
        }
        finally
        {
            _settingsSaveLock.Release();
        }
    }

    private async Task<AppSettings?> ResolveRuntimeTranslationSettingsAsync()
    {
        if (!_settings.Translation.Enabled)
        {
            return _settings;
        }

        var errors = AppSettingsValidator.ValidateTranslation(_settings);
        var profile = _settings.Translation.ActiveProfile;
        var isValidated = profile is not null && TranslationProfileRules.IsValidated(profile);
        if (errors.Count == 0 && isValidated)
        {
            return _settings;
        }

        var reason = errors.Count > 0
            ? string.Join(Environment.NewLine, errors)
            : "当前翻译模型配置尚未通过连接测试。";
        var sourceOnly = await ShowConfirmDialogAsync(
            "翻译暂不可用",
            $"{reason}{Environment.NewLine}{Environment.NewLine}是否仅启动原文字幕？",
            "仅原文启动",
            "返回设置",
            "TranslationUnavailableDialog");
        if (sourceOnly)
        {
            return _settings with
            {
                Translation = _settings.Translation with { Enabled = false }
            };
        }

        OpenSettingsTab("Services");
        return null;
    }

    private void OnTranslationSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingQuickControls)
        {
            return;
        }

        _syncingQuickControls = true;
        try
        {
            if (HomeTranslationEnabledSwitch is not null && TranslationEnabledSwitch is not null)
            {
                HomeTranslationEnabledSwitch.IsChecked = TranslationEnabledSwitch.IsChecked;
            }

            if (HomeTranslationTargetLanguageBox is not null && TranslationTargetLanguageBox is not null)
            {
                SelectComboBoxItemByTag(HomeTranslationTargetLanguageBox, GetSelectedTranslationTargetLanguage());
            }
        }
        finally
        {
            _syncingQuickControls = false;
        }

        if (TranslationSelectionSummaryText is not null)
        {
            UpdateTranslationProfileSelectionUi();
        }
        else
        {
            UpdateHomeServiceSummary();
            RefreshPipelineVisualStates();
        }

        ScheduleSettingsSave();
    }

    private void OnHomeTranslationEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingQuickControls)
        {
            return;
        }

        _syncingQuickControls = true;
        try
        {
            if (TranslationEnabledSwitch is not null)
            {
                TranslationEnabledSwitch.IsChecked = HomeTranslationEnabledSwitch.IsChecked;
            }
        }
        finally
        {
            _syncingQuickControls = false;
        }

        if (TranslationSelectionSummaryText is not null)
        {
            UpdateTranslationProfileSelectionUi();
        }
        else
        {
            UpdateHomeServiceSummary();
            RefreshPipelineVisualStates();
        }

        ScheduleSettingsSave();
    }

    private void OnHomeTranslationTargetLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingQuickControls)
        {
            return;
        }

        var targetLanguage = (HomeTranslationTargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-Hans";
        _syncingQuickControls = true;
        try
        {
            if (TranslationTargetLanguageBox is not null)
            {
                SelectComboBoxItemByTag(TranslationTargetLanguageBox, targetLanguage);
            }
        }
        finally
        {
            _syncingQuickControls = false;
        }

        UpdateHomeServiceSummary();
        ScheduleSettingsSave();
    }

    private void OnTranslationProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranslationProfileList.SelectedItem is TranslationProfileDisplayItem selected)
        {
            _settings = _settings with
            {
                Translation = _settings.Translation with { ActiveProfileId = selected.Id }
            };
        }
        UpdateTranslationProfileSelectionUi();
    }

    private async void OnAddTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        var profile = await ShowTranslationProfileEditorAsync(null);
        if (profile is null)
        {
            return;
        }
        var profiles = _settings.Translation.Profiles.ToList();
        profiles.Add(profile);
        _settings = _settings with
        {
            Translation = _settings.Translation with
            {
                Profiles = profiles,
                ActiveProfileId = profile.Id
            }
        };
        RefreshTranslationProfileList(profile.Id);
        await SaveSettingsAsync();
    }

    private async void OnEditTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTranslationProfile();
        if (selected is not null)
        {
            await EditTranslationProfileAsync(selected);
        }
    }

    private async void OnInlineEditTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ResolveProfileFromSender(sender) is { } profile)
        {
            SelectTranslationProfileById(profile.Id);
            await EditTranslationProfileAsync(profile);
        }
    }

    private async Task EditTranslationProfileAsync(TranslationProfile selected)
    {
        var edited = await ShowTranslationProfileEditorAsync(selected);
        if (edited is null)
        {
            return;
        }
        ReplaceTranslationProfile(edited);
        RefreshTranslationProfileList(edited.Id);
        await SaveSettingsAsync();
    }

    private async void OnCopyTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTranslationProfile();
        if (selected is null)
        {
            return;
        }
        var copy = selected with
        {
            Id = Guid.NewGuid(),
            Name = $"{selected.Name} 副本",
            ValidationFingerprint = null,
            LastValidatedAt = null,
            LastValidationLatencyMs = null
        };
        var profiles = _settings.Translation.Profiles.ToList();
        profiles.Add(copy);
        _settings = _settings with
        {
            Translation = _settings.Translation with
            {
                Profiles = profiles,
                ActiveProfileId = copy.Id
            }
        };
        RefreshTranslationProfileList(copy.Id);
        await SaveSettingsAsync();
    }

    private async void OnDeleteTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTranslationProfile();
        if (selected is not null)
        {
            await DeleteTranslationProfileAsync(selected);
        }
    }

    private async void OnInlineDeleteTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ResolveProfileFromSender(sender) is { } profile)
        {
            await DeleteTranslationProfileAsync(profile);
        }
    }

    private async Task DeleteTranslationProfileAsync(TranslationProfile selected)
    {
        var confirmed = await ShowConfirmDialogAsync(
            "删除翻译配置",
            $"确定删除模型配置“{selected.Name}”吗？",
            "删除",
            "取消",
            "DeleteTranslationProfileDialog",
            ControlAppearance.Danger);
        if (!confirmed)
        {
            return;
        }
        var profiles = _settings.Translation.Profiles.Where(profile => profile.Id != selected.Id).ToList();
        var nextActiveId = _settings.Translation.ActiveProfileId == selected.Id
            ? null
            : _settings.Translation.ActiveProfileId;
        _settings = _settings with
        {
            Translation = _settings.Translation with
            {
                Profiles = profiles,
                ActiveProfileId = nextActiveId
            }
        };
        RefreshTranslationProfileList(nextActiveId);
        await SaveSettingsAsync();
    }

    private async void OnTestTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        var profile = SelectedTranslationProfile();
        if (profile is not null)
        {
            await TestTranslationProfileAsync(profile);
        }
    }

    private async void OnInlineTestTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ResolveProfileFromSender(sender) is { } profile)
        {
            SelectTranslationProfileById(profile.Id);
            await TestTranslationProfileAsync(profile);
        }
    }

    private TranslationProfile? ResolveProfileFromSender(object sender)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            return _settings.Translation.Profiles.FirstOrDefault(profile => profile.Id == id);
        }

        return null;
    }

    private void SelectTranslationProfileById(Guid id)
    {
        if (TranslationProfileList.ItemsSource is IEnumerable<TranslationProfileDisplayItem> items)
        {
            var match = items.FirstOrDefault(item => item.Id == id);
            if (match is not null)
            {
                TranslationProfileList.SelectedItem = match;
            }
        }
    }

    private async Task TestTranslationProfileAsync(TranslationProfile profile)
    {
        var errors = TranslationProfileRules.Validate(profile);
        if (errors.Count > 0)
        {
            TranslationTestResultText.Text = string.Join(Environment.NewLine, errors);
            return;
        }

        TestTranslationProfileButton.IsEnabled = false;
        TranslationTestProgressRing.Visibility = Visibility.Visible;
        TranslationTestResultText.Text = "正在发送测试翻译...";
        var sourceText = GetSelectedTranslationTargetLanguage() == "en"
            ? "直播将在9:30开始。"
            : "The live stream starts at 9:30.";
        var sourceLanguage = GetSelectedTranslationTargetLanguage() == "en" ? "zh" : "en";
        var started = Stopwatch.StartNew();
        try
        {
            await using var client = TranslationWorkerClientFactory.Create(AppContext.BaseDirectory, _dataDirectory);
            await client.StartAsync(profile);
            var response = await client.TranslateAsync(TranslationWorkerRequest.Translate(
                $"test-{Guid.NewGuid():N}",
                0,
                "connection-test",
                1,
                sourceLanguage,
                GetSelectedTranslationTargetLanguage(),
                sourceText,
                [],
                DateTimeOffset.Now,
                isConnectionTest: true));
            await client.ShutdownAsync();
            started.Stop();

            if (!response.Ok || string.IsNullOrWhiteSpace(response.TranslatedText))
            {
                TranslationTestResultText.Text =
                    $"失败 · {response.ErrorKind ?? response.ErrorCode ?? "unknown"} · {response.ErrorMessage}";
                return;
            }

            var validated = profile with
            {
                ValidationFingerprint = TranslationProfileRules.CreateValidationFingerprint(profile),
                LastValidatedAt = DateTimeOffset.Now,
                LastValidationLatencyMs = (int)started.ElapsedMilliseconds
            };
            ReplaceTranslationProfile(validated);
            RefreshTranslationProfileList(validated.Id);
            var warnings = response.WarningCodes.Length == 0
                ? ""
                : $" · 警告: {string.Join(", ", response.WarningCodes)}";
            TranslationTestResultText.Text =
                $"成功 · {started.ElapsedMilliseconds} ms · {sourceText} → {response.TranslatedText}{warnings}";
            await SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            TranslationTestResultText.Text = $"失败 · connection · {ex.Message}";
        }
        finally
        {
            TranslationTestProgressRing.Visibility = Visibility.Collapsed;
            TestTranslationProfileButton.IsEnabled = SelectedTranslationProfile() is not null;
        }
    }

    private async Task<TranslationProfile?> ShowTranslationProfileEditorAsync(TranslationProfile? existing)
    {
        var nameBox = new Wpf.Ui.Controls.TextBox
        {
            Text = existing?.Name ?? "",
            PlaceholderText = "例如：DeepSeek / 本地 Ollama",
            ClearButtonEnabled = true
        };
        var locationBox = new ComboBox
        {
            VerticalContentAlignment = VerticalAlignment.Center
        };
        locationBox.Items.Add(new ComboBoxItem { Content = "本地服务", Tag = TranslationServiceLocation.Local });
        locationBox.Items.Add(new ComboBoxItem { Content = "远程服务", Tag = TranslationServiceLocation.Remote });
        locationBox.SelectedIndex = existing?.Location == TranslationServiceLocation.Local ? 0 : 1;

        var baseUrlBox = new Wpf.Ui.Controls.TextBox
        {
            Text = existing?.BaseUrl ?? "",
            PlaceholderText = "https://api.deepseek.com/v1 或 http://127.0.0.1:11434/v1",
            ClearButtonEnabled = true
        };
        var endpointDot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var endpointPreview = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        endpointPreview.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var endpointPreviewPanel = new DockPanel();
        DockPanel.SetDock(endpointDot, Dock.Left);
        endpointPreviewPanel.Children.Add(endpointDot);
        endpointPreviewPanel.Children.Add(endpointPreview);
        var endpointChip = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 6, 0, 0),
            Child = endpointPreviewPanel
        };
        endpointChip.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        endpointChip.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var modelBox = new Wpf.Ui.Controls.TextBox
        {
            Text = existing?.Model ?? "",
            PlaceholderText = "例如：deepseek-chat / qwen2.5",
            ClearButtonEnabled = true
        };
        var apiKeyBox = new Wpf.Ui.Controls.PasswordBox
        {
            Password = existing?.ApiKey ?? "",
            PlaceholderText = "本地服务如无需鉴权可留空"
        };

        var compatibilityBox = new ComboBox
        {
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (var compatibility in Enum.GetValues<TranslationRequestCompatibility>())
        {
            compatibilityBox.Items.Add(new ComboBoxItem { Content = CompatibilityLabel(compatibility), Tag = compatibility });
        }
        compatibilityBox.SelectedIndex = Math.Max(0, Array.IndexOf(
            Enum.GetValues<TranslationRequestCompatibility>(),
            existing?.RequestCompatibility ?? TranslationRequestCompatibility.Standard));

        var timeoutBox = new NumberBox
        {
            Value = existing?.TimeoutMs ?? 10000,
            Minimum = 3000,
            Maximum = 30000,
            SmallChange = 1000
        };
        var concurrencyBox = new NumberBox
        {
            Value = existing?.MaxConcurrency ?? 2,
            Minimum = 1,
            Maximum = 4,
            SmallChange = 1
        };
        var customExtraBodyBox = new Wpf.Ui.Controls.TextBox
        {
            Text = existing?.CustomExtraBody.GetRawText() ?? "{}",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            MinHeight = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var validationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        validationText.SetResourceReference(TextBlock.ForegroundProperty, "AcousticPeakBrush");
        var validationBanner = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = validationText
        };
        validationBanner.SetResourceReference(Border.BackgroundProperty, "AcousticPeakSoftBrush");
        validationBanner.SetResourceReference(Border.BorderBrushProperty, "AcousticPeakBrush");

        void ShowValidationError(string message)
        {
            validationText.Text = message;
            validationBanner.Visibility = Visibility.Visible;
        }

        var advancedPanel = new StackPanel();
        advancedPanel.Children.Add(CreateField("请求兼容", compatibilityBox));
        var limits = new Grid();
        limits.ColumnDefinitions.Add(new ColumnDefinition());
        limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        limits.ColumnDefinitions.Add(new ColumnDefinition());
        var timeoutField = CreateField("超时 (ms)", timeoutBox);
        var concurrencyField = CreateField("最大并发", concurrencyBox);
        Grid.SetColumn(concurrencyField, 2);
        limits.Children.Add(timeoutField);
        limits.Children.Add(concurrencyField);
        advancedPanel.Children.Add(limits);
        var customField = CreateField("Custom extraBody (JSON)", customExtraBodyBox);
        advancedPanel.Children.Add(customField);

        void UpdateCompatibilityVisibility()
        {
            customField.Visibility = SelectedEnum<TranslationRequestCompatibility>(compatibilityBox) ==
                                     TranslationRequestCompatibility.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        compatibilityBox.SelectionChanged += (_, _) => UpdateCompatibilityVisibility();
        UpdateCompatibilityVisibility();

        void UpdateEndpointPreview()
        {
            // 使用无异常分支解析，避免逐字输入时每次按键抛异常造成卡顿
            if (TranslationProfileRules.TryBuildFinalEndpoint(baseUrlBox.Text, out var finalEndpoint))
            {
                endpointPreview.Text = $"最终请求  {finalEndpoint}";
                endpointChip.ToolTip = finalEndpoint;
                endpointDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AcousticLiveBrush");
            }
            else
            {
                endpointPreview.Text = "最终请求  等待输入有效的 HTTP(S) Base URL";
                endpointChip.ToolTip = null;
                endpointDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AcousticIdleBrush");
            }
        }
        baseUrlBox.TextChanged += (_, _) => UpdateEndpointPreview();
        baseUrlBox.LostKeyboardFocus += (_, _) =>
        {
            if (existing is null)
            {
                locationBox.SelectedIndex = TranslationProfileRules.SuggestLocation(baseUrlBox.Text) ==
                                            TranslationServiceLocation.Local ? 0 : 1;
            }
        };
        UpdateEndpointPreview();

        var form = new StackPanel
        {
            Width = 512,
            Margin = new Thickness(0, 2, 4, 2)
        };

        // 顶部紧凑协议说明条（保留 TranslationProtocolNotice 契约）
        var protocolIcon = new SymbolIcon(SymbolRegular.Info20)
        {
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        protocolIcon.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var protocolNotice = new TextBlock
        {
            Text = "接口协议：兼容 OpenAI Chat Completions API",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        protocolNotice.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAutomationId(protocolNotice, "TranslationProtocolNotice");
        var protocolRow = new StackPanel { Orientation = Orientation.Horizontal };
        protocolRow.Children.Add(protocolIcon);
        protocolRow.Children.Add(protocolNotice);
        var protocolBanner = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 14),
            Child = protocolRow
        };
        protocolBanner.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        protocolBanner.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        form.Children.Add(protocolBanner);

        // 第一排：配置名称 + 服务位置（双栏更紧凑清晰）
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nameField = CreateField("配置名称", nameBox);
        var locationField = CreateField("服务位置", locationBox);
        Grid.SetColumn(locationField, 2);
        topRow.Children.Add(nameField);
        topRow.Children.Add(locationField);
        form.Children.Add(topRow);

        // 第二排：Base URL + 实时端点预览条
        var baseUrlField = CreateField("Base URL", baseUrlBox);
        baseUrlField.Children.Add(endpointChip);
        form.Children.Add(baseUrlField);

        // 第三排：模型名称 + API Key（双栏）
        var modelKeyRow = new Grid();
        modelKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        modelKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var modelField = CreateField("模型名称", modelBox);
        var apiKeyField = CreateField("API Key", apiKeyBox);
        Grid.SetColumn(apiKeyField, 2);
        modelKeyRow.Children.Add(modelField);
        modelKeyRow.Children.Add(apiKeyField);
        form.Children.Add(modelKeyRow);

        // 第四排：Fluent CardExpander 高级设置（继承 Expander，兼容 ExpandCollapsePattern）
        var advancedHeader = new StackPanel();
        var advancedHeaderTitle = new TextBlock { Text = "高级设置", FontSize = 13, FontWeight = FontWeights.Medium };
        advancedHeaderTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var advancedHeaderHint = new TextBlock
        {
            Text = "请求兼容模板、超时与最大并发",
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 0)
        };
        advancedHeaderHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        advancedHeader.Children.Add(advancedHeaderTitle);
        advancedHeader.Children.Add(advancedHeaderHint);

        var advancedExpander = new CardExpander
        {
            Header = advancedHeader,
            Icon = new SymbolIcon(SymbolRegular.Options24),
            ContentPadding = new Thickness(14, 12, 14, 4),
            Content = advancedPanel,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(advancedExpander, "TranslationAdvancedSettingsExpander");
        form.Children.Add(advancedExpander);
        form.Children.Add(validationBanner);

        TranslationProfile? candidate = null;
        var dialogScrollViewer = new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = form
        };
        dialogScrollViewer.PreviewMouseWheel += (_, args) =>
        {
            if (FindScrollableAncestor(args.OriginalSource as DependencyObject, dialogScrollViewer, args.Delta) is not null)
            {
                return;
            }

            if (dialogScrollViewer.ScrollableHeight <= 0)
            {
                return;
            }

            var step = -(args.Delta / 120d) * 48d;
            dialogScrollViewer.ScrollToVerticalOffset(
                Math.Clamp(dialogScrollViewer.VerticalOffset + step, 0, dialogScrollViewer.ScrollableHeight));
            args.Handled = true;
        };
        AutomationProperties.SetAutomationId(dialogScrollViewer, "TranslationProfileDialogScrollViewer");

        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = existing is null ? "添加翻译模型" : "编辑翻译模型",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            DialogWidth = 588,
            DialogMaxHeight = 620,
            Content = dialogScrollViewer
        };
        // 去掉 ContentDialog 模板包裹整块交互区的大面积 DropShadowEffect，
        // 消除输入与滚动时每帧重算 600x600 模糊导致的卡顿。
        dialog.Loaded += (_, _) => StripHeavyDialogEffects(dialog, dialogScrollViewer);
        // ContentDialog's template has its own ScrollViewer; only the editor content should scroll.
        ScrollViewer.SetVerticalScrollBarVisibility(dialog, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(dialog, ScrollBarVisibility.Disabled);
        dialog.Closing += (_, args) =>
        {
            if (args.Result != ContentDialogResult.Primary)
            {
                return;
            }
            JsonElement customBody;
            try
            {
                customBody = JsonDocument.Parse(customExtraBodyBox.Text).RootElement.Clone();
            }
            catch (JsonException ex)
            {
                ShowValidationError($"extraBody JSON 错误: line {ex.LineNumber}, byte {ex.BytePositionInLine}");
                args.Cancel = true;
                return;
            }

            var edited = new TranslationProfile
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = nameBox.Text.Trim(),
                BaseUrl = baseUrlBox.Text.Trim(),
                Model = modelBox.Text.Trim(),
                ApiKey = apiKeyBox.Password,
                Location = SelectedEnum<TranslationServiceLocation>(locationBox),
                RequestCompatibility = SelectedEnum<TranslationRequestCompatibility>(compatibilityBox),
                CustomExtraBody = customBody,
                TimeoutMs = (int)NumberValue(timeoutBox, 10000),
                MaxConcurrency = (int)NumberValue(concurrencyBox, 2)
            };
            var errors = TranslationProfileRules.Validate(edited);
            if (errors.Count > 0)
            {
                ShowValidationError(string.Join(Environment.NewLine, errors));
                args.Cancel = true;
                return;
            }

            var fingerprint = TranslationProfileRules.CreateValidationFingerprint(edited);
            candidate = edited with
            {
                ValidationFingerprint = existing?.ValidationFingerprint == fingerprint ? fingerprint : null,
                LastValidatedAt = existing?.ValidationFingerprint == fingerprint ? existing.LastValidatedAt : null,
                LastValidationLatencyMs = existing?.ValidationFingerprint == fingerprint
                    ? existing.LastValidationLatencyMs
                    : null
            };
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? candidate : null;
    }

    private static void StripHeavyDialogEffects(DependencyObject root, DependencyObject stopNode)
    {
        if (ReferenceEquals(root, stopNode))
        {
            return;
        }

        if (root is UIElement element && element.Effect is DropShadowEffect)
        {
            element.Effect = null;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            StripHeavyDialogEffects(VisualTreeHelper.GetChild(root, i), stopNode);
        }
    }

    private static ScrollViewer? FindScrollableAncestor(
        DependencyObject? source,
        ScrollViewer dialogScrollViewer,
        int delta)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is not ScrollViewer scrollViewer || ReferenceEquals(scrollViewer, dialogScrollViewer) ||
                scrollViewer.ScrollableHeight <= 0)
            {
                continue;
            }

            var canScroll = delta > 0
                ? scrollViewer.VerticalOffset > 0
                : scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
            if (canScroll)
            {
                return scrollViewer;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        if (element is FrameworkElement frameworkElement)
        {
            return frameworkElement.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        return element is System.Windows.Media.Visual
            ? System.Windows.Media.VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
    }

    private static StackPanel CreateField(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 5)
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(labelText);
        panel.Children.Add(control);
        return panel;
    }

    private void RefreshTranslationProfileList(Guid? selectedId)
    {
        var items = _settings.Translation.Profiles
            .Select(profile =>
            {
                var validated = TranslationProfileRules.IsValidated(profile);
                return new TranslationProfileDisplayItem(
                    profile.Id,
                    profile.Name,
                    SafeFinalEndpoint(profile.BaseUrl),
                    validated ? "已验证" : "未验证",
                    validated);
            })
            .ToArray();
        TranslationProfileList.ItemsSource = items;
        TranslationProfileList.SelectedItem = selectedId is { } id
            ? items.FirstOrDefault(item => item.Id == id)
            : null;
        UpdateTranslationProfileSelectionUi();
    }

    private void UpdateTranslationProfileSelectionUi()
    {
        var profile = SelectedTranslationProfile();
        var hasProfile = profile is not null;
        EditTranslationProfileButton.IsEnabled = hasProfile;
        CopyTranslationProfileButton.IsEnabled = hasProfile;
        DeleteTranslationProfileButton.IsEnabled = hasProfile;
        TestTranslationProfileButton.IsEnabled = hasProfile;
        TranslationSelectionSummaryText.Text = profile is null
            ? _settings.Translation.Profiles.Count == 0 ? "尚未添加配置" : "请选择活动配置"
            : $"{profile.Name} · {(TranslationProfileRules.IsValidated(profile) ? "已验证" : "未验证")}";
        TranslationRemoteNoticeText.Visibility = profile?.Location == TranslationServiceLocation.Remote
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateHomeServiceSummary();
        RefreshPipelineVisualStates();
    }

    private TranslationProfile? SelectedTranslationProfile()
    {
        if (TranslationProfileList.SelectedItem is not TranslationProfileDisplayItem selected)
        {
            return null;
        }
        return _settings.Translation.Profiles.FirstOrDefault(profile => profile.Id == selected.Id);
    }

    private void ReplaceTranslationProfile(TranslationProfile profile)
    {
        var profiles = _settings.Translation.Profiles
            .Select(existing => existing.Id == profile.Id ? profile : existing)
            .ToList();
        _settings = _settings with
        {
            Translation = _settings.Translation with
            {
                Profiles = profiles,
                ActiveProfileId = profile.Id
            }
        };
    }

    private void SelectTranslationTargetLanguage(string targetLanguage)
    {
        _syncingQuickControls = true;
        try
        {
            SelectComboBoxItemByTag(TranslationTargetLanguageBox, targetLanguage);
            if (HomeTranslationTargetLanguageBox is not null)
            {
                SelectComboBoxItemByTag(HomeTranslationTargetLanguageBox, targetLanguage);
            }
        }
        finally
        {
            _syncingQuickControls = false;
        }
    }

    private static void SelectComboBoxItemByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (comboBox.SelectedIndex < 0 && comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private string GetSelectedTranslationTargetLanguage()
    {
        return (TranslationTargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-Hans";
    }

    private static T SelectedEnum<T>(ComboBox comboBox) where T : struct, Enum
    {
        return comboBox.SelectedItem is ComboBoxItem { Tag: T value } ? value : default;
    }

    private static string CompatibilityLabel(TranslationRequestCompatibility compatibility) => compatibility switch
    {
        TranslationRequestCompatibility.Standard => "标准兼容（不设置思考模式）",
        TranslationRequestCompatibility.DeepSeek => "DeepSeek（thinking.type=disabled）",
        TranslationRequestCompatibility.QwenVllm => "Qwen + vLLM（enable_thinking=false）",
        TranslationRequestCompatibility.Custom => "自定义 extraBody",
        _ => compatibility.ToString()
    };

    private static string SafeFinalEndpoint(string baseUrl)
    {
        try
        {
            return TranslationProfileRules.BuildFinalEndpoint(baseUrl);
        }
        catch
        {
            return baseUrl;
        }
    }

    private sealed record TranslationProfileDisplayItem(
        Guid Id,
        string Name,
        string Endpoint,
        string ValidationStatus,
        bool IsValidated);

    private void SelectVadEndpointMode(VadEndpointMode mode)
    {
        var button = mode switch
        {
            VadEndpointMode.LowLatency => LowLatencyVadModeButton,
            VadEndpointMode.Balanced => BalancedVadModeButton,
            VadEndpointMode.SentenceComplete => SentenceCompleteVadModeButton,
            VadEndpointMode.Fixed => FixedVadModeButton,
            _ => BalancedVadModeButton
        };
        button.IsChecked = true;
    }

    private VadEndpointMode SelectedVadEndpointMode()
    {
        if (LowLatencyVadModeButton.IsChecked == true)
        {
            return VadEndpointMode.LowLatency;
        }

        if (SentenceCompleteVadModeButton.IsChecked == true)
        {
            return VadEndpointMode.SentenceComplete;
        }

        return FixedVadModeButton.IsChecked == true ? VadEndpointMode.Fixed : VadEndpointMode.Balanced;
    }

    private void UpdateVadEndpointModeUi()
    {
        if (EndSilenceBox is null || CurrentVadEndpointText is null)
        {
            return;
        }

        var mode = SelectedVadEndpointMode();
        EndSilenceBox.IsEnabled = mode == VadEndpointMode.Fixed;
        var fixedMs = Math.Clamp((int)NumberValue(EndSilenceBox, 400), 200, 800);
        var profile = VadEndpointProfiles.Get(mode, fixedMs);
        var suffix = profile.IsAdaptive ? "自适应" : "固定";
        CurrentVadEndpointText.Text =
            $"当前断句等待：{profile.InitialEndSilenceMs}ms（{VadEndpointModeLabel(mode)}，{suffix}）";
        if (HomeVadBadgeText is not null)
        {
            HomeVadBadgeText.Text = $"{VadEndpointModeLabel(mode)} {profile.InitialEndSilenceMs}ms";
        }
        AdaptiveVadStatusText.Text = profile.IsAdaptive ? "等待运行" : "固定值模式，不参与自适应";
        UpdateVadModeDetailCard(mode, fixedMs, profile);
    }

    private void UpdateVadModeDetailCard(VadEndpointMode mode, int fixedMs, VadEndpointProfile profile)
    {
        if (VadModeDetailTitleText is null ||
            VadModeDetailRangeText is null ||
            VadModeDetailDescriptionText is null ||
            VadRangeLeftCol is null ||
            VadRangeActiveCol is null ||
            VadRangeRightCol is null)
        {
            return;
        }

        double minMs;
        double maxMs;
        switch (mode)
        {
            case VadEndpointMode.LowLatency:
                VadModeDetailTitleText.Text = "低延迟";
                VadModeDetailRangeText.Text = $"自适应区间 {profile.MinimumEndSilenceMs} – {profile.MaximumEndSilenceMs} ms";
                VadModeDetailDescriptionText.Text = "响应最快，适合语速紧凑、少停顿的播报或游戏解说；若说话人习惯句中停顿，可能会将长句切得偏碎。";
                minMs = profile.MinimumEndSilenceMs;
                maxMs = profile.MaximumEndSilenceMs;
                break;

            case VadEndpointMode.SentenceComplete:
                VadModeDetailTitleText.Text = "句子完整";
                VadModeDetailRangeText.Text = $"自适应区间 {profile.MinimumEndSilenceMs} – {profile.MaximumEndSilenceMs} ms";
                VadModeDetailDescriptionText.Text = "等待更长时间确认句子结束，减少半句提前切断，适合语速较慢的演讲、访谈或技术分享。";
                minMs = profile.MinimumEndSilenceMs;
                maxMs = profile.MaximumEndSilenceMs;
                break;

            case VadEndpointMode.Fixed:
                VadModeDetailTitleText.Text = "固定值";
                VadModeDetailRangeText.Text = $"固定等待 {fixedMs} ms（不自适应）";
                VadModeDetailDescriptionText.Text = "严格按照下方指定的毫秒数切段，不随语速波动变化，适合节奏稳定、环境可控的音频流。";
                minMs = Math.Max(200d, fixedMs - 14d);
                maxMs = Math.Min(800d, fixedMs + 14d);
                break;

            default:
                VadModeDetailTitleText.Text = "均衡（推荐）";
                VadModeDetailRangeText.Text = $"自适应区间 {profile.MinimumEndSilenceMs} – {profile.MaximumEndSilenceMs} ms";
                VadModeDetailDescriptionText.Text = "根据说话人的停顿习惯动态微调切段时机，在大多数直播、网课与会议中兼顾响应速度与语义完整。";
                minMs = profile.MinimumEndSilenceMs;
                maxMs = profile.MaximumEndSilenceMs;
                break;
        }

        var left = Math.Max(0d, minMs - 200d);
        var active = Math.Max(20d, maxMs - minMs);
        var right = Math.Max(0d, 800d - (200d + left + active));
        VadRangeLeftCol.Width = new GridLength(left, GridUnitType.Star);
        VadRangeActiveCol.Width = new GridLength(active, GridUnitType.Star);
        VadRangeRightCol.Width = new GridLength(right, GridUnitType.Star);
    }

    private static string FormatAdaptiveVadStatus(EndpointEvaluation? evaluation)
    {
        if (evaluation is null)
        {
            return "等待收集停顿样本";
        }

        var decision = evaluation.Decision switch
        {
            EndpointEvaluationDecision.Adjusted => evaluation.Signal switch
            {
                EndpointEvaluationSignal.ContinuousSpeech => $"连续快语速，降至 {evaluation.EffectiveEndSilenceMs}ms",
                EndpointEvaluationSignal.IncompleteUtterance => $"未完句续接，调至 {evaluation.EffectiveEndSilenceMs}ms",
                _ => $"已调整至 {evaluation.EffectiveEndSilenceMs}ms"
            },
            EndpointEvaluationDecision.WaitingForSamples => "样本不足，持续学习",
            EndpointEvaluationDecision.WaitingForQuickResumes => "等待连续快速恢复",
            EndpointEvaluationDecision.WaitingForStablePauses => "等待稳定停顿",
            EndpointEvaluationDecision.Cooldown => "调整冷却中",
            EndpointEvaluationDecision.RateLimited => "调整频率受限",
            EndpointEvaluationDecision.AtBoundary => "已到模式边界",
            EndpointEvaluationDecision.TargetUnchanged => "当前端点保持不变",
            EndpointEvaluationDecision.IdleReturning => "空闲后回归初始值",
            EndpointEvaluationDecision.IdleNoChange => "空闲后保持初始值",
            _ => "已完成判断"
        };

        return $"样本 {evaluation.SampleCount}/8 · P75 {evaluation.P75PauseMs}ms · {decision}";
    }

    private static string VadEndpointModeLabel(VadEndpointMode mode)
    {
        return mode switch
        {
            VadEndpointMode.LowLatency => "低延迟",
            VadEndpointMode.Balanced => "均衡",
            VadEndpointMode.SentenceComplete => "句子完整",
            VadEndpointMode.Fixed => "固定值",
            _ => "均衡"
        };
    }

    private string SelectedAudioDeviceId()
    {
        var selectedIndex = AudioDeviceComboBox.SelectedIndex;
        return selectedIndex >= 0 && selectedIndex < _audioDevices.Count
            ? _audioDevices[selectedIndex].Id
            : "default";
    }

    private void RegisterHotkeys()
    {
        if (HotkeysEnabledSwitch.IsChecked != true)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(OnWndProc);

        RegisterHotKey(handle, HotkeyToggleCaption, ModControl | ModAlt, (uint)KeyInterop.VirtualKeyFromKey(Key.S));
        RegisterHotKey(handle, HotkeyToggleWindow, ModControl | ModAlt, (uint)KeyInterop.VirtualKeyFromKey(Key.H));
        RegisterHotKey(handle, HotkeyToggleLock, ModControl | ModAlt, (uint)KeyInterop.VirtualKeyFromKey(Key.L));
    }

    private void UnregisterHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(handle, HotkeyToggleCaption);
        UnregisterHotKey(handle, HotkeyToggleWindow);
        UnregisterHotKey(handle, HotkeyToggleLock);
        _hwndSource?.RemoveHook(OnWndProc);
        _hwndSource = null;
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeyToggleCaption:
                OnStartStopClick(this, new RoutedEventArgs());
                break;
            case HotkeyToggleWindow:
                ToggleFloatingWindowVisibility();
                break;
            case HotkeyToggleLock:
                OnToggleFloatingLockClick(this, new RoutedEventArgs());
                break;
        }

        return IntPtr.Zero;
    }

    private void ToggleFloatingWindowVisibility()
    {
        if (_floatingWindow is null || !_floatingWindow.IsVisible)
        {
            ShowFloatingWindow();
        }
        else
        {
            _floatingWindow.Hide();
            UpdateFloatingWindowButtonState();
        }
    }

    private void UpdateFloatingWindowButtonState()
    {
        var isVisible = _floatingWindow?.IsVisible == true;
        _isFloatingWindowVisible = isVisible;
        var buttonText = isVisible ? "隐藏悬浮窗" : "显示悬浮窗";

        HomeFloatingWindowButton.ToolTip = buttonText;
        HomeFloatingWindowText.Text = buttonText;
        AutomationProperties.SetName(HomeFloatingWindowButton, buttonText);
        FloatingWindowIcon.Symbol = isVisible ? SymbolRegular.Dismiss24 : SymbolRegular.Window24;
        SyncTrayMenuState();
    }

    private string GetSelectedLanguage()
    {
        return "auto";
    }

    private static double NumberValue(NumberBox numberBox, double fallback)
    {
        return numberBox.Value ?? fallback;
    }

    private void EnsureDataDirectories()
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "subtitles"));
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "debug-audio"));

        // D018: if the data directory is not writable (e.g. installed to
        // Program Files), fail explicitly rather than silently discarding
        // settings, history and logs for the entire session.
        var probe = Path.Combine(_dataDirectory, ".write-probe");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            StartStopButton.IsEnabled = false;
            SetRuntimeError($"数据目录不可写：{_dataDirectory}");
            throw new InvalidOperationException(
                $"数据目录不可写，请把程序解压到可写位置（{_dataDirectory}）。", ex);
        }
    }

    private static void OpenDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void AppendAppLog(string message)
    {
        try
        {
            var logsDirectory = Path.Combine(_dataDirectory, "logs");
            Directory.CreateDirectory(logsDirectory);
            File.AppendAllText(
                Path.Combine(logsDirectory, "app.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the capture or UI path.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
