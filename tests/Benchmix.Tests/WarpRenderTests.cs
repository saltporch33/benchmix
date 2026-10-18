using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// A track with warp markers (Daniel, 2026-10-18), rendered through the real voice. Where each
/// moment of the song should come out is the warp written out in <see cref="Spread"/> over the tempo
/// written out in <see cref="F"/> — never the tempo map — and it is measured on the audio, as
/// <c>TempoRampRenderTests</c> does for a tempo ramp.
/// </summary>
public class WarpRenderTests
{
    const int Rate = 48000;
    private readonly ITestOutputHelper _out;
    public WarpRenderTests(ITestOutputHelper output) => _out = output;

    /// <summary>The track frame its tempo has reached at timeline frame x: v0 until a, a straight line in speed to v1 at b, v1 after.</summary>
    static double F(double x, double v0, double a, double b, double v1)
    {
        if (x < a) return v0 * x;
        if (x < b) return v0 * a + v0 * (x - a) + 0.5 * (v1 - v0) / (b - a) * (x - a) * (x - a);
        return v0 * a + (v0 + v1) / 2 * (b - a) + v1 * (x - b);
    }

    /// <summary>The inverse of <see cref="F"/>: the timeline frame at which the tempo reaches p.</summary>
    static double InverseF(double p, double v0, double a, double b, double v1)
    {
        double atA = v0 * a, atB = atA + (v0 + v1) / 2 * (b - a);
        if (p < atA) return p / v0;
        if (p >= atB) return b + (p - atB) / v1;
        double k = (v1 - v0) / (b - a), dp = p - atA;
        return a + 2 * dp / (v0 + Math.Sqrt(v0 * v0 + 2 * k * dp));
    }

    /// <summary>
    /// The warp written out: where the audio is when the tempo has got to t — through the origin to
    /// the first marker, a straight line from each marker to the next, and the tempo's own pace after the last.
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

    static TempoMap.Pin[] ToPins((double Audio, double Tempo)[] pins) => pins.Select(p => new TempoMap.Pin(p.Audio, p.Tempo)).ToArray();

    /// <summary>A 431 Hz carrier — not 440, which lines up with the vocoder's delay — with smooth swells at these source times.</summary>
    static AudioClip Swells(double seconds, double[] atSourceSeconds, double sigma = 0.04)
    {
        int frames = (int)(seconds * Rate);
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            double t = f / (double)Rate;
            double env = 0.35;
            foreach (double at in atSourceSeconds) { double d = (t - at) / sigma; env += 0.65 * Math.Exp(-d * d); }
            float v = (float)(0.6 * env * Math.Sin(2 * Math.PI * 431 * t));
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        return AudioClip.FromSamples("swells", s, Rate);
    }

    /// <summary>A ramp: value == position, so where a frame came from is in the frame itself.</summary>
    static AudioClip RampSource(int frames)
    {
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = f / (float)frames;
        return AudioClip.FromSamples("ramp", s, Rate);
    }

    static (float[] Buf, TrackVoice Voice) Render(Action<TrackVoice> configure, int frames, int block, long seekTo = 0)
    {
        var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        configure(v);
        graph.LimiterEnabled = false;
        graph.Seek(seekTo);
        graph.SnapParameters();
        var buf = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(block, frames - done);
            graph.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return (buf, v);
    }

    const double EnvelopeCoeff = 0.0008;

    static double[] Envelope(ReadOnlySpan<float> interleaved)
    {
        var env = new double[interleaved.Length / 2];
        double state = 0;
        for (int f = 0; f < env.Length; f++) { state += (Math.Abs(interleaved[f * 2]) - state) * EnvelopeCoeff; env[f] = state; }
        return env;
    }

    static double[] Smooth(double[] x)
    {
        var y = new double[x.Length];
        double state = 0;
        for (int i = 0; i < x.Length; i++) { state += (x[i] - state) * EnvelopeCoeff; y[i] = state; }
        return y;
    }

