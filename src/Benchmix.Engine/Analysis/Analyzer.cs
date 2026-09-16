using System.Text;
using Benchmix.Engine.Audio;
using NAudio.Dsp;

namespace Benchmix.Engine.Analysis;

/// <summary>Where a value came from. The UI shows this, because a tag and a guess deserve different trust.</summary>
public enum ValueSource
{
    None,
    /// <summary>Read from the file's own tags — usually written by Serato, Rekordbox or djay.</summary>
    Tag,
    /// <summary>Computed by Benchmix from the audio.</summary>
    Detected,
    /// <summary>Typed in by the user, which beats both.</summary>
    Manual,
}

public sealed record TrackAnalysis
{
    public double Bpm { get; init; }
    public ValueSource BpmSource { get; init; }
    public double BpmConfidence { get; init; }

    /// <summary>Normalised key name such as "F#m", or null if unknown.</summary>
    public string? Key { get; init; }
    public string? Camelot { get; init; }
    public ValueSource KeySource { get; init; }
    public double KeyConfidence { get; init; }

    /// <summary>Tuning offset from A=440 in cents, for display.</summary>
    public double TuningCents { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }

    public bool KeyIsTrustworthy => KeySource is ValueSource.Tag or ValueSource.Manual;
}

/// <summary>
/// Tempo and key for a track.
///
/// Policy, decided by measurement rather than preference (see docs/SPIKE-03-analysis.md): read
/// the file's tags first, and only analyse the audio when a tag is missing. On Daniel's own
/// library that is 97% of BPMs and 91% of keys already known and correct. The tempo detector
/// scored 94% exact with nothing outright wrong, so it is trusted when it has to run; the key
/// detector scored 47%, so its output is always labelled an estimate and never applied silently.
/// </summary>
public static class Analyzer
{
    /// <summary>Analysis runs at a reduced rate — nothing here needs content above ~11 kHz.</summary>
    public const int AnalysisRate = 22050;

    /// <summary>Seconds of audio examined. Enough to be stable, short enough to stay fast.</summary>
    public const double WindowSeconds = 180;

    public static TrackAnalysis Analyze(string path, CancellationToken cancel = default)
    {
        var tags = Id3.Read(path);
        tags.TryGetValue("TIT2", out var title);
        tags.TryGetValue("TPE1", out var artist);
        tags.TryGetValue("TBPM", out var tagBpm);
        tags.TryGetValue("TKEY", out var tagKey);

        double bpm = 0;
        var bpmSource = ValueSource.None;
        double bpmConfidence = 0;

        if (double.TryParse(tagBpm, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 20 and < 400)
        {
            bpm = parsed;
            bpmSource = ValueSource.Tag;
            bpmConfidence = 1;
        }

        string? key = Keys.Normalize(tagKey);
        var keySource = key is null ? ValueSource.None : ValueSource.Tag;
        double keyConfidence = key is null ? 0 : 1;
        double tuningCents = 0;

        // Only decode when something is actually missing.
        if (bpmSource == ValueSource.None || keySource == ValueSource.None)
        {
            float[] mono;
            try { mono = DecodeMono(path, AnalysisRate, WindowSeconds, cancel); }
            catch (ClipLoadException) { mono = Array.Empty<float>(); }

            if (mono.Length > AnalysisRate * 5)
            {
                if (bpmSource == ValueSource.None)
                {
                    var (env, fps) = OnsetEnvelope.Compute(mono, AnalysisRate);
                    var tempo = TempoDetector.Detect(env, fps);
                    if (tempo.Bpm > 0)
                    {
                        bpm = Math.Round(tempo.Bpm, 2);
                        bpmSource = ValueSource.Detected;
                        bpmConfidence = tempo.Confidence;
                    }
                }

                if (keySource == ValueSource.None)
                {
                    tuningCents = KeyDetector.EstimateTuningCents(mono, AnalysisRate);
                    var k = KeyDetector.Detect(mono, AnalysisRate);
                    key = k.Key;
                    keySource = ValueSource.Detected;
                    keyConfidence = k.Confidence;
                }
            }
        }

        return new TrackAnalysis
        {
            Bpm = bpm,
            BpmSource = bpmSource,
            BpmConfidence = bpmConfidence,
            Key = key,
            Camelot = Keys.ToCamelot(key),
            KeySource = keySource,
            KeyConfidence = keyConfidence,
            TuningCents = tuningCents,
            Title = title,
            Artist = artist,
        };
    }

    /// <summary>Speed multiplier that takes <paramref name="fromBpm"/> to <paramref name="toBpm"/>.</summary>
    public static double SpeedToMatch(double fromBpm, double toBpm)
        => fromBpm <= 0 || toBpm <= 0 ? 1.0 : toBpm / fromBpm;

    /// <summary>
    /// If two tempos are nearly a factor of two apart, the detector or the tagger probably
    /// disagreed about half-time; folding brings them into the same octave before matching.
    /// </summary>
    public static double FoldToNearestOctave(double bpm, double reference)
    {
        if (bpm <= 0 || reference <= 0) return bpm;
        double best = bpm, bestDistance = Math.Abs(Math.Log2(bpm / reference));
        foreach (var factor in new[] { 0.5, 2.0, 4.0, 0.25 })
        {
            double candidate = bpm * factor;
            if (candidate is < 40 or > 260) continue;
            double d = Math.Abs(Math.Log2(candidate / reference));
            if (d < bestDistance) { bestDistance = d; best = candidate; }
        }
        return best;
    }

    internal static float[] DecodeMono(string path, int rate, double maxSeconds, CancellationToken cancel)
    {
        var clip = AudioClip.Load(path, rate, null, cancel);
        long frames = clip.FrameCount;
        if (maxSeconds > 0) frames = Math.Min(frames, (long)(maxSeconds * rate));

        var mono = new float[frames];
        var s = clip.Samples;
        for (long f = 0; f < frames; f++) mono[f] = 0.5f * (s[f * 2] + s[f * 2 + 1]);
        return mono;
    }
}

/// <summary>Minimal ID3v2 text-frame reader — TIT2, TPE1, TBPM, TKEY.</summary>
public static class Id3
{
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[10];
            if (fs.Read(head, 0, 10) != 10) return result;
            if (head[0] != 'I' || head[1] != 'D' || head[2] != '3') return result;

