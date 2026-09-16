using Benchmix.Engine.Audio;
using Benchmix.Engine.Stems;
using Xunit;

namespace Benchmix.Tests;

/// <summary>
/// Stem separation, checked the only way that actually means anything: against a track that has
/// no vocals. If the "vocals" stem of an instrumental is not far quieter than the "vocals" stem of
/// a vocal track, the separator is not separating, whatever else it appears to do.
/// </summary>
public class StemTests
{
    static readonly string Library = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Sports DJ", "audio");

    const string VocalTrack = "9523F493-5F99-4F39-9784-0C129F7286A7.mp3";       // T.R.O.Y. — has vocals
    const string InstrumentalTrack = "CFBCA39E-E325-485F-839D-CF95466767AA.mp3"; // Still D.R.E. — instrumental

    static bool Ready => StemSeparator.IsModelInstalled
                      && File.Exists(Path.Combine(Library, VocalTrack))
                      && File.Exists(Path.Combine(Library, InstrumentalTrack));

    /// <summary>Loads a slice of a track, long enough to be meaningful and short enough to stay quick.</summary>
    static AudioClip Excerpt(string file, double fromSeconds, double seconds, int rate = 44100)
    {
        var full = AudioClip.Load(Path.Combine(Library, file), rate);
        long start = (long)(fromSeconds * rate);
        long count = Math.Min((long)(seconds * rate), full.FrameCount - start);

        var s = new float[count * 2];
        Array.Copy(full.Samples, start * 2, s, 0, count * 2);
        return AudioClip.FromSamples(file, s, rate);
    }

    static double Db(float[] x)
    {
        double sum = 0;
        foreach (var v in x) sum += v * (double)v;
        double rms = Math.Sqrt(sum / Math.Max(1, x.Length));
        return rms <= 1e-12 ? -200 : 20 * Math.Log10(rms);
    }

    [SkippableFact]
    public async Task VocalsAreFoundInAVocalTrackAndAbsentFromAnInstrumental()
    {
        Skip.IfNot(Ready, "separation model or sample library not present");

        var vocalMix = Excerpt(VocalTrack, 40, 40);
        var instrumentalMix = Excerpt(InstrumentalTrack, 40, 40);

        var a = await StemSeparator.SeparateAsync(vocalMix);
        var b = await StemSeparator.SeparateAsync(instrumentalMix);

        double vocalGap = Db(a.Primary.Samples) - Db(vocalMix.Samples);
        double controlGap = Db(b.Primary.Samples) - Db(instrumentalMix.Samples);

        // On the vocal track the voice should sit within ~15 dB of the full mix.
        Assert.InRange(vocalGap, -15, 0);

        // On the instrumental it should be buried — the spike measured -44 dB.
        Assert.True(controlGap < -25, $"control track's vocals stem was only {controlGap:F1} dB below the mix");

        // And the gap between them is the real evidence that it is separating, not just filtering.
        Assert.True(vocalGap - controlGap > 20,
            $"only {vocalGap - controlGap:F1} dB between a vocal track and an instrumental — separation is not working");
    }

    [SkippableFact]
    public async Task StemsSumBackToTheOriginalAndKeepItsLength()
    {
        Skip.IfNot(Ready, "separation model or sample library not present");

        var mix = Excerpt(VocalTrack, 60, 20);
        var result = await StemSeparator.SeparateAsync(mix);

        Assert.Equal(mix.FrameCount, result.Primary.FrameCount);
        Assert.Equal(mix.FrameCount, result.Secondary.FrameCount);
        Assert.Equal(mix.SampleRate, result.Primary.SampleRate);

        // The instrumental is derived as mix - vocals, so this mostly proves the arithmetic and the
        // frame alignment: any indexing slip would show up here as a large residual.
        var residual = new float[mix.Samples.Length];
        for (int i = 0; i < residual.Length; i++)
            residual[i] = mix.Samples[i] - (result.Primary.Samples[i] + result.Secondary.Samples[i]);

        Assert.True(Db(residual) < -35, $"stems did not reconstruct the mix: residual {Db(residual):F1} dBFS");
    }

