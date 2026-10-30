using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Benchmix.Engine.Audio;

public enum RenderFormat
{
    /// <summary>16-bit PCM with TPDF dither. Smallest, and what most people want to play.</summary>
    Pcm16,
    /// <summary>24-bit PCM. No audible quantisation, half the size of float; this type's own default.</summary>
    Pcm24,
    /// <summary>
    /// 32-bit float. Lossless against the mix bus, including anything above 0 dBFS. What the app's
    /// Render box offers first (Daniel, 2026-10-27: "keep all options but make 32 default").
    /// </summary>
    Float32,
    /// <summary>
    /// MP3 at 320 kbps through Windows' own encoder (Daniel: "please also add
    /// mp3 mixdown option to desktop version"; built 2026-10-29). The encoder is fed the very blocks
    /// the WAV loop pulls, as 16-bit PCM with the WAV's own dither. A mix at a rate MP3 cannot hold
    /// is converted on the way in (<see cref="Renderer.Mp3RateFor"/>).
    /// </summary>
    Mp3,
}

public sealed record RenderOptions
{
    public RenderFormat Format { get; init; } = RenderFormat.Pcm24;
    /// <summary>Extra silence rendered past the last clip that can be heard, so fades finish.</summary>
    public double TailSeconds { get; init; } = 2.0;
    /// <summary>Render only this region, in engine frames. Null renders everything.</summary>
    public (long Start, long End)? Region { get; init; }
    public bool ApplyLimiter { get; init; } = true;
}

/// <param name="PeakBefore">Highest level reached before the limiter, so an overdriven mix is visible.</param>
/// <param name="Clipped">True when the limiter had to pull peaks down, or the output hit full scale.</param>
/// <param name="Note">
/// What the render had to do that the listener should be told — an MP3's rate conversion, or a
/// bitrate the encoder did not offer — as a sentence without its full stop. Null when nothing.
/// </param>
public sealed record RenderResult(string Path, long Frames, double Seconds, float PeakBefore, bool Clipped, double ElapsedSeconds,
                                  string? Note = null);

/// <summary>
/// Bounces the mix to a file by pulling blocks from the very same <see cref="MixGraph"/> that
/// feeds the speakers. Nothing is recomputed differently for the render — the only difference is
/// that blocks are pulled as fast as the CPU allows instead of in realtime.
///
/// <para>
/// The file holds what can be heard, and no more: a muted track is silent in it, as in the
/// speakers, and the file ends where the last track that can be heard ends — not where the
/// longest track does (Daniel, 2026-10-27). Before then a muted track longer than the rest left
/// its whole length as silence on the end of every render.
/// </para>
/// <para>
/// An MP3 (2026-10-29) is the same blocks through Windows' MP3 encoder — Media Foundation, the
/// decoder every song comes in through, so nothing new is loaded — as 16-bit PCM with the WAV's
/// dither (<see cref="Dither16"/>, one function for both), at 320 kbps.
/// </para>
/// </summary>
public static class Renderer
{
    public const int BlockFrames = 4096;

    /// <summary>What an MP3 is written at: the most the format holds, and what a DJ library is made of.</summary>
    public const int Mp3BitRate = 320_000;

    /// <summary>The rates an MP3 can hold (MPEG-1 layer III).</summary>
    public static readonly int[] Mp3Rates = { 32000, 44100, 48000 };

    /// <summary>
    /// The rate an MP3 of a mix at <paramref name="mixRate"/> is written at: the mix's own when MP3
    /// holds it; else 44.1 kHz for a multiple of it (88.2, 176.4) and 48 kHz for anything else.
    /// </summary>
    public static int Mp3RateFor(int mixRate)
        => Array.IndexOf(Mp3Rates, mixRate) >= 0 ? mixRate : mixRate % 44100 == 0 ? 44100 : 48000;

