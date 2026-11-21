using System.Windows;
using System.Windows.Media;

namespace StreamTranslator.App.Controls;

/// <summary>
/// 分段式声学 LED 电平仪，用于在主页演播控台实时呈现系统输出音量。
/// 采用快上升、平滑回落的连续亮格响应（不绘制孤立悬空峰值块），
/// 并在亮格数未变化时跳过重绘以降低低配置设备上的渲染开销。
/// </summary>
public sealed class SegmentedLevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(SegmentedLevelMeter),
        new FrameworkPropertyMetadata(0d, OnLevelChanged));

    public static readonly DependencyProperty SegmentCountProperty = DependencyProperty.Register(
        nameof(SegmentCount),
        typeof(int),
        typeof(SegmentedLevelMeter),
        new FrameworkPropertyMetadata(28, FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentCountChanged));

    public static readonly DependencyProperty IsSpeechActiveProperty = DependencyProperty.Register(
        nameof(IsSpeechActive),
        typeof(bool),
        typeof(SegmentedLevelMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InactiveBrushProperty = DependencyProperty.Register(
        nameof(InactiveBrush),
        typeof(Brush),
        typeof(SegmentedLevelMeter),
        new FrameworkPropertyMetadata(CreateFrozenBrush(Color.FromArgb(0x28, 0x80, 0x88, 0x94)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NormalBrushProperty = DependencyProperty.Register(
        nameof(NormalBrush),
        typeof(Brush),
        typeof(SegmentedLevelMeter),
        new FrameworkPropertyMetadata(CreateFrozenBrush(Color.FromRgb(0x0F, 0x9D, 0x76)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarmBrushProperty = DependencyProperty.Register(
        nameof(WarmBrush),
        typeof(Brush),
        typeof(SegmentedLevelMeter),
        new FrameworkPropertyMetadata(CreateFrozenBrush(Color.FromRgb(0xD9, 0x77, 0x06)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakBrushProperty = DependencyProperty.Register(
        nameof(PeakBrush),
        typeof(Brush),
        typeof(SegmentedLevelMeter),
        new FrameworkPropertyMetadata(CreateFrozenBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)), FrameworkPropertyMetadataOptions.AffectsRender));

    private double _displayedLevel;
    private int _renderedLitCount;

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    public bool IsSpeechActive
    {
        get => (bool)GetValue(IsSpeechActiveProperty);
        set => SetValue(IsSpeechActiveProperty, value);
    }

    public Brush InactiveBrush
    {
        get => (Brush)GetValue(InactiveBrushProperty);
        set => SetValue(InactiveBrushProperty, value);
    }

    public Brush NormalBrush
    {
        get => (Brush)GetValue(NormalBrushProperty);
        set => SetValue(NormalBrushProperty, value);
    }

    public Brush WarmBrush
    {
        get => (Brush)GetValue(WarmBrushProperty);
        set => SetValue(WarmBrushProperty, value);
    }

    public Brush PeakBrush
    {
        get => (Brush)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public void ResetPeak()
    {
        var needsRedraw = _displayedLevel > 0d || _renderedLitCount != 0;
        _displayedLevel = 0d;
        _renderedLitCount = 0;
        if (needsRedraw)
        {
            InvalidateVisual();
        }
    }

    private static void OnSegmentCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentedLevelMeter meter)
        {
            meter._renderedLitCount = meter.ComputeLitCount(meter._displayedLevel);
        }
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SegmentedLevelMeter meter)
        {
            return;
        }

        var next = Math.Clamp((double)e.NewValue, 0d, 100d);
        meter._displayedLevel = next <= 0.5d ? 0d : next;

        var litCount = meter.ComputeLitCount(meter._displayedLevel);
        if (litCount != meter._renderedLitCount)
        {
            meter._renderedLitCount = litCount;
            meter.InvalidateVisual();
        }
    }

    private int ComputeLitCount(double level)
    {
        var count = Math.Max(8, SegmentCount);
        return Math.Clamp((int)Math.Round(Math.Clamp(level, 0d, 100d) / 100d * count), 0, count);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        var count = Math.Max(8, SegmentCount);
        if (width <= 4 || height <= 2)
        {
            return;
        }

        const double gap = 3d;
        var segmentWidth = (width - gap * (count - 1)) / count;
        if (segmentWidth <= 1d)
        {
            segmentWidth = Math.Max(1d, width / count);
        }

        var litCount = ComputeLitCount(_displayedLevel);
        var radius = Math.Min(2d, segmentWidth * 0.35);
        var inactive = InactiveBrush;
        var normal = NormalBrush;
        var warm = WarmBrush;
        var peak = PeakBrush;
        var speechActive = IsSpeechActive;

        // 先绘制未点亮的底格
        for (var i = litCount; i < count; i++)
        {
            var x = i * (segmentWidth + gap);
            drawingContext.DrawRoundedRectangle(inactive, null, new Rect(x, 0, segmentWidth, height), radius, radius);
        }

        if (litCount <= 0)
        {
            return;
        }

        // 未触发人声时稍微柔化活跃格亮度，人声触发后恢复 100% 饱满对比度
        var pushedOpacity = false;
        if (!speechActive)
        {
            drawingContext.PushOpacity(0.76d);
            pushedOpacity = true;
        }

        for (var i = 0; i < litCount; i++)
        {
            var x = i * (segmentWidth + gap);
            var ratio = (i + 1) / (double)count;
            var activeBrush = ratio > 0.86d
                ? peak
                : ratio > 0.65d
                    ? warm
                    : normal;
            drawingContext.DrawRoundedRectangle(activeBrush, null, new Rect(x, 0, segmentWidth, height), radius, radius);
        }

        if (pushedOpacity)
        {
            drawingContext.Pop();
        }
    }
}
