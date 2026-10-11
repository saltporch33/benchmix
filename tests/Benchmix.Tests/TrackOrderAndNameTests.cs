using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Renaming a track (Daniel, 2026-10-11). The name is the only thing that changes; these pin that
/// a bad name never replaces a good one and that a rename survives saving and undo like any edit.
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
}
