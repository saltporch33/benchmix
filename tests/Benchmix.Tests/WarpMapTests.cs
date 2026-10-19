using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// The tempo map's warp stage (warp markers, Daniel, 2026-10-18): each marker pins a point of a
/// track's audio to a point of its tempo, and the audio between two markers is spread evenly.
/// Every expected value comes from the warp written out in <see cref="Spread"/> and the tempo
/// integral written out in <see cref="RampIntegral"/> — never from the map itself.
/// </summary>
public class WarpMapTests
{
    /// <summary>
    /// The warp written out: through the origin to the first marker, a straight line from each
    /// marker to the next, and the tempo's own pace (slope 1) after the last.
    /// </summary>
    static double Spread((double Audio, double Tempo)[] pins, double t)
    {
        double a0 = 0, t0 = 0;
        foreach (var (a, tp) in pins)
        {
            if (t < tp) return a0 + (a - a0) / (tp - t0) * (t - t0);
            (a0, t0) = (a, tp);
        }
        return a0 + (t - t0);
    }

    /// <summary>The position integral of one tempo ramp, as in <c>TempoMapTests</c>: v0 until a, a straight line to v1 at b, v1 after.</summary>
    static double RampIntegral(double x, double v0, double a, double b, double v1)
    {
        if (x < a) return v0 * x;
        if (x < b) return v0 * a + v0 * (x - a) + 0.5 * (v1 - v0) / (b - a) * (x - a) * (x - a);
        return v0 * a + (v0 + v1) / 2 * (b - a) + v1 * (x - b);
    }

    static TempoMap.Pin[] PinsOf((double Audio, double Tempo)[] pins) => pins.Select(p => new TempoMap.Pin(p.Audio, p.Tempo)).ToArray();

    // A song that drifts: the beat at 5:00 is still in time, the one at 6:00 is dragged 2400 frames
    // (50 ms) later, the one at 7:00 back into time. In frames at 48 kHz, at 0.93 on the knob.
    const double V = 0.93;
    static readonly (double Audio, double Tempo)[] Drift =
    {
        (300 * 48_000, 300 * 48_000),
        (360 * 48_000, 360 * 48_000 + 2400 * V),
        (420 * 48_000, 420 * 48_000),
    };

    [Fact]
    public void AMarkerNobodyHasMovedChangesNothing()
    {
        var tempo = TempoMap.Build(V, new[] { new TempoMap.Ramp(1_000_000, 2_000_000, 1.1) });
        var unmoved = new[] { new TempoMap.Pin(500_000, 500_000), new TempoMap.Pin(9_000_000, 9_000_000) };
        Assert.True(ReferenceEquals(TempoMap.Warped(tempo, unmoved), tempo) && ReferenceEquals(TempoMap.LenientWarped(tempo, unmoved), tempo)
                    && ReferenceEquals(TempoMap.Warped(tempo, ReadOnlySpan<TempoMap.Pin>.Empty), tempo),
            "a warp marker that was only placed made a new tempo map — placing one would put the track through different arithmetic and move it");
        var constant = TempoMap.Constant(1.0);
        Assert.True(ReferenceEquals(TempoMap.LenientWarped(constant, unmoved), constant) && TempoMap.LenientWarped(constant, unmoved).IsConstant,
            "an untouched track with an unmoved marker is no longer constant — it would go through the stretcher and stop being bit-identical");
    }

    [Fact]
    public void TheAudioAtAMarkerLandsExactlyWhereItWasDropped()
    {
        // Dropped on timeline frames: each marker's tempo is the tempo map's own position there.
        long[] dropped = { 2_200_000, 15_000_000, 18_582_000, 27_100_000 };
        var tempo = TempoMap.Constant(V);
        double[] audio = { 2_000_000, 14_000_000, 17_400_000, 25_000_000 };
        var pins = audio.Select((a, i) => new TempoMap.Pin(a, tempo.PositionAt(dropped[i]))).ToArray();
        var map = TempoMap.Warped(tempo, pins);
        for (int i = 0; i < pins.Length; i++)
            Assert.True(map.PositionAt(dropped[i]) == audio[i],
                $"the audio at marker {i} plays at {map.PositionAt(dropped[i])}, not {audio[i]}, at the frame it was dropped on — the beat would not land on the beat it was dragged to");
    }

    [Fact]
    public void BetweenMarkersTheAudioIsSpreadEvenly()
    {
        var map = TempoMap.Warped(TempoMap.Constant(V), PinsOf(Drift));
        for (double x = -1e6; x < 500 * 48_000 / V; x += 4_999.7)
        {
            double expected = Spread(Drift, V * x);
            Assert.True(Math.Abs(map.PositionAt(x) - expected) < 1e-6,
                $"at frame {x} the audio is at {map.PositionAt(x)}, the warp written out says {expected}");
            Assert.True(Math.Abs(map.RelAt(expected) - x) < 1e-6,
                $"at audio position {expected} the inverse says frame {map.RelAt(expected)}, not {x}");
        }
    }

