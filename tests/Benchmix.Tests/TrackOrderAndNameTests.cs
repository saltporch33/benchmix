using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Renaming a track and moving it up or down the list (Daniel, 2026-10-11). Only the name, or only
/// the order, changes; these pin that nothing else does, and that both survive saving and undo like
/// any edit.
/// </summary>
public class TrackOrderAndNameTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;

    public TrackOrderAndNameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-tracks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "ramp.wav");

        int frames = Rate * 20;
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

    [Fact]
    public async Task ABlankNameIsRefusedAndTheOldNameKept()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        t.Name = "Bed";

        foreach (var blank in new[] { "", "   ", "\t\r\n", null })
        {
            var r = s.RenameTrack(t, blank);
            Assert.False(r.Changed);
            Assert.True(t.Name == "Bed", $"a blank name was saved (now '{t.Name}') — the track would come back as an unnamed lane");
        }
    }

    [Fact]
    public async Task ARenameKeepsWhatWasTypedWithoutTheSpacesAroundIt()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);

        var r = s.RenameTrack(t, "  Drums (from the bridge)\t");
        Assert.True(r.Changed, r.Message);
        Assert.True(t.Name == "Drums (from the bridge)", $"got '{t.Name}': the spaces typed around the name were kept, or the name inside was changed");

        s.RenameTrack(t, "Two\r\nlines");
        Assert.True(t.Name == "Two  lines", $"got '{t.Name}': a line break pasted into the name was kept, and would break the strip's one-line label");

        s.RenameTrack(t, new string('x', 200));
        Assert.True(t.Name.Length == ProjectSession.MaximumTrackNameLength,
            $"a 200-character name was kept at {t.Name.Length} characters, not cut at {ProjectSession.MaximumTrackNameLength}");
    }

    [Fact]
    public async Task RenamingToTheSameNameIsNotAChange()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        t.Name = "Bed";

        var r = s.RenameTrack(t, " Bed ");
        Assert.False(r.Changed, "renaming to the name it already had counted as a change, so it would cost a press of Ctrl+Z");
    }

    [Fact]
    public async Task ARenameOfATrackNoLongerInTheProjectChangesNothing()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        var gone = t.Clone();
        gone.Name = "Old";

        var r = s.RenameTrack(gone, "Late");
        Assert.False(r.Changed);
        Assert.True(gone.Name == "Old", "a track no longer in the project was renamed — a rename arriving after an undo would be written nowhere and look as if it worked");
    }

    [Fact]
    public async Task ARenamedTrackKeepsItsNameThroughSaveAndUndo()
    {
        var s = ProjectSession.CreateOffline(Rate);
        var t = await s.AddTrackAsync(_wav);
        string original = t.Name;

        var undo = new UndoStack();
        undo.Push(s.Project, "rename");
        Assert.True(s.RenameTrack(t, "Vocals").Changed);

        string path = Path.Combine(_dir, "renamed.benchmix");
        ProjectStore.Save(s.Project, path);
        var loaded = ProjectStore.Load(path).Project;
        Assert.True(loaded.Tracks[0].Name == "Vocals", $"the saved project reopened as '{loaded.Tracks[0].Name}': the rename was not saved");

        var back = undo.Undo(s.Project);
        Assert.True(back?.Tracks[0].Name == original, $"undo gave '{back?.Tracks[0].Name}', not '{original}': Ctrl+Z would not take the rename back");
    }

    [Fact]
    public async Task ARenamedEmptyTrackKeepsItsNameWhenASongIsPutOnIt()
    {
        // An empty track named "Track N" takes the name of the first song put on it. One he has
        // named himself must not be overwritten that way.
        var s = ProjectSession.CreateOffline(Rate);
        await s.AddTrackAsync(_wav);
        var empty = s.AddEmptyTrack();
        Assert.True(s.RenameTrack(empty, "Drums").Changed);

        var placed = await s.PlaceFileAsync(empty, _wav, 5);
        Assert.True(placed.Changed, placed.Message);
        Assert.True(empty.Name == "Drums", $"the track he named 'Drums' was renamed '{empty.Name}' when a song was put on it");
    }

    // ---------------------------------------------------------------- moving a track

    /// <summary>Another source, quiet enough that three summed stay well under full scale.</summary>
    private string Wav(string name, Func<int, float> sample)
    {
        int frames = Rate * 20;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) { float v = sample(f); s[f * 2] = v; s[f * 2 + 1] = v; }
        string path = Path.Combine(_dir, name + ".wav");
        AudioClip.FromSamples(name, s, Rate).SaveWav(path);
        return path;
    }

    /// <summary>Three tracks: the ramp (turned down), a 431 Hz tone, and seeded noise.</summary>
    private async Task<(ProjectSession S, TrackModel A, TrackModel B, TrackModel C)> ThreeTracksAsync()
    {
        var rnd = new Random(7);
        var s = ProjectSession.CreateOffline(Rate);
        var a = await s.AddTrackAsync(_wav);
        var b = await s.AddTrackAsync(Wav("tone", f => (float)(0.25 * Math.Sin(2 * Math.PI * 431 * f / Rate))));
        var c = await s.AddTrackAsync(Wav("noise", _ => (float)((rnd.NextDouble() * 2 - 1) * 0.2)));
        a.Gain = 0.3;
        s.ApplyAll();
        return (s, a, b, c);
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
    public async Task MovingATrackChangesItsPlaceInTheSavedProject()
    {
        var (s, a, b, c) = await ThreeTracksAsync();
        var r = s.MoveTrack(c, 0);
        Assert.True(r.Changed, r.Message);

        string path = Path.Combine(_dir, "moved.benchmix");
        ProjectStore.Save(s.Project, path);
        var order = ProjectStore.Load(path).Project.Tracks.Select(t => t.Id).ToList();
        Assert.True(order.SequenceEqual(new[] { c.Id, a.Id, b.Id }),
            "the saved project reopened with the tracks in their old order: the move was not saved");
    }

    [Fact]
    public async Task MovingATrackIsOneUndoStepThatPutsTheOrderBack()
    {
        var (s, a, b, c) = await ThreeTracksAsync();
        var undo = new UndoStack();
        undo.Push(s.Project, "move");
        Assert.True(s.MoveTrack(a, 2).Changed);
        Assert.True(s.Project.Tracks.Select(t => t.Id).SequenceEqual(new[] { b.Id, c.Id, a.Id }), "a track moved to the bottom did not land there");

        var back = undo.Undo(s.Project);
        Assert.True(back is not null && back.Tracks.Select(t => t.Id).SequenceEqual(new[] { a.Id, b.Id, c.Id }),
            "Ctrl+Z did not put the tracks back in their old order");
    }

    [Fact]
    public async Task MovingATrackToWhereItAlreadyIsChangesNothing()
    {
        var (s, a, b, c) = await ThreeTracksAsync();
        Assert.False(s.MoveTrack(b, 1).Changed, "moving a track onto its own place counted as a change, so it would cost a press of Ctrl+Z");
        Assert.False(s.MoveTrack(a, -5).Changed, "moving the top track further up counted as a change");
        Assert.True(s.MoveTrack(a, 99).Changed && s.Project.Tracks[^1] == a, "moving a track past the bottom did not put it at the bottom");
        Assert.False(s.MoveTrack(a.Clone(), 0).Changed, "a track no longer in the project was moved");
    }

    [Fact]
    public async Task AShiftClickRangeFollowsTheOrderOnScreenAfterAMove()
    {
        // Shift+click takes the tracks BETWEEN its two ends, and "between" is the order on screen.
        var (s, a, b, c) = await ThreeTracksAsync();
        foreach (var t in new[] { a, b, c })
            Assert.True(TimelineEditor.Split(t, 5, s).Changed && TimelineEditor.Split(t, 10, s).Changed);
        ClipModel Middle(TrackModel t) => t.Clips.OrderBy(x => x.TrackStartSeconds).ElementAt(1);

        bool TakesB() => SectionSelection.Range(s.Project.Tracks, a, Middle(a), c, Middle(c), s).Any(r => r.Track == b);
        Assert.True(TakesB(), "with the tracks in order A, B, C, a range from A to C should take B's section between them");

        Assert.True(s.MoveTrack(b, 0).Changed);
        Assert.False(TakesB(), "B was moved to the top, yet a range from A to C still took its section — Delete would remove a section from a track not between the two clicked");
    }

    [Fact]
    public async Task MovingATrackIsNotHeard()
    {
        // A move does not touch the engine, and voices are found by id, so what plays straight after
        // is the same, sample for sample. After a reload — every undo, redo and open rebuilds the
        // voices in the new order — the three are summed in a different order, which in floating
        // point can differ in the last bit and no further.
        var (s, a, b, c) = await ThreeTracksAsync();
        var before = Render(s, Rate * 2);

        Assert.True(s.MoveTrack(c, 0).Changed);
        var straightAfter = Render(s, Rate * 2);
        Assert.True(before.SequenceEqual(straightAfter), "moving a track changed what plays straight away — the engine should not have been touched");

        await s.OpenAsync(s.Project.Clone());
        var afterReload = Render(s, Rate * 2);
        float worst = 0;
        for (int i = 0; i < before.Length; i++) worst = Math.Max(worst, Math.Abs(before[i] - afterReload[i]));
        Assert.True(worst <= 2.4e-7f,
            $"after a reload the mix differs by {worst:E2} — more than float rounding of a three-way sum, so moving a track changed what is heard");
    }
}