            int size = 0;
            for (int i = 6; i < 10; i++) size = (size << 7) | (head[i] & 0x7F);
            if (size <= 0 || size > 50_000_000) return result;

            var data = new byte[size];
            if (fs.Read(data, 0, size) != size) return result;

            int p = 0;
            while (p + 10 <= data.Length)
            {
                var id = Encoding.ASCII.GetString(data, p, 4);
                if (id[0] == '\0') break;
                int fsz = (data[p + 4] << 24) | (data[p + 5] << 16) | (data[p + 6] << 8) | data[p + 7];
                if (fsz <= 0 || p + 10 + fsz > data.Length) break;

                if (id[0] == 'T' && fsz > 1)
                {
                    byte enc = data[p + 10];
                    var body = data.AsSpan(p + 11, fsz - 1);
                    string text = enc switch
                    {
                        1 => Encoding.Unicode.GetString(body),
                        2 => Encoding.BigEndianUnicode.GetString(body),
                        3 => Encoding.UTF8.GetString(body),
                        _ => Encoding.Latin1.GetString(body),
                    };
                    text = text.TrimEnd('\0').Trim().TrimStart('﻿');
                    if (text.Length > 0 && !result.ContainsKey(id)) result[id] = text;
                }
                p += 10 + fsz;
            }
        }
        catch { /* a tagless or malformed file simply has no tags */ }
        return result;
    }
}

/// <summary>
/// Half-wave-rectified spectral flux over a log-compressed spectrum, local-mean subtracted.
/// The standard onset front end, and the input to tempo detection.
/// </summary>
public static class OnsetEnvelope
{
    public const int FftSize = 2048;
    public const int Hop = 512;

