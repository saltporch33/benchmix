using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Versions beside a project (Daniel: "auto-save + version history";
/// built 2026-10-29). Every Save keeps a dated copy, autosave keeps one every ten minutes while
/// edits go on, the list reads them back newest first, and a restore is a plain read of one. The
/// autosave itself lives in the view-model and is proved by <c>--buttontest</c>.
/// </summary>
public class VersionsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public VersionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-versions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "mix.benchmix");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static BenchmixProject Project(double bpm)
    {
        var p = new BenchmixProject { SampleRate = 48000 };
        p.ProjectBpm = bpm;
        return p;
    }

    [Fact]
    public void VersionsAreListedNewestFirst_WithTheTimeFromTheirNames()
    {
        ProjectStore.Save(Project(100), _path, "20261029-175821");
        ProjectStore.Save(Project(101), _path, "20261029-180300");
        ProjectStore.Save(Project(102), _path, "20261028-090000");
        File.WriteAllText(Path.Combine(_dir, "Backups", "mix-notastamp.benchmix"), "{}");   // a file that is not a version

        var versions = ProjectStore.Versions(_path);
        Assert.Equal(3, versions.Count);
        Assert.Equal(new DateTime(2026, 10, 29, 18, 3, 0), versions[0].Written);
        Assert.Equal(new DateTime(2026, 10, 29, 17, 58, 21), versions[1].Written);
        Assert.Equal(new DateTime(2026, 10, 28, 9, 0, 0), versions[2].Written);
        Assert.Equal(101, ProjectStore.Load(versions[0].Path).Project.ProjectBpm);
        Assert.Equal(100, ProjectStore.Load(versions[1].Path).Project.ProjectBpm);
    }

    [Fact]
    public void AVersionIsDueWithNoneYet_AndTenMinutesAfterTheLast()
    {
        var now = new DateTime(2026, 10, 29, 18, 0, 0);
        Assert.True(ProjectStore.VersionDue(_path, now), "a project with no versions is due one at its first autosave");
        ProjectStore.Save(Project(100), _path, ProjectStore.Stamp(now.AddMinutes(-5)));
        Assert.False(ProjectStore.VersionDue(_path, now),
            "five minutes after a version, an autosave keeps none: otherwise twenty edits would be twenty versions and the list would hold a minute of work");
        Assert.True(ProjectStore.VersionDue(_path, now.AddMinutes(5)), "ten minutes after the last version, the next autosave keeps one");
    }

    [Fact]
    public void OnlyTheNewestVersionsAreKept()
    {
        var start = new DateTime(2026, 10, 29, 8, 0, 0);
        for (int i = 0; i < ProjectStore.BackupsToKeep + 5; i++)
            ProjectStore.Save(Project(100 + i), _path, ProjectStore.Stamp(start.AddMinutes(i)));

        var versions = ProjectStore.Versions(_path);
        Assert.Equal(ProjectStore.BackupsToKeep, versions.Count);
        Assert.Equal(start.AddMinutes(ProjectStore.BackupsToKeep + 4), versions[0].Written);
        Assert.Equal(start.AddMinutes(5), versions[^1].Written);                 // the five oldest are gone
        Assert.Equal(40, ProjectStore.BackupsToKeep);                            // about a working day at one every ten minutes
    }

    [Fact]
    public void AVersionReadsBackAsTheProjectWasWhenItWasKept()
    {
        var p = Project(93);
        p.Tracks.Add(new TrackModel { Name = "Bed", SourcePath = @"C:\nowhere\bed.mp3" });
        ProjectStore.Save(p, _path, "20261029-170000");
        p.Tracks[0].Name = "Topper";
        p.ProjectBpm = 120;
        ProjectStore.Save(p, _path, "20261029-171000");

        var versions = ProjectStore.Versions(_path);
        var earlier = ProjectStore.Load(versions[1].Path).Project;
        Assert.Equal(93, earlier.ProjectBpm);
        Assert.Equal("Bed", earlier.Tracks[0].Name);
        var latest = ProjectStore.Load(versions[0].Path).Project;
        Assert.Equal(120, latest.ProjectBpm);
        Assert.Equal("Topper", latest.Tracks[0].Name);
        Assert.Equal(120, ProjectStore.Load(_path).Project.ProjectBpm);        // the project file itself is the newest state
    }
}
