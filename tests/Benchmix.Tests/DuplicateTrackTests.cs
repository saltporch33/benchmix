using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Duplicate track (Daniel, 2026-10-27: "duplicate a track with the existing clips on it"). The
/// copy sits right below the original with the same sections to the frame, the same knobs, tempo
/// ramps and warp markers, under its own name; it plays the same audio at the same moments — the
/// mix with both is exactly twice the mix with one; and editing the copy leaves the original
/// alone. The song is a ramp, so a rendered sample says where in the song it came from.
/// </summary>
public class DuplicateTrackTests : IDisposable
{
    const int Rate = 48000;
    const int SongFrames = Rate * 60;
    private readonly string _dir;
    private readonly string _wav;

    public DuplicateTrackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-duplicate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");
        var s = new float[SongFrames * 2];
        for (int f = 0; f < SongFrames; f++) s[f * 2] = s[f * 2 + 1] = f / (float)SongFrames;
        AudioClip.FromSamples("ramp", s, Rate).SaveWav(_wav);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static long Start(ClipModel c, ProjectSession s) => TimelineEditor.StartFrameOf(c, s);
    static long End(ClipModel c, ProjectSession s) => TimelineEditor.EndFrameOf(c, s);
    static List<ClipModel> Ordered(TrackModel t, ProjectSession s) => t.Clips.OrderBy(c => Start(c, s)).ToList();

    /// <summary>A track worth copying: three pieces, one moved away, a knob or two turned, a ramp and a marker.</summary>
    async Task<(ProjectSession s, TrackModel t)> ArrangedAsync()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        t.Name = "Bed";
        Assert.True(TimelineEditor.Split(t, 20, s).Changed && TimelineEditor.Split(t, 40, s).Changed);
        var third = Ordered(t, s)[2];
        var move = new SectionMove(s, t, third);
        move.DragTo(t, 45, SnapSettings.Off);
        move.Commit();
        t.EqLow = 0.5;
        t.Gain = 0.8;
        t.Pan = -0.25;
        t.Semitones = 2;
        t.Warps.Add(new WarpMarker(30, 30));             // placed, never moved: changes nothing
        s.ApplyAll();
        return (s, t);
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
    public async Task ADuplicateSitsRightBelowWithTheSameSectionsAndSettings()
    {
        var (s, t) = await ArrangedAsync();
        using (s)
        {
            Assert.True(TempoRamps.Add(t, 50, 55, 1.1, s).Changed);
            var (copy, result) = s.DuplicateTrack(t);
            Assert.True(result.Changed && copy is not null, result.Message);

            Assert.True(s.Project.Tracks.IndexOf(copy!) == s.Project.Tracks.IndexOf(t) + 1, "the copy is not right below the original");
            Assert.True(copy!.Id != t.Id, "the copy shares the original's id: the project would hold two tracks that are one");
            Assert.Equal("Bed copy", copy.Name);

            var mine = Ordered(t, s);
            var theirs = Ordered(copy, s);
            Assert.True(theirs.Count == mine.Count, $"the copy has {theirs.Count} sections, the original {mine.Count}");
            for (int i = 0; i < mine.Count; i++)
            {
                Assert.True(Start(theirs[i], s) == Start(mine[i], s) && End(theirs[i], s) == End(mine[i], s)
                            && TimelineEditor.SourceStartFrameOf(theirs[i], s) == TimelineEditor.SourceStartFrameOf(mine[i], s),
                    $"section {i} of the copy is not at the original's frames, or does not play the same part of the song");
                Assert.True(theirs[i].Id != mine[i].Id, $"section {i} of the copy shares its id with the original's");
                Assert.True(!ReferenceEquals(theirs[i], mine[i]), $"section {i} IS the original's: editing one would edit both");
            }

            Assert.True(copy.Speed == t.Speed && copy.Semitones == t.Semitones && copy.Mode == t.Mode
                        && copy.EqLow == t.EqLow && copy.EqMid == t.EqMid && copy.EqHigh == t.EqHigh
                        && copy.Gain == t.Gain && copy.Pan == t.Pan && copy.Muted == t.Muted && copy.Soloed == t.Soloed
                        && copy.PreserveFormants == t.PreserveFormants && copy.TimelineOffsetSeconds == t.TimelineOffsetSeconds
                        && copy.Bpm == t.Bpm && copy.SourcePath == t.SourcePath,
                "the copy's knobs or settings differ from the original's");
            Assert.True(copy.Ramps.SequenceEqual(t.Ramps) && !ReferenceEquals(copy.Ramps, t.Ramps), "the copy's tempo ramps are not the original's, or are the same list");
            Assert.True(copy.Warps.SequenceEqual(t.Warps) && !ReferenceEquals(copy.Warps, t.Warps), "the copy's warp markers are not the original's, or are the same list");
            Assert.True(s.HasAllAudio(copy) && s.VoiceFor(copy) is not null, "the copy has no voice or no audio: it would be silent, or show as missing");
            Assert.Contains("Duplicated Bed as \"Bed copy\"", result.Message);
            Assert.Contains("tempo ramps", result.Message);
            Assert.Contains("warp markers", result.Message);
        }
    }

