using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// A project follows its songs (Daniel, 2026-10-28, after moving a project and its mp3s to another
/// folder and getting a lane of "Missing:" lines): "Any way we can make these follow each other?
/// like, it looks for the source files in current folder project .benchmix file is located? …
/// if I ever rename the source sound files, would i still get this message even if we resolve the
/// folder location following issue?" Three answers, tested here: a song that is not where the
/// project says is looked for next to the project, then in its subfolders, then — renamed — by
/// its fingerprint; and Find missing file points at it anywhere, for every section cut from it.
///
/// <para>
/// The files are real: small WAVs written to a temp folder, because the whole point is what sits on
/// disk where. Each fixture file is a ramp of its own slope, so two songs never share a fingerprint.
/// </para>
/// </summary>
public class RelinkTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;

    public RelinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-relink", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A two-second ramp whose slope tells one song from another, written where asked.</summary>
    string Song(string folder, string name, float slope)
    {
        Directory.CreateDirectory(folder);
        int frames = Rate * 2;
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++) s[f * 2] = s[f * 2 + 1] = slope * f / frames;
        string path = Path.Combine(folder, name);
        AudioClip.FromSamples(name, s, Rate).SaveWav(path);
        return path;
    }

    /// <summary>A project whose two tracks point at the songs where they were first added.</summary>
    static BenchmixProject ProjectUsing(string bed, string topper)
    {
        var p = new BenchmixProject();
        p.Tracks.Add(new TrackModel { Name = "Bed", SourcePath = bed, Clips = { new ClipModel { SourcePath = bed } } });
        p.Tracks.Add(new TrackModel { Name = "Topper", SourcePath = topper, Clips = { new ClipModel { SourcePath = topper } } });
        SourceRelinker.Remember(p, bed);
        SourceRelinker.Remember(p, topper);
        return p;
    }

    static IEnumerable<string> PathsIn(BenchmixProject p) => p.AllTracks.SelectMany(ProjectSession.SourcePathsOf);

    [Fact]
    public void AMovedProjectFindsItsSongsNextToIt()
    {
        // Added from "old", then the project and both songs moved together to "new": "old" is gone.
        string old = Path.Combine(_dir, "old"), @new = Path.Combine(_dir, "new");
        var p = ProjectUsing(Song(old, "bed.wav", 1f), Song(old, "topper.wav", 0.5f));
        File.Move(Path.Combine(old, "bed.wav"), Path.Combine(@new, "bed.wav").Also(f => Directory.CreateDirectory(Path.GetDirectoryName(f)!)));
        File.Move(Path.Combine(old, "topper.wav"), Path.Combine(@new, "topper.wav"));
        string project = Path.Combine(@new, "mix.benchmix");

        var relinks = SourceRelinker.Resolve(p, project);

        Assert.True(relinks.Count == 2 && relinks.All(r => r.How == SourceRelinker.NextToTheProject),
            $"moving a project with its songs left {2 - relinks.Count} of them missing: the lanes would read Missing: for files sitting right next to the project");
        Assert.True(PathsIn(p).All(File.Exists) && PathsIn(p).All(q => q.StartsWith(@new, StringComparison.OrdinalIgnoreCase)),
            "the project's paths were not moved to where the songs now are, so the next open would hunt again — and Save would write the old places");
        Assert.True(p.Tracks[0].Clips[0].SourcePath == p.Tracks[0].SourcePath, "a track and its section point at different files after a relink");
    }

    [Fact]
    public void ASongInASubfolderOfTheProjectIsFoundToo()
    {
        string old = Path.Combine(_dir, "old"), @new = Path.Combine(_dir, "new");
        var p = ProjectUsing(Song(old, "bed.wav", 1f), Song(old, "topper.wav", 0.5f));
        Directory.CreateDirectory(Path.Combine(@new, "audio"));
        File.Move(Path.Combine(old, "bed.wav"), Path.Combine(@new, "audio", "bed.wav"));
        File.Move(Path.Combine(old, "topper.wav"), Path.Combine(@new, "audio", "topper.wav"));

        var relinks = SourceRelinker.Resolve(p, Path.Combine(@new, "mix.benchmix"));

        Assert.True(relinks.Count == 2 && PathsIn(p).All(File.Exists),
            "songs kept in a folder beside the project were not found: a project with its own audio folder would open empty after a move");
    }

    [Fact]
    public void ARenamedSongIsFoundByItsFingerprint()
    {
        string folder = Path.Combine(_dir, "mix");
        string bed = Song(folder, "bed.wav", 1f), topper = Song(folder, "topper.wav", 0.5f);
        var p = ProjectUsing(bed, topper);
        File.Move(bed, Path.Combine(folder, "boston garden ambience (final).wav"));

        var relinks = SourceRelinker.Resolve(p, Path.Combine(folder, "mix.benchmix"));

        Assert.True(relinks.Count == 1 && relinks[0].How == SourceRelinker.ByFingerprint
                    && p.Tracks[0].SourcePath.EndsWith("boston garden ambience (final).wav"),
            "a renamed song next to the project was not recognised by its fingerprint: his question — 'if I ever rename the source sound files' — would still be a Missing: lane");
        Assert.True(p.Tracks[1].SourcePath == topper, "the song that had not moved was touched");
        Assert.True(p.SourceFingerprints.ContainsKey(p.Tracks[0].SourcePath) && !p.SourceFingerprints.ContainsKey(bed),
            "the fingerprint did not follow the song to its new name");
    }

    [Fact]
    public void ADifferentSongUnderTheOldNameIsNotMistakenForIt_WhenTheNameFailsFingerprintDecides()
    {
        // The topper was renamed; another file with the topper's old name is NOT lying around, so
        // the search by name finds nothing and the fingerprint must do the work — and must not pick
        // the bed, whose content differs.
        string folder = Path.Combine(_dir, "mix");
        string bed = Song(folder, "bed.wav", 1f), topper = Song(folder, "topper.wav", 0.5f);
        var p = ProjectUsing(bed, topper);
        File.Move(topper, Path.Combine(folder, "renamed.wav"));
        Song(folder, "decoy.wav", 0.25f);                              // same size, different content

        var relinks = SourceRelinker.Resolve(p, Path.Combine(folder, "mix.benchmix"));

        Assert.True(relinks.Count == 1 && p.Tracks[1].SourcePath.EndsWith("renamed.wav"),
            $"the renamed topper was relinked to '{Path.GetFileName(p.Tracks[1].SourcePath)}', not to renamed.wav — a song of the same size with other content was taken for it");
    }

    [Fact]
    public void ASongThatIsWhereTheProjectSaysIsLeftAlone()
    {
        string here = Path.Combine(_dir, "here"), there = Path.Combine(_dir, "there");
        string bed = Song(here, "bed.wav", 1f), topper = Song(here, "topper.wav", 0.5f);
        var p = ProjectUsing(bed, topper);
        Song(there, "bed.wav", 0.25f);                                 // a same-named file next to the project

        var relinks = SourceRelinker.Resolve(p, Path.Combine(there, "mix.benchmix"));

        Assert.True(relinks.Count == 0 && p.Tracks[0].SourcePath == bed,
            "a song that still exists where the project says was swapped for a same-named file next to the project");
    }

    [Fact]
    public void FindMissingFileRelinksEverySectionFromThatSongOnEveryTab()
    {
        string folder = Path.Combine(_dir, "mix");
        string bed = Song(folder, "bed.wav", 1f), topper = Song(folder, "topper.wav", 0.5f);
        var p = ProjectUsing(bed, topper);
        // A section of the bed pasted onto the topper's track, and a second tab with the bed too.
        p.Tracks[1].Clips.Add(new ClipModel { SourcePath = bed, TrackStartSeconds = 1 });
        p.Tabs.Add(new TabModel { Name = "Bites", Tracks = { new TrackModel { Name = "Bed bite", SourcePath = bed, Clips = { new ClipModel { SourcePath = bed } } } } });
        string moved = Path.Combine(_dir, "elsewhere", "the bed.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(bed, moved);

        int changed = SourceRelinker.Relink(p, bed, moved);

        Assert.True(changed == 5, $"Find missing file changed {changed} places, not 5 (two tracks' songs and three sections): some sections would still read from the old path");
        Assert.True(PathsIn(p).All(File.Exists), "a section still points at the missing file after Find missing file");
        Assert.True(p.SourceFingerprints.ContainsKey(moved), "the found file's fingerprint was not remembered, so the next rename would not be recognised");
    }

    [Fact]
    public async Task TheFingerprintIsRememberedWhenASongIsAddedAndSurvivesSaveAndReopen()
    {
        string folder = Path.Combine(_dir, "mix");
        string bed = Song(folder, "bed.wav", 1f);
        var s = ProjectSession.CreateOffline(Rate);
        await s.AddTrackAsync(bed, 0);

        Assert.True(s.Project.SourceFingerprints.TryGetValue(bed, out var print) && print == SourceRelinker.Fingerprint(bed),
            "adding a song did not remember its fingerprint, so a rename could never be recognised");

        string project = Path.Combine(folder, "mix.benchmix");
        ProjectStore.Save(s.Project, project);
        var loaded = ProjectStore.Load(project).Project;
        Assert.True(loaded.SourceFingerprints.TryGetValue(bed, out var back) && back == print,
            "the fingerprint did not survive a save and reopen");
        Assert.True(loaded.SchemaVersion == 6, "remembering fingerprints changed the file's version: an older Benchmix would refuse the file for a lookup table it could simply ignore");
    }

    [Fact]
    public void AProjectWithNothingMissingNeedsNothing()
    {
        string folder = Path.Combine(_dir, "mix");
        var p = ProjectUsing(Song(folder, "bed.wav", 1f), Song(folder, "topper.wav", 0.5f));
        Assert.Empty(SourceRelinker.Resolve(p, Path.Combine(folder, "mix.benchmix")));
    }
}

static class RelinkTestExtensions
{
    /// <summary>Runs <paramref name="act"/> on a value and hands the value back — to make a folder before a move into it.</summary>
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}
