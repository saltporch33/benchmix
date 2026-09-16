using System.Collections.Concurrent;

namespace Benchmix.Engine.Audio;

/// <summary>
/// A windowed-sinc fractional interpolator, used for "linked" tempo/pitch — where changing the
/// speed is allowed to change the pitch with it, exactly like slowing a record down.
///
/// This path exists because it is the only <i>genuinely</i> artifact-free way to change tempo.
/// A phase vocoder always invents something; resampling invents nothing, it just reads the same
/// waveform at a different rate. For the small corrections a mashup usually needs — a few
/// percent — and for hip-hop where the pitch drop is the point, linked is often the better
/// sounding choice, so Benchmix offers it as a first-class mode rather than hiding it.
///
/// Kernels are cached per rate ratio. When playing faster than 1.0 the kernel is widened by the
/// ratio, which lowers its cutoff proportionally — that is the anti-aliasing the decimation
/// needs, and skipping it is what makes naive resamplers sound grainy when sped up.
/// </summary>
public sealed class SincResampler
{
    /// <summary>Half-width of the kernel in sinc zero crossings. 16 gives a very clean response.</summary>
    public const int Zeros = 16;

    /// <summary>Sub-sample positions tabulated between each pair of input samples.</summary>
    public const int Phases = 512;

    private static readonly ConcurrentDictionary<int, Kernel> Cache = new();

    /// <summary>Quantises the ratio so small knob wobbles reuse a cached kernel.</summary>
    private static int KeyFor(double ratio) => (int)Math.Round(Math.Clamp(ratio, 0.25, 4.0) * 200);

    public static Kernel KernelFor(double rateRatio)
        => Cache.GetOrAdd(KeyFor(rateRatio), k => new Kernel(k / 200.0));

    /// <summary>
    /// Reads one interpolated frame from an interleaved buffer at a fractional position.
    /// </summary>
    public static double Sample(ReadOnlySpan<float> interleaved, long frameCount, int channels,
                                int channel, double position, Kernel kernel)
    {
        long center = (long)Math.Floor(position);
        double frac = position - center;

        double acc = 0, norm = 0;
        int half = kernel.HalfTaps;
        for (int i = -half + 1; i <= half; i++)
        {
            long idx = center + i;
            if (idx < 0 || idx >= frameCount) continue;
            double w = kernel.Weight(i - frac);
            acc += w * interleaved[(int)(idx * channels + channel)];
            norm += w;
        }
        return norm > 1e-12 ? acc / norm : 0;
    }

    /// <summary>A tabulated windowed sinc for one rate ratio.</summary>
    public sealed class Kernel
    {
        private readonly float[] _table;   // index = round(|x| * Phases), x in zero crossings
        private readonly double _scale;    // kernel widening factor (>= 1 when decimating)
        private readonly double _invScale;

        internal Kernel(double rateRatio)
        {
            _scale = Math.Max(1.0, rateRatio);
            _invScale = 1.0 / _scale;
            HalfTaps = (int)Math.Ceiling(Zeros * _scale);

            int n = Zeros * Phases + 2;
            _table = new float[n];
            for (int i = 0; i < n; i++)
            {
                double x = i / (double)Phases;                 // zero crossings from centre
                _table[i] = (float)(Sinc(x) * Blackman(x / Zeros));
            }
        }

        /// <summary>Number of input samples used either side of the centre.</summary>
        public int HalfTaps { get; }

        /// <summary>Kernel weight at an offset measured in input samples.</summary>
        public double Weight(double samplesFromCentre)
        {
            double x = Math.Abs(samplesFromCentre) * _invScale;    // to zero-crossing units
            if (x >= Zeros) return 0;
            double t = x * Phases;
            int i = (int)t;
            double f = t - i;
            return _table[i] * (1 - f) + _table[i + 1] * f;
        }

        private static double Sinc(double x)
        {
            if (x == 0) return 1;
            double p = Math.PI * x;
            return Math.Sin(p) / p;
        }

        /// <summary>Blackman window over [-1, 1], given |x| in [0, 1].</summary>
        private static double Blackman(double x)
        {
            if (x >= 1) return 0;
            double a = Math.PI * (x + 1);       // map [-1,1] -> [0, 2pi]
            return 0.42 - 0.5 * Math.Cos(a) + 0.08 * Math.Cos(2 * a);
        }
    }
}
