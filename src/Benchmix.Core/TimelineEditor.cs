using Benchmix.Core.Model;

namespace Benchmix.Core;

/// <summary>What an edit did, in words the status bar can show.</summary>
public sealed record EditResult(bool Changed, string Message, ClipModel? Clip = null)
{
    public static EditResult No(string why) => new(false, why);
}

/// <summary>
/// Splitting, cutting, copying and pasting sections of a track.
///
/// <para>
/// Three rules decide everything here, and all three exist because Benchmix is a <b>mashup</b>
/// tool rather than a single-track editor:
/// </para>
/// <list type="number">
/// <item>
/// <b>Nothing ever ripples.</b> Cutting or deleting leaves a hole. Audacity closes the gap because
/// Audacity has no second song that has to stay in time; here, sliding one track along would pull
/// it out of line with every other deck, silently, and usually unnoticed until thirty seconds
/// later in the mix. Two songs lined up by ear are the whole value of a project file.
/// </item>
/// <item>
/// <b>Sections on one track never overlap.</b> A track is one read position, one stretcher and
/// one EQ; two sections sounding at once is not representable in that. Paste overwrites what is
/// under it. Two things at once is what a second track is for.
/// </item>
/// <item>
/// <b>Splitting and leaving both halves alone changes nothing.</b> The two halves meet at a seam,
/// which the engine plays straight through with no fade, no declick and no re-prime.
/// </item>
/// </list>
/// </summary>
public static class TimelineEditor
{
    /// <summary>Shorter than this and a section is not worth keeping; it is a slip of the mouse.</summary>
    public const double MinimumSectionSeconds = 0.02;

    /// <summary>
    /// The section covering a position in the track's own unstretched timebase, or null in a gap.
    /// </summary>
    public static ClipModel? ClipAt(TrackModel track, double trackSeconds, ProjectSession session)
    {
        foreach (var c in track.Clips)
        {
            double len = LengthOf(c, session);
            if (trackSeconds >= c.TrackStartSeconds && trackSeconds < c.TrackStartSeconds + len) return c;
        }
        return null;
    }

    /// <summary>A section's length in seconds, resolving "to the end of the file".</summary>
    public static double LengthOf(ClipModel clip, ProjectSession session)
    {
        double total = session.AudioFor(clip)?.DurationSeconds ?? 0;
        double end = clip.SourceEndSeconds < 0 ? total : Math.Min(clip.SourceEndSeconds, total);
        return Math.Max(0, end - Math.Max(0, clip.SourceStartSeconds));
    }

    /// <summary>Converts a timeline position into the track's own unstretched timebase.</summary>
    public static double TrackSecondsAt(TrackModel track, double timelineSeconds)
        => (timelineSeconds - track.TimelineOffsetSeconds) * Math.Max(0.01, track.Speed);

    /// <summary>Converts a position in the track's own timebase back onto the timeline.</summary>
    public static double TimelineSecondsAt(TrackModel track, double trackSeconds)
        => track.TimelineOffsetSeconds + trackSeconds / Math.Max(0.01, track.Speed);

    /// <summary>
    /// Cuts the section under <paramref name="timelineSeconds"/> in two at that point.
    ///
    /// Both halves keep the track's position, so the audio does not move: the second half starts
    /// in the source exactly where the first stops. That equality is what the engine recognises as
    /// a seam, so a split you do not follow up with anything is inaudible.
    /// </summary>
    public static EditResult Split(TrackModel track, double timelineSeconds, ProjectSession session)
    {
        double at = TrackSecondsAt(track, timelineSeconds);
        var clip = ClipAt(track, at, session);
        if (clip is null) return EditResult.No("There is nothing to split at the playhead.");

        double into = at - clip.TrackStartSeconds;
        double length = LengthOf(clip, session);
        if (into < MinimumSectionSeconds || length - into < MinimumSectionSeconds)
            return EditResult.No("That is too close to the edge of the section to split.");

        // One number, written to both sides, so the seam test is an exact match rather than a
        // near-miss that would reintroduce a declick at a join that should be silent.
        double boundary = clip.SourceStartSeconds + into;

        var second = clip.Clone();
        second.Id = Guid.NewGuid();
        second.TrackStartSeconds = clip.TrackStartSeconds + into;
        second.SourceStartSeconds = boundary;
        second.FadeInSeconds = 0;                 // a fade belongs to the outer edges, not the join

        clip.SourceEndSeconds = boundary;
        clip.FadeOutSeconds = 0;

        track.Clips.Insert(track.Clips.IndexOf(clip) + 1, second);
        Normalise(track);
        return new EditResult(true, $"Split at {timelineSeconds:0.00}s.", second);
    }

