using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// A track holding several sections.
///
/// Every test here uses a <b>ramp</b> as its source, so a sample's value says which part of the
/// song it came from. A constant tone would let a clip read the wrong part of itself and still
/// pass — that already happened once on this codebase, to the first regression test written for
/// the clip-offset bug.
/// </summary>
public class MultiClipRenderTests
{
    const int Rate = 48000;
    private readonly ITestOutputHelper _out;

    public MultiClipRenderTests(ITestOutputHelper output) => _out = output;

    /// <summary>A ramp from 0 to 1 over its whole length: value == position.</summary>
    static AudioClip Ramp(int frames)
    {
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = f / (float)frames;
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        return AudioClip.FromSamples("ramp", s, Rate);
    }

    static VoiceClip Section(AudioClip audio, long trackStart, long from, long to) => new()
    {
        Audio = audio,
        TrackStartFrame = trackStart,
        SourceStartFrame = from,
        SourceEndFrame = to,
    };

    static float[] Render(ClipTimeline timeline, int frames, Action<TrackVoice>? configure = null)
    {
        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.SetTimeline(timeline);
        configure?.Invoke(v);
        graph.LimiterEnabled = false;
        graph.Seek(0);
        graph.SnapParameters();

        var buf = new float[frames * 2];
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(1024, frames - done);      // small blocks, so joins land mid-block
            graph.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    // ---------------------------------------------------------------- the seam

    [Fact]
    public void SplittingASectionAndLeavingBothHalvesAloneChangesNothing()
    {
        var audio = Ramp(40000);
        const int Frames = 30000;

        var whole = Render(new ClipTimeline(new[] { Section(audio, 0, 0, 40000) }), Frames);

        // The same audio, described as two sections that meet exactly where the first stops.
        var split = Render(new ClipTimeline(new[]
        {
            Section(audio, 0, 0, 12345),
            Section(audio, 12345, 12345, 40000),
        }), Frames);

        int differing = 0;
        float worst = 0;
        for (int i = 0; i < whole.Length; i++)
        {
            if (whole[i] == split[i]) continue;
            differing++;
            worst = Math.Max(worst, Math.Abs(whole[i] - split[i]));
        }

        _out.WriteLine($"{differing} samples differed, worst {worst:F6}");
        Assert.True(differing == 0,
            $"splitting a clip and moving neither half changed {differing} samples (worst {worst:F6}); "
          + "a split that is not a no-op means every hand-built loop ticks at the join");
    }

    [Fact]
    public void ASeamIsStillSeamlessWhenTheTrackIsPitched()
    {
        var audio = Ramp(60000);
        const int Frames = 40000;

        static void Pitch(TrackVoice v) { v.Mode = StretchMode.Independent; v.Semitones = 3; }

        var whole = Render(new ClipTimeline(new[] { Section(audio, 0, 0, 60000) }), Frames, Pitch);
        var split = Render(new ClipTimeline(new[]
        {
            Section(audio, 0, 0, 20000),
            Section(audio, 20000, 20000, 60000),
        }), Frames, Pitch);

        int differing = 0;
        for (int i = 0; i < whole.Length; i++)
            if (Math.Abs(whole[i] - split[i]) > 1e-6f) differing++;

        _out.WriteLine($"{differing} of {whole.Length} samples differed on the pitched path");
        Assert.True(differing == 0,
            $"{differing} samples differed; on the vocoder path a seam must also be a no-op, or "
          + "splitting a pitched track re-primes the stretcher and audibly stumbles");
    }

    // ---------------------------------------------------------------- a hand-built loop

    [Fact]
    public void RepeatingASectionPlaysThatSectionAgainRatherThanWhateverFollowedIt()
    {
        // Take the stretch of the ramp from 0.25 to 0.50 and lay it down twice, back to back.
        var audio = Ramp(40000);
        const int A = 10000, B = 20000, Len = B - A;

        var buf = Render(new ClipTimeline(new[]
        {
            Section(audio, 0, A, B),
            Section(audio, Len, A, B),
        }), Len * 2);

        // Both repeats must run 0.25 -> 0.50. If the second one read on past B it would run
        // 0.50 -> 0.75 instead, which is exactly the bug this feature exists to avoid.
        // Sampled well clear of either edge: a declick ramp is 5 ms, and reading inside one would
        // measure the envelope rather than the position.
        float StartOf(int repeat) => buf[(repeat * Len + 1000) * 2];
        float EndOf(int repeat) => buf[((repeat + 1) * Len - 1000) * 2];

        _out.WriteLine($"first {StartOf(0):F4}..{EndOf(0):F4}, second {StartOf(1):F4}..{EndOf(1):F4}");

        Assert.Equal(0.275f, StartOf(0), 2);
        Assert.Equal(0.475f, EndOf(0), 2);
        Assert.Equal(0.275f, StartOf(1), 2);
        Assert.Equal(0.475f, EndOf(1), 2);
    }

    [Fact]
    public void TheJoinBetweenTwoRepeatsDoesNotDropOut()
    {
        // A loud steady signal rather than a ramp, so a hole at the join shows as a dip in level
        // and nothing else can be mistaken for one.
        int frames = 40000;
        var s = new float[frames * 2];
        Array.Fill(s, 0.5f);
        var audio = AudioClip.FromSamples("flat", s, Rate);

        const int A = 10000, B = 20000, Len = B - A;
        var buf = Render(new ClipTimeline(new[]
        {
            Section(audio, 0, A, B),
            Section(audio, Len, A, B),
        }), Len * 2);

        // Worst level anywhere within 10 ms of the join.
        int join = Len;
        int window = (int)(0.010 * Rate);
        float worst = 1;
        for (int f = join - window; f < join + window; f++) worst = Math.Min(worst, Math.Abs(buf[f * 2]));

        _out.WriteLine($"quietest sample within 10 ms of the join: {worst:F4} (steady level is 0.5)");
        Assert.True(worst > 0.35f,
            $"the join dipped to {worst:F4} from a steady 0.5 — that is a hole at every loop repeat");
    }

    // ---------------------------------------------------------------- gaps

    [Fact]
    public void AGapIsSilentAndTheAudioAfterItStillPlaysInTheRightPlace()
    {
        var audio = Ramp(40000);
        const int Len = 8000, Gap = 5000;

        var buf = Render(new ClipTimeline(new[]
        {
            Section(audio, 0, 0, Len),
            Section(audio, Len + Gap, 20000, 20000 + Len),
        }), (Len + Gap + Len) + 2000);

        float inGap = 0;
        for (int f = Len + 200; f < Len + Gap - 200; f++) inGap = Math.Max(inGap, Math.Abs(buf[f * 2]));

        float afterGap = buf[(Len + Gap + 400) * 2];
        _out.WriteLine($"loudest sample in the gap {inGap:F6}; first section after the gap reads {afterGap:F4}");

        Assert.True(inGap < 1e-4f, $"the gap was not silent — it peaked at {inGap:F6}");

        // 20000/40000 = 0.5, so the section after the gap must start at about half way up the ramp.
        Assert.Equal(0.508f, afterGap, 2);
    }

    [Fact]
    public void ATrackReachesPastAGapSoTheRenderDoesNotStopAtTheHole()
    {
        var audio = Ramp(40000);
        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.SetTimeline(new ClipTimeline(new[]
        {
            Section(audio, 0, 0, 5000),
            Section(audio, 30000, 0, 5000),
        }));

        Assert.Equal(35000, v.TimelineLengthFrames);
        Assert.Equal(35000, graph.LengthFrames);
    }

    // ---------------------------------------------------------------- still true of one clip

    [Fact]
    public void AnOffsetSectionStillPlaysFromItsBeginningNotFromPartWayIn()
    {
        // The same trap as the single-clip version: a section laid down at track frame N must
        // start at its own first sample, not N frames into itself.
        const long Start = 9000;
        var audio = Ramp(20000);

        var buf = Render(new ClipTimeline(new[] { Section(audio, Start, 0, 20000) }), 20000);

        Assert.True(Math.Abs(buf[(Start - 100) * 2]) < 1e-3f, "audio appeared before the section did");
        Assert.True(buf[(Start + 2) * 2] < 0.004f,
            $"the section began at value {buf[(Start + 2) * 2]:F4}, so it skipped its own opening");
        Assert.Equal(0.10f, buf[(Start + 2000) * 2], 2);
    }
}
