using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Benchmix.App.Controls;

/// <summary>
/// A rotary knob in the style of a DJ mixer: a 270° dial with a track arc, a coloured value arc,
/// a pointer, and two lines of text underneath.
///
/// This is a leaf <see cref="FrameworkElement"/> that paints itself in <see cref="OnRender"/>
/// rather than a templated <c>Control</c>. A mixer strip stacks a dozen of these and they redraw
/// on every parameter change, so the cost of a template tree, a style lookup and a handful of
/// <c>Shape</c> children per knob is not worth paying for something that is four drawing calls.
///
/// All geometry is derived from <see cref="FrameworkElement.ActualWidth"/> /
/// <see cref="FrameworkElement.ActualHeight"/>, so one knob type serves both the compact channel
/// EQ (~40×56) and the large master controls (~90×110).
/// </summary>
public sealed class Knob : FrameworkElement
{
    // The dial runs from 225° (lower-left) clockwise through 12 o'clock to -45° (lower-right).
    // Angles are the ordinary maths convention — 0° east, increasing counter-clockwise — which
    // puts the 90° dead-zone at the bottom, where a real knob's end-stops live. Screen Y is
    // flipped once, in PointOnCircle, so the rest of the maths reads normally.
    private const double StartAngleDeg = 225.0;
    private const double SweepDeg = 270.0;

    /// <summary>Vertical travel, in device-independent pixels, for one full Min→Max sweep.</summary>
    private const double FullSweepTravelPx = 200.0;

    /// <summary>Shift multiplier for drag, wheel and keyboard, giving roughly 1/5 sensitivity.</summary>
    private const double FineFactor = 0.2;

    private const double WheelStepFraction = 0.02;  // per wheel notch
    private const double KeyStepFraction = 0.01;    // per arrow press
    private const double TextBandHeight = 26.0;     // room for the label + value lines

    private static readonly Typeface TextFace = new("Segoe UI");

    private static readonly SolidColorBrush DefaultArc = Frozen(0xFF, 0x4F, 0xC3, 0xF7);
    private static readonly SolidColorBrush DefaultTrack = Frozen(0xFF, 0x3A, 0x3F, 0x46);
    private static readonly SolidColorBrush DefaultKnob = Frozen(0xFF, 0x23, 0x26, 0x2B);
    private static readonly SolidColorBrush DefaultText = Frozen(0xFF, 0xD5, 0xDB, 0xE2);

    private Point _lastDragPoint;
    private bool _dragging;

    /// <summary>Creates a knob that can take keyboard focus and draws its own focus ring.</summary>
    public Knob()
    {
        Focusable = true;
        FocusVisualStyle = null; // the dashed ring in OnRender replaces the default adorner
    }

    /// <summary>Raised whenever <see cref="Value"/> settles on a new number, with that number.</summary>
    public event EventHandler<double>? ValueChanged;

    #region Dependency properties

