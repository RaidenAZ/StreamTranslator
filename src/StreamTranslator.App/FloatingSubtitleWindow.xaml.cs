using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using StreamTranslator.App.Runtime;
using StreamTranslator.Core.Configuration;
using StreamTranslator.Core.Subtitles;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.App;

public partial class FloatingSubtitleWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    private static readonly SolidColorBrush SourcePrimaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush SourceSecondaryBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB8, 0xE6, 0xEE, 0xF8)));
    private static readonly SolidColorBrush TranslationPrimaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xC8)));
    private static readonly SolidColorBrush TranslationSecondaryBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xDC, 0xF0, 0xF4, 0xFA)));
    private static readonly SolidColorBrush PendingTranslationBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0xD8, 0xE4, 0xF2)));
    private static readonly SolidColorBrush ListeningActivityBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xA3)));
    private static readonly SolidColorBrush ProcessingActivityBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xB0, 0x41)));

    private readonly DispatcherTimer _hoverHideTimer;
    private bool _locked;
    private bool _trimScheduled;
    private bool _applyingBounds;
    private bool _contextMenuOpen;
    private int _maxSubtitleItems = 2;
    private double _fontSize = 18d;
    private double _opacity = 0.72d;
    private SubtitleDisplayMode _displayMode = SubtitleDisplayMode.BilingualSourceFirst;
    private SubtitleOverlayTheme _theme = SubtitleOverlayTheme.CinemaGlass;
    private SubtitleActivityState _activityState = SubtitleActivityState.Idle;
    private double _lastActivityScale = 0.2d;
    private bool _activityTrackShown;
    private bool _hoverToolbarVisible;

    public FloatingSubtitleWindow()
    {
        InitializeComponent();
        _hoverHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _hoverHideTimer.Tick += (_, _) =>
        {
            _hoverHideTimer.Stop();
            if (!IsMouseOver && !_contextMenuOpen)
            {
                SetHoverToolbarVisible(false);
            }
        };

        ApplyVisualTheme(_theme, _opacity);
        UpdateHoverToolbarLabels();
        Entries.Add(new FloatingSubtitleEntry("waiting", 1, "等待字幕...", "", false, _fontSize, _displayMode, isLatest: true));
        LocationChanged += OnWindowBoundsChanged;
        SizeChanged += OnWindowBoundsChanged;
        IsVisibleChanged += OnWindowIsVisibleChanged;
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _hoverHideTimer.Stop();
            _hoverToolbarVisible = false;
            HoverToolbar.BeginAnimation(OpacityProperty, null);
            HoverToolbar.Opacity = 0;
            HoverToolbar.Visibility = Visibility.Collapsed;
            HoverToolbar.IsHitTestVisible = false;
            HoverBridge.IsHitTestVisible = false;
            UpdateActivity(new SubtitleActivitySnapshot(SubtitleActivityState.Idle, 0, 0));
        }
    }

    public ObservableCollection<FloatingSubtitleEntry> Entries { get; } = [];
    public event Action<IReadOnlyCollection<string>>? VisibleGroupsChanged;
    public event Action<FloatingWindowBounds>? BoundsChanged;
    public event Action<bool>? LockedChanged;
    public event Action<FloatingAppearanceChange>? AppearanceChanged;
    public event Action? HideRequested;
    public event Action? OpenSettingsRequested;

    public bool IsLocked => _locked;
    public SubtitleDisplayMode DisplayMode => _displayMode;
    public SubtitleOverlayTheme OverlayTheme => _theme;
    public FloatingWindowBounds CurrentBounds => new(Left, Top, ActualWidth > 0 ? ActualWidth : Width);

    /// <summary>
    /// 还原上次记录的位置与宽度。仅在窗口显示前调用有效；
    /// 若记录的位置已完全落在可见屏幕之外（如显示器变更），则忽略并保持居中。
    /// </summary>
    public void ApplyWindowBounds(double? left, double? top, double? width)
    {
        _applyingBounds = true;
        try
        {
            if (width is { } savedWidth && !double.IsNaN(savedWidth))
            {
                Width = Math.Max(MinWidth, savedWidth);
            }

            if (left is { } savedLeft && top is { } savedTop &&
                !double.IsNaN(savedLeft) && !double.IsNaN(savedTop))
            {
                var restored = new Rect(savedLeft, savedTop, Math.Max(MinWidth, Width), Math.Max(MinHeight, 84));
                var virtualScreen = new Rect(
                    SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth,
                    SystemParameters.VirtualScreenHeight);
                restored.Intersect(virtualScreen);
                if (restored.Width >= 40 && restored.Height >= 40)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = savedLeft;
                    Top = savedTop;
                }
            }
        }
        finally
        {
            _applyingBounds = false;
        }
    }

    public void ResetPositionToBottomCenter()
    {
        var workArea = GetCurrentScreenWorkArea();
        var targetWidth = Math.Clamp(780d, MinWidth, Math.Max(MinWidth, workArea.Width - 64));
        _applyingBounds = true;
        try
        {
            Width = targetWidth;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = workArea.Left + (workArea.Width - targetWidth) / 2;
            var estimatedHeight = ActualHeight > 0 ? ActualHeight : 140;
            Top = Math.Max(workArea.Top + 24, workArea.Bottom - estimatedHeight - 48);
        }
        finally
        {
            _applyingBounds = false;
        }

        UpdateMaxHeightForCurrentScreen();
        BoundsChanged?.Invoke(CurrentBounds);
    }

    private void OnWindowBoundsChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded || _applyingBounds)
        {
            return;
        }

        UpdateMaxHeightForCurrentScreen();
        BoundsChanged?.Invoke(CurrentBounds);
    }

    public void SetCaption(string text)
    {
        Entries.Clear();
        Entries.Add(new FloatingSubtitleEntry("caption", 1, text, "", false, _fontSize, _displayMode, isLatest: true));
        NotifyVisibleGroupsChanged();
    }

    public void PublishSource(SubtitleItem item, bool translationPending)
    {
        if (string.IsNullOrWhiteSpace(item.UtteranceGroupId) || string.IsNullOrWhiteSpace(item.SourceText))
        {
            return;
        }

        var existing = Entries.FirstOrDefault(entry =>
            string.Equals(entry.UtteranceGroupId, item.UtteranceGroupId, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.ReplaceSource(item.Revision, item.SourceText, translationPending);
        }
        else
        {
            if (Entries.Count == 1 && Entries[0].UtteranceGroupId is "waiting" or "caption")
            {
                Entries.Clear();
            }

            Entries.Add(new FloatingSubtitleEntry(
                item.UtteranceGroupId,
                item.Revision,
                item.SourceText,
                "",
                translationPending,
                _fontSize,
                _displayMode,
                isLatest: true));
        }

        TrimToMaximum();
        UpdateEntryEmphasis();
        ScheduleRenderedHeightTrim();
    }

    public void ApplyTranslation(TranslationResultUpdate update)
    {
        var existing = Entries.FirstOrDefault(entry =>
            string.Equals(entry.UtteranceGroupId, update.UtteranceGroupId, StringComparison.Ordinal) &&
            entry.SourceRevision == update.SourceRevision);
        existing?.SetTranslation(update.TranslatedText);
        ScheduleRenderedHeightTrim();
    }

    public void ClearTranslationPending(TranslationTaskStatusUpdate update)
    {
        var existing = Entries.FirstOrDefault(entry =>
            string.Equals(entry.UtteranceGroupId, update.UtteranceGroupId, StringComparison.Ordinal) &&
            entry.SourceRevision == update.SourceRevision);
        existing?.ClearTranslationPending();
        ScheduleRenderedHeightTrim();
    }

    public void ApplySettings(
        double fontSize,
        int maxSubtitleItems,
        double opacity,
        SubtitleDisplayMode displayMode = SubtitleDisplayMode.BilingualSourceFirst,
        SubtitleOverlayTheme theme = SubtitleOverlayTheme.CinemaGlass)
    {
        _fontSize = Math.Clamp(fontSize, 12, 72);
        _maxSubtitleItems = Math.Clamp(maxSubtitleItems, 1, 3);
        _opacity = Math.Clamp(opacity, 0.0, 0.95);
        _displayMode = displayMode;
        _theme = theme;

        ApplyVisualTheme(_theme, _opacity);
        UpdateHoverToolbarLabels();
        UpdateMaxHeightForCurrentScreen();

        foreach (var entry in Entries)
        {
            entry.ApplyPresentation(_fontSize, _displayMode);
        }

        TrimToMaximum();
        UpdateEntryEmphasis();
        ScheduleRenderedHeightTrim();
    }

    public void UpdateActivity(SubtitleActivitySnapshot snapshot)
    {
        if (!IsVisible)
        {
            if (_activityState != SubtitleActivityState.Idle || _activityTrackShown)
            {
                _activityState = SubtitleActivityState.Idle;
                _activityTrackShown = false;
                ActivityBar.BeginAnimation(OpacityProperty, null);
                ActivityBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ActivityTrack.BeginAnimation(OpacityProperty, null);
                ActivityTrack.Opacity = 0;
                ActivityTrack.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var previousState = _activityState;
        _activityState = snapshot.State;

        switch (snapshot.State)
        {
            case SubtitleActivityState.ListeningSpeech:
                if (previousState != SubtitleActivityState.ListeningSpeech)
                {
                    ActivityBar.Background = ListeningActivityBrush;
                    ActivityBar.BeginAnimation(OpacityProperty, null);
                    ActivityBar.Opacity = 1;
                }

                var targetScale = Math.Clamp(0.2d + Math.Clamp(snapshot.AudioLevel, 0, 100) / 100d * 0.8d, 0.2d, 1.0d);
                if (previousState != SubtitleActivityState.ListeningSpeech ||
                    Math.Abs(targetScale - _lastActivityScale) >= 0.04d)
                {
                    _lastActivityScale = targetScale;
                    var scaleAnim = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(110))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    Timeline.SetDesiredFrameRate(scaleAnim, 24);
                    ActivityBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                }

                if (!_activityTrackShown)
                {
                    AnimateTrackVisibility(show: true, targetOpacity: 0.95, durationMs: 120);
                }
                break;

            case SubtitleActivityState.Processing:
                if (previousState != SubtitleActivityState.Processing)
                {
                    ActivityBar.Background = ProcessingActivityBrush;
                    ActivityBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    ActivityBarScale.ScaleX = 0.42d;
                    _lastActivityScale = 0.42d;
                    var pulse = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(650))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Timeline.SetDesiredFrameRate(pulse, 24);
                    ActivityBar.BeginAnimation(OpacityProperty, pulse);
                }

                if (!_activityTrackShown)
                {
                    AnimateTrackVisibility(show: true, targetOpacity: 0.9, durationMs: 140);
                }
                break;

            default:
                // 静默状态下若已收起，直接返回，避免每 90ms 反复启动冗余动画
                if (previousState == SubtitleActivityState.Idle && !_activityTrackShown)
                {
                    return;
                }

                ActivityBar.BeginAnimation(OpacityProperty, null);
                ActivityBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ActivityBar.Opacity = 0.85;
                ActivityBarScale.ScaleX = 0.2d;
                _lastActivityScale = 0.2d;
                AnimateTrackVisibility(show: false, targetOpacity: 0, durationMs: 240);
                break;
        }
    }

    public void ToggleLocked()
    {
        SetLocked(!_locked);
    }

    private void AnimateTrackVisibility(bool show, double targetOpacity, int durationMs)
    {
        _activityTrackShown = show;
        if (show)
        {
            ActivityTrack.Visibility = Visibility.Visible;
        }

        var animation = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, 24);
        if (!show)
        {
            animation.Completed += (_, _) =>
            {
                if (!_activityTrackShown)
                {
                    ActivityTrack.Visibility = Visibility.Collapsed;
                }
            };
        }

        ActivityTrack.BeginAnimation(OpacityProperty, animation);
    }

    private void ApplyVisualTheme(SubtitleOverlayTheme theme, double userOpacity)
    {
        switch (theme)
        {
            case SubtitleOverlayTheme.HighContrast:
                ChromeBackgroundBrush.Color = Color.FromRgb(0x09, 0x0B, 0x0F);
                ChromeBackgroundBrush.Opacity = Math.Max(userOpacity, 0.86);
                ChromeBorderTopStop.Color = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
                ChromeBorderBottomStop.Color = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
                ChromeShadowEffect.Opacity = 0.62;
                ChromeShadowEffect.BlurRadius = 22;
                Resources["FloatingTextShadowEffect"] = CreateTextShadow(blurRadius: 6, shadowDepth: 1.5, opacity: 0.95);
                break;

            case SubtitleOverlayTheme.MinimalFloating:
                ChromeBackgroundBrush.Color = Color.FromRgb(0x0B, 0x10, 0x17);
                // 极简浮字模式默认大幅压低底板不透明度，方便全屏视频/游戏阅读
                ChromeBackgroundBrush.Opacity = Math.Min(userOpacity, 0.22);
                ChromeBorderTopStop.Color = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
                ChromeBorderBottomStop.Color = Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF);
                ChromeShadowEffect.Opacity = ChromeBackgroundBrush.Opacity > 0.04 ? 0.22 : 0.0;
                ChromeShadowEffect.BlurRadius = 10;
                Resources["FloatingTextShadowEffect"] = CreateTextShadow(blurRadius: 7, shadowDepth: 1.5, opacity: 1.0);
                break;

            default:
                ChromeBackgroundBrush.Color = Color.FromRgb(0x0F, 0x14, 0x1B);
                ChromeBackgroundBrush.Opacity = userOpacity;
                ChromeBorderTopStop.Color = Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF);
                ChromeBorderBottomStop.Color = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
                ChromeShadowEffect.Opacity = userOpacity > 0.05 ? 0.46 : 0.0;
                ChromeShadowEffect.BlurRadius = 20;
                Resources["FloatingTextShadowEffect"] = CreateTextShadow(blurRadius: 5, shadowDepth: 1, opacity: 0.88);
                break;
        }
    }

    private static DropShadowEffect CreateTextShadow(double blurRadius, double shadowDepth, double opacity)
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

    private void UpdateHoverToolbarLabels()
    {
        PillFontSizeText.Text = $"{Math.Round(_fontSize):0}";
        PillDisplayModeButton.Content = _displayMode switch
        {
            SubtitleDisplayMode.BilingualTranslationFirst => "双语·译文",
            SubtitleDisplayMode.TranslationOnly => "仅译文",
            SubtitleDisplayMode.SourceOnly => "仅原文",
            _ => "双语·原文"
        };
        PillThemeButton.Content = _theme switch
        {
            SubtitleOverlayTheme.HighContrast => "高对比",
            SubtitleOverlayTheme.MinimalFloating => "极简浮字",
            _ => "暗玻璃"
        };
    }

    private void SyncContextMenuState()
    {
        MenuLockItem.Header = _locked ? "解锁悬浮窗" : "锁定悬浮窗（点击穿透）";
        MenuModeTranslationFirst.IsChecked = _displayMode == SubtitleDisplayMode.BilingualTranslationFirst;
        MenuModeSourceFirst.IsChecked = _displayMode == SubtitleDisplayMode.BilingualSourceFirst;
        MenuModeTranslationOnly.IsChecked = _displayMode == SubtitleDisplayMode.TranslationOnly;
        MenuModeSourceOnly.IsChecked = _displayMode == SubtitleDisplayMode.SourceOnly;
        MenuThemeCinemaGlass.IsChecked = _theme == SubtitleOverlayTheme.CinemaGlass;
        MenuThemeHighContrast.IsChecked = _theme == SubtitleOverlayTheme.HighContrast;
        MenuThemeMinimalFloating.IsChecked = _theme == SubtitleOverlayTheme.MinimalFloating;
    }

    private void UpdateMaxHeightForCurrentScreen()
    {
        var currentWorkArea = GetCurrentScreenWorkArea();
        // 保持字幕主面板在当前屏幕工作区高度的 40% 以内（WorkArea.Height * 0.4），并为顶部悬停胶囊预留 34px 空间。
        MaxHeight = Math.Max(148, currentWorkArea.Height * 0.4 + 34);
    }

    private Rect GetCurrentScreenWorkArea()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var screen = System.Windows.Forms.Screen.FromHandle(handle);
                var source = PresentationSource.FromVisual(this);
                var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var topLeft = transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
                var bottomRight = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
                var width = bottomRight.X - topLeft.X;
                var height = bottomRight.Y - topLeft.Y;
                if (width > 100 && height > 100)
                {
                    return new Rect(topLeft, bottomRight);
                }
            }
        }
        catch
        {
            // Fallback to primary screen WorkArea when handle/screen is unavailable.
        }

        return SystemParameters.WorkArea;
    }

    private void TrimToMaximum()
    {
        while (Entries.Count > _maxSubtitleItems)
        {
            Entries.RemoveAt(0);
        }
    }

    private void UpdateEntryEmphasis()
    {
        var count = Entries.Count;
        for (var i = 0; i < count; i++)
        {
            var distanceFromLatest = count - 1 - i;
            Entries[i].SetRecency(distanceFromLatest);
        }
    }

    private void ScheduleRenderedHeightTrim()
    {
        if (_trimScheduled || MaxHeight <= 0)
        {
            return;
        }

        _trimScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _trimScheduled = false;
            TrimToRenderedHeight();
            UpdateEntryEmphasis();
            NotifyVisibleGroupsChanged();
        }));
    }

    private void TrimToRenderedHeight()
    {
        if (Entries.Count <= 1 || MaxHeight <= 0)
        {
            return;
        }

        var chromeBudget = Math.Max(110, MaxHeight - 36);
        var width = Math.Max(ActualWidth > 0 ? ActualWidth : Width, MinWidth);
        while (Entries.Count > 1)
        {
            Chrome.Measure(new Size(width, double.PositiveInfinity));
            if (Chrome.DesiredSize.Height <= chromeBudget)
            {
                break;
            }
            Entries.RemoveAt(0);
        }
    }

    private void NotifyVisibleGroupsChanged()
    {
        VisibleGroupsChanged?.Invoke(
            Entries
                .Where(entry => entry.UtteranceGroupId is not "waiting" and not "caption")
                .Select(entry => entry.UtteranceGroupId)
                .ToArray());
    }

    private void SetLocked(bool locked)
    {
        _locked = locked;
        if (locked)
        {
            SetHoverToolbarVisible(false);
        }
        LeftResizeThumb.IsHitTestVisible = !locked;
        RightResizeThumb.IsHitTestVisible = !locked;
        SetClickThrough(locked);
        ShowLockHint(locked);
        LockedChanged?.Invoke(locked);
    }

    private void ShowLockHint(bool locked)
    {
        LockHintText.Text = locked
            ? "已锁定 · 鼠标点击将穿透（Ctrl+Alt+L 解锁）"
            : "已解锁 · 可拖动移动、悬停微调或右键菜单";
        LockHint.Visibility = Visibility.Visible;
        var fadeOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(1600),
            Duration = TimeSpan.FromMilliseconds(400),
            FillBehavior = FillBehavior.HoldEnd
        };
        fadeOut.Completed += (_, _) => LockHint.Visibility = Visibility.Collapsed;
        LockHint.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void SetHoverToolbarVisible(bool visible)
    {
        var shouldShow = visible && !_locked;
        if (shouldShow == _hoverToolbarVisible)
        {
            return;
        }

        _hoverToolbarVisible = shouldShow;
        HoverToolbar.IsHitTestVisible = shouldShow;
        HoverBridge.IsHitTestVisible = shouldShow;
        if (shouldShow)
        {
            HoverToolbar.Visibility = Visibility.Visible;
        }

        var animation = new DoubleAnimation
        {
            To = shouldShow ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(shouldShow ? 160 : 220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        if (!shouldShow)
        {
            animation.Completed += (_, _) =>
            {
                if (!_hoverToolbarVisible)
                {
                    HoverToolbar.Visibility = Visibility.Collapsed;
                }
            };
        }

        HoverToolbar.BeginAnimation(OpacityProperty, animation);
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        _hoverHideTimer.Stop();
        SetHoverToolbarVisible(true);
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (_contextMenuOpen)
        {
            return;
        }

        _hoverHideTimer.Stop();
        _hoverHideTimer.Start();
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = true;
        _hoverHideTimer.Stop();
        SyncContextMenuState();
    }

    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = false;
        if (!IsMouseOver)
        {
            _hoverHideTimer.Start();
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // 点击胶囊栏里的按钮或左右缩放手柄时不触发整窗拖拽
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<ButtonBase>(source) is not null || FindAncestor<Thumb>(source) is not null))
        {
            return;
        }

        DragMove();
    }

    private void OnLeftResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        var workArea = GetCurrentScreenWorkArea();
        var maxWidth = Math.Max(MinWidth, workArea.Width - 32);
        var currentWidth = ActualWidth > 0 ? ActualWidth : Width;
        var proposedWidth = Math.Clamp(currentWidth - e.HorizontalChange, MinWidth, maxWidth);
        var actualDelta = currentWidth - proposedWidth;
        Width = proposedWidth;
        Left += actualDelta;
    }

    private void OnRightResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        var workArea = GetCurrentScreenWorkArea();
        var maxWidth = Math.Max(MinWidth, workArea.Width - 32);
        var currentWidth = ActualWidth > 0 ? ActualWidth : Width;
        Width = Math.Clamp(currentWidth + e.HorizontalChange, MinWidth, maxWidth);
    }

    private void OnIncreaseFontClick(object sender, RoutedEventArgs e)
    {
        EmitAppearanceChange(fontSize: Math.Clamp(_fontSize + 2, 12, 72));
    }

    private void OnDecreaseFontClick(object sender, RoutedEventArgs e)
    {
        EmitAppearanceChange(fontSize: Math.Clamp(_fontSize - 2, 12, 72));
    }

    private void OnCycleDisplayModeClick(object sender, RoutedEventArgs e)
    {
        var next = _displayMode switch
        {
            SubtitleDisplayMode.BilingualSourceFirst => SubtitleDisplayMode.BilingualTranslationFirst,
            SubtitleDisplayMode.BilingualTranslationFirst => SubtitleDisplayMode.TranslationOnly,
            SubtitleDisplayMode.TranslationOnly => SubtitleDisplayMode.SourceOnly,
            _ => SubtitleDisplayMode.BilingualSourceFirst
        };
        EmitAppearanceChange(displayMode: next);
    }

    private void OnCycleThemeClick(object sender, RoutedEventArgs e)
    {
        var next = _theme switch
        {
            SubtitleOverlayTheme.CinemaGlass => SubtitleOverlayTheme.HighContrast,
            SubtitleOverlayTheme.HighContrast => SubtitleOverlayTheme.MinimalFloating,
            _ => SubtitleOverlayTheme.CinemaGlass
        };
        EmitAppearanceChange(theme: next);
    }

    private void OnDisplayModeMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<SubtitleDisplayMode>(tag, out var mode))
        {
            EmitAppearanceChange(displayMode: mode);
        }
    }

    private void OnThemeMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<SubtitleOverlayTheme>(tag, out var theme))
        {
            EmitAppearanceChange(theme: theme);
        }
    }

    private void OnLockButtonClick(object sender, RoutedEventArgs e)
    {
        ToggleLocked();
    }

    private void OnHideButtonClick(object sender, RoutedEventArgs e)
    {
        HideRequested?.Invoke();
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        ResetPositionToBottomCenter();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke();
    }

    private void EmitAppearanceChange(
        double? fontSize = null,
        SubtitleDisplayMode? displayMode = null,
        SubtitleOverlayTheme? theme = null)
    {
        var nextFont = fontSize ?? _fontSize;
        var nextMode = displayMode ?? _displayMode;
        var nextTheme = theme ?? _theme;
        ApplySettings(nextFont, _maxSubtitleItems, _opacity, nextMode, nextTheme);
        AppearanceChanged?.Invoke(new FloatingAppearanceChange(nextFont, nextMode, nextTheme));
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    internal static Brush ResolveSourcePrimaryBrush() => SourcePrimaryBrush;
    internal static Brush ResolveSourceSecondaryBrush() => SourceSecondaryBrush;
    internal static Brush ResolveTranslationPrimaryBrush() => TranslationPrimaryBrush;
    internal static Brush ResolveTranslationSecondaryBrush() => TranslationSecondaryBrush;
    internal static Brush ResolvePendingTranslationBrush() => PendingTranslationBrush;
}

