using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Benchmix.App.Controls;

/// <summary>How a tempo ramp is drawn: placed, only its first dot so far, or with its end tempo being typed.</summary>
public enum RampLook { Placed, StartOnly, Typing }

/// <summary>One tempo ramp as the lane draws it (Daniel, 2026-10-11).</summary>
public sealed record RampVisual(double StartSeconds, double EndSeconds, double FromBpm, double ToBpm, RampLook Look);

/// <summary>Where a ramp's dots and labels are, in lane pixels.</summary>
public sealed record RampGeometry(Point StartDot, Point EndDot, Rect StartLabel, Rect EndLabel, string StartText, string EndText);

/// <summary>
/// One place decides where a tempo ramp is drawn, so the lane's hit test and its drawing cannot
/// disagree. His words: "If it goes down, then first point goes on top line of track and second
/// point on bottom with yellow line connecting and vice versa".
/// </summary>
public static class RampLayout
{
    /// <summary>How far the dots sit inside the lane's top and bottom edges.</summary>
    public const double Inset = 7;
    public const double DotRadius = 4.5;
    /// <summary>How near a dot, either side, a press grabs it.</summary>
    public const double GrabPixels = 7;
    /// <summary>Within this many BPM a ramp is drawn level across the middle.</summary>
    public const double LevelBand = 0.05;

    public static readonly Color Yellow = Color.FromRgb(0xFF, 0xEB, 0x3B);

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private const double FontSize = 10.5, PadX = 3, PadY = 1;

    public static FormattedText Text(string s, double pixelsPerDip, Brush brush)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, brush, pixelsPerDip);

    public static RampGeometry Layout(RampVisual r, double pps, double scroll, double height, double pixelsPerDip)
    {
        double xs = (r.StartSeconds - scroll) * pps, xe = (r.EndSeconds - scroll) * pps;
        double top = Inset, bottom = Math.Max(Inset, height - Inset), mid = height / 2;

        double ys, ye;
        if (r.Look == RampLook.StartOnly) ys = ye = mid;
        else if (r.ToBpm < r.FromBpm - LevelBand) { ys = top; ye = bottom; }       // slowing down: from the top edge to the bottom
        else if (r.ToBpm > r.FromBpm + LevelBand) { ys = bottom; ye = top; }       // speeding up: from the bottom edge to the top
        else ys = ye = mid;

        string st = r.FromBpm.ToString("0.0", CultureInfo.InvariantCulture);
        string et = r.ToBpm.ToString("0.0", CultureInfo.InvariantCulture);
        var sf = Text(st, pixelsPerDip, Brushes.White);
        var ef = Text(et, pixelsPerDip, Brushes.White);
        double sw = sf.Width + 2 * PadX, sh = sf.Height + 2 * PadY;
        double ew = ef.Width + 2 * PadX, eh = ef.Height + 2 * PadY;

        // Inside the ramp when there is room, so a label never covers the next ramp's; outside otherwise.
        bool inside = r.Look != RampLook.StartOnly && xe - xs >= sw + ew + 20;
        double slx = inside || r.Look == RampLook.StartOnly ? xs + 7 : xs - 7 - sw;
        double elx = inside ? xe - 7 - ew : xe + 7;
        double LabelY(double y, double lh) => Math.Clamp(y - lh / 2, 1, Math.Max(1, height - lh - 1));

        return new RampGeometry(
            new Point(xs, ys), new Point(xe, ye),
            new Rect(slx, LabelY(ys, sh), sw, sh), new Rect(elx, LabelY(ye, eh), ew, eh),
            st, et);
    }

    /// <summary>Draws one ramp: the yellow line, its two dots and their tempos.</summary>
    public static void Draw(DrawingContext dc, RampVisual r, double pps, double scroll, double height, double pixelsPerDip)
    {
        var g = Layout(r, pps, scroll, height, pixelsPerDip);
        var yellow = new SolidColorBrush(Yellow);
        yellow.Freeze();

        if (r.Look != RampLook.StartOnly)
        {
            var line = new Pen(yellow, 2);
            if (r.Look == RampLook.Typing) line.DashStyle = DashStyles.Dash;
            line.Freeze();
            dc.DrawLine(line, g.StartDot, g.EndDot);
        }

        var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16)), 1);
        outline.Freeze();
        dc.DrawEllipse(yellow, outline, g.StartDot, DotRadius, DotRadius);
        if (r.Look != RampLook.StartOnly) dc.DrawEllipse(yellow, outline, g.EndDot, DotRadius, DotRadius);

        var back = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x11, 0x16));
        back.Freeze();
        void Label(Rect at, string text)
        {
            dc.DrawRoundedRectangle(back, null, at, 3, 3);
            dc.DrawText(Text(text, pixelsPerDip, yellow), new Point(at.X + PadX, at.Y + PadY));
        }
        Label(g.StartLabel, g.StartText);
        if (r.Look == RampLook.Placed) Label(g.EndLabel, g.EndText);     // while typing, the box shows the end tempo
    }
}
