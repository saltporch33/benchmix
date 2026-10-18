using System.Windows;
using System.Windows.Media;

namespace Benchmix.App.Controls;

/// <summary>
/// One warp marker as the lane draws it (Daniel, 2026-10-18): where its beat is heard on the
/// timeline, where the stretch of audio leading up to it starts — the marker before it, or the
/// track's start — and how much that stretch is spread ("+0.8%"), null when it is not.
/// </summary>
public sealed record WarpVisual(double Seconds, double FromSeconds, string? Label);

/// <summary>
/// One place decides where a warp marker is drawn and where a press grabs it, so the lane's hit
/// test and its drawing cannot disagree (as <see cref="RampLayout"/> does for ramps).
/// </summary>
public static class WarpLayout
{
    /// <summary>How near a marker's line, either side, a press grabs it rather than putting a new one down.</summary>
    public const double GrabPixels = 5;

    public const double LineWidth = 1.5;

    /// <summary>The handle at the top of the line: a small triangle pointing down at the beat.</summary>
    public const double HandleWidth = 10, HandleHeight = 7;

    private const double PadX = 3, PadY = 1;

    /// <summary>Where a marker's line is, in lane pixels.</summary>
    public static double X(WarpVisual w, double pps, double scroll) => (w.Seconds - scroll) * pps;

    /// <summary>The marker whose line is nearest a lane x, within <see cref="GrabPixels"/>; −1 when none is.</summary>
    public static int IndexAt(IReadOnlyList<WarpVisual> warps, double x, double pps, double scroll)
    {
        int best = -1;
        double distance = GrabPixels;
        for (int i = 0; i < warps.Count; i++)
        {
            double d = Math.Abs(X(warps[i], pps, scroll) - x);
            if (d <= distance) { distance = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Draws a track's markers: each a thin line the height of the lane with its handle at the top,
    /// and over each stretch that is spread, how much, at the top of the lane between its two ends.
    /// A label that does not fit between them at this zoom is left out rather than drawn over its
    /// neighbours. Only what is in view is drawn.
    /// </summary>
    public static void Draw(DrawingContext dc, IReadOnlyList<WarpVisual> warps, Brush brush,
                            double pps, double scroll, double width, double height, double pixelsPerDip)
    {
        var dark = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x11, 0x16));
        dark.Freeze();
        // A dark edge either side of the line, so it reads over a waveform of any colour.
        var backing = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x0E, 0x11, 0x16)), LineWidth + 2);
        backing.Freeze();
        var line = new Pen(brush, LineWidth);
        line.Freeze();
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16)), 1);
        outline.Freeze();

        foreach (var w in warps)
        {
            double x = X(w, pps, scroll);

            // The stretch's label first, so the lines and handles stay on top of it.
            if (w.Label is { } label)
            {
                double from = (w.FromSeconds - scroll) * pps;
                if (x >= 0 && from <= width)
                {
                    var text = RampLayout.Text(label, pixelsPerDip, brush);
                    double lw = text.Width + 2 * PadX, lh = text.Height + 2 * PadY;
                    if (lw + HandleWidth + 4 <= x - from)
                    {
                        var at = new Rect((from + x) / 2 - lw / 2, 1, lw, lh);
                        dc.DrawRoundedRectangle(dark, null, at, 3, 3);
                        dc.DrawText(text, new Point(at.X + PadX, at.Y + PadY));
                    }
                }
            }

            if (x < -HandleWidth || x > width + HandleWidth) continue;
            dc.DrawLine(backing, new Point(x, 0), new Point(x, height));
            dc.DrawLine(line, new Point(x, 0), new Point(x, height));

            var handle = new StreamGeometry();
            using (var ctx = handle.Open())
            {
                ctx.BeginFigure(new Point(x - HandleWidth / 2, 0), true, true);
                ctx.LineTo(new Point(x + HandleWidth / 2, 0), true, false);
                ctx.LineTo(new Point(x, HandleHeight), true, false);
            }
            handle.Freeze();
            dc.DrawGeometry(brush, outline, handle);
        }
    }
}
