using Benchmix.Core;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Writing tempo and key into a song's own tags (Daniel; built 2026-10-29).
/// Every file here is made by the tests — an MP3 rendered from a tone — never one of his. The
/// values are read back by the app's own reader and by the app's own analysis, and the music is
/// compared byte for byte.
/// </summary>
public class TagWritingTests : IDisposable
{
    private readonly string _dir;

    public TagWritingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-tags", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A two-second 431 Hz tone, rendered to an MP3 with no tags at all.</summary>
    async Task<string> Mp3Async(string name)
    {
        const int rate = 48000;
        var tone = new float[rate * 2 * 2];
        for (int f = 0; f < rate * 2; f++) tone[f * 2] = tone[f * 2 + 1] = (float)(0.4 * Math.Sin(2 * Math.PI * 431 * f / rate));
        string wav = Path.Combine(_dir, name + "-source.wav");
        AudioClip.FromSamples(name, tone, rate).SaveWav(wav);
        var s = ProjectSession.CreateOffline(rate);
        await s.AddTrackAsync(wav);
        s.ApplyAll();
        string mp3 = Path.Combine(_dir, name + ".mp3");
        Renderer.Render(s.Graph, mp3, new RenderOptions { Format = RenderFormat.Mp3, TailSeconds = 0, ApplyLimiter = false });
        // Windows' encoder leaves one frame of its own, TFLT = MPG/3; the tempo and key are what must be blank.
        var tags = Id3.Read(mp3);
        Assert.False(tags.ContainsKey("TBPM") || tags.ContainsKey("TKEY"), "the rendered MP3 starts without a tempo or key tag");
        return mp3;
    }

    [Fact]
    public async Task WritesTempoAndKeyIntoAnUntaggedMp3_AndLeavesTheMusicUntouched()
    {
        string mp3 = await Mp3Async("plain");
        byte[] before = File.ReadAllBytes(mp3);

        var result = TagWriter.Write(mp3, 93.0, "Em");
        Assert.True(result.WroteBpm && result.WroteKey, "both values were blank, both should be written");

        var tags = Id3.Read(mp3);
        Assert.Equal("93", tags["TBPM"]);
        Assert.Equal("Em", tags["TKEY"]);
        Assert.True(TagWriter.Music(File.ReadAllBytes(mp3)).SequenceEqual(TagWriter.Music(before)),
            "the music must be the same bytes after the tag was written: a tag write that touches the audio is a corrupted song");
        Assert.False(File.Exists(mp3 + ".benchmix-tags.tmp"), "the working copy is gone once it has taken the file's place");

        // The app's own analysis now takes them as the file's word, not its guess.
        var analysis = Analyzer.Analyze(mp3);
        Assert.Equal(93.0, analysis.Bpm);
        Assert.Equal(ValueSource.Tag, analysis.BpmSource);
        Assert.Equal("Em", analysis.Key);
        Assert.Equal(ValueSource.Tag, analysis.KeySource);
    }

    [Fact]
    public async Task FillsOnlyWhatTheFileLacks_NeverOverwriting()
    {
        string mp3 = await Mp3Async("partial");
        var first = TagWriter.Write(mp3, 93.0, null);
        Assert.True(first.WroteBpm && !first.WroteKey);

        var second = TagWriter.Write(mp3, 120.0, "Am");                 // the tempo is there already; the key is not
        Assert.False(second.WroteBpm, "a tempo already in the file is never overwritten — Serato's word beats Benchmix's guess");
        Assert.True(second.WroteKey);
        var tags = Id3.Read(mp3);
        Assert.Equal("93", tags["TBPM"]);
        Assert.Equal("Am", tags["TKEY"]);

        var third = TagWriter.Write(mp3, 100.0, "C");
        Assert.False(third.WroteBpm || third.WroteKey, "with both there, nothing is written");
        Assert.Equal(("93", "Am"), (Id3.Read(mp3)["TBPM"], Id3.Read(mp3)["TKEY"]));
        Assert.Equal((null, null), TagWriter.Blanks(mp3, 100.0, "C"));
    }

    [Fact]
    public async Task ATempoThatIsNotWholeIsWrittenAsItIs()
    {
        string mp3 = await Mp3Async("decimal");
        TagWriter.Write(mp3, 127.5, null);
        Assert.Equal("127.5", Id3.Read(mp3)["TBPM"]);
        Assert.Equal(127.5, Analyzer.Analyze(mp3).Bpm);
        Assert.Equal("93", TagWriter.BpmText(93.004));                   // a hair off whole is written whole
    }

    [Fact]
    public async Task RefusesANonMp3_AndLeavesAFileItCannotReplaceAsItWas()
    {
        string wav = Path.Combine(_dir, "song.wav");
        AudioClip.FromSamples("w", new float[9600], 48000).SaveWav(wav);
        var ex = Assert.Throws<InvalidOperationException>(() => TagWriter.Write(wav, 93.0, "Em"));
        Assert.Contains("MP3", ex.Message);

        string mp3 = await Mp3Async("locked");
        byte[] before = File.ReadAllBytes(mp3);
        File.SetAttributes(mp3, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => TagWriter.Write(mp3, 93.0, "Em"));
            Assert.Equal(before, File.ReadAllBytes(mp3));
            Assert.False(File.Exists(mp3 + ".benchmix-tags.tmp"), "no working copy is left beside a file that could not be replaced");
        }
        finally { File.SetAttributes(mp3, FileAttributes.Normal); }
    }
}