    [Fact]
    public void BeforeTheFirstMarkerTheTrackIsStillAtZeroAtItsOrigin()
    {
        var pins = new[] { new TempoMap.Pin(480_000, 470_000), new TempoMap.Pin(960_000, 960_000) };
        foreach (var tempo in new[] { TempoMap.Constant(1.0), TempoMap.Constant(V), TempoMap.Build(1.0, new[] { new TempoMap.Ramp(-1000, 5000, 1.2) }) })
        {
            var map = TempoMap.Warped(tempo, pins);
            Assert.True(map.PositionAt(0) == 0 && map.RelAt(0) == 0,
                $"the warped track's position at its origin is {map.PositionAt(0)}, not 0 — the whole track would move");
        }
    }

    [Fact]
    public void AfterTheLastMarkerTheAudioPlaysAtItsTempoAgain()
    {
        var map = TempoMap.Warped(TempoMap.Constant(V), PinsOf(Drift));
        double last = map.RelAt(Drift[^1].Audio);
        foreach (double later in new[] { 1.0, 48_000, 48_000 * 600.0 })
        {
            Assert.True(map.SpeedAt(last + later) == V, $"{later} frames after the last marker the speed is {map.SpeedAt(last + later)}, not the knob's {V}");
            double moved = map.PositionAt(last + later) - Drift[^1].Audio;
            Assert.True(Math.Abs(moved - V * later) < 1e-6, $"{later} frames after the last marker the audio has moved {moved}, not {V * later}");
        }
    }

    [Fact]
    public void TheSpeedJumpsAtAMarkerByItsSlopesAndNowhereElse()
    {
        var map = TempoMap.Warped(TempoMap.Constant(V), PinsOf(Drift));
        double s0 = Drift[0].Audio / Drift[0].Tempo;
        double s1 = (Drift[1].Audio - Drift[0].Audio) / (Drift[1].Tempo - Drift[0].Tempo);
        double s2 = (Drift[2].Audio - Drift[1].Audio) / (Drift[2].Tempo - Drift[1].Tempo);
        double[] at = Drift.Select(p => p.Tempo / V).ToArray();
        Assert.True(Math.Abs(map.SpeedAt(at[0] - 1) - s0 * V) < 1e-12 && Math.Abs(map.SpeedAt(at[0] + 1) - s1 * V) < 1e-12
                    && Math.Abs(map.SpeedAt(at[1] + 1) - s2 * V) < 1e-12 && Math.Abs(map.SpeedAt(at[2] + 1) - V) < 1e-12,
            "between two markers the track does not play at the speed that spreads the audio between them evenly");
        Assert.True(Math.Abs(map.SpeedAt((at[0] + at[1]) / 2) - map.SpeedAt(at[0] + 10)) < 1e-12,
            "the speed changes between two markers — the audio between them would not be spread evenly");
    }

    [Fact]
    public void TheTracksPositionNeverStepsBackwardsAcrossAMarker()
    {
        var map = TempoMap.Warped(TempoMap.Constant(V), PinsOf(Drift));
        foreach (var p in Drift)
        {
            long centre = (long)(p.Tempo / V);
            for (long r = centre - 3000; r < centre + 3000; r++)
            {
                Assert.True(map.PositionAt(r + 1) > map.PositionAt(r), $"the position steps back at frame {r} — the render loop would play a frame of the song twice");
                Assert.True(map.TrackFrameAt(r + 1) >= map.TrackFrameAt(r), $"the track frame goes back at frame {r}");
            }
        }
    }

    [Fact]
    public void TheInverseAgreesWithTheRenderLoopAtEveryFrameAcrossAMarker()
    {
        var map = TempoMap.Warped(TempoMap.Constant(V), PinsOf(Drift));
        foreach (var p in Drift)
            for (long t = (long)p.Audio - 3000; t < (long)p.Audio + 3000; t++)
            {
                long r = map.FirstRelAt(t);
                Assert.True(map.TrackFrameAt(r) >= t && map.TrackFrameAt(r - 1) < t,
                    $"frame {t}: a section's end and the next one's start would disagree by a frame at a warp marker — the butt join goes unrecognised and clicks");

                long lo = r - 50, hi = r + 50;
                while (lo < hi) { long mid = lo + (hi - lo) / 2; if (Math.Floor(Spread(Drift, V * mid)) >= t) hi = mid; else lo = mid + 1; }
                Assert.True(Math.Abs(r - lo) <= 1, $"frame {t}: the map puts it at timeline frame {r}, the warp written out at {lo}");
            }
    }

