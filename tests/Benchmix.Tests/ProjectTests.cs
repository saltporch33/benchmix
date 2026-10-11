using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

public class KeyTests
{
    [Theory]
    [InlineData("F#m", "F#m")]
    [InlineData("Gbm", "F#m")]
    [InlineData("1A", "G#m")]
    [InlineData("8B", "C")]
    [InlineData("Ebm", "Ebm")]
    [InlineData("D#m", "Ebm")]
    [InlineData("A minor", "Am")]
    [InlineData("  Bb  ", "Bb")]
    public void NormalisesTagSpellings(string tag, string expected)
        => Assert.Equal(expected, Keys.Normalize(tag));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("H")]
    [InlineData("13A")]
    [InlineData("0A")]
    [InlineData("not a key")]
    public void RejectsNonsense(string tag) => Assert.Null(Keys.Normalize(tag));

    [Fact]
    public void CamelotRoundTrips()
    {
        Assert.Equal("8A", Keys.ToCamelot("Am"));
        Assert.Equal("8B", Keys.ToCamelot("C"));
        Assert.Equal("1A", Keys.ToCamelot("G#m"));
    }

    [Fact]
    public void RelativeMajorMinorNeedsNoShift()
    {
        // A minor and C major share a pitch-class set: shifting either would be wrong.
        Assert.Equal(0, Keys.SemitonesBetween("Am", "C"));
        Assert.True(Keys.AreCompatible("Am", "C"));
    }

    [Fact]
    public void SameKeyNeedsNoShift() => Assert.Equal(0, Keys.SemitonesBetween("G#m", "G#m"));

    [Fact]
    public void ShiftIsTheShortWayRound()
    {
        // Am (8A) to Bm (10A) is two steps on the wheel = +2 semitones, not -10.
        Assert.Equal(2, Keys.SemitonesBetween("Am", "Bm"));
        Assert.Equal(-2, Keys.SemitonesBetween("Bm", "Am"));

        foreach (var a in Keys.Names)
            foreach (var b in Keys.Names)
            {
                var s = Keys.SemitonesBetween(a + "m", b + "m");
                Assert.NotNull(s);
                Assert.InRange(s!.Value, -6, 5);
            }
    }

    [Fact]
    public void NeighboursOnTheWheelAreCompatible()
    {
        Assert.True(Keys.AreCompatible("Am", "Em"));       // 8A and 9A
        Assert.False(Keys.AreCompatible("Am", "Ebm"));     // 8A and 2A, across the wheel
    }

    [Fact]
    public void UnknownKeysDoNotThrow()
    {
        Assert.Null(Keys.SemitonesBetween(null, "Am"));
        Assert.Null(Keys.SemitonesBetween("Am", "zzz"));
        Assert.False(Keys.AreCompatible(null, null));
        Assert.Equal("key unknown", Keys.Describe(null, "Am"));
    }
}

public class TempoMathTests
{
    [Fact]
    public void SpeedToMatchScalesCorrectly()
    {
        Assert.Equal(93.0 / 102.0, Analyzer.SpeedToMatch(102, 93), 10);
        Assert.Equal(1.0, Analyzer.SpeedToMatch(120, 120), 10);
        Assert.Equal(1.0, Analyzer.SpeedToMatch(0, 120));      // unknown tempo changes nothing
    }

    [Fact]
    public void FoldsHalfAndDoubleTimeTowardTheReference()
    {
        Assert.Equal(140, Analyzer.FoldToNearestOctave(70, 128), 6);     // half-time reading
        Assert.Equal(87.5, Analyzer.FoldToNearestOctave(175, 90), 6);    // double-time reading
        Assert.Equal(93, Analyzer.FoldToNearestOctave(93, 102), 6);      // already close: leave it
    }

    [Fact]
    public void FoldingStaysInsideAMusicalRange()
    {
        // Nothing should fold to 15 BPM or 500 BPM even when the reference is extreme.
        for (double bpm = 60; bpm <= 200; bpm += 7)
        {
            double folded = Analyzer.FoldToNearestOctave(bpm, 128);
            Assert.InRange(folded, 40, 260);
        }
    }
}

