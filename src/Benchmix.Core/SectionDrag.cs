using Benchmix.Core.Model;

namespace Benchmix.Core;

/// <summary>How a drag should snap, decided once per pointer move by the window.</summary>
/// <param name="Enabled">False while Ctrl is held: the drag goes exactly where the pointer does.</param>
/// <param name="PixelsPerSecond">Current zoom, so "close" means close on screen rather than in time.</param>
/// <remarks>
/// Nothing snaps to bar lines: Daniel asked for bar snapping to go on 2026-10-11 (docs/DECISIONS.md).
/// Away from every section edge a drag lands exactly where it is dropped.
/// </remarks>
public sealed record SnapSettings(bool Enabled, double PixelsPerSecond)
{
    /// <summary>How near, on screen, an edge has to come before it pulls.</summary>
    public const double RadiusPixels = 10;

    public static readonly SnapSettings Off = new(false, 1);

    public double RadiusSeconds => RadiusPixels / Math.Max(0.01, PixelsPerSecond);
}

/// <summary>One end of one section, somewhere a dragged edge can snap to.</summary>
public readonly record struct SnapEdge(double TimelineSeconds, TrackModel Track, ClipModel Clip, bool IsEnd);

/// <summary>
/// Where things snap to: section edges, on any track, because lining a vocal up against the end of
/// a beat on another deck is the everyday case. Nothing else — when no edge is close, a drag goes
/// exactly where it is dropped.
/// </summary>
public static class Snapping
{
    /// <summary>Every section edge in the project except those of <paramref name="except"/>.</summary>
    public static List<SnapEdge> EdgesOf(IEnumerable<TrackModel> tracks, ProjectSession s, ClipModel? except)
    {
        var edges = new List<SnapEdge>();
        foreach (var t in tracks)
            foreach (var c in t.Clips)
            {
                if (ReferenceEquals(c, except)) continue;
                long length = TimelineEditor.LengthFramesOf(c, s);
                if (length <= 0) continue;
                long start = TimelineEditor.StartFrameOf(c, s);
                edges.Add(new SnapEdge(TimelineEditor.TimelineSecondsOfFrame(t, start, s), t, c, false));
                edges.Add(new SnapEdge(TimelineEditor.TimelineSecondsOfFrame(t, start + length, s), t, c, true));
            }
        return edges;
    }

    /// <summary>Every section edge in the project except those of the sections in <paramref name="except"/>.</summary>
    public static List<SnapEdge> EdgesExcept(IEnumerable<TrackModel> tracks, ProjectSession s, IReadOnlySet<ClipModel> except)
    {
        var edges = new List<SnapEdge>();
        foreach (var t in tracks)
            foreach (var c in t.Clips)
            {
                if (except.Contains(c)) continue;
                long length = TimelineEditor.LengthFramesOf(c, s);
                if (length <= 0) continue;
                long start = TimelineEditor.StartFrameOf(c, s);
                edges.Add(new SnapEdge(TimelineEditor.TimelineSecondsOfFrame(t, start, s), t, c, false));
                edges.Add(new SnapEdge(TimelineEditor.TimelineSecondsOfFrame(t, start + length, s), t, c, true));
            }
        return edges;
    }

    /// <summary>
    /// The frame an edge falls on in <paramref name="track"/>'s own timebase. Exact — the very
    /// frame the engine will use — when the edge is on that same track, which is what makes two
    /// sections snapped together a true butt join rather than a near miss with a gap in it.
    /// </summary>
    public static long FrameOn(TrackModel track, SnapEdge edge, ProjectSession s)
        => ReferenceEquals(edge.Track, track)
            ? (edge.IsEnd ? TimelineEditor.EndFrameOf(edge.Clip, s) : TimelineEditor.StartFrameOf(edge.Clip, s))
            : TimelineEditor.TrackFrameAt(track, edge.TimelineSeconds, s);

    /// <summary>The nearest edge to <paramref name="seconds"/> within the snap radius, if any.</summary>
    public static SnapEdge? Nearest(IEnumerable<SnapEdge> edges, double seconds, SnapSettings snap)
    {
        if (!snap.Enabled) return null;
        SnapEdge? best = null;
        double bestDistance = snap.RadiusSeconds;
        foreach (var e in edges)
        {
            double d = Math.Abs(e.TimelineSeconds - seconds);
            if (d <= bestDistance) { bestDistance = d; best = e; }
        }
        return best;
    }
}

/// <summary>
/// Dragging a section: along its own track, or onto another one.
///
/// <para>
/// The section really moves while the pointer does, so it is heard and seen where it will land.
/// Only the drop resolves overlap — overwriting, as a paste does. Resolving it on every pointer
/// move would chew through every section the pointer happened to pass over on the way.
/// </para>
/// <para>
/// Moving onto an empty track gives that track the tempo and pitch the section was playing at, so
/// it sounds exactly as it did. Carried on past it, the empty track is put back as it was.
/// </para>
/// </summary>
public sealed class SectionMove
{
    private readonly ProjectSession _s;
    private readonly TimelineEditor.DeckSettings _playedAs;
    private readonly Dictionary<TrackModel, TimelineEditor.DeckSettings> _adopted = new(ReferenceEqualityComparer.Instance);
    private readonly List<SnapEdge> _edges;