    [SkippableFact]
    public async Task ItReportsWhichProcessorRanAndHowLongItTook()
    {
        Skip.IfNot(Ready, "separation model or sample library not present");

        var mix = Excerpt(VocalTrack, 30, 15);
        var result = await StemSeparator.SeparateAsync(mix);

        Assert.Contains(result.ExecutionProvider, new[] { "DirectML", "CPU" });
        Assert.True(result.Seconds > 0);
        // dim_f of 3072 out of 3841 bins caps the vocal stem's bandwidth; surfaced so the UI can say so.
        Assert.InRange(result.BandLimitHz, 15_000, 19_000);
    }

    [Fact]
    public void ModelMetadataIsSelfConsistent()
    {
        var m = StemModelStore.VocalsModel;
        Assert.Equal(64, m.Sha256.Length);
        Assert.True(m.SizeBytes > 1_000_000);
        Assert.EndsWith(".onnx", m.FileName);

        var p = m.Parameters;
        Assert.Equal(3841, p.NBins);
        Assert.Equal(3840, p.Trim);
        Assert.Equal(261_120, p.ChunkSize);
        Assert.Equal(253_440, p.GenSize);
        Assert.True(p.GenSize > 0, "chunk must be longer than twice the trim, or nothing is produced");
    }

    [Fact]
    public void AWrongChecksumIsRefused()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"bmx-fake-{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllText(temp, "this is not a neural network");
            var ex = Assert.Throws<InvalidDataException>(
                () => StemModelStore.InstallFrom(StemModelStore.VocalsModel, temp));
            Assert.Contains("is not", ex.Message);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}

/// <summary>The whole split path as the UI drives it: separate, write, and land two usable tracks.</summary>
public class StemSplitTests
{
    static readonly string Library = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Sports DJ", "audio");
    const string VocalTrack = "9523F493-5F99-4F39-9784-0C129F7286A7.mp3";

    static bool Ready => StemSeparator.IsModelInstalled && File.Exists(Path.Combine(Library, VocalTrack));

    [SkippableFact]
    public async Task SplittingATrackAddsTwoStemsThatInheritItsPlacement()
    {
        Skip.IfNot(Ready, "separation model or sample library not present");

        using var session = Benchmix.Core.ProjectSession.CreateOffline(44100);
        var source = await session.AddTrackAsync(Path.Combine(Library, VocalTrack));

        // Give it a distinctive placement so we can prove the stems inherit it.
        source.TimelineOffsetSeconds = 7.5;
        source.Speed = 0.9118;
        source.SourceStartSeconds = 30;
        source.SourceEndSeconds = 75;
        session.Apply(source);

        var (vocals, instrumental) = await session.SplitStemsAsync(source);

        Assert.Equal(3, session.Project.Tracks.Count);
        Assert.True(source.Muted, "the original should be muted, not deleted");

        foreach (var stem in new[] { vocals, instrumental })
        {
            Assert.Equal(7.5, stem.TimelineOffsetSeconds, 4);
            Assert.Equal(0.9118, stem.Speed, 6);
            Assert.Equal(30, stem.SourceStartSeconds, 4);
            Assert.Equal(75, stem.SourceEndSeconds, 4);
            Assert.Equal(source.Bpm, stem.Bpm);
            Assert.Equal(source.Key, stem.Key);

            Assert.True(File.Exists(stem.SourcePath), $"stem file was not written: {stem.SourcePath}");
            Assert.NotNull(session.ClipFor(stem));
            Assert.NotNull(session.VoiceFor(stem));
            // Same length as the original, so they line up sample for sample.
            Assert.Equal(session.ClipFor(source)!.FrameCount, session.ClipFor(stem)!.FrameCount);
        }

        // The stems are real files another tool could open.
        using var reader = new NAudio.Wave.WaveFileReader(vocals.SourcePath);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(24, reader.WaveFormat.BitsPerSample);

        Assert.NotNull(session.LastStemResult);
        Assert.Contains(session.LastStemResult!.ExecutionProvider, new[] { "DirectML", "CPU" });
    }
}
