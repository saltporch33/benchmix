using Benchmix.Engine.Audio;
using NAudio.Wave;
using Xunit;

namespace Benchmix.Tests;

public class MixGraphTests
{
    const int Rate = 48000;

    static AudioClip Tone(double hz, double seconds, double amp = 0.4, int rate = Rate)
    {
        int frames = (int)(seconds * rate);
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * hz * f / rate));
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        return AudioClip.FromSamples("tone", s, rate);
    }

    static AudioClip Noise(double seconds, double amp = 0.3, int seed = 7, int rate = Rate)
    {
        int frames = (int)(seconds * rate);
        var rnd = new Random(seed);
        var s = new float[frames * 2];
        for (int i = 0; i < s.Length; i++) s[i] = (float)((rnd.NextDouble() * 2 - 1) * amp);
        return AudioClip.FromSamples("noise", s, rate);
    }

    static double DominantHz(ReadOnlySpan<float> interleaved, int rate, double lo = 100, double hi = 3000)
    {
        // Goertzel over the left channel.
        int frames = interleaved.Length / 2;
        var mono = new double[frames];
        for (int f = 0; f < frames; f++) mono[f] = interleaved[f * 2];

        double best = lo, bestE = -1;
        for (double hz = lo; hz <= hi; hz += 0.5)
        {
            double w = 2 * Math.PI * hz / rate, coeff = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            foreach (var x in mono) { double s0 = x + coeff * s1 - s2; s2 = s1; s1 = s0; }
            double e = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            if (e > bestE) { bestE = e; best = hz; }
        }
        return best;
    }

    static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (var v in x) s += v * (double)v;
        return x.Length == 0 ? 0 : Math.Sqrt(s / x.Length);
    }

    [Fact]
    public void UntouchedTrack_RendersBitIdenticalToItsSource()
    {
        // The whole promise of the tool: a track you did not modify is not modified. That has to
        // survive the EQ (which delays), the stretcher (which must be bypassed) and the master bus.
        var clip = Noise(2.0);
        using var graph = new MixGraph(Rate);
        var track = graph.AddTrack();
        track.SetClip(clip);
        Assert.True(track.IsUnprocessed);

        graph.Seek(0);
        graph.SnapParameters();

        int frames = (int)clip.FrameCount;
        var outBuf = new float[frames * 2];
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(4096, frames - done);
            graph.Render(outBuf.AsSpan(done * 2, n * 2), n);
            done += n;
        }

        int mismatches = 0;
        for (int i = 0; i < frames * 2; i++)
            if (outBuf[i] != clip.Samples[i]) mismatches++;

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void ChangingSpeed_LeavesPitchAlone()
    {
        var clip = Tone(440, 3.0);
        using var graph = new MixGraph(Rate);
        var track = graph.AddTrack();
        track.SetClip(clip);
        track.Mode = StretchMode.Independent;
        track.Speed = 1.25;                  // 25% faster

        graph.Seek(0);
        graph.SnapParameters();

        var buf = new float[Rate * 2];       // one second, well past any start transient
        graph.Render(buf, Rate);             // discard the first second
        graph.Render(buf, Rate);

        double hz = DominantHz(buf, Rate);
        Assert.InRange(hz, 430, 450);
    }

    [Fact]
    public void ChangingPitch_LeavesSpeedAlone()
    {
        var clip = Tone(440, 3.0);
        using var graph = new MixGraph(Rate);
        var track = graph.AddTrack();
        track.SetClip(clip);
        track.Mode = StretchMode.Independent;
        track.Semitones = 12;                // one octave up

        Assert.Equal(clip.FrameCount, track.TimelineLengthFrames);

        graph.Seek(0);
        graph.SnapParameters();
        var buf = new float[Rate * 2];
        graph.Render(buf, Rate);
        graph.Render(buf, Rate);

        double hz = DominantHz(buf, Rate, 400, 1400);
        Assert.InRange(hz, 860, 900);
    }

    [Fact]
    public void SpeedAndPitchTogether_BothLand()
    {
        var clip = Tone(440, 4.0);
        using var graph = new MixGraph(Rate);
        var track = graph.AddTrack();
        track.SetClip(clip);
        track.Speed = 0.8;                   // 20% slower
        track.Semitones = -3;

        // 4 s of source at 0.8x plays for 5 s.
        Assert.InRange(track.TimelineLengthFrames, (long)(4.9 * Rate), (long)(5.1 * Rate));

        graph.Seek(0);
        graph.SnapParameters();
        var buf = new float[Rate * 2];
        graph.Render(buf, Rate);
        graph.Render(buf, Rate);

        double want = 440 * Math.Pow(2, -3 / 12.0);
        double hz = DominantHz(buf, Rate, 250, 700);
        Assert.InRange(hz, want * 0.98, want * 1.02);
    }

    [Fact]
    public void LinkedMode_MovesPitchWithSpeed()
    {
        var clip = Tone(440, 3.0);
        using var graph = new MixGraph(Rate);
        var track = graph.AddTrack();
        track.SetClip(clip);
        track.Mode = StretchMode.Linked;
        track.Speed = 2.0;                   // an octave up, and half as long

        Assert.InRange(track.EffectiveSemitones, 11.99, 12.01);
        Assert.InRange(track.TimelineLengthFrames, (long)(1.45 * Rate), (long)(1.55 * Rate));

        graph.Seek(0);
        graph.SnapParameters();
        var buf = new float[Rate * 2];
        graph.Render(buf, Rate);

        double hz = DominantHz(buf, Rate, 400, 1400);
        Assert.InRange(hz, 860, 900);
    }

    [Fact]
    public void TracksAtDifferentOffsets_OverlapCorrectly()
    {
        using var graph = new MixGraph(Rate);
        var a = graph.AddTrack();
        a.SetClip(Tone(440, 1.0, 0.3));

        var b = graph.AddTrack();
        b.SetClip(Tone(440, 1.0, 0.3));
        b.TimelineOffsetFrames = Rate;        // starts exactly when A ends

        Assert.Equal(Rate * 2, graph.LengthFrames);

        graph.Seek(0);
        graph.SnapParameters();

        var first = new float[Rate * 2];
        var second = new float[Rate * 2];
        graph.Render(first, Rate);
        graph.Render(second, Rate);

        // Both seconds carry audio; neither is silent, and they do not double up.
        Assert.InRange(Rms(first), 0.15, 0.30);
        Assert.InRange(Rms(second), 0.15, 0.30);
    }

    [Fact]
    public void MuteSilencesOnlyThatTrack()
    {
        using var graph = new MixGraph(Rate);
        var a = graph.AddTrack();
        a.SetClip(Tone(440, 1.0, 0.3));
        var b = graph.AddTrack();
        b.SetClip(Tone(660, 1.0, 0.3));
        b.Muted = true;

        graph.Seek(0);
        graph.SnapParameters();
        var buf = new float[Rate * 2];
        graph.Render(buf, Rate);

        double hz = DominantHz(buf, Rate, 300, 900);
        Assert.InRange(hz, 430, 450);        // only A survives
    }

    [Fact]
    public void Limiter_HoldsTheMixUnderFullScale()
    {
        using var graph = new MixGraph(Rate);
        for (int i = 0; i < 4; i++)
        {
            var t = graph.AddTrack();
            t.SetClip(Tone(200 + i * 70, 1.0, 0.8));   // deliberately way too hot
        }
        graph.LimiterEnabled = true;
        graph.Seek(0);
        graph.SnapParameters();

        var buf = new float[Rate * 2];
        graph.Render(buf, Rate);

        float peak = 0;
        foreach (var v in buf) peak = Math.Max(peak, Math.Abs(v));
        Assert.True(peak <= 1.0f, $"peak {peak:F4} exceeded full scale");
    }

    [Fact]
    public void Render_WritesAPlayableWavOfTheRightLength()
    {
        var dir = Path.Combine(Path.GetTempPath(), "benchmix-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"render-{Guid.NewGuid():N}.wav");

        try
        {
            using var graph = new MixGraph(Rate);
            var t = graph.AddTrack();
            t.SetClip(Tone(440, 2.0, 0.4));

            var result = Renderer.Render(graph, path, new RenderOptions { TailSeconds = 0.5, Format = RenderFormat.Pcm24 });

            Assert.True(File.Exists(path));
            Assert.InRange(result.Seconds, 2.4, 2.6);
            Assert.False(result.Clipped);

            using var reader = new WaveFileReader(path);
            Assert.Equal(24, reader.WaveFormat.BitsPerSample);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.Equal(Rate, reader.WaveFormat.SampleRate);
            Assert.InRange(reader.TotalTime.TotalSeconds, 2.4, 2.6);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        // The same project must render to the same bytes twice, or "meticulous" means nothing.
        var dir = Path.Combine(Path.GetTempPath(), "benchmix-tests");
        Directory.CreateDirectory(dir);
        var p1 = Path.Combine(dir, $"det-a-{Guid.NewGuid():N}.wav");
        var p2 = Path.Combine(dir, $"det-b-{Guid.NewGuid():N}.wav");

        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                using var graph = new MixGraph(Rate);
                var t = graph.AddTrack();
                t.SetClip(Noise(1.0, 0.3, seed: 42));
                t.Speed = 1.07;
                t.Semitones = 2;
                t.Eq.SetGains(1.3, 0.8, 1.0);
                Renderer.Render(graph, pass == 0 ? p1 : p2, new RenderOptions { TailSeconds = 0.2, Format = RenderFormat.Pcm16 });
            }

            var a = File.ReadAllBytes(p1);
            var b = File.ReadAllBytes(p2);
            Assert.Equal(a.Length, b.Length);
            Assert.True(a.AsSpan().SequenceEqual(b), "two renders of the same project differed");
        }
        finally
        {
            foreach (var p in new[] { p1, p2 }) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
        }
    }
}
