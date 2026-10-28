using System.Text.Json;
using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// The crossfade X (Daniel, 2026-10-28): "the crossfade thingy when im splicing a clip into another
/// one….on other daw's it looked like a very narrow tall X….basically i think it just did a small
/// fraction of a second volume decrease/fade out and fade into the next clip (very very small,
/// fraction of second)". Every butt join has been crossfaded over 5 ms since 2026-09-24; this is
/// the length he can now pick for a splice, the rule for where the lane draws the X, and the file.
///
/// <para>
/// The engine tests use a ramp source — every sample is its own position — cut so the two sides of
/// the join do not meet. The crossfade's shape is then visible sample by sample, and a crossfade of
/// the wrong length, or applied to the wrong frames, cannot pass. The law is written out here, not
/// asked of the engine.
/// </para>
/// </summary>
public class CrossfadeTests : IDisposable
{
    const int Rate = 48000;
    const int Frames = Rate * 40;

    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    private readonly string _wav;

    public CrossfadeTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-crossfade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");
        Ramp().SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Value == position / Frames, in both channels: where every sample came from is in the sample.</summary>
    static AudioClip Ramp()
    {
        var s = new float[Frames * 2];
        for (int f = 0; f < Frames; f++) s[f * 2] = s[f * 2 + 1] = f / (float)Frames;
        return AudioClip.FromSamples("ramp", s, Rate);
    }