public class ProjectStoreTests
{
    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "benchmix-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static BenchmixProject SampleProject() => new()
    {
        Name = "Test",
        ProjectBpm = 93,
        MasterGain = 0.9,
        LoopStartSeconds = 4,
        LoopEndSeconds = 20,
        Tracks =
        {
            new TrackModel
            {
                Name = "Bed", SourcePath = @"C:\music\bed.mp3", Bpm = 93, Key = "G#m", Camelot = "1A",
                Speed = 1.0, EqLow = 1.0, TimelineOffsetSeconds = 0,
            },
            new TrackModel
            {
                Name = "Topper", SourcePath = @"C:\music\top.mp3", Bpm = 102, Key = "G#m", Camelot = "1A",
                Speed = 0.9118, Semitones = -2, EqLow = 0, EqHigh = 1.2,
                TimelineOffsetSeconds = 8.25, Mode = StretchMode.Linked, Muted = true,
            },
        },
    };

    [Fact]
    public void SaveThenLoadRoundTripsEveryField()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "test.benchmix");
        var original = SampleProject();

        ProjectStore.Save(original, path);
        var loaded = ProjectStore.Load(path).Project;

        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.ProjectBpm, loaded.ProjectBpm);
        Assert.Equal(original.MasterGain, loaded.MasterGain);
        Assert.Equal(original.LoopStartSeconds, loaded.LoopStartSeconds);
        Assert.Equal(2, loaded.Tracks.Count);

        var t = loaded.Tracks[1];
        Assert.Equal("Topper", t.Name);
        Assert.Equal(0.9118, t.Speed, 6);
        Assert.Equal(-2, t.Semitones);
        Assert.Equal(0, t.EqLow);
        Assert.Equal(1.2, t.EqHigh, 6);
        Assert.Equal(8.25, t.TimelineOffsetSeconds, 6);
        Assert.Equal(StretchMode.Linked, t.Mode);
        Assert.True(t.Muted);
        Assert.Equal("G#m", t.Key);
    }

    [Fact]
    public void SecondSaveLeavesABackup()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "test.benchmix");
        var p = SampleProject();

        ProjectStore.Save(p, path);
        p.Name = "Changed";
        ProjectStore.Save(p, path);

        Assert.True(File.Exists(path + ".bak"));
        Assert.Equal("Changed", ProjectStore.Load(path).Project.Name);
    }

    [Fact]
    public void ACorruptProjectFallsBackToTheBackup()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "test.benchmix");
        var p = SampleProject();

        ProjectStore.Save(p, path);
        p.Name = "Second";
        ProjectStore.Save(p, path);          // now there is a .bak holding the first save

        File.WriteAllText(path, "{ this is not json");

        var result = ProjectStore.Load(path);
        Assert.True(result.UsedFallback);
        Assert.NotNull(result.Note);
        Assert.Equal("Test", result.Project.Name);
    }

    [Fact]
    public void ANewerSchemaIsRefusedClearly()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "future.benchmix");
        File.WriteAllText(path, """{ "schemaVersion": 999, "name": "From the future", "tracks": [] }""");

        var ex = Assert.Throws<InvalidDataException>(() => ProjectStore.Load(path));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void DatedBackupsAreKept()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "test.benchmix");
        ProjectStore.Save(SampleProject(), path, "20260916-120000");
        ProjectStore.Save(SampleProject(), path, "20260916-120500");

        var backups = Directory.GetFiles(Path.Combine(dir, "Backups"), "*.benchmix");
        Assert.Equal(2, backups.Length);
    }
}

public class UndoStackTests
{
    [Fact]
    public void UndoRestoresThePreviousState()
    {
        var undo = new UndoStack();
        var p = new BenchmixProject { Name = "one" };

        undo.Push(p, "rename");
        p.Name = "two";

        var restored = undo.Undo(p);
        Assert.NotNull(restored);
        Assert.Equal("one", restored!.Name);
    }

    [Fact]
    public void RedoPutsItBack()
    {
        var undo = new UndoStack();
        var p = new BenchmixProject { Name = "one" };
        undo.Push(p, "rename");
        p.Name = "two";

        var back = undo.Undo(p)!;
        Assert.Equal("one", back.Name);

        var forward = undo.Redo(back);
        Assert.NotNull(forward);
        Assert.Equal("two", forward!.Name);
    }

    [Fact]
    public void UndoIsDeepSoLaterEditsDoNotLeakBackwards()
    {
        var undo = new UndoStack();
        var p = new BenchmixProject { Tracks = { new TrackModel { Name = "a", Speed = 1.0 } } };

        undo.Push(p, "speed");
        p.Tracks[0].Speed = 0.5;               // mutate the live object, not a copy

        var restored = undo.Undo(p)!;
        Assert.Equal(1.0, restored.Tracks[0].Speed);
    }

