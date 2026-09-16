namespace Benchmix.Engine.Audio;

/// <summary>Per-track and master levels, published for the meters.</summary>
public sealed class MixMeters
{
    public float[] TrackPeak = Array.Empty<float>();
    public float MasterPeakLeft;
    public float MasterPeakRight;
    /// <summary>True if the master hit or passed full scale since the last reset.</summary>
    public bool MasterClipped;
}

/// <summary>
/// The whole mix: every track summed, then master gain, then an optional safety limiter.
///
/// This object is the single source of audio truth. Live playback pulls blocks from it in
/// realtime; the renderer pulls blocks from the same object as fast as the CPU allows. There is
/// no second "render engine" that might disagree with what was heard.
/// </summary>
public sealed class MixGraph : IDisposable
{
    private readonly List<TrackVoice> _tracks = new();
    private readonly int _sampleRate;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    private double _masterGain = 1.0, _targetMasterGain = 1.0;
    private readonly double _gainRamp;

    // One-pole peak limiter state.
    private double _limiterGain = 1.0;
    private readonly double _limiterRelease;

    public MixGraph(int sampleRate, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _gainRamp = 1.0 / Math.Max(1, sampleRate * 0.010);
        _limiterRelease = 1.0 - Math.Exp(-1.0 / (sampleRate * 0.150));   // 150 ms release
        Meters = new MixMeters();
    }

    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public IReadOnlyList<TrackVoice> Tracks => _tracks;
    public MixMeters Meters { get; }

    /// <summary>Playhead in engine frames.</summary>
    public long PositionFrames { get; private set; }

    /// <summary>Keeps the mix under full scale on the way out. On by default for renders.</summary>
    public bool LimiterEnabled { get; set; } = true;

    /// <summary>Ceiling the limiter aims for. −0.3 dBFS leaves room for inter-sample peaks.</summary>
    public double CeilingLinear { get; set; } = 0.966;

    public double MasterGain
    {
        get => _targetMasterGain;
        set => _targetMasterGain = Math.Clamp(value, 0, 4);
    }

    /// <summary>Longest timeline position any track reaches, in frames.</summary>
    public long LengthFrames
    {
        get
        {
            long max = 0;
            foreach (var t in _tracks)
                max = Math.Max(max, t.TimelineOffsetFrames + t.TimelineLengthFrames);
            return max;
        }
    }

    public TrackVoice AddTrack()
    {
        var v = new TrackVoice(_sampleRate, _channels);
        _tracks.Add(v);
        Meters.TrackPeak = new float[_tracks.Count];
        return v;
    }

    public void RemoveTrack(TrackVoice voice)
    {
        if (_tracks.Remove(voice)) { voice.Dispose(); Meters.TrackPeak = new float[_tracks.Count]; }
    }

    public void Clear()
    {
        foreach (var t in _tracks) t.Dispose();
        _tracks.Clear();
        Meters.TrackPeak = Array.Empty<float>();
    }

    public void Seek(long frame)
    {
        PositionFrames = Math.Max(0, frame);
        foreach (var t in _tracks) t.Seek(PositionFrames);
        _limiterGain = 1.0;
    }

    public void SnapParameters()
    {
        _masterGain = _targetMasterGain;
        foreach (var t in _tracks) t.SnapParameters();
    }

    /// <summary>
    /// Renders the next <paramref name="frames"/> frames of the mix, replacing the buffer.
    /// Advances the playhead.
    /// </summary>
    public void Render(Span<float> output, int frames)
    {
        output[..(frames * _channels)].Clear();

        int need = frames * _channels;
        if (_scratch.Length < need) _scratch = new float[need];

        if (Meters.TrackPeak.Length != _tracks.Count) Meters.TrackPeak = new float[_tracks.Count];

        for (int i = 0; i < _tracks.Count; i++)
        {
            var span = _scratch.AsSpan(0, need);
            span.Clear();
            _tracks[i].Render(span, PositionFrames, frames);

            float peak = 0;
            for (int s = 0; s < need; s++)
            {
                float v = span[s];
                output[s] += v;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
            Meters.TrackPeak[i] = peak;
        }

        float pl = 0, pr = 0;
        for (int f = 0; f < frames; f++)
        {
            _masterGain = Approach(_masterGain, _targetMasterGain, _gainRamp);

            int i = f * _channels;
            double l = output[i] * _masterGain;
            double r = _channels > 1 ? output[i + 1] * _masterGain : l;

            if (LimiterEnabled)
            {
                double mag = Math.Max(Math.Abs(l), Math.Abs(r));
                double want = mag > CeilingLinear ? CeilingLinear / mag : 1.0;
                // Attack instantly, release slowly — a peak limiter, not a compressor.
                if (want < _limiterGain) _limiterGain = want;
                else _limiterGain += (want - _limiterGain) * _limiterRelease;
                l *= _limiterGain;
                r *= _limiterGain;
            }

            if (Math.Abs(l) >= 1.0 || Math.Abs(r) >= 1.0) Meters.MasterClipped = true;

            output[i] = (float)l;
            if (_channels > 1) output[i + 1] = (float)r;

            if (Math.Abs(l) > pl) pl = (float)Math.Abs(l);
            if (Math.Abs(r) > pr) pr = (float)Math.Abs(r);
        }

        Meters.MasterPeakLeft = pl;
        Meters.MasterPeakRight = pr;
        PositionFrames += frames;
    }

    private static double Approach(double cur, double target, double step)
    {
        if (cur == target) return target;
        double d = target - cur;
        return Math.Abs(d) <= step ? target : cur + Math.Sign(d) * step;
    }

    public void Dispose() => Clear();
}
