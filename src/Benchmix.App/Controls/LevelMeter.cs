using System.Windows;
using System.Windows.Media;

namespace Benchmix.App.Controls;

/// <summary>
/// A peak level meter with a green/amber/red zone bar and a peak-hold line.
///
/// The control is deliberately passive: it takes linear amplitudes and paints them. Ballistics —
/// peak decay, RMS smoothing, clip latching — belong to the audio side, which knows the sample
/// rate and the buffer cadence; the host simply pushes new <see cref="Value"/> and
/// <see cref="PeakHold"/> numbers per UI frame.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    // Displayed range. Amplitudes are mapped to dBFS and then laid out linearly in dB, because a
    // linear-amplitude bar is useless for audio: -20 dBFS — a perfectly healthy working level —
    // is 0.1 in amplitude and would sit at a tenth of the bar, while the entire usable top of the
    // scale bunches into the last sliver. On this scale -20 dBFS lands at two-thirds height.
    private const double MinDb = -60.0;
    private const double WarnDb = -6.0;
    private const double ClipDb = -1.0;

    // Zone boundaries as bar fractions. A linear-in-dB scale makes the top zones narrow on purpose
    // (0.90 and 0.983): the amber and red bands are meant to read as a thin danger margin.
    private const double WarnStart = 1.0 - WarnDb / MinDb;
    private const double ClipStart = 1.0 - ClipDb / MinDb;

    private const double PeakLineThickness = 2.0;

    private static readonly SolidColorBrush DefaultMeter = Frozen(0xFF, 0x41, 0xC3, 0x6A);
    private static readonly SolidColorBrush DefaultWarn = Frozen(0xFF, 0xE8, 0xC3, 0x3A);
    private static readonly SolidColorBrush DefaultClip = Frozen(0xFF, 0xE4, 0x48, 0x3C);
    private static readonly SolidColorBrush DefaultTrack = Frozen(0xFF, 0x1C, 0x1F, 0x24);

    // One-entry memo for the brightened peak brush. The meter repaints every UI frame but the peak
    // only crosses a zone boundary occasionally, so this keeps the steady state allocation-free.
    private Brush? _peakSourceBrush;
    private Brush? _peakBrush;

    #region Dependency properties

    /// <summary>Identifies the <see cref="Value"/> dependency property.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="PeakHold"/> dependency property.</summary>
    public static readonly DependencyProperty PeakHoldProperty = DependencyProperty.Register(
        nameof(PeakHold), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="IsVertical"/> dependency property.</summary>
    public static readonly DependencyProperty IsVerticalProperty = DependencyProperty.Register(
        nameof(IsVertical), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Identifies the <see cref="MeterBrush"/> dependency property.</summary>
    public static readonly DependencyProperty MeterBrushProperty = DependencyProperty.Register(
        nameof(MeterBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(DefaultMeter, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="WarnBrush"/> dependency property.</summary>
    public static readonly DependencyProperty WarnBrushProperty = DependencyProperty.Register(
        nameof(WarnBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(DefaultWarn, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="ClipBrush"/> dependency property.</summary>
    public static readonly DependencyProperty ClipBrushProperty = DependencyProperty.Register(
        nameof(ClipBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(DefaultClip, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="TrackBrush"/> dependency property.</summary>
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Current level as a linear amplitude, 0..1. Values at or below zero draw bare track.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// Peak-hold position as a linear amplitude, 0..1, drawn as a thin bright line. The host owns
    /// the decay, so it can pick the hold time and fall rate its meter ballistics call for.
    /// </summary>
    public double PeakHold
    {
        get => (double)GetValue(PeakHoldProperty);
        set => SetValue(PeakHoldProperty, value);
    }

    /// <summary>True for a bottom-up bar (the mixer-strip default); false for a left-to-right bar.</summary>
    public bool IsVertical
    {
        get => (bool)GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    /// <summary>Brush for the safe part of the bar, below <c>-6 dBFS</c>.</summary>
    public Brush MeterBrush
    {
        get => (Brush)GetValue(MeterBrushProperty);
        set => SetValue(MeterBrushProperty, value);
    }

    /// <summary>Brush for the <c>-6</c>..<c>-1 dBFS</c> band.</summary>
    public Brush WarnBrush
    {
        get => (Brush)GetValue(WarnBrushProperty);
        set => SetValue(WarnBrushProperty, value);
    }

    /// <summary>Brush for the band above <c>-1 dBFS</c>.</summary>
    public Brush ClipBrush
    {
        get => (Brush)GetValue(ClipBrushProperty);
        set => SetValue(ClipBrushProperty, value);
    }

    /// <summary>Brush for the unlit background of the bar.</summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    #endregion

    /// <summary>
    /// Converts a linear amplitude to its position on the bar, 0..1, via the -60..0 dBFS scale.
    /// </summary>
    /// <remarks>
    /// Silence and anything at or below -60 dBFS pin to 0; anything at or above full scale pins
    /// to 1, so an overload does not run off the end of the control.
    /// </remarks>
    public static double AmplitudeToBarFraction(double amplitude)
    {
        if (double.IsNaN(amplitude) || amplitude <= 0.0)
        {
            return 0.0;
        }

        if (amplitude >= 1.0)
        {
            return 1.0;
        }

        double db = 20.0 * Math.Log10(amplitude);

        // db is negative here, and so is MinDb, so the quotient is the fraction of the way *down*
        // the scale — subtracting it from 1 gives the fraction of the way up the bar.
        return db <= MinDb ? 0.0 : 1.0 - db / MinDb;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // A childless FrameworkElement measures to zero and vanishes in an auto-sized panel, so
        // fall back to a usable bar on whichever axis the parent left unconstrained.
        double w = double.IsInfinity(availableSize.Width) ? (IsVertical ? 10.0 : 120.0) : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? (IsVertical ? 120.0 : 10.0) : availableSize.Height;
        return new Size(w, h);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (!IsUsable(w) || !IsUsable(h))
        {
            return;
        }

        bool vertical = IsVertical;
        dc.DrawRectangle(TrackBrush, null, new Rect(0, 0, w, h));

        // Each zone is painted only as far as the level actually reaches, so the bar keeps its
        // colours in place instead of recolouring wholesale as the level crosses a boundary.
        double level = AmplitudeToBarFraction(Value);
        FillRange(dc, MeterBrush, 0.0, Math.Min(level, WarnStart), w, h, vertical);
        FillRange(dc, WarnBrush, WarnStart, Math.Min(level, ClipStart), w, h, vertical);
        FillRange(dc, ClipBrush, ClipStart, level, w, h, vertical);

        double peak = AmplitudeToBarFraction(PeakHold);
        if (peak > 0.0)
        {
            DrawPeakLine(dc, peak, w, h, vertical);
        }
    }

    /// <summary>Paints the bar between two 0..1 fractions, growing bottom-up or left-to-right.</summary>
    private static void FillRange(DrawingContext dc, Brush brush, double from, double to, double w, double h, bool vertical)
    {
        if (to <= from)
        {
            return;
        }

        if (vertical)
        {
            // Fraction 1 is the top of the control, so the higher fraction gets the smaller Y.
            double top = h - to * h;
            dc.DrawRectangle(brush, null, new Rect(0, top, w, h - from * h - top));
        }
        else
        {
            double left = from * w;
            dc.DrawRectangle(brush, null, new Rect(left, 0, to * w - left, h));
        }
    }

    private void DrawPeakLine(DrawingContext dc, double peak, double w, double h, bool vertical)
    {
        double extent = vertical ? h : w;
        double thickness = Math.Min(PeakLineThickness, extent); // keeps the clamp below well-formed

        // Colour the line by the zone it sits in, lightened so it still separates from the lit bar
        // underneath on the frames where the level has caught up with the hold.
        var zone = peak >= ClipStart ? ClipBrush : peak >= WarnStart ? WarnBrush : MeterBrush;
        var brush = BrightenCached(zone);

        if (vertical)
        {
            double y = Math.Clamp(h - peak * h - thickness / 2.0, 0.0, h - thickness);
            dc.DrawRectangle(brush, null, new Rect(0, y, w, thickness));
        }
        else
        {
            double x = Math.Clamp(peak * w - thickness / 2.0, 0.0, w - thickness);
            dc.DrawRectangle(brush, null, new Rect(x, 0, thickness, h));
        }
    }

    private Brush BrightenCached(Brush source)
    {
        if (!ReferenceEquals(source, _peakSourceBrush) || _peakBrush is null)
        {
            _peakSourceBrush = source;
            _peakBrush = Brighten(source);
        }

        return _peakBrush;
    }

    /// <summary>Lifts a solid colour toward white; anything else (gradient, image) is passed through.</summary>
    private static Brush Brighten(Brush source)
    {
        if (source is not SolidColorBrush solid)
        {
            return source;
        }

        var c = solid.Color;
        var lit = new SolidColorBrush(Color.FromArgb(c.A, Lift(c.R), Lift(c.G), Lift(c.B)));
        lit.Freeze();
        return lit;

        static byte Lift(byte v) => (byte)(v + (255 - v) * 0.55);
    }

    private static bool IsUsable(double d) => !double.IsNaN(d) && !double.IsInfinity(d) && d > 0.0;

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