    /// <summary>
    /// Until 2026-10-03 a refused command — a split with nothing under the playhead — still left an
    /// undo step behind, identical to the project. The next Ctrl+Z put back the same state and
    /// seemed to do nothing; the edit he meant to undo needed a second press.
    /// </summary>
    [Fact]
    public void UndoSkipsAStepThatChangedNothing()
    {
        var undo = new UndoStack();
        var p = new BenchmixProject { Tracks = { new TrackModel { Name = "a", Speed = 1.0 } } };

        undo.Push(p, "speed");
        p.Tracks[0].Speed = 0.5;
        undo.Push(p, "split");                 // refused: nothing changes after this snapshot

        var restored = undo.Undo(p);
        Assert.True(restored is { } r && r.Tracks[0].Speed == 1.0,
            $"one Ctrl+Z after a refused command left the speed at {restored?.Tracks[0].Speed} — "
          + "the press did nothing and the edit he meant to undo is still there");

        Assert.Null(undo.Undo(restored!));    // and the skipped step is not waiting to be pressed
    }

    [Fact]
    public void EmptyStackIsSafe()
    {
        var undo = new UndoStack();
        Assert.Null(undo.Undo(new BenchmixProject()));
        Assert.Null(undo.Redo(new BenchmixProject()));
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void TheStackIsBounded()
    {
        var undo = new UndoStack(limit: 5);
        var p = new BenchmixProject();
        // Every step a real change: an undo step that changed nothing is skipped, by design.
        for (int i = 0; i < 20; i++) { undo.Push(p, $"edit {i}"); p.Name = $"after edit {i}"; }

        int undone = 0;
        for (var current = p; undo.Undo(current) is { } previous; current = previous) undone++;
        Assert.Equal(5, undone);
    }
}

public class ProjectSessionTests
{
    static AudioClip Tone(double hz, double seconds, int rate = 48000)
    {
        int frames = (int)(seconds * rate);
        var s = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(0.4 * Math.Sin(2 * Math.PI * hz * f / rate));
            s[f * 2] = v;
            s[f * 2 + 1] = v;
        }
        return AudioClip.FromSamples("tone", s, rate);
    }

    [Fact]
    public void OfflineSessionWorksWithoutAnAudioDevice()
    {
        using var session = ProjectSession.CreateOffline();
        Assert.True(session.Engine.IsOffline);
        Assert.Equal(48000, session.SampleRate);

        // Transport calls must be harmless rather than throwing.
        session.Engine.Play();
        session.Engine.Pause();
        session.Engine.Stop();
    }

    [Fact]
    public void MatchTempoSetsTheSpeedThatLandsOnTheTarget()
    {
        using var session = ProjectSession.CreateOffline();
        var model = new TrackModel { Name = "t", Bpm = 102 };
        session.Project.Tracks.Add(model);

        var (speed, from) = session.MatchTempo(model, 93);

        Assert.Equal(102, from, 6);
        Assert.Equal(93.0 / 102.0, speed, 6);
        Assert.Equal(93.0, model.Bpm * model.Speed, 4);      // it really lands on 93
    }

    [Fact]
    public void MatchTempoFoldsAHalfTimeReadingFirst()
    {
        using var session = ProjectSession.CreateOffline();
        var model = new TrackModel { Name = "t", Bpm = 70 };   // really 140, read half-time
        session.Project.Tracks.Add(model);

        var (speed, from) = session.MatchTempo(model, 128);

        Assert.Equal(140, from, 6);
        Assert.InRange(speed, 0.9, 0.92);                      // a gentle stretch, not a 1.83x mangle
    }

    [Fact]
    public void SoloMutesTheOthersAndExplicitMuteStillWins()
    {
        using var session = ProjectSession.CreateOffline();

        var a = new TrackModel { Name = "a" };
        var b = new TrackModel { Name = "b" };
        var c = new TrackModel { Name = "c", Muted = true };
        session.Project.Tracks.AddRange(new[] { a, b, c });

        foreach (var m in session.Project.Tracks)
            session.EnsureVoice(m).SetClip(Tone(440, 0.5));

        b.Soloed = true;
        session.ApplyAll();

        Assert.True(session.VoiceFor(a)!.Muted);      // not soloed, so silenced
        Assert.False(session.VoiceFor(b)!.Muted);     // the soloed one plays
        Assert.True(session.VoiceFor(c)!.Muted);      // muted stays muted
    }

    [Fact]
    public void SuggestSemitonesUsesTheCamelotRelationship()
    {
        using var session = ProjectSession.CreateOffline();
        var a = new TrackModel { Name = "a", Key = "Am" };
        var b = new TrackModel { Name = "b", Key = "C" };

        Assert.Equal(0, session.SuggestSemitones(a, b));    // relative major/minor
    }
}
