using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// The tempo map (tempo ramps, Daniel, 2026-10-11): where a track has got to in its own audio at
/// each point of the timeline. Every expected value here comes from the integral written out in
/// <see cref="Analytic"/> or from the arithmetic the map replaced — never from the map itself.
/// </summary>
public class TempoMapTests
{
    /// <summary>The position integral of one ramp, written out: v0 until a, a straight line to v1 at b, v1 after.</summary>
    static double Analytic(double x, double v0, double a, double b, double v1)
    {
        if (x < a) return v0 * x;
        if (x < b) return v0 * a + v0 * (x - a) + 0.5 * (v1 - v0) / (b - a) * (x - a) * (x - a);
        return v0 * a + (v0 + v1) / 2 * (b - a) + v1 * (x - b);
    }

    /// <summary>The render loop's old inverse, copied from TrackVoice before tempo maps.</summary>
    static long OldFirstRelAt(long trackFrame, double speed)
    {
        long r = (long)Math.Ceiling(trackFrame / speed);
        while ((long)Math.Floor(r * speed) < trackFrame) r++;
        while ((long)Math.Floor((r - 1) * speed) >= trackFrame) r--;
        return r;
    }

    static readonly double[] Speeds = { 0.25, 0.5, 0.7, 0.92, 0.93, 1.0, 1.05, 1.07, 1.25, 4.0 };

    [Fact]
    public void AMapWithNoRampsIsExactlyTheArithmeticItReplaced()
    {
        var rng = new Random(11);
        foreach (double v in Speeds)
        {
            var map = TempoMap.Constant(v);
            var rels = Enumerable.Range(0, 4000).Select(_ => (long)((rng.NextDouble() * 2 - 1) * 1e8))
                                 .Concat(Enumerable.Range(14330, 30).Select(i => (long)i)).ToList();
            foreach (long r in rels)
            {
                Assert.True(map.PositionAt(r) == r * v && map.TrackFrameAt(r) == (long)Math.Floor(r * v) && map.RelAt(r) == r / v
                            && map.DurationOf(r, 1234567) == 1234567 / v && map.FirstRelAt(r) == OldFirstRelAt(r, v),
                    $"at speed {v}, frame {r}, a track with no tempo ramp maps differently from yesterday's build — every saved mashup would move");
            }
        }
    }

    [Fact]
    public void BeforeTheFirstRampTheMapIsStillExactlyTheKnob()
    {
        const double v0 = 0.93;
        var map = TempoMap.Build(v0, new[] { new TempoMap.Ramp(1_000_000, 2_000_000, 1.1) });
        for (long x = -500_000; x < 1_000_000; x += 997)
        {
            Assert.True(map.PositionAt(x) == x * v0, $"at frame {x}, before the ramp, the position is not the knob's — sections before the ramp would move a hair");
            double p = x * v0;
            Assert.True(map.RelAt(p) == p / v0, $"at position {p}, before the ramp, the inverse is not the knob's");
        }
    }

    static IEnumerable<(double V0, double A, double B, double V1)> Ramps()
    {
        // Gentle and steep, up and down: slopes from about 1e-8 to 5e-6 of speed per frame.
        yield return (1.0, 48_000, 48_000 * 31, 115.0 / 110);
        yield return (0.958, 96_000, 96_000 + 480_000, 1.0);
        yield return (1.10, 10_000, 10_000 + 48_000, 0.90);
        yield return (0.92, 0, 200_000, 1.25);
        yield return (2.0, 5_000, 5_000 + 300_000, 0.5);
    }

    [Fact]
    public void TheTracksPositionNeverStepsBackwards()
    {
        foreach (var (v0, a, b, v1) in Ramps())
        {
            var map = TempoMap.Build(v0, new[] { new TempoMap.Ramp(a, b, v1) });
            foreach (double edge in new[] { a, b })
                for (long r = (long)edge - 3000; r < (long)edge + 3000; r++)
                {
                    Assert.True(map.PositionAt(r + 1) - map.PositionAt(r) >= TempoMap.LowestSpeed - 1e-9,
                        $"the position steps back at frame {r} — the render loop would play a frame of the song twice");
                    Assert.True(map.TrackFrameAt(r + 1) >= map.TrackFrameAt(r), $"the track frame goes back at frame {r}");
                }
        }
    }

    [Fact]
    public void TheInverseAgreesWithTheRenderLoopAtEveryFrame()
    {
        foreach (var (v0, a, b, v1) in Ramps())
        {
            var map = TempoMap.Build(v0, new[] { new TempoMap.Ramp(a, b, v1) });
            foreach (double edgeX in new[] { a, (a + b) / 2, b })
            {
                long centre = (long)Analytic(edgeX, v0, a, b, v1);
                for (long t = centre - 3000; t < centre + 3000; t++)
                {
                    long r = map.FirstRelAt(t);
                    Assert.True(map.TrackFrameAt(r) >= t && map.TrackFrameAt(r - 1) < t,
                        $"frame {t}: a section's end and the next one's start would disagree by a frame — the butt join goes unrecognised and clicks");

                    // The first frame whose analytic position reaches t, by bisection.
                    long lo = r - 50, hi = r + 50;
                    while (lo < hi) { long mid = lo + (hi - lo) / 2; if (Math.Floor(Analytic(mid, v0, a, b, v1)) >= t) hi = mid; else lo = mid + 1; }
                    Assert.True(Math.Abs(r - lo) <= 1, $"frame {t}: the map puts it at timeline frame {r}, the integral at {lo}");
                }
            }
        }
    }