    [Fact]
    public async Task ADuplicatePlaysTheSameAudioAtTheSameMoments()
    {
        var (s, t) = await ArrangedAsync();
        using (s)
        {
            var alone = Render(s, Rate * 50);
            Assert.True(s.DuplicateTrack(t).Result.Changed);
            var both = Render(s, Rate * 50);
            for (int i = 0; i < alone.Length; i++)
                if (both[i] != 2 * alone[i])
                    Assert.Fail($"with the copy in the mix, sample {i / 2} is {both[i]} where it should be exactly twice {alone[i]}: the copy is not playing the same audio at the same moment");
        }
    }

    [Fact]
    public async Task EditingTheCopyLeavesTheOriginalAlone()
    {
        var (s, t) = await ArrangedAsync();
        using (s)
        {
            var alone = Render(s, Rate * 50);
            var frames = Ordered(t, s).Select(c => (Start(c, s), End(c, s))).ToList();
            var (copy, _) = s.DuplicateTrack(t);

            var piece = Ordered(copy!, s)[1];
            var move = new SectionMove(s, copy!, piece);
            move.DragTo(copy!, 52, SnapSettings.Off);
            move.Commit();
            copy!.EqLow = 1.0;
            copy.Muted = true;                                // so what is left in the mix is the original alone
            s.ApplyAll();

            Assert.True(Ordered(t, s).Select(c => (Start(c, s), End(c, s))).SequenceEqual(frames), "moving a section on the copy moved one on the original");
            Assert.True(t.EqLow == 0.5, "turning the copy's knob turned the original's");
            var again = Render(s, Rate * 50);
            Assert.True(again.AsSpan().SequenceEqual(alone), "after editing the copy, the original no longer plays as it did");
        }
    }

    [Fact]
    public async Task ADuplicatesNameIsNumberedWhenTaken()
    {
        var (s, t) = await ArrangedAsync();
        using (s)
        {
            var (first, _) = s.DuplicateTrack(t);
            var (second, _) = s.DuplicateTrack(t);
            var (ofCopy, _) = s.DuplicateTrack(first!);
            Assert.Equal("Bed copy", first!.Name);
            Assert.Equal("Bed copy 2", second!.Name);
            Assert.Equal("Bed copy copy", ofCopy!.Name);
            Assert.True(s.Project.Tracks.IndexOf(ofCopy) == s.Project.Tracks.IndexOf(first) + 1, "the copy of a copy is not right below it");
        }
    }

    [Fact]
    public async Task ADuplicateSurvivesSaveAndReopenAsItsOwnTrack()
    {
        var (s, t) = await ArrangedAsync();
        using (s)
        {
            var (copy, _) = s.DuplicateTrack(t);
            string path = Path.Combine(_dir, "dup.benchmix");
            ProjectStore.Save(s.Project, path);
            var loaded = ProjectStore.Load(path).Project;
            Assert.Equal(2, loaded.Tracks.Count);
            Assert.True(loaded.Tracks[1].Id == copy!.Id && loaded.Tracks[1].Name == "Bed copy" && loaded.Tracks[1].Clips.Count == copy.Clips.Count,
                "reopened, the copy is not there as its own track with its sections");
            Assert.True(loaded.Tracks[0].Id == t.Id && loaded.Tracks[0].Clips.Count == t.Clips.Count, "reopened, the original is not as it was");
        }
    }
}