    /// <summary>Takes a copy of a section, detached from the project so it survives later edits.</summary>
    public static ClipModel Copy(ClipModel clip)
    {
        var copy = clip.Clone();
        copy.Id = Guid.NewGuid();
        return copy;
    }

    /// <summary>Removes a section, leaving a hole where it was.</summary>
    public static EditResult Delete(TrackModel track, ClipModel clip)
    {
        if (!track.Clips.Remove(clip)) return EditResult.No("That section is not on this track.");
        return new EditResult(true, "Section deleted. The gap is left as it is.");
    }

    /// <summary>
    /// Drops a section onto a track at a timeline position, overwriting whatever is under it.
    ///
    /// <para>
    /// Overwrite rather than insert, because inserting would push everything after it along and
    /// break the track's alignment with every other one. What is underneath is trimmed, split
    /// around, or removed, and nothing else moves.
    /// </para>
    /// </summary>
    public static EditResult Paste(TrackModel track, ClipModel clipboard, double timelineSeconds,
                                   ProjectSession session)
    {
        double length = LengthOf(clipboard, session);
        if (length < MinimumSectionSeconds)
            return EditResult.No("There is nothing on the clipboard to paste.");

        double start = Math.Max(0, TrackSecondsAt(track, timelineSeconds));
        double end = start + length;

        var placed = clipboard.Clone();
        placed.Id = Guid.NewGuid();
        placed.TrackStartSeconds = start;

        ClearRange(track, start, end, session);
        track.Clips.Add(placed);
        Normalise(track);

        string note = placed.SourcePath.Equals(track.SourcePath, StringComparison.OrdinalIgnoreCase)
            ? ""
            : " It takes this track's tempo, pitch and EQ.";
        return new EditResult(true, $"Pasted {length:0.0}s at {timelineSeconds:0.00}s.{note}", placed);
    }

    /// <summary>
    /// Puts a second copy of a section immediately after itself — the one keystroke that turns a
    /// chosen section into a hand-built loop when held down.
    /// </summary>
    public static EditResult Duplicate(TrackModel track, ClipModel clip, ProjectSession session)
    {
        double length = LengthOf(clip, session);
        if (length < MinimumSectionSeconds) return EditResult.No("That section is too short to repeat.");

        double start = clip.TrackStartSeconds + length;
        var copy = Copy(clip);
        copy.TrackStartSeconds = start;

        ClearRange(track, start, start + length, session);
        track.Clips.Add(copy);
        Normalise(track);
        return new EditResult(true, $"Repeated {length:0.0}s at {TimelineSecondsAt(track, start):0.00}s.", copy);
    }

    /// <summary>
    /// Empties a span of the track's own timebase: sections wholly inside it go, ones that straddle
    /// an edge are trimmed, and one that spans the whole range is split around it.
    /// </summary>
    private static void ClearRange(TrackModel track, double start, double end, ProjectSession session)
    {
        foreach (var c in track.Clips.ToList())
        {
            double cStart = c.TrackStartSeconds;
            double cEnd = cStart + LengthOf(c, session);
            if (cEnd <= start || cStart >= end) continue;                 // clear of the range

            if (cStart >= start && cEnd <= end) { track.Clips.Remove(c); continue; }

            if (cStart < start && cEnd > end)
            {
                // Straddles the whole range: keep the head, and add the tail back after it.
                var tail = c.Clone();
                tail.Id = Guid.NewGuid();
                tail.TrackStartSeconds = end;
                tail.SourceStartSeconds = c.SourceStartSeconds + (end - cStart);
                tail.FadeInSeconds = 0;
                track.Clips.Add(tail);

                c.SourceEndSeconds = c.SourceStartSeconds + (start - cStart);
                c.FadeOutSeconds = 0;
                continue;
            }

            if (cStart < start)                                            // overlaps the left edge
            {
                c.SourceEndSeconds = c.SourceStartSeconds + (start - cStart);
                c.FadeOutSeconds = 0;
            }
            else                                                           // overlaps the right edge
            {
                c.SourceStartSeconds += end - cStart;
                c.TrackStartSeconds = end;
                c.FadeInSeconds = 0;
            }
        }

        track.Clips.RemoveAll(c => c.SourceEndSeconds >= 0
                               && c.SourceEndSeconds - c.SourceStartSeconds < MinimumSectionSeconds);
    }

    /// <summary>Keeps the list in playing order, which everything downstream assumes.</summary>
    public static void Normalise(TrackModel track)
        => track.Clips.Sort((a, b) => a.TrackStartSeconds.CompareTo(b.TrackStartSeconds));
}
