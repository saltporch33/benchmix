using Benchmix.Core.Model;

namespace Benchmix.Core;

/// <summary>
/// Selecting more than one section at once (Daniel, 2026-10-03): the range a Shift+click covers,
/// the sections a selection box touches (2026-10-11), and deleting the lot. Ctrl+click needs
/// nothing here — it flips one section in or out.
/// </summary>
public static class SectionSelection
{
    /// <summary>
    /// The sections a Shift+click from <paramref name="anchor"/> to <paramref name="target"/>
    /// selects: on every track from the anchor's to the target's, inclusive, every section lying
    /// wholly within the time the two span. Either order. "Wholly" so that a whole song on a track
    /// in between is not swept up by two short pieces that merely overlap it.
    /// </summary>
    public static List<(TrackModel Track, ClipModel Clip)> Range(
        IList<TrackModel> tracks, TrackModel anchorTrack, ClipModel anchor,
        TrackModel targetTrack, ClipModel target, ProjectSession s)
    {
        var result = new List<(TrackModel, ClipModel)>();
        int a = tracks.IndexOf(anchorTrack), b = tracks.IndexOf(targetTrack);
        if (a < 0 || b < 0) return result;

        double from = Math.Min(Start(anchorTrack, anchor, s), Start(targetTrack, target, s));
        double to = Math.Max(End(anchorTrack, anchor, s), End(targetTrack, target, s));

        // A millisecond, so the two ends themselves — whose times went through the same frame
        // rounding — are never excluded by a rounding hair.
        const double tolerance = 0.001;
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
            foreach (var c in tracks[i].Clips)
            {
                // A section whose file is missing reads as zero length: it is not drawn and cannot
                // be clicked, so it must never be swept into a selection Delete then acts on — that
                // would erase the missing song's arrangement for good once the file is back.
                if (TimelineEditor.LengthFramesOf(c, s) <= 0) continue;
                if (Start(tracks[i], c, s) >= from - tolerance && End(tracks[i], c, s) <= to + tolerance)
                    result.Add((tracks[i], c));
            }
        return result;
    }

    /// <summary>
    /// The sections a selection box covers (Daniel, 2026-10-11): on every track from
    /// <paramref name="firstTrack"/> to <paramref name="lastTrack"/>, every section the box
    /// <b>touches</b>, even partly — the Windows desktop's rule, where Shift+click takes only what is
    /// wholly inside. Sections are [start, end): a box ending exactly where a section starts does not
    /// take it, and a box of no width takes the one under it. Topmost track first, then by start, so
    /// the first is the one commands should act on. Either corner may come first.
    /// </summary>
    public static List<(TrackModel Track, ClipModel Clip)> Touching(
        IList<TrackModel> tracks, int firstTrack, int lastTrack, double fromSeconds, double toSeconds, ProjectSession s)
    {
        var result = new List<(TrackModel, ClipModel)>();
        int lo = Math.Max(0, Math.Min(firstTrack, lastTrack));
        int hi = Math.Min(tracks.Count - 1, Math.Max(firstTrack, lastTrack));
        double from = Math.Min(fromSeconds, toSeconds), to = Math.Max(fromSeconds, toSeconds);

        for (int i = lo; i <= hi; i++)
            foreach (var c in tracks[i].Clips.OrderBy(c => TimelineEditor.StartFrameOf(c, s)))
            {
                // A section whose file is missing is not drawn and cannot be clicked: never selected.
                if (TimelineEditor.LengthFramesOf(c, s) <= 0) continue;
                double start = Start(tracks[i], c, s), end = End(tracks[i], c, s);
                bool reaches = start < to || (from == to && start <= to);
                if (reaches && end > from) result.Add((tracks[i], c));
            }
        return result;
    }