    public SectionMove(ProjectSession session, TrackModel origin, ClipModel clip)
    {
        _s = session;
        Origin = origin;
        Current = origin;
        Clip = clip;
        _playedAs = TimelineEditor.SettingsOf(origin);
        _edges = Snapping.EdgesOf(session.Project.Tracks, session, clip);
        LengthFrames = TimelineEditor.LengthFramesOf(clip, session);
    }

    public TrackModel Origin { get; }
    public TrackModel Current { get; private set; }
    public ClipModel Clip { get; }
    public long LengthFrames { get; }

    /// <summary>What the last move snapped to, in words ("a section"); null when it was not snapped.</summary>
    public string? SnappedTo { get; private set; }

    /// <summary>Where on the timeline it snapped to a section edge, for drawing a guide line.</summary>
    public double? SnapLineSeconds { get; private set; }

    /// <summary>True when <see cref="Current"/> is an empty track that took this section's settings.</summary>
    public bool CurrentAdopted => _adopted.ContainsKey(Current);

    /// <summary>
    /// Puts the section on <paramref name="target"/> with its start at
    /// <paramref name="proposedStart"/> on the timeline, snapped as <paramref name="snap"/> says.
    /// </summary>
    public void DragTo(TrackModel target, double proposedStart, SnapSettings snap)
    {
        if (!ReferenceEquals(target, Current)) Hop(target);

        var track = Current;
        double length = TimelineEditor.SecondsOf(LengthFrames, _s) / Math.Max(0.01, track.Speed);
        double start = Math.Max(0, proposedStart);

        // Where the section ends if it starts at s, and where it must start to end at e. Under a
        // tempo ramp or a moved warp marker its timeline length depends on where it sits, so both
        // go through the map.
        bool ramped = TimelineEditor.TempoVaries(track);
        double lengthSeconds = TimelineEditor.SecondsOf(LengthFrames, _s);
        double EndFrom(double s) => ramped
            ? TimelineEditor.TimelineSecondsAt(track, TimelineEditor.TrackSecondsAt(track, s) + lengthSeconds)
            : s + length;
        double StartForEnd(double e) => ramped
            ? TimelineEditor.TimelineSecondsAt(track, TimelineEditor.TrackSecondsAt(track, e) - lengthSeconds)
            : e - length;

        SnappedTo = null;
        SnapLineSeconds = null;
        SnapEdge? edge = null;
        bool endOnEdge = false;

        if (snap.Enabled)
        {
            // Either end of the section may be the one that is close: dropping a vocal so it ends
            // where the beat starts is as common as starting where it stops.
            double best = snap.RadiusSeconds;
            foreach (var e in _edges)
            {
                double d = Math.Abs(start - e.TimelineSeconds);
                if (d <= best) { best = d; edge = e; endOnEdge = false; }
                // Its end on this edge only if the whole section then fits after 0:00. Otherwise the
                // start would land before the timeline begins and the only way to honour the snap
                // would be to push the track's other sections later, which is not what was asked.
                d = Math.Abs(EndFrom(start) - e.TimelineSeconds);
                if (d < best && StartForEnd(e.TimelineSeconds) >= 0) { best = d; edge = e; endOnEdge = true; }
            }

            if (edge is { } hit)
            {
                start = Math.Max(0, endOnEdge ? StartForEnd(hit.TimelineSeconds) : hit.TimelineSeconds);
                SnappedTo = "a section";
                SnapLineSeconds = hit.TimelineSeconds;
            }
        }

        TimelineEditor.EnsureOriginAtOrBefore(track, start, _s);
        long frame = TimelineEditor.TrackFrameAt(track, start, _s);

        // An edge on this same track is taken to the exact frame, not via its timeline position:
        // that is what makes the two sections a butt join the engine recognises, crossfaded,
        // instead of a one-frame gap with a declick either side of it.
        if (edge is { } exact && ReferenceEquals(exact.Track, track))
        {
            long at = Snapping.FrameOn(track, exact, _s);
            frame = endOnEdge ? at - LengthFrames : at;
        }
        if (frame < 0) frame = Math.Max(0, frame + TimelineEditor.ShiftOrigin(track, -frame, _s));

        Clip.TrackStartSeconds = TimelineEditor.SecondsOf(frame, _s);
        TimelineEditor.Normalise(track);
    }

    private void Hop(TrackModel target)
    {
        Current.Clips.Remove(Clip);
        if (_adopted.Remove(Current, out var before) && Current.Clips.Count == 0)
            TimelineEditor.Restore(Current, before);

        if (TimelineEditor.IsEmptyDeck(target))
        {
            var before2 = TimelineEditor.SettingsOf(target);
            if (TimelineEditor.Adopt(target, _playedAs)) _adopted[target] = before2;
        }

        target.Clips.Add(Clip);
        Current = target;
    }

    /// <summary>Drops the section where it is, overwriting what is under it.</summary>
    public EditResult Commit()
    {
        int covered = TimelineEditor.Settle(Current, Clip, _s);
        double at = TimelineEditor.TimelineSecondsAt(Current, Clip.TrackStartSeconds);

        string where = ReferenceEquals(Current, Origin)
            ? $"Section moved to {at:0.00}s"
            : $"Section moved to {Current.Name} at {at:0.00}s";
        string snapped = SnappedTo is null ? "" : $", snapped to {SnappedTo}";
        string over = covered > 0 ? " It replaced what was underneath." : "";

        string how = "";
        if (!ReferenceEquals(Current, Origin))
        {
            bool sameFeel = _playedAs.SameFeelAs(Current);
            how = CurrentAdopted ? (TimelineEditor.CarriesTempoChanges(_playedAs) ? $" The empty track took that track's tempo, {TimelineEditor.TempoChangesOf(_playedAs)} and pitch." : " The empty track took its tempo and pitch.")
                : sameFeel ? ""
                : " It now plays at this track's tempo and pitch.";
        }
        return new EditResult(true, $"{where}{snapped}.{over}{how}", Clip);
    }
}