    public static RenderResult Render(MixGraph graph, string path, RenderOptions? options = null,
                                      IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        options ??= new RenderOptions();
        var started = System.Diagnostics.Stopwatch.StartNew();

        long start = options.Region?.Start ?? 0;
        long end = options.Region?.End ?? (graph.AudibleLengthFrames + (long)(options.TailSeconds * graph.SampleRate));
        end = Math.Max(start, end);
        long total = end - start;

        bool previousLimiter = graph.LimiterEnabled;
        long previousPosition = graph.PositionFrames;
        graph.LimiterEnabled = options.ApplyLimiter;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool mp3 = options.Format == RenderFormat.Mp3;
        // The MP3 writer picks its container from the extension, so the temporary name keeps it.
        string tmp = mp3 ? Path.ChangeExtension(path, ".tmp.mp3") : path + ".tmp";

        graph.Meters.MasterClipped = false;
        graph.Meters.MasterPrePeak = 0;
        long written = 0;
        string? note = null;

        try
        {
            graph.Seek(start);
            graph.SnapParameters();

            written = mp3
                ? RenderMp3(graph, tmp, total, progress, cancel, out note)
                : RenderWav(graph, tmp, total, options.Format, progress, cancel);

            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            graph.LimiterEnabled = previousLimiter;
            graph.Seek(previousPosition);
        }

        started.Stop();
        // The peak the caller wants is the one BEFORE the limiter — that is the number that tells
        // you the mix was 6 dB too hot. After it, every overdriven render reports a tidy -0.3 dBFS.
        float prePeak = graph.Meters.MasterPrePeak;
        bool limited = options.ApplyLimiter && prePeak > graph.CeilingLinear;

        return new RenderResult(path, written, written / (double)graph.SampleRate,
                                prePeak, limited || graph.Meters.MasterClipped, started.Elapsed.TotalSeconds, note);
    }

    private static long RenderWav(MixGraph graph, string tmp, long total, RenderFormat format,
                                  IProgress<double>? progress, CancellationToken cancel)
    {
        var waveFormat = format == RenderFormat.Float32
            ? WaveFormat.CreateIeeeFloatWaveFormat(graph.SampleRate, graph.Channels)
            : new WaveFormat(graph.SampleRate, format == RenderFormat.Pcm16 ? 16 : 24, graph.Channels);

        using var writer = new WaveFileWriter(tmp, waveFormat);
        var buf = new float[BlockFrames * graph.Channels];
        var pcm = new byte[buf.Length * 2];
        var rnd = new Random(0x5EED);   // deterministic dither: the same project renders identically
        double dLast = 0;
        long written = 0;

        while (written < total)
        {
            cancel.ThrowIfCancellationRequested();
            int frames = (int)Math.Min(BlockFrames, total - written);
            var span = buf.AsSpan(0, frames * graph.Channels);
            graph.Render(span, frames);

            switch (format)
            {
                case RenderFormat.Float32:
                    writer.WriteSamples(buf, 0, frames * graph.Channels);
                    break;
                case RenderFormat.Pcm24:
                    WritePcm24(writer, span);
                    break;
                default:
                    writer.Write(pcm, 0, Dither16(span, pcm, rnd, ref dLast));
                    break;
            }

            written += frames;
            progress?.Report(written / (double)total);
        }
        return written;
    }

    /// <summary>
    /// Windows' MP3 encoder, fed the mix as the WAV loop pulls it: block by block through
    /// <see cref="GraphSource"/>, dithered to 16 bits by the WAV path's own <see cref="Dither16"/>,
    /// and — at a rate MP3 cannot hold — through the live output's <see cref="DeviceRateConverter"/>
    /// first. Returns the mix frames pulled; what was done on the way is in <paramref name="note"/>.
    /// </summary>
    private static long RenderMp3(MixGraph graph, string tmp, long total, IProgress<double>? progress,
                                  CancellationToken cancel, out string? note)
    {
        MediaFoundationApi.Startup();          // once per process; a session that opened a song has done it already
        var source = new GraphSource(graph, total, progress, cancel);
        ISampleProvider feed = source;
        int rate = Mp3RateFor(graph.SampleRate);
        note = null;
        if (rate != graph.SampleRate)
        {
            long frames = (long)Math.Ceiling(total * (double)rate / graph.SampleRate);
            feed = new FramesOf(new DeviceRateConverter(source, rate), frames);
            note = $"The MP3 was converted from {graph.SampleRate / 1000.0:0.#} kHz to {rate / 1000.0:0.#} kHz, which MP3 can hold";
        }

        var pcm = new Pcm16Of(feed);
        var type = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_MP3, pcm.WaveFormat, Mp3BitRate)
                   ?? throw new InvalidOperationException($"Windows offers no MP3 encoder for {pcm.WaveFormat.Channels} channels at {rate} Hz.");
        int kbps = type.AverageBytesPerSecond * 8 / 1000;      // read first: the encoder releases the type with itself
        using (var encoder = new MediaFoundationEncoder(type)) encoder.Encode(tmp, pcm);