    /// <summary>Identifies the <see cref="Value"/> dependency property.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(
            1.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValuePropertyChanged,
            CoerceValueProperty));

    /// <summary>Identifies the <see cref="Minimum"/> dependency property.</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnRangeChanged));

    /// <summary>Identifies the <see cref="Maximum"/> dependency property.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender, OnRangeChanged));

    /// <summary>Identifies the <see cref="DefaultValue"/> dependency property.</summary>
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(Knob), new FrameworkPropertyMetadata(1.0));

    /// <summary>Identifies the <see cref="Label"/> dependency property.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Knob),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="ValueText"/> dependency property.</summary>
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(Knob),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="IsBipolar"/> dependency property.</summary>
    public static readonly DependencyProperty IsBipolarProperty = DependencyProperty.Register(
        nameof(IsBipolar), typeof(bool), typeof(Knob),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="ArcBrush"/> dependency property.</summary>
    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(DefaultArc, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="TrackBrush"/> dependency property.</summary>
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="KnobBrush"/> dependency property.</summary>
    public static readonly DependencyProperty KnobBrushProperty = DependencyProperty.Register(
        nameof(KnobBrush), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(DefaultKnob, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="TextBrush"/> dependency property.</summary>
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(DefaultText, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The current value, always coerced into <see cref="Minimum"/>..<see cref="Maximum"/>.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Lower end of the dial's travel.</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Upper end of the dial's travel.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The value a double-click (or Home) snaps back to — unity gain, centre pan, and so on.</summary>
    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    /// <summary>Caption drawn under the dial, e.g. <c>"LOW"</c>.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// Overrides the computed number on the bottom line. The host owns the unit formatting —
    /// a gain knob wants "-inf dB" at the bottom of its travel, which no generic formatter knows.
    /// </summary>
    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    /// <summary>
    /// When true the value arc grows outward from 12 o'clock in either direction, so a pitch or
    /// pan knob reads as "how far off centre"; when false it fills from the minimum end.
    /// </summary>
    public bool IsBipolar
    {
        get => (bool)GetValue(IsBipolarProperty);
        set => SetValue(IsBipolarProperty, value);
    }

    /// <summary>Brush for the value arc and the pointer.</summary>
    public Brush ArcBrush
    {
        get => (Brush)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    /// <summary>Brush for the unfilled track arc behind the value arc.</summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Fill brush for the knob body.</summary>
    public Brush KnobBrush
    {
        get => (Brush)GetValue(KnobBrushProperty);
        set => SetValue(KnobBrushProperty, value);
    }

    /// <summary>Brush for the label and value text.</summary>
    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    #endregion

    #region Property plumbing

    private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var knob = (Knob)d;
        knob.ValueChanged?.Invoke(knob, (double)e.NewValue);
    }

    // Moving an end-stop can leave Value outside the new range, so re-run the clamp.
    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.CoerceValue(ValueProperty);

    private static object CoerceValueProperty(DependencyObject d, object baseValue)
    {
        var knob = (Knob)d;
        var v = (double)baseValue;
        if (double.IsNaN(v))
        {
            return knob.DefaultValue;
        }

        double min = knob.Minimum;
        double max = knob.Maximum;

        // An inverted range is a host bug, not a reason to throw out of a render pass.
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return Math.Clamp(v, min, max);
    }

    /// <summary>Position of <paramref name="value"/> within the range, as 0..1.</summary>
    private double Normalise(double value)
    {
        double range = Maximum - Minimum;
        return range <= 0 ? 0.0 : Math.Clamp((value - Minimum) / range, 0.0, 1.0);
    }

    private static double AngleFor(double normalised) => StartAngleDeg - SweepDeg * normalised;

    private static bool IsFineModifierDown => (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

    /// <summary>Adds <paramref name="delta"/> range-units to <see cref="Value"/>; coercion does the clamping.</summary>
    private void Nudge(double delta, bool fine) => Value += fine ? delta * FineFactor : delta;

    #endregion

    #region Input

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        // FrameworkElement has no OnMouseDoubleClick (that lives on Control), so the reset gesture
        // is read straight off the click count.
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }

        _lastDragPoint = e.GetPosition(this);
        _dragging = CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        var p = e.GetPosition(this);

        // Screen Y grows downward, so the previous point minus the current one makes "drag up"
        // positive. Deltas are accumulated move-by-move rather than measured from the press
        // point, which lets Shift be pressed or released mid-drag without the value jumping.
        double dy = _lastDragPoint.Y - p.Y;
        _lastDragPoint = p;

        Nudge(dy / FullSweepTravelPx * (Maximum - Minimum), IsFineModifierDown);
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double notches = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        Nudge(notches * WheelStepFraction * (Maximum - Minimum), IsFineModifierDown);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = KeyStepFraction * (Maximum - Minimum);

        switch (e.Key)
        {
            case Key.Up or Key.Right:
                Nudge(step, IsFineModifierDown);
                break;
            case Key.Down or Key.Left:
                Nudge(-step, IsFineModifierDown);
                break;
            case Key.Home:
                Value = DefaultValue;
                break;
            default:
                return; // leave anything else for the host (Tab, shortcuts, …)
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    #endregion

    #region Layout and rendering

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // A childless FrameworkElement measures to zero and would be invisible in an auto-sized
        // panel, so fall back to a sensible dial size on whichever axis the parent left open.
        double w = double.IsInfinity(availableSize.Width) ? 56.0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 76.0 : availableSize.Height;
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

        // Without a filled shape spanning the element, a drag that starts in a corner — outside
        // the knob body — would hit-test through to whatever sits behind the knob.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double textBand = Math.Min(TextBandHeight, h * 0.45);
        double dialBox = Math.Min(w, h - textBand);

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawCaptions(dc, w, h, textBand, pixelsPerDip);

        if (dialBox <= 4.0)
        {
            return; // too small to draw a dial into; the captions still carry the information
        }

        var centre = new Point(w / 2.0, (h - textBand) / 2.0);
        double outer = dialBox / 2.0;

        double arcThickness = Math.Max(2.0, outer * 0.22);
        double trackThickness = Math.Max(1.0, arcThickness * 0.45);

        // Both arcs share a radius so the thin track sits centred behind the thick value arc.
        double arcRadius = outer - arcThickness / 2.0;

        double normalised = Normalise(Value);
        double valueAngle = AngleFor(normalised);

        var trackPen = MakeRoundPen(TrackBrush, trackThickness);
        DrawArc(dc, trackPen, centre, arcRadius, StartAngleDeg, AngleFor(1.0));

        // A bipolar knob grows from the 12 o'clock mark (the midpoint of the sweep) so the arc
        // reads as a signed offset; a unipolar one fills from the minimum end-stop.
        double arcOrigin = IsBipolar ? AngleFor(0.5) : StartAngleDeg;
        var arcPen = MakeRoundPen(ArcBrush, arcThickness);
        DrawArc(dc, arcPen, centre, arcRadius, arcOrigin, valueAngle);

        double bodyRadius = arcRadius - arcThickness / 2.0 - Math.Max(1.5, outer * 0.08);
        if (bodyRadius > 0.5)
        {
            var rimPen = MakeRoundPen(TrackBrush, Math.Max(0.75, bodyRadius * 0.05));
            dc.DrawEllipse(KnobBrush, rimPen, centre, bodyRadius, bodyRadius);

            var pointerPen = MakeRoundPen(ArcBrush, Math.Max(1.5, bodyRadius * 0.15));
            dc.DrawLine(
                pointerPen,
                PointOnCircle(centre, bodyRadius * 0.30, valueAngle),
                PointOnCircle(centre, bodyRadius * 0.86, valueAngle));
        }

        if (IsKeyboardFocused)
        {
            var focusPen = new Pen(TextBrush, 1.0) { DashStyle = new DashStyle([2.0, 2.0], 0.0) };
            FreezeIfPossible(focusPen);
            dc.DrawEllipse(null, focusPen, centre, outer - 0.5, outer - 0.5);
        }
    }

    private void DrawCaptions(DrawingContext dc, double w, double h, double textBand, double pixelsPerDip)
    {
        // Caption size tracks the width so it stays proportionate across the 40→90 px range, but is
        // also capped by the band: two Segoe UI lines cost roughly 2.55× the em size in line boxes,
        // and without that cap a 90 px knob's text overruns the bottom edge by a couple of pixels.
        double labelSize = Math.Clamp(Math.Min(w * 0.17, textBand / 2.55), 7.0, 11.0);
        double valueSize = Math.Max(7.0, labelSize * 0.9);

        var brush = TextBrush;

        string label = Label;
        var labelText = string.IsNullOrEmpty(label) ? null : MakeText(label, labelSize, brush, pixelsPerDip, w);

        string valueText = ValueText;
        if (string.IsNullOrEmpty(valueText))
        {
            valueText = Value.ToString("0.##", CultureInfo.CurrentCulture);
        }

        var value = MakeText(valueText, valueSize, brush, pixelsPerDip, w);

        // Centre the block in its band so a knob with no label still reads as deliberately placed.
        double used = (labelText?.Height ?? 0.0) + value.Height;
        double y = h - textBand + Math.Max(0.0, (textBand - used) / 2.0);

        if (labelText is not null)
        {
            dc.DrawText(labelText, new Point((w - labelText.Width) / 2.0, y));
            y += labelText.Height;
        }

        dc.DrawText(value, new Point((w - value.Width) / 2.0, y));
    }

    private static FormattedText MakeText(string text, double emSize, Brush brush, double pixelsPerDip, double maxWidth) =>
        // TextFormattingMode.Display snaps glyphs to whole pixels, which is what keeps 7–11 px
        // captions crisp; the Ideal mode WPF defaults to is built for large print-like text.
        // numberSubstitution is genuinely optional here — null! only silences the annotation.
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextFace, emSize, brush,
            null!, TextFormattingMode.Display, pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    /// <summary>Maps a dial angle onto the circle, flipping Y for WPF's downward screen axis.</summary>
    private static Point PointOnCircle(Point centre, double radius, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(centre.X + radius * Math.Cos(rad), centre.Y - radius * Math.Sin(rad));
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point centre, double radius, double fromDeg, double toDeg)
    {
        double sweep = toDeg - fromDeg;
        if (radius <= 0.0 || Math.Abs(sweep) < 0.05)
        {
            return;
        }

        // Y was flipped in PointOnCircle, so a decreasing maths angle draws clockwise on screen.
        var direction = sweep < 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(centre, radius, fromDeg), isFilled: false, isClosed: false);
            ctx.ArcTo(
                PointOnCircle(centre, radius, toDeg),
                new Size(radius, radius),
                rotationAngle: 0.0,
                isLargeArc: Math.Abs(sweep) > 180.0,
                direction,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Pen MakeRoundPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        FreezeIfPossible(pen);
        return pen;
    }

    /// <summary>
    /// Freezes a pen so WPF can hand it to the render thread without a copy. Freezing recurses into
    /// the pen's brush, so it is guarded by <see cref="Freezable.CanFreeze"/> — an animated or
    /// data-bound brush supplied by the host reports false and is left alone.
    /// </summary>
    private static void FreezeIfPossible(Freezable f)
    {
        if (f.CanFreeze)
        {
            f.Freeze();
        }
    }

    private static bool IsUsable(double d) => !double.IsNaN(d) && !double.IsInfinity(d) && d > 0.0;

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    #endregion
}
