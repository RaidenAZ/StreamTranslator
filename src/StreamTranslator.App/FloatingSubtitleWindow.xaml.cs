using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
    private int _maxSubtitleItems = 2;

    public FloatingSubtitleWindow()
    {
        InitializeComponent();
        Resources["FloatingSourceFontSize"] = 18d;
        Resources["FloatingTranslationFontSize"] = 16.2d;
        Entries.Add(new FloatingSubtitleEntry("waiting", 1, "等待字幕...", "", false));
    }

    public ObservableCollection<FloatingSubtitleEntry> Entries { get; } = [];
    public event Action<IReadOnlyCollection<string>>? VisibleGroupsChanged;

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
        Chrome.Opacity = Math.Clamp(opacity, 0.35, 0.95);
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
        OnPropertyChanged(nameof(TranslationVisibility));
    }

    public void SetTranslation(string text)
    {
        _translatedText = text;
        _translationPending = false;
        OnPropertyChanged(nameof(TranslatedText));
        OnPropertyChanged(nameof(TranslationVisibility));
    }

    public void ClearTranslationPending()
    {
        _translationPending = false;
        OnPropertyChanged(nameof(TranslationVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
