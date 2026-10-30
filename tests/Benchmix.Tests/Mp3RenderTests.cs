using Benchmix.Core;
using Benchmix.Engine.Audio;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace Benchmix.Tests;

/// <summary>
/// MP3 render (Daniel: "please also add mp3 mixdown option to desktop
/// version"; built 2026-10-29). The MP3 is Windows' own encoder fed the blocks the WAV gets, so
/// every test here decodes the MP3 back and measures it against the WAV of the same render — a
/// length, a lag, a residual — never a number on its own.
/// </summary>
public class Mp3RenderTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public Mp3RenderTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "benchmix-mp3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// A 431 Hz tone at half scale from 0.5 s to 3.5 s with 10 ms ramps on and off: an onset to
    /// place the MP3 against the WAV by, and a steady stretch to compare them over. Not 440 Hz, as
    /// the house style has it, and the placement is read off the onset, never off the tone.
    /// </summary>
    static float[] Tone(int rate, double seconds = 4.0)
    {
        var x = new float[(int)(rate * seconds) * 2];
        int from = (int)(0.5 * rate), to = (int)(3.5 * rate), ramp = rate / 100;
        for (int f = from; f < to; f++)
        {
            double env = Math.Min(1, Math.Min(f - from, to - 1 - f) / (double)ramp);
            float v = (float)(0.5 * env * Math.Sin(2 * Math.PI * 431 * (f - from) / rate));
            x[f * 2] = x[f * 2 + 1] = v;
        }
        return x;
    }

    async Task<ProjectSession> SessionAsync(int rate)
    {
        string song = Path.Combine(_dir, $"tone-{rate}.wav");
        AudioClip.FromSamples("tone", Tone(rate), rate).SaveWav(song);
        var s = ProjectSession.CreateOffline(rate);
        await s.AddTrackAsync(song);
        s.ApplyAll();
        return s;
    }

    RenderResult Render(ProjectSession s, string name, RenderFormat format)
        => Renderer.Render(s.Graph, Path.Combine(_dir, name),
                           new RenderOptions { Format = format, TailSeconds = 0.5, ApplyLimiter = false });

    /// <summary>The left channel, decoded by the reader every song comes in through, and the file's own rate.</summary>
    static (float[] Left, int Rate) Decode(string path)
    {
        using WaveStream reader = Path.GetExtension(path) == ".wav"
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        var sp = reader.ToSampleProvider();
        int ch = sp.WaveFormat.Channels;
        var all = new List<float>();
        var buf = new float[sp.WaveFormat.SampleRate * ch];
        int n;
        while ((n = sp.Read(buf.AsSpan())) > 0)
            for (int i = 0; i < n; i += ch) all.Add(buf[i]);
        return (all.ToArray(), sp.WaveFormat.SampleRate);
    }

    /// <summary>
    /// How many frames late <paramref name="mp3"/> runs against <paramref name="wav"/>, searched up
    /// to <paramref name="maxLag"/> over <paramref name="window"/> frames from <paramref name="from"/>
    /// (the onset is in them), and the residual over the window after that, lined up, in dB
    /// against the tone.
    /// </summary>
    static (int Lag, double ResidualDb) Align(float[] wav, float[] mp3, int from, int window, int maxLag)
    {
        int bestLag = 0;
        double best = double.NegativeInfinity;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = from; i < from + window; i++) sum += wav[i] * mp3[i + lag];
            if (sum > best) { best = sum; bestLag = lag; }
        }
        double diff = 0, power = 0;
        for (int i = from + window; i < from + 2 * window; i++)
        {
            double d = mp3[i + bestLag] - wav[i];
            diff += d * d;
            power += wav[i] * (double)wav[i];
        }
        return (bestLag, 10 * Math.Log10(diff / power));
    }

    [Fact]
    public async Task AnMp3RenderDecodesToTheWavOfTheSameMix_WithinTheCodecDelay()
    {
        const int rate = 48000;
        var s = await SessionAsync(rate);
        var wavResult = Render(s, "mix.wav", RenderFormat.Pcm16);
        var mp3Result = Render(s, "mix.mp3", RenderFormat.Mp3);
        Assert.Equal(wavResult.Frames, mp3Result.Frames);
        Assert.Null(mp3Result.Note);

        var (wav, _) = Decode(Path.Combine(_dir, "mix.wav"));
        var (mp3, mp3Rate) = Decode(Path.Combine(_dir, "mix.mp3"));
        Assert.Equal(rate, mp3Rate);
        Assert.True(Math.Abs(mp3.Length - wav.Length) <= rate / 10,
            $"the MP3 decodes to {mp3.Length} frames, the WAV is {wav.Length}: the mixdown is "
            + $"{(mp3.Length < wav.Length ? "cut short" : "longer")} by {Math.Abs(mp3.Length - wav.Length) * 1000.0 / rate:0} ms");

        // The onset at 0.5 s is in the first window; up to 50 ms of lag is searched: a codec has a
        // delay of its own (32.6 ms here, 2026-10-29), which no player can take out of a file carrying no gapless information.
        var (lag, residual) = Align(wav, mp3, from: (int)(0.4 * rate), window: rate, maxLag: rate / 20);
        Assert.True(lag < rate / 20,
            $"the MP3 runs {lag * 1000.0 / rate:0.0} ms behind the WAV, more than any codec delay: the mix is misplaced in it");
        Assert.True(residual < -30,
            $"lined up ({lag} frames late), the MP3 differs from the WAV by {residual:0.0} dB over the steady tone: at 320 kbps a tone should come back within -40 dB");

        double kbps = new FileInfo(Path.Combine(_dir, "mix.mp3")).Length * 8.0 / wavResult.Seconds / 1000;
        Assert.True(Math.Abs(kbps - 320) < 16, $"the MP3 is {kbps:0} kbps by its size, not 320");
        _out.WriteLine($"MP3 against the WAV at 48 kHz: {lag} frames ({lag * 1000.0 / rate:0.0} ms) late, residual {residual:0.0} dB, "
                     + $"decoded {mp3.Length - wav.Length:+0;-0} frames longer, {kbps:0} kbps");
    }

    [Fact]
    public async Task AnMp3RenderIsDeterministic()
    {
        var s = await SessionAsync(48000);
        Render(s, "one.mp3", RenderFormat.Mp3);
        Render(s, "two.mp3", RenderFormat.Mp3);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "one.mp3")), File.ReadAllBytes(Path.Combine(_dir, "two.mp3")));
    }

    [Fact]
    public async Task AMixAtARateMp3CannotHold_IsConvertedOnTheWayIn()
    {
        Assert.Equal(48000, Renderer.Mp3RateFor(48000));
        Assert.Equal(44100, Renderer.Mp3RateFor(44100));
        Assert.Equal(44100, Renderer.Mp3RateFor(88200));
        Assert.Equal(48000, Renderer.Mp3RateFor(96000));
        Assert.Equal(48000, Renderer.Mp3RateFor(192000));

        const int rate = 96000;
        var s = await SessionAsync(rate);
        var result = Render(s, "mix96.mp3", RenderFormat.Mp3);
        Assert.Contains("48 kHz", result.Note);
        var (mp3, mp3Rate) = Decode(Path.Combine(_dir, "mix96.mp3"));
        Assert.Equal(48000, mp3Rate);
        Assert.True(Math.Abs(mp3.Length / 48000.0 - result.Seconds) <= 0.1,
            $"the MP3 of a 96 kHz mix plays {mp3.Length / 48000.0:0.00} s, the mix is {result.Seconds:0.00} s");

        // Through the converter and the codec alike, the tone still matches the WAV of the same mix:
        // the MP3 decoded at 48 kHz, the 96 kHz WAV read every other frame (a pure tone loses nothing to that).
        Render(s, "mix96.wav", RenderFormat.Pcm16);
        var (wav96, _) = Decode(Path.Combine(_dir, "mix96.wav"));
        var wav = new float[wav96.Length / 2];
        for (int i = 0; i < wav.Length; i++) wav[i] = wav96[2 * i];
        var (lag, residual) = Align(wav, mp3, from: (int)(0.4 * 48000), window: 48000, maxLag: 48000 / 20);
        Assert.True(residual < -30,
            $"converted to 48 kHz, the MP3 differs from the WAV of the same mix by {residual:0.0} dB ({lag} frames late)");
        _out.WriteLine($"MP3 of a 96 kHz mix: {mp3.Length} frames at 48 kHz for a {result.Seconds:0.000} s mix, {lag} frames late, residual {residual:0.0} dB");
    }
}