    static int BestLag(double[] reference, double[] signal, int maxLag, int from, int length)
    {
        double refMean = 0, sigMean = 0;
        for (int i = from; i < from + length; i++) { refMean += reference[i]; sigMean += signal[i]; }
        refMean /= length;
        sigMean /= length;
        double best = double.NegativeInfinity;
        int bestLag = 0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            int n = 0;
            for (int i = from; i < from + length; i++)
            {
                int j = i + lag;
                if (j < 0 || j >= signal.Length) continue;
                sum += (reference[i] - refMean) * (signal[j] - sigMean);
                n++;
            }
            if (n > 0 && sum / n > best) { best = sum / n; bestLag = lag; }
        }
        return bestLag;
    }

    /// <summary>The envelope the swells at <paramref name="sourceSeconds"/> should make, frame by frame, with the audio at <paramref name="source"/>(frame).</summary>
    static double[] ExpectedEnvelope(int frames, Func<long, double> source, double[] sourceSeconds, long from = 0)
    {
        var expected = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double tSrc = source(from + f) / Rate, env = 0.35;
            foreach (double at in sourceSeconds) { double d = (tSrc - at) / 0.04; env += 0.65 * Math.Exp(-d * d); }
            expected[f] = env;
        }
        return Smooth(expected);
    }

    // ---------------------------------------------------------------- the cases

    /// <summary>VocoderWarpAtOwnTempo is a song left at its own tempo and only warped: the warp alone sends it through the stretcher.</summary>
    public enum Kind { VocoderWarp, LinkWarp, VocoderWarpOverRamp, LinkWarpOverRamp, VocoderWarpAtOwnTempo }

    sealed record Case(StretchMode Mode, double V0, (double A, double B, double V1)? Ramp)
    {
        /// <summary>How far the tempo alone has carried the track at timeline frame x, written out.</summary>
        public double Tempo(double x) => Ramp is { } r ? F(x, V0, r.A, r.B, r.V1) : V0 * x;
        public double TimelineOf(double tempo) => Ramp is { } r ? InverseF(tempo, V0, r.A, r.B, r.V1) : tempo / V0;
        public TempoMap Map => Ramp is { } r ? TempoMap.Build(V0, new[] { new TempoMap.Ramp(r.A, r.B, r.V1) }) : TempoMap.Constant(V0);
    }

    static Case Setup(Kind kind) => kind switch
    {
        Kind.VocoderWarp => new(StretchMode.Independent, 0.95, null),
        Kind.LinkWarp => new(StretchMode.Linked, 0.95, null),
        Kind.VocoderWarpOverRamp => new(StretchMode.Independent, 0.95, (3.0 * Rate, 11.0 * Rate, 1.06)),
        Kind.LinkWarpOverRamp => new(StretchMode.Linked, 0.95, (3.0 * Rate, 11.0 * Rate, 1.06)),
        _ => new(StretchMode.Independent, 1.0, null),
    };

    // Markers heard at 4 s, 9 s and 13 s: the audio before the first 3% faster than its tempo,
    // between the first two 4% slower, between the last two 5% faster, and at its tempo after.
    static readonly double[] MarkerSeconds = { 4, 9, 13 };
    static readonly double[] Slopes = { 1.03, 0.96, 1.05 };

    /// <summary>The markers, in frames: each one's audio on a whole frame, as the editor puts it, and its tempo where it is heard.</summary>
    static (double Audio, double Tempo)[] PinsFor(Case c)
    {
        var pins = new (double, double)[MarkerSeconds.Length];
        double a = 0, tp = 0;
        for (int i = 0; i < pins.Length; i++)
        {
            double t = c.Tempo(MarkerSeconds[i] * Rate);
            a = Math.Round(a + Slopes[i] * (t - tp));
            pins[i] = (a, t);
            tp = t;
        }
        return pins;
    }

    static TempoMap WarpedMap(Case c) => TempoMap.Warped(c.Map, ToPins(PinsFor(c)));

    [Theory]
    [InlineData(Kind.VocoderWarp, 4096)]
    [InlineData(Kind.VocoderWarp, 1000)]
    [InlineData(Kind.LinkWarp, 4096)]
    [InlineData(Kind.LinkWarp, 1000)]
    [InlineData(Kind.VocoderWarpOverRamp, 4096)]
    [InlineData(Kind.VocoderWarpOverRamp, 1000)]
    [InlineData(Kind.LinkWarpOverRamp, 4096)]
    [InlineData(Kind.LinkWarpOverRamp, 1000)]
    [InlineData(Kind.VocoderWarpAtOwnTempo, 4096)]
    [InlineData(Kind.VocoderWarpAtOwnTempo, 1000)]
    public void AWarpedTrackStaysAlignedAllTheWayThrough(Kind kind, int block)
    {
        var c = Setup(kind);
        var pins = PinsFor(c);
        double Source(long frame) => Spread(pins, c.Tempo(frame));
        // Swells before the first marker, between each two, and after the last.
        double[] outputSeconds = { 1.0, 2.5, 5.5, 7.5, 10.5, 12.0, 14.5, 16.5 };
        double[] sourceSeconds = outputSeconds.Select(o => Source((long)(o * Rate)) / Rate).ToArray();
        var clip = Swells(24, sourceSeconds);
        int frames = 18 * Rate;

        var (buf, _) = Render(v =>
        {
            v.SetClip(clip);
            v.Mode = c.Mode;
            v.Tempo = TempoMap.Warped(c.Map, ToPins(pins));
        }, frames, block);

        var expected = ExpectedEnvelope(frames, Source, sourceSeconds);
        var actual = Envelope(buf);
        foreach (double o in outputSeconds)
        {
            int centre = (int)(o * Rate);
            int lag = BestLag(expected, actual, 4000, Math.Max(Rate / 2, centre - 24000), 48000);
            _out.WriteLine($"{kind}, blocks of {block}: the swell at {o:0.0}s lands {lag} frames off");
            Assert.True(Math.Abs(lag) < 200,
                $"{o:0.0}s into a {kind} track the audio is {lag} frames ({lag * 1000.0 / Rate:F1} ms) off where the warp markers put it — "
              + "the beat he dragged into time with the other deck is not where he put it");
        }
    }

    [Theory]
    [InlineData(Kind.VocoderWarp, 333)]
    [InlineData(Kind.VocoderWarp, 1000)]
    [InlineData(Kind.VocoderWarp, 4096)]
    [InlineData(Kind.LinkWarp, 333)]
    [InlineData(Kind.LinkWarp, 4096)]
    [InlineData(Kind.VocoderWarpOverRamp, 1000)]
    [InlineData(Kind.LinkWarpOverRamp, 1000)]
    [InlineData(Kind.VocoderWarpAtOwnTempo, 1000)]
    public void AMovedWarpMarkerNeverRestartsTheStretcherPartWay(Kind kind, int block)
    {
        var c = Setup(kind);
        var clip = Swells(32, new[] { 5.0 });
        var (_, voice) = Render(v =>
        {
            v.SetClip(clip);
            v.Mode = c.Mode;
            v.Tempo = WarpedMap(c);
        }, 20 * Rate, block);
        Assert.True(voice.RepositionCount == 1,
            $"the stretcher restarted {voice.RepositionCount - 1} times inside one section — each restart is a jump in the audio where a warp marker is");
    }

    [Theory]
    [InlineData(Kind.LinkWarp)]
    [InlineData(Kind.LinkWarpOverRamp)]
    public void OnLinkAWarpedTrackSoundsTheSameAtEveryBlockSize(Kind kind)
    {
        var map = WarpedMap(Setup(kind));
        var clip = RampSource(24 * Rate);
        float[] At(int block) => Render(v =>
        {
            v.SetClip(clip);
            v.Mode = StretchMode.Linked;
            v.Tempo = map;
        }, 18 * Rate, block).Buf;

        var small = At(333);
        foreach (int block in new[] { 1000, 4096, 8192 })
        {
            var other = At(block);
            int differing = Enumerable.Range(0, small.Length).Count(i => small[i] != other[i]);
            Assert.True(differing == 0, $"{differing} samples differ between blocks of 333 and {block}: preview and the rendered file would not match on a warped track");
        }
    }

    [Theory]
    [InlineData(Kind.LinkWarp)]
    [InlineData(Kind.LinkWarpOverRamp)]
    public void ASeekIntoAWarpedStretchOnLinkPlaysExactlyWhatPlayingThroughDoes(Kind kind)
    {
        var map = WarpedMap(Setup(kind));
        var clip = RampSource(24 * Rate);
        void Configure(TrackVoice v)
        {
            v.SetClip(clip);
            v.Mode = StretchMode.Linked;
            v.Tempo = map;
        }
        long seek = 8 * Rate + 123;                                     // between the markers at 4 s and 9 s; plays on across 9 s and 13 s
        var through = Render(Configure, 14 * Rate, 1000).Buf;
        var jumped = Render(Configure, 5 * Rate, 1000, seekTo: seek).Buf;

        int differing = 0;
        for (int i = 0; i < jumped.Length; i++) if (jumped[i] != through[seek * 2 + i]) differing++;
        Assert.True(differing == 0,
            $"after a jump into a warped stretch, {differing} samples differ from what playing there gave: the track plays different audio than it would have got there by playing");
    }

    [Fact]
    public void ASeekIntoAWarpedStretchOnTheVocoderLandsAligned()
    {
        var c = Setup(Kind.VocoderWarp);
        var pins = PinsFor(c);
        double Source(long frame) => Spread(pins, c.Tempo(frame));
        double swellOut = 10.5;
        double swellSrc = Source((long)(swellOut * Rate)) / Rate;
        var clip = Swells(24, new[] { swellSrc });
        long seek = 8 * Rate + 777;
        int frames = 4 * Rate;

        var (buf, _) = Render(v =>
        {
            v.SetClip(clip);
            v.Mode = StretchMode.Independent;
            v.Tempo = TempoMap.Warped(c.Map, ToPins(pins));
        }, frames, 1000, seekTo: seek);

        var expected = ExpectedEnvelope(frames, Source, new[] { swellSrc }, seek);
        int centre = (int)(swellOut * Rate - seek);
        int lag = BestLag(expected, Envelope(buf), 4000, Math.Max(Rate / 2, centre - 24000), 48000);
        Assert.True(Math.Abs(lag) < 200, $"a jump into a warped stretch lands {lag} frames out of time");
    }

    [Fact]
    public void AWarpMarkerInTheMiddleOfABlockLandsTheAudioAfterItInTimeOnTheVocoder()
    {
        // One speed jump of 28%: the audio 10% slower than its tempo up to the marker, 15% faster
        // after it. The marker is heard on frame 240 517 — inside a block of 777, 1000, 2500 and 4096
        // alike. Fed one speed across it, the vocoder smears the jump over the run that crosses it,
        // by an amount that depends on where the block happens to start.
        //
        // The swell is 30 ms after the marker, inside that run. Further on, the read position is
        // exact again at the end of the run and nothing shows: measured 2026-10-18 with the run left
        // whole across the marker, a swell 0.25 s after it landed 0 frames off at every block size,
        // and one 30 ms after it 16, 15, −27 and −63 frames off at 777, 1000, 2500 and 4096.
        const double v = 0.97;
        long jumpAt = 5L * Rate + 517;
        double t0 = v * 3 * Rate, t1 = v * jumpAt, t2 = v * 9 * Rate;
        double a1 = t0 + 0.90 * (t1 - t0);
        var pins = new (double Audio, double Tempo)[] { (t0, t0), (a1, t1), (a1 + 1.15 * (t2 - t1), t2) };
        double Source(long frame) => Spread(pins, v * frame);
        double swellOut = jumpAt / (double)Rate + 0.03;
        double swellSrc = Source((long)(swellOut * Rate)) / Rate;
        var clip = Swells(16, new[] { swellSrc });
        int frames = 9 * Rate;
        var expected = ExpectedEnvelope(frames, Source, new[] { swellSrc });
        int centre = (int)(swellOut * Rate);

        var lags = new Dictionary<int, int>();
        foreach (int block in new[] { 777, 1000, 2500, 4096 })
        {
            Assert.True(jumpAt % block != 0, "setup: the marker should fall inside a block");
            var (buf, voice) = Render(x =>
            {
                x.SetClip(clip);
                x.Mode = StretchMode.Independent;
                x.Tempo = TempoMap.Warped(TempoMap.Constant(v), ToPins(pins));
            }, frames, block);
            int lag = BestLag(expected, Envelope(buf), 4000, centre - 24000, 48000);
            lags[block] = lag;
            _out.WriteLine($"blocks of {block}: the swell 30 ms after the marker lands {lag} frames off");
            Assert.True(Math.Abs(lag) < 200,
                $"in blocks of {block}, the beat just after a warp marker is {lag} frames ({lag * 1000.0 / Rate:F1} ms) off where the marker puts it");
            Assert.True(voice.RepositionCount == 1, "the stretcher restarted at the warp marker — a jump in the audio there");
        }
        Assert.True(lags.Values.Max() - lags.Values.Min() <= 20,
            $"the beat just after a warp marker lands {string.Join(", ", lags.Select(l => $"{l.Value} frames off in blocks of {l.Key}"))} — "
          + "where it is heard depends on where a block starts, so the preview and the rendered file would put it in different places");
    }

    // ---------------------------------------------------------------- where the speed jumps

    [Fact]
    public void ATrackWithNoMovedMarkerHasNoSpeedJump()
    {
        var unmoved = new[] { new TempoMap.Pin(100_000, 100_000), new TempoMap.Pin(500_000, 500_000) };
        var constant = TempoMap.Constant(0.95);
        var ramped = TempoMap.Build(0.95, new[] { new TempoMap.Ramp(10_000, 900_000, 1.1) });
        foreach (var map in new[] { constant, ramped, TempoMap.LenientWarped(constant, unmoved), TempoMap.LenientWarped(ramped, unmoved) })
            foreach (double x in new[] { -1e6, 0, 100_000.0 / 0.95, 1e8 })
                Assert.True(double.IsPositiveInfinity(map.NextJumpAfter(x)),
                    $"a track with no moved warp marker has a speed jump at {map.NextJumpAfter(x)} — the voice would cut its runs there for nothing");
    }

    [Theory]
    [InlineData(Kind.VocoderWarp)]
    [InlineData(Kind.VocoderWarpOverRamp)]
    public void TheNextSpeedJumpIsTheNextMarkerOnTheTimeline(Kind kind)
    {
        var c = Setup(kind);
        var pins = PinsFor(c);
        var map = TempoMap.Warped(c.Map, ToPins(pins));
        double[] heard = pins.Select(p => c.TimelineOf(p.Tempo)).ToArray();     // where each marker is heard, written out
        bool Near(double got, double want) => Math.Abs(got - want) < 1e-6;

        Assert.True(Near(map.NextJumpAfter(-5000), heard[0]) && Near(map.NextJumpAfter(0), heard[0]) && Near(map.NextJumpAfter(heard[0] - 1), heard[0]),
            $"before the first marker the next speed jump is at {map.NextJumpAfter(0)}, not {heard[0]} where it is heard");
        for (int i = 0; i + 1 < heard.Length; i++)
        {
            Assert.True(Near(map.NextJumpAfter(heard[i]), heard[i + 1]) && Near(map.NextJumpAfter((heard[i] + heard[i + 1]) / 2), heard[i + 1]),
                $"at or after marker {i} the next speed jump is at {map.NextJumpAfter(heard[i])}, not at the next marker at {heard[i + 1]} — "
              + "a marker already passed would cut every run after it short, or the next one would be fed the wrong speed");
        }
        Assert.True(double.IsPositiveInfinity(map.NextJumpAfter(heard[^1])) && double.IsPositiveInfinity(map.NextJumpAfter(heard[^1] + 48_000)),
            "after the last marker there is still a speed jump ahead");

        // Either side of each jump, the speed is the tempo times the slope of the stretch, written out.
        double a0 = 0, t0 = 0;
        for (int i = 0; i < pins.Length; i++)
        {
            double before = (pins[i].Audio - a0) / (pins[i].Tempo - t0);
            double after = i + 1 < pins.Length ? (pins[i + 1].Audio - pins[i].Audio) / (pins[i + 1].Tempo - pins[i].Tempo) : 1;
            double tempoSpeed(double x) => (c.Tempo(x + 0.5) - c.Tempo(x - 0.5));
            Assert.True(Math.Abs(map.SpeedAt(heard[i] - 2) - before * tempoSpeed(heard[i] - 2)) < 1e-6
                        && Math.Abs(map.SpeedAt(heard[i] + 2) - after * tempoSpeed(heard[i] + 2)) < 1e-6,
                $"the speed either side of marker {i} is {map.SpeedAt(heard[i] - 2)} and {map.SpeedAt(heard[i] + 2)}, not the stretches' own — the jump is not where NextJumpAfter says");
            (a0, t0) = pins[i];
        }
    }
}