/// <summary>
/// Dragging several selected sections at once (Daniel, 2026-10-11), by the one grabbed.
///
/// <para>
/// The rule is fixed when the drag starts: all on one track, they go sideways and onto another
/// track, together; spread over several, they go sideways only. On each track every section moves
/// by ONE whole number of the track's frames, so a seam or a butt join between two of them is still
/// exactly that — the seam rule. Each track turns the same move in time into its own frames. The
/// group stops at 0:00 as a whole and is never squeezed. It snaps as a unit, to other sections'
/// edges only, and on the drop overwrites what it lands on — never its own sections.
/// </para>
/// <para>
/// The picture is put back before every pointer move and the group placed afresh, as
/// <see cref="LoopDrag"/> does, so passing over an empty track, or dragging back from before a
/// track's origin, leaves no trace. Only the arrangement and an empty track's adopted settings are
/// put back: a knob turned during the drag keeps its new value.
/// </para>
/// </summary>
public sealed class GroupMove
{
    private sealed class Member
    {
        public required ClipModel Clip;
        public required TrackModel Home;
        public required long StartFrame;
        public required long Length;
    }

    // Warp markers move with the origin (ShiftOrigin), so they are part of the picture put back.
    private sealed record Before(double Offset, ClipModel[] Clips, double[] Starts, WarpMarker[] Warps);

    private readonly ProjectSession _s;
    private readonly List<Member> _members = new();
    private readonly Member _grabbed;
    private readonly HashSet<ClipModel> _memberSet = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TrackModel, Member> _refOnHome = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TrackModel, double> _refStartOnHome = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TrackModel, Before> _before = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TrackModel, TimelineEditor.DeckSettings> _adopted = new(ReferenceEqualityComparer.Instance);
    private readonly List<SnapEdge> _edges;
    private readonly TimelineEditor.DeckSettings? _playedAs;
    private readonly double _grabbedStart;

    public GroupMove(ProjectSession session, IEnumerable<(TrackModel Track, ClipModel Clip)> selected, ClipModel grabbed)
    {
        _s = session;
        foreach (var (t, c) in selected)
        {
            long length = TimelineEditor.LengthFramesOf(c, session);
            if (length <= 0 || !t.Clips.Contains(c) || !_memberSet.Add(c)) continue;    // a missing file is never moved
            _members.Add(new Member { Clip = c, Home = t, StartFrame = TimelineEditor.StartFrameOf(c, session), Length = length });
        }
        _grabbed = _members.FirstOrDefault(m => ReferenceEquals(m.Clip, grabbed))
                   ?? throw new ArgumentException("The grabbed section is not one of the group.", nameof(grabbed));

        var homes = _members.Select(m => m.Home).Distinct(ReferenceEqualityComparer.Instance).Cast<TrackModel>().ToList();
        CanChangeTrack = homes.Count == 1;
        Origin = CanChangeTrack ? homes[0] : null;
        Destination = Origin;
        if (Origin is not null) _playedAs = TimelineEditor.SettingsOf(Origin);

        foreach (var home in homes)
        {
            var reference = ReferenceEquals(_grabbed.Home, home)
                ? _grabbed
                : _members.Where(m => ReferenceEquals(m.Home, home)).MinBy(m => m.StartFrame)!;
            _refOnHome[home] = reference;
            _refStartOnHome[home] = TimelineEditor.TimelineSecondsOfFrame(home, reference.StartFrame, session);
            Remember(home);
        }
        _grabbedStart = _refStartOnHome[_grabbed.Home];
        _edges = Snapping.EdgesExcept(session.Project.Tracks, session, _memberSet);     // its own edges are never targets
    }

    /// <summary>True when every section is on one track, so the group may go onto another.</summary>
    public bool CanChangeTrack { get; }
    public TrackModel? Origin { get; }
    /// <summary>The track a one-track group is on now; null for a group across tracks.</summary>
    public TrackModel? Destination { get; private set; }
    public bool DestinationAdopted => Destination is not null && _adopted.ContainsKey(Destination);
    public int Count => _members.Count;
    public ClipModel Grabbed => _grabbed.Clip;
    public string? SnappedTo { get; private set; }
    public double? SnapLineSeconds { get; private set; }

    /// <summary>Every track this drag has changed, so the caller can redraw them.</summary>
    public IEnumerable<TrackModel> Touched => _before.Keys;

