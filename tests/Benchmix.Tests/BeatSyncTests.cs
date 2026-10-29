using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Sync (Daniel, 2026-10-28: "an automated snap button that realigns to match peaks like kicks and
/// snares"; built 2026-10-29). The beats are click trains whose phase is written out here, never
/// read back from the detector; a beat found is compared with where the click was put.
/// </summary>
public class BeatSyncTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;

    public BeatSyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-sync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A 10 ms burst that dies away, at <paramref name="at"/>.</summary>
    static void Hit(float[] x, int rate, double at, float amp)
    {
        int start = (int)Math.Round(at * rate);
        for (int i = 0; i < rate / 100 && start + i < x.Length; i++)
            x[start + i] += amp * (float)(Math.Exp(-i / (rate * 0.003)) * Math.Sin(2 * Math.PI * 1000 * i / rate));
    }

    /// <summary>Clicks on every beat from <paramref name="phase"/> on, and weaker ones on the offbeats if asked.</summary>
    static float[] Clicks(int rate, double seconds, double bpm, double phase, float amp = 0.8f, float offbeatAmp = 0, double from = 0)
    {
        var x = new float[(int)(rate * seconds)];
        double period = 60.0 / bpm;
        for (double t = phase; t < seconds; t += period) if (t >= from) Hit(x, rate, t, amp);
        if (offbeatAmp > 0)
            for (double t = phase + period / 2; t < seconds; t += period) if (t >= from) Hit(x, rate, t, offbeatAmp);
        return x;
    }

    string Wav(string name, float[] mono)
    {
        var stereo = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) stereo[i * 2] = stereo[i * 2 + 1] = mono[i];
        string path = Path.Combine(_dir, name + ".wav");
        AudioClip.FromSamples(name, stereo, Rate).SaveWav(path);
        return path;
    }

    // ---------------------------------------------------------------- the detector

    [Fact]
    public void FindsTheBeatNearestAMomentInAClickTrain_WithinThreeMilliseconds()
    {
        // 128 BPM is a beat every 0.46875 s; from 0.1234 s, the beat nearest 10 s is the 22nd: 0.1234 + 21 × 0.46875.
        var mono = Clicks(24000, 20, 128, 0.1234);
        var found = BeatPhase.Find(mono, 24000, 128, 10.0);
        const double expected = 0.1234 + 21 * 0.46875;
        Assert.True(Math.Abs(found.Seconds - expected) < 0.003,
            $"the beat was read at {found.Seconds:F4} s, the click is at {expected:F4} s ({found.Bpm:F3} BPM read): {(found.Seconds - expected) * 1000:+0.0;-0.0} ms off, and Sync would land the beats that far apart");
        Assert.True(found.Confidence > 0.5, $"a bare click train should be an unmistakable beat, confidence was {found.Confidence:F2}");
    }

    [Fact]
    public void PicksTheBeatOverAWeakerOffbeat()
    {
        var mono = Clicks(24000, 20, 128, 0.1234, amp: 0.8f, offbeatAmp: 0.3f);
        var found = BeatPhase.Find(mono, 24000, 128, 10.0);
        const double expected = 0.1234 + 21 * 0.46875;
        Assert.True(Math.Abs(found.Seconds - expected) < 0.003,
            $"with hi-hats on the offbeats the beat was read at {found.Seconds:F4} s, the kick is at {expected:F4} s: Sync would put a kick on a hi-hat");
    }

    [Fact]
    public void AStretchThatStartsLoudIsNotReadAsABeatAtItsStart()
    {
        // Music under the playhead is loud from its first sample, and the first frame of flux is every
        // bin arriving at once — on his two songs it outweighed the eighteen beats in the window, so
        // the beat was read at the window's start and a second Sync slid the track by the same amount
        // again. Here: a loud broadband burst in the first 20 ms, and clicks on the beat as before,
        // over eight seconds (seventeen beats). The beat nearest 4 s is 0.1234 + 8 × 0.46875.
        var mono = Clicks(24000, 8, 128, 0.1234);
        uint seed = 12345;
        for (int i = 0; i < 480; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            mono[i] += (seed / (float)uint.MaxValue) * 2f - 1f;
        }
        var found = BeatPhase.Find(mono, 24000, 128, 4.0);
        const double expected = 0.1234 + 8 * 0.46875;
        Assert.True(Math.Abs(found.Seconds - expected) < 0.003,
            $"with the audio loud from the start the beat was read at {found.Seconds:F4} s, the click is at {expected:F4} s ({found.Bpm:F2} BPM read): Sync would land the beats {(found.Seconds - expected) * 1000:+0.0;-0.0} ms apart, and the same again on every press");
    }

    [Fact]
    public void FindsTheBeatWhenTheTagIsAFewBpmOff()
    {
        // Clicks at 126 BPM, told 128: over twenty seconds a comb at 128 drifts two thirds of a beat
        // from them and reads nothing, so the period is refined on the stretch first. The beat
        // nearest 10 s at 126 BPM from 0.1234 s is 0.1234 + 21 × (60 / 126).
        var mono = Clicks(24000, 20, 126, 0.1234);
        var found = BeatPhase.Find(mono, 24000, 128, 10.0);
        const double expected = 0.1234 + 21 * (60.0 / 126);
        Assert.True(Math.Abs(found.Seconds - expected) < 0.003,
            $"told 128 BPM for a 126 BPM stretch, the beat was read at {found.Seconds:F4} s, the click is at {expected:F4} s");
        Assert.True(Math.Abs(found.Bpm - 126) < 0.3, $"the stretch's own tempo read as {found.Bpm:F2} BPM, it is 126");
    }

    [Fact]
    public void SilenceAndTooLittleAudioHaveNoBeat()
    {
        Assert.Equal(0, BeatPhase.Find(new float[24000 * 10], 24000, 128, 5.0).Confidence);
        Assert.Equal(0, BeatPhase.Find(new float[1000], 24000, 128, 0.0).Confidence);
    }

    // ---------------------------------------------------------------- the move

    static async Task<TrackModel> Track(ProjectSession s, string path, double bpm)
    {
        var t = await s.AddTrackAsync(path);
        t.Bpm = bpm;
        t.BpmSource = "test";
        s.Apply(t);
        return t;
    }

    [Fact]
    public async Task SlidesTheTrackToTheNearestBeat_ForwardWhenThereIsNoRoomBefore()
    {
        // Both at 120 BPM (a beat every 0.5 s): the reference's beats from 0.25 s, the other's from 0.41 s.
        // Nearest 5 s the other's beat is 0.16 s before the reference's, but its origin is at 0:00, so it
        // goes the other way: 0.5 − 0.16 = 0.34 s later, 16320 frames, and its beats land on 0.25 + k × 0.5.
        var s = ProjectSession.CreateOffline(Rate);
        var reference = await Track(s, Wav("reference", Clicks(Rate, 30, 120, 0.25)), 120);
        var other = await Track(s, Wav("other", Clicks(Rate, 30, 120, 0.41)), 120);

        var plan = BeatSync.Plan(other, reference, 5.0, s);
        Assert.True(plan.Ok, plan.Message);
        Assert.True(Math.Abs(plan.Frames - 16320) <= 0.003 * Rate,
            $"the move was {plan.Frames} frames ({plan.Frames * 1000.0 / Rate:F1} ms), the clicks are 0.34 s apart the short way round: {plan.Message}");
        Assert.StartsWith("Slid other", plan.Message);
        Assert.Contains("later", plan.Message);

        Assert.Equal(plan.Frames, BeatSync.Slide(other, plan.Frames, s));
        Assert.Equal(plan.Frames / (double)Rate, other.TimelineOffsetSeconds);
        Assert.Equal(0, reference.TimelineOffsetSeconds);

        var again = BeatSync.Plan(other, reference, 5.0, s);
        Assert.True(again.Ok && again.Frames == 0, $"synced once, it should be in time: {again.Message}");
        Assert.Contains("already in time", again.Message);
    }

    [Fact]
    public async Task SlidesEarlierWhenThereIsRoom_AndNeverMoreThanHalfABeat()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var reference = await Track(s, Wav("reference", Clicks(Rate, 30, 120, 0.25)), 120);
        var other = await Track(s, Wav("other", Clicks(Rate, 30, 120, 0.41)), 120);
        other.TimelineOffsetSeconds = 1.0;                    // two beats in: its beats are at 1.41 + k × 0.5
        s.Apply(other);

        var plan = BeatSync.Plan(other, reference, 5.0, s);
        Assert.True(plan.Ok, plan.Message);
        Assert.True(Math.Abs(plan.Frames - (-0.16 * Rate)) <= 0.003 * Rate,
            $"the move was {plan.Frames} frames, 0.16 s earlier (−7680) is the short way round: {plan.Message}");
        Assert.Contains("earlier", plan.Message);
        BeatSync.Slide(other, plan.Frames, s);
        Assert.True(Math.Abs(other.TimelineOffsetSeconds - 0.84) < 0.003, $"origin {other.TimelineOffsetSeconds}");
        long originFrames = s.SecondsToFrames(other.TimelineOffsetSeconds);
        Assert.Equal(originFrames / (double)Rate, other.TimelineOffsetSeconds);   // whole frames, then seconds
    }

    [Fact]
    public async Task RefusesUnmatchedTempos_Ramps_AndAMomentWithNoBeat()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var reference = await Track(s, Wav("reference", Clicks(Rate, 30, 120, 0.25)), 120);
        var other = await Track(s, Wav("other", Clicks(Rate, 30, 120, 0.41)), 126);
        var late = await Track(s, Wav("late", Clicks(Rate, 30, 120, 0.41, from: 20)), 120);

        var tempo = BeatSync.Plan(other, reference, 5.0, s);
        Assert.False(tempo.Ok);
        Assert.Contains("Match BPM first", tempo.Message);

        other.Bpm = 120;
        other.Ramps.Add(new TempoRamp(2, 4, 1.1));
        var ramped = BeatSync.Plan(other, reference, 5.0, s);
        Assert.False(ramped.Ok);
        Assert.Contains("tempo ramps", ramped.Message);
        other.Ramps.Clear();

        var quiet = BeatSync.Plan(late, reference, 5.0, s);     // its clicks only start at 20 s
        Assert.False(quiet.Ok);
        Assert.Contains("Couldn't find late's beat", quiet.Message);
        Assert.Equal(0, late.TimelineOffsetSeconds);

        Assert.True(BeatSync.Plan(late, reference, 25.0, s).Ok, "with the playhead where its clicks are, the same track syncs");
    }
}
