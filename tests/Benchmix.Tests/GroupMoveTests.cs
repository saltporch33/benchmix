using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Dragging several selected sections at once (Daniel, 2026-10-11). The source is a ramp
/// (value == position), so a section that lands a frame off, or plays the wrong part, shows.
/// </summary>
public class GroupMoveTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;

    public GroupMoveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-group", Guid.NewGuid().ToString("N"));
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

    static long Start(ClipModel c, ProjectSession s) => TimelineEditor.StartFrameOf(c, s);
    static long End(ClipModel c, ProjectSession s) => TimelineEditor.EndFrameOf(c, s);
    static double When(TrackModel t, ClipModel c, ProjectSession s) => TimelineEditor.TimelineSecondsOfFrame(t, Start(c, s), s);

    /// <summary>The section that starts at this point of the timeline.</summary>
    static ClipModel At(TrackModel t, double timelineSeconds, ProjectSession s)
        => t.Clips.Single(c => Math.Abs(When(t, c, s) - timelineSeconds) < 0.001);

    static async Task<TrackModel> CutAsync(ProjectSession s, string wav, double offset, params double[] at)
    {
        var t = await s.AddTrackAsync(wav, offset);
        foreach (var x in at) Assert.True(TimelineEditor.Split(t, x, s).Changed, $"could not split at {x}s");
        return t;
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
    public async Task TwoSectionsMovedTogetherStillMeetAtASeam()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await s.AddTrackAsync(_wav);
        a.Speed = 0.93;
        s.ApplyAll();
        foreach (var x in new[] { 10.0, 20.0, 30.0 }) TimelineEditor.Split(a, x, s);
        var m1 = At(a, 10, s);
        var m2 = At(a, 20, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 17.3 + 0.37 / Rate, SnapSettings.Off);       // off a whole frame, on purpose
        move.Commit();
        s.ApplyAll();

        var timeline = s.VoiceFor(a)!.Timeline;
        int i = Enumerable.Range(0, timeline.Count).Single(k => timeline[k].TrackStartFrame == Start(m1, s));
        Assert.True(End(m1, s) == Start(m2, s) && TimelineEditor.SourceEndFrameOf(m1, s) == TimelineEditor.SourceStartFrameOf(m2, s)
                    && timeline.SeamAfter(i),
            "the split point of two sections moved together now has a gap and two declicks: an audible tick where nothing was moved apart");
    }

    [Fact]
    public async Task AGroupMovedAlongItsTrackSoundsLikeTheUnsplitSectionMovedTheSameWay()
    {
        var one = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(one, _wav, 0, 10, 30);
        var whole = new SectionMove(one, a, At(a, 10, one));
        whole.DragTo(a, 17.3, SnapSettings.Off);
        whole.Commit();

        var two = ProjectSession.CreateOffline(Rate);
        var b = await CutAsync(two, _wav, 0, 10, 20, 30);
        var first = At(b, 10, two);
        var group = new GroupMove(two, new[] { (b, first), (b, At(b, 20, two)) }, first);
        group.DragTo(b, 17.3, SnapSettings.Off);
        group.Commit();

        var x = Render(one, Rate * 45);
        var y = Render(two, Rate * 45);
        int differing = 0;
        for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) differing++;
        Assert.True(differing == 0, $"{differing} samples differ: moving a split section as a group changed what plays inside it");
    }

    [Fact]
    public async Task AGroupOnTwoTracksMovesTheSameTimeOnBoth()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20);
        var b = await s.AddTrackAsync(_wav);
        b.Speed = 0.93;
        s.ApplyAll();
        TimelineEditor.Split(b, 10, s);
        TimelineEditor.Split(b, 20, s);
        var ma = At(a, 10, s);
        var mb = At(b, 10, s);

        var move = new GroupMove(s, new[] { (a, ma), (b, mb) }, ma);
        Assert.False(move.CanChangeTrack);
        move.DragTo(null, 10 + 3.21, SnapSettings.Off);
        move.Commit();

        Assert.True(Math.Abs(When(a, ma, s) - 13.21) <= 1.0 / Rate && Math.Abs(When(b, mb, s) - 13.21) <= 1.0 / Rate,
            $"one moved to {When(a, ma, s):0.00000}s, the other to {When(b, mb, s):0.00000}s: the section on the other deck slid " +
            "a different amount and fell out of time with the one it was lined up with");
    }

    [Fact]
    public async Task AGroupOnSeveralTracksStaysOnItsTracksWhateverTrackItIsDraggedOver()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20);
        var b = await CutAsync(s, _wav, 0, 10, 20);
        var empty = s.AddEmptyTrack();
        var ma = At(a, 10, s);
        var mb = At(b, 10, s);

        var move = new GroupMove(s, new[] { (a, ma), (b, mb) }, ma);
        move.DragTo(empty, 12, SnapSettings.Off);
        move.Commit();

        Assert.True(a.Clips.Contains(ma) && b.Clips.Contains(mb) && empty.Clips.Count == 0,
            "a group on two tracks was dumped onto one track — it can only go sideways");
    }

    [Fact]
    public async Task AGroupOnOneTrackDraggedOntoAnEmptyTrackLandsTogetherAndPlaysAsItDid()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await s.AddTrackAsync(_wav);
        a.Speed = 0.95;
        s.ApplyAll();
        foreach (var x in new[] { 10.0, 20.0, 30.0, 40.0 }) TimelineEditor.Split(a, x, s);
        var m1 = At(a, 10, s);
        var m2 = At(a, 30, s);
        long apart = Start(m2, s) - Start(m1, s);
        var empty = s.AddEmptyTrack();

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(empty, 10, SnapSettings.Off);
        var result = move.Commit();

        Assert.True(empty.Clips.Contains(m1) && empty.Clips.Contains(m2) && !a.Clips.Contains(m1) && !a.Clips.Contains(m2),
            "the group did not land on the track it was dragged to");
        Assert.True(Start(m2, s) - Start(m1, s) == apart, "the two sections are no longer the same distance apart on their new track");
        Assert.True(empty.Speed == 0.95, $"the empty track plays them at speed {empty.Speed}, not 0.95: they no longer sound as they did");
        Assert.True(Math.Abs(When(empty, m1, s) - 10) <= 1.0 / Rate && Math.Abs(When(empty, m2, s) - 30) <= 1.0 / Rate,
            "moved straight down at the same time, the sections no longer play at the same moment");
        Assert.Contains("2 sections moved to", result.Message);
    }

    [Fact]
    public async Task DraggingAGroupPastAnEmptyTrackPutsItBackAsItWas()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20, 30);
        a.Speed = 0.9;
        s.ApplyAll();
        var empty = s.AddEmptyTrack();
        string name = empty.Name;
        var m1 = At(a, 10 / 0.9, s);
        var m2 = At(a, 20 / 0.9, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(empty, 12, SnapSettings.Off);
        move.DragTo(a, 12, SnapSettings.Off);
        move.Commit();

        Assert.True(empty.Clips.Count == 0 && string.IsNullOrEmpty(empty.SourcePath) && empty.Speed == 1.0 && empty.Name == name,
            "passing over the empty track left it holding a song, or a tempo, it does not have");
    }

    [Fact]
    public async Task AGroupStopsAtZeroAsAWholeAndIsNotSqueezed()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 5, 10, 20, 25);
        var m1 = At(a, 5, s);
        var m2 = At(a, 20, s);
        long apart = Start(m2, s) - Start(m1, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m2);
        move.DragTo(a, 20 - 40, SnapSettings.Off);
        move.Commit();

        Assert.True(Start(m1, s) == 0 && When(a, m1, s) == 0, $"the leftmost section stopped at {When(a, m1, s):0.00000}s, not 0:00");
        Assert.True(Start(m2, s) - Start(m1, s) == apart,
            "the group was squeezed at 0:00 — the second section no longer comes 15 s after the first");
    }

    [Fact]
    public async Task AGroupDraggedBeforeItsTrackBeganMovesTheOriginBack()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 30, 40, 50, 55);
        var stays = At(a, 30, s);
        var m1 = At(a, 40, s);
        var m2 = At(a, 50, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 12, SnapSettings.Off);
        move.Commit();

        Assert.True(Math.Abs(When(a, m1, s) - 12) <= 1.0 / Rate, $"the group landed at {When(a, m1, s):0.00000}s, not 12 s: the track's start acted as a wall");
        Assert.True(Math.Abs(When(a, stays, s) - 30) <= 1e-9, "a section not in the group moved when the track's origin was moved back");
        Assert.True(End(m1, s) == Start(m2, s), "the seam between the two moved sections opened up");
    }

    [Fact]
    public async Task DraggingAGroupBackFromBeforeTheOriginPutsTheOriginBack()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 30, 40, 50, 55);
        var m1 = At(a, 40, s);
        var m2 = At(a, 50, s);
        double origin = a.TimelineOffsetSeconds;

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 12, SnapSettings.Off);
        move.DragTo(a, 42, SnapSettings.Off);
        move.Commit();

        Assert.True(a.TimelineOffsetSeconds == origin,
            $"a drag that went before the track's start and came back left the track rebased (origin {a.TimelineOffsetSeconds}, was {origin})");
    }

    [Fact]
    public async Task AnyMemberOfAGroupSnapsToAnotherSectionsExactFrame()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20, 30, 40);
        TimelineEditor.Delete(a, At(a, 30, s));                       // a gap from 30 to 40
        var m1 = At(a, 10, s);
        var m2 = At(a, 20, s);
        var after = At(a, 40, s);
        long apart = Start(m2, s) - Start(m1, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 10 + 10 - 0.1, new SnapSettings(true, 28));     // the second one's end 0.1 s short of 40
        move.Commit();
        s.ApplyAll();

        var timeline = s.VoiceFor(a)!.Timeline;
        int i = Enumerable.Range(0, timeline.Count).Single(k => timeline[k].TrackStartFrame == Start(m2, s));
        Assert.True(End(m2, s) == Start(after, s) && timeline.ButtJoinAfter(i),
            "a near miss left a gap with a declick either side, instead of a join the engine crossfades");
        Assert.True(Start(m2, s) - Start(m1, s) == apart, "the grabbed section did not move by the same frames as the one that snapped");
    }

    [Fact]
    public async Task AGroupDoesNotSnapToWhereItsOwnSectionsWere()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20, 30);
        TimelineEditor.Delete(a, At(a, 0, s));
        TimelineEditor.Delete(a, At(a, 30, s));
        var m1 = At(a, 10, s);
        var m2 = At(a, 20, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 9.9, new SnapSettings(true, 28));
        move.Commit();

        Assert.True(move.SnappedTo is null && Start(m1, s) == TimelineEditor.TrackFrameAt(a, 9.9, s),
            "the group jumped back onto its own ghost — it snapped to where its own sections were");
    }

    [Fact]
    public async Task WithNoSectionNearbyAGroupLandsExactlyWhereItWasDropped()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20, 30);
        s.Project.ProjectBpm = 120;
        var m1 = At(a, 10, s);
        var m2 = At(a, 20, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 36.7, new SnapSettings(true, 28));
        move.Commit();

        Assert.True(move.SnappedTo is null && Start(m1, s) == s.SecondsToFrames(36.7),
            "the group jumped to a bar line — bar snapping was turned off on 2026-10-11, so a drag lands where it is dropped");
    }

    [Fact]
    public async Task HoldingCtrlDragsAGroupExactlyWhereThePointerGoes()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20, 30);
        var m1 = At(a, 10, s);
        var m2 = At(a, 20, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 30.0 - 0.0005, SnapSettings.Off);                // right by the edge at 30: Ctrl means no snap
        move.Commit();

        Assert.True(move.SnappedTo is null && Start(m1, s) == s.SecondsToFrames(30.0 - 0.0005),
            "with Ctrl held the group was pulled onto a nearby edge instead of going where the pointer put it");
    }

    [Fact]
    public async Task ADroppedGroupOverwritesWhatIsUnderItButNeverItsOwnSections()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20, 30, 40);
        var m1 = At(a, 10, s);
        var m2 = At(a, 20, s);
        var under = At(a, 30, s);
        long l1 = TimelineEditor.LengthFramesOf(m1, s), l2 = TimelineEditor.LengthFramesOf(m2, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 15, SnapSettings.Off);                           // over its own old place and half of [30,40)
        var result = move.Commit();

        Assert.True(TimelineEditor.LengthFramesOf(m1, s) == l1 && TimelineEditor.LengthFramesOf(m2, s) == l2 && a.Clips.Contains(m1) && a.Clips.Contains(m2),
            "the group cut into itself: part of what was moved is missing");
        var ordered = a.Clips.OrderBy(c => Start(c, s)).ToList();
        for (int i = 0; i + 1 < ordered.Count; i++)
            Assert.True(End(ordered[i], s) <= Start(ordered[i + 1], s), "two sections are left sounding at once on one track");
        Assert.True(Math.Abs(When(a, under, s) - 35) <= 1.0 / Rate, "the section under the drop was not cut back to where the group ends");
        Assert.Contains("replaced what was underneath", result.Message);
    }

    [Fact]
    public async Task AKnobTurnedDuringAGroupDragIsKept()
    {
        // The arrangement is put back on every pointer move; the track's own settings are not.
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 10, 20, 30);
        var m1 = At(a, 10, s);
        var m2 = At(a, 20, s);

        var move = new GroupMove(s, new[] { (a, m1), (a, m2) }, m1);
        move.DragTo(a, 12, SnapSettings.Off);
        a.Semitones = 2;
        move.DragTo(a, 13, SnapSettings.Off);
        move.Commit();

        Assert.True(a.Semitones == 2, "a pitch change made during the drag was put back by the next pointer move, while the knob still showed it");
    }

    [Fact]
    public async Task AGroupAtZeroIsNotSnappedFurtherBackOnOneTrackOnly()
    {
        // The group is pushed against 0:00. An edge 0.03 s beyond one member would pull the group
        // before 0:00, where the other track cannot follow: that snap must not be taken.
        var s = ProjectSession.CreateOffline(Rate);
        var a = await CutAsync(s, _wav, 0, 1, 2);
        TimelineEditor.Delete(a, At(a, 0, s));                         // nothing at 0:00 on A to snap to
        var b = await s.AddTrackAsync(_wav);
        b.Speed = 0.93;
        s.ApplyAll();
        TimelineEditor.Split(b, 6, s);
        TimelineEditor.Split(b, 7, s);
        TimelineEditor.Delete(b, At(b, 0, s));
        await CutAsync(s, _wav, 1.5, 4.97);                            // an edge at 4.97 s on a third track
        var ma = At(a, 1, s);
        var mb = At(b, 6, s);

        var move = new GroupMove(s, new[] { (a, ma), (b, mb) }, ma);
        move.DragTo(null, 1 - 1.02, new SnapSettings(true, 200));     // past 0:00; at 200 px/s an edge pulls within 0.05 s
        move.Commit();

        Assert.True(move.SnappedTo is null, "the group snapped to an edge it could only reach by going before 0:00");
        Assert.True(Math.Abs(When(a, ma, s) - 0) <= 1.0 / Rate && Math.Abs(When(b, mb, s) - 5) <= 1.0 / Rate,
            $"one deck's section landed at {When(a, ma, s):0.00000}s and the other's at {When(b, mb, s):0.00000}s: " +
            "they moved by different amounts, and two sections lined up across decks are no longer together");
    }
}
