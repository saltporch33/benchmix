using Microsoft.ML.OnnxRuntime;

namespace Benchmix.Engine.Stems;

/// <summary>
/// The numbers an MDX-Net model needs. These are not guessed: every UVR model is keyed in UVR's
/// own registry by the MD5 of its last 10 MB, and the values come from there.
/// </summary>
public sealed class MdxParams
{
    public int NFft { get; init; }
    public int DimF { get; init; }
    public int DimT { get; init; }
    /// <summary>Hard-coded to 1024 throughout UVR, not a per-model value.</summary>
    public int Hop { get; init; } = 1024;
    public double Compensate { get; init; } = 1.0;
    public string PrimaryStem { get; init; } = "Vocals";

    public int NBins => NFft / 2 + 1;
    public int Trim => NFft / 2;
    public int ChunkSize => Hop * (DimT - 1);
    public int GenSize => ChunkSize - 2 * Trim;

    /// <summary>The highest frequency the model can represent, given DimF of NBins.</summary>
    public double BandLimitHz(int sampleRate) => DimF * (sampleRate / 2.0) / (NBins - 1);
}

/// <summary>
/// MDX-Net inference: STFT → ONNX → inverse STFT, chunked the way UVR's own <c>demix_base</c>
/// does it.
///
/// The model returns the estimated <b>vocal spectrogram itself</b>, not a mask. The other stem is
/// derived as <c>mix − vocals × compensate</c>, which is how UVR does it with a single model.
///
/// Note the FFT: MDX-Net's n_fft of 7680 is <b>not</b> a power of two (7680 = 512 × 15), so a
/// radix-2 FFT cannot be used at all. Hence <see cref="MixedRadixFft"/>.
/// </summary>
public sealed class MdxSeparator : IDisposable
{
    private readonly MdxParams _p;
    private readonly InferenceSession _session;
    private readonly string _inputName, _outputName;
    private readonly MixedRadixFft _fft;
    private readonly double[] _hann;
    private readonly double[] _windowSquareSum;
    private readonly int _paddedLength;
    private readonly ThreadLocal<Frame> _frames;
    /// <summary>Reused across chunks: the model output is ~12 MB and copying it per chunk churns the heap.</summary>
    private readonly float[] _modelOutput;
    private bool _disposed;

    private sealed class Frame
    {
        public double[] ZRe = null!, ZIm = null!, SpecRe = null!, SpecIm = null!;
        public MixedRadixFft.Work Work = null!;
    }

    public MdxSeparator(string modelPath, MdxParams parameters, bool useGpu = true)
    {
        _p = parameters;

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
        };

