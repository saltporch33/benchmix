using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// The join between two sections that butt together without continuing each other — every repeat
/// of a hand-built loop is one.
///
/// <para>
/// These use a sine cut at two points half a cycle apart, so the waveform on one side of the join
/// is heading the opposite way to the other. A loop cut anywhere in real music is like that, and
/// nothing about it is rare. The first attempt at measuring this cut at two points that happened
/// to be in phase and saw nothing wrong, which is the constant-tone trap in another form: the test
/// signal has to make the defect visible or the test proves nothing.
/// </para>
/// <para>
/// The same trap sprang twice. The tone was 440 Hz at first, and the phase vocoder's delay —
/// 7200 frames — is exactly 66 cycles of 440 Hz, so a defect that put the wrong audio 7200 frames
/// out came back in phase and passed. 431 Hz lines up with nothing here.
/// </para>
/// </summary>
public class JoinTests
{
    const int Rate = 48000;

    // 431 Hz, amplitude 0.5: the largest step between neighbouring samples is 0.028. A source
    // offset of 12418 frames is 111.5 cycles, so the second repeat starts half a cycle out.
    const double Hz = 431;
    const int LoopStart = 10000;
    const int LoopLength = 12418;

    private readonly ITestOutputHelper _out;
    public JoinTests(ITestOutputHelper output) => _out = output;

    public enum Path { Untouched, Linked, LinkedFaster, LinkedSlower, Pitched, PitchedSlightly, Stretched, StretchedFaster }