    private static double Start(TrackModel t, ClipModel c, ProjectSession s)
        => TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.StartFrameOf(c, s), s);

    private static double End(TrackModel t, ClipModel c, ProjectSession s)
        => TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.EndFrameOf(c, s), s);

    /// <summary>
    /// Removes every given section from its track, leaving each gap where it was — nothing
    /// ripples, as with a single delete. One call, so the caller records one undo step.
    /// </summary>
    public static EditResult DeleteAll(IEnumerable<(TrackModel Track, ClipModel Clip)> sections)
    {
        int n = 0;
        foreach (var (track, clip) in sections)
            if (track.Clips.Remove(clip)) n++;

        return n switch
        {
            0 => EditResult.No("Nothing selected to delete."),
            1 => new EditResult(true, "Section deleted. The gap is left as it is."),
            _ => new EditResult(true, $"Deleted {n} sections. The gaps are left as they are."),
        };
    }

    // ---------------------------------------------------------------- several at once: copy, paste, repeat (2026-10-29)

    /// <summary>
    /// What Copy and Cut hold when several sections are selected (Daniel, 2026-10-27: "MUST be
    /// able to select multiple clips to copy/paste them"): the sections as they lie, in rows. The
    /// group starts where its earliest section starts on the timeline; that section's row measures
    /// from its exact frame, every other row from the same moment through its own map, so a seam
    /// or a butt join inside a row is still exactly that when the group is put down. A section
    /// whose song is missing is left out, as a marked stretch leaves it out.
    /// </summary>
    public static SectionGroup Copy(IList<TrackModel> tracks, IEnumerable<(TrackModel Track, ClipModel Clip)> sections, ProjectSession s)
    {
        var picked = sections.Where(x => tracks.Contains(x.Track) && TimelineEditor.LengthFramesOf(x.Clip, s) > 0).ToList();
        if (picked.Count == 0) return new SectionGroup(Array.Empty<SectionGroup.Row>());

        var (firstTrack, first) = picked.OrderBy(x => TimelineStart(x.Track, x.Clip, s)).First();
        double startSeconds = TimelineStart(firstTrack, first, s);
        int top = picked.Min(x => tracks.IndexOf(x.Track));

        var rows = new List<SectionGroup.Row>();
        foreach (var byTrack in picked.GroupBy(x => x.Track).OrderBy(g => tracks.IndexOf(g.Key)))
        {
            var track = byTrack.Key;
            long rowStart = ReferenceEquals(track, firstTrack)
                ? TimelineEditor.StartFrameOf(first, s)
                : TimelineEditor.TrackFrameAt(track, startSeconds, s);
            var clips = new List<ClipModel>();
            long length = 0;
            foreach (var (_, c) in byTrack.OrderBy(x => TimelineEditor.StartFrameOf(x.Clip, s)))
            {
                long offset = TimelineEditor.StartFrameOf(c, s) - rowStart;
                var copy = TimelineEditor.Copy(c);
                copy.TrackStartSeconds = TimelineEditor.SecondsOf(offset, s);
                clips.Add(copy);
                length = Math.Max(length, offset + TimelineEditor.LengthFramesOf(c, s));
            }
            rows.Add(new SectionGroup.Row(tracks.IndexOf(track) - top, track, clips, length));
        }
        return new SectionGroup(rows);
    }

    private static double TimelineStart(TrackModel track, ClipModel clip, ProjectSession s)
        => TimelineEditor.TimelineSecondsOfFrame(track, TimelineEditor.StartFrameOf(clip, s), s);

    /// <summary>
    /// Why a group cannot be pasted at all, or null. Rows that would fall off the bottom are a
    /// different matter — <see cref="MissingRows"/> — which the caller can put right by adding tracks.
    /// </summary>
    public static string? PasteProblem(IList<TrackModel> tracks, TrackModel onto, SectionGroup group, ProjectSession s)
    {
        if (group.Count == 0) return "There is nothing on the clipboard to paste.";
        if (!tracks.Contains(onto)) return "Click a track to paste onto.";
        if (group.Rows.SelectMany(r => r.Clips).Any(c => s.AudioFor(c) is null))
            return "A song that was copied is no longer in the project, so there is nothing to paste. Copy it again.";
        return null;
    }

    /// <summary>How many more tracks a paste of <paramref name="group"/> onto <paramref name="onto"/> needs below the last one.</summary>
    public static int MissingRows(IList<TrackModel> tracks, TrackModel onto, SectionGroup group)
        => Math.Max(0, tracks.IndexOf(onto) + group.RowSpan - tracks.Count);

    /// <summary>
    /// Puts a group down: its top row on <paramref name="onto"/> at <paramref name="timelineSeconds"/>,
    /// each further row on the track that many below. Every section overwrites what is under it,
    /// as a dropped section does; what lies under the gaps between them is left alone — a group is
    /// those sections, not the stretch they span. An empty track takes on the tempo and pitch of
    /// the track its row came off, so the sections play as they did. The caller records one undo
    /// step, and adds the tracks <see cref="MissingRows"/> asks for first.
    /// </summary>
    public static EditResult Paste(IList<TrackModel> tracks, TrackModel onto, SectionGroup group, double timelineSeconds, ProjectSession s)
    {
        if (PasteProblem(tracks, onto, group, s) is { } why) return EditResult.No(why);
        int missing = MissingRows(tracks, onto, group);
        if (missing > 0)
            return EditResult.No($"Those sections take {group.RowSpan} tracks from {onto.Name} down: {missing} more track{(missing == 1 ? " is" : "s are")} needed below it.");

        int top = tracks.IndexOf(onto);
        double at = Math.Max(0, timelineSeconds);
        var placed = new List<ClipModel>();
        var names = new List<string>();
        var notes = new List<string>();
        foreach (var row in group.Rows)
        {
            var target = tracks[top + row.Offset];
            var from = TimelineEditor.SettingsOf(row.From);
            bool adopted = TimelineEditor.Adopt(target, from);
            TimelineEditor.EnsureOriginAtOrBefore(target, at, s);
            long start = Math.Max(0, TimelineEditor.TrackFrameAt(target, at, s));
            placed.AddRange(PutDown(target, row, start, s));
            names.Add(target.Name);
            if (adopted)
                notes.Add($" {target.Name} takes that song's tempo{(TimelineEditor.CarriesTempoChanges(from) ? ", " + TimelineEditor.TempoChangesOf(from) : "")} and pitch, so they play as they did.");
        }
        string where = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return new EditResult(true, $"Pasted {group.Count} sections at {at:0.00}s on {where}.{string.Concat(notes)}", placed[0]) { Placed = placed };
    }

    /// <summary>One row of a group onto a track from <paramref name="start"/>, each section clearing only its own span.</summary>
    private static List<ClipModel> PutDown(TrackModel target, SectionGroup.Row row, long start, ProjectSession s)
    {
        var placed = new List<ClipModel>();
        foreach (var c in row.Clips)
        {
            long offset = TimelineEditor.StartFrameOf(c, s), length = TimelineEditor.LengthFramesOf(c, s);
            TimelineEditor.ClearRange(target, start + offset, start + offset + length, s);
            var p = c.Clone();
            p.Id = Guid.NewGuid();
            p.TrackStartSeconds = TimelineEditor.SecondsOf(start + offset, s);
            target.Clips.Add(p);
            placed.Add(p);
        }
        TimelineEditor.Normalise(target);
        return placed.OrderBy(p => TimelineEditor.StartFrameOf(p, s)).ToList();
    }

    /// <summary>
    /// Ctrl+D with several sections selected: a copy of the group straight after itself, on the
    /// same tracks. The copies start where the group ends — on the exact frame of the section that
    /// ends last, in its row, so a group that is a loop goes on being one; at that moment through
    /// the other rows' maps, and never before a row's own last section ends. The caller records one
    /// undo step.
    /// </summary>
    public static EditResult Repeat(IList<TrackModel> tracks, IEnumerable<(TrackModel Track, ClipModel Clip)> sections, ProjectSession s)
    {
        var picked = sections.Where(x => tracks.Contains(x.Track) && TimelineEditor.LengthFramesOf(x.Clip, s) > 0).ToList();
        var group = Copy(tracks, picked, s);
        if (group.Count == 0) return EditResult.No("Nothing selected to repeat.");

        var (lastTrack, last) = picked.OrderByDescending(x => TimelineEditor.TimelineSecondsOfFrame(x.Track, TimelineEditor.EndFrameOf(x.Clip, s), s)).First();
        double endSeconds = TimelineEditor.TimelineSecondsOfFrame(lastTrack, TimelineEditor.EndFrameOf(last, s), s);
        var placed = new List<ClipModel>();
        foreach (var row in group.Rows)
        {
            var target = row.From;
            long rowEnd = picked.Where(x => ReferenceEquals(x.Track, target)).Max(x => TimelineEditor.EndFrameOf(x.Clip, s));
            long start = ReferenceEquals(target, lastTrack)
                ? TimelineEditor.EndFrameOf(last, s)
                : Math.Max(rowEnd, TimelineEditor.TrackFrameAt(target, endSeconds, s));
            placed.AddRange(PutDown(target, row, start, s));
        }
        return new EditResult(true, $"Repeated {group.Count} sections at {endSeconds:0.00}s.", placed[0]) { Placed = placed };
    }
}

/// <summary>
/// What Copy and Cut hold for several selected sections (2026-10-29): one row per track they sit
/// on, top to bottom, each row's sections placed from the group's start in the frames of the
/// track they came off. Unlike a <see cref="Piece"/>, a group is exactly those sections: the gaps
/// between them are not part of it.
/// </summary>
public sealed record SectionGroup(IReadOnlyList<SectionGroup.Row> Rows)
{
    /// <summary>
    /// One track's share: how many rows below the group's top track it sits, the track as it was
    /// (an empty track takes its tempo and pitch), its sections from the group's start, and how far
    /// the last of them reaches.
    /// </summary>
    public sealed record Row(int Offset, TrackModel From, IReadOnlyList<ClipModel> Clips, long LengthFrames);

    public int Count => Rows.Sum(r => r.Clips.Count);

    /// <summary>How many tracks, from the one chosen down, a paste needs.</summary>
    public int RowSpan => Rows.Count == 0 ? 0 : Rows[^1].Offset + 1;

    /// <summary>From the group's start to its last end, in the frames of the row that reaches furthest.</summary>
    public long LengthFrames => Rows.Count == 0 ? 0 : Rows.Max(r => r.LengthFrames);
}
