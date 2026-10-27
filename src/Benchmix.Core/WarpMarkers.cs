using Benchmix.Core.Model;
using Benchmix.Engine.Audio;

namespace Benchmix.Core;

/// <summary>
/// A track's warp markers (Daniel, 2026-10-18): each pins a point of its audio — a beat — to the
/// moment it should be heard, and the audio between two markers is spread evenly to fit. See
/// <see cref="TrackModel.Warps"/> and <see cref="WarpMarker"/>.
///
/// <para>
/// His words: "you basically match the bpm as closely as possible for starters, then you flag the
/// beats that start to drift apart, and then you click warp to match them up". So a marker is put
/// down first on a beat that is still in time, where it changes nothing; a beat that has drifted is
/// then dragged onto where it belongs. Nothing before the marker on its left moves. After the last
/// marker the track plays at its tempo again, so everything after it moves along with it.
/// </para>
/// </summary>
public static class WarpMarkers
{
    /// <summary>The closest two markers can be, or the first to the track's start, in the track's own seconds.</summary>
    public const double MinimumSeconds = 0.1;

    /// <summary>
    /// Makes a track's markers playable, as loading a project does: on whole frames, sorted, at
    /// least <see cref="MinimumSeconds"/> apart and after the track's start, each after the one
    /// before in the tempo too, and spreading the audio no further than the engine allows. Anything
    /// else is dropped — a hand-edited or damaged file must neither crash the app nor warp a track
    /// in a way nobody set up. An unmoved marker stays exactly unmoved.
    /// </summary>
    public static void Normalise(TrackModel t, int sampleRate)
    {
        double OnFrame(double seconds) => Math.Round(seconds * sampleRate) / sampleRate;
        var kept = new List<WarpMarker>();
        double a = 0, tempo = 0;
        var playable = t.Warps
            .Where(w => w is not null && double.IsFinite(w.TrackSeconds) && double.IsFinite(w.TempoSeconds))
            .Select(w => w.TrackSeconds == w.TempoSeconds
                ? new WarpMarker(OnFrame(w.TrackSeconds), OnFrame(w.TrackSeconds))
                : w with { TrackSeconds = OnFrame(w.TrackSeconds) })
            .OrderBy(w => w.TrackSeconds);
        foreach (var w in playable)
        {
            if (w.TrackSeconds - a < MinimumSeconds || !(w.TempoSeconds > tempo)) continue;
            double spread = (w.TrackSeconds - a) / (w.TempoSeconds - tempo);
            if (!(spread >= TempoMap.LowestWarp && spread <= TempoMap.HighestWarp)) continue;
            kept.Add(w);
            (a, tempo) = (w.TrackSeconds, w.TempoSeconds);
        }
        t.Warps = kept;
    }

    /// <summary>The markers as the tempo map takes them, in seconds from the track's origin.</summary>
    public static IEnumerable<TempoMap.Pin> PinsOf(TrackModel t) => t.Warps.Select(w => new TempoMap.Pin(w.TrackSeconds, w.TempoSeconds));

    /// <summary>Whether any of the track's markers has been moved — whether it is warped at all.</summary>
    public static bool AnyMoved(TrackModel t) => t.Warps.Any(w => w.TrackSeconds != w.TempoSeconds);

    // ---------------------------------------------------------------- lookups

    /// <summary>Where marker <paramref name="i"/>'s beat is heard, on the timeline.</summary>
    public static double TimelineSecondsOf(TrackModel t, int i) => TimelineEditor.TimelineSecondsAt(t, t.Warps[i].TrackSeconds);

