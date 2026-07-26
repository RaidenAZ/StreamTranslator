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
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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
    private Snackbar? _copyFailureSnackbar;
    private readonly TranslationIssueTracker _translationIssueTracker = new();
    private TranslationRuntimeStatus _translationRuntimeStatus = new("已关闭", "已关闭", 0, 0);

    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureDataDirectories();
            _settingsStore = new SettingsStore(Path.Combine(_dataDirectory, "settings.json"));
            _settings = await _settingsStore.LoadAsync();
            ApplySettingsToUi(_settings);
            LoadAudioDevices(_settings.Audio.DeviceId);
            RegisterHotkeys();
            ShowPage(HomePage);
            SetActiveNavigationItem("HomePage");
            DataDirectoryText.Text = $"数据目录: {_dataDirectory}";
            ResetSubtitlePlaceholder();
            await LoadTodaySubtitleHistoryAsync();
            TryShowFloatingWindow();
            App.StartupCompleted = true;
        }
        catch (Exception ex)
        {
            AppendAppLog($"启动初始化失败: {ex.Message}");
            LastErrorText.Text = ex.Message;
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

        e.Cancel = true;
        _isClosing = true;
        IsEnabled = false;

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
        }
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
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
            }

            runtimeSettings = ResolveRuntimeTranslationSettings();
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
            var result = System.Windows.MessageBox.Show(
                this,
                $"翻译 worker 启动失败：{ex.InnerException?.Message ?? ex.Message}{Environment.NewLine}{Environment.NewLine}是否仅启动原文字幕？选择“否”返回设置。",
                "翻译暂不可用",
                System.Windows.MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Yes);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                ShowPage(SettingsPage);
                SetActiveNavigationItem("SettingsPage");
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
                LastErrorText.Text = sourceOnlyException.Message;
            }
        }
        catch (Exception ex)
        {
            StartStopButton.IsEnabled = true;
            await StopRuntimeAsync();
            AppendAppLog($"字幕 runtime 启动失败: {ex.Message}");
            LastErrorText.Text = ex.Message;
        }
    }

    private async Task StartRuntimeCoreAsync(AppSettings runtimeSettings)
    {
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
        _runtime.RuntimeError += OnRuntimeError;
        _runtime.AudioLevelChanged += OnAudioLevelChanged;
        _runtime.VadEndpointChanged += OnVadEndpointChanged;
        _runtime.TranslationReady += OnTranslationReady;
        _runtime.TranslationStatusChanged += OnTranslationStatusChanged;
        _runtime.TranslationTaskStatusChanged += OnTranslationTaskStatusChanged;

        AudioStatusText.Text = "等待捕获";
        VadStatusText.Text = "加载中";
        WorkerStatusText.Text = "启动中";
        ApiStatusText.Text = "等待请求";
        SubtitleOutputStatusText.Text = "等待字幕";
        TranslationWorkerStatusText.Text = _sessionTranslationEnabled ? "启动中" : "已关闭";
        TranslationApiStatusText.Text = _sessionTranslationEnabled ? "等待请求" : "已关闭";
        HomeRuntimeSummaryText.Text = "启动中，正在连接音频与识别服务";
        SetStateBadge("启动中");
        StartStopButton.IsEnabled = false;

        await _runtime.StartAsync();

        _isRunning = true;
        TranslationSettingsPanel.IsEnabled = false;
        HardMaxSegmentBox.IsEnabled = false;
        StartStopText.Text = "停止字幕";
        StartStopIcon.Symbol = SymbolRegular.Stop24;
        StartStopButton.IsEnabled = true;
        HomeRuntimeSummaryText.Text = "运行中，正在监听系统输出声音";
        SetStateBadge("运行中");
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
            _runtime.RuntimeError -= OnRuntimeError;
            _runtime.AudioLevelChanged -= OnAudioLevelChanged;
            _runtime.VadEndpointChanged -= OnVadEndpointChanged;
            _runtime.TranslationReady -= OnTranslationReady;
            _runtime.TranslationStatusChanged -= OnTranslationStatusChanged;
            _runtime.TranslationTaskStatusChanged -= OnTranslationTaskStatusChanged;
            await _runtime.DisposeAsync();
            _runtime = null;
        }

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
        AudioLevelBar.Value = 0;
        HomeAudioLevelText.Text = "0%";
        HomeRuntimeSummaryText.Text = "未启动，等待开始";
        SetStateBadge("就绪");
        StartStopText.Text = "开始字幕";
        StartStopIcon.Symbol = SymbolRegular.Play24;
        StartStopButton.IsEnabled = true;
        TranslationSettingsPanel.IsEnabled = true;
        HardMaxSegmentBox.IsEnabled = true;
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
            LastErrorText.Text = ex.Message;
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
            LastErrorText.Text = ex.Message;
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

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "清空后会删除当天字幕历史，无法从界面恢复。确定继续吗？",
            "确认清空历史",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        SubtitleList.Items.Clear();
        var historyPath = Path.Combine(_dataDirectory, "subtitles", $"{DateTime.Now:yyyy-MM-dd}.jsonl");
        if (File.Exists(historyPath))
        {
            File.WriteAllText(historyPath, string.Empty);
        }

        ResetSubtitlePlaceholder();
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
            Version = "V1.2",
            OperatingSystem = Environment.OSVersion.ToString(),
            DataDirectory = _dataDirectory,
            AudioStatus = AudioStatusText.Text,
            VadStatus = VadStatusText.Text,
            AsrWorkerStatus = WorkerStatusText.Text,
            AsrApiStatus = ApiStatusText.Text,
            AsrModel = ModelBox.Text,
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
            SubtitleHistoryCopyStatusText.Text = "已复制";
            return;
        }

        SubtitleHistoryCopyStatusText.Text = "剪贴板暂时不可用，请稍后重试";
        ShowCopyFailureToast();
        var error = result.Error;
        AppendAppLog(
            $"{operation}复制失败: {error?.GetType().Name ?? "UnknownException"}: {error?.Message ?? "未知错误"}");
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

    private void OnRuntimeStatusChanged(object? sender, string status)
    {
        // BeginInvoke keeps UI exceptions from propagating back into the
        // runtime's background tasks (and from blocking the audio pipeline).
        Dispatcher.BeginInvoke(() =>
        {
            if (status.Contains("ASR API", StringComparison.OrdinalIgnoreCase))
            {
                ApiStatusText.Text = status;
            }
            else if (status.Contains("worker", StringComparison.OrdinalIgnoreCase))
            {
                WorkerStatusText.Text = status;
            }
            else if (status.Contains("VAD", StringComparison.OrdinalIgnoreCase))
            {
                VadStatusText.Text = status;
            }
            else if (status.Contains("捕获", StringComparison.OrdinalIgnoreCase))
            {
                AudioStatusText.Text = status;
            }

            AppendAppLog(status);
        });
    }

    private void OnSubtitleReady(object? sender, SubtitleItem item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var text = item.SourceText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                RemoveSubtitlePlaceholder();
                if (string.Equals(item.Type, "subtitle_revision", StringComparison.Ordinal))
                {
                    ApplySubtitleRevision(item);
                }
                else
                {
                    SubtitleList.Items.Insert(0, item);
                }

                var translationPending = _sessionTranslationEnabled &&
                    !SourceLanguageDecision.ShouldSkip(
                        _settings.Asr.Language,
                        _settings.Translation.TargetLanguage,
                        item.SourceText);
                _floatingWindow?.PublishSource(item, translationPending);
                SubtitleOutputStatusText.Text = "输出中";
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
            }
            _floatingWindow?.ApplyTranslation(update);
            SubtitleOutputStatusText.Text = "双语输出中";
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
        });
    }

    private void OnTranslationTaskStatusChanged(object? sender, TranslationTaskStatusUpdate status)
    {
        var issue = _translationIssueTracker.Apply(status);
        Dispatcher.BeginInvoke(() =>
        {
            UpdateTranslationIssueUi(issue);
            _floatingWindow?.ClearTranslationPending(status);
        });
    }

    private void SetStateBadge(string text)
    {
        HomeStateBadgeText.Text = text;
        var foregroundKey = text switch
        {
            "运行中" => "SystemFillColorSuccessBrush",
            "启动中" => "SystemFillColorCautionBrush",
            "错误" => "SystemFillColorCriticalBrush",
            _ => "AccentTextFillColorPrimaryBrush"
        };
        HomeStateBadgeText.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
    }

    private void UpdateTranslationIssueUi(TranslationIssueState state)
    {
        TranslationIssueText.Text = state.Summary;
        TranslationIssueText.Visibility = state.HasIssue ? Visibility.Visible : Visibility.Collapsed;
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
    }

    private void OnRuntimeError(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            LastErrorText.Text = ex.Message;
            SetStateBadge("错误");
            HomeRuntimeSummaryText.Text = "运行异常，请查看问题信息";
            AppendAppLog($"错误: {ex.GetType().Name}: {ex.Message}");

            if (ex is RuntimeFatalException && _isRunning)
            {
                await StopRuntimeAsync();
                LastErrorText.Text = ex.Message;
                SetStateBadge("错误");
                HomeRuntimeSummaryText.Text = "字幕已停止，请处理问题后重新开始";
            }
        });
    }

    private void OnAudioLevelChanged(object? sender, double level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AudioLevelBar.Value = level;
            HomeAudioLevelText.Text = $"{Math.Round(level)}%";
        });
    }

    private void OnVadEndpointChanged(object? sender, VadEndpointRuntimeStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var label = VadEndpointModeLabel(status.Mode);
            var suffix = status.IsAdaptive ? "自适应" : "固定";
            CurrentVadEndpointText.Text = $"当前断句等待：{status.EffectiveEndSilenceMs}ms（{label}，{suffix}）";
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
            _floatingWindow.Closed += (closedWindow, _) =>
            {
                if (closedWindow is FloatingSubtitleWindow floatingWindow)
                {
                    floatingWindow.VisibleGroupsChanged -= OnFloatingVisibleGroupsChanged;
                }
                _floatingWindow = null;
                UpdateFloatingWindowButtonState();
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
            FloatingOpacitySlider.Value);
        UpdateFloatingWindowButtonState();
    }

    private void OnFloatingVisibleGroupsChanged(IReadOnlyCollection<string> visibleGroupIds)
    {
        _runtime?.UpdateTranslationVisibleGroups(visibleGroupIds);
    }

    private void TryShowFloatingWindow()
    {
        try
        {
            ShowFloatingWindow();
        }
        catch (Exception ex)
        {
            LastErrorText.Text = ex.Message;
            AppendAppLog($"悬浮窗显示失败: {ex}");
        }
    }

    private void LoadAudioDevices(string selectedDeviceId)
    {
        _audioDevices.Clear();
        AudioDeviceComboBox.Items.Clear();
        try
        {
            var devices = _audioDeviceService.GetOutputDevices();
            var selectedIndex = -1;
            var defaultIndex = -1;

            foreach (var device in devices)
            {
                var index = _audioDevices.Count;
                _audioDevices.Add(device);
                AudioDeviceComboBox.Items.Add($"{device.Name}{(device.IsDefault ? " (默认)" : "")}");

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
                AudioDeviceComboBox.SelectedIndex = selectedIndex >= 0 ? selectedIndex : Math.Max(defaultIndex, 0);
            }

            UpdateCurrentAudioDeviceText();
        }
        catch (Exception ex)
        {
            LastErrorText.Text = $"音频设备加载失败: {ex.Message}";
            CurrentAudioDeviceText.Text = "加载失败";
        }
    }

    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCurrentAudioDeviceText();
    }

    private void OnVadEndpointModeChecked(object sender, RoutedEventArgs e)
    {
        UpdateVadEndpointModeUi();
    }

    private void OnFixedEndSilenceValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (FixedVadModeButton?.IsChecked == true)
        {
            UpdateVadEndpointModeUi();
        }
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
        ApiKeyBox.Password = settings.Asr.ApiKey;
        BaseUrlBox.Text = settings.Asr.BaseUrl;
        ModelBox.Text = settings.Asr.Model;
        TimeoutBox.Value = settings.Asr.TimeoutMs;
        MaxConcurrencyBox.Value = settings.Asr.MaxConcurrency;
        FloatingFontSizeBox.Value = settings.SubtitleWindow.FontSize;
        FloatingLinesBox.Value = settings.SubtitleWindow.MaxSubtitleItems;
        FloatingOpacitySlider.Value = settings.SubtitleWindow.Opacity;
        HotkeysEnabledSwitch.IsChecked = settings.Hotkeys.Enabled;
        LanguageBox.Text = "自动检测 (auto)";
        TranslationEnabledSwitch.IsChecked = settings.Translation.Enabled;
        SelectTranslationTargetLanguage(settings.Translation.TargetLanguage);
        RefreshTranslationProfileList(settings.Translation.ActiveProfileId);
    }

    private async Task SaveSettingsAsync()
    {
        if (_settingsStore is null)
        {
            return;
        }

        _settings = _settings with
        {
            SchemaVersion = 4,
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
                ApiKey = ApiKeyBox.Password,
                BaseUrl = BaseUrlBox.Text,
                Model = ModelBox.Text,
                Language = "auto",
                TimeoutMs = (int)NumberValue(TimeoutBox, 30000),
                MaxConcurrency = (int)NumberValue(MaxConcurrencyBox, 2)
            },
            Translation = _settings.Translation with
            {
                Enabled = TranslationEnabledSwitch.IsChecked == true,
                TargetLanguage = GetSelectedTranslationTargetLanguage(),
                ActiveProfileId = SelectedTranslationProfile()?.Id
            },
            SubtitleWindow = _settings.SubtitleWindow with
            {
                FontSize = NumberValue(FloatingFontSizeBox, 18),
                MaxSubtitleItems = (int)NumberValue(FloatingLinesBox, 2),
                Opacity = FloatingOpacitySlider.Value
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
            }
        };

        await _settingsStore.SaveAsync(_settings);
    }

    private AppSettings? ResolveRuntimeTranslationSettings()
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
        var result = System.Windows.MessageBox.Show(
            this,
            $"{reason}{Environment.NewLine}{Environment.NewLine}是否仅启动原文字幕？",
            "翻译暂不可用",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Yes);
        if (result == System.Windows.MessageBoxResult.Yes)
        {
            return _settings with
            {
                Translation = _settings.Translation with { Enabled = false }
            };
        }

        ShowPage(SettingsPage);
        SetActiveNavigationItem("SettingsPage");
        return null;
    }

    private void OnTranslationSettingChanged(object sender, RoutedEventArgs e)
    {
        if (TranslationSelectionSummaryText is not null)
        {
            UpdateTranslationProfileSelectionUi();
        }
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
        if (selected is null)
        {
            return;
        }
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
        if (selected is null)
        {
            return;
        }
        var result = System.Windows.MessageBox.Show(
            this,
            $"确定删除模型配置“{selected.Name}”吗？",
            "删除翻译配置",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }
        var profiles = _settings.Translation.Profiles.Where(profile => profile.Id != selected.Id).ToList();
        _settings = _settings with
        {
            Translation = _settings.Translation with
            {
                Profiles = profiles,
                ActiveProfileId = null
            }
        };
        RefreshTranslationProfileList(null);
        await SaveSettingsAsync();
    }

    private async void OnTestTranslationProfileClick(object sender, RoutedEventArgs e)
    {
        var profile = SelectedTranslationProfile();
        if (profile is null)
        {
            return;
        }
        var errors = TranslationProfileRules.Validate(profile);
        if (errors.Count > 0)
        {
            TranslationTestResultText.Text = string.Join(Environment.NewLine, errors);
            return;
        }

        TestTranslationProfileButton.IsEnabled = false;
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
            TestTranslationProfileButton.IsEnabled = SelectedTranslationProfile() is not null;
        }
    }

    private async Task<TranslationProfile?> ShowTranslationProfileEditorAsync(TranslationProfile? existing)
    {
        var nameBox = new TextBox { Text = existing?.Name ?? "", Height = 34 };
        var baseUrlBox = new TextBox { Text = existing?.BaseUrl ?? "", Height = 34 };
        var endpointPreview = new TextBlock
        {
            Foreground = FindResource("TextFillColorSecondaryBrush") as System.Windows.Media.Brush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var modelBox = new TextBox { Text = existing?.Model ?? "", Height = 34 };
        var apiKeyBox = new PasswordBox { Password = existing?.ApiKey ?? "", Height = 34 };
        var locationBox = new ComboBox { Height = 34 };
        locationBox.Items.Add(new ComboBoxItem { Content = "本地服务", Tag = TranslationServiceLocation.Local });
        locationBox.Items.Add(new ComboBoxItem { Content = "远程服务", Tag = TranslationServiceLocation.Remote });
        locationBox.SelectedIndex = existing?.Location == TranslationServiceLocation.Local ? 0 : 1;
        var compatibilityBox = new ComboBox { Height = 34 };
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
        var customExtraBodyBox = new TextBox
        {
            Text = existing?.CustomExtraBody.GetRawText() ?? "{}",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var validationText = new TextBlock
        {
            Foreground = TryFindResource("SystemFillColorCriticalBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var advancedPanel = new StackPanel();
        advancedPanel.Children.Add(CreateField("请求兼容", compatibilityBox));
        var limits = new Grid();
        limits.ColumnDefinitions.Add(new ColumnDefinition());
        limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        limits.ColumnDefinitions.Add(new ColumnDefinition());
        var timeoutField = CreateField("超时 ms", timeoutBox);
        var concurrencyField = CreateField("最大并发", concurrencyBox);
        Grid.SetColumn(concurrencyField, 2);
        limits.Children.Add(timeoutField);
        limits.Children.Add(concurrencyField);
        advancedPanel.Children.Add(limits);
        var customField = CreateField("Custom extraBody", customExtraBodyBox);
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
            try
            {
                endpointPreview.Text = $"最终请求: {TranslationProfileRules.BuildFinalEndpoint(baseUrlBox.Text)}";
            }
            catch
            {
                endpointPreview.Text = "最终请求: 等待有效 Base URL";
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
            Width = 480,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var protocolNotice = new TextBlock
        {
            Text = "接口协议：兼容 OpenAI Chat Completions API",
            Foreground = FindResource("TextFillColorSecondaryBrush") as System.Windows.Media.Brush,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetAutomationId(protocolNotice, "TranslationProtocolNotice");
        form.Children.Add(protocolNotice);
        form.Children.Add(CreateField("配置名称", nameBox));
        var baseUrlField = CreateField("Base URL", baseUrlBox);
        baseUrlField.Children.Add(endpointPreview);
        form.Children.Add(baseUrlField);
        form.Children.Add(CreateField("模型名称", modelBox));
        form.Children.Add(CreateField("API Key", apiKeyBox));
        form.Children.Add(CreateField("服务位置", locationBox));
        var advancedExpander = new Expander
        {
            Header = "高级设置",
            Content = advancedPanel,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(advancedExpander, "TranslationAdvancedSettingsExpander");
        form.Children.Add(advancedExpander);
        form.Children.Add(validationText);

        TranslationProfile? candidate = null;
        var dialogScrollViewer = new ScrollViewer
        {
            MaxHeight = 500,
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

            var direction = args.Delta > 0 ? -1 : 1;
            var step = Math.Max(24, dialogScrollViewer.ViewportHeight * 0.12);
            dialogScrollViewer.ScrollToVerticalOffset(
                Math.Clamp(dialogScrollViewer.VerticalOffset + direction * step, 0, dialogScrollViewer.ScrollableHeight));
            args.Handled = true;
        };
        AutomationProperties.SetAutomationId(dialogScrollViewer, "TranslationProfileDialogScrollViewer");

        var dialog = new ContentDialog(RootContentDialogHost)
        {
            Title = existing is null ? "添加翻译模型" : "编辑翻译模型",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DialogWidth = 560,
            DialogMaxHeight = 680,
            Content = dialogScrollViewer
        };
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
                validationText.Text = $"extraBody JSON 错误: line {ex.LineNumber}, byte {ex.BytePositionInLine}";
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
                validationText.Text = string.Join(Environment.NewLine, errors);
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
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Application.Current.TryFindResource("TextFillColorSecondaryBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(control);
        return panel;
    }

    private void RefreshTranslationProfileList(Guid? selectedId)
    {
        var items = _settings.Translation.Profiles
            .Select(profile => new TranslationProfileDisplayItem(
                profile.Id,
                profile.Name,
                SafeFinalEndpoint(profile.BaseUrl),
                TranslationProfileRules.IsValidated(profile) ? "已验证" : "未验证"))
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
        TranslationTargetLanguageBox.SelectedItem = TranslationTargetLanguageBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), targetLanguage, StringComparison.OrdinalIgnoreCase));
        if (TranslationTargetLanguageBox.SelectedIndex < 0)
        {
            TranslationTargetLanguageBox.SelectedIndex = 0;
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
        string ValidationStatus);

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
        var profile = VadEndpointProfiles.Get(mode, (int)NumberValue(EndSilenceBox, 400));
        var suffix = profile.IsAdaptive ? "自适应" : "固定";
        CurrentVadEndpointText.Text =
            $"当前断句等待：{profile.InitialEndSilenceMs}ms（{VadEndpointModeLabel(mode)}，{suffix}）";
        AdaptiveVadStatusText.Text = profile.IsAdaptive ? "等待运行" : "固定值模式，不参与自适应";
    }

    private static string FormatAdaptiveVadStatus(EndpointEvaluation? evaluation)
    {
        if (evaluation is null)
        {
            return "等待收集停顿样本";
        }

        var decision = evaluation.Decision switch
        {
            EndpointEvaluationDecision.Adjusted => $"已调整至 {evaluation.EffectiveEndSilenceMs}ms",
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
        var buttonText = isVisible ? "隐藏悬浮窗" : "显示悬浮窗";

        HomeFloatingWindowButton.ToolTip = buttonText;
        AutomationProperties.SetName(HomeFloatingWindowButton, buttonText);
        FloatingWindowIcon.Symbol = isVisible ? SymbolRegular.Dismiss24 : SymbolRegular.Window24;
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
