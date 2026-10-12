using System.Text.RegularExpressions;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;

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
/// <para>
/// <b>Every position is worked out in whole frames</b>, rounded exactly the way
/// <see cref="ProjectSession"/> rounds them for the engine, and only then turned back into seconds.
/// The seam rule and the butt-join rule both need two numbers to match exactly; adding seconds
/// and rounding the sum does not give the same frame as rounding each part and adding, and one
/// frame out is the difference between an inaudible join and a hole in it.
/// </para>
/// </summary>
public static class TimelineEditor
{
    /// <summary>Shorter than this and a section is not worth keeping; it is a slip of the mouse.</summary>
    public const double MinimumSectionSeconds = 0.02;

    // ---------------------------------------------------------------- frames

    /// <summary>Where a section starts, in the track's own frames, exactly as the engine will place it.</summary>
    public static long StartFrameOf(ClipModel clip, ProjectSession s)
        => s.SecondsToFrames(Math.Max(0, clip.TrackStartSeconds));

    /// <summary>First source frame a section plays, as the engine reads it.</summary>
    public static long SourceStartFrameOf(ClipModel clip, ProjectSession s)
        => s.SecondsToFrames(Math.Max(0, clip.SourceStartSeconds));

    /// <summary>One past the last source frame a section plays, "to the end of the file" resolved.</summary>
    public static long SourceEndFrameOf(ClipModel clip, ProjectSession s)
    {
        long count = s.AudioFor(clip)?.FrameCount ?? 0;
        return clip.SourceEndSeconds < 0 ? count : Math.Min(s.SecondsToFrames(clip.SourceEndSeconds), count);
    }

    /// <summary>A section's length in frames — the engine's <see cref="VoiceClip.LengthFrames"/>.</summary>
    public static long LengthFramesOf(ClipModel clip, ProjectSession s)
        => Math.Max(0, SourceEndFrameOf(clip, s) - SourceStartFrameOf(clip, s));

    /// <summary>One past a section's last frame, in the track's own frames.</summary>
    public static long EndFrameOf(ClipModel clip, ProjectSession s)
        => StartFrameOf(clip, s) + LengthFramesOf(clip, s);

    public static double SecondsOf(long frames, ProjectSession s) => frames / (double)s.SampleRate;

    /// <summary>The track's own frame at a timeline position. Negative before the track's origin.</summary>
    public static long TrackFrameAt(TrackModel track, double timelineSeconds, ProjectSession s)
        => (long)Math.Round(TrackSecondsAt(track, timelineSeconds) * s.SampleRate);

    /// <summary>Where a frame of the track's own timebase sits on the timeline.</summary>
    public static double TimelineSecondsOfFrame(TrackModel track, long frame, ProjectSession s)
        => TimelineSecondsAt(track, SecondsOf(frame, s));

    private static long MinimumFrames(ProjectSession s) => s.SecondsToFrames(MinimumSectionSeconds);

    // ---------------------------------------------------------------- lookups