    static AudioClip Sine(int frames)
    {
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * Hz * f / Rate));
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        return AudioClip.FromSamples("sine", s, Rate);
    }

    static VoiceClip Repeat(AudioClip audio, long trackStart) => new()
    {
        Audio = audio,
        TrackStartFrame = trackStart,
        SourceStartFrame = LoopStart,
        SourceEndFrame = LoopStart + LoopLength,
    };

    static double Configure(TrackVoice v, Path path)
    {
        switch (path)
        {
            case Path.Linked: v.Mode = StretchMode.Linked; return 1.0;
            case Path.LinkedFaster: v.Mode = StretchMode.Linked; v.Speed = 1.05; return 1.05;
            case Path.LinkedSlower: v.Mode = StretchMode.Linked; v.Speed = 0.7; return 0.7;
            case Path.Pitched: v.Mode = StretchMode.Independent; v.Semitones = 2; return 1.0;
            case Path.PitchedSlightly: v.Mode = StretchMode.Independent; v.Semitones = 0.5; return 1.0;
            case Path.Stretched: v.Mode = StretchMode.Independent; v.Speed = 0.93; return 0.93;
            case Path.StretchedFaster: v.Mode = StretchMode.Independent; v.Speed = 1.25; return 1.25;
            default: return 1.0;
        }
    }

    static float[] Render(ClipTimeline timeline, Path path, int frames, int block, out double speed)
    {
        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.SetTimeline(timeline);
        speed = Configure(v, path);
        graph.LimiterEnabled = false;
        graph.Seek(0);
        graph.SnapParameters();

        var buf = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(block, frames - done);
            graph.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    static float WorstStep(float[] buf, int from, int to)
    {
        float worst = 0;
        for (int f = from; f < to; f++) worst = Math.Max(worst, Math.Abs(buf[(f + 1) * 2] - buf[f * 2]));
        return worst;
    }

    [Theory]
    [InlineData(Path.Untouched)]
    [InlineData(Path.Linked)]
    [InlineData(Path.LinkedFaster)]
    [InlineData(Path.LinkedSlower)]
    [InlineData(Path.Pitched)]
    [InlineData(Path.PitchedSlightly)]
    [InlineData(Path.Stretched)]
    [InlineData(Path.StretchedFaster)]
    public void ALoopRepeatDoesNotClickWhereTheTwoCutsDoNotMatch(Path path)
    {
        var audio = Sine(Rate * 3);
        var timeline = new ClipTimeline(new[] { Repeat(audio, 0), Repeat(audio, LoopLength) });

        var buf = Render(timeline, path, LoopLength * 2, 1000, out double speed);
        int join = (int)Math.Round(LoopLength / speed);

        // The same render, well away from any edge, is the yardstick — not a number from outside.
        float steady = WorstStep(buf, 3000, join - 1500);
        float atJoin = WorstStep(buf, join - 480, join + 480);
        _out.WriteLine($"{path}: worst step within 10 ms of the join {atJoin:F4}, elsewhere {steady:F4}");

        Assert.True(atJoin <= steady * 1.5f,
            $"{path}: the signal jumped by {atJoin:F4} at the loop join against {steady:F4} everywhere else — "
          + "that is a click on every repeat of every loop cut at an arbitrary point");
    }

    [Theory]
    [InlineData(Path.Untouched)]
    [InlineData(Path.Linked)]
    [InlineData(Path.LinkedFaster)]
    [InlineData(Path.LinkedSlower)]
    [InlineData(Path.Pitched)]
    [InlineData(Path.PitchedSlightly)]
    [InlineData(Path.Stretched)]
    [InlineData(Path.StretchedFaster)]
    public void TheCrossfadeDoesNotMoveTheSectionAfterTheJoin(Path path)
    {
        var audio = Sine(Rate * 3);

        // The second repeat on its own, at the same place, is the reference: after the 5 ms fade
        // the joined render must be exactly it. A fade that shifted or smeared the incoming section
        // would pass a click test and still put the loop out of time.
        var joined = Render(new ClipTimeline(new[] { Repeat(audio, 0), Repeat(audio, LoopLength) }),
                            path, LoopLength * 2, 1000, out double speed);
        var alone = Render(new ClipTimeline(new[] { Repeat(audio, LoopLength) }),
                           path, LoopLength * 2, 1000, out _);

        int join = (int)Math.Ceiling(LoopLength / speed);
        int fade = (int)(TrackVoice.DeclickSeconds * Rate);
        int differing = 0;
        for (int i = (join + fade + 1) * 2; i < joined.Length; i++)
            if (joined[i] != alone[i]) differing++;

        _out.WriteLine($"{path}: {differing} samples differ after the fade");
        Assert.True(differing == 0,
            $"{path}: {differing} samples of the incoming repeat differ from the same repeat played alone — "
          + "the join is moving or altering the audio after it, not just smoothing the edge");
    }

    [Theory]
    [InlineData(Path.Untouched, false)]
    [InlineData(Path.Linked, false)]
    [InlineData(Path.Pitched, false)]
    [InlineData(Path.Untouched, true)]
    [InlineData(Path.Linked, true)]
    [InlineData(Path.Pitched, true)]
    public void AJoinIsStillCrossfadedWhenTheArrangementIsReplacedRightAtIt(Path path, bool outgoingChanged)
    {
        // Any edit during playback hands the voice a new arrangement, which drops what it was
        // bound to. If that lands on the very block that starts at a join, the voice has not been
        // playing the outgoing section as far as it knows — and the join used to get neither the
        // crossfade nor a declick: a step of 0.97.
        var audio = Sine(Rate * 3);
        ClipTimeline Arrangement() => new(new[] { Repeat(audio, 0), Repeat(audio, LoopLength) });

        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.SetTimeline(Arrangement());
        Configure(v, path);
        graph.LimiterEnabled = false;
        graph.Seek(0);
        graph.SnapParameters();

        int total = LoopLength * 2;
        var buf = new float[total * 2];
        int done = 0;
        while (done < LoopLength)
        {
            int n = Math.Min(1000, LoopLength - done);
            graph.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        // Either the same sections as new objects, or with the outgoing one trimmed at its head —
        // which the voice cannot carry its binding across, so it rebuilds the outgoing state.
        v.SetTimeline(outgoingChanged
            ? new ClipTimeline(new[]
              {
                  new VoiceClip { Audio = audio, TrackStartFrame = 100, SourceStartFrame = LoopStart + 100, SourceEndFrame = LoopStart + LoopLength },
                  Repeat(audio, LoopLength),
              })
            : Arrangement());
        while (done < total)
        {
            int n = Math.Min(1000, total - done);
            graph.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }

        float steady = WorstStep(buf, 3000, LoopLength - 1500);
        float atJoin = WorstStep(buf, LoopLength - 480, LoopLength + 480);
        _out.WriteLine($"{path}, arrangement replaced at the join ({(outgoingChanged ? "outgoing changed" : "unchanged")}): worst step {atJoin:F4}, elsewhere {steady:F4}");
        // When the outgoing section itself changed, its state has to be rebuilt from scratch, and a
        // freshly primed phase vocoder is close to, but not sample-continuous with, the one that was
        // playing: 0.045 against 0.028 measured. That is allowed a little more room; the defect this
        // pins was 0.97.
        float allowed = outgoingChanged && path == Path.Pitched ? 2.0f : 1.5f;
        Assert.True(atJoin <= steady * allowed,
            $"{path}: replacing the arrangement right at a join left it jumping by {atJoin:F4} against {steady:F4}");
    }

    [Fact]
    public void AJoinOnAFrameThatRoundsAwkwardlyIsStillCrossfaded()
    {
        // At 0.7x, ceil(14343 / 0.7) * 0.7 comes out a hair under 14343 in floating point. The
        // outgoing section then ran one frame past the join, the join went unrecognised, and the
        // switch was hard: a step of 0.40 against 0.02.
        const int Join = 14343;
        var audio = Sine(Rate * 3);
        var timeline = new ClipTimeline(new[]
        {
            new VoiceClip { Audio = audio, TrackStartFrame = 0, SourceStartFrame = LoopStart, SourceEndFrame = LoopStart + Join },
            new VoiceClip { Audio = audio, TrackStartFrame = Join, SourceStartFrame = LoopStart, SourceEndFrame = LoopStart + Join },
        });

        var buf = Render(timeline, Path.LinkedSlower, (int)(Join * 2 / 0.7), 1000, out _);
        int join = (int)Math.Ceiling(Join / 0.7);
        float steady = WorstStep(buf, 3000, join - 1500);
        float atJoin = WorstStep(buf, join - 480, join + 480);
        _out.WriteLine($"join at track frame {Join}, 0.7x: worst step {atJoin:F4}, elsewhere {steady:F4}");

        Assert.True(atJoin <= steady * 1.5f,
            $"the join jumped by {atJoin:F4} against {steady:F4} elsewhere — it was not crossfaded");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFadeOnOneSideOfAJoinStillLeavesTheOtherSideDeclicked(bool fadeOnTheWayOut)
    {
        // One side has a fade of its own, so the join is not crossfaded. The other side is then a
        // cut edge like any other and must get its declick — not start (or stop) dead on whatever
        // sample it was cut at, which on this signal is 0.43 away from silence.
        var audio = Sine(Rate * 3);
        var first = Repeat(audio, 0);
        var second = Repeat(audio, LoopLength);
        var timeline = new ClipTimeline(new[]
        {
            fadeOnTheWayOut ? new VoiceClip { Audio = audio, TrackStartFrame = 0, SourceStartFrame = LoopStart,
                                              SourceEndFrame = LoopStart + LoopLength, FadeOutSeconds = 0.05 } : first,
            fadeOnTheWayOut ? second : new VoiceClip { Audio = audio, TrackStartFrame = LoopLength, SourceStartFrame = LoopStart,
                                                       SourceEndFrame = LoopStart + LoopLength, FadeInSeconds = 0.05 },
        });

        var buf = Render(timeline, Path.Untouched, LoopLength * 2, 1000, out _);
        float steady = WorstStep(buf, 3000, LoopLength - 3000);
        float atJoin = WorstStep(buf, LoopLength - 480, LoopLength + 480);
        _out.WriteLine($"fade {(fadeOnTheWayOut ? "out" : "in")}: worst step at the join {atJoin:F4}, elsewhere {steady:F4}");

        Assert.True(atJoin <= steady * 1.5f,
            $"the side of the join without a fade jumped by {atJoin:F4} (elsewhere {steady:F4}) — "
          + "it was left without a declick because the frames touched, though nothing covered the join");
    }

    [Theory]
    [InlineData(Path.Untouched)]
    [InlineData(Path.Linked)]
    [InlineData(Path.LinkedFaster)]
    [InlineData(Path.LinkedSlower)]
    public void AJoinNearTheEndOfABlockSoundsTheSameAsOneInTheMiddle(Path path)
    {
        // Not the vocoder paths: the stretcher itself comes out slightly differently for different
        // block sizes even with no join at all, which is older than joins and not what this checks.
        // Preview pulls small blocks and a render pulls large ones. A fade that stopped at the end
        // of whichever block it started in would make the two disagree, and would leave a step
        // wherever it was cut short. 12382 - 30 puts the join 30 frames before a block boundary.
        var audio = Sine(Rate * 3);
        var timeline = new ClipTimeline(new[] { Repeat(audio, 0), Repeat(audio, LoopLength) });

        var small = Render(timeline, path, LoopLength * 2, LoopLength - 30, out _);
        var large = Render(timeline, path, LoopLength * 2, 8192, out _);

        int differing = 0;
        for (int i = 0; i < small.Length; i++) if (small[i] != large[i]) differing++;

        Assert.True(differing == 0,
            $"{path}: {differing} samples differ between block sizes — preview and render would not match at a loop join");
    }
}
