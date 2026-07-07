using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace StreamTranslator.App;

public partial class FloatingSubtitleWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private bool _locked;

    public FloatingSubtitleWindow()
    {
        InitializeComponent();
    }

    public void SetCaption(string text)
    {
        CaptionText.Text = text;
    }

    public void ApplySettings(double fontSize, int maxLines, double opacity)
    {
        CaptionText.FontSize = fontSize;
        CaptionText.LineHeight = Math.Round(fontSize * 1.28);
        CaptionText.MaxHeight = CaptionText.LineHeight * Math.Clamp(maxLines, 1, 3);
        Chrome.Opacity = Math.Clamp(opacity, 0.35, 0.95);
    }

    public void ToggleLocked()
    {
        SetLocked(!_locked);
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
