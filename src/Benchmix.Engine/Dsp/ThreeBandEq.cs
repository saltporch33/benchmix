namespace Benchmix.Engine.Dsp;

/// <summary>How the three knobs behave.</summary>
public enum EqMode
{
    /// <summary>
    /// Subtractive complementary crossover. Bands sum to the input <b>exactly</b> at unity, and
    /// a knob at the bottom is a true kill (multiply by zero). This is what a club isolator does
    /// and it is the Benchmix default.
    /// </summary>
    Isolator,

    /// <summary>
    /// Classic shelf/peak EQ — gentler curves, −23 dB floor rather than silence. Closer to a
    /// channel EQ on a mixer than to an isolator.
    /// </summary>
    Shelving,
}

/// <summary>
/// A three-band DJ EQ with one property that matters more than any other here: in
/// <see cref="EqMode.Isolator"/> with all three knobs at unity, the output is the input, sample
/// for sample (delayed by <see cref="LatencySamples"/>). Benchmix renders finished files, so a
/// track whose EQ was never touched must not be altered by having passed through the EQ at all.
///
/// Structure, after Lipshitz &amp; Vanderkooy (JAES 31, 1983), the same family Mixxx ships as
/// "LV-Mix":
/// <code>
///   low  = LP1(x)                 // 4th-order Bessel at the low/mid split
///   mid  = LP2(D2(x)) - low       // 4th-order Bessel at the mid/high split, delay-aligned to LP1
///   high = D3(x)     - LP2(D2(x))
///   y    = gLow*low + gMid*mid + gHigh*high
///        = (gLow-gMid)*LP1(x) + (gMid-gHigh)*LP2(D2(x)) + gHigh*D3(x)
/// </code>
/// With all gains at 1 the first two terms cancel identically and <c>y = D3(x)</c>.
///
/// Three parallel filters summed — the obvious design — cannot do this: the bands have different
/// group delay, so near each crossover they add as complex vectors and leave ±1–3 dB of ripple
/// even with the knobs centred.
///
/// Bessel is chosen over Butterworth because the high band is formed by <i>subtraction</i>, which
/// only behaves like a real highpass when the lowpass has near-constant group delay — otherwise a
/// single integer delay cannot align it.
/// </summary>
public sealed class ThreeBandEq
{
    /// <summary>Mixxx's isolator defaults, which are well-tested on real material.</summary>
    public const double DefaultLowMidHz = 246.0;
    public const double DefaultMidHighHz = 2500.0;

    /// <summary>Shelving mode cannot reach silence, so it clamps where Mixxx clamps.</summary>
    public const double ShelvingKillDb = -23.0;

    private readonly int _channels;
    private readonly double _sampleRate;
    private EqMode _mode;

    // Isolator path
    private readonly BiquadCascade _lp1;
    private readonly BiquadCascade _lp2;
    private readonly DelayLine _d2;
    private readonly DelayLine _d3;

    // Shelving path
    private readonly Biquad[][] _shelf;      // [channel][3]

    // Gains, ramped per sample so a turning knob never clicks.
    private double _gLow = 1, _gMid = 1, _gHigh = 1;
    private double _tLow = 1, _tMid = 1, _tHigh = 1;
    private readonly double _rampPerSample;

    public ThreeBandEq(double sampleRate, int channels, EqMode mode = EqMode.Isolator,
                       double lowMidHz = DefaultLowMidHz, double midHighHz = DefaultMidHighHz)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _mode = mode;
        LowMidHz = lowMidHz;
        MidHighHz = midHighHz;

        var lp1Sections = FilterDesign.Bessel4LowPass(lowMidHz, sampleRate);
        var lp2Sections = FilterDesign.Bessel4LowPass(midHighHz, sampleRate);
        _lp1 = new BiquadCascade(lp1Sections, channels);
        _lp2 = new BiquadCascade(lp2Sections, channels);