    /// <summary>The marker nearest <paramref name="timelineSeconds"/> within <paramref name="slopSeconds"/>, or −1.</summary>
    public static int IndexNear(TrackModel t, double timelineSeconds, double slopSeconds)
    {
        int best = -1;
        double distance = slopSeconds;
        for (int i = 0; i < t.Warps.Count; i++)
        {
            double d = Math.Abs(TimelineSecondsOf(t, i) - timelineSeconds);
            if (d <= distance) { distance = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// How fast the audio leading up to marker <paramref name="i"/> plays against its tempo — from the
    /// marker before it, or the track's start: above 1 faster, below 1 slower, 1 untouched.
    /// </summary>
    public static double SpreadBefore(TrackModel t, int i)
    {
        double a0 = i == 0 ? 0 : t.Warps[i - 1].TrackSeconds, t0 = i == 0 ? 0 : t.Warps[i - 1].TempoSeconds;
        return (t.Warps[i].TrackSeconds - a0) / (t.Warps[i].TempoSeconds - t0);
    }

    /// <summary>"0.8% faster", "1.2% slower", or "at its tempo", for a status message.</summary>
    public static string Describe(double spread)
        => Math.Abs(spread - 1) < 0.0005 ? "at its tempo" : $"{Math.Abs(spread - 1) * 100:0.0}% {(spread > 1 ? "faster" : "slower")}";

    /// <summary>"+0.8%" or "−1.2%" for a lane label; null where the audio is not spread enough to say.</summary>
    public static string? Label(double spread)
        => Math.Abs(spread - 1) < 0.0005 ? null : $"{(spread > 1 ? "+" : "−")}{Math.Abs(spread - 1) * 100:0.0}%";

    /// <summary>The track's tempo map without its warp, in seconds: the knob and the ramps.</summary>
    internal static TempoMap TempoOf(TrackModel t)
        => TimelineEditor.TempoSecondsMapOf(t) ?? TempoMap.Lenient(t.Speed, Array.Empty<TempoMap.Ramp>());

    // ---------------------------------------------------------------- edits

    /// <summary>
    /// Puts a marker on the track at <paramref name="timelineSeconds"/>, on the frame of its audio
    /// heard there, where it changes nothing: its tempo is where the warp already has that audio.
    /// Refused before the track's start and on top of another marker.
    /// </summary>
    public static EditResult Add(TrackModel t, double timelineSeconds, ProjectSession s, out int index)
    {
        index = -1;
        if (!double.IsFinite(timelineSeconds)) return EditResult.No("That is not a place on the track.");
        // A marker pins a beat of the track's audio. On a track with nothing on it there is no beat,
        // and the next song put there — pasted, dropped or dragged — would be warped by a marker
        // nobody set on it, or silently lose it (review, 2026-10-18).
        if (t.Clips.Count == 0) return EditResult.No($"A warp marker goes on a beat of a song; {t.Name} has nothing on it yet.");
        double at = TimelineEditor.SecondsOf(TimelineEditor.TrackFrameAt(t, timelineSeconds, s), s);
        if (at < MinimumSeconds) return EditResult.No("A warp marker goes on the track's audio, after where the track starts.");
        if (t.Warps.Any(w => Math.Abs(w.TrackSeconds - at) < MinimumSeconds))
            return EditResult.No("There is already a warp marker there. Drag it to move its beat, or right-click it to remove it.");

        var pins = PinsOf(t).ToArray();
        var marker = new WarpMarker(at, TempoMap.TempoOf(pins, at));
        int i = t.Warps.FindIndex(w => w.TrackSeconds > at);
        if (i < 0) i = t.Warps.Count;
        var after = t.Warps.ToList();
        after.Insert(i, marker);
        if (!TempoMap.TryWarped(TempoOf(t), after.Select(w => new TempoMap.Pin(w.TrackSeconds, w.TempoSeconds)).ToArray(), out _))
            return EditResult.No("A warp marker cannot go there.");

        t.Warps = after;
        index = i;
        return new EditResult(true, $"{t.Name}: warp marker at {TimelineSecondsOf(t, i):0.000}s. Drag it to move the beat there; the markers either side stay where they are.");
    }

    /// <summary>
    /// Takes a marker away. The markers either side of it stay where they are and the audio between
    /// them is spread evenly again; without a marker after it, everything after goes back to its tempo.
    /// </summary>
    public static EditResult Remove(TrackModel t, int index, ProjectSession s)
    {
        if (index < 0 || index >= t.Warps.Count) return EditResult.No("That warp marker is no longer there.");
        // Only the last marker moves what comes after it; without one after, everything later goes
        // back. With one after, the audio from there on stays exactly where it was.
        bool last = index == t.Warps.Count - 1;
        var later = last ? TempoRamps.FirstSectionFrom(t, TimelineSecondsOf(t, index), s) : null;
        double before = later is null ? 0 : When(t, later, s);
        t.Warps.RemoveAt(index);
        string around = last
            ? "the audio before it is spread evenly again, and after it plays at its tempo."
            : "the audio between the markers either side of it is spread evenly again.";
        return new EditResult(true, $"{t.Name}: warp marker removed; {around} Ctrl+Z puts it back." + LaterClause(t, later, before, s));
    }

    /// <summary>
    /// The track's origin moved back by <paramref name="frames"/> and every section moved forward by
    /// as many of its own frames (<see cref="TimelineEditor.ShiftOrigin"/>): the markers are on the
    /// same audio, so they move with it. Before the first marker the audio is its tempo times the
    /// first stretch's spread, so the tempo of every marker moves by the frames over that spread —
    /// and every section is heard exactly where it was. Unmoved markers stay exactly unmoved.
    /// </summary>
    internal static void ShiftWithOrigin(TrackModel t, long frames, ProjectSession s)
    {
        if (t.Warps.Count == 0 || frames == 0) return;
        double first = SpreadBefore(t, 0);
        double shift = TimelineEditor.SecondsOf(frames, s);
        t.Warps = t.Warps.Select(w =>
        {
            double track = TimelineEditor.SecondsOf(s.SecondsToFrames(w.TrackSeconds) + frames, s);
            return new WarpMarker(track, w.TrackSeconds == w.TempoSeconds && first == 1 ? track : w.TempoSeconds + shift / first);
        }).ToList();
    }

    private static double When(TrackModel t, ClipModel c, ProjectSession s)
        => TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.StartFrameOf(c, s), s);

    internal static string LaterClause(TrackModel t, ClipModel? later, double before, ProjectSession s)
    {
        if (later is null) return "";
        double d = When(t, later, s) - before;
        if (Math.Abs(d) < 0.0005) return "";
        return $" Anything later on this track now plays {Math.Abs(d):0.000}s {(d < 0 ? "sooner" : "later")}.";
    }

    /// <summary>
    /// Where a marker being dragged may snap to, on the timeline: section edges on other tracks,
    /// tempo-ramp ends on any track, and warp markers on other tracks. Not this track's own
    /// sections or markers — they move as soon as the warp changes.
    /// </summary>
    internal static List<(double Seconds, string What)> SnapTargets(TrackModel t, ProjectSession s)
    {
        var targets = new List<(double, string)>();
        var others = s.Project.Tracks.Where(x => !ReferenceEquals(x, t)).ToList();
        targets.AddRange(Snapping.EdgesOf(others, s, null).Select(e => (e.TimelineSeconds, "a section edge")));
        foreach (var x in s.Project.Tracks)
            foreach (var r in x.Ramps) { targets.Add((r.StartSeconds, "a tempo ramp")); targets.Add((r.EndSeconds, "a tempo ramp")); }
        foreach (var o in others)
            for (int i = 0; i < o.Warps.Count; i++) targets.Add((TimelineSecondsOf(o, i), "a warp marker"));
        return targets;
    }
}

/// <summary>
/// Dragging a warp marker (the <see cref="RampEdgeDrag"/> pattern): every move works from the
/// markers as they were when the drag began, so the drag can be taken back by dragging back. The
/// beat under the marker is heard where the pointer is; the markers either side stay put, and the
/// audio between them is spread to fit — never more than twice as fast or half as fast as its tempo.
/// </summary>
public sealed class WarpDrag
{
    // Kept this far inside the limits, in tempo seconds, so the spread at a limit is never refused
    // for a rounding error the size of an atom.
    private const double Margin = 1e-9;

    private readonly ProjectSession _s;
    private readonly TrackModel _track;
    private readonly int _index;
    private readonly WarpMarker _original;
    private readonly double _lo, _hi;
    private readonly long _startFrame;
    private readonly List<(double Seconds, string What)> _targets;

    public WarpDrag(ProjectSession session, TrackModel track, int index)
    {
        _s = session;
        _track = track;
        _index = index;
        _original = track.Warps[index];
        StartSeconds = WarpMarkers.TimelineSecondsOf(track, index);
        _startFrame = session.SecondsToFrames(StartSeconds);

        // The spread up to it, from the marker before it (or the start of the track), and from it
        // to the next one, if there is one, must stay within what the engine allows. After the
        // last marker the audio plays at its tempo whatever happens, so that side has no limit.
        var m = _original;
        double a0 = index > 0 ? track.Warps[index - 1].TrackSeconds : 0, t0 = index > 0 ? track.Warps[index - 1].TempoSeconds : 0;
        double lo = t0 + (m.TrackSeconds - a0) / TempoMap.HighestWarp;
        double hi = t0 + (m.TrackSeconds - a0) / TempoMap.LowestWarp;
        if (index + 1 < track.Warps.Count)
        {
            var n = track.Warps[index + 1];
            lo = Math.Max(lo, n.TempoSeconds - (n.TrackSeconds - m.TrackSeconds) / TempoMap.LowestWarp);
            hi = Math.Min(hi, n.TempoSeconds - (n.TrackSeconds - m.TrackSeconds) / TempoMap.HighestWarp);
        }
        // A marker boxed in at a limit on both sides has no room at all, and the margin would leave
        // the window inside out (review, 2026-10-18: Math.Clamp threw on every pointer move). Where
        // it is now is good by construction, so it stays there.
        (_lo, _hi) = lo + Margin <= hi - Margin ? (lo + Margin, hi - Margin) : (m.TempoSeconds, m.TempoSeconds);
        _targets = WarpMarkers.SnapTargets(track, session);
    }

    /// <summary>Where the marker's beat was heard when the drag began.</summary>
    public double StartSeconds { get; }

    /// <summary>Where it is heard now.</summary>
    public double NowSeconds => WarpMarkers.TimelineSecondsOf(_track, _index);

    public WarpMarker Marker => _track.Warps[_index];
    public int Index => _index;

    /// <summary>What the last move snapped to, in words; null when it was not snapped.</summary>
    public string? SnappedTo { get; private set; }

    /// <summary>Where on the timeline it snapped, for drawing a guide line.</summary>
    public double? SnapLineSeconds { get; private set; }

    /// <summary>True when the last move went as far as the marker can go, short of where it was asked.</summary>
    public bool Clamped { get; private set; }

    /// <summary>
    /// True when what stopped it was the engine — the track would have played faster or slower there
    /// than the engine can — rather than the spread either side reaching half or twice its tempo.
    /// </summary>
    public bool BeyondEngine { get; private set; }

    /// <summary>
    /// Puts the marker's beat at <paramref name="timelineSeconds"/> — snapped as <paramref name="snap"/>
    /// says to an edge, a ramp's end, another track's marker or the playhead it carries, and on a
    /// whole frame — as far as the markers either side allow.
    /// </summary>
    public void DragTo(double timelineSeconds, SnapSettings snap)
    {
        double at = Math.Max(0, timelineSeconds);
        string? what = null;
        double? on = null;
        if (snap.Enabled)
        {
            double best = snap.RadiusSeconds;
            foreach (var (x, w) in _targets)
            {
                double d = Math.Abs(x - at);
                if (x >= 0 && d <= best) { best = d; on = x; what = w; }
            }
            if (snap.Playhead is { } p && Math.Abs(p - at) <= best) { on = p; what = "the playhead"; }
            if (on is { } hit) at = hit;
        }
        long frame = _s.SecondsToFrames(at);
        at = TimelineEditor.SecondsOf(frame, _s);

        // Back where it started is exactly where it started, so a marker dragged away and back is
        // still unmoved — on an untouched track, still not warped at all.
        WarpMarker moved;
        BeyondEngine = false;
        if (frame == _startFrame) { moved = _original; Clamped = false; }
        else
        {
            // The tempo alone, which the warp does not change, says how far the track has got there.
            double tempo = TimelineEditor.TempoSecondsAt(_track, at);
            double clamped = Math.Clamp(tempo, _lo, _hi);
            Clamped = clamped != tempo;
            moved = _original with { TempoSeconds = clamped };
        }

        var pins = _track.Warps.Select((w, i) => i == _index ? moved : w).Select(w => new TempoMap.Pin(w.TrackSeconds, w.TempoSeconds)).ToArray();
        if (TempoMap.TryWarped(WarpMarkers.TempoOf(_track), pins, out _)) _track.Warps[_index] = moved;
        else Clamped = BeyondEngine = true;       // it would ask the engine for a speed it cannot play: stay at the last good place

        bool onSnap = on is not null && !Clamped;
        SnapLineSeconds = onSnap ? on : null;
        SnappedTo = onSnap ? what : null;
    }

    /// <summary>The status bar's line while dragging.</summary>
    public string Describe()
    {
        double d = NowSeconds - StartSeconds;
        string where = Math.Abs(d) < 0.0005 ? "where it was" : $"{Math.Abs(d):0.000}s {(d > 0 ? "later" : "sooner")}";
        string spreads = $"; {WarpMarkers.Describe(WarpMarkers.SpreadBefore(_track, _index))} {(_index == 0 ? "from the start of the track" : "from the marker before")}"
                       + (_index + 1 < _track.Warps.Count ? $", {WarpMarkers.Describe(WarpMarkers.SpreadBefore(_track, _index + 1))} to the next" : "");
        string tail = BeyondEngine ? " — as far as it goes: the track would play faster or slower there than Benchmix can"
                    : Clamped ? " — as far as it goes: the audio either side would play more than twice as fast or half as fast"
                    : SnappedTo is { } on ? " — on " + on : "";
        return $"{_track.Name}: beat at {StartSeconds:0.000}s → {NowSeconds:0.000}s, {where}{spreads}{tail}";
    }

    public EditResult Finish()
    {
        var now = Marker;
        if (now == _original) return new EditResult(false, $"{_track.Name}: the warp marker was left where it was.");
        double d = NowSeconds - StartSeconds;
        string before = WarpMarkers.Describe(WarpMarkers.SpreadBefore(_track, _index));
        string between = _index == 0 ? "from the start of the track to it" : "between it and the marker before it";
        string after = _index + 1 < _track.Warps.Count
            ? $", and {WarpMarkers.Describe(WarpMarkers.SpreadBefore(_track, _index + 1))} from it to the next one."
            : ". Everything after it on this track moves with it.";
        return new EditResult(true,
            $"{_track.Name}: the beat at {StartSeconds:0.000}s now plays at {NowSeconds:0.000}s, {Math.Abs(d):0.000}s {(d > 0 ? "later" : "sooner")}. The audio {between} plays {before}{after}");
    }
}
