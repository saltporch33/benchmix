using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Clip edges: trimming, declicks and fades. These are the difference between a mashup that
/// sounds assembled and one that ticks every time a section starts.
/// </summary>
public class ClipEdgeTests
{
    const int Rate = 48000;

    /// <summary>A constant-amplitude tone, so any edge ramp shows up unambiguously.</summary>
    static AudioClip Steady(double seconds, float amp = 0.5f)
    {
        int frames = (int)(seconds * Rate);
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            // A square-ish DC-offset signal: every sample is the same magnitude, so the only thing
            // that can change the output level is an envelope.
            s[f * 2] = amp;
            s[f * 2 + 1] = amp;
        }
        return AudioClip.FromSamples("steady", s, Rate);
    }

    static float[] RenderFirst(MixGraph graph, int frames)
    {
        graph.Seek(0);
        graph.SnapParameters();
        var buf = new float[frames * 2];
        graph.Render(buf, frames);
        return buf;
    }

    [Fact]
    public void AnUntrimmedClipGetsNoEdgeRampAtAll()
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Steady(1.0));
        graph.LimiterEnabled = false;

        var buf = RenderFirst(graph, 1000);

        // First sample already at full level: nothing faded it in.
        Assert.Equal(0.5f, buf[0], 5);
    }

    [Fact]
    public void ATrimmedStartIsDeclicked()
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Steady(2.0));
        t.SourceStartFrame = Rate / 2;          // cut half a second in
        graph.LimiterEnabled = false;

        var buf = RenderFirst(graph, 2000);

        // Starts from silence...
        Assert.True(Math.Abs(buf[0]) < 0.05, $"first sample was {buf[0]:F3}, expected a declick from near zero");

        // ...and is at full level once the 5 ms declick is done (240 frames at 48 kHz).
        Assert.Equal(0.5f, buf[400 * 2], 2);
    }

    [Fact]
    public void AnExplicitFadeInRampsOverTheRequestedTime()
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Steady(3.0));
        t.FadeInSeconds = 1.0;
        graph.LimiterEnabled = false;

        var buf = RenderFirst(graph, Rate);

        Assert.True(Math.Abs(buf[0]) < 0.02);                    // silent at the start
        // Equal-power law: halfway through the fade sits at sin(pi/4) = 0.707 of full, not 0.5.
        Assert.Equal(0.5f * 0.7071f, buf[(Rate / 2) * 2], 2);
        Assert.Equal(0.5f, buf[(Rate - 100) * 2], 2);            // full by the end
    }

    [Fact]
    public void AFadeOutReachesSilenceAtTheClipEnd()
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Steady(2.0));
        t.FadeOutSeconds = 0.5;
        graph.LimiterEnabled = false;

        graph.Seek(0);
        graph.SnapParameters();

        // Render the whole clip and look at the tail.
        var buf = new float[Rate * 2 * 2];
        int done = 0;
        while (done < Rate * 2)
        {
            int n = Math.Min(4096, Rate * 2 - done);
            graph.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }

        Assert.Equal(0.5f, buf[(Rate) * 2], 2);                       // mid-clip, untouched
        Assert.True(Math.Abs(buf[(Rate * 2 - 2) * 2]) < 0.02,
            $"last sample was {buf[(Rate * 2 - 2) * 2]:F3}, expected the fade to reach silence");
    }

    [Fact]
    public void TrimmingShortensTheTimeline()
    {
        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(Steady(10.0));

        Assert.Equal(10 * Rate, t.TimelineLengthFrames);

        t.SourceStartFrame = 2 * Rate;
        t.SourceEndFrame = 6 * Rate;
        Assert.Equal(4 * Rate, t.TimelineLengthFrames);

        // Trim and stretch compose: 4 seconds of source at half speed occupies 8 on the timeline.
        t.Speed = 0.5;
        Assert.Equal(8 * Rate, t.TimelineLengthFrames);
    }

    [Fact]
    public void TrimSelectsTheRightPartOfTheSource()
    {
        // A clip whose three seconds each hold a different level; trimming to the middle second
        // should give back only the middle level.
        int frames = 3 * Rate;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = f < Rate ? 0.2f : f < 2 * Rate ? 0.5f : 0.8f;
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }

        using var graph = new MixGraph(Rate);
        var t = graph.AddTrack();
        t.SetClip(AudioClip.FromSamples("steps", s, Rate));
        t.SourceStartFrame = Rate;
        t.SourceEndFrame = 2 * Rate;
        graph.LimiterEnabled = false;

        var buf = RenderFirst(graph, Rate / 2);
        Assert.Equal(0.5f, buf[(Rate / 4) * 2], 2);
    }
}