        // Align the bands using each cascade's measured group delay at DC, not a guessed constant.
        int g1 = (int)Math.Round(GroupDelayAtDc(lp1Sections));
        int g2 = (int)Math.Round(GroupDelayAtDc(lp2Sections));
        int d2 = Math.Max(0, g1 - g2);
        int d3 = d2 + g2;

        _d2 = new DelayLine(d2, channels);
        _d3 = new DelayLine(d3, channels);
        LatencySamples = d3;

        _shelf = new Biquad[channels][];
        for (int c = 0; c < channels; c++) _shelf[c] = new Biquad[3];
        RebuildShelves();

        // ~10 ms to travel the full range: fast enough to feel instant, slow enough not to click.
        _rampPerSample = 1.0 / Math.Max(1, sampleRate * 0.010);
    }

    public double LowMidHz { get; }
    public double MidHighHz { get; }
    /// <summary>
    /// Switching topology mid-stream swaps an entire filter structure, so the old state is dropped
    /// and the caller is expected to be between clips (loading a project) rather than mid-playback.
    /// </summary>
    public EqMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            _mode = value;
            Reset();
            RebuildShelves();
        }
    }

    /// <summary>Constant delay the isolator path introduces; the caller compensates for it.</summary>
    public int LatencySamples { get; }

    /// <summary>True while the ramped gains have reached their targets.</summary>
    public bool Settled => _gLow == _tLow && _gMid == _tMid && _gHigh == _tHigh;

    /// <summary>
    /// Sets the three knobs as linear gains. 1.0 is unity; 0.0 is a full kill (isolator only).
    /// Values are approached over ~10 ms rather than applied instantly.
    /// </summary>
    public void SetGains(double low, double mid, double high)
    {
        _tLow = Math.Clamp(low, 0, 4);
        _tMid = Math.Clamp(mid, 0, 4);
        _tHigh = Math.Clamp(high, 0, 4);
        if (_mode == EqMode.Shelving) RebuildShelves();
    }

    /// <summary>Sets the knobs in dB. <see cref="double.NegativeInfinity"/> means kill.</summary>
    public void SetGainsDb(double lowDb, double midDb, double highDb)
        => SetGains(FromDb(lowDb), FromDb(midDb), FromDb(highDb));

    /// <summary>Jumps straight to the current targets — used when loading a project, not while playing.</summary>
    public void SnapGains() { _gLow = _tLow; _gMid = _tMid; _gHigh = _tHigh; }

    public void Reset()
    {
        _lp1.Reset(); _lp2.Reset(); _d2.Reset(); _d3.Reset();
        for (int c = 0; c < _channels; c++)
            for (int b = 0; b < 3; b++) _shelf[c][b].Reset();
    }

    /// <summary>Processes an interleaved block in place.</summary>
    public void Process(Span<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (_mode == EqMode.Shelving) { ProcessShelving(interleaved, frames); return; }

        for (int f = 0; f < frames; f++)
        {
            StepGains();
            double fLow = _gLow - _gMid, fMid = _gMid - _gHigh, fHigh = _gHigh;

            for (int c = 0; c < _channels; c++)
            {
                int i = f * _channels + c;
                double x = interleaved[i];

                double low = _lp1.Process(c, x);
                double band = _lp2.Process(c, _d2.Push(c, x));
                double high = _d3.Push(c, x);

                interleaved[i] = (float)(fLow * low + fMid * band + fHigh * high);
            }
        }
    }

    private void ProcessShelving(Span<float> interleaved, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            StepGains();
            for (int c = 0; c < _channels; c++)
            {
                int i = f * _channels + c;
                double x = interleaved[i];
                x = _shelf[c][0].Process(x);
                x = _shelf[c][1].Process(x);
                x = _shelf[c][2].Process(x);
                interleaved[i] = (float)x;
            }
        }
    }

    private void StepGains()
    {
        _gLow = Approach(_gLow, _tLow, _rampPerSample);
        _gMid = Approach(_gMid, _tMid, _rampPerSample);
        _gHigh = Approach(_gHigh, _tHigh, _rampPerSample);
    }

    private static double Approach(double cur, double target, double step)
    {
        if (cur == target) return target;
        double d = target - cur;
        if (Math.Abs(d) <= step) return target;
        return cur + Math.Sign(d) * step;
    }

    private void RebuildShelves()
    {
        double lowDb = Math.Max(ShelvingKillDb, ToDb(_tLow));
        double midDb = Math.Max(ShelvingKillDb, ToDb(_tMid));
        double highDb = Math.Max(ShelvingKillDb, ToDb(_tHigh));

        var lo = Biquad.LowShelf(LowMidHz * 0.45, 0.9, lowDb, _sampleRate);
        var md = Biquad.Peaking(Math.Sqrt(LowMidHz * MidHighHz), 0.9, midDb, _sampleRate);
        var hi = Biquad.HighShelf(MidHighHz * 1.6, 0.9, highDb, _sampleRate);

        for (int c = 0; c < _channels; c++)
        {
            // Preserve filter state across a coefficient change; only the coefficients move.
            _shelf[c][0].B0 = lo.B0; _shelf[c][0].B1 = lo.B1; _shelf[c][0].B2 = lo.B2; _shelf[c][0].A1 = lo.A1; _shelf[c][0].A2 = lo.A2;
            _shelf[c][1].B0 = md.B0; _shelf[c][1].B1 = md.B1; _shelf[c][1].B2 = md.B2; _shelf[c][1].A1 = md.A1; _shelf[c][1].A2 = md.A2;
            _shelf[c][2].B0 = hi.B0; _shelf[c][2].B1 = hi.B1; _shelf[c][2].B2 = hi.B2; _shelf[c][2].A1 = hi.A1; _shelf[c][2].A2 = hi.A2;
        }
    }

    public static double ToDb(double linear) => linear <= 1e-7 ? double.NegativeInfinity : 20 * Math.Log10(linear);
    public static double FromDb(double db) => double.IsNegativeInfinity(db) ? 0 : Math.Pow(10, db / 20);

    /// <summary>Group delay in samples at DC, from the unwrapped phase slope.</summary>
    internal static double GroupDelayAtDc(IReadOnlyList<Biquad> sections)
    {
        const double W = 1e-3, H = 1e-4;
        double total = 0;
        foreach (var s in sections)
            total += -(Phase(s, W + H) - Phase(s, W - H)) / (2 * H);
        return Math.Max(0, total);

        static double Phase(Biquad q, double w)
        {
            double cw = Math.Cos(w), sw = Math.Sin(w);
            double c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = q.B0 + q.B1 * cw + q.B2 * c2, ni = -(q.B1 * sw + q.B2 * s2);
            double dr = 1 + q.A1 * cw + q.A2 * c2, di = -(q.A1 * sw + q.A2 * s2);
            return Math.Atan2(ni, nr) - Math.Atan2(di, dr);
        }
    }
}

/// <summary>Fixed integer delay, one ring per channel. A zero-length delay is a pass-through.</summary>
internal sealed class DelayLine
{
    private readonly float[][] _buf;
    private readonly int _len;
    private int _pos;

    public DelayLine(int samples, int channels)
    {
        _len = Math.Max(0, samples);
        _buf = new float[channels][];
        for (int c = 0; c < channels; c++) _buf[c] = new float[Math.Max(1, _len)];
    }

    public int Length => _len;

    public void Reset()
    {
        foreach (var b in _buf) Array.Clear(b);
        _pos = 0;
    }

    /// <summary>Pushes a sample for one channel and returns the sample from <c>Length</c> ago.</summary>
    public double Push(int channel, double x)
    {
        if (_len == 0) return x;
        var b = _buf[channel];
        double old = b[_pos];
        b[_pos] = (float)x;
        // The position only advances once per frame, on the last channel.
        if (channel == _buf.Length - 1) _pos = _pos + 1 >= _len ? 0 : _pos + 1;
        return old;
    }
}