    /// <summary>
    /// Puts the group with the grabbed section's start at <paramref name="proposedGrabbedStart"/>
    /// on the timeline — on <paramref name="target"/> when it may change track (null: where it is).
    /// </summary>
    public void DragTo(TrackModel? target, double proposedGrabbedStart, SnapSettings snap)
    {
        Restore();
        if (CanChangeTrack)
        {
            Destination = target ?? Destination ?? Origin;
            if (!ReferenceEquals(Destination, Origin)) HopTo(Destination!);
        }

        double delta = proposedGrabbedStart - _grabbedStart;
        double lowest = -_members.Min(m => StartAt(m, 0));         // so the whole group stays after 0:00
        delta = Math.Max(delta, lowest);

        SnappedTo = null;
        SnapLineSeconds = null;
        (Member M, SnapEdge E, bool End)? hit = null;
        if (snap.Enabled)
        {
            double best = snap.RadiusSeconds, shift = 0;
            foreach (var m in _members)
            {
                double s0 = StartAt(m, delta), e0 = EndAt(m, delta);
                foreach (var e in _edges)
                {
                    double d = e.TimelineSeconds - s0;              // ties go to a start, as in SectionMove
                    if (Math.Abs(d) <= best && delta + d >= lowest) { best = Math.Abs(d); shift = d; hit = (m, e, false); }
                    d = e.TimelineSeconds - e0;
                    if (Math.Abs(d) < best && delta + d >= lowest) { best = Math.Abs(d); shift = d; hit = (m, e, true); }
                }
            }
            if (hit is { } h)
            {
                // The move is worked back from the very frame the snapped edge lands on. Under a
                // tempo ramp the group's sections do not all move by the same time — they move by
                // the same frames — so adding the gap to the grabbed section's start would leave a
                // member's edge short of the line it snapped to.
                var on = Dest(h.M);
                long at = Snapping.FrameOn(on, h.E, _s);
                long anchor = (h.End ? at - h.M.Length : at) - FromRef(h.M);
                delta = TimelineEditor.TimelineSecondsOfFrame(on, anchor, _s) - RefStart(on);
                SnappedTo = "a section";
                SnapLineSeconds = h.E.TimelineSeconds;
            }
        }
        Place(delta, hit);
    }

    private void Place(double delta, (Member M, SnapEdge E, bool End)? hit)
    {
        foreach (var dest in Destinations())
        {
            var here = _members.Where(m => ReferenceEquals(Dest(m), dest)).ToList();
            // Room before the track's origin is made by moving the origin back, never by squeezing.
            TimelineEditor.EnsureOriginAtOrBefore(dest, Math.Max(0, here.Min(m => StartAt(m, delta))), _s);
            long anchor = AnchorFrame(dest, delta);
            if (hit is { } h && ReferenceEquals(Dest(h.M), dest))
            {
                // The snapped edge is taken to its exact frame: on the same track a true butt join,
                // not a near miss; on another, the nearest frame to the line, ramp or not.
                long at = Snapping.FrameOn(dest, h.E, _s);
                anchor = (h.End ? at - h.M.Length : at) - FromRef(h.M);
            }
            long first = anchor + here.Min(FromRef);
            if (first < 0) { anchor += TimelineEditor.ShiftOrigin(dest, -first, _s); first = anchor + here.Min(FromRef); }
            if (first < 0) anchor -= first;                          // already at 0:00: on, together
            foreach (var m in here) m.Clip.TrackStartSeconds = TimelineEditor.SecondsOf(anchor + FromRef(m), _s);
            TimelineEditor.Normalise(dest);
        }
    }

    /// <summary>Drops the group where it is, overwriting what is under it but never one of its own.</summary>
    public EditResult Commit()
    {
        int covered = 0;
        var dests = Destinations();
        foreach (var dest in dests)
        {
            foreach (var m in _members.Where(m => ReferenceEquals(Dest(m), dest)))
            {
                long start = TimelineEditor.StartFrameOf(m.Clip, _s);
                covered += TimelineEditor.ClearRange(dest, start, start + m.Length, _s, exceptAll: _memberSet);
            }
            TimelineEditor.Normalise(dest);
        }

        int n = _members.Count;
        string what;
        if (CanChangeTrack && !ReferenceEquals(Destination, Origin))
            what = $"{n} sections moved to {Destination!.Name}";
        else
        {
            double now = TimelineEditor.TimelineSecondsOfFrame(_grabbed.Home, TimelineEditor.StartFrameOf(_grabbed.Clip, _s), _s);
            double d = now - _grabbedStart;
            string tracks = dests.Count > 1 ? $", on {dests.Count} tracks" : "";
            what = Math.Abs(d) < 0.0005 ? $"{n} sections left where they were"
                 : $"{n} sections moved {Math.Abs(d):0.00}s {(d > 0 ? "later" : "earlier")}{tracks}";
        }
        string snapped = SnappedTo is null ? "" : $", snapped to {SnappedTo}";
        string over = covered > 0 ? " They replaced what was underneath." : "";
        string how = "";
        if (CanChangeTrack && !ReferenceEquals(Destination, Origin) && _playedAs is not null)
        {
            var dest = Destination!;
            bool sameFeel = _playedAs.SameFeelAs(dest);
            how = DestinationAdopted ? (TimelineEditor.CarriesTempoChanges(_playedAs) ? $" The empty track took that track's tempo, {TimelineEditor.TempoChangesOf(_playedAs)} and pitch." : " The empty track took their tempo and pitch.")
                : sameFeel ? "" : " They now play at this track's tempo and pitch.";
        }
        return new EditResult(true, $"{what}{snapped}.{over}{how}", _grabbed.Clip);
    }

    // ---- the only timeline <-> track conversions here, all through TimelineEditor ----

