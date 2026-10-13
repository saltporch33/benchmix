using NAudio.Wave;

namespace Benchmix.Engine.Audio;

/// <summary>
/// Plays the mix through a sound output that runs at another rate than the mix does (2026-10-12).
///
/// <para>
/// The mix runs at the rate of the output Benchmix started on, and every position in a project is
/// counted in its frames, so it cannot change rate mid-session. When Windows moves playback to an
/// output at another rate — Bluetooth headphones at 44.1 kHz after speakers at 48 — this converts
/// on the way out, after the mix, as Windows itself would. Only what is heard live passes through
/// it: a render writes the mix as it is, so preview and render are still the one signal path.
/// </para>
/// <para>
/// The interpolation is the windowed sinc the LINK mode uses (<see cref="SincResampler"/>), with
/// the same anti-aliasing widening when the output is slower than the mix. Its look-ahead is the
/// kernel's half width — under half a millisecond.
/// </para>
/// </summary>
public sealed class DeviceRateConverter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly double _step;                 // source frames per output frame
    private readonly SincResampler.Kernel _kernel;
    private readonly int _half;

    private float[] _buffer;                        // interleaved source frames; [0] is the oldest kept
    private int _frames;                            // how many frames _buffer holds
    private double _position;                       // where the next output frame falls, in _buffer's frames

    public DeviceRateConverter(ISampleProvider source, int outputRate)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _step = source.WaveFormat.SampleRate / (double)outputRate;
        _kernel = SincResampler.KernelFor(_step);
        _half = _kernel.HalfTaps;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputRate, _channels);

        // Silence before the first frame, so the first output sample has its full kernel.
        _buffer = new float[(4096 + 2 * _half) * _channels];
        _frames = _half;
        _position = _half;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> output)
    {
        int frames = output.Length / _channels;
        if (frames == 0) return 0;

        // Every source frame the last output frame's kernel reaches, pulled in one go.
        int needed = (int)Math.Floor(_position + (frames - 1) * _step) + _half + 1;
        if (needed > _frames) Fill(needed - _frames);

        Span<float> acc = stackalloc float[8];
        int ch = Math.Min(_channels, acc.Length);
        for (int i = 0; i < frames; i++)
        {
            double p = _position + i * _step;
            long centre = (long)Math.Floor(p);
            double frac = p - centre;
            acc.Clear();
            double norm = 0;
            for (int k = -_half + 1; k <= _half; k++)
            {
                long at = centre + k;
                if (at < 0 || at >= _frames) continue;
                double w = _kernel.Weight(k - frac);
                if (w == 0) continue;
                norm += w;
                int b = (int)at * _channels;
                for (int c = 0; c < ch; c++) acc[c] += (float)(w * _buffer[b + c]);
            }
            float scale = norm > 1e-12 ? (float)(1 / norm) : 0f;
            for (int c = 0; c < _channels; c++)
                output[i * _channels + c] = c < ch ? acc[c] * scale : 0f;
        }

        // Keep only the history the next block's first kernel reaches back into.
        _position += frames * _step;
        int drop = (int)Math.Floor(_position) - _half;
        if (drop > 0)
        {
            drop = Math.Min(drop, _frames);
            _buffer.AsSpan(drop * _channels, (_frames - drop) * _channels).CopyTo(_buffer);
            _frames -= drop;
            _position -= drop;
        }
        return frames * _channels;
    }

    private void Fill(int count)
    {
        int want = (_frames + count) * _channels;
        if (want > _buffer.Length) Array.Resize(ref _buffer, Math.Max(want, _buffer.Length * 2));
        var into = _buffer.AsSpan(_frames * _channels, count * _channels);
        int got = _source.Read(into);
        if (got < into.Length) into[got..].Clear();     // a short read is silence, never stale samples
        _frames += count;
    }
}