    /// <summary>
    /// The section covering a position in the track's own unstretched timebase, or null in a gap.
    /// </summary>
    public static ClipModel? ClipAt(TrackModel track, double trackSeconds, ProjectSession session)
    {
        long f = session.SecondsToFrames(trackSeconds);
        foreach (var c in track.Clips)
            if (f >= StartFrameOf(c, session) && f < EndFrameOf(c, session)) return c;
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
        => SecondsMapOf(track) is { } map
            ? map.PositionAt(timelineSeconds - track.TimelineOffsetSeconds)
            : (timelineSeconds - track.TimelineOffsetSeconds) * Math.Max(0.01, track.Speed);

    /// <summary>Converts a position in the track's own timebase back onto the timeline.</summary>
    public static double TimelineSecondsAt(TrackModel track, double trackSeconds)
        => SecondsMapOf(track) is { } map
            ? track.TimelineOffsetSeconds + map.RelAt(trackSeconds)
            : track.TimelineOffsetSeconds + trackSeconds / Math.Max(0.01, track.Speed);

    /// <summary>How long on the timeline a stretch of the track takes, from <paramref name="trackStartSeconds"/> on.</summary>
    public static double TimelineLength(TrackModel track, double trackStartSeconds, double lengthSeconds)
        => SecondsMapOf(track) is { } map
            ? map.DurationOf(trackStartSeconds, lengthSeconds)
            : lengthSeconds / Math.Max(0.01, track.Speed);

    /// <summary>The speed the track plays at at a point of the timeline.</summary>
    public static double SpeedAt(TrackModel track, double timelineSeconds)
        => SecondsMapOf(track) is { } map
            ? map.SpeedAt(timelineSeconds - track.TimelineOffsetSeconds)
            : Math.Max(0.01, track.Speed);

    /// <summary>
    /// The track's tempo map in seconds from its origin, for a track with tempo ramps; null for one
    /// without, whose conversions keep their own arithmetic exactly. The same map the engine plays,
    /// in another unit. Built leniently, so a slip in a project file can never throw while drawing.
    /// </summary>
    public static TempoMap? SecondsMapOf(TrackModel track)
        => track.Ramps.Count == 0
            ? null
            : TempoMap.Lenient(track.Speed, track.Ramps.Select(r => new TempoMap.Ramp(
                  r.StartSeconds - track.TimelineOffsetSeconds, r.EndSeconds - track.TimelineOffsetSeconds, r.ToSpeed)));

    // ---------------------------------------------------------------- split, copy, delete

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

        long start = StartFrameOf(clip, session);
        long into = session.SecondsToFrames(at) - start;
        long length = LengthFramesOf(clip, session);
        long minimum = MinimumFrames(session);
        if (into < minimum || length - into < minimum)
            return EditResult.No("That is too close to the edge of the section to split.");

        // One number, written to both sides, so the seam test is an exact match rather than a
        // near-miss that would reintroduce a declick at a join that should be silent. It is a whole
        // frame, so the second half's start lands on the very frame the first half's end does.
        double boundary = SecondsOf(SourceStartFrameOf(clip, session) + into, session);

        var second = clip.Clone();
        second.Id = Guid.NewGuid();
        second.TrackStartSeconds = SecondsOf(start + into, session);
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

    // ---------------------------------------------------------------- paste and repeat

    /// <summary>
    /// Drops a section onto a track at a timeline position, overwriting whatever is under it.
    ///
    /// <para>
    /// Overwrite rather than insert, because inserting would push everything after it along and
    /// break the track's alignment with every other one. What is underneath is trimmed, split
    /// around, or removed, and nothing else moves.
    /// </para>
    /// <para>
    /// It lands exactly at <paramref name="timelineSeconds"/>, even before where the track's own
    /// song begins: the track's origin moves back to make room rather than acting as a wall.
    /// </para>
    /// <para>
    /// <paramref name="copiedFrom"/> is the track the section was copied off. An empty track takes
    /// on that track's tempo and pitch with it, so the section plays exactly as it did there.
    /// </para>
    /// </summary>
    public static EditResult Paste(TrackModel track, ClipModel clipboard, double timelineSeconds,
                                   ProjectSession session, TrackModel? copiedFrom = null)
    {
        long length = LengthFramesOf(clipboard, session);
        if (length < MinimumFrames(session))
            return EditResult.No("There is nothing on the clipboard to paste.");

        bool adopted = copiedFrom is not null && Adopt(track, SettingsOf(copiedFrom));

        double at = Math.Max(0, timelineSeconds);
        EnsureOriginAtOrBefore(track, at, session);
        long start = Math.Max(0, TrackFrameAt(track, at, session));

        var placed = clipboard.Clone();
        placed.Id = Guid.NewGuid();
        placed.TrackStartSeconds = SecondsOf(start, session);

        ClearRange(track, start, start + length, session);
        track.Clips.Add(placed);
        Normalise(track);

        string note = adopted
            ? copiedFrom is { Ramps.Count: > 0 }
                ? " The track takes that song's tempo, its tempo ramps and pitch."
                : " The track takes that song's tempo and pitch, so it plays as it did."
            : placed.SourcePath.Equals(track.SourcePath, StringComparison.OrdinalIgnoreCase)
                ? ""
                : " It takes this track's tempo, pitch and EQ.";
        return new EditResult(true, $"Pasted {SecondsOf(length, session):0.0}s at {at:0.00}s.{note}", placed);
    }

    /// <summary>
    /// Puts a second copy of a section immediately after itself — the one keystroke that turns a
    /// chosen section into a hand-built loop when held down.
    /// </summary>
    public static EditResult Duplicate(TrackModel track, ClipModel clip, ProjectSession session)
    {
        long length = LengthFramesOf(clip, session);
        if (length < MinimumFrames(session)) return EditResult.No("That section is too short to repeat.");

        // Counted in frames from where the engine really ends the section, so the copy starts on
        // exactly that frame. Summed in seconds it could start one or two frames late: a gap, with
        // a declick either side of it, which is a hole at every repeat.
        long start = EndFrameOf(clip, session);
        var copy = Copy(clip);
        copy.TrackStartSeconds = SecondsOf(start, session);

        ClearRange(track, start, start + length, session);
        track.Clips.Add(copy);
        Normalise(track);
        return new EditResult(true, $"Repeated {SecondsOf(length, session):0.0}s at {TimelineSecondsOfFrame(track, start, session):0.00}s.", copy);
    }

    /// <summary>
    /// Makes a section on a track that already exists sound where it was dropped, overwriting
    /// whatever is under it — the end of a drag, the same rule as paste.
    /// </summary>
    /// <returns>How many other sections were cut back or removed to make room.</returns>
    public static int Settle(TrackModel track, ClipModel clip, ProjectSession session)
    {
        long start = StartFrameOf(clip, session);
        int covered = ClearRange(track, start, start + LengthFramesOf(clip, session), session, except: clip);
        Normalise(track);
        return covered;
    }

    // ---------------------------------------------------------------- the track's origin

    /// <summary>
    /// Moves a track's origin back by <paramref name="frames"/> of its own timebase, moving every
    /// section forward by the same whole number of frames so that nothing is heard to move.
    ///
    /// <para>
    /// Exact at the track's original tempo: every section starts on the same engine frame as
    /// before. At any other tempo the origin cannot land on a whole output frame and a whole track
    /// frame at once, so a section may move by up to one frame — about twenty microseconds.
    /// Every join between sections is untouched either way, because they all move together.
    /// </para>
    /// <para>
    /// It never moves further than the track's origin can go, which is back to 0:00. Asked for
    /// more, it used to shift every section anyway and pin the origin at zero — pushing the whole
    /// track later on the timeline, out of line with every other deck.
    /// </para>
    /// </summary>
    /// <returns>How many frames it actually moved: <paramref name="frames"/>, or fewer at 0:00.</returns>
    public static long ShiftOrigin(TrackModel track, long frames, ProjectSession session)
    {
        frames = Math.Min(frames, OriginRoomFrames(track, session));
        if (frames <= 0) return 0;
        var map = SecondsMapOf(track);                     // before the origin moves: it is measured from it
        foreach (var c in track.Clips)
            c.TrackStartSeconds = SecondsOf(StartFrameOf(c, session) + frames, session);

        if (map is null)
        {
            double speed = Math.Max(0.01, track.Speed);
            track.TimelineOffsetSeconds = Math.Max(0, track.TimelineOffsetSeconds - SecondsOf(frames, session) / speed);
        }
        else
        {
            // Under tempo ramps the origin goes back by as much timeline as those frames take at the
            // tempo there. The ramps themselves sit on the timeline and do not move.
            track.TimelineOffsetSeconds = Math.Max(0, track.TimelineOffsetSeconds + map.RelAt(-SecondsOf(frames, session)));
        }
        return frames;
    }

    /// <summary>How far, in the track's own frames, its origin can move back before reaching 0:00.</summary>
    public static long OriginRoomFrames(TrackModel track, ProjectSession session)
        => SecondsMapOf(track) is { } map
            ? (long)Math.Floor(-map.PositionAt(-track.TimelineOffsetSeconds) * session.SampleRate + 1e-6)
            : (long)Math.Floor(track.TimelineOffsetSeconds * session.SampleRate * Math.Max(0.01, track.Speed) + 1e-6);

    /// <summary>
    /// Makes sure a section can be placed at <paramref name="timelineSeconds"/> on this track —
    /// that the point is not before where the track's timebase begins.
    ///
    /// <para>
    /// A track's origin is where its song was first dropped, and it used to be a wall: a section
    /// pasted or dragged before it was clamped to it, landing somewhere other than where it was
    /// put. The origin is bookkeeping, not something anyone chose, so it moves instead.
    /// </para>
    /// </summary>
    /// <returns>True when the origin moved.</returns>
    public static bool EnsureOriginAtOrBefore(TrackModel track, double timelineSeconds, ProjectSession session)
    {
        long at = TrackFrameAt(track, Math.Max(0, timelineSeconds), session);
        if (at >= 0) return false;
        ShiftOrigin(track, -at, session);
        return true;
    }

    // ---------------------------------------------------------------- empty tracks

    /// <summary>
    /// The settings that decide how a track plays a section — when it lands and what note it is —
    /// plus what the strip shows about the song. Not the EQ or the volume: those are mix decisions
    /// for the deck, and a fresh deck starts flat. The name is kept only so that
    /// <see cref="Restore"/> can put it back; adopting never renames a track.
    /// </summary>
    public sealed record DeckSettings(
        string Name, string SourcePath, double Speed, double Semitones, StretchMode Mode,
        bool PreserveFormants, double Bpm, string? BpmSource, string? Key, string? Camelot, string? KeySource,
        IReadOnlyList<TempoRamp> Ramps)
    {
        /// <summary>The same tempo, pitch and tempo ramps: a section moved between the two plays at the same feel.</summary>
        public bool SameFeelAs(TrackModel t)
            => t.Speed == Speed && t.Semitones == Semitones && t.Mode == Mode && t.Ramps.SequenceEqual(Ramps);
    }

    public static DeckSettings SettingsOf(TrackModel t) => new(
        t.Name, t.SourcePath, t.Speed, t.Semitones, t.Mode, t.PreserveFormants,
        t.Bpm, t.BpmSource, t.Key, t.Camelot, t.KeySource, t.Ramps.ToArray());

    /// <summary>A track with no song of its own and nothing on it — one made with "empty track".</summary>
    public static bool IsEmptyDeck(TrackModel t) => t.Clips.Count == 0 && string.IsNullOrEmpty(t.SourcePath);

    private static readonly Regex AutoName = new(@"^Track \d+$", RegexOptions.CultureInvariant);

    /// <summary>True for the placeholder name an empty track is given, which nobody chose.</summary>
    public static bool IsAutoName(string name) => AutoName.IsMatch(name);

    /// <summary>
    /// Gives an empty track the tempo and pitch a section was playing at, so that dropping it there
    /// changes nothing about how it sounds or when it lands. A track that already has a song keeps
    /// its own: a section put there takes on that track's settings, as it always has.
    /// </summary>
    /// <returns>True when the track was empty and took the settings.</returns>
    public static bool Adopt(TrackModel target, DeckSettings from)
    {
        if (!IsEmptyDeck(target)) return false;

        target.SourcePath = from.SourcePath;
        target.Speed = from.Speed;
        target.Semitones = from.Semitones;
        target.Mode = from.Mode;
        target.PreserveFormants = from.PreserveFormants;
        target.Bpm = from.Bpm;
        target.BpmSource = from.BpmSource;
        target.Key = from.Key;
        target.Camelot = from.Camelot;
        target.KeySource = from.KeySource;
        // Its tempo ramps too: they are spans of the timeline, so the empty track then plays the very
        // speed profile the section was playing under.
        target.Ramps = from.Ramps.ToList();
        // The name stays. A second deck holding part of a song is still a second deck, and two
        // tracks both called after the song would make every status message ambiguous.
        return true;
    }

    /// <summary>Puts a track back exactly as it was before it adopted anything.</summary>
    public static void Restore(TrackModel target, DeckSettings before)
    {
        target.Name = before.Name;
        target.SourcePath = before.SourcePath;
        target.Speed = before.Speed;
        target.Semitones = before.Semitones;
        target.Mode = before.Mode;
        target.PreserveFormants = before.PreserveFormants;
        target.Bpm = before.Bpm;
        target.BpmSource = before.BpmSource;
        target.Key = before.Key;
        target.Camelot = before.Camelot;
        target.KeySource = before.KeySource;
        target.Ramps = before.Ramps.ToList();
    }

    // ---------------------------------------------------------------- making room

    /// <summary>
    /// Empties a span of the track's own frames: sections wholly inside it go, ones that straddle
    /// an edge are cut back to it, and one that spans the whole range is split around it. Every
    /// cut lands on the span's own frames, so whatever is put there abuts its neighbours exactly.
    /// </summary>
    /// <returns>How many sections were cut back or removed.</returns>
    internal static int ClearRange(TrackModel track, long start, long end, ProjectSession session,
                                   ClipModel? except = null, IReadOnlySet<ClipModel>? exceptAll = null)
    {
        int touched = 0;
        var cut = new List<ClipModel>();
        foreach (var c in track.Clips.ToList())
        {
            if (ReferenceEquals(c, except) || exceptAll?.Contains(c) == true) continue;
            long length = LengthFramesOf(c, session);
            if (length <= 0) continue;                          // no audio: nothing to collide with

            long cStart = StartFrameOf(c, session);
            long cEnd = cStart + length;
            if (cEnd <= start || cStart >= end) continue;       // clear of the range
            touched++;

            if (cStart >= start && cEnd <= end) { track.Clips.Remove(c); continue; }

            long source = SourceStartFrameOf(c, session);
            if (cStart < start && cEnd > end)
            {
                // Straddles the whole range: keep the head, and add the tail back after it.
                var tail = c.Clone();
                tail.Id = Guid.NewGuid();
                tail.TrackStartSeconds = SecondsOf(end, session);
                tail.SourceStartSeconds = SecondsOf(source + (end - cStart), session);
                tail.FadeInSeconds = 0;
                track.Clips.Add(tail);
                cut.Add(tail);

                c.SourceEndSeconds = SecondsOf(source + (start - cStart), session);
                c.FadeOutSeconds = 0;
                cut.Add(c);
                continue;
            }

            if (cStart < start)                                            // overlaps the left edge
            {
                c.SourceEndSeconds = SecondsOf(source + (start - cStart), session);
                c.FadeOutSeconds = 0;
            }
            else                                                           // overlaps the right edge
            {
                c.SourceStartSeconds = SecondsOf(source + (end - cStart), session);
                c.TrackStartSeconds = SecondsOf(end, session);
                c.FadeInSeconds = 0;
            }
            cut.Add(c);
        }

        // Only what was just cut can have become a sliver. A section whose file is missing reads
        // as zero length too, and deleting it for that would lose it for good once the file is back.
        long minimum = MinimumFrames(session);
        foreach (var c in cut)
            if (LengthFramesOf(c, session) < minimum) track.Clips.Remove(c);
        return touched;
    }

    /// <summary>Keeps the list in playing order, which everything downstream assumes.</summary>
    public static void Normalise(TrackModel track)
        => track.Clips.Sort((a, b) => a.TrackStartSeconds.CompareTo(b.TrackStartSeconds));
}