    public static (float[] Env, double Fps) Compute(float[] x, int rate)
    {
        int m = (int)Math.Log2(FftSize);
        var window = new float[FftSize];
        for (int i = 0; i < FftSize; i++) window[i] = (float)FastFourierTransform.HammingWindow(i, FftSize);

        int frames = Math.Max(0, (x.Length - FftSize) / Hop);
        if (frames == 0) return (Array.Empty<float>(), rate / (double)Hop);

        var raw = new float[frames];
        var prev = new float[FftSize / 2];
        var cur = new float[FftSize / 2];
        var fft = new Complex[FftSize];

        for (int f = 0; f < frames; f++)
        {
            int off = f * Hop;
            for (int i = 0; i < FftSize; i++) { fft[i].X = x[off + i] * window[i]; fft[i].Y = 0; }
            FastFourierTransform.FFT(true, m, fft);

            double flux = 0;
            for (int k = 0; k < FftSize / 2; k++)
            {
                double re = fft[k].X, im = fft[k].Y;
                cur[k] = (float)Math.Log(1 + 1000 * Math.Sqrt(re * re + im * im));
                float d = cur[k] - prev[k];
                if (d > 0) flux += d;
            }
            raw[f] = (float)flux;
            (prev, cur) = (cur, prev);
        }

        const int W = 16;
        var env = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int a = Math.Max(0, i - W), b = Math.Min(frames - 1, i + W);
            double mean = 0;
            for (int j = a; j <= b; j++) mean += raw[j];
            mean /= (b - a + 1);
            env[i] = (float)Math.Max(0, raw[i] - mean);
        }
        return (env, rate / (double)Hop);
    }
}

public readonly record struct TempoResult(double Bpm, double Confidence);

/// <summary>
/// Tempo by autocorrelation of the onset envelope, reinforced by a comb over integer multiples of
/// each candidate period and weighted by a log-Gaussian prior around 120 BPM, then parabolically
/// refined. Measured at 94% exact and 0% wrong on 34 Serato-tagged tracks.
/// </summary>
public static class TempoDetector
{
    public const double MinBpm = 60, MaxBpm = 200;

    public static TempoResult Detect(float[] env, double fps, double priorCenter = 120, double priorWidthOctaves = 0.9)
    {
        if (env.Length < 64) return new TempoResult(0, 0);

        int minLag = (int)Math.Floor(60.0 * fps / MaxBpm);
        int maxLag = (int)Math.Ceiling(60.0 * fps / MinBpm);
        maxLag = Math.Min(maxLag, env.Length / 2);
        if (maxLag <= minLag + 2) return new TempoResult(0, 0);

        double mu = 0;
        foreach (var v in env) mu += v;
        mu /= env.Length;

        var e = new double[env.Length];
        for (int i = 0; i < env.Length; i++) e[i] = env[i] - mu;

        double norm0 = 0;
        foreach (var v in e) norm0 += v * v;
        if (norm0 <= 0) return new TempoResult(0, 0);

        int acLen = Math.Min(e.Length - 1, maxLag * 4 + 2);
        var ac = new double[acLen + 1];
        for (int lag = minLag; lag <= acLen; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < e.Length; i++) s += e[i] * e[i + lag];
            ac[lag] = s / norm0;
        }

        var score = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double comb = ac[lag];
            for (int mult = 2; mult <= 4; mult++)
            {
                int l2 = lag * mult;
                if (l2 <= acLen) comb += ac[l2] * 0.5 / mult;
            }
            double bpm = 60.0 * fps / lag;
            double prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / priorCenter) / priorWidthOctaves, 2));
            score[lag] = comb * prior;
        }

        int best = minLag;
        double bestV = double.NegativeInfinity;
        for (int lag = minLag; lag <= maxLag; lag++)
            if (score[lag] > bestV) { bestV = score[lag]; best = lag; }

        double refined = best;
        if (best > minLag && best < maxLag)
        {
            double y0 = score[best - 1], y1 = score[best], y2 = score[best + 1];
            double den = y0 - 2 * y1 + y2;
            if (Math.Abs(den) > 1e-12)
            {
                double delta = 0.5 * (y0 - y2) / den;
                if (Math.Abs(delta) <= 1) refined = best + delta;
            }
        }

        double second = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
            if (Math.Abs(lag - best) > 3 && score[lag] > second) second = score[lag];

        double conf = (bestV > 0 && second > 0) ? Math.Max(0, 1 - second / bestV) : (bestV > 0 ? 1 : 0);
        return new TempoResult(60.0 * fps / refined, conf);
    }
}

public readonly record struct KeyResult(string Key, double Confidence);