    static float[] Render(MixGraph graph, int frames, int block = 1000)
    {
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

    /// <summary>The law: equal power, from the join's first frame, over n output frames.</summary>
    static (double Rise, double Fall) Law(int f, int n)
    {
        double t = (f + 0.5) / n;
        return (Math.Sin(Math.PI / 2 * t), Math.Cos(Math.PI / 2 * t));
    }

    // Section A plays 10000..22418 of the file; B starts at 40000, so the join jumps.
    const long AStart = 10000, AEnd = 22418, BStart = 40000;
    const long Join = AEnd - AStart;

    static ClipTimeline Spliced(AudioClip audio, long bLength, double crossfade) => new(new[]
    {
        new VoiceClip { Audio = audio, TrackStartFrame = 0, SourceStartFrame = AStart, SourceEndFrame = AEnd },
        new VoiceClip { Audio = audio, TrackStartFrame = Join, SourceStartFrame = BStart, SourceEndFrame = BStart + bLength, CrossfadeSeconds = crossfade },
    });

    /// <summary>How far the rendered join strays from the law over its first <paramref name="frames"/> frames.</summary>
    static (double Worst, int At) WorstMiss(float[] buf, float[] src, int n, int frames)
    {
        double worst = 0; int at = -1;
        for (int f = 0; f < frames; f++)
        {
            var (rise, fall) = Law(f, n);
            double want = src[(BStart + f) * 2] * rise + src[(AEnd + f) * 2] * fall;   // B rising under A's continuation past its cut
            double miss = Math.Abs(buf[(Join + f) * 2] - want);
            if (miss > worst) { worst = miss; at = f; }
        }
        return (worst, at);
    }

    [Theory]
    [InlineData(0.0, 240)]       // nothing picked: the standard 5 ms
    [InlineData(0.05, 2400)]     // 50 ms from the menu
    [InlineData(0.2, 9600)]      // the longest on the menu
    public void TheSpliceFadesOverTheChosenLengthByTheEqualPowerLaw(double chosen, int n)
    {
        var audio = Ramp();
        using var graph = new MixGraph(Rate);
        graph.AddTrack().SetTimeline(Spliced(audio, Rate, chosen));
        var buf = Render(graph, (int)Join + Rate);
        var src = audio.Samples;

        var (worst, at) = WorstMiss(buf, src, n, n);
        _out.WriteLine($"{chosen * 1000:0} ms: worst miss from the law over {n} frames {worst:E2}, at frame {at}");
        Assert.True(worst <= 1e-6,
            $"{chosen * 1000:0} ms crossfade: frame {at} after the join is {worst:E2} off the equal-power law over {n} frames — "
          + "the splice is not fading over the length he picked, so it either still clicks or the fade runs on into what follows");

        // After the fade the incoming section is exactly itself, up to its own end declick.
        int differing = 0;
        for (int f = n; f < Rate - 480; f++) if (buf[(Join + f) * 2] != src[(BStart + f) * 2]) differing++;
        Assert.True(differing == 0,
            $"{differing} samples after the {n}-frame fade differ from the incoming section's own — the crossfade is longer than chosen, or alters what follows it");
    }

    [Fact]
    public void ACrossfadeLongerThanTheSectionEndsWithTheSection()
    {
        // 200 ms picked, into a section 1000 frames long: the fade must be over by the section's
        // end, or whatever comes next would cut it dead mid-fade — the very click a crossfade is for.
        var audio = Ramp();
        using var graph = new MixGraph(Rate);
        graph.AddTrack().SetTimeline(Spliced(audio, 1000, 0.2));
        var buf = Render(graph, (int)Join + 2000);
        var src = audio.Samples;

        // Up to the section's own end declick (its last 240 frames, before a gap).
        var clamped = WorstMiss(buf, src, 1000, 760);
        var unclamped = WorstMiss(buf, src, 9600, 760);
        _out.WriteLine($"fade cut to the section: worst miss {clamped.Worst:E2}; against the full 200 ms law it would be {unclamped.Worst:E2}");
        Assert.True(clamped.Worst <= 1e-6,
            $"frame {clamped.At} after the join is {clamped.Worst:E2} off a fade cut to the section's 1000 frames — "
          + "a fade longer than its section is cut dead at the section's end, which is a click");
    }

    // ---- a loop join on a sine, as JoinTests measure it, with a longer crossfade ----

    const double Hz = 431;                  // not 440: see JoinTests
    const int LoopStart = 10000, LoopLength = 12418;

    public enum Chain { Untouched, Linked, Pitched, Stretched }

    static AudioClip Sine(int frames)
    {
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * Hz * f / Rate));
        return AudioClip.FromSamples("sine", s, Rate);
    }

    static float[] RenderLoop(ClipTimeline timeline, Chain path, int frames, out TempoMap map)
    {
        using var graph = new MixGraph(Rate);
        var v = graph.AddTrack();
        v.SetTimeline(timeline);
        switch (path)
        {
            case Chain.Linked: v.Mode = StretchMode.Linked; break;
            case Chain.Pitched: v.Mode = StretchMode.Independent; v.Semitones = 2; break;
            case Chain.Stretched: v.Mode = StretchMode.Independent; v.Speed = 0.93; break;
        }
        map = v.Tempo;
        return Render(graph, frames);
    }

    static float WorstStep(float[] buf, int from, int to)
    {
        float worst = 0;
        for (int f = from; f < to; f++) worst = Math.Max(worst, Math.Abs(buf[(f + 1) * 2] - buf[f * 2]));
        return worst;
    }

    [Theory]
    [InlineData(Chain.Untouched)]
    [InlineData(Chain.Linked)]
    [InlineData(Chain.Pitched)]
    [InlineData(Chain.Stretched)]
    public void ALongerCrossfadeStillLeavesNoStepAtALoopJoinAndMovesNothingAfterIt(Chain path)
    {
        var audio = Sine(Rate * 3);
        VoiceClip Repeat(long at, double crossfade) => new()
        {
            Audio = audio, TrackStartFrame = at, SourceStartFrame = LoopStart, SourceEndFrame = LoopStart + LoopLength, CrossfadeSeconds = crossfade,
        };
        var joined = RenderLoop(new ClipTimeline(new[] { Repeat(0, 0), Repeat(LoopLength, 0.05) }), path, LoopLength * 2, out var map);
        var alone = RenderLoop(new ClipTimeline(new[] { Repeat(LoopLength, 0.05) }), path, LoopLength * 2, out _);

        int join = (int)Math.Round(map.RelAt(LoopLength));
        int fade = (int)(0.05 * Rate);
        float steady = WorstStep(joined, 3000, join - 1500);
        float across = WorstStep(joined, join - 480, join + fade + 480);
        _out.WriteLine($"{path}: worst step across the 50 ms fade {across:F4}, elsewhere {steady:F4}");
        Assert.True(across <= steady * 1.5f,
            $"{path}: the signal jumped by {across:F4} across a 50 ms crossfade against {steady:F4} everywhere else — a longer crossfade must not click where the 5 ms one does not");

        int differing = 0;
        for (int i = ((int)Math.Ceiling(map.RelAt(LoopLength)) + fade + 1) * 2; i < joined.Length; i++)
            if (joined[i] != alone[i]) differing++;
        Assert.True(differing == 0,
            $"{path}: {differing} samples of the incoming repeat differ from the same repeat played alone after a 50 ms fade — the longer crossfade is moving or altering the audio after it");
    }

    // ---- the engine notices a changed length ----

    [Fact]
    public void ASectionWhoseCrossfadeChangedIsNotTheSameSection()
    {
        var audio = Ramp();
        var a = new VoiceClip { Audio = audio, TrackStartFrame = 0, SourceStartFrame = 0, SourceEndFrame = 1000 };
        var b = new VoiceClip { Audio = audio, TrackStartFrame = 1000, SourceStartFrame = 5000, SourceEndFrame = 6000 };
        var bLonger = new VoiceClip { Audio = audio, TrackStartFrame = 1000, SourceStartFrame = 5000, SourceEndFrame = 6000, CrossfadeSeconds = 0.05 };
        var was = new ClipTimeline(new[] { a, b });
        var now = new ClipTimeline(new[] { a, bLonger });

        Assert.True(now.Equivalent(b) is null,
            "a section with a new crossfade length counted as the same section: a voice bound to it would keep playing the old length until something else re-primed it");
        Assert.True(ReferenceEquals(now.Equivalent(a), now[0]), "the untouched section before it must still carry across the edit");
        Assert.False(was.Matches(now), "two arrangements differing only in a crossfade length count as the same: the edit would be dropped as a no-op");
    }

    // ---- where the X goes: the editor's rule is the engine's ----

    static ClipModel At(TrackModel t, double trackSeconds) => t.Clips.Single(c => Math.Abs(c.TrackStartSeconds - trackSeconds) < 1e-6);

    [Fact]
    public async Task TheXIsDrawnOnlyAtAButtJoinWithNoFadeOfItsOwnOnEitherSide()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, 0);
        Assert.True(TimelineEditor.Split(t, 10, s).Changed && TimelineEditor.Split(t, 20, s).Changed, "could not split the ramp at 10 s and 20 s");
        var (first, second, third) = (At(t, 0), At(t, 10), At(t, 20));
        Assert.True(TimelineEditor.CrossfadeAt(t, first, s) == 0 && TimelineEditor.CrossfadeAt(t, second, s) == 0 && TimelineEditor.CrossfadeAt(t, third, s) == 0,
            "a seam — a split nobody moved — got an X: it would say the audio is being faded where not one sample changes");

        // Take the middle out and butt the last piece against the first: a join the engine crossfades.
        TimelineEditor.Delete(t, second);
        third.TrackStartSeconds = 10;
        TimelineEditor.Normalise(t);
        s.Apply(t);
        var engine = s.VoiceFor(t)!.Timeline;
        Assert.True(engine.Count == 2 && engine.ButtJoinAfter(0), "the two pieces did not become a butt join in the engine");
        Assert.True(TimelineEditor.CrossfadeAt(t, third, s) == TimelineEditor.StandardCrossfadeSeconds,
            $"a butt join with nothing picked draws {TimelineEditor.CrossfadeAt(t, third, s) * 1000:0.#} ms, not the standard 5 ms the engine fades it over");
        Assert.True(TimelineEditor.CrossfadeAt(t, first, s) == 0, "the first section, with nothing before it, got an X");

        third.CrossfadeSeconds = 0.05;
        s.Apply(t);
        Assert.True(TimelineEditor.CrossfadeAt(t, third, s) == 0.05 && s.VoiceFor(t)!.Timeline[1].CrossfadeSeconds == 0.05,
            "the picked 50 ms did not reach both the X and the engine");

        first.FadeOutSeconds = 0.25;
        Assert.True(TimelineEditor.CrossfadeAt(t, third, s) == 0, "with a fade of its own on the outgoing side there is no crossfade — each side ramps alone — yet an X was drawn");
        first.FadeOutSeconds = 0;
        third.FadeInSeconds = 0.25;
        Assert.True(TimelineEditor.CrossfadeAt(t, third, s) == 0, "with a fade-in of its own on the incoming side there is no crossfade, yet an X was drawn");
        third.FadeInSeconds = 0;

        third.TrackStartSeconds = 10.5;                      // a gap now
        TimelineEditor.Normalise(t);
        Assert.True(TimelineEditor.CrossfadeAt(t, third, s) == 0, "a section after a gap got an X: a gap is declicked on each side, never crossfaded");
    }

    [Fact]
    public async Task ARepeatCarriesTheLengthAndASplitSecondHalfStartsWithTheStandard()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, 0);
        TimelineEditor.Split(t, 10, s);
        var piece = At(t, 0);
        TimelineEditor.Delete(t, At(t, 10));
        piece.CrossfadeSeconds = 0.1;

        Assert.True(TimelineEditor.Duplicate(t, piece, s).Changed, "could not repeat the piece");
        var repeat = At(t, 10);
        Assert.True(repeat.CrossfadeSeconds == 0.1 && TimelineEditor.CrossfadeAt(t, repeat, s) == 0.1,
            "a repeat did not carry the length picked for the section: every repeat of a loop must splice alike");

        TimelineEditor.Split(t, 5, s);
        Assert.True(At(t, 0).CrossfadeSeconds == 0.1, "splitting a section took the length away from the start it was picked for");
        Assert.True(At(t, 5).CrossfadeSeconds == 0, "a split's second half kept the length: its start is a new one, and would splice at 100 ms wherever it is later butted");
    }

    // ---- the file ----

    [Fact]
    public void TheLengthSurvivesSaveAndReopenAndAVersion5FileOpensWithTheStandard()
    {
        string path = Path.Combine(_dir, "x.benchmix");
        var p = new BenchmixProject();
        p.Tracks.Add(new TrackModel
        {
            Name = "T", SourcePath = _wav,
            Clips =
            {
                new ClipModel { SourcePath = _wav, TrackStartSeconds = 0, SourceEndSeconds = 1 },
                new ClipModel { SourcePath = _wav, TrackStartSeconds = 1, SourceStartSeconds = 5, SourceEndSeconds = 6, CrossfadeSeconds = 0.1 },
            },
        });
        ProjectStore.Save(p, path);
        var loaded = ProjectStore.Load(path).Project;
        Assert.True(loaded.SchemaVersion == 6 && loaded.Tracks[0].Clips[1].CrossfadeSeconds == 0.1 && loaded.Tracks[0].Clips[0].CrossfadeSeconds == 0,
            "a saved project lost a splice's crossfade length, or is not a version 6 file");

        // A version 5 file, as v2026.10.27.7 wrote it: no crossfade lengths anywhere.
        string old = Path.Combine(_dir, "old.benchmix");
        var v5 = new
        {
            schemaVersion = 5, name = "Old", sampleRate = Rate, masterGain = 1.0, limiterEnabled = true, eqMode = "Isolator",
            currentTabIndex = 0,
            tabs = new[]
            {
                new
                {
                    id = Guid.NewGuid(), name = "Mix", projectBpm = 93.0,
                    tracks = new[]
                    {
                        new { id = Guid.NewGuid(), name = "Bed", sourcePath = _wav, speed = 1.0,
                              clips = new[] { new { id = Guid.NewGuid(), sourcePath = _wav, trackStartSeconds = 0.0, sourceStartSeconds = 0.0, sourceEndSeconds = -1.0 } } },
                    },
                },
            },
        };
        File.WriteAllText(old, JsonSerializer.Serialize(v5, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.True(ProjectStore.TryRead(old, out var opened) && opened is not null, "a version 5 file no longer opens");
        Assert.True(opened!.SchemaVersion == BenchmixProject.CurrentSchemaVersion && opened.Tracks.Count == 1 && opened.Tracks[0].Clips.Single().CrossfadeSeconds == 0,
            "a version 5 file's section did not open with the standard crossfade (0), or the file was not brought to the current version");
    }
}
