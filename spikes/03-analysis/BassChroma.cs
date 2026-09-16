namespace Benchmix.Analysis;


    /// <summary>
    /// Bass-weighted chroma. The root of a chord is usually the lowest note, so a chroma built
    /// only from the bass register identifies the tonic far more directly than a full-spectrum
    /// chroma, which is dominated by whatever the melody and the cymbals are doing.
    /// </summary>
    public static class BassChroma
    {
        public static double[] Compute(float[] x, int rate, double tuning, double loHz, double hiHz)
        {
            const int N = 16384, Hop = 8192;              // long window: bass needs frequency resolution
            int m = (int)Math.Log2(N);
            var win = new float[N];
            for (int i = 0; i < N; i++) win[i] = (float)NAudio.Dsp.FastFourierTransform.HammingWindow(i, N);

            var chroma = new double[12];
            var fft = new NAudio.Dsp.Complex[N];
            int kMin = Math.Max(2, (int)(loHz * N / rate));
            int kMax = Math.Min(N / 2 - 2, (int)(hiHz * N / rate));

            for (int off = 0; off + N < x.Length; off += Hop)
            {
                for (int i = 0; i < N; i++) { fft[i].X = x[off + i] * win[i]; fft[i].Y = 0; }
                NAudio.Dsp.FastFourierTransform.FFT(true, m, fft);

                // strongest peak in the bass band for this frame = the likely root
                double bestMag = 0; double bestFreq = 0;
                for (int k = kMin; k <= kMax; k++)
                {
                    double re = fft[k].X, im = fft[k].Y;
                    double mag = Math.Sqrt(re * re + im * im);
                    if (mag <= bestMag) continue;
                    if (mag <= Math.Sqrt(fft[k - 1].X * fft[k - 1].X + fft[k - 1].Y * fft[k - 1].Y)) continue;
                    bestMag = mag; bestFreq = k * rate / (double)N;
                }
                if (bestFreq <= 0) continue;
                double pitch = 69 + 12 * Math.Log2(bestFreq / 440.0) - tuning;
                int pc = (int)Math.Round(pitch) % 12; if (pc < 0) pc += 12;
                chroma[pc] += Math.Sqrt(bestMag);
            }
            double s = 0; foreach (var v in chroma) s += v;
            if (s > 0) for (int i = 0; i < 12; i++) chroma[i] /= s;
            return chroma;
        }

        public static double[] Blend(double[] full, double[] bass, double bassWeight)
        {
            var o = new double[12];
            for (int i = 0; i < 12; i++) o[i] = full[i] * (1 - bassWeight) + bass[i] * bassWeight;
            double s = 0; foreach (var v in o) s += v;
            if (s > 0) for (int i = 0; i < 12; i++) o[i] /= s;
            return o;
        }
    }

