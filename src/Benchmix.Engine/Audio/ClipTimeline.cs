namespace Benchmix.Engine.Audio;

/// <summary>
/// One section a track plays: which audio, which part of it, and where it sits.
///
/// <para>
/// <see cref="TrackStartFrame"/> is in the track's <b>own, unstretched</b> frames, so it is the
/// same clock as the source. That keeps <c>Speed</c> out of the structure entirely: changing a
/// track's tempo rescales where every clip lands without rebuilding anything, so turning the TEMPO
/// knob cannot restart the vocoder sixty times a second.
/// </para>
/// </summary>
public sealed class VoiceClip
{
    public AudioClip? Audio { get; init; }

    /// <summary>Where this section starts in the track's own unstretched frames.</summary>
    public long TrackStartFrame { get; init; }

    /// <summary>First source frame used.</summary>
    public long SourceStartFrame { get; init; }

    /// <summary>One past the last source frame used, or -1 for the end of the file.</summary>
    public long SourceEndFrame { get; init; } = -1;

    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }

    /// <summary>
    /// The crossfade into this section where it butts against the one before (2026-10-28): the
    /// length chosen for that splice, or 0 for the standard <see cref="TrackVoice.DeclickSeconds"/>.
    /// Nothing at a seam, a gap, or a join with a fade of its own on either side.
    /// </summary>
    public double CrossfadeSeconds { get; init; }

    /// <summary>Resolved end, with -1 and any over-long value pulled back to the file's length.</summary>
    public long SourceEnd
    {
        get
        {
            long count = Audio?.FrameCount ?? 0;
            return SourceEndFrame < 0 ? count : Math.Min(SourceEndFrame, count);
        }
    }

    /// <summary>Length in source frames, which is also its length in track frames.</summary>
    public long LengthFrames => Math.Max(0, SourceEnd - SourceStartFrame);

    public long TrackEndFrame => TrackStartFrame + LengthFrames;

    /// <summary>True when this section does not begin at the very start of its file.</summary>
    public bool CutAtStart => SourceStartFrame > 0;

    /// <summary>True when this section does not run to the very end of its file.</summary>
    public bool CutAtEnd => Audio is not null && SourceEnd < Audio.FrameCount;

    /// <summary>
    /// Whether <paramref name="next"/> continues this section exactly: same audio, its source
    /// picking up precisely where this one stops, laid down precisely where this one ends, and
    /// neither side carrying a fade of its own.
    ///
    /// <para>
    /// This is the test for a <b>seam</b> — the join left behind by splitting a clip and not
    /// moving either half. At a seam nothing happens at all: no envelope, no declick, no
    /// re-prime, no crossfade. Splitting a clip in two therefore changes not one sample, which is
    /// the first thing anyone tries and the thing that would be most alarming to get wrong.
    /// </para>
    /// <para>
    /// The comparison is exact rather than within a tolerance, deliberately. Both numbers are
    /// written from the same value by the split command and converted by the same rounding, so
    /// they match. A tolerance would hide the day some other code path stops producing matching
    /// numbers, and would start silently suppressing declicks between sections the user really did
    /// mean to be separate.
    /// </para>
    /// </summary>
    public bool JoinsSeamlesslyTo(VoiceClip next)
        => Audio is not null
        && ReferenceEquals(Audio, next.Audio)
        && SourceEnd == next.SourceStartFrame
        && TrackEndFrame == next.TrackStartFrame
        && FadeOutSeconds <= 0
        && next.FadeInSeconds <= 0;
}

/// <summary>
/// A track's sections, in order and never overlapping, published as one immutable object.
///
/// <para>
/// The render thread reads this through a single volatile field, so it sees either the whole old
/// arrangement or the whole new one and never a half-applied edit — the same trick
/// <see cref="MixGraph"/> uses for its voice array. Nothing here is mutated after construction.
/// </para>
/// </summary>
public sealed class ClipTimeline
{
    public static readonly ClipTimeline Empty = new(Array.Empty<VoiceClip>());

    private readonly VoiceClip[] _clips;
    private readonly bool[] _seamBefore;
    private readonly bool[] _seamAfter;

