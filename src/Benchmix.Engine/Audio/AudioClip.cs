using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Benchmix.Engine.Audio;

/// <summary>Why a source could not be loaded. The UI marks the track instead of crashing.</summary>
public enum ClipLoadFailure
{
    SourceMissing,
    Undecodable,
    Empty,
    /// <summary>A OneDrive Files-On-Demand placeholder that could not be hydrated.</summary>
    Offline,
    IoError,
}

public sealed class ClipLoadException : Exception
{
    public ClipLoadException(ClipLoadFailure failure, string path, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        SourcePath = path;
    }

    public ClipLoadFailure Failure { get; }
    public string SourcePath { get; }
}

/// <summary>
/// A decoded song held in memory as interleaved float32 stereo at the engine rate.
///
/// In RAM rather than streamed from a disk cache: a mashup editor scrubs, loops and jumps
/// constantly, and every one of those is free from an array and painful from a file. A 4-minute
/// stereo track at 48 kHz costs ~92 MB, so a typical 2-6 track mashup sits comfortably inside
/// this machine's 32 GB. <see cref="EstimateMemoryBytes"/> lets the UI warn before it bites.
/// </summary>
public sealed class AudioClip
{
    private AudioClip(string path, float[] samples, int sampleRate, int channels)
    {
        SourcePath = path;
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
        FrameCount = samples.Length / channels;
    }

    public string SourcePath { get; }
    /// <summary>Interleaved float32. Never reallocated after construction.</summary>
    public float[] Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long FrameCount { get; }
    public double DurationSeconds => FrameCount / (double)SampleRate;

    /// <summary>Peak envelope for waveform drawing, built lazily on first request.</summary>
    public PeakData? Peaks { get; private set; }

    private readonly object _peakGate = new();

    /// <summary>
    /// Builds the waveform envelope once. Callable from any thread: the loader builds it on a
    /// worker while the UI may already be trying to draw, and both must get the same object
    /// rather than racing to build two.
    /// </summary>
    public PeakData EnsurePeaks(int samplesPerBucket = 512)
    {
        var existing = Peaks;
        if (existing is not null) return existing;

        lock (_peakGate)
        {
            return Peaks ??= PeakData.Build(this, samplesPerBucket);
        }
    }

    public static long EstimateMemoryBytes(double durationSeconds, int sampleRate = 48000, int channels = 2)
        => (long)(durationSeconds * sampleRate * channels * sizeof(float));

    /// <summary>
    /// Decodes anything Media Foundation can open — mp3, m4a/aac, flac, wav, wma, ogg, opus —
    /// to interleaved stereo float at <paramref name="targetRate"/>.
    /// </summary>
    public static AudioClip Load(string path, int targetRate, IProgress<double>? progress = null,
                                 CancellationToken cancel = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ClipLoadException(ClipLoadFailure.SourceMissing, path, $"File not found: {path}");
        if ((info.Attributes & FileAttributes.Offline) != 0 || ((int)info.Attributes & 0x400000) != 0)
        {
            // Touching the file asks OneDrive to hydrate it; if that fails we report it as such
            // rather than as a decode error, which is a far more useful message.
            try { using var probe = File.OpenRead(path); probe.ReadByte(); }
            catch (Exception ex) { throw new ClipLoadException(ClipLoadFailure.Offline, path, "File is a cloud placeholder that could not be downloaded.", ex); }
        }

        WaveStream reader;
        try
        {
            reader = string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)
                ? new WaveFileReader(path)
                : new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
                {
                    RequestFloatOutput = true,
                    SingleReaderObject = true,
                });
        }
        catch (Exception ex)
        {
            throw new ClipLoadException(ClipLoadFailure.Undecodable, path, $"Could not decode {Path.GetFileName(path)}.", ex);
        }

        try
        {
            ISampleProvider sp = reader.ToSampleProvider();
            sp = ToStereo(sp);
            if (sp.WaveFormat.SampleRate != targetRate) sp = new WdlResamplingSampleProvider(sp, targetRate);

            double expectedSeconds = reader.TotalTime.TotalSeconds;
            int expectedSamples = expectedSeconds > 0 ? (int)(expectedSeconds * targetRate * 2) + targetRate : targetRate * 2 * 300;

            var acc = new List<float>(Math.Max(targetRate * 2, expectedSamples));
            var buf = new float[targetRate * 2];   // ~1 second per pull
            int n;
            while ((n = sp.Read(buf.AsSpan())) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                for (int i = 0; i < n; i++) acc.Add(buf[i]);
                if (expectedSamples > 0) progress?.Report(Math.Min(1, acc.Count / (double)expectedSamples));
            }

            if (acc.Count == 0) throw new ClipLoadException(ClipLoadFailure.Empty, path, "The file decoded to no audio.");
            progress?.Report(1);
            return new AudioClip(path, acc.ToArray(), targetRate, 2);
        }
        catch (ClipLoadException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ClipLoadException(ClipLoadFailure.IoError, path, $"Failed reading {Path.GetFileName(path)}.", ex);
        }
        finally { reader.Dispose(); }
    }

    /// <summary>Builds a clip directly from samples — used by tests and by stem separation.</summary>
    public static AudioClip FromSamples(string name, float[] interleavedStereo, int sampleRate)
        => new(name, interleavedStereo, sampleRate, 2);

    private static ISampleProvider ToStereo(ISampleProvider sp)
    {
        if (sp.WaveFormat.Channels == 2) return sp;
        if (sp.WaveFormat.Channels == 1) return new MonoToStereoSampleProvider(sp);
        return new MultiplexingSampleProvider(new[] { sp }, 2);
    }
}

/// <summary>
/// Min/max peaks per bucket of frames, so the waveform can be drawn at any zoom without
/// re-reading the audio. Stored per channel, mono-summed for the overview.
/// </summary>
public sealed class PeakData
{
    private PeakData(float[] min, float[] max, int samplesPerBucket, long frameCount)
    {
        Min = min;
        Max = max;
        SamplesPerBucket = samplesPerBucket;
        FrameCount = frameCount;
    }

    public float[] Min { get; }
    public float[] Max { get; }
    public int SamplesPerBucket { get; }
    public long FrameCount { get; }
    public int BucketCount => Min.Length;

    public static PeakData Build(AudioClip clip, int samplesPerBucket)
    {
        samplesPerBucket = Math.Max(16, samplesPerBucket);
        int buckets = (int)((clip.FrameCount + samplesPerBucket - 1) / samplesPerBucket);
        var min = new float[buckets];
        var max = new float[buckets];
        var s = clip.Samples;
        int ch = clip.Channels;

        for (int b = 0; b < buckets; b++)
        {
            long start = (long)b * samplesPerBucket;
            long end = Math.Min(clip.FrameCount, start + samplesPerBucket);
            float lo = 0, hi = 0;
            for (long f = start; f < end; f++)
            {
                // Mono sum for the overview: what the eye reads as "the waveform".
                float v = 0;
                for (int c = 0; c < ch; c++) v += s[f * ch + c];
                v /= ch;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            min[b] = lo;
            max[b] = hi;
        }
        return new PeakData(min, max, samplesPerBucket, clip.FrameCount);
    }
}