    private long AnchorFrame(TrackModel dest, double delta) => TimelineEditor.TrackFrameAt(dest, RefStart(dest) + delta, _s);

    private double StartAt(Member m, double delta)
        => TimelineEditor.TimelineSecondsOfFrame(Dest(m), AnchorFrame(Dest(m), delta) + FromRef(m), _s);

    private double EndAt(Member m, double delta)
        => TimelineEditor.TimelineSecondsOfFrame(Dest(m), AnchorFrame(Dest(m), delta) + FromRef(m) + m.Length, _s);

    private TrackModel Dest(Member m) => CanChangeTrack ? Destination! : m.Home;
    private Member Ref(TrackModel dest) => CanChangeTrack ? _grabbed : _refOnHome[dest];
    private double RefStart(TrackModel dest) => CanChangeTrack ? _grabbedStart : _refStartOnHome[dest];

    /// <summary>Where a section sits relative to its track's reference section, in track frames — kept exactly.</summary>
    private long FromRef(Member m) => m.StartFrame - Ref(Dest(m)).StartFrame;

    private List<TrackModel> Destinations()
        => _members.Select(Dest).Distinct(ReferenceEqualityComparer.Instance).Cast<TrackModel>().ToList();

    private void HopTo(TrackModel dest)
    {
        Remember(dest);
        if (TimelineEditor.IsEmptyDeck(dest))
        {
            var before = TimelineEditor.SettingsOf(dest);
            if (TimelineEditor.Adopt(dest, _playedAs!)) _adopted[dest] = before;   // before anything lands on it
        }
        foreach (var m in _members)
        {
            m.Home.Clips.Remove(m.Clip);
            dest.Clips.Add(m.Clip);
        }
    }

    private void Remember(TrackModel t)
    {
        if (_before.ContainsKey(t)) return;
        _before[t] = new Before(t.TimelineOffsetSeconds, t.Clips.ToArray(), t.Clips.Select(c => c.TrackStartSeconds).ToArray(), t.Warps.ToArray());
    }

    private void Restore()
    {
        foreach (var (t, b) in _before)
        {
            t.Clips.Clear();
            t.Clips.AddRange(b.Clips);
            for (int i = 0; i < b.Clips.Length; i++) b.Clips[i].TrackStartSeconds = b.Starts[i];
            t.TimelineOffsetSeconds = b.Offset;
            t.Warps = b.Warps.ToList();
        }
        foreach (var (t, deck) in _adopted) TimelineEditor.Restore(t, deck);
        _adopted.Clear();
    }
}

/// <summary>
/// Dragging one edge of a section to trim it. Snaps to other sections' edges, and stops at its
/// neighbours on the same track rather than eating into them — trimming is about this section, so
/// it never changes another one. (A move or a paste does overwrite; that is putting something new
/// somewhere, not adjusting what is there.)
/// </summary>
public sealed class SectionTrim
{
    /// <summary>Shortest a trim will leave a section.</summary>
    public const double MinimumSeconds = 0.05;

    private readonly ProjectSession _s;
    private readonly TrackModel _track;
    private readonly ClipModel _clip;
    private readonly bool _start;
    private readonly List<SnapEdge> _edges;

    // The shortest this trim may leave the section: MinimumSeconds, or the section's own length if
    // it is already shorter (a split can leave 20 ms). Such a section can grow but not shrink —
    // and nothing is forced on it: holding it to the full minimum pushed its edge out into its
    // neighbour, or before its file began, the moment it was touched.
    private readonly long _minimum;

    public SectionTrim(ProjectSession session, TrackModel track, ClipModel clip, bool start)
    {
        _s = session;
        _track = track;
        _clip = clip;
        _start = start;
        _edges = Snapping.EdgesOf(session.Project.Tracks, session, clip);
        _minimum = Math.Min(session.SecondsToFrames(MinimumSeconds), TimelineEditor.LengthFramesOf(clip, session));
    }

    public string? SnappedTo { get; private set; }
    public double? SnapLineSeconds { get; private set; }

    /// <summary>
    /// "Clear trim": uncovers as much of the section's song as dragging each end outward could —
    /// back to the start of the file or the section before, on to the end of the file or the
    /// section after. The audio stays where it is in time.
    ///
    /// <para>
    /// Until 2026-10-03 it reset the section's source range in place instead. That slid the audio
    /// to start the song at the section's left edge, and ran the section on over every section
    /// after it: two sections sounding at once on one track, which is not representable.
    /// </para>
    /// </summary>
    public static void Clear(ProjectSession session, TrackModel track, ClipModel clip)
    {
        new SectionTrim(session, track, clip, start: true).DragTo(0, SnapSettings.Off);

        long fileFrames = session.AudioFor(clip)?.FrameCount ?? 0;
        double beyond = TimelineEditor.TimelineSecondsAt(track,
            clip.TrackStartSeconds + TimelineEditor.SecondsOf(fileFrames, session)) + 1;
        new SectionTrim(session, track, clip, start: false).DragTo(beyond, SnapSettings.Off);
    }