    [Fact]
    public void AWarpOverATempoRampFollowsTheRamp()
    {
        const double v0 = 1.0, a = 48_000 * 100, b = 48_000 * 130, v1 = 1.05;
        var tempo = TempoMap.Build(v0, new[] { new TempoMap.Ramp(a, b, v1) });
        // Markers either side of the ramp and one inside it, the one inside moved.
        (double, double)[] pins =
        {
            (RampIntegral(48_000 * 90, v0, a, b, v1), RampIntegral(48_000 * 90, v0, a, b, v1)),
            (RampIntegral(48_000 * 115, v0, a, b, v1), RampIntegral(48_000 * 115, v0, a, b, v1) + 1500),
            (RampIntegral(48_000 * 140, v0, a, b, v1), RampIntegral(48_000 * 140, v0, a, b, v1)),
        };
        var map = TempoMap.Warped(tempo, PinsOf(pins));
        for (double x = 48_000 * 80; x < 48_000 * 150; x += 3_331.3)
        {
            double expected = Spread(pins, RampIntegral(x, v0, a, b, v1));
            Assert.True(Math.Abs(map.PositionAt(x) - expected) < 1e-5,
                $"at frame {x} under a ramp the warped audio is at {map.PositionAt(x)}, the ramp and warp written out say {expected}");
        }
        double after = 48_000 * 145;
        Assert.True(Math.Abs(map.SpeedAt(after) - v1) < 1e-12, "after the last marker the track does not play at the tempo its ramp left it at");
    }

    [Fact]
    public void TheFastestAndSlowestSpeedsCountBothSidesOfAMarker()
    {
        // Squeezed before the marker, stretched after it.
        var pins = new[] { new TempoMap.Pin(100_000, 90_000), new TempoMap.Pin(200_000, 200_000) };
        var map = TempoMap.Warped(TempoMap.Constant(1.0), pins);
        double before = 100_000.0 / 90_000, after = 100_000.0 / 110_000;
        Assert.True(Math.Abs(map.MaxSpeedOver(80_000, 100_000) - before) < 1e-12,
            "the faster side of a marker was missed — the resampler would alias there");
        Assert.True(Math.Abs(map.MaxSpeedOver(95_000, 96_000) - after) < 1e-12 && Math.Abs(map.MaxSpeedOver(0, 50_000) - before) < 1e-12,
            "the fastest speed between two markers is not the speed between them");
        Assert.True(Math.Abs(map.MaxSpeed - before) < 1e-12 && Math.Abs(map.MinSpeed - after) < 1e-12,
            $"the map's range is {map.MinSpeed}–{map.MaxSpeed}, not {after}–{before}: the voice would refuse it, or the kernels would be too narrow");
    }

    [Fact]
    public void ImpossibleMarkersAreRefused_AndTheLenientBuilderDropsThem()
    {
        var tempo = TempoMap.Constant(1.0);
        Assert.Throws<ArgumentException>(() => TempoMap.Warped(tempo, new[] { new TempoMap.Pin(100, 90), new TempoMap.Pin(90, 200) }));     // out of order
        Assert.Throws<ArgumentException>(() => TempoMap.Warped(tempo, new[] { new TempoMap.Pin(100, 90), new TempoMap.Pin(200, 80) }));     // tempo goes back
        Assert.Throws<ArgumentException>(() => TempoMap.Warped(tempo, new[] { new TempoMap.Pin(100, 30) }));                                // three times as fast
        Assert.Throws<ArgumentException>(() => TempoMap.Warped(tempo, new[] { new TempoMap.Pin(0, 0) }));                                   // at the origin
        Assert.Throws<ArgumentException>(() => TempoMap.Warped(TempoMap.Warped(tempo, new[] { new TempoMap.Pin(100, 90) }), new[] { new TempoMap.Pin(100, 90) }));
        Assert.Throws<ArgumentException>(() => TempoMap.Warped(TempoMap.Constant(3.9), new[] { new TempoMap.Pin(100, 60) }));             // 6.5×: beyond the engine

        var lenient = TempoMap.LenientWarped(tempo, new[] { new TempoMap.Pin(300, 290), new TempoMap.Pin(100, 30), new TempoMap.Pin(200, 210), new TempoMap.Pin(double.NaN, 5) });
        Assert.True(lenient.IsWarped && Math.Abs(lenient.PositionAt(210) - 200) < 1e-9 && Math.Abs(lenient.PositionAt(290) - 300) < 1e-9 && lenient.PositionAt(0) == 0,
            "the lenient builder kept an impossible marker, or dropped a good one — a damaged project file would play wrong or crash");
        var fast = TempoMap.Constant(3.9);
        Assert.True(ReferenceEquals(TempoMap.LenientWarped(fast, new[] { new TempoMap.Pin(100, 60) }), fast),
            "the lenient builder made a map faster than the engine can play — the voice would throw on it");
    }