public sealed class FloatingSubtitleEntry : INotifyPropertyChanged
{
    private string _sourceText;
    private string _translatedText;
    private bool _translationPending;
    private double _baseFontSize;
    private SubtitleDisplayMode _displayMode;
    private double _emphasisOpacity = 1.0;

    public FloatingSubtitleEntry(
        string utteranceGroupId,
        int sourceRevision,
        string sourceText,
        string translatedText,
        bool translationPending,
        double baseFontSize = 18d,
        SubtitleDisplayMode displayMode = SubtitleDisplayMode.BilingualSourceFirst,
        bool isLatest = true)
    {
        UtteranceGroupId = utteranceGroupId;
        SourceRevision = sourceRevision;
        _sourceText = sourceText;
        _translatedText = translatedText;
        _translationPending = translationPending;
        _baseFontSize = Math.Clamp(baseFontSize, 12, 72);
        _displayMode = displayMode;
        _emphasisOpacity = isLatest ? 1.0 : 0.62;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string UtteranceGroupId { get; }
    public int SourceRevision { get; private set; }
    public string SourceText => _sourceText;
    public string TranslatedText => _translatedText;
    public double EmphasisOpacity => _emphasisOpacity;

    /// <summary>翻译进行中显示轻量占位点，避免译文到达时布局跳动且不显示空行。</summary>
    public string DisplayTranslatedText =>
        _translationPending && string.IsNullOrWhiteSpace(_translatedText) ? "···" : _translatedText;

    private bool HasTranslationLine => _translationPending || !string.IsNullOrWhiteSpace(_translatedText);
    private bool IsPlaceholderEntry => UtteranceGroupId is "waiting" or "caption";

    public string PrimaryLineText
    {
        get
        {
            if (IsPlaceholderEntry)
            {
                return _sourceText;
            }

            return _displayMode switch
            {
                SubtitleDisplayMode.BilingualTranslationFirst => HasTranslationLine ? DisplayTranslatedText : _sourceText,
                SubtitleDisplayMode.TranslationOnly => HasTranslationLine ? DisplayTranslatedText : _sourceText,
                _ => _sourceText
            };
        }
    }

    public Visibility PrimaryLineVisibility =>
        string.IsNullOrWhiteSpace(PrimaryLineText) ? Visibility.Collapsed : Visibility.Visible;

    public double PrimaryLineFontSize => _baseFontSize;

    public FontWeight PrimaryLineFontWeight => FontWeights.SemiBold;

    public Brush PrimaryLineBrush
    {
        get
        {
            if (IsPlaceholderEntry)
            {
                return FloatingSubtitleWindow.ResolveSourceSecondaryBrush();
            }

            var showingTranslation =
                (_displayMode is SubtitleDisplayMode.BilingualTranslationFirst or SubtitleDisplayMode.TranslationOnly) &&
                HasTranslationLine;
            if (!showingTranslation)
            {
                return FloatingSubtitleWindow.ResolveSourcePrimaryBrush();
            }

            return _translationPending && string.IsNullOrWhiteSpace(_translatedText)
                ? FloatingSubtitleWindow.ResolvePendingTranslationBrush()
                : FloatingSubtitleWindow.ResolveTranslationPrimaryBrush();
        }
    }

    public string SecondaryLineText
    {
        get
        {
            if (IsPlaceholderEntry)
            {
                return "";
            }

            return _displayMode switch
            {
                SubtitleDisplayMode.BilingualSourceFirst => HasTranslationLine ? DisplayTranslatedText : "",
                SubtitleDisplayMode.BilingualTranslationFirst => HasTranslationLine ? _sourceText : "",
                _ => ""
            };
        }
    }

    public Visibility SecondaryLineVisibility =>
        string.IsNullOrWhiteSpace(SecondaryLineText) ? Visibility.Collapsed : Visibility.Visible;

    public double SecondaryLineFontSize =>
        _displayMode == SubtitleDisplayMode.BilingualTranslationFirst
            ? Math.Max(11, Math.Round(_baseFontSize * 0.82, 1))
            : Math.Max(12, Math.Round(_baseFontSize * 0.9, 1));

    public FontWeight SecondaryLineFontWeight => FontWeights.Normal;

    public Brush SecondaryLineBrush
    {
        get
        {
            if (_displayMode == SubtitleDisplayMode.BilingualTranslationFirst)
            {
                return FloatingSubtitleWindow.ResolveSourceSecondaryBrush();
            }

            return _translationPending && string.IsNullOrWhiteSpace(_translatedText)
                ? FloatingSubtitleWindow.ResolvePendingTranslationBrush()
                : FloatingSubtitleWindow.ResolveTranslationSecondaryBrush();
        }
    }

    public void SetRecency(int distanceFromLatest)
    {
        var next = distanceFromLatest switch
        {
            <= 0 => 1.0,
            1 => 0.62,
            _ => 0.44
        };
        if (Math.Abs(_emphasisOpacity - next) > 0.001)
        {
            _emphasisOpacity = next;
            OnPropertyChanged(nameof(EmphasisOpacity));
        }
    }

    public void ApplyPresentation(double baseFontSize, SubtitleDisplayMode displayMode)
    {
        _baseFontSize = Math.Clamp(baseFontSize, 12, 72);
        _displayMode = displayMode;
        NotifyPresentationChanged();
    }

    public void ReplaceSource(int revision, string text, bool translationPending)
    {
        SourceRevision = revision;
        _sourceText = text;
        _translatedText = "";
        _translationPending = translationPending;
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(TranslatedText));
        OnPropertyChanged(nameof(DisplayTranslatedText));
        NotifyPresentationChanged();
    }