        if (useGpu)
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
                ExecutionProvider = "DirectML";
            }
            catch
            {
                // No DX12 adapter, or it is busy. CPU is ~7x slower but produces identical output.
                ExecutionProvider = "CPU";
            }
        }
        else ExecutionProvider = "CPU";

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        // Factor n_fft into (power of two) x (small odd): 7680 = 512*15, 6144 = 2048*3.
        int odd = _p.NFft, pow2 = 1;
        while ((odd & 1) == 0 && pow2 * 2 <= 4096) { odd >>= 1; pow2 <<= 1; }
        _fft = new MixedRadixFft(pow2, odd);
        if (_fft.N != _p.NFft)
            throw new InvalidOperationException($"Cannot factor n_fft={_p.NFft} for the FFT.");

        _hann = new double[_p.NFft];
        for (int i = 0; i < _p.NFft; i++)
            _hann[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / _p.NFft);   // periodic, matching torch

        _paddedLength = _p.ChunkSize + 2 * _p.Trim;
        _windowSquareSum = new double[_paddedLength];
        for (int f = 0; f < _p.DimT; f++)
        {
            int b = f * _p.Hop;
            for (int i = 0; i < _p.NFft; i++) _windowSquareSum[b + i] += _hann[i] * _hann[i];
        }

        _modelOutput = new float[4 * _p.DimF * _p.DimT];

        _frames = new ThreadLocal<Frame>(() => new Frame
        {
            ZRe = new double[_p.NFft],
            ZIm = new double[_p.NFft],
            SpecRe = new double[_p.NFft],
            SpecIm = new double[_p.NFft],
            Work = _fft.NewWork(),
        });
    }

    /// <summary>"DirectML" or "CPU" — what actually ran, not what was asked for.</summary>
    public string ExecutionProvider { get; }

    public MdxParams Parameters => _p;

    /// <summary>
    /// Separates an interleaved stereo clip into the model's primary stem and the remainder.
    /// Both come back at the same length and sample rate as the input.
    /// </summary>
    public (float[] Primary, float[] Secondary) Separate(
        float[] interleaved, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        int frames = interleaved.Length / 2;
        int trim = _p.Trim, gen = _p.GenSize;
        int pad = gen - frames % gen;
        int total = trim + frames + pad + trim;

        // Float, not double: the source is float and these are the two biggest allocations by far.
        // Doubles here were most of the spike's 3.6 GB peak.
        var padded = new float[2][];
        for (int c = 0; c < 2; c++)
        {
            padded[c] = new float[total];
            for (int i = 0; i < frames; i++) padded[c][trim + i] = interleaved[i * 2 + c];
        }

        int chunkCount = (frames + pad) / gen;
        var outL = new float[frames + pad];
        var outR = new float[frames + pad];

        int tensorLength = 4 * _p.DimF * _p.DimT;
        var tensor = new float[tensorLength];
        long[] shape = { 1, 4, _p.DimF, _p.DimT };
        var accL = new double[_paddedLength];
        var accR = new double[_paddedLength];
        var chunk = new double[2][] { new double[_paddedLength], new double[_paddedLength] };

        using var runOptions = new RunOptions();

        for (int ci = 0; ci < chunkCount; ci++)
        {
            cancel.ThrowIfCancellationRequested();
            int start = ci * gen;

            Forward(padded, start, tensor, chunk);

            using (var input = OrtValue.CreateTensorValueFromMemory(
                       OrtMemoryInfo.DefaultInstance, new Memory<float>(tensor), shape))
            using (var results = _session.Run(runOptions, new[] { _inputName }, new[] { input }, new[] { _outputName }))
            {
                results[0].GetTensorDataAsSpan<float>().CopyTo(_modelOutput);
                Inverse(_modelOutput, accL, accR);
            }

            // accL[2*trim + j] is original sample start + j: UVR keeps the middle of each chunk and
            // throws away the trim context on both sides, which is what avoids boundary artefacts.
            for (int i = 0; i < gen && start + i < outL.Length; i++)
            {
                outL[start + i] = (float)accL[2 * trim + i];
                outR[start + i] = (float)accR[2 * trim + i];
            }

            progress?.Report((ci + 1) / (double)chunkCount);
        }

        var primary = new float[frames * 2];
        var secondary = new float[frames * 2];
        double comp = _p.Compensate;
        for (int i = 0; i < frames; i++)
        {
            float pl = (float)(outL[i] * comp);
            float pr = (float)(outR[i] * comp);
            primary[i * 2] = pl;
            primary[i * 2 + 1] = pr;
            secondary[i * 2] = interleaved[i * 2] - pl;
            secondary[i * 2 + 1] = interleaved[i * 2 + 1] - pr;
        }
        return (primary, secondary);
    }

    /// <summary>STFT one chunk: reflect-pad (torch <c>center=True</c>), window, pack L+iR into one complex FFT.</summary>
    private void Forward(float[][] source, int start, float[] tensor, double[][] chunk)
    {
        int nfft = _p.NFft, trim = _p.Trim, chunkSize = _p.ChunkSize, dimF = _p.DimF, dimT = _p.DimT, hop = _p.Hop;

        for (int c = 0; c < 2; c++)
        {
            var a = chunk[c];
            var src = source[c];
            for (int i = 0; i < chunkSize; i++) a[trim + i] = src[start + i];
            for (int j = 0; j < trim; j++) a[j] = a[trim + (trim - j)];                  // reflect, edge excluded
            for (int j = 0; j < trim; j++) a[trim + chunkSize + j] = a[trim + chunkSize - 2 - j];
        }

        var left = chunk[0];
        var right = chunk[1];
        int stride = dimF * dimT;

        Parallel.For(0, dimT, f =>
        {
            var fr = _frames.Value!;
            int b = f * hop;
            for (int i = 0; i < nfft; i++)
            {
                double w = _hann[i];
                fr.ZRe[i] = left[b + i] * w;
                fr.ZIm[i] = right[b + i] * w;
            }
            _fft.Forward(fr.ZRe, fr.ZIm, fr.SpecRe, fr.SpecIm, fr.Work);

            // Unpack the two real signals from the single complex spectrum.
            var sr = fr.SpecRe;
            var si = fr.SpecIm;
            for (int k = 0; k < dimF; k++)
            {
                int m = (nfft - k) % nfft;
                double lRe = 0.5 * (sr[k] + sr[m]);
                double lIm = 0.5 * (si[k] - si[m]);
                double rRe = 0.5 * (si[k] + si[m]);
                double rIm = 0.5 * (sr[m] - sr[k]);
                int o = k * dimT + f;
                tensor[o] = (float)lRe;
                tensor[stride + o] = (float)lIm;
                tensor[2 * stride + o] = (float)rRe;
                tensor[3 * stride + o] = (float)rIm;
            }
        });

        // UVR zeroes the lowest three bins before inference.
        for (int c = 0; c < 4; c++)
            for (int k = 0; k < 3; k++)
                Array.Clear(tensor, c * stride + k * dimT, dimT);
    }

    /// <summary>Inverse STFT: Hermitian-extend, inverse FFT, windowed overlap-add, normalise by the window-square sum.</summary>
    private void Inverse(float[] t, double[] accL, double[] accR)
    {
        int nfft = _p.NFft, dimF = _p.DimF, dimT = _p.DimT, hop = _p.Hop;
        Array.Clear(accL);
        Array.Clear(accR);
        int stride = dimF * dimT;

        // Frames in the same residue class never overlap in the output, so each pass is race-free
        // and the overlap-add can be parallel without locks.
        int classes = (nfft + hop - 1) / hop + 1;

        for (int r = 0; r < classes; r++)
        {
            int count = (dimT - r + classes - 1) / classes;
            if (count <= 0) continue;

            Parallel.For(0, count, q =>
            {
                int f = r + q * classes;
                var fr = _frames.Value!;
                var zr = fr.SpecRe;
                var zi = fr.SpecIm;
                Array.Clear(zr);
                Array.Clear(zi);

                for (int k = 0; k < dimF; k++)
                {
                    int o = k * dimT + f;
                    double a = t[o], b = t[stride + o], c = t[2 * stride + o], d = t[3 * stride + o];
                    zr[k] = a - d;
                    zi[k] = b + c;                                   // Z[k] = L[k] + i*R[k]
                    if (k > 0 && k < nfft - k)
                    {
                        int m = nfft - k;
                        zr[m] = a + d;
                        zi[m] = c - b;
                    }
                }

                _fft.Inverse(zr, zi, fr.ZRe, fr.ZIm, fr.Work);

                int bb = f * hop;
                for (int i = 0; i < nfft; i++)
                {
                    double w = _hann[i];
                    accL[bb + i] += fr.ZRe[i] * w;
                    accR[bb + i] += fr.ZIm[i] * w;
                }
            });
        }

        for (int i = 0; i < _paddedLength; i++)
        {
            double w = _windowSquareSum[i];
            if (w > 1e-11) { accL[i] /= w; accR[i] /= w; }
            else { accL[i] = 0; accR[i] = 0; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frames.Dispose();
        _session.Dispose();
    }
}
