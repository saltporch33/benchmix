namespace Benchmix.Engine.Dsp;

/// <summary>
/// One biquad section in transposed direct form II, double precision.
///
/// TDF-II is used rather than DF-I because it needs only two state variables per channel and has
/// better numerical behaviour at low cutoff frequencies — which matters here, since the low band
/// of the EQ sits at 246 Hz and will be running at 48 kHz, where DF-I can lose precision in the
/// coefficient differences.
///
/// Coefficients follow the RBJ Audio EQ Cookbook, already normalised by a0.
/// </summary>
public struct Biquad
{
    public double B0, B1, B2, A1, A2;
    private double _z1, _z2;

    public static Biquad Identity => new() { B0 = 1, B1 = 0, B2 = 0, A1 = 0, A2 = 0 };

    public void Reset() { _z1 = 0; _z2 = 0; }

    /// <summary>Processes one sample. Hot path — keep it branch-free.</summary>
    public double Process(double x)
    {
        double y = B0 * x + _z1;
        _z1 = B1 * x - A1 * y + _z2;
        _z2 = B2 * x - A2 * y;
        return y;
    }

    /// <summary>Complex magnitude response at <paramref name="hz"/>, for tests and for drawing the curve.</summary>
    public readonly double MagnitudeAt(double hz, double sampleRate)
    {
        double w = 2 * Math.PI * hz / sampleRate;
        double cw = Math.Cos(w), sw = Math.Sin(w);
        double c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = B0 + B1 * cw + B2 * c2, ni = -(B1 * sw + B2 * s2);
        double dr = 1 + A1 * cw + A2 * c2, di = -(A1 * sw + A2 * s2);
        double num = Math.Sqrt(nr * nr + ni * ni);
        double den = Math.Sqrt(dr * dr + di * di);
        return den == 0 ? 0 : num / den;
    }

    // ---------------- RBJ Audio EQ Cookbook ----------------
    // Verified against webaudio.github.io/Audio-EQ-Cookbook. H(z) = (b0 + b1 z^-1 + b2 z^-2)
    //                                                            / (a0 + a1 z^-1 + a2 z^-2)

    public static Biquad LowPass(double hz, double q, double sampleRate)
    {
        var (w0, cw, alpha) = Common(hz, q, sampleRate);
        double a0 = 1 + alpha;
        return Normalise((1 - cw) / 2, 1 - cw, (1 - cw) / 2, a0, -2 * cw, 1 - alpha);
    }

    public static Biquad HighPass(double hz, double q, double sampleRate)
    {
        var (w0, cw, alpha) = Common(hz, q, sampleRate);
        double a0 = 1 + alpha;
        return Normalise((1 + cw) / 2, -(1 + cw), (1 + cw) / 2, a0, -2 * cw, 1 - alpha);
    }

    public static Biquad Peaking(double hz, double q, double dbGain, double sampleRate)
    {
        double a = Math.Pow(10, dbGain / 40);
        var (w0, cw, alpha) = Common(hz, q, sampleRate);
        return Normalise(1 + alpha * a, -2 * cw, 1 - alpha * a,
                         1 + alpha / a, -2 * cw, 1 - alpha / a);
    }

    public static Biquad LowShelf(double hz, double slope, double dbGain, double sampleRate)
    {
        double a = Math.Pow(10, dbGain / 40);
        double w0 = 2 * Math.PI * hz / sampleRate, cw = Math.Cos(w0);
        double alpha = Math.Sin(w0) / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
        double twoSqrtAlpha = 2 * Math.Sqrt(a) * alpha;
        return Normalise(
            a * ((a + 1) - (a - 1) * cw + twoSqrtAlpha),
            2 * a * ((a - 1) - (a + 1) * cw),
            a * ((a + 1) - (a - 1) * cw - twoSqrtAlpha),
            (a + 1) + (a - 1) * cw + twoSqrtAlpha,
            -2 * ((a - 1) + (a + 1) * cw),
            (a + 1) + (a - 1) * cw - twoSqrtAlpha);
    }

