using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using StreamTranslator.Audio.Capture;
using StreamTranslator.App.Runtime;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Subtitles;
using Wpf.Ui.Controls;
using System.Windows.Interop;

namespace StreamTranslator.App;

public partial class MainWindow : FluentWindow
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyToggleCaption = 1001;
    private const int HotkeyToggleWindow = 1002;
    private const int HotkeyToggleLock = 1003;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const string SubtitlePlaceholder = "开始字幕后，这里会保存今天的字幕记录。";

    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly List<AudioDeviceInfo> _audioDevices = [];
    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private FloatingSubtitleWindow? _floatingWindow;
    private SubtitleRuntime? _runtime;
    private HwndSource? _hwndSource;
    private bool _isRunning;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
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
            TryShowFloatingWindow();
        }
        catch (Exception ex)
        {
            AppendAppLog($"启动初始化失败: {ex.Message}");
            LastErrorText.Text = ex.Message;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        IsEnabled = false;

        await SaveSettingsAsync();
        UnregisterHotkeys();
        await StopRuntimeAsync();

        _floatingWindow?.Close();
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
        if (!_isRunning)
        {
            await StartRuntimeAsync();
        }
        else
        {
            await StopRuntimeAsync();
        }
    }

    private async Task StartRuntimeAsync()
    {
        try
        {
            await SaveSettingsAsync();
            var validationErrors = AppSettingsValidator.ValidateForStart(_settings);
            if (validationErrors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));
            }

            _runtime = new SubtitleRuntime(AppContext.BaseDirectory, _dataDirectory, _settings);
            _runtime.StatusChanged += OnRuntimeStatusChanged;
            _runtime.SubtitleReady += OnSubtitleReady;
            _runtime.RuntimeError += OnRuntimeError;
            _runtime.AudioLevelChanged += OnAudioLevelChanged;

            AudioStatusText.Text = "等待捕获";
            VadStatusText.Text = "加载中";
            WorkerStatusText.Text = "启动中";
            ApiStatusText.Text = "等待请求";
            SubtitleOutputStatusText.Text = "等待字幕";
            HomeRuntimeSummaryText.Text = "启动中，正在连接音频与识别服务";
            HomeStateBadgeText.Text = "启动中";
            StartStopButton.IsEnabled = false;

            await _runtime.StartAsync();

            _isRunning = true;
            StartStopText.Text = "停止字幕";
            StartStopIcon.Symbol = SymbolRegular.Stop24;
            StartStopButton.IsEnabled = true;
            HomeRuntimeSummaryText.Text = "运行中，正在监听系统输出声音";
            HomeStateBadgeText.Text = "运行中";
            AppendAppLog("字幕 runtime 已启动");
            TryShowFloatingWindow();
            _floatingWindow?.SetCaption("等待字幕...");
        }
        catch (Exception ex)
        {
            StartStopButton.IsEnabled = true;
            await StopRuntimeAsync();
            AppendAppLog($"字幕 runtime 启动失败: {ex.Message}");
            LastErrorText.Text = ex.Message;
        }
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
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        _isRunning = false;
        AudioStatusText.Text = "未启动";
        VadStatusText.Text = "等待模型";
        WorkerStatusText.Text = "未启动";
        ApiStatusText.Text = "未测试";
        SubtitleOutputStatusText.Text = "等待字幕";
        AudioLevelBar.Value = 0;
        HomeAudioLevelText.Text = "0%";
        HomeRuntimeSummaryText.Text = "未启动，等待开始";
        HomeStateBadgeText.Text = "就绪";
        StartStopText.Text = "开始字幕";
        StartStopIcon.Symbol = SymbolRegular.Play24;
        StartStopButton.IsEnabled = true;
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

    private void OnCopySelectedClick(object sender, RoutedEventArgs e)
    {
        var selectedText = SubtitleList.SelectedItem?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(selectedText) && selectedText != SubtitlePlaceholder)
        {
            Clipboard.SetText(selectedText);
        }
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var builder = new StringBuilder();
        foreach (var item in SubtitleTexts())
        {
            builder.AppendLine(item);
        }

        Clipboard.SetText(builder.ToString());
    }

    private void OnCopyRecentClick(object sender, RoutedEventArgs e)
    {
        var recent = SubtitleList.Items
            .Cast<object>()
            .Select(static item => item.ToString() ?? "")
            .Where(static text => !string.IsNullOrWhiteSpace(text) && text != SubtitlePlaceholder)
            .TakeLast(10);
        Clipboard.SetText(string.Join(Environment.NewLine, recent));
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

    private IEnumerable<string> SubtitleTexts()
    {
        return SubtitleList.Items
            .Cast<object>()
            .Select(static item => item.ToString() ?? "")
            .Where(static text => !string.IsNullOrWhiteSpace(text) && text != SubtitlePlaceholder);
    }

    private void ResetSubtitlePlaceholder()
    {
        SubtitleList.Items.Clear();
        SubtitleList.Items.Add(SubtitlePlaceholder);
    }

    private void RemoveSubtitlePlaceholder()
    {
        if (SubtitleList.Items.Count == 1 &&
            string.Equals(SubtitleList.Items[0]?.ToString(), SubtitlePlaceholder, StringComparison.Ordinal))
        {
            SubtitleList.Items.Clear();
        }
    }

    private void OnOpenDataDirectoryClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(_dataDirectory);
    }

    private void OnOpenLogsDirectoryClick(object sender, RoutedEventArgs e)
    {
        OpenDirectory(Path.Combine(_dataDirectory, "logs"));
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var diagnostics = $"""
            StreamTranslator V1.0
            OS: {Environment.OSVersion}
            DataDirectory: {_dataDirectory}
            AudioStatus: {AudioStatusText.Text}
            VadStatus: {VadStatusText.Text}
            WorkerStatus: {WorkerStatusText.Text}
            ApiStatus: {ApiStatusText.Text}
            Model: {ModelBox.Text}
            Language: {GetSelectedLanguage()}
            MaxConcurrency: {MaxConcurrencyBox.Value}
            """;
        Clipboard.SetText(diagnostics);
    }

    private void ShowPage(UIElement selectedPage)
    {
        HomePage.Visibility = Visibility.Collapsed;
        SubtitleHistoryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        selectedPage.Visibility = Visibility.Visible;
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
        Dispatcher.Invoke(() =>
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
        Dispatcher.Invoke(() =>
        {
            var text = item.SourceText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                RemoveSubtitlePlaceholder();
                SubtitleList.Items.Add(text);
                SubtitleList.ScrollIntoView(text);
                _floatingWindow?.SetCaption(text);
                SubtitleOutputStatusText.Text = "输出中";
            }
        });
    }

    private void OnRuntimeError(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            LastErrorText.Text = ex.Message;
            HomeStateBadgeText.Text = "错误";
            HomeRuntimeSummaryText.Text = "运行异常，请查看问题信息";
            AppendAppLog($"错误: {ex.GetType().Name}: {ex.Message}");

            if (ex is RuntimeFatalException && _isRunning)
            {
                await StopRuntimeAsync();
                LastErrorText.Text = ex.Message;
                HomeStateBadgeText.Text = "错误";
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

    private void ShowFloatingWindow()
    {
        if (_floatingWindow is null)
        {
            _floatingWindow = new FloatingSubtitleWindow();
            _floatingWindow.Closed += (_, _) =>
            {
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
            NumberValue(FloatingFontSizeBox, 28),
            (int)NumberValue(FloatingLinesBox, 2),
            FloatingOpacitySlider.Value);
        UpdateFloatingWindowButtonState();
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
        MinSegmentBox.Value = settings.Vad.MinSegmentMs;
        DiagnosticsSwitch.IsChecked = settings.Diagnostics.Enabled;
        ApiKeyBox.Password = settings.Asr.ApiKey;
        BaseUrlBox.Text = settings.Asr.BaseUrl;
        ModelBox.Text = settings.Asr.Model;
        TimeoutBox.Value = settings.Asr.TimeoutMs;
        MaxConcurrencyBox.Value = settings.Asr.MaxConcurrency;
        FloatingFontSizeBox.Value = settings.SubtitleWindow.FontSize;
        FloatingLinesBox.Value = settings.SubtitleWindow.MaxLines;
        FloatingOpacitySlider.Value = settings.SubtitleWindow.Opacity;
        HotkeysEnabledSwitch.IsChecked = settings.Hotkeys.Enabled;
        SelectLanguage(settings.Asr.Language);
    }

    private async Task SaveSettingsAsync()
    {
        if (_settingsStore is null)
        {
            return;
        }

        _settings = _settings with
        {
            Audio = _settings.Audio with
            {
                DeviceId = FollowDefaultDeviceSwitch.IsChecked == true ? "default" : SelectedAudioDeviceId(),
                FollowDefaultDevice = FollowDefaultDeviceSwitch.IsChecked == true
            },
            Vad = _settings.Vad with
            {
                EndSilenceMs = (int)NumberValue(EndSilenceBox, 300),
                MinSegmentMs = (int)NumberValue(MinSegmentBox, 900)
            },
            Asr = _settings.Asr with
            {
                ApiKey = ApiKeyBox.Password,
                BaseUrl = BaseUrlBox.Text,
                Model = ModelBox.Text,
                Language = GetSelectedLanguage(),
                TimeoutMs = (int)NumberValue(TimeoutBox, 30000),
                MaxConcurrency = (int)NumberValue(MaxConcurrencyBox, 2)
            },
            SubtitleWindow = _settings.SubtitleWindow with
            {
                FontSize = NumberValue(FloatingFontSizeBox, 28),
                MaxLines = (int)NumberValue(FloatingLinesBox, 2),
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
        return (LanguageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto";
    }

    private void SelectLanguage(string language)
    {
        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), language, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                return;
            }
        }

        LanguageBox.SelectedIndex = 0;
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
