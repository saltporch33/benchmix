using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Benchmix.App.Controls;

/// <summary>
/// The time ruler above the tracks. Shows clock time, and — when the project has a tempo — bar
/// lines, because a mashup is lined up in bars, not in seconds.
///
/// The tick interval is chosen from the current zoom so labels never collide: it walks a
/// 1/2/5/10/15/30/60-second ladder and takes the first step that leaves at least ~70 px between
/// labels.
/// </summary>
public sealed class TimelineRuler : FrameworkElement
{
    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScrollSecondsProperty = DependencyProperty.Register(
        nameof(ScrollSeconds), typeof(double), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BpmProperty = DependencyProperty.Register(
        nameof(Bpm), typeof(double), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PixelsPerSecond { get => (double)GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double ScrollSeconds { get => (double)GetValue(ScrollSecondsProperty); set => SetValue(ScrollSecondsProperty, value); }
    public double Bpm { get => (double)GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    static readonly double[] SecondSteps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (double.IsNaN(w) || double.IsNaN(h) || w < 1 || h < 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double pps = Math.Max(0.01, PixelsPerSecond);
        double start = ScrollSeconds;
        double end = start + w / pps;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        var tickPen = new Pen(Foreground, 1);
        tickPen.Freeze();
        var barPen = new Pen(BarBrush, 1);
        barPen.Freeze();

        // Bar lines first, so time ticks draw on top of them.
        if (Bpm > 10)
        {
            double barSeconds = 4 * 60.0 / Bpm;          // assume 4/4; true for essentially all of this material
            if (barSeconds * pps >= 14)                   // stop drawing bars once they would be a smear
            {
                long firstBar = (long)Math.Floor(start / barSeconds);
                for (long b = firstBar; ; b++)
                {
                    double t = b * barSeconds;
                    if (t > end) break;
                    if (t < start) continue;
                    double x = (t - start) * pps;
                    bool isPhrase = b % 4 == 0;          // 4-bar phrase, the unit mashups are cut on
                    dc.DrawLine(barPen, new Point(x, isPhrase ? h * 0.35 : h * 0.62), new Point(x, h));
                }
            }
        }

        double step = SecondSteps[^1];
        foreach (var s in SecondSteps)
            if (s * pps >= 70) { step = s; break; }

        long firstTick = (long)Math.Floor(start / step);
        for (long i = firstTick; ; i++)
        {
            double t = i * step;
            if (t > end) break;
            if (t < 0) continue;
            double x = (t - start) * pps;
            if (x < -1) continue;

            dc.DrawLine(tickPen, new Point(x, h * 0.45), new Point(x, h));

            var text = new FormattedText(FormatTime(t), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10.5, Foreground, dpi)
            { TextAlignment = TextAlignment.Left };
            dc.DrawText(text, new Point(x + 3, 1));
        }
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }
}

/// <summary>A one-pixel playhead line drawn over the lanes.</summary>
public sealed class PlayheadOverlay : FrameworkElement
{
    public static readonly DependencyProperty PositionSecondsProperty = DependencyProperty.Register(
        nameof(PositionSeconds), typeof(double), typeof(PlayheadOverlay),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(PlayheadOverlay),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScrollSecondsProperty = DependencyProperty.Register(
        nameof(ScrollSeconds), typeof(double), typeof(PlayheadOverlay),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LoopStartProperty = DependencyProperty.Register(
        nameof(LoopStart), typeof(double?), typeof(PlayheadOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LoopEndProperty = DependencyProperty.Register(
        nameof(LoopEnd), typeof(double?), typeof(PlayheadOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(PlayheadOverlay),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PositionSeconds { get => (double)GetValue(PositionSecondsProperty); set => SetValue(PositionSecondsProperty, value); }
    public double PixelsPerSecond { get => (double)GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double ScrollSeconds { get => (double)GetValue(ScrollSecondsProperty); set => SetValue(ScrollSecondsProperty, value); }
    public double? LoopStart { get => (double?)GetValue(LoopStartProperty); set => SetValue(LoopStartProperty, value); }
    public double? LoopEnd { get => (double?)GetValue(LoopEndProperty); set => SetValue(LoopEndProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public PlayheadOverlay() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (double.IsNaN(w) || double.IsNaN(h) || w < 1 || h < 1) return;

        double pps = Math.Max(0.01, PixelsPerSecond);

        if (LoopStart is { } ls && LoopEnd is { } le && le > ls)
        {
            double x0 = (ls - ScrollSeconds) * pps;
            double x1 = (le - ScrollSeconds) * pps;
            if (x1 > 0 && x0 < w)
            {
                var fill = new SolidColorBrush(Color.FromArgb(28, 255, 210, 80));
                fill.Freeze();
                var edge = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 210, 80)), 1);
                edge.Freeze();
                var rect = new Rect(Math.Max(0, x0), 0, Math.Min(w, x1) - Math.Max(0, x0), h);
                if (rect.Width > 0) dc.DrawRectangle(fill, edge, rect);
            }
        }

        double x = (PositionSeconds - ScrollSeconds) * pps;
        if (x < -1 || x > w + 1) return;

        var pen = new Pen(LineBrush, 1.5);
        pen.Freeze();
        dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
    }
}
