using Benchmix.Engine.Audio;
using Benchmix.Engine.Dsp;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// One test per defect an adversarial review found and measured. Each names the behaviour that was
/// wrong, so a future change that reintroduces it fails with a sentence rather than a diff.
/// </summary>
public class RegressionTests
{
    const int Rate = 48000;

    static AudioClip Tone(double hz, double seconds, float amp = 0.4f)
    {
        int frames = (int)(seconds * Rate);
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * hz * f / Rate));
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        return AudioClip.FromSamples("tone", s, Rate);
    }

    static AudioClip Dc(double seconds, float level = 0.5f)
    {
        int frames = (int)(seconds * Rate);
        var s = new float[frames * 2];
        Array.Fill(s, level);
        return AudioClip.FromSamples("dc", s, Rate);
    }

    static double DominantHz(ReadOnlySpan<float> interleaved, double lo, double hi)
    {
        int frames = interleaved.Length / 2;
        var mono = new double[frames];
        for (int f = 0; f < frames; f++) mono[f] = interleaved[f * 2];

        double best = lo, bestE = -1;
        for (double hz = lo; hz <= hi; hz += 0.5)
        {
            double w = 2 * Math.PI * hz / Rate, coeff = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            foreach (var x in mono) { double s0 = x + coeff * s1 - s2; s2 = s1; s1 = s0; }
            double e = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            if (e > bestE) { bestE = e; best = hz; }
        }
        return best;
    }

    static float[] RenderAll(MixGraph g, int frames, int block = 4096)
    {
        g.Seek(0);
        g.SnapParameters();
        var buf = new float[frames * 2];
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(block, frames - done);
            g.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    // ---------------------------------------------------------------- pitch on the first block

    [Fact]
    public void PitchAppliesFromTheVeryFirstBlock()
    {
        // The stretcher is created lazily inside the first Render that needs it — after the
        // parameters were already applied against a null. The first block used to come out at the
        // ORIGINAL pitch, which is ~85 ms of wrong note at the head of every pitched track.
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Tone(440, 2.0));
        t.Mode = StretchMode.Independent;
        t.Semitones = 12;
        graph.LimiterEnabled = false;

        var buf = RenderAll(graph, 8192);

        // Skip the stretcher's own start transient, then check we are already an octave up.
        var settled = buf.AsSpan(4096 * 2, 4096 * 2);
        double hz = DominantHz(settled, 400, 1400);
        Assert.InRange(hz, 840, 920);
    }

    [Fact]
    public void TwoRendersOfOneGraphAgreeEvenThoughTheFirstCreatesTheStretcher()
    {
        // Rendering twice from the same graph used to differ: the first render built the stretcher
        // (and lost a block of pitch), the second reused it.
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Tone(440, 1.5));
        t.Semitones = 5;
        graph.LimiterEnabled = false;

        var first = RenderAll(graph, 32768);
        var second = RenderAll(graph, 32768);

        int differing = 0;
        for (int i = 0; i < first.Length; i++)
            if (Math.Abs(first[i] - second[i]) > 1e-6f) differing++;

        Assert.True(differing == 0, $"{differing} samples differed between two renders of the same graph");
    }

    // ---------------------------------------------------------------- mute is a ramp, not a cut

    [Fact]
    public void MutingDuringPlaybackRampsInsteadOfCutting()
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Dc(2.0));
        graph.LimiterEnabled = false;

        graph.Seek(0);
        graph.SnapParameters();

        var buf = new float[Rate * 2];
        graph.Render(buf.AsSpan(0, 2048 * 2), 2048);      // running at full level
        t.Muted = true;
        graph.Render(buf.AsSpan(2048 * 2, 4096 * 2), 4096);

        double worst = 0;
        for (int f = 1; f < 6144; f++)
            worst = Math.Max(worst, Math.Abs(buf[f * 2] - buf[(f - 1) * 2]));

        // A hard cut on a 0.5 DC signal steps by 0.5. A 10 ms ramp steps by ~0.001.
        Assert.True(worst < 0.01, $"muting stepped by {worst:F4} — that is a click, not a ramp");
        Assert.True(Math.Abs(buf[6000 * 2]) < 0.01, "mute never reached silence");
    }

    // ---------------------------------------------------------------- removing a track while rendering

    [Fact]
    public void SeekingAPitchedTrackDoesNoAudioWorkOnTheCallersThread()
    {
        // A seek comes from the UI thread — every pixel of a ruler scrub while paused. It used to
        // re-prime the phase vocoder right there: about 1.75 ms per pitched track per call, and a
        // way for the UI thread to be inside the native stretcher while the audio thread was too.
        // Now it only records where to go, and the render thread does the work.
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Tone(330, 4));
        t.Mode = StretchMode.Independent;
        t.Semitones = 3;
        t.Speed = 1.07;
        RenderAll(graph, Rate / 2);                       // the stretcher exists and has run

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int Seeks = 200;
        for (int i = 0; i < Seeks; i++) t.Seek(Rate + i * 97);
        sw.Stop();
        double perSeekMs = sw.Elapsed.TotalMilliseconds / Seeks;

        Assert.True(perSeekMs < 0.25,
            $"a seek on a pitched track took {perSeekMs:F3} ms on the calling thread — it is running the "
          + "stretcher there instead of leaving that to the render thread");
    }

    [Fact]
    public void RemovingATrackWhileTheGraphIsBeingRenderedDoesNotThrow()
    {
        // The removed voice used to be disposed immediately, freeing its native stretcher while the
        // render thread could still be inside it, and the meter array was swapped between the
        // length check and the indexed write.
        using var graph = new MixGraph(Rate);
        var keep = graph.AddTrack();
        keep.SetClip(Tone(220, 5.0));

        var doomed = new List<TrackVoice>();
        for (int i = 0; i < 6; i++)
        {
            var v = graph.AddTrack();
            v.SetClip(Tone(300 + i * 40, 5.0));
            v.Semitones = 2;                    // give it a native stretcher to free
            doomed.Add(v);
        }

        graph.Seek(0);
        graph.SnapParameters();

        var buf = new float[1024 * 2];
        var error = (Exception?)null;

        var render = Task.Run(() =>
        {
            try { for (int i = 0; i < 400; i++) graph.Render(buf, 1024); }
            catch (Exception ex) { error = ex; }
        });

        foreach (var v in doomed)
        {
            Thread.Sleep(2);
            graph.RemoveTrack(v);
            graph.CollectRetired();
        }

        render.Wait(TimeSpan.FromSeconds(30));
        Assert.Null(error);
        Assert.Single(graph.Tracks);
    }

    // ---------------------------------------------------------------- honest peak reporting

    [Fact]
    public void RenderReportsThePeakBeforeTheLimiterNotAfterIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "benchmix-tests");
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, $"hot-{Guid.NewGuid():N}.wav");

        try
        {
            using var graph = new MixGraph(Rate);
            for (int i = 0; i < 6; i++)
            {
                var t = graph.AddTrack();
                t.SetClip(Tone(200 + i * 55, 1.0, 0.9f));       // deliberately far too hot
            }

            var result = Renderer.Render(graph, wav, new RenderOptions { TailSeconds = 0, ApplyLimiter = true });

            // Measured after the limiter this would read ~0.97 and look fine, which is exactly the
            // situation the number is supposed to warn about.
            Assert.True(result.PeakBefore > 2.0f,
                $"peak reported as {result.PeakBefore:F2}; the mix was several times full scale");
            Assert.True(result.Clipped, "a mix this hot should report that the limiter engaged");
        }
        finally { try { if (File.Exists(wav)) File.Delete(wav); } catch { } }
    }

    // ---------------------------------------------------------------- the passthrough guarantee, stated precisely

    [Fact]
    public void UntouchedPassthroughIsBitExactWithTheLimiterOff()
    {
        var rnd = new Random(11);
        var s = new float[40000 * 2];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.95f;

        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(AudioClip.FromSamples("loud", s, Rate));
        graph.LimiterEnabled = false;

        var outBuf = RenderAll(graph, 40000);

        int differing = 0;
        for (int i = 0; i < s.Length; i++) if (outBuf[i] != s[i]) differing++;
        Assert.Equal(0, differing);
    }

    [Fact]
    public void TheLimiterDoesAlterALoudUntouchedTrack()
    {
        // Stated as a test so nobody reads the passthrough guarantee as unconditional. The limiter
        // is on by default and its whole job is to change loud audio; "untouched is untouched"
        // holds for the signal path, not for a mix deliberately pushed past the ceiling.
        var rnd = new Random(11);
        var s = new float[20000 * 2];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(rnd.NextDouble() * 2 - 1) * 0.99f;

        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(AudioClip.FromSamples("loud", s, Rate));
        graph.LimiterEnabled = true;

        var outBuf = RenderAll(graph, 20000);

        int differing = 0;
        for (int i = 0; i < s.Length; i++) if (outBuf[i] != s[i]) differing++;
        Assert.True(differing > 0, "with the limiter on, a track peaking at 0.99 should be attenuated");
    }

    // ---------------------------------------------------------------- EQ mode actually reaches the engine

    [Fact]
    public void SwitchingEqModeChangesHowAKilledBandBehaves()
    {
        var bass = Tone(60, 1.0, 0.5f);

        double Attenuation(EqMode mode)
        {
            var eq = new ThreeBandEq(Rate, 2, EqMode.Isolator);
            eq.Mode = mode;                       // set through the property, as a project load does
            eq.SetGains(0, 1, 1);
            eq.SnapGains();

            var work = (float[])bass.Samples.Clone();
            eq.Process(work);

            double Rms(float[] x, int from)
            {
                double sum = 0;
                int n = 0;
                for (int i = from; i < x.Length; i++) { sum += x[i] * (double)x[i]; n++; }
                return Math.Sqrt(sum / n);
            }
            return 20 * Math.Log10(Rms(work, 20000) / Rms(bass.Samples, 20000));
        }

        double isolator = Attenuation(EqMode.Isolator);
        double shelving = Attenuation(EqMode.Shelving);

        Assert.True(isolator < -30, $"isolator kill was only {isolator:F1} dB");
        Assert.InRange(shelving, -30, -12);                    // shelves clamp, they cannot reach silence
        Assert.True(shelving > isolator + 5, "the two modes should not behave the same");
    }

    // ---------------------------------------------------------------- deferred seeking

    [Fact]
    public void ARequestedSeekLandsOnTheNextBlockAndNotMidBlock()
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Tone(440, 10.0));

        graph.Seek(0);
        graph.SnapParameters();
        Assert.Equal(0, graph.PositionFrames);

        graph.RequestSeek(5 * Rate);
        Assert.Equal(0, graph.PositionFrames);                  // not applied yet
        Assert.Equal(5 * Rate, graph.TargetPositionFrames);     // but visible as the target

        var buf = new float[1024 * 2];
        graph.Render(buf, 1024);

        Assert.Equal(5 * Rate + 1024, graph.PositionFrames);
        Assert.Equal(graph.PositionFrames, graph.TargetPositionFrames);
    }
}