    public static Biquad HighShelf(double hz, double slope, double dbGain, double sampleRate)
    {
        double a = Math.Pow(10, dbGain / 40);
        double w0 = 2 * Math.PI * hz / sampleRate, cw = Math.Cos(w0);
        double alpha = Math.Sin(w0) / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
        double twoSqrtAlpha = 2 * Math.Sqrt(a) * alpha;
        return Normalise(
            a * ((a + 1) + (a - 1) * cw + twoSqrtAlpha),
            -2 * a * ((a - 1) + (a + 1) * cw),
            a * ((a + 1) + (a - 1) * cw - twoSqrtAlpha),
            (a + 1) - (a - 1) * cw + twoSqrtAlpha,
            2 * ((a - 1) - (a + 1) * cw),
            (a + 1) - (a - 1) * cw - twoSqrtAlpha);
    }

    static (double W0, double CosW0, double Alpha) Common(double hz, double q, double sampleRate)
    {
        double w0 = 2 * Math.PI * Math.Clamp(hz, 1, sampleRate * 0.49) / sampleRate;
        return (w0, Math.Cos(w0), Math.Sin(w0) / (2 * Math.Max(1e-4, q)));
    }

    static Biquad Normalise(double b0, double b1, double b2, double a0, double a1, double a2)
        => new() { B0 = b0 / a0, B1 = b1 / a0, B2 = b2 / a0, A1 = a1 / a0, A2 = a2 / a0 };
}

/// <summary>A cascade of biquads applied in series, with per-channel state.</summary>
public sealed class BiquadCascade
{
    private readonly Biquad[][] _sections;   // [channel][section]

    public BiquadCascade(IReadOnlyList<Biquad> prototype, int channels)
    {
        _sections = new Biquad[channels][];
        for (int c = 0; c < channels; c++)
        {
            _sections[c] = new Biquad[prototype.Count];
            for (int s = 0; s < prototype.Count; s++) _sections[c][s] = prototype[s];
        }
        Order = prototype.Count * 2;
    }

    public int Order { get; }
    public int Channels => _sections.Length;

    public void Reset()
    {
        for (int c = 0; c < _sections.Length; c++)
            for (int s = 0; s < _sections[c].Length; s++)
                _sections[c][s].Reset();
    }

    public double Process(int channel, double x)
    {
        var sec = _sections[channel];
        for (int s = 0; s < sec.Length; s++) x = sec[s].Process(x);
        return x;
    }

    /// <summary>Magnitude response of the whole cascade, for tests and the UI curve.</summary>
    public double MagnitudeAt(double hz, double sampleRate)
    {
        double m = 1;
        foreach (var s in _sections[0]) m *= s.MagnitudeAt(hz, sampleRate);
        return m;
    }
}

/// <summary>
/// Analog filter prototypes turned into biquad cascades by the bilinear transform.
///
/// Bessel is the one that matters for Benchmix: the EQ is a subtractive crossover, where the
/// high band is formed as <c>x - lowpass(x)</c>. That subtraction only behaves like a real
/// highpass if the lowpass has near-constant group delay across the band, which is exactly the
/// property Bessel has and Butterworth does not.
///
/// The pole set is not taken from a table. The Bessel polynomial roots are hardcoded but then
/// <b>verified</b> against the polynomial's own coefficients (Vieta's formulas) at construction,
/// and the -3 dB normalisation frequency is found numerically by bisection rather than looked up.
/// A wrong constant therefore fails loudly instead of quietly detuning the crossover.
/// </summary>
public static class FilterDesign
{
    /// <summary>Conjugate pole pairs of the delay-normalised Bessel polynomial, one entry per pair.</summary>
    static readonly (double Re, double Im)[] Bessel4Poles =
    {
        (-2.103789, 2.657418),
        (-2.896211, 0.867234),
    };

    /// <summary>B4(s) = s^4 + 10 s^3 + 45 s^2 + 105 s + 105.</summary>
    static readonly double[] Bessel4Coefficients = { 1, 10, 45, 105, 105 };