    public void DragTo(double proposedEdge, SnapSettings snap)
    {
        double at = Math.Max(0, proposedEdge);
        var hit = Snapping.Nearest(_edges, at, snap);
        SnapLineSeconds = hit?.TimelineSeconds;
        SnappedTo = hit is null ? null : "a section";
        if (hit is { } h) at = h.TimelineSeconds;

        long minimum = _minimum;
        var (prevEnd, nextStart) = Neighbours();

        if (_start)
        {
            TimelineEditor.EnsureOriginAtOrBefore(_track, at, _s);
            (prevEnd, nextStart) = Neighbours();

            // Where source frame zero would sit if the section reached back that far: the trim can
            // uncover audio back to there and no further.
            long zero = TimelineEditor.StartFrameOf(_clip, _s) - TimelineEditor.SourceStartFrameOf(_clip, _s);
            long end = TimelineEditor.EndFrameOf(_clip, _s);
            long frame = hit is { } e ? Snapping.FrameOn(_track, e, _s) : TimelineEditor.TrackFrameAt(_track, at, _s);

            // The section before, and the start of the file, win over the minimum length — the
            // mirror of the end trim. The other way round, a section already shorter than the
            // minimum was pushed into its neighbour, or read from before its file began.
            long lowest = Math.Max(Math.Max(zero, prevEnd), 0);
            long highest = end - minimum;
            frame = Math.Clamp(frame, lowest, Math.Max(lowest, highest));

            _clip.TrackStartSeconds = TimelineEditor.SecondsOf(frame, _s);
            _clip.SourceStartSeconds = TimelineEditor.SecondsOf(frame - zero, _s);
        }
        else
        {
            long start = TimelineEditor.StartFrameOf(_clip, _s);
            long source = TimelineEditor.SourceStartFrameOf(_clip, _s);
            long count = _s.AudioFor(_clip)?.FrameCount ?? 0;
            long frame = hit is { } e ? Snapping.FrameOn(_track, e, _s) : TimelineEditor.TrackFrameAt(_track, at, _s);

            // The next section wins over the minimum length: a section already shorter than the
            // minimum, butted against its neighbour, must not be pushed into it.
            long lowest = start + minimum;
            long highest = Math.Min(start + (count - source), nextStart);
            frame = Math.Clamp(frame, Math.Min(lowest, highest), highest);

            _clip.SourceEndSeconds = TimelineEditor.SecondsOf(source + (frame - start), _s);
        }
        TimelineEditor.Normalise(_track);
    }

    /// <summary>End of the section before this one and start of the one after, in track frames.</summary>
    private (long PrevEnd, long NextStart) Neighbours()
    {
        long myStart = TimelineEditor.StartFrameOf(_clip, _s);
        long prevEnd = long.MinValue, nextStart = long.MaxValue;
        foreach (var c in _track.Clips)
        {
            if (ReferenceEquals(c, _clip) || TimelineEditor.LengthFramesOf(c, _s) <= 0) continue;
            long s = TimelineEditor.StartFrameOf(c, _s);
            if (s < myStart) prevEnd = Math.Max(prevEnd, TimelineEditor.EndFrameOf(c, _s));
            else nextStart = Math.Min(nextStart, s);
        }
        return (prevEnd, nextStart);
    }
}

/// <summary>
/// Loop mode: dragging a section's edge repeats the section instead of trimming it.
///
/// <para>
/// The result is ordinary sections — exactly what holding Ctrl+D builds, so a loop made this way
/// is the hand-built kind, not the transport's loop region. Each repeat starts on the exact frame
/// the one before it ends, so every join is a butt join the engine crossfades.
/// </para>
/// <para>
/// A run of repeats is treated as one loop. Grab the end of the last repeat and drag it back, and
/// the whole loop shrinks — it does not start a second loop of the last repeat. Anything else on
/// the track that the repeats run over is overwritten, as with Ctrl+D, but only when the drag ends
/// there: pulling the edge back during the same drag gives it back.
/// </para>
/// <para>
/// By default the edge moves in whole repeats. Holding Ctrl frees it, and the last repeat is cut
/// part-way — at its tail on the right, at its head on the left, so the loop stays in phase with
/// the section it came from.
/// </para>
/// </summary>
public sealed class LoopDrag
{
    /// <summary>A ceiling, so a wild drag at low zoom cannot build tens of thousands of sections.</summary>
    public const int MaxRepeats = 999;

    private readonly ProjectSession _s;
    private readonly TrackModel _track;
    private readonly bool _left;
    private readonly List<ClipModel> _before;
    private readonly double _offsetBefore;
    private readonly List<WarpMarker> _warpsBefore;
    private readonly Guid _baseId;

    // The fade on the loop's outer end on the side being dragged. That end is rebuilt on every
    // move, and a repeat copies the section's own fades — which inside an existing loop are
    // zero — so without this, grabbing a faded loop again silently dropped its fade.
    private readonly double _outerFade;

    public LoopDrag(ProjectSession session, TrackModel track, ClipModel grabbed, bool left)
    {
        _s = session;
        _track = track;
        _left = left;

        var run = LoopRun.Of(track, grabbed, session);
        var loopBase = left ? run.Full[^1] : run.Full[0];
        _baseId = loopBase.Id;
        _outerFade = left ? run.All[0].FadeInSeconds : run.All[^1].FadeOutSeconds;

        // The loop's existing repeats on the side being dragged are what the drag redefines, so
        // they leave the "before" picture. Everything else on the track is kept and put back each
        // time the edge moves, which is what lets a drag give back what it passed over.
        var redefined = new HashSet<ClipModel>(ReferenceEqualityComparer.Instance);
        foreach (var c in run.All)
        {
            long cs = TimelineEditor.StartFrameOf(c, session);
            long bs = TimelineEditor.StartFrameOf(loopBase, session);
            if (left ? cs < bs : cs > bs) redefined.Add(c);
        }
        _before = track.Clips.Where(c => !redefined.Contains(c)).Select(c => c.Clone()).ToList();
        _offsetBefore = track.TimelineOffsetSeconds;
        _warpsBefore = track.Warps.ToList();             // they move with the origin, so they go back with it
    }