    [Fact]
    public void AMarkerPutDownWhereNothingHasMovedIsUnmoved_AndOneOnAMovedStretchMovesNothing()
    {
        var rng = new Random(5);
        // In seconds on whole frames, as the editor puts markers down: there a0 + (a − a0) is often a
        // hair off a, so only the exact answer for an untouched stretch keeps a new marker unmoved
        // (review, 2026-10-18: in whole frames this could not fail).
        var unmoved = new[] { new TempoMap.Pin(4801 / 48000.0, 4801 / 48000.0), new TempoMap.Pin(9601 / 48000.0, 9601 / 48000.0) };
        // Moved after 6 s only: up to the unmoved marker at 0.2 s, nothing has moved.
        var moved = new[] { unmoved[0], unmoved[1], new TempoMap.Pin(6.0, 6 + 1700 / 48000.0) };
        // Within the first ten seconds, where a hair is common: a few percent of frames there.
        for (int k = 0; k < 4000; k++)
        {
            double a = Math.Round(rng.NextDouble() * 10 * 48_000) / 48_000;
            Assert.True(TempoMap.TempoOf(ReadOnlySpan<TempoMap.Pin>.Empty, a) == a && TempoMap.TempoOf(unmoved, a) == a,
                $"a marker put down at {a} on a track nothing has moved is not exactly unmoved — placing it would put the track through the stretcher");
            if (a < unmoved[1].Audio)
                Assert.True(TempoMap.TempoOf(moved, a) == a, $"a marker put down at {a}, before anything moved, is not exactly unmoved");
        }

        var tempo = TempoMap.Constant(V);
        var before = TempoMap.Warped(tempo, PinsOf(Drift));
        foreach (double a in new[] { 100.0 * 48_000, 330.5 * 48_000, 391.25 * 48_000, 600.0 * 48_000 })
        {
            var pins = PinsOf(Drift).Append(new TempoMap.Pin(a, TempoMap.TempoOf(PinsOf(Drift), a))).OrderBy(p => p.Audio).ToArray();
            var after = TempoMap.Warped(tempo, pins);
            for (double x = 0; x < 700 * 48_000; x += 7_919.3)
                Assert.True(Math.Abs(after.PositionAt(x) - before.PositionAt(x)) < 1e-6,
                    $"a marker put down at {a} moved the audio at frame {x} by {after.PositionAt(x) - before.PositionAt(x)} frames — placing a marker must change nothing");
        }
    }

    [Fact]
    public void TryWarpedRefusesWhatWarpedRefuses_WithoutThrowing()
    {
        var tempo = TempoMap.Constant(1.0);
        Assert.True(!TempoMap.TryWarped(tempo, new[] { new TempoMap.Pin(100, 30) }, out var m1) && ReferenceEquals(m1, tempo)
                    && !TempoMap.TryWarped(TempoMap.Constant(3.9), new[] { new TempoMap.Pin(100, 60) }, out _)
                    && TempoMap.TryWarped(tempo, new[] { new TempoMap.Pin(100, 100) }, out var m2) && ReferenceEquals(m2, tempo)
                    && TempoMap.TryWarped(tempo, new[] { new TempoMap.Pin(100, 95) }, out var m3) && m3.IsWarped,
            "the warp check a drag uses disagrees with the builder — a drag could stop short of a good place, or reach an impossible one");
    }

    [Fact]
    public void ADifferentWarpIsADifferentMap()
    {
        var tempo = TempoMap.Constant(1.0);
        var a = TempoMap.Warped(tempo, new[] { new TempoMap.Pin(1000, 990) });
        var same = TempoMap.Warped(TempoMap.Constant(1.0), new[] { new TempoMap.Pin(1000, 990) });
        var other = TempoMap.Warped(tempo, new[] { new TempoMap.Pin(1000, 980) });
        Assert.True(a.SameAs(same) && !a.SameAs(other) && !a.SameAs(tempo) && !tempo.SameAs(a),
            "a moved warp marker was not seen as a change — the voice would keep playing the old timing");
        Assert.True(ReferenceEquals(a.Unwarped, tempo) && ReferenceEquals(tempo.Unwarped, tempo), "the map without its warp is not the tempo it was built on");
    }
}
