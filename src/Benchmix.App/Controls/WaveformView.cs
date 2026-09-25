using System.Windows;
using System.Windows.Media;
using Benchmix.Engine.Audio;

namespace Benchmix.App.Controls;

/// <summary>One section as the lane needs to draw it.</summary>
/// <param name="Peaks">Envelope of the whole file this section is cut from.</param>
/// <param name="TimelineStartSeconds">Where it begins on the timeline, after the track's tempo.</param>
/// <param name="IsForeign">True when it came from a different song than the rest of the track.</param>
/// <param name="LoopHandleLeft">
/// True when this section's left end is the outer left end of the loop the selected section
/// belongs to — where, in loop mode, a drag repeats it. Usually the selected section itself; for a
/// loop built earlier, the first of its repeats.
/// </param>
/// <param name="LoopHandleRight">The same for the loop's outer right end.</param>
public sealed record ClipVisual(
    PeakData? Peaks,
    int SampleRate,
    double TimelineStartSeconds,
    double SourceStartSeconds,
    double SourceEndSeconds,
    double Speed,
    bool IsSelected,
    bool IsForeign,
    bool LoopHandleLeft = false,
    bool LoopHandleRight = false);

/// <summary>
/// Draws one track's sections on the timeline.
///
/// It draws from a precomputed min/max peak envelope rather than the samples, so redrawing while
/// scrolling or zooming costs one pass over the visible pixel columns instead of one pass over
/// several million samples.
///
/// The important subtlety is that it draws <b>timeline</b> time, not source time. When a track is
/// stretched to 92%, its waveform gets visually wider by the same amount, because that is where
/// the audio now actually falls. A waveform that ignored the stretch would put every transient in
/// the wrong place and make the display useless for lining anything up.
///
/// <para>
/// A track holds several sections, each possibly cut from a different song, so each carries its
/// own peaks. They arrive as one immutable array: WPF repaints when a bound value <b>changes</b>,
/// and a list edited in place leaves the reference identical and the lane stale.
/// </para>
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty ClipsProperty = DependencyProperty.Register(
        nameof(Clips), typeof(IReadOnlyList<ClipVisual>), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScrollSecondsProperty = DependencyProperty.Register(
        nameof(ScrollSeconds), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ClipBackgroundProperty = DependencyProperty.Register(
        nameof(ClipBackground), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When on, the selected section's two ends are drawn as grab handles: dragging them repeats it.</summary>
    public static readonly DependencyProperty LoopModeProperty = DependencyProperty.Register(
        nameof(LoopMode), typeof(bool), typeof(WaveformView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ClipVisual>? Clips
    {
        get => (IReadOnlyList<ClipVisual>?)GetValue(ClipsProperty);
        set => SetValue(ClipsProperty, value);
    }

    public double PixelsPerSecond { get => (double)GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double ScrollSeconds { get => (double)GetValue(ScrollSecondsProperty); set => SetValue(ScrollSecondsProperty, value); }
    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
    public Brush? ClipBackground { get => (Brush?)GetValue(ClipBackgroundProperty); set => SetValue(ClipBackgroundProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public bool LoopMode { get => (bool)GetValue(LoopModeProperty); set => SetValue(LoopModeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (double.IsNaN(w) || double.IsNaN(h) || w < 1 || h < 1) return;

        // A transparent hit-test surface, so clicks on empty lane area still reach this element.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var clips = Clips;
        if (clips is null) return;

        // The selected section last, so while it is dragged over another it is the one on top.
        foreach (var clip in clips) if (!clip.IsSelected) DrawClip(dc, clip, w, h);
        foreach (var clip in clips) if (clip.IsSelected) DrawClip(dc, clip, w, h);

        // Loop handles on top of everything, at the loop's outer ends — not at the selected
        // section's own ends, which inside a loop are the joins between repeats.
        if (LoopMode)
            foreach (var clip in clips)
                if (clip.LoopHandleLeft || clip.LoopHandleRight) DrawLoopHandles(dc, clip, w, h);
    }

    private void DrawClip(DrawingContext dc, ClipVisual clip, double w, double h)
    {
        var peaks = clip.Peaks;
        if (peaks is null || peaks.BucketCount == 0 || clip.SampleRate <= 0) return;

        double pps = Math.Max(0.01, PixelsPerSecond);
        double speed = Math.Max(0.01, clip.Speed);
        double srcStart = Math.Max(0, clip.SourceStartSeconds);
        double srcTotal = peaks.FrameCount / (double)clip.SampleRate;
        double srcEnd = clip.SourceEndSeconds < 0 ? srcTotal : Math.Min(clip.SourceEndSeconds, srcTotal);
        if (srcEnd <= srcStart) return;

        // Timeline extent of this section after the stretch.
        double clipStart = clip.TimelineStartSeconds;
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
        double secondsPerBucket = peaks.SamplesPerBucket / (double)clip.SampleRate;

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

        // A section from another song is outlined, so it is obvious that this track's tempo, pitch
        // and EQ are about to be applied to audio that did not come from this track's song.
        if (clip.IsForeign)
        {
            var foreign = new Pen(new SolidColorBrush(Color.FromArgb(160, 245, 197, 24)), 1)
            {
                DashStyle = DashStyles.Dash,
            };
            foreign.Freeze();
            dc.DrawRoundedRectangle(null, foreign, new Rect(xFrom, 1, Math.Max(1, xTo - xFrom), h - 2), 3, 3);
        }

        // A faint line where each section begins, so two butted together — every repeat of a
        // loop — read as separate pieces rather than one long block.
        double startX = (clipStart - viewStart) * pps;
        if (startX >= 0 && startX <= w)
        {
            var divider = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
            divider.Freeze();
            dc.DrawLine(divider, new Point(Math.Round(startX) + 0.5, 0), new Point(Math.Round(startX) + 0.5, h));
        }

        if (clip.IsSelected)
        {
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5);
            outline.Freeze();
            dc.DrawRoundedRectangle(null, outline, new Rect(xFrom, 0.75, Math.Max(1, xTo - xFrom), h - 1.5), 3, 3);
        }
    }

    /// <summary>Bright bars at a loop's outer ends: the places to grab to repeat it.</summary>
    private void DrawLoopHandles(DrawingContext dc, ClipVisual clip, double w, double h)
    {
        if (clip.Peaks is null || clip.SampleRate <= 0) return;
        double pps = Math.Max(0.01, PixelsPerSecond);
        double srcTotal = clip.Peaks.FrameCount / (double)clip.SampleRate;
        double srcEnd = clip.SourceEndSeconds < 0 ? srcTotal : Math.Min(clip.SourceEndSeconds, srcTotal);
        double length = Math.Max(0, srcEnd - Math.Max(0, clip.SourceStartSeconds)) / Math.Max(0.01, clip.Speed);

        double left = (clip.TimelineStartSeconds - ScrollSeconds) * pps;
        double right = (clip.TimelineStartSeconds + length - ScrollSeconds) * pps;

        var fill = new SolidColorBrush(Color.FromArgb(235, 120, 220, 255));
        fill.Freeze();
        double top = h * 0.25, height = h * 0.5;
        if (clip.LoopHandleLeft && left >= -4 && left <= w)
            dc.DrawRoundedRectangle(fill, null, new Rect(left, top, 4, height), 2, 2);
        if (clip.LoopHandleRight && right >= 0 && right <= w + 4)
            dc.DrawRoundedRectangle(fill, null, new Rect(right - 4, top, 4, height), 2, 2);
    }
}
