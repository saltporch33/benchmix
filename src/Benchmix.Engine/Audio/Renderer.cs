using NAudio.Wave;

namespace Benchmix.Engine.Audio;

public enum RenderFormat
{
    /// <summary>16-bit PCM with TPDF dither. Smallest, and what most people want to play.</summary>
    Pcm16,
    /// <summary>24-bit PCM. The sensible archival default — no audible quantisation, half the size of float.</summary>
    Pcm24,
    /// <summary>32-bit float. Lossless against the mix bus, including anything above 0 dBFS.</summary>
    Float32,
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
public sealed record RenderResult(string Path, long Frames, double Seconds, float PeakBefore, bool Clipped, double ElapsedSeconds);

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
/// </summary>
public static class Renderer
{
    public const int BlockFrames = 4096;

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
        string tmp = path + ".tmp";

        graph.Meters.MasterClipped = false;
        graph.Meters.MasterPrePeak = 0;
        long written = 0;

        try
        {
            var format = options.Format == RenderFormat.Float32
                ? WaveFormat.CreateIeeeFloatWaveFormat(graph.SampleRate, graph.Channels)
                : new WaveFormat(graph.SampleRate, options.Format == RenderFormat.Pcm16 ? 16 : 24, graph.Channels);

            using (var writer = new WaveFileWriter(tmp, format))
            {
                graph.Seek(start);
                graph.SnapParameters();

                var buf = new float[BlockFrames * graph.Channels];
                var rnd = new Random(0x5EED);   // deterministic dither: the same project renders identically
                double dLast = 0;

                while (written < total)
                {
                    cancel.ThrowIfCancellationRequested();
                    int frames = (int)Math.Min(BlockFrames, total - written);
                    var span = buf.AsSpan(0, frames * graph.Channels);
                    graph.Render(span, frames);

                    switch (options.Format)
                    {
                        case RenderFormat.Float32:
                            writer.WriteSamples(buf, 0, frames * graph.Channels);
                            break;
                        case RenderFormat.Pcm24:
                            WritePcm24(writer, span);
                            break;
                        default:
                            WritePcm16Dithered(writer, span, rnd, ref dLast);
                            break;
                    }

                    written += frames;
                    progress?.Report(written / (double)total);
                }
            }

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
                                prePeak, limited || graph.Meters.MasterClipped, started.Elapsed.TotalSeconds);
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
    /// 16-bit with TPDF dither and first-order noise shaping. Truncating to 16 bits without dither
    /// puts correlated quantisation distortion on quiet fade-outs; a two-LSB triangular dither
    /// turns that into a steady, far less audible hiss.
    /// </summary>
    private static void WritePcm16Dithered(WaveFileWriter writer, ReadOnlySpan<float> samples, Random rnd, ref double last)
    {
        const double Scale = 32767.0;
        Span<byte> bytes = stackalloc byte[2];
        foreach (var f in samples)
        {
            double d = (rnd.NextDouble() - rnd.NextDouble());        // TPDF, +/-1 LSB
            double shaped = f * Scale + d - last * 0.5;
            double rounded = Math.Round(shaped);
            last = rounded - shaped;
            int v = (int)Math.Clamp(rounded, short.MinValue, short.MaxValue);
            bytes[0] = (byte)(v & 0xFF);
            bytes[1] = (byte)((v >> 8) & 0xFF);
            writer.Write(bytes);
        }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
    }
}
