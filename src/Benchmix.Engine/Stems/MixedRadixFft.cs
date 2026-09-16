namespace Benchmix.Engine.Stems;

/// <summary>
/// Complex FFT of length N = N1 * N2 (N1 a power of two, N2 small) via four-step Cooley-Tukey.
/// Sized for MDX-Net's n_fft = 7680 = 512 * 15, which is NOT a power of two, so NAudio's
/// radix-2 FastFourierTransform cannot be used.
/// </summary>
public sealed class MixedRadixFft
{
    public readonly int N, N1, N2;
    readonly int log2N1;
    readonly double[] w1re, w1im;   // radix-2 twiddles, length N1/2
    readonly int[] rev;             // bit-reversal permutation for N1
    readonly double[] twRe, twIm;   // W_N^(n1*k2), indexed n1*N2 + k2
    readonly double[] dRe, dIm;     // N2 x N2 DFT matrix, indexed k2*N2 + n2

    public MixedRadixFft(int n1, int n2)
    {
        N1 = n1; N2 = n2; N = n1 * n2;
        if ((N1 & (N1 - 1)) != 0) throw new ArgumentException("N1 must be a power of two");
        log2N1 = (int)Math.Round(Math.Log2(N1));

        w1re = new double[N1 / 2]; w1im = new double[N1 / 2];
        for (int t = 0; t < N1 / 2; t++)
        {
            double a = -2.0 * Math.PI * t / N1;
            w1re[t] = Math.Cos(a); w1im[t] = Math.Sin(a);
        }
        rev = new int[N1];
        for (int i = 0; i < N1; i++)
        {
            int r = 0, x = i;
            for (int b = 0; b < log2N1; b++) { r = (r << 1) | (x & 1); x >>= 1; }
            rev[i] = r;
        }
        twRe = new double[N]; twIm = new double[N];
        for (int a1 = 0; a1 < N1; a1++)
            for (int k2 = 0; k2 < N2; k2++)
            {
                double a = -2.0 * Math.PI * ((long)a1 * k2 % N) / N;
                twRe[a1 * N2 + k2] = Math.Cos(a); twIm[a1 * N2 + k2] = Math.Sin(a);
            }
        dRe = new double[N2 * N2]; dIm = new double[N2 * N2];
        for (int k2 = 0; k2 < N2; k2++)
            for (int n = 0; n < N2; n++)
            {
                double a = -2.0 * Math.PI * (n * k2 % N2) / N2;
                dRe[k2 * N2 + n] = Math.Cos(a); dIm[k2 * N2 + n] = Math.Sin(a);
            }
    }

    public sealed class Work
    {
        public readonly double[] gRe, gIm;
        public Work(int n) { gRe = new double[n]; gIm = new double[n]; }
    }
    public Work NewWork() => new Work(N);

    void Fft1(double[] re, double[] im, int off)
    {
        for (int i = 0; i < N1; i++)
        {
            int j = rev[i];
            if (j > i)
            {
                (re[off + i], re[off + j]) = (re[off + j], re[off + i]);
                (im[off + i], im[off + j]) = (im[off + j], im[off + i]);
            }
        }
        for (int len = 2; len <= N1; len <<= 1)
        {
            int half = len >> 1, step = N1 / len;
            for (int i = 0; i < N1; i += len)
            {
                int a = off + i, b = a + half;
                for (int j = 0; j < half; j++, a++, b++)
                {
                    int tw = j * step;
                    double wr = w1re[tw], wi = w1im[tw];
                    double xr = re[b] * wr - im[b] * wi;
                    double xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
            }
        }
    }

    /// <summary>Forward DFT. src may equal dst only if they are distinct from work buffers.</summary>
    public void Forward(double[] srcRe, double[] srcIm, double[] dstRe, double[] dstIm, Work w)
    {
        Span<double> ar = stackalloc double[N2];
        Span<double> ai = stackalloc double[N2];
        var gRe = w.gRe; var gIm = w.gIm;
        for (int n1 = 0; n1 < N1; n1++)
        {
            for (int n2 = 0; n2 < N2; n2++) { int idx = n1 + N1 * n2; ar[n2] = srcRe[idx]; ai[n2] = srcIm[idx]; }
            int tbase = n1 * N2;
            for (int k2 = 0; k2 < N2; k2++)
            {
                double sr = 0, si = 0; int db = k2 * N2;
                for (int n2 = 0; n2 < N2; n2++)
                {
                    double wr = dRe[db + n2], wi = dIm[db + n2];
                    sr += ar[n2] * wr - ai[n2] * wi;
                    si += ar[n2] * wi + ai[n2] * wr;
                }
                double tr = twRe[tbase + k2], ti = twIm[tbase + k2];
                gRe[k2 * N1 + n1] = sr * tr - si * ti;
                gIm[k2 * N1 + n1] = sr * ti + si * tr;
            }
        }
        for (int k2 = 0; k2 < N2; k2++) Fft1(gRe, gIm, k2 * N1);
        for (int k2 = 0; k2 < N2; k2++)
        {
            int b = k2 * N1;
            for (int k1 = 0; k1 < N1; k1++) { dstRe[N2 * k1 + k2] = gRe[b + k1]; dstIm[N2 * k1 + k2] = gIm[b + k1]; }
        }
    }

    /// <summary>Inverse DFT with 1/N scaling, via the conjugate trick.</summary>
    public void Inverse(double[] srcRe, double[] srcIm, double[] dstRe, double[] dstIm, Work w)
    {
        for (int i = 0; i < N; i++) srcIm[i] = -srcIm[i];
        Forward(srcRe, srcIm, dstRe, dstIm, w);
        double s = 1.0 / N;
        for (int i = 0; i < N; i++) { dstRe[i] *= s; dstIm[i] = -dstIm[i] * s; }
    }
}
