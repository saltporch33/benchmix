using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Benchmix.Analysis;

/// <summary>Decodes any Media Foundation source to mono float at a chosen analysis rate.</summary>
public static class Decode
{
    public static float[] ToMono(string path, int rate, double maxSeconds = 0)
    {
        using var reader = new MediaFoundationReader(path);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != rate)
            sp = new WdlResamplingSampleProvider(sp, rate);

        int cap = maxSeconds > 0 ? (int)(maxSeconds * rate) : int.MaxValue;
        var buf = new List<float>(Math.Min(cap, rate * 300));
        var tmp = new float[rate];
        int n;
        while (buf.Count < cap && (n = sp.Read(tmp.AsSpan())) > 0)
        {
            int take = Math.Min(n, cap - buf.Count);
            for (int i = 0; i < take; i++) buf.Add(tmp[i]);
        }
        return buf.ToArray();
    }
}

/// <summary>
/// Onset-strength envelope: half-wave-rectified spectral flux over a log-compressed magnitude
/// spectrum, then local-mean subtracted. Standard front end for tempo and beat tracking.
/// </summary>
public static class OnsetEnvelope
{
    public const int FftSize = 2048;
    public const int Hop = 512;

    public static (float[] Env, double Fps) Compute(float[] x, int rate)
    {
        int m = (int)Math.Log2(FftSize);
        var window = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            window[i] = (float)FastFourierTransform.HammingWindow(i, FftSize);

        int frames = Math.Max(0, (x.Length - FftSize) / Hop);
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
                double mag = Math.Sqrt(re * re + im * im);
                cur[k] = (float)Math.Log(1 + 1000 * mag);
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
/// Tempo by autocorrelation of the onset envelope, reinforced by a comb over integer multiples
/// of each candidate period, weighted by a log-Gaussian perceptual prior that resolves the usual
/// half/double-time ambiguity, then parabolically refined.
/// </summary>
public static class TempoDetector
{
    public const double MinBpm = 60, MaxBpm = 200;

    public static TempoResult Detect(float[] env, double fps, double priorCenter = 120, double priorWidthOctaves = 0.9)
    {
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

        // autocorrelation once, for every lag we might touch
        var ac = new double[maxLag * 4 + 2];
        for (int lag = minLag; lag < ac.Length; lag++)
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
                if (l2 < ac.Length) comb += ac[l2] * 0.5 / mult;
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

public readonly record struct KeyResult(string Name, string Camelot, double Confidence);

/// <summary>Chroma folded over A1..B6, matched against Krumhansl-Schmuckler key profiles.</summary>
public static class KeyDetector
{
    static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    static readonly string[] Names = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B" };
    static readonly int[] MajorCamelot = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };
    static readonly int[] MinorCamelot = { 5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10 };

    public static double[] Chroma(float[] x, int rate)
    {
        const int N = 8192, Hop = 4096;
        int m = (int)Math.Log2(N);
        var win = new float[N];
        for (int i = 0; i < N; i++) win[i] = (float)FastFourierTransform.HammingWindow(i, N);

        var chroma = new double[12];
        var fft = new Complex[N];
        for (int off = 0; off + N < x.Length; off += Hop)
        {
            for (int i = 0; i < N; i++) { fft[i].X = x[off + i] * win[i]; fft[i].Y = 0; }
            FastFourierTransform.FFT(true, m, fft);
            for (int k = 1; k < N / 2; k++)
            {
                double f = k * (double)rate / N;
                if (f < 55 || f > 2000) continue;
                double re = fft[k].X, im = fft[k].Y;
                double mag = Math.Sqrt(re * re + im * im);
                double pitch = 69 + 12 * Math.Log2(f / 440.0);
                int pc = (int)Math.Round(pitch) % 12;
                if (pc < 0) pc += 12;
                chroma[pc] += mag;
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
        bool bestMin = false;

        for (int t = 0; t < 12; t++)
        {
            foreach (var minor in new[] { false, true })
            {
                var prof = minor ? Minor : Major;
                var rot = new double[12];
                for (int i = 0; i < 12; i++) rot[i] = prof[((i - t) % 12 + 12) % 12];
                double r = Pearson(c, rot);
                if (r > bestR) { second = bestR; bestR = r; bestT = t; bestMin = minor; }
                else if (r > second) second = r;
            }
        }

        string name = Names[bestT] + (bestMin ? "m" : "");
        string cam = (bestMin ? MinorCamelot[bestT] : MajorCamelot[bestT]).ToString() + (bestMin ? "A" : "B");
        return new KeyResult(name, cam, Math.Max(0, bestR - second));
    }

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

    /// <summary>Normalises Serato/djay tag spellings — "F#m", "Ebm", "1A", "8B" — to one comparable form.</summary>
    public static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();

        var cam = System.Text.RegularExpressions.Regex.Match(tag, "^([0-9]{1,2})([ABab])$");
        if (cam.Success)
        {
            int n = int.Parse(cam.Groups[1].Value);
            bool minor = cam.Groups[2].Value.ToUpperInvariant() == "A";
            var table = minor ? MinorCamelot : MajorCamelot;
            for (int t = 0; t < 12; t++)
                if (table[t] == n) return Names[t] + (minor ? "m" : "");
            return null;
        }

        var mm = System.Text.RegularExpressions.Regex.Match(tag, "^([A-Ga-g][#b]?)[ ]*(m|min|minor)?$");
        if (!mm.Success) return null;

        string root = char.ToUpperInvariant(mm.Groups[1].Value[0]) + mm.Groups[1].Value.Substring(1);
        var enharmonic = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Db"] = "C#", ["D#"] = "Eb", ["Gb"] = "F#", ["Ab"] = "G#", ["A#"] = "Bb", ["Cb"] = "B", ["Fb"] = "E",
        };
        if (enharmonic.TryGetValue(root, out var norm)) root = norm;
        return root + (mm.Groups[2].Success ? "m" : "");
    }

    public static string CamelotOf(string normalized)
    {
        bool minor = normalized.EndsWith("m", StringComparison.Ordinal);
        string root = minor ? normalized[..^1] : normalized;
        int t = Array.IndexOf(Names, root);
        if (t < 0) return "?";
        return (minor ? MinorCamelot[t] : MajorCamelot[t]).ToString() + (minor ? "A" : "B");
    }

    /// <summary>Semitone distance from a to b, in [-6, 5] — the smallest shift that gets there.</summary>
    public static int SemitonesBetween(string a, string b)
    {
        int ta = Array.IndexOf(Names, a.EndsWith("m", StringComparison.Ordinal) ? a[..^1] : a);
        int tb = Array.IndexOf(Names, b.EndsWith("m", StringComparison.Ordinal) ? b[..^1] : b);
        if (ta < 0 || tb < 0) return 0;
        int d = (tb - ta + 12) % 12;
        return d > 6 ? d - 12 : d;
    }
}
