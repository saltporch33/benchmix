using System.Windows;
using System.Windows.Media;
using Benchmix.Engine.Audio;

namespace Benchmix.App.Controls;

/// <summary>
/// Draws one track's waveform on the timeline.
///
/// It draws from a precomputed min/max peak envelope rather than the samples, so redrawing while
/// scrolling or zooming costs one pass over the visible pixel columns instead of one pass over
/// several million samples.
///
/// The important subtlety is that it draws <b>timeline</b> time, not source time. When a track is
/// stretched to 92%, its waveform gets visually wider by the same amount, because that is where
/// the audio now actually falls. A waveform that ignored the stretch would put every transient in
/// the wrong place and make the display useless for lining anything up.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(PeakData), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SampleRateProperty = DependencyProperty.Register(
        nameof(SampleRate), typeof(int), typeof(WaveformView),
        new FrameworkPropertyMetadata(48000, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScrollSecondsProperty = DependencyProperty.Register(
        nameof(ScrollSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the clip begins on the timeline.</summary>
    public static readonly DependencyProperty ClipOffsetSecondsProperty = DependencyProperty.Register(
        nameof(ClipOffsetSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourceStartSecondsProperty = DependencyProperty.Register(
        nameof(SourceStartSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Negative means play to the end of the source.</summary>
    public static readonly DependencyProperty SourceEndSecondsProperty = DependencyProperty.Register(
        nameof(SourceEndSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>1.0 is the original tempo; the waveform widens or narrows to match.</summary>
    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
        nameof(Speed), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ClipBackgroundProperty = DependencyProperty.Register(
        nameof(ClipBackground), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public PeakData? Peaks { get => (PeakData?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public int SampleRate { get => (int)GetValue(SampleRateProperty); set => SetValue(SampleRateProperty, value); }
    public double PixelsPerSecond { get => (double)GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double ScrollSeconds { get => (double)GetValue(ScrollSecondsProperty); set => SetValue(ScrollSecondsProperty, value); }
    public double ClipOffsetSeconds { get => (double)GetValue(ClipOffsetSecondsProperty); set => SetValue(ClipOffsetSecondsProperty, value); }
    public double SourceStartSeconds { get => (double)GetValue(SourceStartSecondsProperty); set => SetValue(SourceStartSecondsProperty, value); }
    public double SourceEndSeconds { get => (double)GetValue(SourceEndSecondsProperty); set => SetValue(SourceEndSecondsProperty, value); }
    public double Speed { get => (double)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }
    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
    public Brush? ClipBackground { get => (Brush?)GetValue(ClipBackgroundProperty); set => SetValue(ClipBackgroundProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (double.IsNaN(w) || double.IsNaN(h) || w < 1 || h < 1) return;

        // A transparent hit-test surface, so clicks on empty lane area still reach this element.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var peaks = Peaks;
        if (peaks is null || peaks.BucketCount == 0 || SampleRate <= 0) return;

        double pps = Math.Max(0.01, PixelsPerSecond);
        double speed = Math.Max(0.01, Speed);
        double srcStart = Math.Max(0, SourceStartSeconds);
        double srcTotal = peaks.FrameCount / (double)SampleRate;
        double srcEnd = SourceEndSeconds < 0 ? srcTotal : Math.Min(SourceEndSeconds, srcTotal);
        if (srcEnd <= srcStart) return;

        // Timeline extent of this clip after the stretch.
        double clipStart = ClipOffsetSeconds;
        double clipEnd = clipStart + (srcEnd - srcStart) / speed;

        double viewStart = ScrollSeconds;
        double viewEnd = ScrollSeconds + w / pps;
        double drawStart = Math.Max(clipStart, viewStart);
        double drawEnd = Math.Min(clipEnd, viewEnd);
        if (drawEnd <= drawStart) return;

        double xFrom = (drawStart - viewStart) * pps;
        double xTo = (drawEnd - viewStart) * pps;

        if (ClipBackground is not null)
            dc.DrawRoundedRectangle(ClipBackground, null, new Rect(xFrom, 0, Math.Max(1, xTo - xFrom), h), 3, 3);

        var brush = WaveBrush;
        if (IsMuted && brush is SolidColorBrush scb)
        {
            var c = scb.Color;
            brush = new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B));
            brush.Freeze();
        }

        double mid = h / 2;
        double halfHeight = (h / 2) - 2;
        double secondsPerBucket = peaks.SamplesPerBucket / (double)SampleRate;

        // One vertical line per pixel column, from the min/max of every bucket that column covers.
        // Columns are drawn into a StreamGeometry so the whole waveform is a single draw call.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            int x0 = (int)Math.Floor(xFrom), x1 = (int)Math.Ceiling(xTo);
            for (int px = x0; px < x1; px++)
            {
                double tA = viewStart + px / pps;
                double tB = viewStart + (px + 1) / pps;

                // timeline -> source
                double sA = srcStart + (tA - clipStart) * speed;
                double sB = srcStart + (tB - clipStart) * speed;
                if (sB <= srcStart || sA >= srcEnd) continue;
                sA = Math.Max(sA, srcStart);
                sB = Math.Min(sB, srcEnd);

                int bA = (int)(sA / secondsPerBucket);
                int bB = (int)(sB / secondsPerBucket);
                if (bA < 0) bA = 0;
                if (bB >= peaks.BucketCount) bB = peaks.BucketCount - 1;
                if (bB < bA) bB = bA;
                if (bA >= peaks.BucketCount) continue;

                float lo = 0, hi = 0;
                for (int b = bA; b <= bB; b++)
                {
                    if (peaks.Min[b] < lo) lo = peaks.Min[b];
                    if (peaks.Max[b] > hi) hi = peaks.Max[b];
                }

                double yTop = mid - hi * halfHeight;
                double yBottom = mid - lo * halfHeight;
                if (yBottom - yTop < 1) { yTop = mid - 0.5; yBottom = mid + 0.5; }

                ctx.BeginFigure(new Point(px + 0.5, yTop), false, false);
                ctx.LineTo(new Point(px + 0.5, yBottom), true, false);
            }
        }
        geometry.Freeze();

        var pen = new Pen(brush, 1);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        if (IsSelected)
        {
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5);
            outline.Freeze();
            dc.DrawRoundedRectangle(null, outline, new Rect(xFrom, 0.75, Math.Max(1, xTo - xFrom), h - 1.5), 3, 3);
        }
    }
}
