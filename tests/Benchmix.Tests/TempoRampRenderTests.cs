using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// A track whose tempo changes over time (tempo ramps, Daniel, 2026-10-11), rendered through the
/// real voice. Where each moment of the song should come out is worked out from the integral
/// written in <see cref="F"/> — never from the tempo map — and measured on the audio, as
/// <c>StretchLatencyTests</c> does for one speed.
/// </summary>
public class TempoRampRenderTests
{
    const int Rate = 48000;
    private readonly ITestOutputHelper _out;
    public TempoRampRenderTests(ITestOutputHelper output) => _out = output;

    /// <summary>The track frame at timeline frame x: v0 until a, a straight line in speed to v1 at b, v1 after.</summary>
    static double F(double x, double v0, double a, double b, double v1)
    {
        if (x < a) return v0 * x;
        if (x < b) return v0 * a + v0 * (x - a) + 0.5 * (v1 - v0) / (b - a) * (x - a) * (x - a);
        return v0 * a + (v0 + v1) / 2 * (b - a) + v1 * (x - b);
    }

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

    /// <summary>VocoderFromOwnTempo is his own case: a song at its own tempo, 110 BPM ramping to 115.</summary>
    public enum Kind { VocoderUp, VocoderDown, LinkUp, LinkDown, VocoderFromOwnTempo }

    static (StretchMode Mode, double V0, double V1) Setup(Kind kind) => kind switch
    {
        Kind.VocoderFromOwnTempo => (StretchMode.Independent, 1.0, 115.0 / 110),
        Kind.VocoderUp => (StretchMode.Independent, 0.92, 1.10),
        Kind.VocoderDown => (StretchMode.Independent, 1.10, 0.90),
        Kind.LinkUp => (StretchMode.Linked, 0.92, 1.10),
        _ => (StretchMode.Linked, 1.10, 0.90),
    };

    [Theory]
    [InlineData(Kind.VocoderUp, 4096)]
    [InlineData(Kind.VocoderUp, 1000)]
    [InlineData(Kind.VocoderDown, 4096)]
    [InlineData(Kind.VocoderDown, 1000)]
    [InlineData(Kind.LinkUp, 4096)]
    [InlineData(Kind.LinkUp, 1000)]
    [InlineData(Kind.LinkDown, 4096)]
    [InlineData(Kind.LinkDown, 1000)]
    [InlineData(Kind.VocoderFromOwnTempo, 4096)]
    [InlineData(Kind.VocoderFromOwnTempo, 1000)]
    public void ATrackUnderATempoRampStaysAlignedAllTheWayThrough(Kind kind, int block)
    {
        var (mode, v0, v1) = Setup(kind);
        double a = 2.0 * Rate, b = 14.0 * Rate;
        // Swells before the ramp, all through it, and after it where the tempo holds at the end.
        double[] outputSeconds = { 1.0, 3, 5, 7, 9, 11, 13, 15.5, 17 };
        double[] sourceSeconds = outputSeconds.Select(o => F(o * Rate, v0, a, b, v1) / Rate).ToArray();
        var clip = Swells(24, sourceSeconds);
        int frames = 18 * Rate;

        var (buf, _) = Render(v =>
        {
            v.SetClip(clip);
            v.Mode = mode;
            v.Tempo = TempoMap.Build(v0, new[] { new TempoMap.Ramp(a, b, v1) });
        }, frames, block);

        var expected = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double tSrc = F(f, v0, a, b, v1) / Rate, env = 0.35;
            foreach (double at in sourceSeconds) { double d = (tSrc - at) / 0.04; env += 0.65 * Math.Exp(-d * d); }
            expected[f] = env;
        }
        expected = Smooth(expected);
        var actual = Envelope(buf);

