using Benchmix.Engine.Dsp;
using Signalsmith;

namespace Benchmix.Engine.Audio;

/// <summary>How a track's speed and pitch relate to each other.</summary>
public enum StretchMode
{
    /// <summary>
    /// Speed and pitch move independently — the phase-vocoder path. This is what "change the
    /// tempo without changing the pitch" means, and it is the default.
    /// </summary>
    Independent,

    /// <summary>
    /// Speed and pitch move together, as if playing a record faster. Pure resampling, so nothing
    /// is invented and nothing can smear — the genuinely transparent option, and often the better
    /// sound for corrections under a few percent.
    /// </summary>
    Linked,
}

/// <summary>
/// One track of the timeline: a clip, its speed and pitch, a three-band EQ, gain and pan.
///
/// The signal chain is <c>clip -> stretch -> EQ -> gain/pan -> mix</c>, and it is the <b>same
/// chain for live preview and for the final render</b>. Spike 01 measured ~150x realtime
/// headroom on this machine's CPU even on the high-quality preset, which removed the usual
/// reason editors keep two different signal paths. What Daniel hears while turning a knob is
/// what lands in the exported file.
/// </summary>
public sealed class TrackVoice : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _channels;

    private Stretch? _stretch;
    private float[] _inScratch = Array.Empty<float>();
    private float[] _outScratch = Array.Empty<float>();

    private double _sourcePos;          // fractional source frame the next output frame reads from
    private double _inCarry;            // fractional input frames owed to the stretcher
    private bool _needsPrime = true;

    private double _speed = 1.0;
    private double _semitones;
    private StretchMode _mode = StretchMode.Independent;
    private bool _preserveFormants = true;
    private bool _dirty = true;

    private double _gain = 1.0, _targetGain = 1.0;
    private double _pan;                // -1 left, +1 right
    private readonly double _gainRamp;

    public TrackVoice(int sampleRate, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        Eq = new ThreeBandEq(sampleRate, channels);
        _gainRamp = 1.0 / Math.Max(1, sampleRate * 0.010);
    }

    public AudioClip? Clip { get; private set; }
    public ThreeBandEq Eq { get; }

    /// <summary>Where this clip begins on the timeline, in engine frames.</summary>
    public long TimelineOffsetFrames { get; set; }

    /// <summary>First source frame used (trim in).</summary>
    public long SourceStartFrame { get; set; }

    /// <summary>One past the last source frame used, or -1 for the end of the clip.</summary>
    public long SourceEndFrame { get; set; } = -1;

    public bool Muted { get; set; }

    /// <summary>1.0 = original tempo, 1.08 = 8% faster.</summary>
    public double Speed
    {
        get => _speed;
        set { var v = Math.Clamp(value, 0.25, 4.0); if (v != _speed) { _speed = v; _dirty = true; } }
    }

    /// <summary>Pitch offset in semitones. Ignored in <see cref="StretchMode.Linked"/>, where speed sets it.</summary>
    public double Semitones
    {
        get => _semitones;
        set { var v = Math.Clamp(value, -24, 24); if (v != _semitones) { _semitones = v; _dirty = true; } }
    }

    public StretchMode Mode
    {
        get => _mode;
        set { if (value != _mode) { _mode = value; _dirty = true; _needsPrime = true; } }
    }

    /// <summary>Keeps vocal character natural when pitching. Independent mode only.</summary>
    public bool PreserveFormants
    {
        get => _preserveFormants;
        set { if (value != _preserveFormants) { _preserveFormants = value; _dirty = true; } }
    }

    /// <summary>Effective pitch change in semitones, including what Linked mode implies.</summary>
    public double EffectiveSemitones => _mode == StretchMode.Linked ? 12 * Math.Log2(_speed) : _semitones;

    /// <summary>
    /// True when this track asks for no speed and no pitch change at all. The stretcher is then
    /// bypassed entirely and samples are copied straight through — an untouched track is not put
    /// through a phase vocoder just to come out the same, which is both faster and, more
    /// importantly, exact.
    /// </summary>
    public bool IsUnprocessed => _speed == 1.0 && (_mode == StretchMode.Linked || _semitones == 0);

    public double Gain
    {
        get => _targetGain;
        set => _targetGain = Math.Clamp(value, 0, 4);
    }

    public double Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1, 1);
    }

    /// <summary>Length on the timeline in engine frames, after the speed change.</summary>
    public long TimelineLengthFrames
    {
        get
        {
            if (Clip is null) return 0;
            long end = SourceEndFrame < 0 ? Clip.FrameCount : Math.Min(SourceEndFrame, Clip.FrameCount);
            long src = Math.Max(0, end - SourceStartFrame);
            return (long)(src / _speed);
        }
    }

    public void SetClip(AudioClip? clip)
    {
        Clip = clip;
        SourceStartFrame = 0;
        SourceEndFrame = -1;
        _needsPrime = true;
    }

    public void SnapParameters()
    {
        _gain = _targetGain;
        Eq.SnapGains();
    }

    /// <summary>Repositions to a timeline frame. Cheap enough to call while scrubbing.</summary>
    public void Seek(long timelineFrame)
    {
        long rel = timelineFrame - TimelineOffsetFrames;
        _sourcePos = SourceRead(Math.Max(0, rel));
        _inCarry = 0;
        _needsPrime = true;
        Eq.Reset();
        PrimeEq();
    }

    /// <summary>
    /// Source frame to read for a given timeline offset, looking <see cref="ThreeBandEq.LatencySamples"/>
    /// ahead so the EQ's constant delay does not shift the track against the rest of the mix.
    /// </summary>
    private double SourceRead(long relTimelineFrame)
        => SourceStartFrame + (relTimelineFrame + Eq.LatencySamples) * _speed;

    /// <summary>
    /// Runs the EQ's own latency worth of audio through it and discards the result, so the first
    /// block after a seek is steady state rather than the filters fading up from zero.
    /// </summary>
    private void PrimeEq()
    {
        int lat = Eq.LatencySamples;
        if (lat <= 0 || Clip is null) return;

        EnsureInScratch(lat);
        var buf = _inScratch.AsSpan(0, lat * _channels);
        long from = (long)Math.Round(_sourcePos) - lat;
        CopySource(from, lat, buf, Clip.FrameCount);
        Eq.Process(buf);
    }

    /// <summary>
    /// Renders <paramref name="frames"/> frames starting at <paramref name="timelineFrame"/> and
    /// <b>adds</b> them into <paramref name="output"/>. Returns the number of frames that actually
    /// carried audio, so the mixer can tell silence from a finished clip.
    /// </summary>
    public int Render(Span<float> output, long timelineFrame, int frames)
    {
        if (Clip is null || Muted) { StepGainOnly(frames); return 0; }

        long end = SourceEndFrame < 0 ? Clip.FrameCount : Math.Min(SourceEndFrame, Clip.FrameCount);
        long relStart = timelineFrame - TimelineOffsetFrames;
        if (relStart + frames <= 0 || relStart >= TimelineLengthFrames) { StepGainOnly(frames); return 0; }

        EnsureBuffers(frames);
        ApplyParametersIfDirty();

        // Keep the read position honest against the caller's clock; this also handles the first
        // block after a seek and any drift from fractional input consumption.
        double wanted = SourceRead(Math.Max(0, relStart));
        if (Math.Abs(wanted - _sourcePos) > 2) { _sourcePos = wanted; _needsPrime = true; _inCarry = 0; }

        var work = _outScratch.AsSpan(0, frames * _channels);
        work.Clear();

        if (IsUnprocessed) RenderDirect(work, frames, end);
        else if (_mode == StretchMode.Linked) RenderLinked(work, frames, end);
        else RenderIndependent(work, frames, end);

        Eq.Process(work);

        // Gain and pan, ramped per frame so knobs never click.
        for (int f = 0; f < frames; f++)
        {
            _gain = Approach(_gain, _targetGain, _gainRamp);
            double l = _gain * (_pan <= 0 ? 1 : 1 - _pan);
            double r = _gain * (_pan >= 0 ? 1 : 1 + _pan);
            int i = f * _channels;
            output[i] += (float)(work[i] * l);
            if (_channels > 1) output[i + 1] += (float)(work[i + 1] * r);
        }
        return frames;
    }

    /// <summary>Straight copy for a track with no speed or pitch change — no DSP in the path at all.</summary>
    private void RenderDirect(Span<float> work, int frames, long sourceEnd)
    {
        long start = (long)Math.Round(_sourcePos);
        CopySource(start, frames, work, sourceEnd);
        _sourcePos = start + frames;
    }

    private void RenderLinked(Span<float> work, int frames, long sourceEnd)
    {
        var kernel = SincResampler.KernelFor(_speed);
        var src = Clip!.Samples.AsSpan();
        long count = Clip.FrameCount;

        for (int f = 0; f < frames; f++)
        {
            if (_sourcePos >= sourceEnd) break;
            for (int c = 0; c < _channels; c++)
                work[f * _channels + c] = (float)SincResampler.Sample(src, count, _channels, c, _sourcePos, kernel);
            _sourcePos += _speed;
        }
    }

    private void RenderIndependent(Span<float> work, int frames, long sourceEnd)
    {
        var st = _stretch ??= CreateStretch();

        // How many input frames this block of output consumes, carrying the fraction forward so
        // the read position does not drift over a four-minute song.
        double exact = frames * _speed + _inCarry;
        int inFrames = (int)Math.Floor(exact);
        _inCarry = exact - inFrames;
        if (inFrames <= 0) inFrames = 1;

        if (_needsPrime) { Prime(st); _needsPrime = false; }

        long start = (long)Math.Round(_sourcePos);
        EnsureInScratch(inFrames);
        var input = _inScratch.AsSpan(0, inFrames * _channels);
        CopySource(start, inFrames, input, sourceEnd);

        st.Process(input, inFrames, work, frames);
        _sourcePos = start + inFrames;
    }

    /// <summary>
    /// Feeds the stretcher the audio immediately before the read position, so the first block
    /// after a seek is continuous instead of fading up out of an empty analysis window.
    /// </summary>
    private void Prime(Stretch st)
    {
        st.Reset();
        int need = Math.Max(0, st.SeekLength());
        if (need == 0 || Clip is null) return;

        long from = (long)Math.Round(_sourcePos) - need;
        EnsureInScratch(need);
        var buf = _inScratch.AsSpan(0, need * _channels);
        CopySource(from, need, buf, Clip.FrameCount);
        st.Seek(buf, need, (float)_speed);
    }

    private void CopySource(long startFrame, int frames, Span<float> dest, long sourceEnd)
    {
        dest.Clear();
        var src = Clip!.Samples;
        for (int f = 0; f < frames; f++)
        {
            long idx = startFrame + f;
            if (idx < 0 || idx >= sourceEnd || idx >= Clip.FrameCount) continue;
            for (int c = 0; c < _channels; c++)
                dest[f * _channels + c] = src[idx * _channels + c];
        }
    }

    private Stretch CreateStretch()
    {
        var st = new Stretch();
        st.PresetDefault(_channels, _sampleRate, true);
        _dirty = true;
        _needsPrime = true;
        return st;
    }

    private void ApplyParametersIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        if (_mode != StretchMode.Independent || _stretch is null) return;

        _stretch.SetTransposeSemitones((float)_semitones, 0.5f);
        // Compensating formants keeps a pitched-up vocal sounding like a person rather than a
        // chipmunk; compensatePitch keeps the note where the user asked for it.
        _stretch.SetFormantSemitones(_preserveFormants ? 0f : (float)_semitones, _preserveFormants);
    }

    private void StepGainOnly(int frames)
    {
        for (int f = 0; f < frames; f++) _gain = Approach(_gain, _targetGain, _gainRamp);
    }

    private void EnsureBuffers(int frames)
    {
        int need = frames * _channels;
        if (_outScratch.Length < need) _outScratch = new float[need];
    }

    private void EnsureInScratch(int frames)
    {
        int need = Math.Max(1, frames) * _channels;
        if (_inScratch.Length < need) _inScratch = new float[need];
    }

    private static double Approach(double cur, double target, double step)
    {
        if (cur == target) return target;
        double d = target - cur;
        return Math.Abs(d) <= step ? target : cur + Math.Sign(d) * step;
    }

    public void Dispose()
    {
        _stretch?.Dispose();
        _stretch = null;
    }
}
