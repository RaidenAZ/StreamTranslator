using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace StreamTranslator.App;

internal enum TrayStatus
{
    Ready,
    Starting,
    Running,
    Error
}

/// <summary>
/// 系统托盘图标管理器：提供实时状态角标（绿=监听中、琥珀=启动中、灰=就绪、红=异常）、
/// 左键快速呼出/隐藏主窗，以及右键常用控制菜单，并在退出时完整释放 GDI 图标句柄。
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _toggleWindowItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _startStopItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _toggleFloatingItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _toggleLockItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _exitItem;
    private readonly Dictionary<TrayStatus, System.Drawing.Icon> _icons = new();
    private TrayStatus _currentStatus = TrayStatus.Ready;
    private bool _balloonShown;
    private bool _disposed;

    public event Action? ToggleMainWindowRequested;
    public event Action? ToggleStartStopRequested;
    public event Action? ToggleFloatingWindowRequested;
    public event Action? ToggleFloatingLockRequested;
    public event Action? ExitRequested;

    public TrayIconManager()
    {
        _menu = new System.Windows.Forms.ContextMenuStrip();

        _toggleWindowItem = new System.Windows.Forms.ToolStripMenuItem("隐藏主窗口")
        {
            Font = new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold)
        };
        _toggleWindowItem.Click += (_, _) => ToggleMainWindowRequested?.Invoke();

        _startStopItem = new System.Windows.Forms.ToolStripMenuItem("开始字幕");
        _startStopItem.Click += (_, _) => ToggleStartStopRequested?.Invoke();

        _toggleFloatingItem = new System.Windows.Forms.ToolStripMenuItem("显示悬浮窗");
        _toggleFloatingItem.Click += (_, _) => ToggleFloatingWindowRequested?.Invoke();

        _toggleLockItem = new System.Windows.Forms.ToolStripMenuItem("锁定悬浮窗");
        _toggleLockItem.Click += (_, _) => ToggleFloatingLockRequested?.Invoke();

        _exitItem = new System.Windows.Forms.ToolStripMenuItem("退出 StreamTranslator");
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(_toggleWindowItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(_startStopItem);
        _menu.Items.Add(_toggleFloatingItem);
        _menu.Items.Add(_toggleLockItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "StreamTranslator · 就绪",
            Icon = GetOrCreateIcon(TrayStatus.Ready),
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                ToggleMainWindowRequested?.Invoke();
            }
        };
    }

    public void UpdateStatus(TrayStatus status)
    {
        if (_disposed || _currentStatus == status)
        {
            return;
        }

        _currentStatus = status;
        _notifyIcon.Icon = GetOrCreateIcon(status);
        _notifyIcon.Text = status switch
        {
            TrayStatus.Running => "StreamTranslator · 监听中",
            TrayStatus.Starting => "StreamTranslator · 启动中",
            TrayStatus.Error => "StreamTranslator · 运行异常",
            _ => "StreamTranslator · 就绪"
        };
    }

    public void UpdateMenuState(
        bool isMainWindowVisible,
        bool isRunning,
        bool isTransitioning,
        bool isFloatingVisible,
        bool isFloatingLocked)
    {
        if (_disposed)
        {
            return;
        }

        _toggleWindowItem.Text = isMainWindowVisible ? "隐藏主窗口" : "打开主窗口";
        _startStopItem.Text = isTransitioning
            ? (isRunning ? "停止中…" : "启动中…")
            : (isRunning ? "停止字幕" : "开始字幕");
        _startStopItem.Enabled = !isTransitioning;
        _toggleFloatingItem.Text = isFloatingVisible ? "隐藏悬浮窗" : "显示悬浮窗";
        _toggleLockItem.Text = isFloatingLocked ? "解锁悬浮窗" : "锁定悬浮窗";
    }

    public void ShowMinimizedBalloonOnce()
    {
        if (_disposed || _balloonShown)
        {
            return;
        }

        _balloonShown = true;
        _notifyIcon.ShowBalloonTip(
            2600,
            "StreamTranslator 已收入系统托盘",
            "单击托盘图标可快速恢复主窗口，右键可启停字幕或退出程序。",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private System.Drawing.Icon GetOrCreateIcon(TrayStatus status)
    {
        if (_icons.TryGetValue(status, out var existing))
        {
            return existing;
        }

        var created = CreateStatusIcon(status);
        _icons[status] = created;
        return created;
    }

    private static System.Drawing.Icon CreateStatusIcon(TrayStatus status)
    {
        const int size = 32;
        using var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(System.Drawing.Color.Transparent);

            // 深青蓝圆角底块
            using var tilePath = CreateRoundedRectPath(new System.Drawing.RectangleF(1.5f, 1.5f, 29f, 29f), 7f);
            using var tileBrush = new LinearGradientBrush(
                new System.Drawing.PointF(0, 0),
                new System.Drawing.PointF(size, size),
                System.Drawing.Color.FromArgb(0xFF, 0x16, 0x32, 0x47),
                System.Drawing.Color.FromArgb(0xFF, 0x0E, 0x1D, 0x2B));
            g.FillPath(tileBrush, tilePath);

            using var rimPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), 1f);
            g.DrawPath(rimPen, tilePath);

            // 两条错落双语字幕横条（上：亮白原文条，下：暖金译文条）
            using var primaryBar = CreateRoundedRectPath(new System.Drawing.RectangleF(6.5f, 10.5f, 18f, 3.6f), 1.8f);
            using var whiteBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
            g.FillPath(whiteBrush, primaryBar);

            using var secondaryBar = CreateRoundedRectPath(new System.Drawing.RectangleF(6.5f, 17.5f, 13.5f, 3.6f), 1.8f);
            using var goldBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xFF, 0xFF, 0xE2, 0x99));
            g.FillPath(goldBrush, secondaryBar);

            // 右下角状态角标（深色描边环 + 状态实心点）
            var badgeColor = status switch
            {
                TrayStatus.Running => System.Drawing.Color.FromArgb(0xFF, 0x2D, 0xD4, 0xA3),
                TrayStatus.Starting => System.Drawing.Color.FromArgb(0xFF, 0xF5, 0xB0, 0x41),
                TrayStatus.Error => System.Drawing.Color.FromArgb(0xFF, 0xF8, 0x71, 0x71),
                _ => System.Drawing.Color.FromArgb(0xFF, 0x8C, 0x97, 0xA6)
            };

            using var ringBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xFF, 0x0E, 0x1D, 0x2B));
            g.FillEllipse(ringBrush, 19.5f, 19.5f, 11.5f, 11.5f);

            using var dotBrush = new System.Drawing.SolidBrush(badgeColor);
            g.FillEllipse(dotBrush, 21.5f, 21.5f, 7.5f, 7.5f);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var raw = System.Drawing.Icon.FromHandle(hIcon);
            return (System.Drawing.Icon)raw.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRectPath(System.Drawing.RectangleF rect, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon = null;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
        _icons.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