    /// <summary>
    /// 4th-order Bessel lowpass as two cascaded biquads, -3 dB at <paramref name="cutoffHz"/>.
    /// </summary>
    public static Biquad[] Bessel4LowPass(double cutoffHz, double sampleRate)
    {
        VerifyBesselPoles();
        double scale = Bessel4MinusThreeDbFrequency();   // where the prototype hits -3 dB

        var sections = new Biquad[Bessel4Poles.Length];
        for (int i = 0; i < Bessel4Poles.Length; i++)
        {
            var (re, im) = Bessel4Poles[i];
            double wn = Math.Sqrt(re * re + im * im) / scale;   // normalised so -3 dB lands at 1
            double q = Math.Sqrt(re * re + im * im) / (2 * Math.Abs(re));
            sections[i] = Biquad.LowPass(cutoffHz * wn, q, sampleRate);
        }
        return sections;
    }

    /// <summary>Butterworth lowpass of even order, as order/2 cascaded biquads.</summary>
    public static Biquad[] ButterworthLowPass(int order, double cutoffHz, double sampleRate)
    {
        if (order % 2 != 0 || order < 2) throw new ArgumentOutOfRangeException(nameof(order), "even order >= 2");
        int pairs = order / 2;
        var sections = new Biquad[pairs];
        for (int k = 0; k < pairs; k++)
        {
            // Butterworth poles are evenly spaced on the unit circle in the left half plane.
            double theta = Math.PI * (2.0 * k + 1) / (2.0 * order);
            double q = 1.0 / (2.0 * Math.Sin(theta));
            sections[k] = Biquad.LowPass(cutoffHz, q, sampleRate);
        }
        return sections;
    }

    /// <summary>Linkwitz-Riley 4th order = two identical Butterworth 2nd-order stages.</summary>
    public static Biquad[] LinkwitzRiley4LowPass(double cutoffHz, double sampleRate)
    {
        var b = Biquad.LowPass(cutoffHz, Math.Sqrt(0.5), sampleRate);
        return new[] { b, b };
    }

    // ---- self-checks ----

    static bool _verified;

    /// <summary>
    /// Confirms the hardcoded poles really are the roots of B4 by rebuilding the polynomial from
    /// them. Cheap, runs once, and turns a typo into an immediate exception.
    /// </summary>
    static void VerifyBesselPoles()
    {
        if (_verified) return;

        // (s^2 - 2*Re1*s + |p1|^2)(s^2 - 2*Re2*s + |p2|^2) must equal B4(s).
        var (r1, i1) = Bessel4Poles[0];
        var (r2, i2) = Bessel4Poles[1];
        double m1 = r1 * r1 + i1 * i1, m2 = r2 * r2 + i2 * i2;
        double c3 = -2 * r1 + -2 * r2;
        double c2 = m1 + m2 + 4 * r1 * r2;
        double c1 = -2 * r1 * m2 + -2 * r2 * m1;
        double c0 = m1 * m2;

        double[] got = { 1, c3, c2, c1, c0 };
        for (int i = 0; i < got.Length; i++)
        {
            double want = Bessel4Coefficients[i];
            if (Math.Abs(got[i] - want) > 1e-3 * Math.Max(1, Math.Abs(want)))
                throw new InvalidOperationException(
                    $"Bessel pole table is wrong: rebuilt coefficient[{i}] = {got[i]:F6}, expected {want}.");
        }
        _verified = true;
    }

    /// <summary>Bisects for the frequency where the delay-normalised prototype is 1/sqrt(2).</summary>
    static double Bessel4MinusThreeDbFrequency()
    {
        static double Mag(double w)
        {
            double m = 1;
            foreach (var (re, im) in Bessel4Poles)
            {
                double wn2 = re * re + im * im;
                // |H(jw)| for s^2 - 2*re*s + wn2, DC-normalised
                double realPart = wn2 - w * w;
                double imagPart = -2 * re * w;
                m *= wn2 / Math.Sqrt(realPart * realPart + imagPart * imagPart);
            }
            return m;
        }

        const double Target = 0.70710678118654752;
        double lo = 0.01, hi = 10;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Mag(mid) > Target) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }
}
