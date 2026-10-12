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
}
