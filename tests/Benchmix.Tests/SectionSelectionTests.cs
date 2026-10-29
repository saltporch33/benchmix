using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Selecting several sections at once (Daniel, 2026-10-03): the range a Shift+click covers, and
/// deleting the selection in one go. The source is a ramp, as everywhere else, though nothing here
/// renders — these are about which sections are chosen and that the rest stay put.
/// </summary>
public class SectionSelectionTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;

    public SectionSelectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-selection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");

        int frames = Rate * 60;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = f / (float)frames;
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A track cut at 10, 20 and 30 s: four sections.</summary>
    private static async Task<TrackModel> FourSectionsAsync(ProjectSession s, string wav)
    {
        var t = await s.AddTrackAsync(wav);
        foreach (var at in new[] { 10.0, 20.0, 30.0 })
            Assert.True(TimelineEditor.Split(t, at, s).Changed, $"could not split at {at}s");
        return t;
    }

    private static List<ClipModel> InOrder(TrackModel t) => t.Clips.OrderBy(c => c.TrackStartSeconds).ToList();

    [Fact]
    public async Task ShiftClickRangeTakesEverySectionBetween_WhicheverEndWasClickedFirst()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await FourSectionsAsync(s, _wav);
        var c = InOrder(t);

        var forward = SectionSelection.Range(s.Project.Tracks, t, c[0], t, c[2], s).Select(r => r.Clip.Id).ToHashSet();
        var backward = SectionSelection.Range(s.Project.Tracks, t, c[2], t, c[0], s).Select(r => r.Clip.Id).ToHashSet();

        Assert.Equal(3, forward.Count);
        Assert.Contains(c[0].Id, forward);
        Assert.Contains(c[1].Id, forward);
        Assert.Contains(c[2].Id, forward);
        Assert.False(forward.Contains(c[3].Id), "the section after the range was swept up — Delete would then remove audio that was never selected");
        Assert.True(forward.SetEquals(backward), "clicking the far end first chose a different set of sections");
    }

    [Fact]
    public async Task ShiftClickAcrossTracksTakesTheTracksBetween_ButOnlySectionsWhollyInsideTheTime()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);
        var whole = await s.AddTrackAsync(_wav);          // one 60 s section on the track in between
        var b = await FourSectionsAsync(s, _wav);
        var ca = InOrder(a);
        var cb = InOrder(b);

        var ids = SectionSelection.Range(s.Project.Tracks, a, ca[1], b, cb[2], s)   // 10–30 s, tracks a..b
                                  .Select(r => r.Clip.Id).ToHashSet();

        Assert.Contains(ca[1].Id, ids);
        Assert.Contains(ca[2].Id, ids);
        Assert.Contains(cb[1].Id, ids);
        Assert.Contains(cb[2].Id, ids);
        Assert.DoesNotContain(ca[0].Id, ids);
        Assert.DoesNotContain(ca[3].Id, ids);
        Assert.DoesNotContain(cb[0].Id, ids);
        Assert.DoesNotContain(cb[3].Id, ids);
        Assert.False(ids.Contains(whole.Clips[0].Id),
            "a whole song on the track in between was swept up by two short pieces that merely overlap it — Delete would remove the entire song");
        Assert.Equal(4, ids.Count);
    }

    [Fact]
    public async Task RangeOnOneTrackLeavesOtherTracksAlone()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);
        var b = await FourSectionsAsync(s, _wav);
        var ca = InOrder(a);

        var range = SectionSelection.Range(s.Project.Tracks, a, ca[0], a, ca[3], s);

        Assert.Equal(4, range.Count);
        Assert.All(range, r => Assert.Same(a, r.Track));
        Assert.DoesNotContain(range, r => ReferenceEquals(r.Track, b));
    }

    [Fact]
    public async Task ShiftClickRangeLeavesSectionsWhoseFileIsMissingAlone()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);

        // A track whose song is gone, between the two: its section is not drawn and cannot be
        // clicked, so a range across it must not pick it up.
        string gone = Path.Combine(_dir, "gone.wav");
        var missing = new TrackModel { SourcePath = gone };
        var ghost = new ClipModel { SourcePath = gone, TrackStartSeconds = 15, SourceEndSeconds = -1 };
        missing.Clips.Add(ghost);
        s.Project.Tracks.Add(missing);

        var b = await FourSectionsAsync(s, _wav);
        var ca = InOrder(a);
        var cb = InOrder(b);
        Assert.Null(s.AudioFor(ghost));
        Assert.Equal(0, TimelineEditor.LengthFramesOf(ghost, s));

        var ids = SectionSelection.Range(s.Project.Tracks, a, ca[1], b, cb[2], s)   // 10–30 s, across the missing track
                                  .Select(r => r.Clip.Id).ToHashSet();

        Assert.False(ids.Contains(ghost.Id),
            "a section whose audio is missing was swept into the range — Delete would then erase the missing song's arrangement for good once the file is back");
        Assert.Equal(4, ids.Count);
        Assert.Contains(missing, s.Project.Tracks);
    }

    [Fact]
    public async Task DeleteAllRemovesEverySelectedSection_AndMovesNothingElse()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);
        var b = await FourSectionsAsync(s, _wav);
        var ca = InOrder(a);
        var cb = InOrder(b);
        double keptStart = ca[3].TrackStartSeconds;
        double keptSource = ca[3].SourceStartSeconds;

        var result = SectionSelection.DeleteAll(new[] { (a, ca[1]), (a, ca[2]), (b, cb[0]) });

        Assert.True(result.Changed);
        Assert.Contains("3 sections", result.Message);
        Assert.Equal(2, a.Clips.Count);
        Assert.Equal(3, b.Clips.Count);
        var kept = a.Clips.Single(c => c.Id == ca[3].Id);
        Assert.True(kept.TrackStartSeconds == keptStart && kept.SourceStartSeconds == keptSource,
            "deleting sections moved a section that was not selected — the gap must stay a gap, or the track falls out of time with the others");
    }

    [Fact]
    public void DeleteAllWithNothingSelectedChangesNothing()
    {
        var result = SectionSelection.DeleteAll(Array.Empty<(TrackModel, ClipModel)>());
        Assert.False(result.Changed);
    }

    // ---------------------------------------------------------------- several at once: copy, paste, repeat (2026-10-29)

    [Fact]
    public async Task PasteOfAGroupKeepsEachSectionsPlaceToTheFrame_AndLeavesWhatLiesUnderTheGapsAlone()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);          // [0,10) [10,20) [20,30) [30,60)
        var b = await FourSectionsAsync(s, _wav);
        var tracks = new List<TrackModel> { a, b };
        var ca = InOrder(a);
        long span = TimelineEditor.StartFrameOf(ca[2], s) - TimelineEditor.StartFrameOf(ca[0], s);

        var group = SectionSelection.Copy(tracks, new[] { (a, ca[0]), (a, ca[2]) }, s);
        Assert.Equal(2, group.Count);
        Assert.Single(group.Rows);
        var result = SectionSelection.Paste(tracks, b, group, 15.0, s);

        Assert.True(result.Changed, result.Message);
        Assert.Equal(2, result.Placed.Count);
        long first = TimelineEditor.StartFrameOf(result.Placed[0], s);
        Assert.Equal(TimelineEditor.TrackFrameAt(b, 15.0, s), first);
        Assert.True(TimelineEditor.StartFrameOf(result.Placed[1], s) - first == span,
            "the second pasted section did not keep its distance from the first to the frame — a loop copied as a group would come back with a gap and two declicks at the join");
        Assert.Equal(ca[0].SourceStartSeconds, result.Placed[0].SourceStartSeconds);
        Assert.Equal(ca[2].SourceStartSeconds, result.Placed[1].SourceStartSeconds);

        // b's [20,30) lost 20..25 to the first paste and [30,60) lost 35..45 to the second; between
        // the two pasted sections, 25..35, b is as it was: the gap in the group is not part of it.
        var under = InOrder(b).Where(x => TimelineEditor.StartFrameOf(x, s) >= TimelineEditor.EndFrameOf(result.Placed[0], s)
                                       && TimelineEditor.EndFrameOf(x, s) <= TimelineEditor.StartFrameOf(result.Placed[1], s)).ToList();
        Assert.True(under.Count == 2
                    && TimelineEditor.StartFrameOf(under[0], s) == TimelineEditor.EndFrameOf(result.Placed[0], s)
                    && TimelineEditor.EndFrameOf(under[1], s) == TimelineEditor.StartFrameOf(result.Placed[1], s),
            "pasting a group cleared what lay under the gap between its sections — a group is the sections, not the stretch they span; MARK MODE is for the stretch");
        Assert.Equal(4, a.Clips.Count);                     // the track copied from is untouched
    }

    [Fact]
    public async Task PasteAcrossTracksKeepsTheRows_AndRefusesWithoutRoomBelow()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);
        var b = await FourSectionsAsync(s, _wav);
        var c = await FourSectionsAsync(s, _wav);
        var tracks = new List<TrackModel> { a, b, c };
        var ca = InOrder(a);
        var cb = InOrder(b);

        // a's [10,20) and b's [20,30), given bottom first: two rows, a's on top, b's one below.
        var group = SectionSelection.Copy(tracks, new[] { (b, cb[2]), (a, ca[1]) }, s);
        Assert.Equal(2, group.Rows.Count);
        Assert.Equal(0, group.Rows[0].Offset);
        Assert.Equal(1, group.Rows[1].Offset);
        Assert.Equal(2, group.RowSpan);

        Assert.Equal(1, SectionSelection.MissingRows(tracks, c, group));
        Assert.False(SectionSelection.Paste(tracks, c, group, 5.0, s).Changed,
            "a paste whose lower row has no track to land on must refuse, not drop that row on the floor");

        var result = SectionSelection.Paste(tracks, b, group, 5.0, s);
        Assert.True(result.Changed, result.Message);
        var onB = result.Placed.Single(p => b.Clips.Contains(p));
        var onC = result.Placed.Single(p => c.Clips.Contains(p));
        Assert.Equal(TimelineEditor.TrackFrameAt(b, 5.0, s), TimelineEditor.StartFrameOf(onB, s));
        Assert.Equal(TimelineEditor.TrackFrameAt(c, 5.0, s) + (TimelineEditor.StartFrameOf(cb[2], s) - TimelineEditor.StartFrameOf(ca[1], s)),
                     TimelineEditor.StartFrameOf(onC, s));
        Assert.Equal(ca[1].SourceStartSeconds, onB.SourceStartSeconds);
        Assert.Equal(cb[2].SourceStartSeconds, onC.SourceStartSeconds);
        Assert.Equal(4, a.Clips.Count);
    }

    [Fact]
    public async Task RepeatOfAGroupStartsOnTheVeryFrameItEnds_AndKeepsTheJoinInsideIt()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);
        var tracks = new List<TrackModel> { a };
        var ca = InOrder(a);

        // [10,20) and [20,30), a seam between them as split. Repeated as a group they land at 30 and 40.
        var result = SectionSelection.Repeat(tracks, new[] { (a, ca[1]), (a, ca[2]) }, s);

        Assert.True(result.Changed, result.Message);
        Assert.Equal(2, result.Placed.Count);
        Assert.True(TimelineEditor.StartFrameOf(result.Placed[0], s) == TimelineEditor.EndFrameOf(ca[2], s),
            "the repeat did not start on the very frame the group ends: a group looped with Ctrl+D would have a gap, with a declick either side, at every repeat");
        Assert.True(TimelineEditor.StartFrameOf(result.Placed[1], s) == TimelineEditor.EndFrameOf(result.Placed[0], s),
            "the two copies no longer butt as the originals did");
        Assert.Equal(ca[1].SourceStartSeconds, result.Placed[0].SourceStartSeconds);
        Assert.Equal(result.Placed[0].SourceEndSeconds, result.Placed[1].SourceStartSeconds);   // still a seam: the song goes on
        Assert.Equal(6, a.Clips.Count);                     // the tail [30,60) is now [50,60)
        Assert.Equal(50.0, InOrder(a)[^1].TrackStartSeconds, 6);
    }

    // ---------------------------------------------------------------- the selection box (2026-10-11)

    private static HashSet<Guid> Ids(IEnumerable<(TrackModel Track, ClipModel Clip)> r) => r.Select(x => x.Clip.Id).ToHashSet();

    [Fact]
    public async Task ABoxTakesASectionItOnlyPartlyCovers()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await FourSectionsAsync(s, _wav);
        var c = InOrder(t);

        var ids = Ids(SectionSelection.Touching(s.Project.Tracks, 0, 0, 15, 25, s));   // half of [10,20), half of [20,30)
        Assert.True(ids.SetEquals(new[] { c[1].Id, c[2].Id }),
            $"a box over 15–25 s took {ids.Count} sections, not the two it half covers — it would not select what the eye sees it touching");
    }

    [Fact]
    public async Task ABoxTakesOnlyTheTracksItCovers()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);
        var b = await FourSectionsAsync(s, _wav);
        var c = await FourSectionsAsync(s, _wav);

        var range = SectionSelection.Touching(s.Project.Tracks, 1, 2, 12, 14, s);
        Assert.True(range.All(r => r.Track != a), "the box took a section from a track above the ones it covers");
        Assert.True(range.Count == 2 && range.Any(r => r.Track == b) && range.Any(r => r.Track == c),
            $"a box over two tracks took {range.Count} sections, not one on each");
    }

    [Fact]
    public async Task ABoxNeverTakesASectionWhoseFileIsMissing()
    {
        var s = ProjectSession.CreateOffline(Rate);
        await FourSectionsAsync(s, _wav);
        string gone = Path.Combine(_dir, "gone.wav");
        var missing = new TrackModel { SourcePath = gone };
        var ghost = new ClipModel { SourcePath = gone, TrackStartSeconds = 15, SourceEndSeconds = -1 };
        missing.Clips.Add(ghost);
        s.Project.Tracks.Add(missing);
        await FourSectionsAsync(s, _wav);

        var ids = Ids(SectionSelection.Touching(s.Project.Tracks, 0, 2, 0, 60, s));
        Assert.False(ids.Contains(ghost.Id),
            "a section whose audio is missing was taken by the box — Delete would then erase the missing song's arrangement for good once the file is back");
        Assert.Equal(8, ids.Count);
    }

    [Fact]
    public async Task ABoxEndingWhereASectionStartsDoesNotTakeIt_AndABoxOfNoWidthTakesTheOneUnderIt()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await FourSectionsAsync(s, _wav);
        var c = InOrder(t);

        var ending = Ids(SectionSelection.Touching(s.Project.Tracks, 0, 0, 5, 10, s));
        Assert.True(ending.SetEquals(new[] { c[0].Id }),
            "a box ending exactly where a section starts took it — the box would select a section it does not cover");

        var point = Ids(SectionSelection.Touching(s.Project.Tracks, 0, 0, 10, 10, s));
        Assert.True(point.SetEquals(new[] { c[1].Id }),
            "a box of no width at 10 s did not take the section starting there, the one under the pointer");
    }

    [Fact]
    public async Task ABoxGivesTheSameSectionsWhicheverWayItWasDragged()
    {
        var s = ProjectSession.CreateOffline(Rate);
        await FourSectionsAsync(s, _wav);
        await FourSectionsAsync(s, _wav);

        var down = Ids(SectionSelection.Touching(s.Project.Tracks, 0, 1, 12, 33, s));
        var up = Ids(SectionSelection.Touching(s.Project.Tracks, 1, 0, 33, 12, s));
        Assert.True(down.SetEquals(up) && down.Count == 6,
            $"dragged one way the box took {down.Count} sections, the other way {up.Count}");
    }

    [Fact]
    public async Task ABoxListsTheTopmostTrackFirstThenByTime()
    {
        // The first is the one commands act on after a box: it must be the top-left one.
        var s = ProjectSession.CreateOffline(Rate);
        var a = await FourSectionsAsync(s, _wav);
        var b = await FourSectionsAsync(s, _wav);

        var range = SectionSelection.Touching(s.Project.Tracks, 1, 0, 25, 5, s);
        Assert.True(range[0].Track == a && range[0].Clip.Id == InOrder(a)[0].Id,
            "the first section after a box is not the earliest on the topmost track, so Cut or Ctrl+D would act on another");
        Assert.True(range.Take(3).All(r => r.Track == a) && range.Skip(3).All(r => r.Track == b), "the box's sections are not listed track by track, top first");
    }
}
