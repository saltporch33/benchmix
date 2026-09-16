using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;
using NAudio.Wave;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// The whole path on real music: decode a tagged MP3, match its tempo to another, render, and
/// check the rendered file is what was asked for.
///
/// These skip themselves when the sample library is not on the machine, so the suite still passes
/// on a clean checkout — but when the files are there, this is the test that would actually catch
/// a regression a user would notice.
/// </summary>
public class EndToEndTests
{
    static readonly string Library = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Sports DJ", "audio");

    // Two Serato-tagged tracks: 93 BPM G#m, and 102 BPM G#m.
    const string BedFile = "CFBCA39E-E325-485F-839D-CF95466767AA.mp3";
    const string TopFile = "9523F493-5F99-4F39-9784-0C129F7286A7.mp3";

    static bool Available => Directory.Exists(Library)
                          && File.Exists(Path.Combine(Library, BedFile))
                          && File.Exists(Path.Combine(Library, TopFile));

    static string Bed => Path.Combine(Library, BedFile);
    static string Top => Path.Combine(Library, TopFile);

    [SkippableFact]
    public void ReadsTempoAndKeyFromTagsWithoutDecoding()
    {
        Skip.IfNot(Available, "sample library not present");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = Analyzer.Analyze(Bed);
        sw.Stop();

        Assert.Equal(ValueSource.Tag, a.BpmSource);
        Assert.Equal(93.0, a.Bpm, 1);
        Assert.Equal("G#m", a.Key);
        Assert.Equal("1A", a.Camelot);
        Assert.True(a.KeyIsTrustworthy);

        // Reading a tag must not decode the file. Anything over a second means it did.
        Assert.True(sw.Elapsed.TotalSeconds < 1.0, $"tag read took {sw.Elapsed.TotalSeconds:F2}s — it decoded when it should not have");
    }

    [SkippableFact]
    public async Task BuildsAMashupAndRendersIt()
    {
        Skip.IfNot(Available, "sample library not present");

        var outDir = Path.Combine(Path.GetTempPath(), "benchmix-tests");
        Directory.CreateDirectory(outDir);
        var wav = Path.Combine(outDir, $"e2e-{Guid.NewGuid():N}.wav");

        try
        {
            using var session = ProjectSession.CreateOffline();

            var bed = await session.AddTrackAsync(Bed);
            var top = await session.AddTrackAsync(Top);

            Assert.Equal(93.0, bed.Bpm, 1);
            Assert.Equal(102.0, top.Bpm, 1);

            // Match the topper onto the bed, exactly as the Match BPM button does.
            var (speed, from) = session.MatchTempo(top, bed.Bpm);
            Assert.Equal(102.0, from, 1);
            Assert.Equal(93.0 / 102.0, speed, 4);
            Assert.Equal(93.0, top.Bpm * top.Speed, 3);

            // Same key, so no shift is wanted.
            Assert.Equal(0, session.SuggestSemitones(top, bed));

            // Classic mashup EQ split, and the topper offset by four bars.
            top.EqLow = 0;
            bed.EqHigh = 0.6;
            top.TimelineOffsetSeconds = 4 * 4 * 60.0 / 93.0;
            session.ApplyAll();

            var result = await session.RenderAsync(wav, new RenderOptions
            {
                Region = (0, session.SecondsToFrames(30)),
                Format = RenderFormat.Pcm24,
                TailSeconds = 0,
            });

            Assert.True(File.Exists(wav));
            Assert.InRange(result.Seconds, 29.5, 30.5);
            Assert.True(result.PeakBefore > 0.05, "the render came out silent");

            using var reader = new WaveFileReader(wav);
            Assert.Equal(24, reader.WaveFormat.BitsPerSample);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.InRange(reader.TotalTime.TotalSeconds, 29.5, 30.5);

            // The rendered mashup should now read at the bed's tempo, not the topper's.
            var rendered = Analyzer.Analyze(wav);
            Assert.Equal(ValueSource.Detected, rendered.BpmSource);
            Assert.InRange(rendered.Bpm, 90, 96);
        }
        finally
        {
            try { if (File.Exists(wav)) File.Delete(wav); } catch { }
        }
    }

    [SkippableFact]
    public async Task ASavedProjectReopensSoundingTheSame()
    {
        Skip.IfNot(Available, "sample library not present");

        var dir = Path.Combine(Path.GetTempPath(), "benchmix-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, "roundtrip.benchmix");
        var renderA = Path.Combine(dir, "a.wav");
        var renderB = Path.Combine(dir, "b.wav");

        try
        {
            // Build, tweak, render, save.
            using (var session = ProjectSession.CreateOffline())
            {
                var bed = await session.AddTrackAsync(Bed);
                var top = await session.AddTrackAsync(Top);
                session.MatchTempo(top, bed.Bpm);
                top.EqLow = 0;
                top.Semitones = -2;
                top.TimelineOffsetSeconds = 2.5;
                bed.Gain = 0.8;
                session.ApplyAll();

                await session.RenderAsync(renderA, new RenderOptions
                { Region = (0, session.SecondsToFrames(12)), Format = RenderFormat.Pcm16, TailSeconds = 0 });

                ProjectStore.Save(session.Project, projectPath);
            }

            // Reopen from disk in a fresh session and render the same region.
            using (var session = ProjectSession.CreateOffline())
            {
                var loaded = ProjectStore.Load(projectPath).Project;
                await session.OpenAsync(loaded);

                await session.RenderAsync(renderB, new RenderOptions
                { Region = (0, session.SecondsToFrames(12)), Format = RenderFormat.Pcm16, TailSeconds = 0 });
            }

            var a = File.ReadAllBytes(renderA);
            var b = File.ReadAllBytes(renderB);
            Assert.Equal(a.Length, b.Length);
            Assert.True(a.AsSpan().SequenceEqual(b),
                "reopening a saved project produced different audio — something is not being persisted");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AMissingFileFailsWithAUsefulMessageRatherThanACrash()
    {
        var ex = Assert.Throws<ClipLoadException>(
            () => AudioClip.Load(@"C:\definitely\not\here\nope.mp3", 48000));
        Assert.Equal(ClipLoadFailure.SourceMissing, ex.Failure);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATrackThatFailsToLoadIsReportedNotThrown()
    {
        using var session = ProjectSession.CreateOffline();
        string? reported = null;
        session.TrackFailed += (_, e) => reported = e.Message;

        var model = new TrackModel { Name = "ghost", SourcePath = @"C:\nope\missing.mp3" };
        session.Project.Tracks.Add(model);
        await session.LoadAudioAsync(model);

        Assert.NotNull(reported);
        Assert.Contains("not found", reported!, StringComparison.OrdinalIgnoreCase);
    }
}
