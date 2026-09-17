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
/// One track of the timeline: its sections, its speed and pitch, a three-band EQ, gain and pan.
///
/// The signal chain is <c>clips -> stretch -> EQ -> gain/pan -> mix</c>, and it is the <b>same
/// chain for live preview and for the final render</b>. Spike 01 measured ~150x realtime
/// headroom on this machine's CPU even on the high-quality preset, which removed the usual
/// reason editors keep two different signal paths. What Daniel hears while turning a knob is
/// what lands in the exported file.
///
/// <para>
/// A track holds a <see cref="ClipTimeline"/> — several sections, in order, never overlapping. A
/// render block is walked as a sequence of <i>runs</i>, each wholly inside one section or wholly
/// in a gap, because a block is thousands of frames long and a hand-built loop crosses a join on
/// every repeat. Rendering the block from whichever section happened to own its first frame would
/// drop audio at every one of them.
/// </para>
/// </summary>
public sealed class TrackVoice : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _channels;

    private Stretch? _stretch;
    private float[] _inScratch = Array.Empty<float>();
    private float[] _outScratch = Array.Empty<float>();
    private float[] _handleScratch = Array.Empty<float>();
    private float[] _envScratch = Array.Empty<float>();

    // Published by reference swap, like MixGraph's voice array: the render thread sees either the
    // whole old arrangement or the whole new one, never a half-applied edit.
    private volatile ClipTimeline _timeline = ClipTimeline.Empty;

    private VoiceClip? _bound;          // the section currently feeding the read position
    private double _sourcePos;          // fractional source frame the next output frame reads from
    private double _inCarry;            // fractional input frames owed to the stretcher
    private bool _needsPrime = true;

    private double _speed = 1.0;
    private double _semitones;
    private StretchMode _mode = StretchMode.Independent;
    private bool _preserveFormants = true;
    private bool _dirty = true;

    private double _gain = 1.0, _targetGain = 1.0;
    private double _muteGain = 1.0;
    private double _pan;                // -1 left, +1 right
    private readonly double _gainRamp;

    public TrackVoice(int sampleRate, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        Eq = new ThreeBandEq(sampleRate, channels);
        _gainRamp = 1.0 / Math.Max(1, sampleRate * 0.010);
    }

    public ThreeBandEq Eq { get; }

    /// <summary>This track's sections. Replaced wholesale; never edited in place.</summary>
    public ClipTimeline Timeline => _timeline;

    /// <summary>
    /// Swaps in a new arrangement. Cheap, but not free — it is gated by the caller so that turning
    /// a knob, which changes nothing structural, does not rebind the section being played.
    /// </summary>
    public void SetTimeline(ClipTimeline timeline)
    {
        _timeline = timeline;
        // The bound section is about to be a stale object from the old list. Dropping it forces
        // the next block to rebind against the new one by position.
        _bound = null;
    }

    /// <summary>Where this track's origin sits on the timeline, in engine frames.</summary>
    public long TimelineOffsetFrames { get; set; }

    public bool Muted { get; set; }

    /// <summary>Peak of this voice's last rendered block, so a meter follows the track, not its row.</summary>
    public float Peak;

    /// <summary>
    /// The shortest ramp applied at every cut edge even when no fade is set. A clip that starts
    /// part-way into a waveform begins on a non-zero sample, and a hard start on a non-zero sample
    /// is a click. Five milliseconds is inaudible as a fade and completely removes it.
    /// </summary>
    public const double DeclickSeconds = 0.005;

    // ---------------------------------------------------------------- the one-clip shim
    //
    // A track used to be exactly one clip, and four test files plus the CLI still say so. These
    // members keep that spelling working against the timeline underneath, which is what lets the
    // existing suite run unmodified and stay the evidence that this refactor changed no behaviour.
    // Nothing in the app calls them; they are not a second way to edit a track.

    private AudioClip? _shimClip;
    private long _shimStart;
    private long _shimEnd = -1;
    private double _shimFadeIn, _shimFadeOut;

    /// <summary>The audio of a single-clip track. Null once a track holds a real timeline.</summary>
    public AudioClip? Clip => _shimClip;

    public void SetClip(AudioClip? clip)
    {
        _shimClip = clip;
        _shimStart = 0;
        _shimEnd = -1;
        RebuildShim();
    }

    /// <summary>First source frame used, for a single-clip track.</summary>
    public long SourceStartFrame
    {
        get => _shimStart;
        set { _shimStart = value; RebuildShim(); }
    }

    /// <summary>One past the last source frame used, or -1 for the end, for a single-clip track.</summary>
    public long SourceEndFrame
    {
        get => _shimEnd;
        set { _shimEnd = value; RebuildShim(); }
    }

    /// <summary>Fade in at the start of a single-clip track.</summary>
    public double FadeInSeconds
    {
        get => _shimFadeIn;
        set { _shimFadeIn = value; RebuildShim(); }
    }

    /// <summary>Fade out at the end of a single-clip track.</summary>
    public double FadeOutSeconds
    {
        get => _shimFadeOut;
        set { _shimFadeOut = value; RebuildShim(); }
    }

    private void RebuildShim()
    {
        if (_shimClip is null) { SetTimeline(ClipTimeline.Empty); return; }
        SetTimeline(new ClipTimeline(new[]
        {
            new VoiceClip
            {
                Audio = _shimClip,
                TrackStartFrame = 0,
                SourceStartFrame = _shimStart,
                SourceEndFrame = _shimEnd,
                FadeInSeconds = _shimFadeIn,
                FadeOutSeconds = _shimFadeOut,
            },
        }));
        _needsPrime = true;
    }

    // ---------------------------------------------------------------- parameters

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

    /// <summary>
    /// How far this track reaches on the timeline, in engine frames, after the speed change.
    /// Measured from the track's origin, so a gap in the middle costs nothing and audio after it
    /// still counts.
    /// </summary>
    public long TimelineLengthFrames => (long)(_timeline.TrackEndFrame / _speed);

    public void SnapParameters()
    {
        _gain = _targetGain;
        _muteGain = Muted ? 0 : 1;
        Eq.SnapGains();
    }

    // ---------------------------------------------------------------- positions

    /// <summary>Repositions to a timeline frame. Cheap enough to call while scrubbing.</summary>
    public void Seek(long timelineFrame)
    {
        EnsureStretchIfNeeded();
        long rel = timelineFrame - TimelineOffsetFrames;

        int idx = _timeline.IndexAt(TrackFrameAt(rel));
        _bound = idx >= 0 ? _timeline[idx] : null;
        _sourcePos = _bound is null ? 0 : SourceReadFor(_bound, rel);

        _inCarry = 0;
        _needsPrime = true;
        Eq.Reset();
        PrimeEq();
    }

    /// <summary>The track's own unstretched frame at a timeline frame relative to its origin.</summary>
    private long TrackFrameAt(long relTimelineFrame)
        => (long)Math.Floor(relTimelineFrame * _speed);

    /// <summary>
    /// Source frame to read for a section at a given timeline offset, looking the EQ's and the
    /// stretcher's latency ahead so their constant delays do not shift the track against the mix.
    /// <para>
    /// <paramref name="relTimelineFrame"/> is deliberately NOT clamped into the section. When a
    /// render block straddles the start of a section it is before it, and that is meaningful: the
    /// read position belongs before the audio, so <c>CopySource</c> yields silence until the
    /// section really begins. Clamping it looks harmless — the envelope hides any early audio —
    /// but it makes the section start reading part-way into itself, skipping as many frames as its
    /// offset. A clip dropped twenty seconds along would begin twenty seconds into the song.
    /// </para>
    /// </summary>
    private double SourceReadFor(VoiceClip c, long relTimelineFrame)
        => c.SourceStartFrame
         + (relTimelineFrame * _speed - c.TrackStartFrame)
         + (Eq.LatencySamples + StretchLatencyFrames) * _speed;

    /// <summary>
    /// Output frames the phase vocoder delays by, or zero when it is bypassed.
    ///
    /// <para>
    /// <c>InputLatency</c> is counted in <b>input</b> frames and <c>OutputLatency</c> in output
    /// frames, so the delay in output frames is the first divided by the playback rate plus the
    /// second. Measured at three rates in <c>StretchLatencyTests</c> and confirmed to within
    /// twenty frames; a test pins the formula so a library update cannot quietly invalidate it.
    /// </para>
    /// <para>
    /// Uncompensated this was 7200 frames — 150 ms, a quarter of a beat at 98 BPM — between a
    /// beatmatched track and one that was left alone. It is compensated exactly like the EQ's
    /// delay, by reading that much further ahead, because both sit in the same output-side clock.
    /// </para>
    /// </summary>
    private double StretchLatencyFrames
    {
        get
        {
            if (IsUnprocessed || _mode != StretchMode.Independent) return 0;
            var st = _stretch;
            return st is null ? 0 : st.InputLatency() / _speed + st.OutputLatency();
        }
    }

    /// <summary>
    /// Builds the stretcher if this track is going to need it.
    ///
    /// It has to exist before any read position is worked out, because the compensation for its
    /// latency is read out of it. Left to be created inside the first Render — which is where it
    /// used to happen — the first block would read from a position computed with no compensation
    /// at all and then immediately resync, a stumble at the head of every pitched clip.
    /// </summary>
    private void EnsureStretchIfNeeded()
    {
        if (_mode == StretchMode.Independent && !IsUnprocessed) _stretch ??= CreateStretch();
    }

    /// <summary>
    /// Runs the EQ's own latency worth of audio through it and discards the result, so the first
    /// block after a jump is steady state rather than the filters fading up from zero.
    /// </summary>
    private void PrimeEq()
    {
        int lat = Eq.LatencySamples;
        if (lat <= 0 || _bound?.Audio is null) return;

        EnsureInScratch(lat);
        var buf = _inScratch.AsSpan(0, lat * _channels);
        long from = (long)Math.Round(_sourcePos) - lat;
        CopySource(_bound, from, lat, buf, _bound.Audio.FrameCount);
        Eq.Process(buf);
    }

    // ---------------------------------------------------------------- render

    /// <summary>
    /// Renders <paramref name="frames"/> frames starting at <paramref name="timelineFrame"/> and
    /// <b>adds</b> them into <paramref name="output"/>. Returns the number of frames that actually
    /// carried audio, so the mixer can tell silence from a finished track.
    /// </summary>
    public int Render(Span<float> output, long timelineFrame, int frames)
    {
        var timeline = _timeline;    // one read for the whole block

        // Mute ramps like everything else rather than cutting in one sample. Only stop rendering
        // once the ramp has actually reached silence, or muting a playing track is a click.
        if (timeline.Count == 0 || (Muted && _muteGain <= 0)) { StepGainOnly(frames); return 0; }

        long relStart = timelineFrame - TimelineOffsetFrames;
        if (relStart + frames <= 0 || relStart >= TimelineLengthFrames) { StepGainOnly(frames); return 0; }

        EnsureBuffers(frames);
        EnsureStretchIfNeeded();
        ApplyParametersIfDirty();

        var work = _outScratch.AsSpan(0, frames * _channels);
        work.Clear();
        var env = _envScratch.AsSpan(0, frames);
        env.Clear();

        // Walk the block as runs. Each is wholly inside one section or wholly in a gap, so a join
        // that lands mid-block is rendered from both sides rather than dropping one of them.
        int f = 0;
        while (f < frames)
        {
            long rel = relStart + f;
            long trackFrame = TrackFrameAt(rel);
            int idx = timeline.IndexAt(trackFrame);

            if (idx < 0)
            {
                // A gap. Still run it through the EQ so the filter stays continuous across it.
                long nextStart = timeline.NextStartAtOrAfter(trackFrame);
                int gap = nextStart < 0
                    ? frames - f
                    : (int)Math.Clamp((long)Math.Floor(nextStart / _speed) - rel, 1, frames - f);

                Eq.Process(work.Slice(f * _channels, gap * _channels));
                f += gap;
                continue;
            }

            var clip = timeline[idx];
            long endRel = (long)Math.Ceiling(clip.TrackEndFrame / _speed);
            int run = (int)Math.Clamp(endRel - rel, 1, frames - f);

            RenderRun(work.Slice(f * _channels, run * _channels), env.Slice(f, run),
                      timeline, idx, rel, run);
            f += run;
        }

        // Gain, pan, mute and the section envelope, per frame so nothing clicks.
        for (int i = 0; i < frames; i++)
        {
            _gain = Approach(_gain, _targetGain, _gainRamp);
            _muteGain = Approach(_muteGain, Muted ? 0 : 1, _gainRamp);

            double e = env[i] * _muteGain;
            double l = _gain * e * (_pan <= 0 ? 1 : 1 - _pan);
            double r = _gain * e * (_pan >= 0 ? 1 : 1 + _pan);
            int k = i * _channels;
            output[k] += (float)(work[k] * l);
            if (_channels > 1) output[k + 1] += (float)(work[k + 1] * r);
        }
        return frames;
    }

    /// <summary>Renders one run: a stretch of output frames belonging to exactly one section.</summary>
    private void RenderRun(Span<float> work, Span<float> env, ClipTimeline timeline,
                           int idx, long rel, int frames)
    {
        var clip = timeline[idx];
        double wanted = SourceReadFor(clip, rel);

        // Rebind when the section changed, or when the read position no longer agrees with the
        // caller's clock. At a SEAM the second section's read position is exactly where the first
        // one had already got to, so this does not fire and nothing is re-primed — which is what
        // makes splitting a clip and leaving it alone change no samples at all.
        if (!ReferenceEquals(_bound, clip) || Math.Abs(wanted - _sourcePos) > 2)
        {
            bool continuous = ReferenceEquals(_bound, clip) is false
                           && idx > 0 && timeline.SeamBefore(idx)
                           && Math.Abs(wanted - _sourcePos) <= 2;

            _bound = clip;
            if (!continuous)
            {
                _sourcePos = wanted;
                _inCarry = 0;
                _needsPrime = true;
                // The EQ's delay line still holds whatever came before, and without re-priming it
                // the first LatencySamples of the section come out as that instead of as the
                // section. On a clip whose opening IS the transient, those samples are the point.
                Eq.Reset();
                PrimeEq();
            }
        }

        // How far this section is allowed to read. Normally its own end — but the read position
        // runs the EQ's and the stretcher's latency AHEAD of what is coming out, so stopping dead
        // at the end would feed those filters silence for the last few hundred frames of every
        // section. After a non-seam join that silence is thrown away by the re-prime; across a
        // SEAM there is no re-prime, and it would emerge as a notch at a join that is supposed to
        // be inaudible. A seam continues in the same file, so the read simply carries on into it.
        long sourceEnd = clip.SourceEnd;
        for (int j = idx; j + 1 < timeline.Count && timeline.SeamAfter(j); j++)
            sourceEnd = timeline[j + 1].SourceEnd;

        if (IsUnprocessed) RenderDirect(work, frames, clip, sourceEnd);
        else if (_mode == StretchMode.Linked) RenderLinked(work, frames, clip, sourceEnd);
        else RenderIndependent(work, frames, clip, sourceEnd);

        // A genuine internal join would otherwise put this section's fade-in immediately after the
        // previous one's fade-out: two ramps back to back, which is a hole rather than a join. On
        // the paths where we can read the outgoing section's continuation, blend against it
        // instead, so the two sum to unity. The vocoder path cannot — one stretcher cannot emit
        // two sections — and relies on its own 120 ms overlap-add to smooth the switch.
        if (idx > 0 && !timeline.SeamBefore(idx) && _mode != StretchMode.Independent
            && clip.FadeInSeconds <= 0 && TrackFrameAt(rel) == clip.TrackStartFrame)
        {
            var prev = timeline[idx - 1];
            if (prev.TrackEndFrame == clip.TrackStartFrame && prev.Audio is not null
                && prev.FadeOutSeconds <= 0)
                BlendAgainstHandle(work, frames, prev);
        }

        Eq.Process(work);
        FillEnvelope(env, timeline, idx, rel, frames);
    }

    /// <summary>
    /// Crossfades the first <see cref="DeclickSeconds"/> of this run against what the previous
    /// section would have played had it carried on — its "handle". Equal power, so the two sum to
    /// unity through the join rather than dipping.
    /// </summary>
    private void BlendAgainstHandle(Span<float> work, int frames, VoiceClip prev)
    {
        int n = Math.Min(frames, (int)(DeclickSeconds * _sampleRate));
        if (n <= 0 || prev.Audio is null) return;

        int need = n * _channels;
        if (_handleScratch.Length < need) _handleScratch = new float[need];
        var handle = _handleScratch.AsSpan(0, need);
        handle.Clear();

        // The handle starts exactly where the previous section was cut off, and is read at the
        // same rate the main path is running at so the two stay in step.
        double pos = prev.SourceEnd + (Eq.LatencySamples + StretchLatencyFrames) * _speed;
        if (_mode == StretchMode.Linked && _speed != 1.0)
        {
            var kernel = SincResampler.KernelFor(_speed);
            var src = prev.Audio.Samples.AsSpan();
            long count = prev.Audio.FrameCount;
            for (int f = 0; f < n; f++, pos += _speed)
                for (int c = 0; c < _channels; c++)
                    handle[f * _channels + c] =
                        (float)SincResampler.Sample(src, count, _channels, c, pos, kernel);
        }
        else
        {
            CopySource(prev, (long)Math.Round(pos), n, handle, prev.Audio.FrameCount);
        }

        for (int f = 0; f < n; f++)
        {
            double t = (f + 0.5) / n;
            double rise = Math.Sin(Math.PI / 2 * t);
            double fall = Math.Cos(Math.PI / 2 * t);
            for (int c = 0; c < _channels; c++)
            {
                int i = f * _channels + c;
                work[i] = (float)(work[i] * rise + handle[i] * fall);
            }
        }
    }

    /// <summary>
    /// Writes this run's share of the section envelope.
    ///
    /// <para>
    /// An explicit fade always wins. Otherwise an edge gets a declick only where it was actually
    /// <b>cut</b> and there is nothing butted up against it. An uncut edge gets no ramp at all,
    /// which is what keeps an unedited track bit-identical through the render.
    /// </para>
    /// <para>
    /// Both sides of a butt join are left alone deliberately. Two abutting declicks do not sum to
    /// unity — one ramps down to nothing and the next ramps back up, so every loop repeat would
    /// punch a ten-millisecond hole in itself. The join is covered instead by the crossfade
    /// against the outgoing section's handle, or, on the vocoder path where that is not possible,
    /// by the stretcher's own overlap-add.
    /// </para>
    /// </summary>
    private void FillEnvelope(Span<float> env, ClipTimeline timeline, int idx, long rel, int frames)
    {
        var clip = timeline[idx];
        long lengthOut = (long)(clip.LengthFrames / _speed);
        long startRel = (long)(clip.TrackStartFrame / _speed);

        bool joinedIn = idx > 0 && timeline[idx - 1].TrackEndFrame == clip.TrackStartFrame;
        bool joinedOut = idx + 1 < timeline.Count
                      && timeline[idx + 1].TrackStartFrame == clip.TrackEndFrame;

        int fadeIn = clip.FadeInSeconds > 0
            ? (int)(clip.FadeInSeconds * _sampleRate)
            : joinedIn ? 0 : EnvelopeFrames(0, clip.CutAtStart);

        int fadeOut = clip.FadeOutSeconds > 0
            ? (int)(clip.FadeOutSeconds * _sampleRate)
            : joinedOut ? 0 : EnvelopeFrames(0, clip.CutAtEnd);

        for (int f = 0; f < frames; f++)
            env[f] = (float)ClipEnvelope(rel + f - startRel, lengthOut, fadeIn, fadeOut);
    }

    /// <summary>Straight copy for a track with no speed or pitch change — no DSP in the path at all.</summary>
    private void RenderDirect(Span<float> work, int frames, VoiceClip clip, long sourceEnd)
    {
        long start = (long)Math.Round(_sourcePos);
        CopySource(clip, start, frames, work, sourceEnd);
        _sourcePos = start + frames;
    }

    private void RenderLinked(Span<float> work, int frames, VoiceClip clip, long sourceEnd)
    {
        var kernel = SincResampler.KernelFor(_speed);
        var src = clip.Audio!.Samples.AsSpan();
        long count = clip.Audio.FrameCount;

        for (int f = 0; f < frames; f++)
        {
            if (_sourcePos >= sourceEnd) break;
            for (int c = 0; c < _channels; c++)
                work[f * _channels + c] = (float)SincResampler.Sample(src, count, _channels, c, _sourcePos, kernel);
            _sourcePos += _speed;
        }
    }

    private void RenderIndependent(Span<float> work, int frames, VoiceClip clip, long sourceEnd)
    {
        var st = _stretch ??= CreateStretch();      // EnsureStretchIfNeeded normally got here first
        // CreateStretch sets _dirty, but ApplyParametersIfDirty already ran for this block — before
        // the stretcher existed — and cleared the flag against a null. Without this the first block
        // of a pitched track comes out at the ORIGINAL pitch, and whether that happens depends on
        // whether anything previously created the stretcher, so two renders of one project differ.
        ApplyParametersIfDirty();

        // How many input frames this run of output consumes, carrying the fraction forward so the
        // read position does not drift over a four-minute song.
        double exact = frames * _speed + _inCarry;
        int inFrames = (int)Math.Floor(exact);
        _inCarry = exact - inFrames;
        if (inFrames <= 0) inFrames = 1;

        if (_needsPrime) { Prime(st, clip); _needsPrime = false; }

        long start = (long)Math.Round(_sourcePos);
        EnsureInScratch(inFrames);
        var input = _inScratch.AsSpan(0, inFrames * _channels);
        CopySource(clip, start, inFrames, input, sourceEnd);

        st.Process(input, inFrames, work, frames);
        _sourcePos = start + inFrames;
    }

    /// <summary>
    /// Feeds the stretcher the audio immediately before the read position, so the first block
    /// after a jump is continuous instead of fading up out of an empty analysis window.
    ///
    /// <para>
    /// Both halves have to be primed. <c>Seek</c> fills the <b>analysis</b> history only; the
    /// synthesis side still starts empty, and the stretcher then emits silence for its first
    /// <c>SeekLength</c> frames — 7200 frames, 150 ms, at the default preset. That is not a fade,
    /// it is a delay: every frame that follows comes out 150 ms late as well. Measured against an
    /// unprocessed track it is exactly a quarter of a beat at 98 BPM, which in a mashup is the two
    /// songs audibly flamming against each other. <c>OutputSeek</c> is the library's own primer for
    /// that half, and is fed the audio that <i>should already have come out</i>.
    /// </para>
    /// </summary>
    private unsafe void Prime(Stretch st, VoiceClip clip)
    {
        st.Reset();
        if (clip.Audio is null) return;

        long at = (long)Math.Round(_sourcePos);
        long count = clip.Audio.FrameCount;

        int need = Math.Max(0, st.SeekLength());
        if (need > 0)
        {
            EnsureInScratch(need);
            var buf = _inScratch.AsSpan(0, need * _channels);
            CopySource(clip, at - need, need, buf, count);
            st.Seek(buf, need, (float)_speed);
        }

        // OutputSeekLength scales with the playback rate (7200 at 1.0, 6854 at 0.92), so it is
        // asked fresh every time rather than cached alongside SeekLength.
        int outNeed = Math.Max(0, st.OutputSeekLength((float)_speed));
        if (outNeed == 0) return;

        EnsureInScratch(outNeed);
        var outBuf = _inScratch.AsSpan(0, outNeed * _channels);
        CopySource(clip, at - outNeed, outNeed, outBuf, count);

        // The pointer overload, deliberately: the span overload infers the frame count from the
        // span's length, which on interleaved stereo would send it reading twice past the end.
        fixed (float* p = outBuf) st.OutputSeek(p, outNeed);
    }

    private void CopySource(VoiceClip clip, long startFrame, int frames, Span<float> dest, long sourceEnd)
    {
        dest.Clear();
        var audio = clip.Audio;
        if (audio is null) return;

        var src = audio.Samples;
        for (int f = 0; f < frames; f++)
        {
            long idx = startFrame + f;
            if (idx < 0 || idx >= sourceEnd || idx >= audio.FrameCount) continue;
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
        // Stay dirty until there is actually something to apply to, or the settings are lost.
        if (_mode != StretchMode.Independent || _stretch is null) return;
        _dirty = false;

        _stretch.SetTransposeSemitones((float)_semitones, 0.5f);
        // Compensating formants keeps a pitched-up vocal sounding like a person rather than a
        // chipmunk; compensatePitch keeps the note where the user asked for it.
        _stretch.SetFormantSemitones(_preserveFormants ? 0f : (float)_semitones, _preserveFormants);
    }

    private void StepGainOnly(int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            _gain = Approach(_gain, _targetGain, _gainRamp);
            _muteGain = Approach(_muteGain, Muted ? 0 : 1, _gainRamp);
        }
    }

    private void EnsureBuffers(int frames)
    {
        int need = frames * _channels;
        if (_outScratch.Length < need) _outScratch = new float[need];
        if (_envScratch.Length < frames) _envScratch = new float[frames];
    }

    private void EnsureInScratch(int frames)
    {
        int need = Math.Max(1, frames) * _channels;
        if (_inScratch.Length < need) _inScratch = new float[need];
    }

    /// <summary>
    /// Fade length in frames. An explicit fade always applies. Otherwise a declick applies only
    /// where the section was actually <b>cut</b> — cutting into a waveform lands on a non-zero
    /// sample and that is a click. A section that runs from the start of its file to the end of it
    /// gets no ramp at all, which is what keeps an unedited track bit-identical through the render.
    /// </summary>
    private int EnvelopeFrames(double seconds, bool cutAtThisEdge)
    {
        if (seconds > 0) return (int)(seconds * _sampleRate);
        return cutAtThisEdge ? (int)(DeclickSeconds * _sampleRate) : 0;
    }

    /// <summary>
    /// Section fade envelope at a position, equal-power rather than linear. Two clips crossfading
    /// with linear ramps dip in the middle, because at the halfway point both are at half
    /// amplitude and the sum is 0.5 rather than 1; a sine law keeps the power constant instead.
    /// </summary>
    private static double ClipEnvelope(long pos, long length, int fadeIn, int fadeOut)
    {
        if (pos < 0 || length <= 0) return 0;

        double env = 1;
        if (fadeIn > 0 && pos < fadeIn)
            env = Math.Sin(Math.PI / 2 * (pos / (double)fadeIn));

        long fromEnd = length - pos;
        if (fadeOut > 0 && fromEnd < fadeOut)
        {
            double tail = Math.Sin(Math.PI / 2 * (Math.Max(0, fromEnd) / (double)fadeOut));
            env = Math.Min(env, tail);
        }
        return env;
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