    public void SetTranslation(string text)
    {
        _translatedText = text;
        _translationPending = false;
        OnPropertyChanged(nameof(TranslatedText));
        OnPropertyChanged(nameof(DisplayTranslatedText));
        NotifyPresentationChanged();
    }

    public void ClearTranslationPending()
    {
        _translationPending = false;
        OnPropertyChanged(nameof(DisplayTranslatedText));
        NotifyPresentationChanged();
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(PrimaryLineText));
        OnPropertyChanged(nameof(PrimaryLineVisibility));
        OnPropertyChanged(nameof(PrimaryLineFontSize));
        OnPropertyChanged(nameof(PrimaryLineFontWeight));
        OnPropertyChanged(nameof(PrimaryLineBrush));
        OnPropertyChanged(nameof(SecondaryLineText));
        OnPropertyChanged(nameof(SecondaryLineVisibility));
        OnPropertyChanged(nameof(SecondaryLineFontSize));
        OnPropertyChanged(nameof(SecondaryLineFontWeight));
        OnPropertyChanged(nameof(SecondaryLineBrush));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public readonly record struct FloatingWindowBounds(double Left, double Top, double Width);

public readonly record struct FloatingAppearanceChange(
    double FontSize,
    SubtitleDisplayMode DisplayMode,
    SubtitleOverlayTheme Theme);