/// <summary>
/// Chroma matched against key profiles. Measured at 47% exact on tagged material, which is why
/// its output is always presented as an estimate — see <see cref="Analyzer"/>.
/// </summary>
public static class KeyDetector
{
    // Triad-weighted profiles scored best of five sets on the calibration library.
    static readonly double[] Major = { 5.0, 1.0, 2.5, 1.2, 4.0, 2.8, 1.2, 4.2, 1.3, 2.6, 1.6, 2.2 };
    static readonly double[] Minor = { 5.0, 1.0, 2.5, 4.0, 1.2, 2.8, 1.2, 4.2, 2.6, 1.3, 2.6, 1.8 };

    const int N = 8192, Hop = 4096;

    public static double EstimateTuningCents(float[] x, int rate)
    {
        int m = (int)Math.Log2(N);
        var win = Window();
        var fft = new Complex[N];
        double sumSin = 0, sumCos = 0;

        for (int off = 0; off + N < x.Length; off += Hop)
        {
            Fill(fft, x, off, win);
            FastFourierTransform.FFT(true, m, fft);
            for (int k = 2; k < N / 2 - 1; k++)
            {
                double f = k * (double)rate / N;
                if (f is < 65 or > 2200) continue;
                double mag = Mag(fft, k);
                if (mag <= Mag(fft, k - 1) || mag < Mag(fft, k + 1)) continue;   // peaks only
                double pitch = 69 + 12 * Math.Log2(f / 440.0);
                double ang = 2 * Math.PI * (pitch - Math.Round(pitch));
                sumSin += mag * Math.Sin(ang);
                sumCos += mag * Math.Cos(ang);
            }
        }
        if (sumSin == 0 && sumCos == 0) return 0;
        return Math.Atan2(sumSin, sumCos) / (2 * Math.PI) * 100;
    }

    public static double[] Chroma(float[] x, int rate)
    {
        int m = (int)Math.Log2(N);
        var win = Window();
        var fft = new Complex[N];
        var chroma = new double[12];

        for (int off = 0; off + N < x.Length; off += Hop)
        {
            Fill(fft, x, off, win);
            FastFourierTransform.FFT(true, m, fft);
            for (int k = 1; k < N / 2; k++)
            {
                double f = k * (double)rate / N;
                if (f is < 55 or > 2000) continue;
                double pitch = 69 + 12 * Math.Log2(f / 440.0);
                int pc = (int)Math.Round(pitch) % 12;
                if (pc < 0) pc += 12;
                chroma[pc] += Mag(fft, k);
            }
        }
        double sum = 0;
        foreach (var v in chroma) sum += v;
        if (sum > 0) for (int i = 0; i < 12; i++) chroma[i] /= sum;
        return chroma;
    }

    public static KeyResult Detect(float[] x, int rate)
    {
        var c = Chroma(x, rate);
        double bestR = double.NegativeInfinity, second = double.NegativeInfinity;
        int bestT = 0;
        bool bestMinor = false;

        for (int t = 0; t < 12; t++)
        {
            foreach (var minor in new[] { false, true })
            {
                var prof = minor ? Minor : Major;
                var rot = new double[12];
                for (int i = 0; i < 12; i++) rot[i] = prof[((i - t) % 12 + 12) % 12];
                double r = Pearson(c, rot);
                if (r > bestR) { second = bestR; bestR = r; bestT = t; bestMinor = minor; }
                else if (r > second) second = r;
            }
        }
        return new KeyResult(Keys.Names[bestT] + (bestMinor ? "m" : ""), Math.Max(0, bestR - second));
    }

    static float[] Window()
    {
        var w = new float[N];
        for (int i = 0; i < N; i++) w[i] = (float)FastFourierTransform.HammingWindow(i, N);
        return w;
    }

    static void Fill(Complex[] fft, float[] x, int off, float[] win)
    {
        for (int i = 0; i < N; i++) { fft[i].X = x[off + i] * win[i]; fft[i].Y = 0; }
    }

    static double Mag(Complex[] fft, int k) => Math.Sqrt(fft[k].X * (double)fft[k].X + fft[k].Y * (double)fft[k].Y);

    static double Pearson(double[] a, double[] b)
    {
        double ma = 0, mb = 0;
        for (int i = 0; i < a.Length; i++) { ma += a[i]; mb += b[i]; }
        ma /= a.Length; mb /= b.Length;
        double sa = 0, sb = 0, sab = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sa += da * da; sb += db * db; sab += da * db;
        }
        return (sa <= 0 || sb <= 0) ? 0 : sab / Math.Sqrt(sa * sb);
    }
}
