using NAudio.Dsp;

namespace Benchmix.Analysis;

/// <summary>
/// Key detection v2. Three changes over the naive chroma+Krumhansl version:
///
///  1. <b>Spectral peak picking</b> — only local maxima contribute, so broadband drums and noise
///     stop smearing energy across every pitch class. Magnitudes are square-rooted so one loud
///     bass note cannot dominate the whole profile.
///  2. <b>Tuning estimation</b> — records aren't all at A=440. The circular mean of each peak's
///     deviation from the nearest equal-tempered semitone gives the track's tuning offset, which
///     is then removed before folding into pitch classes. Without this a track tuned a third of
///     a semitone sharp lands its tonic in the neighbouring bin.
///  3. <b>Harmonic de-weighting</b> — a pitched note puts energy at 2f, 3f, 4f… Each peak votes
///     for its own pitch class and, with decreasing weight, subtracts from the classes its
///     harmonics would otherwise fake.
///
/// The profile set is selectable so it can be chosen on evidence rather than by reputation.
/// </summary>
public static class KeyV2
{
    public static readonly string[] Names = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B" };
    static readonly int[] MajorCamelot = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };
    static readonly int[] MinorCamelot = { 5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10 };

    public sealed record Profile(string Name, double[] Major, double[] Minor);

    public static readonly Profile Krumhansl = new("Krumhansl-Kessler",
        new[] { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 },
        new[] { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 });

    public static readonly Profile Temperley = new("Temperley",
        new[] { 5.0, 2.0, 3.5, 2.0, 4.5, 4.0, 2.0, 4.5, 2.0, 3.5, 1.5, 4.0 },
        new[] { 5.0, 2.0, 3.5, 4.5, 2.0, 4.0, 2.0, 4.5, 3.5, 2.0, 1.5, 4.0 });

    public static readonly Profile Shaath = new("Shaath",
        new[] { 6.6, 2.0, 3.5, 2.3, 4.6, 4.0, 2.5, 5.2, 2.4, 3.7, 2.3, 3.4 },
        new[] { 6.5, 2.7, 3.5, 5.4, 2.6, 3.5, 2.5, 5.2, 4.0, 2.7, 4.3, 3.2 });

    public static readonly Profile AlbrechtShanahan = new("Albrecht-Shanahan",
        new[] { 0.238, 0.006, 0.111, 0.006, 0.137, 0.094, 0.016, 0.214, 0.009, 0.080, 0.008, 0.081 },
        new[] { 0.220, 0.006, 0.104, 0.123, 0.019, 0.103, 0.012, 0.214, 0.062, 0.022, 0.061, 0.052 });

    /// <summary>Triad-weighted: emphasises the three chord tones that actually define the mode.</summary>
    public static readonly Profile Triadic = new("Triadic",
        new[] { 5.0, 1.0, 2.5, 1.2, 4.0, 2.8, 1.2, 4.2, 1.3, 2.6, 1.6, 2.2 },
        new[] { 5.0, 1.0, 2.5, 4.0, 1.2, 2.8, 1.2, 4.2, 2.6, 1.3, 2.6, 1.8 });

    public static readonly Profile[] All = { Krumhansl, Temperley, Shaath, AlbrechtShanahan, Triadic };

    const int N = 8192, Hop = 4096;
    const double MinHz = 65, MaxHz = 2200;

    /// <summary>Tuning deviation of the track in semitone fractions, in [-0.5, 0.5].</summary>
    public static double EstimateTuning(float[] x, int rate)
    {
        int m = (int)Math.Log2(N);
        var win = BuildWindow();
        var fft = new Complex[N];
        double sumSin = 0, sumCos = 0;

        for (int off = 0; off + N < x.Length; off += Hop)
        {
            Fill(fft, x, off, win);
            FastFourierTransform.FFT(true, m, fft);
            foreach (var (freq, mag) in Peaks(fft, rate))
            {
                double pitch = 69 + 12 * Math.Log2(freq / 440.0);
                double dev = pitch - Math.Round(pitch);          // [-0.5, 0.5] semitones
                double ang = 2 * Math.PI * dev;                  // wrap to a circle so the mean is circular
                sumSin += mag * Math.Sin(ang);
                sumCos += mag * Math.Cos(ang);
            }
        }
        if (sumSin == 0 && sumCos == 0) return 0;
        return Math.Atan2(sumSin, sumCos) / (2 * Math.PI);
    }

    public static double[] Chroma(float[] x, int rate, double tuningOffset)
    {
        int m = (int)Math.Log2(N);
        var win = BuildWindow();
        var fft = new Complex[N];
        var chroma = new double[12];

        for (int off = 0; off + N < x.Length; off += Hop)
        {
            Fill(fft, x, off, win);
            FastFourierTransform.FFT(true, m, fft);
            foreach (var (freq, mag) in Peaks(fft, rate))
            {
                double pitch = 69 + 12 * Math.Log2(freq / 440.0) - tuningOffset;
                double w = Math.Sqrt(mag);

                int pc = Mod12((int)Math.Round(pitch));
                chroma[pc] += w;

                // A real note also lit up 2f (same class), 3f (+7), 4f (same), 5f (+4).
                // Remove a little of what those would have faked elsewhere.
                chroma[Mod12(pc + 7)] -= w * 0.18;
                chroma[Mod12(pc + 4)] -= w * 0.10;
            }
        }

        for (int i = 0; i < 12; i++) if (chroma[i] < 0) chroma[i] = 0;
        double sum = 0;
        foreach (var v in chroma) sum += v;
        if (sum > 0) for (int i = 0; i < 12; i++) chroma[i] /= sum;
        return chroma;
    }

    public static (string Name, string Camelot, double Confidence) Detect(float[] x, int rate, Profile profile)
    {
        double tuning = EstimateTuning(x, rate);
        var c = Chroma(x, rate, tuning);
        return FromChroma(c, profile);
    }

    public static (string Name, string Camelot, double Confidence) FromChroma(double[] c, Profile profile)
    {
        double bestR = double.NegativeInfinity, second = double.NegativeInfinity;
        int bestT = 0;
        bool bestMin = false;

        for (int t = 0; t < 12; t++)
        {
            foreach (var minor in new[] { false, true })
            {
                var prof = minor ? profile.Minor : profile.Major;
                var rot = new double[12];
                for (int i = 0; i < 12; i++) rot[i] = prof[Mod12(i - t)];
                double r = Pearson(c, rot);
                if (r > bestR) { second = bestR; bestR = r; bestT = t; bestMin = minor; }
                else if (r > second) second = r;
            }
        }

        string name = Names[bestT] + (bestMin ? "m" : "");
        string cam = (bestMin ? MinorCamelot[bestT] : MajorCamelot[bestT]).ToString() + (bestMin ? "A" : "B");
        return (name, cam, Math.Max(0, bestR - second));
    }

    // ---- helpers ----

    static float[] BuildWindow()
    {
        var w = new float[N];
        for (int i = 0; i < N; i++) w[i] = (float)FastFourierTransform.HammingWindow(i, N);
        return w;
    }

    static void Fill(Complex[] fft, float[] x, int off, float[] win)
    {
        for (int i = 0; i < N; i++) { fft[i].X = x[off + i] * win[i]; fft[i].Y = 0; }
    }

    /// <summary>Local maxima above a floor, with parabolic frequency refinement.</summary>
    static IEnumerable<(double Freq, double Mag)> Peaks(Complex[] fft, int rate)
    {
        int kMin = Math.Max(2, (int)(MinHz * N / rate));
        int kMax = Math.Min(N / 2 - 2, (int)(MaxHz * N / rate));

        var mags = new double[N / 2];
        double maxMag = 0;
        for (int k = kMin - 1; k <= kMax + 1 && k < N / 2; k++)
        {
            double re = fft[k].X, im = fft[k].Y;
            mags[k] = Math.Sqrt(re * re + im * im);
            if (mags[k] > maxMag) maxMag = mags[k];
        }
        if (maxMag <= 0) yield break;
        double floor = maxMag * 0.02;

        for (int k = kMin; k <= kMax; k++)
        {
            if (mags[k] < floor) continue;
            if (mags[k] <= mags[k - 1] || mags[k] < mags[k + 1]) continue;

            double y0 = mags[k - 1], y1 = mags[k], y2 = mags[k + 1];
            double den = y0 - 2 * y1 + y2;
            double delta = Math.Abs(den) > 1e-18 ? 0.5 * (y0 - y2) / den : 0;
            if (Math.Abs(delta) > 1) delta = 0;
            yield return ((k + delta) * rate / (double)N, y1);
        }
    }

    static int Mod12(int i) => ((i % 12) + 12) % 12;

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
