using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Splitting, cutting, copying and pasting sections.
///
/// The three rules being protected here are all consequences of this being a <b>mashup</b> tool:
/// nothing ripples, sections never overlap, and a split you do not follow up with anything changes
/// nothing at all.
/// </summary>
public class TimelineEditorTests : IDisposable
{
    const int Rate = 48000;
    private readonly string _dir;
    private readonly string _wav;

    public TimelineEditorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-editor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _wav = Path.Combine(_dir, "source.wav");

        // Sixty seconds of a ramp: a sample's value says where in the file it came from.
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

    private async Task<(ProjectSession Session, TrackModel Track)> OpenAsync()
    {
        var session = ProjectSession.CreateOffline(Rate);
        var track = await session.AddTrackAsync(_wav);
        return (session, track);
    }

    // ---------------------------------------------------------------- split

    [Fact]
    public async Task ANewTrackIsOneSectionCoveringTheWholeSong()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            Assert.Single(track.Clips);
            Assert.Equal(0, track.Clips[0].TrackStartSeconds);
            Assert.Equal(0, track.Clips[0].SourceStartSeconds);
            Assert.True(track.Clips[0].SourceEndSeconds < 0, "a whole song should not carry an end");
        }
    }

    [Fact]
    public async Task SplittingMakesTwoSectionsThatMeetExactlyWhereTheFirstStops()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            var result = TimelineEditor.Split(track, 20.0, session);
            Assert.True(result.Changed, result.Message);
            Assert.Equal(2, track.Clips.Count);

            var (first, second) = (track.Clips[0], track.Clips[1]);

            // Exact, not approximate: the engine recognises a seam by these two numbers matching,
            // and a near-miss would silently reintroduce a declick at a join meant to be inaudible.
            Assert.Equal(first.SourceEndSeconds, second.SourceStartSeconds);
            Assert.Equal(20.0, second.TrackStartSeconds, 6);
            Assert.Equal(0, second.FadeInSeconds);
            Assert.Equal(0, first.FadeOutSeconds);
        }
    }

    [Fact]
    public async Task SplittingRefusesAtTheVeryEdgeRatherThanMakingASliverNobodyCanSee()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            var result = TimelineEditor.Split(track, 0.001, session);
            Assert.False(result.Changed);
            Assert.Single(track.Clips);
        }
    }

    [Fact]
    public async Task SplittingInAGapDoesNothingAndSaysSo()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            track.Clips[0].SourceEndSeconds = 10;
            var result = TimelineEditor.Split(track, 30.0, session);
            Assert.False(result.Changed);
            Assert.Contains("nothing to split", result.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- delete and cut

    [Fact]
    public async Task DeletingASectionLeavesAHoleAndMovesNothingElse()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(track, 20.0, session);
            TimelineEditor.Split(track, 30.0, session);
            Assert.Equal(3, track.Clips.Count);

            double thirdStart = track.Clips[2].TrackStartSeconds;
            TimelineEditor.Delete(track, track.Clips[1]);

            Assert.Equal(2, track.Clips.Count);
            // The section after the hole must not have shuffled back into it. Rippling would pull
            // this track out of line with every other one in the mashup.
            Assert.Equal(thirdStart, track.Clips[1].TrackStartSeconds, 6);
            Assert.Equal(30.0, track.Clips[1].TrackStartSeconds, 6);
        }
    }

    // ---------------------------------------------------------------- paste and duplicate

    [Fact]
    public async Task PastingOverwritesWhatIsUnderItRatherThanStackingOnTop()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(track, 10.0, session);
            var lifted = TimelineEditor.Copy(track.Clips[0]);     // the first ten seconds

            var result = TimelineEditor.Paste(track, lifted, 30.0, session);
            Assert.True(result.Changed, result.Message);

            // Nothing may overlap: every section must start at or after the one before it ends.
            for (int i = 0; i + 1 < track.Clips.Count; i++)
            {
                double end = track.Clips[i].TrackStartSeconds
                           + TimelineEditor.LengthOf(track.Clips[i], session);
                Assert.True(track.Clips[i + 1].TrackStartSeconds >= end - 1e-6,
                    $"section {i + 1} starts at {track.Clips[i + 1].TrackStartSeconds:0.000} "
                  + $"but section {i} runs to {end:0.000}");
            }
        }
    }

    [Fact]
    public async Task DuplicatingPutsTheSameAudioImmediatelyAfterItself()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(track, 8.0, session);
            var section = track.Clips[0];
            double length = TimelineEditor.LengthOf(section, session);

            var result = TimelineEditor.Duplicate(track, section, session);
            Assert.True(result.Changed, result.Message);

            var copy = result.Clip!;
            Assert.Equal(length, copy.TrackStartSeconds, 6);
            Assert.Equal(section.SourceStartSeconds, copy.SourceStartSeconds, 6);
            Assert.Equal(section.SourceEndSeconds, copy.SourceEndSeconds, 6);
        }
    }

    [Fact]
    public async Task RepeatedDuplicationBuildsALoopWithoutOverlapOrGap()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(track, 4.0, session);
            var section = track.Clips[0];
            double length = TimelineEditor.LengthOf(section, session);

            var last = section;
            for (int i = 0; i < 6; i++)
                last = TimelineEditor.Duplicate(track, last, session).Clip!;

            // Seven copies, butted end to end, starting at 0.
            var loop = track.Clips.Where(c => Math.Abs(TimelineEditor.LengthOf(c, session) - length) < 1e-6)
                                  .OrderBy(c => c.TrackStartSeconds)
                                  .ToList();
            Assert.True(loop.Count >= 7, $"expected at least 7 repeats, found {loop.Count}");
            for (int i = 0; i < 7; i++)
                Assert.Equal(i * length, loop[i].TrackStartSeconds, 5);
        }
    }

    [Fact]
    public async Task PastingFromAnotherSongSaysThatItTakesThisTracksControls()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            var foreign = new ClipModel
            {
                SourcePath = _wav,
                SourceStartSeconds = 1,
                SourceEndSeconds = 3,
            };
            track.SourcePath = "some other song.mp3";

            var result = TimelineEditor.Paste(track, foreign, 5.0, session);
            Assert.True(result.Changed);
            Assert.Contains("tempo, pitch and EQ", result.Message);
        }
    }

    // ---------------------------------------------------------------- undo and persistence

    [Fact]
    public async Task UndoRestoresTheSectionsAndDoesNotShareThemWithThePresent()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            var undo = new UndoStack();
            undo.Push(session.Project, "split");

            TimelineEditor.Split(track, 15.0, session);
            TimelineEditor.Split(track, 25.0, session);
            Assert.Equal(3, track.Clips.Count);

            var restored = undo.Undo(session.Project)!;

            // The snapshot was taken when the track had one section. If Clone had copied the list
            // by reference the snapshot would have tracked the edits and this would read 3.
            Assert.Single(restored.Tracks[0].Clips);
        }
    }

    [Fact]
    public async Task AProjectWithSectionsSurvivesSaveAndLoad()
    {
        var (session, track) = await OpenAsync();
        using (session)
        {
            TimelineEditor.Split(track, 12.0, session);
            TimelineEditor.Duplicate(track, track.Clips[0], session);

            var path = Path.Combine(_dir, "p.benchmix");
            ProjectStore.Save(session.Project, path);
            var reloaded = ProjectStore.Load(path).Project;

            Assert.Equal(BenchmixProject.CurrentSchemaVersion, reloaded.SchemaVersion);
            Assert.Equal(track.Clips.Count, reloaded.Tracks[0].Clips.Count);
            for (int i = 0; i < track.Clips.Count; i++)
            {
                Assert.Equal(track.Clips[i].TrackStartSeconds, reloaded.Tracks[0].Clips[i].TrackStartSeconds, 6);
                Assert.Equal(track.Clips[i].SourceStartSeconds, reloaded.Tracks[0].Clips[i].SourceStartSeconds, 6);
            }
        }
    }

    /// <summary>
    /// A real version 1 project, opened.
    ///
    /// <para>
    /// A hand-written file proves the arithmetic; this proves it against a mix somebody actually
    /// built and would notice being wrong. It is a committed fixture rather than a path into
    /// Daniel's working folder: the first version of this test pinned the live file, and it broke
    /// the moment he opened Benchmix and edited it. A test that fails because the user did some
    /// work is a bad test, and worse, the obvious repair — pasting the new numbers into the
    /// assertions — would have quietly destroyed the only evidence that the migration works.
    /// </para>
    /// </summary>
    [Fact]
    public void ARealVersionOneProjectOpensWithItsMixIntact()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "khaled-tyga-v1.benchmix");
        Assert.True(File.Exists(path), $"the fixture is missing from the build output: {path}");

        var project = ProjectStore.Load(path).Project;

        Assert.Equal(BenchmixProject.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Equal(3, project.Tracks.Count);

        // Three tracks, each one uninterrupted section, exactly where they were.
        foreach (var t in project.Tracks)
        {
            Assert.Single(t.Clips);
            Assert.Equal(0, t.Clips[0].TrackStartSeconds);
            Assert.Null(t.SourceStartSeconds);          // the superseded field is gone, not zeroed
            Assert.Equal(t.SourcePath, t.Clips[0].SourcePath);
        }

        // The second Taste section: 206.54 to 228.60 of the song, dropped at 215.42 with its bass
        // killed. If the migration moved any of these, the mix would come out wrong.
        var taste2 = project.Tracks[2];
        Assert.Equal(215.42170641363907, taste2.TimelineOffsetSeconds, 9);
        Assert.Equal(206.53857868654737, taste2.Clips[0].SourceStartSeconds, 9);
        Assert.Equal(228.60413797342935, taste2.Clips[0].SourceEndSeconds, 9);
        Assert.True(taste2.EqLow <= 0.0001, $"the killed LOW came back as {taste2.EqLow}");

        // Khalid runs to the end of its file, which version 1 spelled as -1 and so does version 2.
        Assert.True(project.Tracks[1].Clips[0].SourceEndSeconds < 0);
    }

    [Fact]
    public void AVersionOneProjectMigratesToOneSectionCoveringWhatItCovered()
    {
        // Written by hand in the old shape, because that is what is on disk in real projects.
        var path = Path.Combine(_dir, "v1.benchmix");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "name": "old",
          "sampleRate": 48000,
          "masterGain": 1,
          "projectBpm": 98.54,
          "tracks": [
            {
              "id": "537c86f0-2e51-4596-a3fc-972ccae167f9",
              "name": "Taste",
              "sourcePath": "C:\\music\\taste.mp3",
              "timelineOffsetSeconds": 0,
              "sourceStartSeconds": 0.907236846880194,
              "sourceEndSeconds": 207.2683018383023,
              "speed": 1,
              "fadeInSeconds": 0,
              "fadeOutSeconds": 0,
              "gain": 1
            }
          ]
        }
        """);

        var loaded = ProjectStore.Load(path).Project;
        var t = loaded.Tracks[0];

        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Single(t.Clips);
        Assert.Equal(0, t.Clips[0].TrackStartSeconds);
        Assert.Equal(0.907236846880194, t.Clips[0].SourceStartSeconds, 9);
        Assert.Equal(207.2683018383023, t.Clips[0].SourceEndSeconds, 9);
        Assert.Equal("C:\\music\\taste.mp3", t.Clips[0].SourcePath);

        // The track's own origin is untouched, so the section lands exactly where it used to.
        Assert.Equal(0, t.TimelineOffsetSeconds);

        // And the superseded fields are gone rather than written back out as zeros.
        Assert.Null(t.SourceStartSeconds);
        Assert.Null(t.SourceEndSeconds);
    }
}
