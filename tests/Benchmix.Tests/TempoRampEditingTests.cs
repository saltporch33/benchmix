using System.Text.RegularExpressions;
using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Editing a track with tempo ramps (Daniel, 2026-10-11): sections stay in the song's own frames, a
/// ramp only changes where on the timeline they land, and every placement is still whole frames.
/// Expected timeline positions come from the integral written out in <see cref="Timeline"/>.
/// </summary>
public class TempoRampEditingTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;

    public TempoRampEditingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-ramps", Guid.NewGuid().ToString("N"));
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
    /// Where track second p lands on the timeline, for a track whose origin is at 0, playing at v0
    /// until a, ramping in a straight line to v1 at b (timeline seconds), and holding v1 after.
    /// </summary>
    static double Timeline(double p, double v0, double a, double b, double v1)
    {
        double atA = v0 * a, atB = atA + (v0 + v1) / 2 * (b - a);
        if (p < atA) return p / v0;
        if (p >= atB) return b + (p - atB) / v1;
        double k = (v1 - v0) / (b - a), dp = p - atA;              // ½k·x² + v0·x = dp
        return a + (-v0 + Math.Sqrt(v0 * v0 + 2 * k * dp)) / k;
    }

    static long Start(ClipModel c, ProjectSession s) => TimelineEditor.StartFrameOf(c, s);
    static long End(ClipModel c, ProjectSession s) => TimelineEditor.EndFrameOf(c, s);
    static double When(TrackModel t, ClipModel c, ProjectSession s) => TimelineEditor.TimelineSecondsOfFrame(t, Start(c, s), s);
    static ClipModel AtTrackSeconds(TrackModel t, double trackSeconds) => t.Clips.Single(c => Math.Abs(c.TrackStartSeconds - trackSeconds) < 1e-6);

    async Task<(ProjectSession S, TrackModel T)> CutAsync(double offset, params double[] at)
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav, offset);
        foreach (var x in at) Assert.True(TimelineEditor.Split(t, x, s).Changed, $"could not split at {x}s");
        return (s, t);
    }

    static void Ramp(ProjectSession s, TrackModel t, double from, double to, double toSpeed)
    {
        t.Ramps.Add(new TempoRamp(from, to, toSpeed));
        s.ApplyAll();
    }

    /// <summary>
    /// The engine starts every section of <paramref name="t"/> on the frame the lane draws it at.
    /// Two separate constructions agree: the engine's tempo map in frames, and the editor's in
    /// seconds — so a ramp measured from the wrong place in either is caught without a render.
    /// </summary>
    internal static void AssertEnginePlacesSectionsWhereCoreDoes(ProjectSession s, TrackModel t, string where)
    {
        var v = s.VoiceFor(t)!;
        foreach (var c in t.Clips)
        {
            long start = TimelineEditor.StartFrameOf(c, s);
            var vc = Enumerable.Range(0, v.Timeline.Count).Select(k => v.Timeline[k]).SingleOrDefault(x => x.TrackStartFrame == start);
            if (vc is null) continue;                                        // a section with no audio is not in the engine
            long engine = v.TimelineOffsetFrames + v.Tempo.FirstRelAt(start);
            long core = (long)Math.Round(TimelineEditor.TimelineSecondsOfFrame(t, start, s) * s.Project.SampleRate);
            Assert.True(Math.Abs(engine - core) <= 1,
                $"{where}: a section the lane draws at frame {core} plays from frame {engine} — what he sees and what he hears are {engine - core} frames apart");
        }
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

    [Fact]
    public async Task ARampMovesNothingBeforeItAndLaterSectionsByWhatTheTempoSays()
    {
        var (s, t) = await CutAsync(0, 10, 20, 40);
        Ramp(s, t, 12, 18, 1.1);

        Assert.True(When(t, AtTrackSeconds(t, 10), s) == 10, "a section before the ramp moved — nothing before a tempo change should");
        double want = Timeline(20, 1.0, 12, 18, 1.1);
        Assert.True(Math.Abs(When(t, AtTrackSeconds(t, 20), s) - want) <= 1.0 / Rate,
            $"a section after the ramp is at {When(t, AtTrackSeconds(t, 20), s):0.00000}s, not {want:0.00000}s where the faster tempo puts it — out of time with what the ramp says");
        Assert.False(s.VoiceFor(t)!.Tempo.IsConstant, "the engine is not playing the tempo ramp at all");
        AssertEnginePlacesSectionsWhereCoreDoes(s, t, "a track with a tempo ramp");
    }

    [Fact]
    public async Task ARampedTrackPlaysEachSectionWhereTheLaneDrawsIt()
    {
        // The origin at 30 s and a ramp from 25 s to 40 s that begins before it. One section cut at
        // both ends with a gap either side, so where it starts and stops can be read off the audio.
        var (s, t) = await CutAsync(30, 33, 36, 45);
        var ordered = t.Clips.OrderBy(c => c.TrackStartSeconds).ToList();
        var cut = ordered[2];
        t.Clips.Remove(ordered[1]);
        t.Clips.Remove(ordered[3]);
        t.Mode = StretchMode.Linked;
        Ramp(s, t, 25, 40, 1.15);

        long wantStart = (long)Math.Round(When(t, cut, s) * Rate);
        long wantEnd = (long)Math.Round(TimelineEditor.TimelineSecondsOfFrame(t, End(cut, s), s) * Rate);
        Assert.True(wantStart > 36 * Rate - 2 * Rate && wantEnd > 40 * Rate, "setup: the section should start under the ramp and end after it");

        var buf = Render(s, Rate * 50);
        long first = -1, last = -1;
        for (long f = wantStart - Rate / 2; f < wantEnd + Rate / 2; f++)
            if (buf[f * 2] != 0) { if (first < 0) first = f; last = f; }

        AssertEnginePlacesSectionsWhereCoreDoes(s, t, "origin at 30 s, ramp from 25 s");
        Assert.True(Math.Abs(first - wantStart) <= 2 && Math.Abs(last + 1 - wantEnd) <= 2,
            $"under a tempo ramp that starts before the track does, the section plays {first}..{last + 1} but the lane draws it at {wantStart}..{wantEnd} — "
          + "it would be heard out of time with where it was lined up");
    }

    [Fact]
    public async Task AnEditBeforeARampedTracksStartReachesTheEngineInOneStep()
    {
        var (s, t) = await CutAsync(30, 33, 36);
        Ramp(s, t, 25, 40, 1.15);
        var v = s.VoiceFor(t)!;
        int before = v.PlacementsPublished;

        // A paste before the track's start moves its origin back and every section forward, and the
        // tempo map, measured from the origin, with them.
        var result = TimelineEditor.Paste(t, TimelineEditor.Copy(t.Clips[0]), 10, s);
        s.Apply(t);

        Assert.True(result.Changed && v.TimelineOffsetFrames == (long)Math.Round(t.TimelineOffsetSeconds * Rate) && t.TimelineOffsetSeconds < 30,
            "setup: the paste should have moved the track's start back");
        Assert.True(v.PlacementsPublished - before == 1,
            $"a paste before a ramped track's start reached the engine in {v.PlacementsPublished - before} steps — a block played between them "
          + "puts the tempo ramp seconds away from where it is, plays the wrong audio and jumps back");
    }

    [Fact]
    public async Task MovingARampedTracksOriginLeavesEverySectionAndRampWhereTheyWere()
    {
        // The origin at 30 s, and a ramp from 25 s to 40 s that begins before it.
        var (s, t) = await CutAsync(30, 33, 36, 45);
        Ramp(s, t, 25, 40, 1.15);
        var before = t.Clips.Select(c => (c, When(t, c, s))).ToList();
        var ramps = t.Ramps.ToList();

        long moved = TimelineEditor.ShiftOrigin(t, 5 * Rate, s);
        Assert.Equal(5 * Rate, moved);
        foreach (var (c, at) in before)
            Assert.True(Math.Abs(When(t, c, s) - at) <= 1.0 / Rate,
                $"moving the track's origin back moved a section from {at:0.00000}s to {When(t, c, s):0.00000}s");
        Assert.True(t.Ramps.SequenceEqual(ramps), "moving the track's origin moved a tempo ramp — ramps sit on the timeline");

        long room = TimelineEditor.OriginRoomFrames(t, s);
        TimelineEditor.ShiftOrigin(t, room, s);
        Assert.True(t.TimelineOffsetSeconds >= 0 && t.TimelineOffsetSeconds <= 1.0 / Rate,
            $"moving the origin back as far as it can go left it at {t.TimelineOffsetSeconds}s: the room and the move disagree under a ramp");
    }

    [Fact]
    public async Task ASectionDraggedUnderARampLandsOnAWholeFrameWhereItWasDropped()
    {
        var (s, t) = await CutAsync(0, 5, 8);
        Ramp(s, t, 10, 30, 1.2);
        var mover = AtTrackSeconds(t, 5);

        var move = new SectionMove(s, t, mover);
        move.DragTo(t, 15.37, SnapSettings.Off);
        move.Commit();

        double frames = mover.TrackStartSeconds * Rate;
        Assert.True(Math.Abs(frames - Math.Round(frames)) < 1e-6, "a section dragged under a ramp is not on a whole frame — its joins would be a frame off");
        Assert.True(Math.Abs(When(t, mover, s) - 15.37) <= 1.0 / Rate, $"dragged to 15.37 s under a ramp, it landed at {When(t, mover, s):0.00000}s");
    }

    [Fact]
    public async Task TheEndOfASectionUnderARampSnapsToTheStartOfTheNext()
    {
        var (s, t) = await CutAsync(0, 5, 8, 20, 25);
        Ramp(s, t, 10, 30, 1.2);
        var mover = AtTrackSeconds(t, 5);
        var next = AtTrackSeconds(t, 20);
        double edge = When(t, next, s);
        // Where the mover must start for its end to fall on that edge: 3 track-seconds back, by the ramp.
        double startForEnd = TimelineEditor.TimelineSecondsAt(t, TimelineEditor.TrackSecondsAt(t, edge) - 3);

        // Dropped a little short, zoomed in so the snap reaches only 0.05 s: an end worked out at the
        // knob's speed, ignoring the ramp, is about 0.2 s out here and misses the edge. (Dropped a
        // little long instead, the overwrite would trim the next section into a join either way.)
        var move = new SectionMove(s, t, mover);
        move.DragTo(t, startForEnd - 0.03, new SnapSettings(true, 200));
        move.Commit();
        s.ApplyAll();

        var timeline = s.VoiceFor(t)!.Timeline;
        int i = Enumerable.Range(0, timeline.Count).Single(k => timeline[k].TrackStartFrame == Start(mover, s));
        Assert.True(End(mover, s) == Start(next, s) && timeline.ButtJoinAfter(i),
            "under a ramp a section's end snapped near the next one's start but not onto it — a gap with a declick either side instead of a join");
    }

    [Fact]
    public async Task ASplitUnderARampIsASeam()
    {
        var (s, t) = await CutAsync(0);
        Ramp(s, t, 10, 30, 1.2);
        Assert.True(TimelineEditor.Split(t, 20, s).Changed);
        s.ApplyAll();

        var ordered = t.Clips.OrderBy(c => Start(c, s)).ToList();
        var timeline = s.VoiceFor(t)!.Timeline;
        Assert.True(End(ordered[0], s) == Start(ordered[1], s)
                    && TimelineEditor.SourceEndFrameOf(ordered[0], s) == TimelineEditor.SourceStartFrameOf(ordered[1], s)
                    && timeline.SeamAfter(0),
            "a split under a tempo ramp is not a seam — the split point would get two declicks: an audible tick where nothing moved");
    }

    [Fact]
    public async Task AnEmptyTrackTakesTheRampsWithTheTempoAndGivesThemBack()
    {
        var (s, t) = await CutAsync(0, 5, 8);
        t.Speed = 0.95;
        Ramp(s, t, 10, 30, 1.05);
        var mover = AtTrackSeconds(t, 5);
        double at = When(t, mover, s);
        var empty = s.AddEmptyTrack();

        var move = new SectionMove(s, t, mover);
        move.DragTo(empty, at, SnapSettings.Off);
        Assert.True(empty.Ramps.SequenceEqual(t.Ramps) && empty.Speed == 0.95,
            "the empty track did not take the tempo ramps along with the tempo — the section would not play at the speed it had");
        Assert.True(Math.Abs(When(empty, mover, s) - at) <= 1.0 / Rate, "moved straight down, the section no longer lands at the same moment");
        move.DragTo(t, at, SnapSettings.Off);
        move.Commit();
        Assert.True(empty.Ramps.Count == 0 && empty.Speed == 1.0, "passing over the empty track left it with a tempo ramp it never had");
    }

    [Fact]
    public async Task PastingFromARampedTrackOntoAnEmptyOneTakesItsRamps()
    {
        var (s, t) = await CutAsync(0, 5, 8);
        Ramp(s, t, 10, 30, 1.05);
        var empty = s.AddEmptyTrack();
        var result = TimelineEditor.Paste(empty, TimelineEditor.Copy(AtTrackSeconds(t, 5)), 40, s, copiedFrom: t);
        Assert.True(result.Changed && empty.Ramps.SequenceEqual(t.Ramps), "pasted onto an empty track, a section from a ramped track left the ramps behind");
        Assert.Contains("tempo ramps", result.Message);
    }

    [Fact]
    public async Task StemTracksTakeTheirSourcesTempoRamps()
    {
        var (s, t) = await CutAsync(0);
        Ramp(s, t, 10, 30, 1.05);
        var stem = s.AddSeparatedTrack(t, _wav, $"{t.Name} — vocals", "#ffffff");
        Assert.True(stem.Ramps.SequenceEqual(t.Ramps), "a vocals stem did not take the song's tempo ramps — it would drift away from the instrumental");
    }

    [Fact]
    public async Task ARampedProjectReopensSoundingTheSame()
    {
        var (s, t) = await CutAsync(0, 10);
        t.Mode = StretchMode.Linked;
        Ramp(s, t, 4, 16, 1.1);
        var before = Render(s, Rate * 20);

        string path = Path.Combine(_dir, "ramped.benchmix");
        ProjectStore.Save(s.Project, path);
        var again = ProjectSession.CreateOffline(Rate);
        await again.OpenAsync(ProjectStore.Load(path).Project);
        var after = Render(again, Rate * 20);

        int differing = Enumerable.Range(0, before.Length).Count(i => before[i] != after[i]);
        Assert.True(differing == 0, $"{differing} samples differ after saving and reopening a project with a tempo ramp");
    }

    [Fact]
    public async Task AProjectWithRampsIsSavedAsSchemaThree()
    {
        var (s, t) = await CutAsync(0);
        Ramp(s, t, 10, 30, 1.05);
        string path = Path.Combine(_dir, "schema.benchmix");
        ProjectStore.Save(s.Project, path);
        string json = File.ReadAllText(path);
        Assert.True(Regex.IsMatch(json, "\"schemaVersion\":\\s*3") && json.Contains("\"ramps\"") && json.Contains("\"toSpeed\""),
            "the saved project is not schema 3 with its ramps — an older Benchmix would open it, drop the tempo ramps without a word and save over them");
    }

    [Fact]
    public async Task AVersionTwoProjectOpensWithNoRamps()
    {
        var (s, _) = await CutAsync(0, 10);
        string path = Path.Combine(_dir, "v2.benchmix");
        ProjectStore.Save(s.Project, path);
        string json = File.ReadAllText(path);
        json = Regex.Replace(json, "\"schemaVersion\":\\s*3", "\"schemaVersion\": 2");
        json = Regex.Replace(json, ",\\s*\"ramps\":\\s*\\[\\s*\\]", "");
        Assert.DoesNotContain("\"ramps\"", json);
        File.WriteAllText(path, json);

        var loaded = ProjectStore.Load(path).Project;
        Assert.True(loaded.Tracks.All(x => x.Ramps is { Count: 0 }) && loaded.SchemaVersion == BenchmixProject.CurrentSchemaVersion,
            "a project saved before tempo ramps did not open cleanly as one with none");
    }

    [Fact]
    public async Task RampsThatCannotBePlayedAreDroppedOnLoad()
    {
        var (s, t) = await CutAsync(0);
        t.Ramps.AddRange(new[]
        {
            new TempoRamp(30, 40, 1.1),
            new TempoRamp(10.0000001, 20, 1.05),     // kept, and put on a whole frame
            new TempoRamp(35, 45, 1.2),              // overlaps the one at 30
            new TempoRamp(50, 50.05, 1.1),           // too short
            new TempoRamp(60, 70, 9.0),              // a speed the engine cannot play
            new TempoRamp(-5, 2, 1.1),               // before 0:00
        });
        string path = Path.Combine(_dir, "bad.benchmix");
        ProjectStore.Save(s.Project, path);
        var ramps = ProjectStore.Load(path).Project.Tracks[0].Ramps;

        Assert.True(ramps.Count == 2 && ramps[0].StartSeconds == 10 && ramps[1].StartSeconds == 30,
            $"loading kept {ramps.Count} ramps — a damaged file would play tempo changes nobody set up, or crash");
    }

    // ---------------------------------------------------------------- making and changing a ramp

    [Fact]
    public async Task ARampStartsAtTheTempoTheTrackIsPlayingThere()
    {
        var (s, t) = await CutAsync(0);
        t.Bpm = 100;
        t.Speed = 0.96;
        Assert.True(TempoRamps.Add(t, 10, 20, 1.05, s).Changed);
        Assert.True(TimelineEditor.SpeedAt(t, 10) == 0.96 && TempoRamps.BpmAt(t, 10) == 100 * 0.96,
            "a ramp does not start at the tempo the track was playing — the tempo would jump where the ramp begins");
        Assert.True(TempoRamps.Add(t, 30, 40, 1.1, s).Changed);
        Assert.True(TimelineEditor.SpeedAt(t, 30) == 1.05,
            "a second ramp does not start at the tempo the first one arrived at — the tempo would jump between them");
    }

    [Fact]
    public async Task AddingARampSaysWhereItGoesAndHowMuchLaterAudioMoves()
    {
        var (s, t) = await CutAsync(0, 30);
        t.Bpm = 100;
        var result = TempoRamps.Add(t, 10.0000001, 20, 1.1, s);
        var r = t.Ramps.Single();
        Assert.True(r.StartSeconds * Rate == Math.Round(r.StartSeconds * Rate) && r.EndSeconds * Rate == Math.Round(r.EndSeconds * Rate),
            "a ramp's ends are not on whole frames — sections placed against them would land a frame off");
        Assert.Contains("from 100.0 to 110.0 BPM between 10.00s and 20.00s", result.Message);
        Assert.Contains("plays 1.36s sooner", result.Message);          // 9.5 s of song at 1.1x after the ramp, not 10 at 1.0x
    }

    [Fact]
    public async Task ARampCannotStartInsideAnotherOrRunIntoTheNext()
    {
        var (s, t) = await CutAsync(0);
        t.Bpm = 100;
        Assert.True(TempoRamps.Add(t, 10, 20, 1.05, s).Changed);
        Assert.True(TempoRamps.Add(t, 30, 40, 1.1, s).Changed);
        Assert.False(TempoRamps.Add(t, 15, 25, 1.0, s).Changed, "a ramp was allowed to start inside another — two tempo changes at once");
        Assert.False(TempoRamps.Add(t, 22, 31, 1.0, s).Changed, "a ramp was allowed to run into the next one");
        Assert.True(TempoRamps.Add(t, 20, 30, 1.0, s).Changed, "a ramp butting both its neighbours should be allowed");
        Assert.True(TempoRamps.IndexInside(t, 25) == 1 && TempoRamps.IndexInside(t, 20) == -1, "the ramp under a point is not found");
        t.Ramps.RemoveAt(1);
        Assert.True(TempoRamps.RoomAround(t, 25) == (20, 30) && TempoRamps.RoomAround(t, 5) == (0, 10)
                    && TempoRamps.RoomAround(t, 50) == (40, double.PositiveInfinity), "the free room between ramps is wrong");
    }

    [Fact]
    public async Task ATempoItCannotReachIsRefused_AndAnUnknownTempoStartsNoRamp()
    {
        var (s, t) = await CutAsync(0);
        t.Bpm = 100;
        Assert.True(TempoRamps.ParseBpm(t, " 97.5 ") is (true, var v, _) && v == 97.5 / 100, "a typed tempo was not read as typed");
        Assert.True(TempoRamps.ParseBpm(t, "fast") is (false, _, var why) && why.Contains("like 115"), "a word was taken for a tempo");
        Assert.True(TempoRamps.ParseBpm(t, "999") is (false, _, var far) && far.Contains("between 25.0 and 400.0"),
            "a tempo the engine cannot play was accepted");
        Assert.False(TempoRamps.Add(t, 10, 20, 9.0, s).Changed, "a ramp to a speed the engine cannot play was placed");
        t.Bpm = 0;
        Assert.True(TempoRamps.Add(t, 10, 20, 1.0, s) is { Changed: false } r && r.Message.Contains("unknown"),
            "a track with no known tempo got a ramp whose BPM means nothing");
    }

    [Fact]
    public async Task ChangingARampsEndTempoOrRemovingItMovesLaterSectionsAndUndoesExactly()
    {
        var (s, t) = await CutAsync(0, 30);
        t.Bpm = 100;
        var later = AtTrackSeconds(t, 30);
        double at = When(t, later, s);
        TempoRamps.Add(t, 10, 20, 1.1, s);
        double sped = When(t, later, s);
        Assert.True(TempoRamps.SetEndSpeed(t, 0, 0.9, s).Changed && When(t, later, s) > at, "slowing the ramp down did not push later sections later");
        Assert.False(TempoRamps.SetEndSpeed(t, 0, 0.9, s).Changed, "typing the same tempo again counted as a change");
        Assert.True(TempoRamps.SetEndSpeed(t, 0, 1.1, s).Changed && When(t, later, s) == sped, "putting the tempo back did not put the section back exactly");
        Assert.True(TempoRamps.Remove(t, 0, s).Changed && t.Ramps.Count == 0 && When(t, later, s) == at,
            "removing the ramp did not put the later section back where it was");
    }

    [Fact]
    public async Task DraggingARampDotStopsAtTheNextRampAndBackPutsItBack()
    {
        var (s, t) = await CutAsync(0);
        t.Bpm = 100;
        TempoRamps.Add(t, 10, 20, 1.05, s);
        TempoRamps.Add(t, 30, 40, 1.1, s);
        var original = t.Ramps[0];

        var end = new RampEdgeDrag(s, t, 0, end: true);
        end.DragTo(35, SnapSettings.Off);
        Assert.True(t.Ramps[0].EndSeconds == 30, $"the end was dragged into the next ramp, to {t.Ramps[0].EndSeconds}s");
        end.DragTo(10.02, SnapSettings.Off);
        Assert.True(t.Ramps[0].EndSeconds == 10 + TempoRamps.MinimumSeconds, "the end was dragged past the ramp's own start");
        end.DragTo(20, SnapSettings.Off);
        Assert.True(t.Ramps[0] == original && !end.Finish().Changed, "dragging the end back did not put the ramp back exactly");

        var start = new RampEdgeDrag(s, t, 1, end: false);
        start.DragTo(15, SnapSettings.Off);
        Assert.True(t.Ramps[1].StartSeconds == 20, "the start was dragged back into the ramp before it");
        start.DragTo(25.37, SnapSettings.Off);
        Assert.True(t.Ramps[1].StartSeconds * Rate == Math.Round(t.Ramps[1].StartSeconds * Rate) && Math.Abs(t.Ramps[1].StartSeconds - 25.37) <= 1.0 / Rate
                    && start.Finish().Changed, "the start did not land on a whole frame where it was dropped");
    }

    [Fact]
    public async Task TheEndDotIgnoresItsOwnTracksSectionsButTheStartDoesNot()
    {
        var (s, t) = await CutAsync(0, 12, 33);
        t.Bpm = 100;
        TempoRamps.Add(t, 20, 30, 1.1, s);
        var snap = new SnapSettings(true, 20);                       // half a second either side
        double own = When(t, AtTrackSeconds(t, 33), s);                // a section edge on its own track, after the ramp
        double before = When(t, AtTrackSeconds(t, 12), s);             // one before it

        double startAt = TempoRamps.SnapPoint(t, before + 0.3, isEnd: false, except: 0, snap, s, out var startSnap);
        Assert.True(startAt == before && startSnap == before, $"a ramp's start did not snap to its own track's section edge at {before}s");
        double endAt = TempoRamps.SnapPoint(t, own + 0.3, isEnd: true, except: 0, snap, s, out var endSnap);
        Assert.True(endSnap is null && Math.Abs(endAt - (own + 0.3)) <= 1.0 / Rate,
            "a ramp's end snapped to a section on its own track — that section moves as soon as the ramp changes the tempo, so the end would not stay on it");

        var other = await s.AddTrackAsync(_wav, own + 0.1);
        endAt = TempoRamps.SnapPoint(t, own + 0.3, isEnd: true, except: 0, snap, s, out endSnap);
        double edge = When(other, other.Clips[0], s);
        Assert.True(endSnap == edge && Math.Abs(endAt - edge) <= 1.0 / Rate, "a ramp's end did not snap to another deck's section");
        Assert.True(TempoRamps.SnapPoint(t, own + 0.3, true, 0, SnapSettings.Off, s, out var none) == TimelineEditor.SecondsOf(s.SecondsToFrames(own + 0.3), s) && none is null,
            "with Ctrl held a ramp's dot still snapped");
    }

    [Fact]
    public async Task UndoIsDeepForRamps()
    {
        var (s, t) = await CutAsync(0);
        var undo = new UndoStack();
        undo.Push(s.Project, "ramp");
        Ramp(s, t, 10, 30, 1.05);
        var back = undo.Undo(s.Project);
        Assert.True(back is not null && back.Tracks[0].Ramps.Count == 0 && t.Ramps.Count == 1,
            "the undo snapshot shares the live list of ramps — Ctrl+Z would not take a ramp back, or would take the live one with it");
    }

    [Fact]
    public async Task AGroupUnderARampSnapsItsEndOntoAnotherTracksEdge()
    {
        // Sections [5,6) and [20,25) of a song, alone on their track, under a ramp from 10 s to 30 s
        // up to 1.2x; another deck starts at 24.2 s, just after the second one ends.
        var (s, t) = await CutAsync(0, 5, 6, 20, 25);
        var grabbed = AtTrackSeconds(t, 5);
        var member = AtTrackSeconds(t, 20);
        t.Clips.RemoveAll(c => !ReferenceEquals(c, grabbed) && !ReferenceEquals(c, member));
        Ramp(s, t, 10, 30, 1.2);
        var other = await s.AddTrackAsync(_wav, 24.2);
        double edge = When(other, other.Clips[0], s);
        double gap = edge - TimelineEditor.TimelineSecondsOfFrame(t, End(member, s), s);
        Assert.True(gap > 0.05 && gap < 0.3, $"setup: the section should end a little before the other deck starts, not {gap:0.000}s");

        var move = new GroupMove(s, new[] { (t, grabbed), (t, member) }, grabbed);
        move.DragTo(t, When(t, grabbed, s), new SnapSettings(true, 28));
        move.Commit();

        double landed = TimelineEditor.TimelineSecondsOfFrame(t, End(member, s), s);
        Assert.True(move.SnapLineSeconds == edge && Math.Abs(landed - edge) <= 1.0 / Rate,
            $"under a tempo ramp the group snapped to the other deck's start at {edge:0.00000}s, but the section's end landed at {landed:0.00000}s — "
          + $"the guide line says lined up and the mix is {(edge - landed) * 1000:0.0} ms out");
    }

    [Fact]
    public async Task AGroupAcrossTracksUnderARampSnapsAndStillMovesTogether()
    {
        // One section of the group on a ramped track, the grabbed one on a track with none.
        var (s, t) = await CutAsync(0, 20, 25);
        var member = AtTrackSeconds(t, 20);
        t.Clips.RemoveAll(c => !ReferenceEquals(c, member));
        Ramp(s, t, 10, 30, 1.2);
        var plain = await s.AddTrackAsync(_wav, 0);
        Assert.True(TimelineEditor.Split(plain, 5, s).Changed && TimelineEditor.Split(plain, 6, s).Changed);
        var grabbed = AtTrackSeconds(plain, 5);
        plain.Clips.RemoveAll(c => !ReferenceEquals(c, grabbed));
        var other = await s.AddTrackAsync(_wav, 24.2);
        s.ApplyAll();
        double edge = When(other, other.Clips[0], s);
        double memberWas = When(t, member, s), grabbedWas = When(plain, grabbed, s);

        var move = new GroupMove(s, new[] { (t, member), (plain, grabbed) }, grabbed);
        move.DragTo(null, grabbedWas, new SnapSettings(true, 28));
        move.Commit();

        double landed = TimelineEditor.TimelineSecondsOfFrame(t, End(member, s), s);
        double memberMoved = When(t, member, s) - memberWas, grabbedMoved = When(plain, grabbed, s) - grabbedWas;
        Assert.True(Math.Abs(landed - edge) <= 1.0 / Rate,
            $"a group across two tracks snapped its ramped section's end to {edge:0.00000}s, but it landed at {landed:0.00000}s — {(edge - landed) * 1000:0.0} ms out");
        Assert.True(Math.Abs(memberMoved - grabbedMoved) <= 1.0 / Rate,
            $"snapping moved the section on one deck {memberMoved:0.00000}s and on the other {grabbedMoved:0.00000}s — what was lined up across the decks no longer is");
    }

    [Fact]
    public async Task AGroupMovedIntoATempoRampKeepsItsSeam_AndOnlyTheGrabbedLandsExactly()
    {
        var (s, t) = await CutAsync(0, 5, 6, 7);
        Ramp(s, t, 10, 30, 1.2);
        var m1 = AtTrackSeconds(t, 5);
        var m2 = AtTrackSeconds(t, 6);
        long apart = Start(m2, s) - Start(m1, s);

        var move = new GroupMove(s, new[] { (t, m1), (t, m2) }, m1);
        move.DragTo(t, 20, SnapSettings.Off);
        move.Commit();
        s.ApplyAll();

        var timeline = s.VoiceFor(t)!.Timeline;
        int i = Enumerable.Range(0, timeline.Count).Single(k => timeline[k].TrackStartFrame == Start(m1, s));
        Assert.True(End(m1, s) == Start(m2, s) && timeline.SeamAfter(i), "moved into a tempo ramp, the seam between two sections opened — it would tick");
        Assert.True(Start(m2, s) - Start(m1, s) == apart, "the group's sections are no longer the same frames apart");
        Assert.True(Math.Abs(When(t, m1, s) - 20) <= 1.0 / Rate, $"the grabbed section landed at {When(t, m1, s):0.00000}s, not 20 s");
    }
}
