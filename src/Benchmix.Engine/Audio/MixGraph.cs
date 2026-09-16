using System.Collections.Concurrent;

namespace Benchmix.Engine.Audio;

/// <summary>Per-track and master levels, published for the meters.</summary>
public sealed class MixMeters
{
    public float[] TrackPeak = Array.Empty<float>();
    public float MasterPeakLeft;
    public float MasterPeakRight;
    /// <summary>True if the master hit or passed full scale since the last reset.</summary>
    public bool MasterClipped;
    /// <summary>
    /// Highest level seen <b>before</b> the limiter since the last reset. Measuring after it is
    /// useless — the limiter's whole job is to pin the output at the ceiling, so a post-limiter
    /// peak always reads "fine" no matter how badly the mix was overdriven.
    /// </summary>
    public float MasterPrePeak;
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
    // Published by reference swap so the render thread never takes a lock and never sees a
    // half-built list. Structural edits build a new array; per-track parameter writes are plain
    // aligned 64-bit stores, which are atomic on x64, so a knob turn cannot tear.
    private volatile TrackVoice[] _tracks = Array.Empty<TrackVoice>();
    private readonly object _structure = new();

    // A removed voice cannot be disposed straight away: the render thread may still hold the array
    // it was in for the rest of the current block, and disposing frees a native stretcher out from
    // under it. Retired voices wait until the render thread has provably moved on.
    private readonly ConcurrentQueue<(uint Epoch, TrackVoice Voice)> _retired = new();
    private volatile uint _renderEpoch;

    // Seeks asked for by the UI are applied by the render thread at a block boundary rather than
    // resetting every voice's filters and stretcher underneath it mid-block.
    private long _pendingSeek = -1;

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
        lock (_structure)
        {
            var v = new TrackVoice(_sampleRate, _channels);
            var next = new TrackVoice[_tracks.Length + 1];
            Array.Copy(_tracks, next, _tracks.Length);
            next[^1] = v;
            _tracks = next;
            return v;
        }
    }

    public void RemoveTrack(TrackVoice voice)
    {
        lock (_structure)
        {
            int idx = Array.IndexOf(_tracks, voice);
            if (idx < 0) return;

            var next = new TrackVoice[_tracks.Length - 1];
            Array.Copy(_tracks, 0, next, 0, idx);
            Array.Copy(_tracks, idx + 1, next, idx, _tracks.Length - idx - 1);
            _tracks = next;
        }

        Retire(voice);
    }

    public void Clear()
    {
        TrackVoice[] old;
        lock (_structure)
        {
            old = _tracks;
            _tracks = Array.Empty<TrackVoice>();
        }
        foreach (var t in old) Retire(t);
    }

    private void Retire(TrackVoice voice)
    {
        voice.Muted = true;
        _retired.Enqueue((_renderEpoch + 2, voice));
        CollectRetired();
    }

    /// <summary>
    /// Disposes voices the render thread has provably finished with. Call from any thread except
    /// the render thread; the UI calls it on its timer so nothing lingers after a removal.
    /// </summary>
    public void CollectRetired()
    {
        int pending = _retired.Count;
        for (int i = 0; i < pending; i++)
        {
            if (!_retired.TryDequeue(out var item)) break;

            // Unsigned subtraction so the comparison stays correct across a wrap.
            if (_renderEpoch - item.Epoch < uint.MaxValue / 2) item.Voice.Dispose();
            else _retired.Enqueue(item);
        }
    }

    /// <summary>
    /// Moves the playhead immediately. Only safe when nothing else is pulling the graph — the
    /// offline renderer, tests, and the render thread's own loop wrap. Anything on the UI thread
    /// during playback must use <see cref="RequestSeek"/>.
    /// </summary>
    public void Seek(long frame)
    {
        Volatile.Write(ref _pendingSeek, -1);
        PositionFrames = Math.Max(0, frame);
        foreach (var t in _tracks) t.Seek(PositionFrames);
        _limiterGain = 1.0;
    }

    /// <summary>
    /// Asks the render thread to move the playhead at the next block boundary. Dragging the ruler
    /// fires dozens of these a second, and applying one inline would reset every voice's filters
    /// and stretcher mid-block — which sounds like a burst of noise.
    /// </summary>
    public void RequestSeek(long frame) => Volatile.Write(ref _pendingSeek, Math.Max(0, frame));

    /// <summary>Where the playhead will be once any requested seek has landed.</summary>
    public long TargetPositionFrames
    {
        get
        {
            long pending = Volatile.Read(ref _pendingSeek);
            return pending >= 0 ? pending : PositionFrames;
        }
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
        // A seek asked for by the UI lands here, between blocks, never inside one.
        long pending = Volatile.Read(ref _pendingSeek);
        if (pending >= 0 && Interlocked.CompareExchange(ref _pendingSeek, -1, pending) == pending)
        {
            PositionFrames = pending;
            foreach (var t in _tracks) t.Seek(PositionFrames);
            _limiterGain = 1.0;
        }

        output[..(frames * _channels)].Clear();

        int need = frames * _channels;
        if (_scratch.Length < need) _scratch = new float[need];

        var tracks = _tracks;   // one read of the published array for the whole block

        // Built locally and published at the end, so a structural edit landing mid-block cannot
        // shrink the array between the length check and an indexed write.
        var peaks = new float[tracks.Length];

        for (int i = 0; i < tracks.Length; i++)
        {
            var span = _scratch.AsSpan(0, need);
            span.Clear();
            tracks[i].Render(span, PositionFrames, frames);

            float peak = 0;
            for (int s = 0; s < need; s++)
            {
                float v = span[s];
                output[s] += v;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
            peaks[i] = peak;
            tracks[i].Peak = peak;      // also on the voice, so a meter can follow the track not the row
        }
        Meters.TrackPeak = peaks;

        float pl = 0, pr = 0, prePeak = 0;
        for (int f = 0; f < frames; f++)
        {
            _masterGain = Approach(_masterGain, _targetMasterGain, _gainRamp);

            int i = f * _channels;
            double l = output[i] * _masterGain;
            double r = _channels > 1 ? output[i + 1] * _masterGain : l;

            float pre = (float)Math.Max(Math.Abs(l), Math.Abs(r));
            if (pre > prePeak) prePeak = pre;

            if (LimiterEnabled)
            {
                double mag = pre;
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
        if (prePeak > Meters.MasterPrePeak) Meters.MasterPrePeak = prePeak;
        PositionFrames += frames;

        // Published last: anything retired before this value was observed is now safe to dispose.
        _renderEpoch++;
    }

    private static double Approach(double cur, double target, double step)
    {
        if (cur == target) return target;
        double d = target - cur;
        return Math.Abs(d) <= step ? target : cur + Math.Sign(d) * step;
    }

    public void Dispose()
    {
        Clear();
        // Nothing is pulling the graph any more, so retired voices can go now.
        while (_retired.TryDequeue(out var item)) item.Voice.Dispose();
    }
}
