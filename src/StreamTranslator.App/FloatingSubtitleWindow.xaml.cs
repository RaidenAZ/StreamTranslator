using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StreamTranslator.Core.Subtitles;
using StreamTranslator.Core.Translation;

namespace StreamTranslator.App;

public partial class FloatingSubtitleWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private bool _locked;
    private bool _trimScheduled;
    private bool _applyingBounds;
    private int _maxSubtitleItems = 2;

    public FloatingSubtitleWindow()
    {
        InitializeComponent();
        Resources["FloatingSourceFontSize"] = 18d;
        Resources["FloatingTranslationFontSize"] = 16.2d;
        Entries.Add(new FloatingSubtitleEntry("waiting", 1, "等待字幕...", "", false));
        LocationChanged += OnWindowBoundsChanged;
        SizeChanged += OnWindowBoundsChanged;
    }

    public ObservableCollection<FloatingSubtitleEntry> Entries { get; } = [];
    public event Action<IReadOnlyCollection<string>>? VisibleGroupsChanged;
    public event Action<FloatingWindowBounds>? BoundsChanged;
    public event Action<bool>? LockedChanged;

    public bool IsLocked => _locked;

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
                var restored = new Rect(savedLeft, savedTop, Math.Max(MinWidth, Width), Math.Max(MinHeight, 76));
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

    private void OnWindowBoundsChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded || _applyingBounds)
        {
            return;
        }

        BoundsChanged?.Invoke(CurrentBounds);
    }

    public void SetCaption(string text)
    {
        Entries.Clear();
        Entries.Add(new FloatingSubtitleEntry("caption", 1, text, "", false));
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
                translationPending));
        }

        TrimToMaximum();
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

    public void ApplySettings(double fontSize, int maxSubtitleItems, double opacity)
    {
        var normalizedFontSize = Math.Clamp(fontSize, 12, 72);
        Resources["FloatingSourceFontSize"] = normalizedFontSize;
        Resources["FloatingTranslationFontSize"] = Math.Round(normalizedFontSize * 0.9, 1);
        _maxSubtitleItems = Math.Clamp(maxSubtitleItems, 1, 3);
        // Opacity applies to the backdrop brush only, so subtitle text stays fully opaque and readable.
        ChromeBackgroundBrush.Opacity = Math.Clamp(opacity, 0.35, 0.95);
        MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height * 0.4);
        TrimToMaximum();
        ScheduleRenderedHeightTrim();
    }

    public void ToggleLocked()
    {
        SetLocked(!_locked);
    }

    private void TrimToMaximum()
    {
        while (Entries.Count > _maxSubtitleItems)
        {
            Entries.RemoveAt(0);
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
            NotifyVisibleGroupsChanged();
        }));
    }

    private void TrimToRenderedHeight()
    {
        if (Entries.Count <= 1 || MaxHeight <= 0)
        {
            return;
        }

        var width = Math.Max(ActualWidth > 0 ? ActualWidth : Width, MinWidth);
        while (Entries.Count > 1)
        {
            Chrome.Measure(new Size(width, double.PositiveInfinity));
            if (Chrome.DesiredSize.Height <= MaxHeight)
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
        SetClickThrough(locked);
        ShowLockHint(locked);
        LockedChanged?.Invoke(locked);
    }

    private void ShowLockHint(bool locked)
    {
        LockHintText.Text = locked
            ? "已锁定 · 鼠标点击将穿透（Ctrl+Alt+L 解锁）"
            : "已解锁，可拖动调整位置";
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

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_locked && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(handle, GwlExStyle, style);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

public sealed class FloatingSubtitleEntry : INotifyPropertyChanged
{
    private string _sourceText;
    private string _translatedText;
    private bool _translationPending;

    public FloatingSubtitleEntry(
        string utteranceGroupId,
        int sourceRevision,
        string sourceText,
        string translatedText,
        bool translationPending)
    {
        UtteranceGroupId = utteranceGroupId;
        SourceRevision = sourceRevision;
        _sourceText = sourceText;
        _translatedText = translatedText;
        _translationPending = translationPending;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string UtteranceGroupId { get; }
    public int SourceRevision { get; private set; }
    public string SourceText => _sourceText;
    public string TranslatedText => _translatedText;

    /// <summary>翻译进行中显示占位省略号，避免译文到达时布局跳动且不显示空行。</summary>
    public string DisplayTranslatedText =>
        _translationPending && string.IsNullOrWhiteSpace(_translatedText) ? "…" : _translatedText;

    public Visibility TranslationVisibility =>
        _translationPending || !string.IsNullOrWhiteSpace(_translatedText)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public void ReplaceSource(int revision, string text, bool translationPending)
    {
        SourceRevision = revision;
        _sourceText = text;
        _translatedText = "";
        _translationPending = translationPending;
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(TranslatedText));
        OnPropertyChanged(nameof(DisplayTranslatedText));
        OnPropertyChanged(nameof(TranslationVisibility));
    }

    public void SetTranslation(string text)
    {
        _translatedText = text;
        _translationPending = false;
        OnPropertyChanged(nameof(TranslatedText));
        OnPropertyChanged(nameof(DisplayTranslatedText));
        OnPropertyChanged(nameof(TranslationVisibility));
    }

    public void ClearTranslationPending()
    {
        _translationPending = false;
        OnPropertyChanged(nameof(DisplayTranslatedText));
        OnPropertyChanged(nameof(TranslationVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public readonly record struct FloatingWindowBounds(double Left, double Top, double Width);