    /// <summary>The section being repeated, as it now is on the track.</summary>
    public ClipModel Base => _track.Clips.First(c => c.Id == _baseId);

    /// <summary>Whole copies laid beside the section, not counting the section itself.</summary>
    public int Repeats { get; private set; }

    /// <summary>True when the outermost copy is only part of the section.</summary>
    public bool Partial { get; private set; }

    /// <summary>
    /// Moves the dragged edge to <paramref name="proposedEdge"/> on the timeline. With
    /// <paramref name="wholeRepeats"/> it rounds to the nearest whole repeat; without, it goes
    /// exactly there and the outermost copy is cut part-way.
    /// </summary>
    public EditResult DragTo(double proposedEdge, bool wholeRepeats)
    {
        Restore();
        var b = Base;
        long length = TimelineEditor.LengthFramesOf(b, _s);
        long minimum = _s.SecondsToFrames(TimelineEditor.MinimumSectionSeconds);
        if (length < minimum) return EditResult.No("That section is too short to loop.");

        return _left ? DragLeft(b, length, minimum, proposedEdge, wholeRepeats)
                     : DragRight(b, length, minimum, proposedEdge, wholeRepeats);
    }

    private EditResult DragRight(ClipModel b, long length, long minimum, double proposedEdge, bool whole)
    {
        long start = TimelineEditor.StartFrameOf(b, _s);
        long edge = TimelineEditor.TrackFrameAt(_track, Math.Max(0, proposedEdge), _s);
        long span = Math.Max(length, edge - start);

        long copies, part;
        if (whole) { copies = Math.Max(0, (long)Math.Round(span / (double)length) - 1); part = 0; }
        else { copies = span / length - 1; part = span % length; }
        if (part < minimum) part = 0;
        if (copies >= MaxRepeats) { copies = MaxRepeats; part = 0; }

        long end = start + (copies + 1) * length + part;
        TimelineEditor.ClearRange(_track, start + length, end, _s, except: b);

        var pieces = new List<ClipModel>();
        for (long k = 1; k <= copies; k++) pieces.Add(Repeat(b, start + k * length));
        if (part > 0)
        {
            var tail = Repeat(b, start + (copies + 1) * length);
            tail.SourceEndSeconds = TimelineEditor.SecondsOf(TimelineEditor.SourceStartFrameOf(b, _s) + part, _s);
            pieces.Add(tail);
        }

        // The loop fades out where it now ends, not after the first pass.
        if (pieces.Count > 0) { pieces[^1].FadeOutSeconds = _outerFade; b.FadeOutSeconds = 0; }
        else b.FadeOutSeconds = _outerFade;
        return Finish(b, pieces, copies, part > 0, length);
    }

    private EditResult DragLeft(ClipModel b, long length, long minimum, double proposedEdge, bool whole)
    {
        long start = TimelineEditor.StartFrameOf(b, _s);
        long edge = TimelineEditor.TrackFrameAt(_track, Math.Max(0, proposedEdge), _s);
        // Nothing goes before 0:00: the room to the left is the track's own frames down to its
        // origin plus however far the origin can still move back — the same figure ShiftOrigin
        // works to, so the two can never disagree by a rounding.
        long room = Math.Max(0, start + TimelineEditor.OriginRoomFrames(_track, _s));
        long span = Math.Clamp(start - edge, 0, room);

        long copies, part;
        if (whole)
        {
            copies = (long)Math.Round(span / (double)length);
            if (copies * length > room) copies = room / length;
            part = 0;
        }
        else { copies = span / length; part = span % length; }
        if (part < minimum) part = 0;
        if (copies >= MaxRepeats) { copies = MaxRepeats; part = 0; }

        // Room before the track's own origin is made by moving the origin, not by stopping there.
        long leftmost = start - copies * length - part;
        if (leftmost < 0)
        {
            TimelineEditor.ShiftOrigin(_track, -leftmost, _s);
            start = TimelineEditor.StartFrameOf(b, _s);
            leftmost = start - copies * length - part;
        }
        if (leftmost < 0) return EditResult.No("There is no room before 0:00 for that loop.");

        TimelineEditor.ClearRange(_track, leftmost, start, _s, except: b);

        var pieces = new List<ClipModel>();
        for (long k = 1; k <= copies; k++) pieces.Add(Repeat(b, start - k * length));
        if (part > 0)
        {
            // The head of the leftmost copy is cut, not its tail, so the audio at every point is
            // what the loop would be playing there: still in phase with the section itself.
            var head = Repeat(b, leftmost);
            head.SourceStartSeconds = TimelineEditor.SecondsOf(
                TimelineEditor.SourceStartFrameOf(b, _s) + (length - part), _s);
            pieces.Add(head);
        }

        if (pieces.Count > 0) { pieces[^1].FadeInSeconds = _outerFade; b.FadeInSeconds = 0; }
        else b.FadeInSeconds = _outerFade;
        return Finish(b, pieces, copies, part > 0, length);
    }