    public ClipTimeline(IEnumerable<VoiceClip> clips)
    {
        // Sorted because everything downstream walks them in order; empty ones dropped because a
        // zero-length clip has no position worth honouring and would only complicate the walk.
        var list = clips.Where(c => c.Audio is not null && c.LengthFrames > 0)
                        .OrderBy(c => c.TrackStartFrame)
                        .ToList();

        // Overlap is prevented by the edit commands. This is the last line of defence: a clip that
        // would sound on top of its neighbour is truncated rather than allowed to produce two read
        // positions for one stretcher, which is not representable and would sound like a fight.
        for (int i = 0; i + 1 < list.Count; i++)
        {
            if (list[i].TrackEndFrame <= list[i + 1].TrackStartFrame) continue;
            long room = list[i + 1].TrackStartFrame - list[i].TrackStartFrame;
            list[i] = room <= 0 ? null! : Truncate(list[i], room);
        }
        list.RemoveAll(c => c is null);

        _clips = list.ToArray();
        _seamBefore = new bool[_clips.Length];
        _seamAfter = new bool[_clips.Length];
        _buttAfter = new bool[_clips.Length];
        for (int i = 0; i + 1 < _clips.Length; i++)
        {
            var (a, b) = (_clips[i], _clips[i + 1]);
            bool seam = a.JoinsSeamlesslyTo(b);
            _seamAfter[i] = seam;
            _seamBefore[i + 1] = seam;
            _buttAfter[i] = !seam
                         && a.TrackEndFrame == b.TrackStartFrame
                         && a.FadeOutSeconds <= 0
                         && b.FadeInSeconds <= 0;
        }
    }

    private readonly bool[] _buttAfter;

    private static VoiceClip Truncate(VoiceClip c, long lengthFrames) => new()
    {
        Audio = c.Audio,
        TrackStartFrame = c.TrackStartFrame,
        SourceStartFrame = c.SourceStartFrame,
        SourceEndFrame = c.SourceStartFrame + lengthFrames,
        FadeInSeconds = c.FadeInSeconds,
        FadeOutSeconds = c.FadeOutSeconds,
        CrossfadeSeconds = c.CrossfadeSeconds,
    };

    public int Count => _clips.Length;
    public VoiceClip this[int i] => _clips[i];

    /// <summary>True when the section before this one runs into it with no join at all.</summary>
    public bool SeamBefore(int i) => _seamBefore[i];

    /// <summary>True when this section runs into the next one with no join at all.</summary>
    public bool SeamAfter(int i) => _seamAfter[i];

    /// <summary>
    /// True when the next section starts on the very frame this one ends, but does NOT continue it
    /// — a loop repeat, or two different passages butted together — and neither side carries a
    /// fade of its own. That join is crossfaded rather than declicked; see
    /// <see cref="TrackVoice"/>.
    /// </summary>
    public bool ButtJoinAfter(int i) => _buttAfter[i];

    /// <summary>End of the last section, in the track's own unstretched frames.</summary>
    public long TrackEndFrame => _clips.Length == 0 ? 0 : _clips[^1].TrackEndFrame;

    /// <summary>Index of the section covering <paramref name="trackFrame"/>, or -1 for a gap.</summary>
    public int IndexAt(long trackFrame)
    {
        int lo = 0, hi = _clips.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (trackFrame < _clips[mid].TrackStartFrame) hi = mid - 1;
            else if (trackFrame >= _clips[mid].TrackEndFrame) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>
    /// Where the next section begins at or after <paramref name="trackFrame"/>, or -1 when there
    /// is nothing further. Used to size a run of silence so a gap costs one step rather than one
    /// step per frame.
    /// </summary>
    public long NextStartAtOrAfter(long trackFrame)
    {
        foreach (var c in _clips)
            if (c.TrackStartFrame >= trackFrame) return c.TrackStartFrame;
        return -1;
    }

    /// <summary>
    /// The section in this timeline identical to <paramref name="clip"/> — same audio, same
    /// place, same cut, same fades — or null if there is none. Lets a voice keep playing a section
    /// across an edit that did not touch it.
    /// </summary>
    public VoiceClip? Equivalent(VoiceClip? clip)
    {
        if (clip is null) return null;
        int i = IndexAt(clip.TrackStartFrame);
        if (i < 0) return null;
        var c = _clips[i];
        return ReferenceEquals(c.Audio, clip.Audio)
            && c.TrackStartFrame == clip.TrackStartFrame
            && c.SourceStartFrame == clip.SourceStartFrame
            && c.SourceEndFrame == clip.SourceEndFrame
            && c.FadeInSeconds == clip.FadeInSeconds
            && c.FadeOutSeconds == clip.FadeOutSeconds
            && c.CrossfadeSeconds == clip.CrossfadeSeconds
            ? c
            : null;
    }

    /// <summary>Whether two timelines describe the same arrangement, field for field.</summary>
    public bool Matches(ClipTimeline other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (_clips.Length != other._clips.Length) return false;
        for (int i = 0; i < _clips.Length; i++)
        {
            var a = _clips[i];
            var b = other._clips[i];
            if (!ReferenceEquals(a.Audio, b.Audio)
                || a.TrackStartFrame != b.TrackStartFrame
                || a.SourceStartFrame != b.SourceStartFrame
                || a.SourceEndFrame != b.SourceEndFrame
                || a.FadeInSeconds != b.FadeInSeconds
                || a.FadeOutSeconds != b.FadeOutSeconds
                || a.CrossfadeSeconds != b.CrossfadeSeconds) return false;
        }
        return true;
    }
}