        if (kbps != Mp3BitRate / 1000)
            note = (note is null ? "" : note + ". ") + $"The encoder offered {kbps} kbps, not {Mp3BitRate / 1000}";
        return source.Done;
    }

    private static void WritePcm24(WaveFileWriter writer, ReadOnlySpan<float> samples)
    {
        Span<byte> bytes = stackalloc byte[3];
        foreach (var f in samples)
        {
            int v = (int)Math.Round(Math.Clamp(f, -1f, 0.9999999f) * 8388607.0);
            bytes[0] = (byte)(v & 0xFF);
            bytes[1] = (byte)((v >> 8) & 0xFF);
            bytes[2] = (byte)((v >> 16) & 0xFF);
            writer.Write(bytes);
        }
    }

    /// <summary>
    /// 16-bit with TPDF dither and first-order noise shaping, two bytes a sample into
    /// <paramref name="bytes"/>; returns how many bytes. Truncating to 16 bits without dither puts
    /// correlated quantisation distortion on quiet fade-outs; a two-LSB triangular dither turns
    /// that into a steady, far less audible hiss. The WAV's 16-bit path and the MP3's input share
    /// it, so the two quantise alike.
    /// </summary>
    internal static int Dither16(ReadOnlySpan<float> samples, Span<byte> bytes, Random rnd, ref double last)
    {
        const double Scale = 32767.0;
        int n = 0;
        foreach (var f in samples)
        {
            double d = (rnd.NextDouble() - rnd.NextDouble());        // TPDF, +/-1 LSB
            double shaped = f * Scale + d - last * 0.5;
            double rounded = Math.Round(shaped);
            last = rounded - shaped;
            int v = (int)Math.Clamp(rounded, short.MinValue, short.MaxValue);
            bytes[n++] = (byte)(v & 0xFF);
            bytes[n++] = (byte)((v >> 8) & 0xFF);
        }
        return n;
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
    }

    /// <summary>
    /// The mix pulled block by block, exactly as the WAV loop pulls it, as a sample provider that
    /// ends after <paramref name="total"/> frames.
    /// </summary>
    private sealed class GraphSource(MixGraph graph, long total, IProgress<double>? progress, CancellationToken cancel) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(graph.SampleRate, graph.Channels);

        /// <summary>Mix frames pulled so far.</summary>
        public long Done { get; private set; }

        public int Read(Span<float> buffer)
        {
            int ch = graph.Channels;
            int frames = (int)Math.Min(buffer.Length / ch, total - Done);
            int got = 0;
            while (got < frames)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(BlockFrames, frames - got);
                graph.Render(buffer.Slice(got * ch, n * ch), n);
                got += n;
                Done += n;
                progress?.Report(Done / (double)total);
            }
            return got * ch;
        }
    }

    /// <summary>
    /// The first so many frames of a provider that never ends on its own — the rate converter
    /// fills silence past its source — so the encoder knows where the file ends.
    /// </summary>
    private sealed class FramesOf(ISampleProvider inner, long frames) : ISampleProvider
    {
        private long _left = frames;

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int ch = inner.WaveFormat.Channels;
            int want = (int)Math.Min(buffer.Length / ch, _left);
            if (want == 0) return 0;
            int got = inner.Read(buffer.Slice(0, want * ch)) / ch;
            _left -= got;
            return got * ch;
        }
    }

    /// <summary>Float samples as 16-bit PCM bytes with the WAV path's dither: what the encoder pulls from.</summary>
    private sealed class Pcm16Of(ISampleProvider source) : IWaveProvider
    {
        private readonly Random _rnd = new(0x5EED);        // the WAV's seed: the same project renders identically
        private double _last;
        private float[] _floats = new float[BlockFrames * source.WaveFormat.Channels];

        public WaveFormat WaveFormat { get; } = new WaveFormat(source.WaveFormat.SampleRate, 16, source.WaveFormat.Channels);

        public int Read(Span<byte> buffer)
        {
            int ch = WaveFormat.Channels;
            int samples = buffer.Length / 2 / ch * ch;
            if (_floats.Length < samples) _floats = new float[samples];
            int got = source.Read(_floats.AsSpan(0, samples));
            return Dither16(_floats.AsSpan(0, got), buffer, _rnd, ref _last);
        }
    }
}
