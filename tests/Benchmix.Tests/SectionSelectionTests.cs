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
}