    [Fact]
    public void TheTempoNeverJumpsAtARampsEdges()
    {
        var maps = new[]
        {
            TempoMap.Build(1.0, new[] { new TempoMap.Ramp(10_000, 60_000, 1.2) }),
            TempoMap.Build(1.0, new[] { new TempoMap.Ramp(10_000, 60_000, 1.2), new TempoMap.Ramp(60_000, 90_000, 0.8) }),   // butted
            TempoMap.Build(0.9, new[] { new TempoMap.Ramp(10_000, 60_000, 1.2), new TempoMap.Ramp(80_000, 90_000, 1.0) }),
        };
        foreach (var map in maps)
            foreach (double edge in new double[] { 10_000, 60_000, 80_000, 90_000 })
            {
                Assert.True(Math.Abs(map.SpeedAt(edge - 1e-6) - map.SpeedAt(edge)) < 1e-9,
                    $"the tempo jumps at {edge} — a ramp exists so that it never jumps");
                Assert.True(Math.Abs(map.PositionAt(edge - 1e-6) - map.PositionAt(edge)) < 1e-5,
                    $"the position jumps at {edge}: the song would skip there");
            }
    }

    [Fact]
    public void ARampGoesEvenlyInBpmAndHoldsAfterwards()
    {
        const double v0 = 1.0, v1 = 115.0 / 110, a = 48_000, b = 48_000 * 31;
        var map = TempoMap.Build(v0, new[] { new TempoMap.Ramp(a, b, v1) });
        Assert.True(Math.Abs(map.SpeedAt((a + b) / 2) - (v0 + v1) / 2) < 1e-12, "half way through, the tempo is not half way between: the ramp does not go evenly");
        Assert.True(Math.Abs(map.PositionAt(b) - (v0 * a + (v0 + v1) / 2 * (b - a))) < 1e-6, "at the ramp's end the song is not where the ramp puts it");
        Assert.True(map.SpeedAt(b + 1e6) == v1 && map.SpeedAt(b + 1e9) == v1, "after the ramp the tempo does not hold where the ramp left it");
    }

    [Fact]
    public void ARampThatBeginsBeforeTheOriginIsMeasuredFromTheOrigin()
    {
        var map = TempoMap.Build(1.0, new[] { new TempoMap.Ramp(-1000, 5000, 1.2) });
        Assert.True(map.PositionAt(0) == 0, $"the track's position at its origin is {map.PositionAt(0)}, not 0: a ramp before where the song was dropped shifts the whole track");
        for (double x = -2000; x < 8000; x += 37.5)
            Assert.True(Math.Abs(map.RelAt(map.PositionAt(x)) - x) < 1e-6, $"at {x} the inverse does not undo the position");
    }

    [Fact]
    public void ImpossibleRampsAreRefused_AndTheLenientBuilderDropsThem()
    {
        Assert.Throws<ArgumentException>(() => TempoMap.Build(1.0, new[] { new TempoMap.Ramp(0, 10, 1.1), new TempoMap.Ramp(5, 20, 1.2) }));
        Assert.Throws<ArgumentException>(() => TempoMap.Build(1.0, new[] { new TempoMap.Ramp(10, 10, 1.1) }));
        Assert.Throws<ArgumentException>(() => TempoMap.Build(1.0, new[] { new TempoMap.Ramp(10, 0, 1.1) }));
        Assert.Throws<ArgumentException>(() => TempoMap.Build(1.0, new[] { new TempoMap.Ramp(0, 10, 0) }));

        var lenient = TempoMap.Lenient(1.0, new[] { new TempoMap.Ramp(5, 20, 9.0), new TempoMap.Ramp(0, 10, 1.1), new TempoMap.Ramp(30, 30, 2) });
        Assert.True(lenient.MaxSpeed == 1.1 && lenient.SpeedAt(10) == 1.1,
            "the lenient builder kept an overlapping or empty ramp, or did not sort them — a bad project file would play wrong or crash");
    }

    [Fact]
    public void TheFastestSpeedInARunIsFound()
    {
        var map = TempoMap.Build(1.0, new[] { new TempoMap.Ramp(1000, 2000, 1.5), new TempoMap.Ramp(2000, 3000, 1.0) });
        Assert.True(map.MaxSpeedOver(1500, 2500) == 1.5, "the peak at 2000 was missed — the resampler would alias on the fast end of a cell");
        Assert.True(map.MaxSpeedOver(0, 900) == 1.0);
    }
}