        foreach (double o in outputSeconds)
        {
            int centre = (int)(o * Rate);
            int lag = BestLag(expected, actual, 4000, Math.Max(Rate / 2, centre - 24000), 48000);
            _out.WriteLine($"{kind}, blocks of {block}: the swell at {o:0.0}s lands {lag} frames off");
            Assert.True(Math.Abs(lag) < 200,
                $"{o:0.0}s into a {kind} tempo ramp the track is {lag} frames ({lag * 1000.0 / Rate:F1} ms) off where the ramp puts it — "
              + "against the other deck, the two songs drift apart while one speeds up");
        }
    }

    [Theory]
    [InlineData(StretchMode.Independent, 333, 0.95)]
    [InlineData(StretchMode.Independent, 1000, 0.95)]
    [InlineData(StretchMode.Independent, 4096, 0.95)]
    [InlineData(StretchMode.Linked, 333, 0.95)]
    [InlineData(StretchMode.Linked, 4096, 0.95)]
    [InlineData(StretchMode.Independent, 1000, 1.0)]     // from the song's own tempo: no stretch at all before the ramp
    [InlineData(StretchMode.Linked, 1000, 1.0)]
    public void ATempoRampNeverRestartsTheStretcherPartWay(StretchMode mode, int block, double before)
    {
        var clip = Swells(32, new[] { 5.0 });
        var (_, voice) = Render(v =>
        {
            v.SetClip(clip);
            v.Mode = mode;
            v.Tempo = TempoMap.Build(before, new[] { new TempoMap.Ramp(4.0 * Rate, 26.0 * Rate, 1.08) });
        }, 28 * Rate, block);
        Assert.True(voice.RepositionCount == 1,
            $"the stretcher restarted {voice.RepositionCount - 1} times inside one section — each restart is a jump in the audio part-way through the tempo change");
    }

    [Fact]
    public void OnLinkATempoRampSoundsTheSameAtEveryBlockSize()
    {
        var clip = RampSource(24 * Rate);
        float[] At(int block) => Render(v =>
        {
            v.SetClip(clip);
            v.Mode = StretchMode.Linked;
            v.Tempo = TempoMap.Build(0.95, new[] { new TempoMap.Ramp(1.0 * Rate, 15.0 * Rate, 1.07) });
        }, 18 * Rate, block).Buf;

        var small = At(333);
        foreach (int block in new[] { 4096, 8192 })
        {
            var other = At(block);
            int differing = Enumerable.Range(0, small.Length).Count(i => small[i] != other[i]);
            Assert.True(differing == 0, $"{differing} samples differ between blocks of 333 and {block}: preview and the rendered file would not match on a tempo ramp");
        }
    }

    [Fact]
    public void ASeekIntoATempoRampOnLinkPlaysExactlyWhatPlayingThroughDoes()
    {
        var clip = RampSource(24 * Rate);
        void Setup(TrackVoice v)
        {
            v.SetClip(clip);
            v.Mode = StretchMode.Linked;
            v.Tempo = TempoMap.Build(0.95, new[] { new TempoMap.Ramp(1.0 * Rate, 15.0 * Rate, 1.07) });
        }
        long seek = 8 * Rate + 123;
        var through = Render(Setup, 14 * Rate, 1000).Buf;
        var jumped = Render(Setup, 4 * Rate, 1000, seekTo: seek).Buf;

        int differing = 0;
        for (int i = 0; i < jumped.Length; i++) if (jumped[i] != through[seek * 2 + i]) differing++;
        Assert.True(differing == 0,
            $"after a jump into a tempo ramp, {differing} samples differ from what playing there gave: the track plays different audio than it would have got there by playing");
    }

    [Fact]
    public void ASeekIntoATempoRampOnTheVocoderLandsAligned()
    {
        const double v0 = 0.95, v1 = 1.08;
        double a = 1.0 * Rate, b = 15.0 * Rate;
        double swellOut = 11.0;
        double swellSrc = F(swellOut * Rate, v0, a, b, v1) / Rate;
        var clip = Swells(24, new[] { swellSrc });
        long seek = 9 * Rate;

        var (buf, _) = Render(v =>
        {
            v.SetClip(clip);
            v.Mode = StretchMode.Independent;
            v.Tempo = TempoMap.Build(v0, new[] { new TempoMap.Ramp(a, b, v1) });
        }, 4 * Rate, 1000, seekTo: seek);

        int frames = 4 * Rate;
        var expected = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double d = (F(seek + f, v0, a, b, v1) / Rate - swellSrc) / 0.04;
            expected[f] = 0.35 + 0.65 * Math.Exp(-d * d);
        }
        int centre = (int)(swellOut * Rate - seek);
        int lag = BestLag(Smooth(expected), Envelope(buf), 4000, Math.Max(Rate / 2, centre - 24000), 48000);
        Assert.True(Math.Abs(lag) < 200, $"a jump into a tempo ramp lands {lag} frames out of time");
    }

    [Fact]
    public void TwoRendersOfATempoRampedTrackAreIdentical()
    {
        var clip = Swells(24, new[] { 4.0, 9.0 });
        float[] Once() => Render(v =>
        {
            v.SetClip(clip);
            v.Mode = StretchMode.Independent;
            v.Tempo = TempoMap.Build(0.95, new[] { new TempoMap.Ramp(1.0 * Rate, 15.0 * Rate, 1.08) });
        }, 16 * Rate, 1000).Buf;
        var x = Once();
        var y = Once();
        Assert.True(x.AsSpan().SequenceEqual(y), "the same tempo-ramped project rendered differently twice");
    }

    static VoiceClip Section(AudioClip audio, long trackStart, long from, long to)
        => new() { Audio = audio, TrackStartFrame = trackStart, SourceStartFrame = from, SourceEndFrame = to };

    [Fact]
    public void ASplitInsideATempoRampOnLinkChangesNothing()
    {
        var audio = RampSource(24 * Rate);
        var map = TempoMap.Build(0.95, new[] { new TempoMap.Ramp(10_000, 600_000, 1.07) });
        float[] With(ClipTimeline timeline) => Render(v => { v.SetTimeline(timeline); v.Mode = StretchMode.Linked; v.Tempo = map; }, 700_000, 1024).Buf;

        var whole = With(new ClipTimeline(new[] { Section(audio, 0, 0, 900_000) }));
        var split = With(new ClipTimeline(new[] { Section(audio, 0, 0, 312_345), Section(audio, 312_345, 312_345, 900_000) }));
        int differing = Enumerable.Range(0, whole.Length).Count(i => whole[i] != split[i]);
        Assert.True(differing == 0, $"splitting a section under a tempo ramp changed {differing} samples — every hand-built loop there would tick");
    }

    [Fact]
    public void ASplitInsideATempoRampOnTheVocoderDoesNotRestartIt()
    {
        // A seam splits one stretcher call into two at slightly different ratios, so the vocoder's
        // output is not claimed to be identical. What is claimed: no restart, and no step.
        var audio = Swells(24, Array.Empty<double>());
        var map = TempoMap.Build(0.95, new[] { new TempoMap.Ramp(10_000, 600_000, 1.07) });
        (float[] Buf, TrackVoice Voice) With(ClipTimeline timeline)
            => Render(v => { v.SetTimeline(timeline); v.Mode = StretchMode.Independent; v.Tempo = map; }, 700_000, 1000);

        var whole = With(new ClipTimeline(new[] { Section(audio, 0, 0, 900_000) }));
        var split = With(new ClipTimeline(new[] { Section(audio, 0, 0, 312_345), Section(audio, 312_345, 312_345, 900_000) }));

        int seam = (int)map.FirstRelAt(312_345);
        float Worst(float[] buf, int from, int to) { float w = 0; for (int f = from; f < to; f++) w = Math.Max(w, Math.Abs(buf[(f + 1) * 2] - buf[f * 2])); return w; }
        float steady = Worst(split.Buf, seam - 30_000, seam - 1_000), atSeam = Worst(split.Buf, seam - 480, seam + 480);
        float diff = 0;
        for (int i = 0; i < whole.Buf.Length; i++) diff = Math.Max(diff, Math.Abs(whole.Buf[i] - split.Buf[i]));
        _out.WriteLine($"vocoder split under a ramp: worst step at the seam {atSeam:F4}, elsewhere {steady:F4}; largest difference from the unsplit render {diff:F5}");

        Assert.True(split.Voice.RepositionCount == whole.Voice.RepositionCount && whole.Voice.RepositionCount == 1,
            "the split under a tempo ramp restarted the stretcher at the seam");
        Assert.True(atSeam <= steady * 1.5f, $"the split under a tempo ramp steps by {atSeam:F4} at the seam against {steady:F4} elsewhere");
    }

    [Fact]
    public void ACutEdgeInsideATempoRampIsDeclickedOnItsOwnFrames()
    {
        // LINK, a ramp source, and a section cut at both ends with gaps either side, all inside the
        // ramp. Frame by frame the output must be the source at the ramp's position, times the 5 ms
        // declick, on the frames the ramp puts the edges on — and silence after.
        const double v0 = 0.95, v1 = 1.07, a = 96_000, b = 480_000;
        const long T0 = 150_000, S0 = 200_000, L = 300_000;
        int n = (int)(TrackVoice.DeclickSeconds * Rate);
        var audio = RampSource(24 * Rate);
        double N = audio.FrameCount;

        var (buf, _) = Render(v =>
        {
            v.SetTimeline(new ClipTimeline(new[] { Section(audio, T0, S0, S0 + L) }));
            v.Mode = StretchMode.Linked;
            v.Tempo = TempoMap.Build(v0, new[] { new TempoMap.Ramp(a, b, v1) });
        }, 500_000, 1000);

        // The inverse of F inside the ramp, written out: where the edges fall on the timeline.
        double Inverse(double p)
        {
            double k = (v1 - v0) / (b - a), dp = p - v0 * a;
            return a + 2 * dp / (v0 + Math.Sqrt(v0 * v0 + 2 * k * dp));
        }
        long startRel = (long)Inverse(T0), lengthOut = (long)(Inverse(T0 + L) - Inverse(T0));

        double worst = 0;
        for (long r = startRel; r < startRel + lengthOut; r++)
        {
            long pos = r - startRel;
            double env = 1;
            if (pos < n) env = Math.Sin(Math.PI / 2 * (pos / (double)n));
            long fromEnd = lengthOut - pos;
            if (fromEnd < n) env = Math.Min(env, Math.Sin(Math.PI / 2 * (Math.Max(0, fromEnd) / (double)n)));
            if (pos >= n && fromEnd >= n) continue;                       // only the edges
            double want = (S0 + F(r, v0, a, b, v1) - T0) / N * env;
            worst = Math.Max(worst, Math.Abs(buf[r * 2] - want));
        }
        long after = (long)Math.Ceiling(Inverse(T0 + L));
        bool silentAfter = Enumerable.Range(0, 5000).All(i => buf[(after + 1 + i) * 2] == 0);

        Assert.True(worst < 1e-3, $"at a cut inside a tempo ramp the declick is {worst:F5} off its own frames — a click, or a dent in the audio beside it");
        Assert.True(silentAfter, "after a section cut inside a tempo ramp, sound carried on into the gap");
    }

    [Fact]
    public void ARenderRunsToTheEndOfATempoRampedTrack()
    {
        var audio = RampSource(10 * Rate);
        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.SetClip(audio);
        v.Tempo = TempoMap.Build(1.0, new[] { new TempoMap.Ramp(2.0 * Rate, 6.0 * Rate, 1.25) });

        // Track frames reached by the end of the ramp, then the rest at 1.25.
        double atRampEnd = F(6.0 * Rate, 1.0, 2.0 * Rate, 6.0 * Rate, 1.25);
        double end = 6.0 * Rate + (audio.FrameCount - atRampEnd) / 1.25;
        Assert.True(Math.Abs(graph.AudibleLengthFrames - end) <= 1,
            $"the render would end at frame {graph.AudibleLengthFrames}, not {end:F0}: it stops before the ramped track finishes, or runs on past it");
    }

    [Fact]
    public void AnyTempoRampSendsTheWholeTrackThroughTheStretcher()
    {
        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.SetClip(RampSource(Rate));
        v.Tempo = TempoMap.Build(1.0, new[] { new TempoMap.Ramp(Rate * 30.0, Rate * 40.0, 1.1) });
        Assert.False(v.IsUnprocessed, "a track with a tempo ramp would play its first stretch as a straight copy and jump 150 ms where the ramp starts");
        v.Tempo = TempoMap.Constant(1.0);
        Assert.True(v.IsUnprocessed, "with the ramp removed the track is untouched again and should be copied straight through");
    }

    [Fact]
    public void SettingSpeedOnAVoiceReplacesItsTempoMap()
    {
        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.Tempo = TempoMap.Build(1.0, new[] { new TempoMap.Ramp(Rate * 1.0, Rate * 4.0, 1.1) });
        v.Speed = 0.9;
        Assert.True(v.Tempo.IsConstant && v.Tempo.BaseSpeed == 0.9);
    }
}
