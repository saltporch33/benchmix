using Benchmix.Engine.Audio;
using Signalsmith;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// Where a track's audio actually lands in time once it has been through the phase vocoder.
///
/// A mashup is two songs lined up against each other to the beat. A track that comes out even a
/// fraction of a beat late against its neighbour is the one defect that makes the whole tool
/// pointless, and it is invisible to every test that looks at one track on its own — which is why
/// these tests always compare a processed track against an unprocessed one rather than checking a
/// number in isolation.
/// </summary>
public class StretchLatencyTests
{
    const int Rate = 48000;
    private readonly ITestOutputHelper _out;

    public StretchLatencyTests(ITestOutputHelper output) => _out = output;

    /// <summary>Silence, then a sudden full-level tone. The onset is the thing being located.</summary>
    static AudioClip Step(double silentSeconds, double toneSeconds, double hz = 1000, float amp = 0.7f)
    {
        int silent = (int)(silentSeconds * Rate);
        int tone = (int)(toneSeconds * Rate);
        var s = new float[(silent + tone) * 2];
        for (int f = 0; f < tone; f++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * hz * f / Rate));
            s[(silent + f) * 2] = v;
            s[(silent + f) * 2 + 1] = v;
        }
        return AudioClip.FromSamples("step", s, Rate);
    }

    /// <summary>First frame whose level passes <paramref name="threshold"/>, or -1.</summary>
    static int OnsetFrame(ReadOnlySpan<float> interleaved, float threshold = 0.05f)
    {
        for (int f = 0; f < interleaved.Length / 2; f++)
            if (Math.Abs(interleaved[f * 2]) > threshold) return f;
        return -1;
    }

    static float[] RenderTrack(Action<TrackVoice> configure, AudioClip clip, int frames)
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(clip);
        configure(t);
        graph.LimiterEnabled = false;
        graph.Seek(0);
        graph.SnapParameters();

        var buf = new float[frames * 2];
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(4096, frames - done);
            graph.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    /// <summary>
    /// The one that matters: a track through the vocoder must line up with a track that bypassed
    /// it. Match BPM puts one track through the stretcher and leaves the other alone, so any delay
    /// here is a delay between the two songs in every beatmatched mashup.
    /// </summary>
    [Fact]
    public void AProcessedTrackLandsAtTheSameTimeAsAnUnprocessedOne()
    {
        var clip = Step(0.5, 1.5);
        const int Frames = 48000 * 2;

        var direct = RenderTrack(_ => { }, clip, Frames);

        // Speed 1.0 with a hair of pitch shift: audibly the same audio, but it engages the
        // vocoder instead of taking the IsUnprocessed bypass. That isolates the vocoder's own
        // timing from any change in the content.
        var processed = RenderTrack(t => { t.Mode = StretchMode.Independent; t.Semitones = 0.01; },
                                    clip, Frames);

        int a = OnsetFrame(direct);
        int b = OnsetFrame(processed);
        _out.WriteLine($"unprocessed onset = {a} frames, processed onset = {b} frames, "
                     + $"difference = {b - a} frames ({(b - a) * 1000.0 / Rate:F1} ms)");

        Assert.True(a >= 0, "the unprocessed track produced no audio at all");
        Assert.True(b >= 0, "the processed track produced no audio at all");

        // One vocoder hop is a fair tolerance; a quarter of a beat is not.
        Assert.True(Math.Abs(b - a) < 256,
            $"the processed track is {(b - a) * 1000.0 / Rate:F1} ms out against the unprocessed one "
          + $"({b - a} frames). In a beatmatched mashup that is the two songs drifting apart.");
    }

    /// <summary>
    /// A steady 1 kHz carrier with ONE smooth swell in it, at <paramref name="atSeconds"/>.
    ///
    /// A repeating wobble is useless here: its correlation peak repeats too, and the delay being
    /// measured is about half a period, so the answer comes back ambiguous between "late" and
    /// "early". A single event has exactly one peak.
    /// </summary>
    static AudioClip OneSwell(double seconds, double atSeconds, double sigma = 0.04, double carrierHz = 1000)
    {
        int frames = (int)(seconds * Rate);
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            double t = f / (double)Rate;
            double d = (t - atSeconds) / sigma;
            double env = 0.35 + 0.65 * Math.Exp(-d * d);
            float v = (float)(0.7 * env * Math.Sin(2 * Math.PI * carrierHz * t));
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        return AudioClip.FromSamples("swell", s, Rate);
    }

    /// <summary>The one-pole both envelopes go through, so its own lag cancels out of the answer.</summary>
    const double EnvelopeCoeff = 0.0008;             // ~40 ms, well under the swell it has to follow

    /// <summary>Rectified and smoothed, which recovers the modulation envelope.</summary>
    static double[] Envelope(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        var env = new double[frames];
        double state = 0;
        for (int f = 0; f < frames; f++)
        {
            state += (Math.Abs(interleaved[f * 2]) - state) * EnvelopeCoeff;
            env[f] = state;
        }
        return env;
    }

    /// <summary>
    /// The same one-pole, applied to the ideal envelope.
    ///
    /// Without this the detector's own ~1000 frames of lag are counted as the stretcher's, because
    /// only the rendered side went through it. Smoothing both means the measurement is of the
    /// difference between them and nothing else.
    /// </summary>
    static double[] Smooth(double[] x)
    {
        var y = new double[x.Length];
        double state = 0;
        for (int i = 0; i < x.Length; i++)
        {
            state += (x[i] - state) * EnvelopeCoeff;
            y[i] = state;
        }
        return y;
    }

    /// <summary>
    /// Lag, in frames, at which <paramref name="signal"/> best matches <paramref name="reference"/>.
    /// Positive means the signal is LATE. Both are mean-subtracted first, or the correlation is
    /// dominated by the steady level the two share rather than by the event being located.
    /// </summary>
    static int BestLag(double[] reference, double[] signal, int maxLag, int from, int length)
    {
        double refMean = 0, sigMean = 0;
        for (int i = from; i < from + length; i++)
        {
            refMean += i < reference.Length ? reference[i] : 0;
            sigMean += i < signal.Length ? signal[i] : 0;
        }
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
                if (j < 0 || j >= signal.Length || i >= reference.Length) continue;
                sum += (reference[i] - refMean) * (signal[j] - sigMean);
                n++;
            }
            if (n == 0) continue;
            double score = sum / n;
            if (score > best) { best = score; bestLag = lag; }
        }
        return bestLag;
    }

    /// <summary>
    /// A stretched track sits where it should, at every playback rate.
    ///
    /// A step is useless for measuring this: a phase vocoder smears one across its whole analysis
    /// window, so a threshold crossing lands somewhere in the smear and moves with the rate. A
    /// single smooth swell survives stretching intact, so cross-correlating its envelope against
    /// the envelope the output should have had gives the group delay directly, with no onset to
    /// argue about.
    ///
    /// <para>
    /// This is what pins the compensation in <c>TrackVoice.StretchLatencyFrames</c>. The delay it
    /// cancels is <c>InputLatency / speed + OutputLatency</c> — measured, before the fix, at 7200 /
    /// 7450 / 6624 frames for these three rates against a predicted 7200 / 7450 / 6624. If a
    /// library update changes that relationship, this test fails rather than the mix quietly
    /// drifting.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.92)]
    [InlineData(1.25)]
    public void AStretchedTrackIsAlignedAtEverySpeed(double speed)
    {
        const double SwellAtSeconds = 2.0;
        var clip = OneSwell(6.0, SwellAtSeconds);
        int frames = (int)(Rate * 4.5 / speed);

        var processed = RenderTrack(t =>
        {
            t.Mode = StretchMode.Independent;
            t.Speed = speed;
            t.Semitones = 0.01;
        }, clip, frames);

        // What the envelope should be at each OUTPUT frame: source time runs at `speed` x output
        // time, so the swell should come out at SwellAtSeconds / speed.
        var expected = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double tSrc = f * speed / Rate;
            double d = (tSrc - SwellAtSeconds) / 0.04;
            expected[f] = 0.35 + 0.65 * Math.Exp(-d * d);
        }

        var actual = Envelope(processed);
        expected = Smooth(expected);

        // A window centred on where the swell is expected, wide enough to hold it wherever it
        // actually landed, and starting past the one-pole's own settling.
        int centre = (int)(SwellAtSeconds * Rate / speed);
        int from = Math.Max(Rate / 2, centre - 24000);
        int length = Math.Min(48000, frames - from - 1);
        int lag = BestLag(expected, actual, 20000, from, length);

        using var st = new Stretch();
        st.PresetDefault(2, Rate, true);
        double uncompensated = st.InputLatency() / speed + st.OutputLatency();
        _out.WriteLine($"speed={speed}: residual delay = {lag} output frames ({lag * 1000.0 / Rate:F1} ms); "
                     + $"uncompensated this would be {uncompensated:F0} frames "
                     + $"({uncompensated * 1000 / Rate:F0} ms)");

        // 200 frames is 4 ms. A beat at 98 BPM is 609 ms.
        Assert.True(Math.Abs(lag) < 200,
            $"a track stretched to speed {speed} lands {lag} frames ({lag * 1000.0 / Rate:F1} ms) "
          + $"off where it belongs; against an unstretched track that is the two songs flamming");

        Assert.True(Math.Abs(lag) < 20000, "correlation hit the search limit; widen it");
    }

    /// <summary>
    /// The library fact that makes <c>OutputSeek</c> necessary: <c>Seek</c> primes the analysis
    /// side only, and the synthesis side then starts empty, so the first block after a plain
    /// Reset+Seek is silent. Pinned so that a library update which changed it would be noticed
    /// rather than leaving a now-redundant primer in place forever.
    /// </summary>
    [Fact]
    public void SeekAlonePrimesOnlyTheAnalysisSideAndLeavesTheFirstBlockSilent()
    {
        using var st = new Stretch();
        st.PresetDefault(2, Rate, true);

        _out.WriteLine($"Block={st.BlockSamples()} Interval={st.IntervalSamples()} "
                     + $"InputLatency={st.InputLatency()} OutputLatency={st.OutputLatency()} "
                     + $"SeekLength={st.SeekLength()}");

        int seek = Math.Max(0, st.SeekLength());
        const int Block = 4096;

        // History for the primer, then the material we actually want to hear: a full-level tone
        // starting at the very first frame we feed to Process.
        var history = new float[Math.Max(1, seek) * 2];
        var input = new float[Block * 2];
        for (int f = 0; f < Block; f++)
        {
            float v = (float)(0.7 * Math.Sin(2 * Math.PI * 1000 * f / Rate));
            input[f * 2] = v;
            input[f * 2 + 1] = v;
        }

        st.Reset();
        st.Seek(history.AsSpan(), seek, 1.0f);

        var output = new float[Block * 2];
        st.Process(input.AsSpan(), Block, output.AsSpan(), Block);

        int onset = OnsetFrame(output);
        double rmsFirst512 = 0;
        for (int i = 0; i < 512 * 2; i++) rmsFirst512 += output[i] * (double)output[i];
        rmsFirst512 = Math.Sqrt(rmsFirst512 / (512 * 2));

        _out.WriteLine($"after Reset+Seek: onset at output frame {onset}, "
                     + $"rms of first 512 frames = {rmsFirst512:F6}");

        Assert.True(onset < 0 || onset > 4000,
            $"Reset+Seek now yields audio at frame {onset}; the synthesis side appears to prime "
          + $"itself, so the OutputSeek call in TrackVoice.Prime may no longer be needed");
    }

    /// <summary>
    /// What <c>OutputSeek</c> buys, stated as behaviour: seeking into the middle of a pitched track
    /// must not leave a hole. Compensating the latency without priming the synthesis side would
    /// trade a permanent 150 ms offset for 150 ms of silence after every seek and every press of
    /// play — which on a four-bar loop is most of the first bar missing.
    /// </summary>
    [Fact]
    public void SeekingIntoAPitchedTrackDoesNotLeaveASilentGap()
    {
        var clip = OneSwell(8.0, 4.0);
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(clip);
        t.Mode = StretchMode.Independent;
        t.Semitones = 4;
        graph.LimiterEnabled = false;

        graph.Seek(Rate * 3);               // land in the middle of continuous audio
        graph.SnapParameters();

        var buf = new float[Rate / 4 * 2];  // the first 250 ms after the seek
        graph.Render(buf, Rate / 4);

        double rms = 0;
        int lat = (int)(0.150 * Rate);
        for (int i = 0; i < lat * 2; i++) rms += buf[i] * (double)buf[i];
        rms = Math.Sqrt(rms / (lat * 2));

        _out.WriteLine($"first 150 ms after a seek: rms = {rms:F4}");
        Assert.True(rms > 0.05,
            $"the first 150 ms after seeking into a pitched track read rms {rms:F4} — that is the "
          + $"stretcher's synthesis side fading up from empty, i.e. a hole at every seek");
    }

    /// <summary>
    /// Whether <c>OutputSeek</c> primes the synthesis side, which is the half <c>Seek</c> leaves
    /// empty.
    ///
    /// The span overload takes no length, so it reads the span's length as a <b>frame</b> count —
    /// hand it an interleaved stereo span and the native side reads twice as far as the buffer
    /// goes. Every buffer here is allocated eight times larger than any plausible reading, so a
    /// wrong guess about the convention shows up as a wrong number rather than as an access
    /// violation that takes the test host with it.
    /// </summary>
    [Fact]
    public void OutputSeekPrimesTheSynthesisSideThatSeekLeavesEmpty()
    {
        using var st = new Stretch();
        st.PresetDefault(2, Rate, true);

        int seek = Math.Max(0, st.SeekLength());
        int outSeek = st.OutputSeekLength(1.0f);
        _out.WriteLine($"SeekLength={seek} OutputSeekLength(1.0)={outSeek} "
                     + $"OutputSeekLength(0.92)={st.OutputSeekLength(0.92f)} "
                     + $"OutputSeekLength(1.25)={st.OutputSeekLength(1.25f)}");

        const int Block = 4096;
        static float Tone(int f) => (float)(0.7 * Math.Sin(2 * Math.PI * 1000 * f / Rate));

        // Deliberately over-allocated: 8x the frames the native side could plausibly want. Both
        // primers are fed real audio — the continuous tone that precedes the read position — not
        // silence, because priming with silence is indistinguishable from not priming at all.
        var history = new float[Math.Max(seek, outSeek) * 16];
        for (int f = 0; f < history.Length / 2; f++) { history[f * 2] = Tone(f); history[f * 2 + 1] = Tone(f); }

        var input = new float[Block * 2];
        for (int f = 0; f < Block; f++) { input[f * 2] = Tone(f); input[f * 2 + 1] = Tone(f); }

        st.Reset();
        st.Seek(history.AsSpan(0, seek * 2), seek, 1.0f);
        st.OutputSeek(history.AsSpan(0, outSeek));          // length in FRAMES, buffer is far larger

        var output = new float[Block * 2];
        st.Process(input.AsSpan(), Block, output.AsSpan(), Block);

        double rms = 0;
        for (int i = 0; i < 512 * 2; i++) rms += output[i] * (double)output[i];
        rms = Math.Sqrt(rms / (512 * 2));
        _out.WriteLine($"after Reset+Seek+OutputSeek: rms of first 512 frames = {rms:F4} "
                     + $"(a settled 0.7 tone reads about {0.7 / Math.Sqrt(2):F4})");

        Assert.True(rms > 0.3,
            $"OutputSeek did not prime the synthesis side — the first 512 frames read rms {rms:F4}");
    }
}