    private EditResult Finish(ClipModel b, List<ClipModel> pieces, long copies, bool partial, long length)
    {
        _track.Clips.AddRange(pieces);
        TimelineEditor.Normalise(_track);
        Repeats = (int)copies;
        Partial = partial;

        double each = TimelineEditor.TimelineLength(_track, b.TrackStartSeconds, TimelineEditor.SecondsOf(length, _s));
        int plays = (int)copies + 1;
        string about = TimelineEditor.TempoVaries(_track) ? "about " : "";   // under a ramp or a warp each repeat takes its own time
        string message = copies == 0 && !partial
            ? "Loop: just the section itself — drag further to repeat it."
            : $"Loop: {plays} × {about}{each:0.00}s{(partial ? " and a part" : "")}.";
        return new EditResult(true, message, b);
    }

    private ClipModel Repeat(ClipModel b, long trackFrame)
    {
        var copy = TimelineEditor.Copy(b);
        copy.TrackStartSeconds = TimelineEditor.SecondsOf(trackFrame, _s);
        copy.FadeInSeconds = 0;
        copy.FadeOutSeconds = 0;
        return copy;
    }

    private void Restore()
    {
        _track.Clips.Clear();
        _track.Clips.AddRange(_before.Select(c => c.Clone()));
        _track.TimelineOffsetSeconds = _offsetBefore;
        _track.Warps = _warpsBefore.ToList();
    }
}

/// <summary>
/// A run of back-to-back repeats of one section, found from any one of them: whole copies, plus
/// at most one part-copy at either end. This is how a loop built earlier is recognised as a loop
/// rather than as a row of unrelated sections.
/// </summary>
public sealed class LoopRun
{
    private LoopRun(List<ClipModel> all, List<ClipModel> full)
    {
        All = all;
        Full = full;
    }

    /// <summary>Every section in the run, in order.</summary>
    public List<ClipModel> All { get; }

    /// <summary>The whole copies in the run, in order. Never empty.</summary>
    public List<ClipModel> Full { get; }

    /// <summary>
    /// True when <paramref name="clip"/>'s left (or right) edge is an outer edge of the whole run.
    /// Only those are loop handles: the edge between two repeats is inside the loop, and treating
    /// it as the loop's end would collapse every repeat beyond it the moment the drag began.
    /// </summary>
    public bool IsOuterEdge(ClipModel clip, bool left)
        => ReferenceEquals(left ? All[0] : All[^1], clip);

    private readonly record struct Span(string Path, long From, long To);

    private static bool IsRightPart(Span p, Span unit) => p.Path == unit.Path && p.From == unit.From && p.To < unit.To;
    private static bool IsLeftPart(Span p, Span unit) => p.Path == unit.Path && p.To == unit.To && p.From > unit.From;

    public static LoopRun Of(TrackModel track, ClipModel grabbed, ProjectSession s)
    {
        var ordered = track.Clips.OrderBy(c => TimelineEditor.StartFrameOf(c, s)).ToList();
        int i = ordered.IndexOf(grabbed);
        if (i < 0) return new LoopRun(new() { grabbed }, new() { grabbed });

        Span SpanOf(int k) => new(ordered[k].SourcePath.ToUpperInvariant(),
                                  TimelineEditor.SourceStartFrameOf(ordered[k], s),
                                  TimelineEditor.SourceEndFrameOf(ordered[k], s));
        bool Abuts(int a, int b) => a >= 0 && b < ordered.Count
                                  && TimelineEditor.EndFrameOf(ordered[a], s) == TimelineEditor.StartFrameOf(ordered[b], s);

        // Which span is the loop's unit: the grabbed section's own, unless it is itself only part
        // of the whole copy beside it.
        var mine = SpanOf(i);
        var unit = mine;
        bool twin = (Abuts(i - 1, i) && SpanOf(i - 1) == mine) || (Abuts(i, i + 1) && SpanOf(i + 1) == mine);
        if (!twin)
        {
            if (Abuts(i - 1, i) && IsRightPart(mine, SpanOf(i - 1))) unit = SpanOf(i - 1);
            else if (Abuts(i, i + 1) && IsLeftPart(mine, SpanOf(i + 1))) unit = SpanOf(i + 1);
        }

        // Walk left over whole copies, taking at most one part-copy at the far end. A right-hand
        // part-copy starts the walk from the whole copy it belongs to.
        int first = i;
        if (mine != unit && IsRightPart(mine, unit)) first = i - 1;
        while (SpanOf(first) == unit && Abuts(first - 1, first))
        {
            var prev = SpanOf(first - 1);
            if (prev == unit) { first--; continue; }
            if (IsLeftPart(prev, unit)) first--;
            break;
        }

        // And right, the mirror image.
        int last = i;
        if (mine != unit && IsLeftPart(mine, unit)) last = i + 1;
        while (SpanOf(last) == unit && Abuts(last, last + 1))
        {
            var next = SpanOf(last + 1);
            if (next == unit) { last++; continue; }
            if (IsRightPart(next, unit)) last++;
            break;
        }

        var all = ordered.GetRange(first, last - first + 1);
        var full = new List<ClipModel>();
        for (int k = first; k <= last; k++) if (SpanOf(k) == unit) full.Add(ordered[k]);
        if (full.Count == 0) full.Add(grabbed);
        return new LoopRun(all, full);
    }
}
