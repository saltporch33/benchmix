using System.Text.RegularExpressions;
using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Editing a track's warp markers (Daniel, 2026-10-18) through the editor: putting one down changes
/// nothing, dragging one puts its beat where it is let go and spreads the audio between its
/// neighbours, and the markers stay on their beats through every other edit. Where a part of the
/// song should be heard is worked out in <see cref="Heard"/>, written out here — never asked of
/// the tempo map — and the engine is held to the editor by
/// <see cref="TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes"/>.
/// </summary>
public class WarpEditingTests : IDisposable
{
    const int Rate = 48000;
    const double Frame = 1.0 / Rate;
    const double V = 0.95;          // the TEMPO knob: so a tempo second and a timeline second differ
    const double O = 2.0;           // the track's origin: so nothing is measured from 0:00 by accident
    private readonly string _dir;
    private readonly string _wav;

    public WarpEditingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-warps", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");
        int frames = Rate * 60;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = f / (float)frames;
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// Where second <paramref name="p"/> of the song is heard, written out for a track at one speed
    /// <paramref name="v"/> from origin <paramref name="o"/> with these markers (its audio, and how far
    /// its tempo has carried it there): the audio spread evenly from each marker to the next, through
    /// (0, 0) before the first, and at its tempo after the last; the tempo is p ÷ v on the timeline.
    /// </summary>
    static double Heard(double p, double o, double v, params (double Audio, double Tempo)[] markers)
    {
        double a0 = 0, t0 = 0;
        foreach (var (a, tempo) in markers)
        {
            if (p < a) return o + (t0 + (p - a0) * (tempo - t0) / (a - a0)) / v;
            (a0, t0) = (a, tempo);
        }
        return o + (t0 + (p - a0)) / v;
    }

    static double Heard(double p, params (double Audio, double Tempo)[] markers) => Heard(p, O, V, markers);

    static long Start(ClipModel c, ProjectSession s) => TimelineEditor.StartFrameOf(c, s);
    static long End(ClipModel c, ProjectSession s) => TimelineEditor.EndFrameOf(c, s);
    static double When(TrackModel t, ClipModel c, ProjectSession s) => TimelineEditor.TimelineSecondsOfFrame(t, Start(c, s), s);
    static ClipModel AtTrackSeconds(TrackModel t, double trackSeconds) => t.Clips.Single(c => Math.Abs(c.TrackStartSeconds - trackSeconds) < 1e-6);
    static double OnFrame(double seconds) => Math.Round(seconds * Rate) / Rate;
    static double Beat(TrackModel t, int i) => WarpMarkers.TimelineSecondsOf(t, i);
    static double[] Beats(TrackModel t) => Enumerable.Range(0, t.Warps.Count).Select(i => Beat(t, i)).ToArray();

    /// <summary>A track of the song at <paramref name="origin"/>, cut at these seconds of the song, then set to <paramref name="speed"/>.</summary>
    async Task<(ProjectSession S, TrackModel T)> DeckAsync(double origin, double speed, params double[] cutsAtTrackSeconds)
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, origin);
        foreach (var p in cutsAtTrackSeconds) Assert.True(TimelineEditor.Split(t, origin + p, s).Changed, $"could not split at {p}s of the song");
        t.Speed = speed;
        s.ApplyAll();
        return (s, t);
    }

    /// <summary>The usual deck: at 0.95 from 2 s, cut at 10, 20, 30 and 40 s of the song, with markers on 12, 24 and 36 s, none moved.</summary>
    async Task<(ProjectSession S, TrackModel T)> StandardAsync()
    {
        var (s, t) = await DeckAsync(O, V, 10, 20, 30, 40);
        foreach (double p in new[] { 12.0, 24, 36 }) Pin(s, t, p);
        return (s, t);
    }

    /// <summary>Puts a marker on second <paramref name="trackSeconds"/> of the song, clicked where it is heard.</summary>
    static int Pin(ProjectSession s, TrackModel t, double trackSeconds)
    {
        var r = WarpMarkers.Add(t, TimelineEditor.TimelineSecondsAt(t, trackSeconds), s, out int i);
        Assert.True(r.Changed && t.Warps[i].TrackSeconds == trackSeconds, $"setup: a warp marker should have gone on {trackSeconds}s of the song: {r.Message}");
        s.ApplyAll();
        return i;
    }

    /// <summary>One whole drag of marker <paramref name="index"/>: pressed, moved to <paramref name="to"/>, let go.</summary>
    static (WarpDrag Drag, EditResult Result) Drag(ProjectSession s, TrackModel t, int index, double to, SnapSettings? snap = null, double? playhead = null)
    {
        var d = new WarpDrag(s, t, index);
        d.DragTo(to, snap ?? SnapSettings.Off, playhead);
        var r = d.Finish();
        s.ApplyAll();
        return (d, r);
    }

    /// <summary>Puts a marker on each of these seconds of the song, then drags each one's beat by its amount.</summary>
    static void Warp(ProjectSession s, TrackModel t, params (double TrackSeconds, double By)[] markers)
    {
        foreach (var (p, _) in markers) Pin(s, t, p);
        foreach (var (p, by) in markers)
        {
            int i = t.Warps.FindIndex(w => w.TrackSeconds == p);
            var (_, r) = Drag(s, t, i, Beat(t, i) + by);
            Assert.True(r.Changed, $"setup: the marker on {p}s of the song did not move: {r.Message}");
        }
    }

    static void Ramp(ProjectSession s, TrackModel t, double from, double to, double toSpeed)
    {
        t.Ramps.Add(new TempoRamp(from, to, toSpeed));
        s.ApplyAll();
    }

    static float[] Render(ProjectSession session, int frames)
    {
        session.ApplyAll();
        var g = session.Graph;
        g.LimiterEnabled = false;
        g.Seek(0);
        g.SnapParameters();
        var buf = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(1024, frames - done);
            g.Render(buf.AsSpan(done * 2, n * 2), n);
            done += n;
        }
        return buf;
    }

    static int Differing(float[] a, float[] b) => Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]);

    // ---------------------------------------------------------------- putting a marker down

    [Fact]
    public async Task PlacingAMarkerChangesNothing_AndOneBeforeTheTracksStartOrOnAnotherIsRefused()
    {
        var (s, t) = await DeckAsync(O, V, 10, 20);
        var when = t.Clips.Select(c => When(t, c, s)).ToArray();

        var r = WarpMarkers.Add(t, O + 15 / V + 0.3 * Frame, s, out int i);
        var m = t.Warps.Single();
        Assert.True(r.Changed && i == 0 && m.TrackSeconds * Rate == Math.Round(m.TrackSeconds * Rate),
            $"a warp marker did not go on a whole frame of the song — sections lined up against it would be a frame off: {r.Message}");
        Assert.True(m.TrackSeconds == m.TempoSeconds, "a warp marker that was only put down is already moved — clicking would pull the song out of time");
        Assert.True(TimelineEditor.SecondsMapOf(t) is null && s.EngineTempoOf(t).IsConstant,
            "putting a marker down put the track through different arithmetic — every section on it would move by a hair, and the stretcher would start");
        Assert.True(t.Clips.Select(c => When(t, c, s)).SequenceEqual(when), "putting a warp marker down moved a section — nothing should be heard to change");

        Assert.False(WarpMarkers.Add(t, O + 0.05, s, out _).Changed, "a warp marker went on the very start of the track, where there is no beat to pin");
        Assert.False(WarpMarkers.Add(t, O - 3, s, out _).Changed, "a warp marker went before the track starts");
        Assert.False(WarpMarkers.Add(t, O + 15.05 / V, s, out _).Changed, "a second warp marker went on top of the first — two pins on one beat");
        Assert.True(t.Warps.Count == 1, "a refused warp marker was put down anyway");
    }

    [Fact]
    public async Task MarkersPutDownOnAnyFrameOfAnUntouchedTrackKeepItOutOfTheStretcher()
    {
        // Regression (2026-10-18): the engine took an unmoved marker's tempo as seconds × rate, which
        // for many whole frames k is k ± one ulp — so the engine saw it as moved while the editor
        // said nothing was warped, and an untouched track went through the stretcher.
        var (s, t) = await DeckAsync(0, 1.0);
        var before = Render(s, Rate * 60);
        var rng = new Random(20261018);
        int placed = 0;
        for (int k = 0; k < 200; k++)
        {
            double at = (rng.NextInt64(Rate / 5, 59L * Rate) + rng.NextDouble() - 0.5) / Rate;
            if (!WarpMarkers.Add(t, at, s, out int i).Changed) continue;           // too close to one already down
            placed++;
            var m = t.Warps[i];
            Assert.True(m.TrackSeconds == m.TempoSeconds, $"a warp marker put down at {m.TrackSeconds}s is already moved");
            Assert.True(s.EngineTempoOf(t).IsConstant,
                $"a warp marker put down on frame {s.SecondsToFrames(m.TrackSeconds)} looks moved to the engine — the untouched track would go through the stretcher "
              + "and stop being bit-identical, while the lane says nothing is warped");
        }
        Assert.True(placed >= 100, $"setup: only {placed} markers went down");
        Assert.True(t.Warps.Any(w => w.TrackSeconds * Rate != Math.Round(w.TrackSeconds * Rate)),
            "setup: none of the frames is one whose seconds times the rate is a hair off a whole number — this would not catch the defect it is for");
        Assert.True(TimelineEditor.SecondsMapOf(t) is null, "with only unmoved markers the editor treats the track as warped");

        s.ApplyAll();
        var v = s.VoiceFor(t)!;
        Assert.True(v.Tempo.IsConstant && v.IsUnprocessed,
            "an untouched track with only unmoved warp markers goes through the stretcher — it is no longer bit-identical, and it plays 150 ms of latency compensation it did not need");
        int differing = Differing(before, Render(s, Rate * 60));
        Assert.True(differing == 0, $"{differing} samples changed after putting {placed} warp markers on an untouched track — putting one down must change nothing");
    }

    [Fact]
    public async Task PlacingMarkersOnARampedTrackChangesNotOneSample()
    {
        async Task<(ProjectSession, TrackModel)> Ramped()
        {
            var (s, t) = await DeckAsync(O, 1.0, 10, 20);
            Ramp(s, t, 8, 25, 1.06);
            return (s, t);
        }
        var (plain, _) = await Ramped();
        var expected = Render(plain, Rate * 30);

        var (s, t) = await Ramped();
        var map = s.EngineTempoOf(t);
        foreach (double at in new[] { 5.3, 15.2, 28.7 })                 // before the ramp, inside it, after it
            Assert.True(WarpMarkers.Add(t, at, s, out _).Changed, $"setup: no warp marker at {at}s");
        Assert.True(t.Warps.All(w => w.TrackSeconds == w.TempoSeconds), "a warp marker put down on a ramped track is already moved");
        Assert.True(TimelineEditor.SecondsMapOf(t) is { IsWarped: false } && s.EngineTempoOf(t) is { IsWarped: false } m && m.SameAs(map),
            "putting warp markers on a ramped track changed its tempo map — every section on it would be heard somewhere else");
        int differing = Differing(expected, Render(s, Rate * 30));
        Assert.True(differing == 0, $"{differing} samples changed after putting warp markers on a ramped track — putting one down must change nothing");
    }

    [Fact]
    public async Task AMarkerPutDownWhereTheTrackIsAlreadyWarpedMovesNothing()
    {
        var (s, t) = await StandardAsync();
        Drag(s, t, 1, Beat(t, 1) + 0.08);
        Drag(s, t, 2, Beat(t, 2) - 0.03);

        // Before the first marker, in each moved stretch, and after the last.
        foreach (double p in new[] { 6.0, 18.0, 30.0, 45.0 })
        {
            var when = t.Clips.Select(c => When(t, c, s)).ToArray();
            var old = t.Warps.ToList();
            var beats = Beats(t);
            double at = TimelineEditor.TimelineSecondsAt(t, p) + 0.4 * Frame;

            var r = WarpMarkers.Add(t, at, s, out int i);
            Assert.True(r.Changed, $"setup: no warp marker at {p}s of the song: {r.Message}");
            var sections = t.Clips.Select(c => When(t, c, s)).ToArray();
            for (int k = 0; k < when.Length; k++)
                Assert.True(Math.Abs(sections[k] - when[k]) <= 1e-9,
                    $"a warp marker put down on {p}s of the song moved a section from {when[k]:0.0000000}s to {sections[k]:0.0000000}s — clicking a beat to pin it pulled the song out of time");
            for (int k = 0; k < old.Count; k++)
            {
                int now = t.Warps.IndexOf(old[k]);
                Assert.True(now >= 0 && Math.Abs(Beat(t, now) - beats[k]) <= 1e-9, $"a warp marker put down on {p}s of the song moved another marker's beat");
            }
            Assert.True(Math.Abs(Beat(t, i) - at) <= Frame, $"the new warp marker is on a beat heard at {Beat(t, i):0.00000}s, not {at:0.00000}s where it was clicked");
        }
        s.ApplyAll();
        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, t, "markers put down on a warped track");
    }

    // ---------------------------------------------------------------- dragging one

    [Fact]
    public async Task DraggingAMarkerPutsItsBeatWhereItWasDropped_AndSpreadsTheAudioBetweenItsNeighbours()
    {
        var (s, t) = await StandardAsync();
        var clips = t.Clips.ToList();
        var was = clips.ToDictionary(c => c, c => When(t, c, s));
        double a = Beat(t, 0), b = Beat(t, 1), c = Beat(t, 2);

        var (_, result) = Drag(s, t, 1, b + 0.06);
        double drop = OnFrame(b + 0.06);
        double tb = (drop - O) * V;                                     // how far the tempo alone has carried the track there
        Assert.True(Math.Abs(t.Warps[1].TempoSeconds - tb) <= 1e-9, $"the marker's tempo is {t.Warps[1].TempoSeconds}, not {tb} — where the tempo has got at the drop point");
        var expected = new[] { (12.0, 12.0), (24.0, tb), (36.0, 36.0) };

        double beat = TimelineEditor.TimelineSecondsAt(t, 24);
        Assert.True(Math.Abs(beat - drop) <= Frame, $"the beat was dropped at {drop:0.00000}s and is heard at {beat:0.00000}s — it does not land on the beat it was dragged to");
        Assert.True(Beat(t, 0) == a, "dragging a warp marker moved the marker on its left — nothing before it should move");
        foreach (var x in clips.Where(x => x.TrackStartSeconds < 12))
            Assert.True(When(t, x, s) == was[x], $"dragging a warp marker moved a section before the marker on its left, from {was[x]}s to {When(t, x, s)}s");
        Assert.True(Math.Abs(Beat(t, 2) - c) <= 1e-9, "dragging a warp marker moved the marker on its right — the beat pinned there drifted");
        foreach (double p in new[] { 20.0, 30.0 })
        {
            double want = Heard(p, expected), got = When(t, AtTrackSeconds(t, p), s);
            Assert.True(Math.Abs(got - want) <= 1e-9,
                $"the section at {p}s of the song is heard at {got:0.0000000}s, not {want:0.0000000}s — the audio between two markers is not spread evenly");
        }
        Assert.True(Math.Abs(When(t, AtTrackSeconds(t, 40), s) - was[AtTrackSeconds(t, 40)]) <= 1e-9,
            "a section after the next marker moved — the marker on the right should hold everything after it");
        Assert.True(result.Changed && result.Message.Contains("0.060s later") && result.Message.Contains("slower") && result.Message.Contains("faster"),
            $"the status line does not say what the drag did: {result.Message}");
        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, t, "a dragged warp marker");
    }

    [Fact]
    public async Task DraggingTheLastMarkerCarriesEverythingAfterItAlongWithTheBeat()
    {
        var (s, t) = await StandardAsync();
        var at40 = AtTrackSeconds(t, 40);
        double was40 = When(t, at40, s), b = Beat(t, 1), c = Beat(t, 2);

        var (d, result) = Drag(s, t, 2, c - 0.04);
        double drop = OnFrame(c - 0.04);
        double tc = (drop - O) * V;

        Assert.True(Math.Abs(TimelineEditor.TimelineSecondsAt(t, 36) - drop) <= Frame, "the last marker's beat is not heard where it was dropped");
        // After the last marker the track plays at its tempo again, from where the marker's tempo is.
        double want = O + (tc + (40 - 36)) / V;
        Assert.True(Math.Abs(When(t, at40, s) - want) <= Frame,
            $"a section after the last marker is heard at {When(t, at40, s):0.00000}s, not {want:0.00000}s — it did not move along with the beat");
        double beatMoved = d.NowSeconds - d.StartSeconds, sectionMoved = When(t, at40, s) - was40;
        Assert.True(Math.Abs(beatMoved - sectionMoved) <= Frame,
            $"the beat moved {beatMoved * 1000:0.000} ms and the section after it {sectionMoved * 1000:0.000} ms — what comes after the last marker fell out of time with it");
        Assert.True(Beat(t, 1) == b, "dragging the last marker moved the marker before it");
        double between = When(t, AtTrackSeconds(t, 30), s), spread = Heard(30, (12, 12), (24, 24), (36, tc));
        Assert.True(Math.Abs(between - spread) <= 1e-9, $"the section between the last two markers is at {between:0.0000000}s, not {spread:0.0000000}s");
        Assert.True(result.Message.Contains("sooner") && result.Message.Contains("Everything after it on this track moves with it."),
            $"the status line does not say that everything after the last marker moves: {result.Message}");
        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, t, "the last warp marker dragged");
    }

    [Fact]
    public async Task DraggingTheFirstMarkerSpreadsTheAudioFromTheTracksStart()
    {
        var (s, t) = await StandardAsync();
        double a = Beat(t, 0), b = Beat(t, 1);
        var (_, result) = Drag(s, t, 0, a + 0.05);
        double drop = OnFrame(a + 0.05), ta = (drop - O) * V;
        Assert.True(Math.Abs(t.Warps[0].TempoSeconds - ta) <= 1e-9 && Math.Abs(TimelineEditor.TimelineSecondsAt(t, 12) - drop) <= Frame,
            "the first marker's beat is not heard where it was dropped");

        Assert.True(When(t, AtTrackSeconds(t, 0), s) == O, "dragging the first warp marker moved the start of the track — the whole song would slide");
        double want = Heard(10, (12, ta), (24, 24), (36, 36)), got = When(t, AtTrackSeconds(t, 10), s);
        Assert.True(Math.Abs(got - want) <= 1e-9, $"a section before the first marker is at {got:0.0000000}s, not {want:0.0000000}s — the audio from the track's start is not spread evenly");
        Assert.True(Math.Abs(Beat(t, 1) - b) <= 1e-9, "dragging the first marker moved the next one");
        Assert.True(result.Message.Contains("from the start of the track to it"), $"the status line does not say the stretch from the start was spread: {result.Message}");
    }

    [Fact]
    public async Task AMarkerCannotSpreadTheAudioBeyondHalfOrTwiceItsTempo()
    {
        var (s, t) = await StandardAsync();
        double b = Beat(t, 1), c = Beat(t, 2);
        bool InReach(int i) => WarpMarkers.SpreadBefore(t, i) is var x && x >= TempoMap.LowestWarp && x <= TempoMap.HighestWarp;

        var d = new WarpDrag(s, t, 1);
        d.DragTo(b + 20, SnapSettings.Off, null);
        Assert.True(d.Clamped && InReach(1) && InReach(2),
            $"dragged far later, the audio either side spreads {WarpMarkers.SpreadBefore(t, 1):0.000}× and {WarpMarkers.SpreadBefore(t, 2):0.000}× — more than the engine is allowed to stretch it");
        Assert.True(Math.Abs(WarpMarkers.SpreadBefore(t, 2) - TempoMap.HighestWarp) < 1e-6, "the drag stopped short of as far as the marker can go");
        d.DragTo(b - 20, SnapSettings.Off, null);
        Assert.True(d.Clamped && InReach(1) && InReach(2) && Math.Abs(WarpMarkers.SpreadBefore(t, 1) - TempoMap.HighestWarp) < 1e-6,
            "dragged far sooner, the audio before the marker is spread beyond twice its tempo, or the drag stopped short");
        d.DragTo(b + 0.5, SnapSettings.Off, null);
        Assert.True(!d.Clamped && Math.Abs(d.NowSeconds - OnFrame(b + 0.5)) <= Frame, "a drag within reach was held back");
        Assert.True(d.Finish().Changed, "a drag that moved the beat did not count as a change — Ctrl+Z would skip it");
        s.ApplyAll();
        Assert.True(s.VoiceFor(t)!.Tempo.IsWarped, "the engine dropped a warp the editor allowed — what is drawn is not what plays");

        // The last marker: only the stretch before it is limited.
        var last = new WarpDrag(s, t, 2);
        last.DragTo(c + 60, SnapSettings.Off, null);
        Assert.True(last.Clamped && InReach(2) && Math.Abs(WarpMarkers.SpreadBefore(t, 2) - TempoMap.LowestWarp) < 1e-6,
            "the last marker dragged far later spreads the audio before it below half its tempo, or stopped short");
        Assert.True(!last.BeyondEngine && last.Describe().Contains("twice as fast or half as fast"),
            "a drag stopped by the spread limit blamed the engine — the status bar would give the wrong reason");
        last.DragTo(c - 30, SnapSettings.Off, null);
        Assert.True(last.Clamped && InReach(2) && Math.Abs(WarpMarkers.SpreadBefore(t, 2) - TempoMap.HighestWarp) < 1e-6,
            "the last marker dragged far sooner spreads the audio before it beyond twice its tempo, or stopped short");
        last.Finish();
        s.ApplyAll();
        Assert.True(s.VoiceFor(t)!.Tempo.IsWarped, "the engine dropped a warp at the limit the editor allowed");
    }

    [Fact]
    public async Task AMarkerStopsWhereTheEngineCouldPlayTheAudioNoFaster()
    {
        // At 2.5× on the knob, the audio before the marker spread 1.26× plays at 3.2× — playable —
        // and spread 1.71× at 4.3×, beyond the engine's 4×, though within the warp's own 2×.
        var (s, t) = await DeckAsync(O, 2.5, 10, 20, 30);
        Pin(s, t, 12);
        Pin(s, t, 24);
        double b = Beat(t, 1);

        var d = new WarpDrag(s, t, 1);
        d.DragTo(b - 1.0, SnapSettings.Off, null);
        Assert.True(!d.Clamped && WarpMarkers.AnyMoved(t), "setup: a drag the engine can play was held back");
        var good = t.Warps[1];
        d.DragTo(b - 2.0, SnapSettings.Off, null);
        Assert.True(d.Clamped && t.Warps[1] == good,
            "a warp marker went where the engine cannot play the audio fast enough — the engine would drop the whole warp and the track would jump out of time");
        Assert.True(d.BeyondEngine && d.Describe().Contains("faster or slower there than Benchmix can"),
            "a drag stopped by the engine's speed limit blamed the spread either side — the status bar would give the wrong reason");
        Assert.True(d.Finish().Changed, "a drag that moved the beat before it was stopped did not count as a change — Ctrl+Z would skip it");
        s.ApplyAll();
        Assert.True(s.VoiceFor(t)!.Tempo is { IsWarped: true } m && m.MaxSpeed <= TempoMap.HighestSpeed, "the engine is not playing the warp the drag stopped at");
    }

    [Fact]
    public async Task DraggingAMarkerBackToWhereItStartedLeavesItExactlyUnmoved()
    {
        var (s, t) = await DeckAsync(O, 1.0, 10, 20, 30);
        Pin(s, t, 12);
        Pin(s, t, 24);
        var original = t.Warps[1];
        double b = Beat(t, 1);

        var d = new WarpDrag(s, t, 1);
        d.DragTo(b + 0.3, SnapSettings.Off, null);
        Assert.True(t.Warps[1] != original && TimelineEditor.SecondsMapOf(t) is not null, "setup: the drag should have warped the track");
        d.DragTo(b - 0.2, SnapSettings.Off, null);
        d.DragTo(b + 0.3 * Frame, SnapSettings.Off, null);              // within the frame it started on

        Assert.True(t.Warps[1] == original && original.TrackSeconds == original.TempoSeconds,
            "a warp marker dragged away and back is not exactly unmoved — the track stays warped by a hair, and goes through the stretcher for nothing");
        var r = d.Finish();
        Assert.True(!r.Changed && r.Message.Contains("left where it was"), $"a drag that ended where it began counted as a change, an undo step for nothing: {r.Message}");
        Assert.True(TimelineEditor.SecondsMapOf(t) is null, "the editor still treats the track as warped");
        s.ApplyAll();
        var v = s.VoiceFor(t)!;
        Assert.True(v.Tempo.IsConstant && v.IsUnprocessed, "an untouched track whose marker was dragged and put back goes through the stretcher — it is no longer bit-identical");
    }

    // ---------------------------------------------------------------- snapping

    [Fact]
    public async Task AMarkerSnapsToASectionEdgeOnAnotherTrack_AndNotWithSnappingOff()
    {
        var (s, t) = await StandardAsync();
        double b = Beat(t, 1);
        var other = await s.AddTrackAsync(_wav, b + 0.25);
        double edge = When(other, other.Clips[0], s);

        var d = new WarpDrag(s, t, 1);
        d.DragTo(b + 0.1, new SnapSettings(true, 20), null);         // half a second either side
        Assert.True(d.SnapLineSeconds == edge && d.SnappedTo == "a section edge",
            $"a beat dragged 0.15 s from another deck's section did not snap to it (guide at {d.SnapLineSeconds}, on {d.SnappedTo})");
        double beat = TimelineEditor.TimelineSecondsAt(t, 24);
        Assert.True(Math.Abs(beat - edge) <= Frame,
            $"the guide line says the beat is on the other deck's edge at {edge:0.00000}s, but it is heard at {beat:0.00000}s — {(beat - edge) * 1000:0.000} ms out");

        d.DragTo(b + 0.1, SnapSettings.Off, null);                     // Ctrl held
        Assert.True(d.SnapLineSeconds is null && d.SnappedTo is null && Math.Abs(TimelineEditor.TimelineSecondsAt(t, 24) - OnFrame(b + 0.1)) <= Frame,
            "with Ctrl held a warp marker still snapped — it could not be put just off an edge");
        Assert.True(d.Finish().Changed, "a drag that moved the beat did not count as a change — Ctrl+Z would skip it");
    }

    [Fact]
    public async Task AMarkerSnapsToThePlayhead()
    {
        var (s, t) = await StandardAsync();
        double b = Beat(t, 1), playhead = b + 0.2;

        var d = new WarpDrag(s, t, 1);
        d.DragTo(b + 0.1, new SnapSettings(true, 20), playhead);
        Assert.True(d.SnapLineSeconds == playhead && d.SnappedTo == "the playhead", $"a beat dragged 0.1 s from the playhead did not snap to it ({d.SnappedTo})");
        Assert.True(Math.Abs(TimelineEditor.TimelineSecondsAt(t, 24) - playhead) <= Frame, "snapped to the playhead, the beat is not heard there");
        d.DragTo(b + 0.1, SnapSettings.Off, playhead);
        Assert.True(d.SnapLineSeconds is null && Math.Abs(TimelineEditor.TimelineSecondsAt(t, 24) - OnFrame(b + 0.1)) <= Frame, "with Ctrl held a warp marker still snapped to the playhead");
    }

    [Fact]
    public async Task AMarkerIgnoresItsOwnTracksSections_ButNotItsOwnTracksRamps()
    {
        var (s, t) = await StandardAsync();
        double c = Beat(t, 2);
        double ownEdge = When(t, AtTrackSeconds(t, 40), s);
        var wide = new SnapSettings(true, 2);                            // five seconds either side

        var d = new WarpDrag(s, t, 2);
        d.DragTo(ownEdge - 0.1, wide, null);
        Assert.True(d.SnapLineSeconds is null && Math.Abs(TimelineEditor.TimelineSecondsAt(t, 36) - OnFrame(ownEdge - 0.1)) <= Frame,
            "a warp marker snapped to a section on its own track — that section moves as soon as the warp changes, so the beat would not stay on it");
        d.DragTo(c, wide, null);
        Assert.False(d.Finish().Changed, "setup: the marker should be back where it was");

        // A ramp sits on the timeline, so its end stays put whatever the warp does: it pulls.
        double rampEnd = c + 0.3;
        Ramp(s, t, 5, rampEnd, V);
        var e = new WarpDrag(s, t, 2);
        e.DragTo(c + 0.4, wide, null);
        Assert.True(e.SnapLineSeconds == rampEnd && Math.Abs(TimelineEditor.TimelineSecondsAt(t, 36) - rampEnd) <= Frame,
            $"a warp marker did not snap to the end of a tempo ramp on its own track (guide at {e.SnapLineSeconds})");
    }

    // ---------------------------------------------------------------- removing one

    [Fact]
    public async Task RemovingAMarkerPutsTheAudioAroundItBack()
    {
        var (s, t) = await StandardAsync();
        var at30 = AtTrackSeconds(t, 30);
        var at40 = AtTrackSeconds(t, 40);
        double was40 = When(t, at40, s), a = Beat(t, 0);
        Drag(s, t, 2, Beat(t, 2) + 0.05);
        Drag(s, t, 1, Beat(t, 1) + 0.1);
        double moved40 = When(t, at40, s), c = Beat(t, 2), tc = t.Warps[2].TempoSeconds;
        Assert.True(moved40 - was40 > 0.04, "setup: dragging the last marker later should have moved what comes after it");

        // A marker between two others: they stay, and the audio between them is spread evenly again.
        var r = WarpMarkers.Remove(t, 1, s);
        Assert.True(r.Changed && t.Warps.Count == 2, $"the warp marker was not removed: {r.Message}");
        Assert.True(Beat(t, 0) == a && Math.Abs(Beat(t, 1) - c) <= 1e-9, "removing a warp marker moved the markers either side of it — their beats would drift");
        Assert.True(Math.Abs(When(t, at40, s) - moved40) <= 1e-9, "removing a marker before the last moved what comes after the last");
        double want = Heard(30, (12, 12), (36, tc)), got = When(t, at30, s);
        Assert.True(Math.Abs(got - want) <= 1e-9, $"after removing a marker the section at 30 s of the song is at {got:0.0000000}s, not {want:0.0000000}s — the audio is not spread evenly between the markers either side");

        // The last one: everything after it goes back to its tempo.
        var last = WarpMarkers.Remove(t, 1, s);
        Assert.True(last.Changed && Math.Abs(When(t, at40, s) - was40) <= Frame,
            $"removing the last warp marker left the section after it at {When(t, at40, s):0.00000}s, not back at {was40:0.00000}s");
        Assert.True(last.Message.Contains("0.050s sooner"), $"the status line does not say how much later audio moved: {last.Message}");
        Assert.False(WarpMarkers.Remove(t, 5, s).Changed, "a marker that is not there was removed");

        Assert.True(WarpMarkers.Remove(t, 0, s).Changed && t.Warps.Count == 0 && TimelineEditor.SecondsMapOf(t) is null,
            "with every warp marker removed the track is still treated as warped");
        s.ApplyAll();
        Assert.True(s.VoiceFor(t)!.Tempo.IsConstant, "with every warp marker removed the engine still plays a warp");
    }

    // ---------------------------------------------------------------- the markers ride with the audio

    [Fact]
    public async Task TurningTempoCarriesTheMarkersWithTheirBeats()
    {
        var (s, t) = await StandardAsync();
        Drag(s, t, 1, Beat(t, 1) + 0.06);
        Drag(s, t, 2, Beat(t, 2) - 0.03);
        var warps = t.Warps.ToList();
        var beats = Beats(t);
        var when = t.Clips.Select(c => When(t, c, s)).ToArray();

        t.Speed = 1.05;
        s.ApplyAll();
        Assert.True(t.Warps.SequenceEqual(warps), "turning TEMPO changed the warp markers — they are on the audio and should not need to");
        for (int i = 0; i < beats.Length; i++)
        {
            double want = O + (beats[i] - O) * V / 1.05;
            Assert.True(Math.Abs(Beat(t, i) - want) <= 1e-9, $"after turning TEMPO a marker is at {Beat(t, i):0.0000000}s, not on its beat at {want:0.0000000}s");
        }
        var sections = t.Clips.Select(c => When(t, c, s)).ToArray();
        for (int k = 0; k < when.Length; k++)
            Assert.True(Math.Abs(sections[k] - (O + (when[k] - O) * V / 1.05)) <= 1e-9, "turning TEMPO on a warped track did not rescale the arrangement with it");
        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, t, "TEMPO turned on a warped track");

        // Match BPM is the same knob, turned for him.
        t.Bpm = 100;
        var (speed, _) = s.MatchTempo(t, 92);
        Assert.True(speed != 1.05 && t.Warps.SequenceEqual(warps), "Match BPM dropped or changed the warp markers");
        Assert.True(Math.Abs(Beat(t, 1) - (O + (beats[1] - O) * V / speed)) <= 1e-9, "after Match BPM a warp marker is no longer on its beat");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PastingBeforeAWarpedTracksStartMovesNothingThatWasThere(bool ramped)
    {
        var (s, t) = await DeckAsync(30, V, 3, 10, 20, 30, 40);
        if (ramped) Ramp(s, t, 25, 45, 1.08);                           // begins before the track does
        Warp(s, t, (12, -0.05), (24, 0.07));
        Assert.True(WarpMarkers.SpreadBefore(t, 0) != 1, "setup: the stretch from the track's start should be spread, so the origin's move is not plain arithmetic");
        var old = t.Clips.ToList();
        var frames = old.ToDictionary(c => c, c => Start(c, s));
        var when = old.ToDictionary(c => c, c => When(t, c, s));
        var beats = Beats(t);
        var audio = t.Warps.Select(w => s.SecondsToFrames(w.TrackSeconds)).ToArray();
        var spreads = Enumerable.Range(0, t.Warps.Count).Select(i => WarpMarkers.SpreadBefore(t, i)).ToArray();
        var first = AtTrackSeconds(t, 0);

        var r = TimelineEditor.Paste(t, TimelineEditor.Copy(first), 10, s);
        Assert.True(r.Changed && t.TimelineOffsetSeconds < 30, "setup: a paste before the track's start should have moved its origin back");
        long shift = Start(first, s) - frames[first];
        foreach (var c in old)
        {
            Assert.True(Start(c, s) - frames[c] == shift, "the sections did not all move by the same whole frames — a join between two of them would open");
            Assert.True(Math.Abs(When(t, c, s) - when[c]) <= Frame,
                $"pasting before a warped track's start moved a section from {when[c]:0.00000}s to {When(t, c, s):0.00000}s — out of line with every other deck");
        }
        for (int i = 0; i < t.Warps.Count; i++)
        {
            var w = t.Warps[i];
            Assert.True(s.SecondsToFrames(w.TrackSeconds) - audio[i] == shift && w.TrackSeconds == TimelineEditor.SecondsOf(s.SecondsToFrames(w.TrackSeconds), s),
                "a warp marker did not move with the audio by the same whole frames — it is no longer on its beat");
            Assert.True(Math.Abs(Beat(t, i) - beats[i]) <= Frame, $"pasting before the track's start moved a warp marker's beat from {beats[i]:0.00000}s to {Beat(t, i):0.00000}s");
            Assert.True(Math.Abs(WarpMarkers.SpreadBefore(t, i) - spreads[i]) <= 1e-9, "moving the track's origin changed how the audio between markers is spread");
        }
        Assert.True(Math.Abs(When(t, r.Placed[0], s) - 10) <= Frame, $"the pasted section is at {When(t, r.Placed[0], s):0.00000}s, not 10 s where it was put");
        s.ApplyAll();
        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, t, "a paste before a warped track's start");
    }

    [Fact]
    public async Task AnEmptyTrackTakesTheWarpAndTheOriginAndGivesThemBack()
    {
        var (s, t) = await StandardAsync();
        Drag(s, t, 1, Beat(t, 1) + 0.06);
        var mover = AtTrackSeconds(t, 20);                              // in the stretch the drag spread
        double at = When(t, mover, s);
        long frame = Start(mover, s);
        var empty = s.AddEmptyTrack();
        double emptyOrigin = empty.TimelineOffsetSeconds;
        Assert.True(emptyOrigin != t.TimelineOffsetSeconds, "setup: the two tracks should start at different places");

        var move = new SectionMove(s, t, mover);
        move.DragTo(empty, at, SnapSettings.Off);
        Assert.True(empty.Warps.SequenceEqual(t.Warps) && empty.TimelineOffsetSeconds == t.TimelineOffsetSeconds && empty.Speed == V,
            "the empty track did not take the warp markers, and the origin they are counted from — the section would not play as it was warped");
        Assert.True(Start(mover, s) == frame, "moved straight down onto an empty track, the section starts on a different frame of the song");
        Assert.True(Math.Abs(When(empty, mover, s) - at) <= Frame, $"moved straight down, the section is heard at {When(empty, mover, s):0.00000}s, not {at:0.00000}s");
        move.DragTo(t, at, SnapSettings.Off);
        move.Commit();
        Assert.True(empty.Warps.Count == 0 && empty.TimelineOffsetSeconds == emptyOrigin && empty.Speed == 1.0,
            "passing over the empty track left it with warp markers, or an origin, it never had");
        Assert.True(Start(mover, s) == frame, "dragged back, the section is not on its own frame");

        var again = new SectionMove(s, t, mover);
        again.DragTo(empty, at, SnapSettings.Off);
        var r = again.Commit();
        s.ApplyAll();
        Assert.True(r.Message.Contains("warp markers"), $"the status line does not say the empty track took the warp markers: {r.Message}");
        Assert.True(Math.Abs(When(empty, mover, s) - at) <= Frame, "dropped on an empty track, the section no longer lands at the same moment");
        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, empty, "an empty track that took a warp");
    }

    [Fact]
    public async Task PastingFromAWarpedTrackOntoAnEmptyOneTakesItsWarp()
    {
        var (s, t) = await StandardAsync();
        Drag(s, t, 1, Beat(t, 1) + 0.06);
        var empty = s.AddEmptyTrack();
        var r = TimelineEditor.Paste(empty, TimelineEditor.Copy(AtTrackSeconds(t, 20)), 47, s, copiedFrom: t);
        Assert.True(r.Changed && empty.Warps.SequenceEqual(t.Warps) && empty.TimelineOffsetSeconds == t.TimelineOffsetSeconds,
            "pasted onto an empty track, a section from a warped track left the warp markers behind");
        Assert.True(r.Message.Contains("warp markers"), $"the status line does not say the track took the warp markers: {r.Message}");
        Assert.True(Math.Abs(When(empty, r.Placed[0], s) - 47) <= Frame, $"the pasted section is at {When(empty, r.Placed[0], s):0.00000}s, not 47 s");
    }

    [Fact]
    public async Task StemTracksTakeTheirSongsWarpMarkers()
    {
        var (s, t) = await StandardAsync();
        Drag(s, t, 1, Beat(t, 1) + 0.06);
        var stem = s.AddSeparatedTrack(t, _wav, $"{t.Name} — vocals", "#ffffff");
        Assert.True(stem.Warps.SequenceEqual(t.Warps) && !ReferenceEquals(stem.Warps, t.Warps) && stem.TimelineOffsetSeconds == t.TimelineOffsetSeconds,
            "a vocals stem did not take the song's warp markers — it would drift off the instrumental's beats");
        Assert.True(s.EngineTempoOf(stem) is { IsWarped: true } m && m.SameAs(s.EngineTempoOf(t)), "the stem plays a different warp from its song");
    }

    // ---------------------------------------------------------------- the project file

    [Fact]
    public async Task AWarpedProjectIsSavedAsSchemaFourAndReopensSoundingTheSame()
    {
        var (s, t) = await DeckAsync(O, V, 10, 20);
        t.Mode = StretchMode.Linked;
        Warp(s, t, (6, -0.02), (14, 0.05));
        var before = Render(s, Rate * 25);

        string path = Path.Combine(_dir, "warped.benchmix");
        ProjectStore.Save(s.Project, path);
        string json = File.ReadAllText(path);
        var version = Regex.Match(json, "\"schemaVersion\":\\s*(\\d+)");
        Assert.True(version.Success && int.Parse(version.Groups[1].Value) >= 4 && json.Contains("\"warps\"") && json.Contains("\"trackSeconds\"") && json.Contains("\"tempoSeconds\""),
            "the saved project is not schema 4 or later with its warp markers — an older Benchmix would open it, drop them without a word and save over them");

        var loaded = ProjectStore.Load(path).Project;
        Assert.True(loaded.Tracks[0].Warps.SequenceEqual(t.Warps), "the warp markers did not come back exactly as they were saved");
        var again = ProjectSession.CreateOffline(Rate);
        await again.OpenAsync(loaded);
        int differing = Differing(before, Render(again, Rate * 25));
        Assert.True(differing == 0, $"{differing} samples differ after saving and reopening a project with warp markers");
    }

    [Fact]
    public async Task WarpMarkersThatCannotBePlayedAreDroppedOnLoad()
    {
        var (s, t) = await DeckAsync(0, 1.0);
        t.Warps = new List<WarpMarker>
        {
            new(30, 24.0),                   // spreads the audio 2.4× from the one before it
            new(10.05, 10.05),               // closer than 0.1 s to the one at 10: the first is kept
            new(20.0000001, 19.9),           // kept, and put on a whole frame
            new(0.05, 0.05),                 // before 0.1 s
            new(25, 19.5),                   // its tempo goes back
            new(10, 10),                     // kept
            new(40, 60),                     // spreads the audio below half
            new(44.00000001, 44.00000001),   // kept, unmoved, and still unmoved on its frame
        };
        string path = Path.Combine(_dir, "bad.benchmix");
        ProjectStore.Save(s.Project, path);
        var warps = ProjectStore.Load(path).Project.Tracks[0].Warps;

        Assert.True(warps.Count == 3 && warps[0] == new WarpMarker(10, 10) && warps[1] == new WarpMarker(20, 19.9) && warps[2] == new WarpMarker(44, 44),
            $"loading kept [{string.Join(", ", warps)}] — a damaged file would warp a track in a way nobody set up, or crash");
        Assert.True(warps[2].TrackSeconds == warps[2].TempoSeconds, "an unmoved marker came back moved — loading would put the track through the stretcher");
    }

    [Fact]
    public void MarkersThatAreNotNumbersAreDropped()
    {
        var t = new TrackModel
        {
            Warps = new List<WarpMarker>
            {
                null!, new(double.NaN, 5), new(5, double.NaN), new(double.PositiveInfinity, 8), new(8, double.NegativeInfinity), new(9, 9),
            },
        };
        WarpMarkers.Normalise(t, Rate);
        Assert.True(t.Warps.Count == 1 && t.Warps[0] == new WarpMarker(9, 9), $"Normalise kept [{string.Join(", ", t.Warps)}] — a damaged file would crash the app or warp a track");
    }

    [Fact]
    public async Task CloneAndUndoAreDeepForWarpMarkers()
    {
        var (s, t) = await DeckAsync(0, 1.0);
        t.Warps.Add(new WarpMarker(10, 10));
        var copy = t.Clone();
        copy.Warps.Add(new WarpMarker(20, 19.9));
        copy.Warps[0] = new WarpMarker(10, 9.9);
        Assert.True(t.Warps.Count == 1 && t.Warps[0] == new WarpMarker(10, 10), "a copied track shares the live list of warp markers — editing one would edit the other");

        var undo = new UndoStack();
        undo.Push(s.Project, "warp");
        Assert.True(WarpMarkers.Add(t, 20, s, out _).Changed, "setup: no warp marker at 20 s");
        var back = undo.Undo(s.Project);
        Assert.True(back is not null && back.Tracks[0].Warps.Count == 1 && t.Warps.Count == 2,
            "the undo snapshot shares the live list of warp markers — Ctrl+Z would not take a marker back, or would take the live one with it");
    }

    // ---------------------------------------------------------------- the engine plays what the lane draws

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheEngineStartsEverySectionWhereTheLaneDrawsItOnAWarpedTrack(bool ramped)
    {
        var (s, t) = await DeckAsync(O, V, 5, 10, 13.7, 20, 27.3, 31, 40, 47);
        if (ramped) Ramp(s, t, 15, 35, 1.08);
        Warp(s, t, (12, -0.04), (24, 0.07), (36, 0.03));
        Assert.True(s.VoiceFor(t)!.Tempo.IsWarped, "setup: the engine should be playing the warp");
        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, t, ramped ? "a warped track with a tempo ramp" : "a warped track");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWarpedTrackPlaysEachSectionWhereTheLaneDrawsIt(bool ramped)
    {
        // One section with a gap either side, a moved marker inside it, so where it starts and stops
        // can be read off the audio.
        var (s, t) = await DeckAsync(O, V, 18, 20, 30, 33);
        t.Clips.Remove(AtTrackSeconds(t, 18));
        t.Clips.Remove(AtTrackSeconds(t, 30));
        var cut = AtTrackSeconds(t, 20);
        t.Mode = StretchMode.Linked;
        if (ramped) Ramp(s, t, 8, 28, 1.07);
        Warp(s, t, (15, -0.03), (25, 0.08), (35, -0.02));

        long wantStart = (long)Math.Round(When(t, cut, s) * Rate);
        long wantEnd = (long)Math.Round(TimelineEditor.TimelineSecondsOfFrame(t, End(cut, s), s) * Rate);
        var buf = Render(s, Rate * 40);
        long first = -1, last = -1;
        for (long f = wantStart - Rate / 2; f < wantEnd + Rate / 2; f++)
            if (buf[f * 2] != 0) { if (first < 0) first = f; last = f; }

        TempoRampEditingTests.AssertEnginePlacesSectionsWhereCoreDoes(s, t, "a section across a moved warp marker");
        Assert.True(Math.Abs(first - wantStart) <= 2 && Math.Abs(last + 1 - wantEnd) <= 2,
            $"on a warped track the section plays {first}..{last + 1} but the lane draws it at {wantStart}..{wantEnd} — it would be heard out of time with where it was lined up");
    }

    // ---------------------------------------------------------------- drags that put the picture back

    [Fact]
    public async Task AGroupDraggedBeforeAWarpedTracksStartAndBackLeavesItsMarkersExactlyAsTheyWere()
    {
        var (s, t) = await DeckAsync(30, V, 10, 20, 25);
        Warp(s, t, (5, -0.03), (15, 0.05));
        var stays = AtTrackSeconds(t, 0);
        var m1 = AtTrackSeconds(t, 10);
        var m2 = AtTrackSeconds(t, 20);
        var warps = t.Warps.ToList();
        double origin = t.TimelineOffsetSeconds, staysAt = When(t, stays, s), m1At = When(t, m1, s);
        var beats = Beats(t);

        var move = new GroupMove(s, new[] { (t, m1), (t, m2) }, m1);
        move.DragTo(t, 12, SnapSettings.Off);
        Assert.True(t.TimelineOffsetSeconds < origin && !t.Warps.SequenceEqual(warps), "setup: the drag before the track's start should have moved its origin, and the markers with it");
        Assert.True(Math.Abs(When(t, stays, s) - staysAt) <= Frame && Math.Abs(When(t, m1, s) - 12) <= Frame,
            "dragged before a warped track's start, the group or a section left behind is not where it should be");
        for (int i = 0; i < beats.Length; i++)
            Assert.True(Math.Abs(Beat(t, i) - beats[i]) <= Frame, "while a group is held before the track's start, a warp marker is off its beat");

        move.DragTo(t, m1At + 0.5, SnapSettings.Off);
        move.Commit();
        Assert.True(t.Warps.SequenceEqual(warps) && t.TimelineOffsetSeconds == origin,
            "a group drag that went before the track's start and came back left the warp markers shifted — every beat they pin would be heard somewhere else");
    }

    [Fact]
    public async Task ALoopDraggedLeftPastAWarpedTracksStartAndBackLeavesItsMarkersExactlyAsTheyWere()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, 30);
        var section = t.Clips[0];
        section.SourceStartSeconds = 10;
        section.SourceEndSeconds = 12;
        section.TrackStartSeconds = 10;
        t.Speed = V;
        s.ApplyAll();
        Warp(s, t, (4, -0.03), (16, 0.05));
        var warps = t.Warps.ToList();
        double origin = t.TimelineOffsetSeconds, at = TimelineEditor.TimelineSecondsAt(t, 10);
        var beats = Beats(t);

        var loop = new LoopDrag(s, t, section, left: true);
        loop.DragTo(24.5, wholeRepeats: true);                          // well before the track began
        Assert.True(t.TimelineOffsetSeconds < origin && !t.Warps.SequenceEqual(warps), "setup: looping left past the track's start should have moved its origin, and the markers with it");
        Assert.True(Math.Abs(TimelineEditor.TimelineSecondsAt(t, loop.Base.TrackStartSeconds) - at) <= Frame, "looping left past a warped track's start moved the section being looped");
        for (int i = 0; i < beats.Length; i++)
            Assert.True(Math.Abs(Beat(t, i) - beats[i]) <= Frame, "looping left past the track's start moved a warp marker off its beat");

        loop.DragTo(at, wholeRepeats: true);
        Assert.True(t.Warps.SequenceEqual(warps) && t.TimelineOffsetSeconds == origin,
            "a loop dragged past the track's start and back left the warp markers shifted — every beat they pin would be heard somewhere else");
    }

    // ---------------------------------------------------------------- a tempo ramp on a warped track

    [Fact]
    public async Task ARampLeftAtTheTempoItWasFilledInWithMovesNothingOnAWarpedTrack()
    {
        var (s, t) = await StandardAsync();
        t.Bpm = 100;
        Drag(s, t, 1, Beat(t, 1) + 0.06);
        double start = TimelineEditor.TimelineSecondsAt(t, 16), end = TimelineEditor.TimelineSecondsAt(t, 22);
        double inside = TimelineEditor.TimelineSecondsAt(t, 30);         // between the moved marker and the next
        Assert.True(TimelineEditor.SpeedAt(t, start) != V, "setup: the audio there should be playing faster or slower than its tempo");

        Assert.True(TempoRamps.BpmAt(t, start) == 100 * V && TempoRamps.BpmAt(t, inside) == 100 * V,
            $"inside a warped stretch the tempo box shows {TempoRamps.BpmAt(t, start):0.000} BPM, not the {100 * V:0.0} the track is set to — "
          + "a ramp left at what it shows would change the tempo");
        double toSpeed = TimelineEditor.TempoSpeedAt(t, start);
        Assert.True(toSpeed == V, $"the tempo a new ramp is filled in with is {toSpeed}, not the knob's {V}");

        var v = s.VoiceFor(t)!;
        var engine = t.Clips.Select(c => v.TimelineOffsetFrames + v.Tempo.FirstRelAt(Start(c, s))).ToArray();
        var when = t.Clips.Select(c => When(t, c, s)).ToArray();
        var beats = Beats(t);

        var r = TempoRamps.Add(t, start, end, toSpeed, s);
        s.ApplyAll();
        Assert.True(r.Changed && !v.Tempo.Unwarped.IsConstant, $"setup: the ramp should be in the engine's map: {r.Message}");
        Assert.False(r.Message.Contains("now plays"), $"the status line says later audio moved for a ramp that changes no tempo: {r.Message}");
        var after = t.Clips.Select(c => v.TimelineOffsetFrames + v.Tempo.FirstRelAt(Start(c, s))).ToArray();
        Assert.True(after.SequenceEqual(engine),
            "a tempo ramp left at the tempo it was filled in with moved a section on a warped track — clicking a ramp in and out again would pull the song out of time");
        var sections = t.Clips.Select(c => When(t, c, s)).ToArray();
        for (int k = 0; k < when.Length; k++)
            Assert.True(Math.Abs(sections[k] - when[k]) <= 1e-9, $"the lane moved a section from {when[k]}s to {sections[k]}s for a ramp that changes no tempo");
        for (int i = 0; i < beats.Length; i++)
            Assert.True(Math.Abs(Beat(t, i) - beats[i]) <= 1e-9, "a ramp that changes no tempo moved a warp marker's beat");
        Assert.True(TempoRamps.BpmAt(t, (start + end) / 2) == 100 * V, "inside the new ramp the tempo is not the one it was filled in with");
    }

    // ---------------------------------------------------------------- from the review, 2026-10-18

    [Theory]
    [InlineData(true)]      // both stretches either side at twice their tempo
    [InlineData(false)]     // both at half
    public async Task AMarkerBoxedInAtTheLimitOnBothSidesCanStillBeGrabbed(bool faster)
    {
        foreach (double origin in new[] { 0.0, 2, 3.7, 5, 10 })
            foreach (double speed in new[] { 1.0, 0.95, 1.07 })
            {
                var (s, t) = await DeckAsync(origin, speed, 10, 20, 30, 40);
                foreach (double p in new[] { 2.3, 7.3, 19.1 }) Pin(s, t, p);
                // Each dragged as far as it goes, the same way, until the middle one has no room either side.
                for (int round = 0; round < 3; round++)
                    foreach (int i in new[] { 1, 2 })
                        Drag(s, t, i, faster ? Beat(t, i) - 60 : Beat(t, i) + 600);
                double limit = faster ? TempoMap.HighestWarp : TempoMap.LowestWarp;
                Assert.True(Math.Abs(WarpMarkers.SpreadBefore(t, 1) - limit) < 1e-6 && Math.Abs(WarpMarkers.SpreadBefore(t, 2) - limit) < 1e-6,
                    $"setup at {origin}s, {speed}: the middle marker should be at the limit on both sides");

                var held = t.Warps[1];
                var d = new WarpDrag(s, t, 1);
                var thrown = Record.Exception(() => d.DragTo(Beat(t, 1) + (faster ? -0.5 : 0.5), SnapSettings.Off, null));
                Assert.True(thrown is null, $"at {origin}s, {speed}: grabbing a marker with no room either side threw ({thrown?.Message}) — the app would show an error on every pointer move");
                Assert.True(t.Warps[1] == held && d.Clamped && !d.Finish().Changed,
                    $"at {origin}s, {speed}: a marker with no room either side moved, or did not say it was as far as it goes");
            }
    }

    [Fact]
    public async Task AMarkerPutBetweenTwoAtTheLimitCanStillBeGrabbed()
    {
        var (s, t) = await DeckAsync(O, V, 10, 20, 30, 40);
        Pin(s, t, 10);
        Pin(s, t, 20);
        Drag(s, t, 1, Beat(t, 1) - 30);                                  // as far as it goes: twice the tempo from 10 s to 20 s
        Assert.True(Math.Abs(WarpMarkers.SpreadBefore(t, 1) - TempoMap.HighestWarp) < 1e-6, "setup: the stretch should be at twice its tempo");
        var r = WarpMarkers.Add(t, TimelineEditor.TimelineSecondsAt(t, 12.5), s, out int m);
        Assert.True(r.Changed, $"setup: {r.Message}");
        var held = t.Warps[m];
        var d = new WarpDrag(s, t, m);
        var thrown = Record.Exception(() => d.DragTo(Beat(t, m) + 0.3, SnapSettings.Off, null));
        Assert.True(thrown is null && t.Warps[m] == held,
            $"refining a stretch already at the limit with a marker in between threw or moved it ({thrown?.Message}) — the app would show an error on every pointer move");
    }

    [Fact]
    public async Task AMarkerCannotGoOnATrackWithNothingOnIt()
    {
        var (s, t) = await DeckAsync(O, V, 10);
        var empty = s.AddEmptyTrack();
        var r = WarpMarkers.Add(empty, 5, s, out int i);
        Assert.True(!r.Changed && i < 0 && empty.Warps.Count == 0,
            "a warp marker went on an empty track — the next song put there would be warped by a marker nobody set on it, or lose it without a word");
        foreach (var c in t.Clips.ToList()) TimelineEditor.Delete(t, c);
        Assert.True(!WarpMarkers.Add(t, O + 5, s, out _).Changed && t.Warps.Count == 0, "a warp marker went on a track whose sections were all deleted");
    }

    [Fact]
    public async Task RemovingAMiddleMarkerSaysNothingMovedAfterTheNextOne()
    {
        var (s, t) = await StandardAsync();                             // markers on 12, 24 and 36 s
        Drag(s, t, 2, Beat(t, 2) + 0.05);
        Drag(s, t, 1, Beat(t, 1) + 0.1);
        var after = AtTrackSeconds(t, 40);                              // after the marker on 36 s
        double at = When(t, after, s);
        var r = WarpMarkers.Remove(t, 1, s);
        Assert.True(r.Changed && !r.Message.Contains("Anything later"),
            $"removing a middle marker said later audio moved, when everything after the next marker stays where it was: {r.Message}");
        Assert.True(Math.Abs(When(t, after, s) - at) <= 1e-9, "removing a middle marker moved audio after the next marker");
    }

    [Fact]
    public async Task DraggingAMarkerBackToWhereItStartedOffTheSongsOwnTempoLeavesItExactlyUnmoved()
    {
        // At 0.95 the tempo of the frame it started on, worked out afresh, is a hair off the marker's
        // own: only coming back to the very frame keeps it exactly unmoved.
        var (s, t) = await StandardAsync();
        var original = t.Warps[1];
        var d = new WarpDrag(s, t, 1);
        d.DragTo(d.StartSeconds + 0.3, SnapSettings.Off, null);
        Assert.True(t.Warps[1] != original, "setup: the drag did not move the marker");
        d.DragTo(d.StartSeconds, SnapSettings.Off, null);
        Assert.True(t.Warps[1] == original && !d.Finish().Changed,
            "a marker dragged away and back is not exactly unmoved at 0.95 — the track would stay warped by a hair and go through the stretcher");
        s.ApplyAll();
        Assert.True(TimelineEditor.SecondsMapOf(t) is null && s.EngineTempoOf(t).IsConstant,
            "a track whose only moved marker was put back still plays through a warp");
    }

    [Fact]
    public async Task ASectionsEndSnapsToAnotherTracksEdgeThroughTheWarp()
    {
        var (s, t) = await DeckAsync(O, V, 13, 23, 40);
        foreach (double p in new[] { 12.0, 24, 36 }) Pin(s, t, p);
        Drag(s, t, 1, Beat(t, 1) + 0.6);                                // the stretch from 12 s to 24 s now plays slower
        var markers = t.Warps.Select(w => (w.TrackSeconds, w.TempoSeconds)).ToArray();
        double TrackAt(double x)                                        // the inverse of Heard, by bisection
        {
            double lo = 0, hi = 60;
            for (int k = 0; k < 200; k++) { double mid = (lo + hi) / 2; if (Heard(mid, markers) < x) lo = mid; else hi = mid; }
            return lo;
        }

        var section = AtTrackSeconds(t, 13);                            // 13 s to 23 s of the song, inside the slowed stretch
        double proposed = When(t, section, s) + 0.5;
        double endThere = Heard(TrackAt(proposed) + 10, markers);
        double edge = endThere + 0.08;                                  // within 10 px at 50 px/s; the start is clear of every edge
        await s.AddTrackAsync(_wav, edge);

        var move = new SectionMove(s, t, section);
        move.DragTo(t, proposed, new SnapSettings(true, 50));
        double end = Heard(TimelineEditor.SecondsOf(End(section, s), s), markers);
        Assert.True(move.SnapLineSeconds == edge && move.SnappedTo == "a section" && Math.Abs(end - edge) <= 2 * Frame,
            $"on a warped track a section's end snapped to {move.SnapLineSeconds}, and ends at {end:0.000000}s, not on the edge at {edge:0.000000}s — its end was worked out at one speed, not through the warp");
    }

    [Fact]
    public async Task AWarpAtTheLimitSavedAtOneRateAndOpenedAtAnotherPlaysWhereItIsDrawn()
    {
        int disagree = 0, cases = 0;
        foreach (double first in new[] { 4.7000123, 3.3333, 5.123457, 6.66661 })
            foreach (double gap in new[] { 5.3, 7.77 })
                foreach (bool late in new[] { true, false })
                {
                    var s = ProjectSession.CreateOffline(44100);
                    var t = await s.AddTrackAsync(_wav, O);
                    t.Speed = 1.0;
                    Assert.True(WarpMarkers.Add(t, TimelineEditor.TimelineSecondsAt(t, first), s, out _).Changed, "setup");
                    Assert.True(WarpMarkers.Add(t, TimelineEditor.TimelineSecondsAt(t, first + gap), s, out _).Changed, "setup");
                    s.ApplyAll();
                    Drag(s, t, 1, late ? Beat(t, 1) + 300 : Beat(t, 1) - 60);  // as far as it goes: half or twice the tempo
                    string path = Path.Combine(_dir, $"rate-{cases++}.benchmix");
                    ProjectStore.Save(s.Project, path);

                    var again = ProjectSession.CreateOffline(48000);
                    await again.OpenAsync(ProjectStore.Load(path).Project);
                    var u = again.Project.Tracks[0];
                    bool drawn = TimelineEditor.SecondsMapOf(u)?.IsWarped == true;
                    bool played = again.EngineTempoOf(u).IsWarped;
                    double lane = TimelineEditor.TimelineSecondsAt(u, 20) * 48000;
                    double engine = again.SecondsToFrames(u.TimelineOffsetSeconds) + again.EngineTempoOf(u).RelAt(20 * 48000.0);
                    if (drawn != played || Math.Abs(lane - engine) > 2) disagree++;
                }
        Assert.True(disagree == 0,
            $"{disagree} of {cases} projects with a marker at the limit, saved at 44.1 kHz and opened at 48 kHz, play somewhere other than where the lane draws them — the engine dropped the warp the lane kept");
    }
}
