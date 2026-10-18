using System.Globalization;
using Benchmix.Core.Model;
using Benchmix.Engine.Audio;

namespace Benchmix.Core;

/// <summary>
/// A track's tempo ramps (Daniel, 2026-10-11): spans of the timeline over which its tempo changes
/// in a straight line. See <see cref="TrackModel.Ramps"/>.
/// </summary>
public static class TempoRamps
{
    /// <summary>The shortest a tempo ramp can be.</summary>
    public const double MinimumSeconds = 0.1;

    /// <summary>
    /// Makes a track's ramps playable, as loading a project does: on whole frames, sorted, each at
    /// least <see cref="MinimumSeconds"/> long, at a speed the engine can play, after 0:00, and none
    /// overlapping the one before. Anything else is dropped — a hand-edited or damaged file must
    /// neither crash the app nor play a tempo change nobody set up.
    /// </summary>
    public static void Normalise(TrackModel t, int sampleRate)
    {
        double OnFrame(double seconds) => Math.Round(seconds * sampleRate) / sampleRate;
        var kept = new List<TempoRamp>();
        double end = double.NegativeInfinity;
        var playable = t.Ramps
            .Where(r => r is not null && double.IsFinite(r.StartSeconds) && double.IsFinite(r.EndSeconds) && double.IsFinite(r.ToSpeed))
            .Select(r => r with { StartSeconds = OnFrame(r.StartSeconds), EndSeconds = OnFrame(r.EndSeconds) })
            .OrderBy(r => r.StartSeconds);
        foreach (var r in playable)
        {
            if (r.StartSeconds < 0 || r.EndSeconds - r.StartSeconds < MinimumSeconds) continue;
            if (r.ToSpeed < TempoMap.LowestSpeed || r.ToSpeed > TempoMap.HighestSpeed) continue;
            if (r.StartSeconds < end) continue;
            kept.Add(r);
            end = r.EndSeconds;
        }
        t.Ramps = kept;
    }

    // ---------------------------------------------------------------- lookups

    /// <summary>The ramp strictly containing <paramref name="seconds"/>, or −1. Its two ends are not inside it.</summary>
    public static int IndexInside(TrackModel t, double seconds)
    {
        for (int i = 0; i < t.Ramps.Count; i++)
            if (t.Ramps[i].StartSeconds < seconds && seconds < t.Ramps[i].EndSeconds) return i;
        return -1;
    }

    /// <summary>The ramp under a right-click, a little either side of its ends included, or −1.</summary>
    public static int IndexUnder(TrackModel t, double seconds, double slop)
    {
        for (int i = 0; i < t.Ramps.Count; i++)
            if (t.Ramps[i].StartSeconds - slop <= seconds && seconds <= t.Ramps[i].EndSeconds + slop) return i;
        return -1;
    }

    /// <summary>
    /// The free stretch of the timeline around <paramref name="seconds"/>: from the end of the ramp
    /// before it (or 0:00) to the start of the ramp after it (or forever), ignoring ramp
    /// <paramref name="except"/>.
    /// </summary>
    public static (double Lo, double Hi) RoomAround(TrackModel t, double seconds, int except = -1)
    {
        double lo = 0, hi = double.PositiveInfinity;
        for (int i = 0; i < t.Ramps.Count; i++)
        {
            if (i == except) continue;
            var r = t.Ramps[i];
            if (r.EndSeconds <= seconds) lo = Math.Max(lo, r.EndSeconds);
            if (r.StartSeconds >= seconds) hi = Math.Min(hi, r.StartSeconds);
        }
        return (lo, hi);
    }

    /// <summary>
    /// The tempo the track plays at a point of the timeline, in BPM, by its tempo alone: the knob and
    /// its ramps. Warp markers spread the audio a little faster or slower between them on top of
    /// this; a ramp neither starts from nor stores that.
    /// </summary>
    public static double BpmAt(TrackModel t, double seconds) => t.Bpm * TimelineEditor.TempoSpeedAt(t, seconds);

    /// <summary>
    /// Reads a typed tempo. It must be a number, and one the engine can play for this song: between
    /// a quarter and four times its own tempo.
    /// </summary>
    public static (bool Ok, double Speed, string Why) ParseBpm(TrackModel t, string? typed)
    {
        string text = (typed ?? "").Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out bpm))
            return (false, 0, "Type a tempo in BPM, like 115.");
        if (t.Bpm <= 0) return (false, 0, $"{t.Name}'s tempo is unknown, so there is nothing to work a speed out from.");
        double speed = bpm / t.Bpm;
        if (!double.IsFinite(bpm) || speed < TempoMap.LowestSpeed || speed > TempoMap.HighestSpeed)
            return (false, 0, $"{bpm:0.0} BPM is out of reach for {t.Name}: between {t.Bpm * TempoMap.LowestSpeed:0.0} and {t.Bpm * TempoMap.HighestSpeed:0.0} BPM.");
        return (true, speed, "");
    }

    /// <summary>
    /// Where a ramp's dot lands near <paramref name="seconds"/>, on a whole frame: snapped within the
    /// snap distance to a section edge on another track, or a ramp end on any track; and, for a
    /// start only, to an edge of this track's own sections. An end is not offered those: they sit
    /// after the ramp's start, so they move as soon as the ramp changes the tempo, and an end
    /// snapped to one would not stay on it.
    /// </summary>
    /// <param name="except">The ramp being moved, whose own ends are not targets (−1 for a new one).</param>
    public static double SnapPoint(TrackModel t, double seconds, bool isEnd, int except, SnapSettings snap,
                                   ProjectSession s, out double? snappedTo)
    {
        snappedTo = null;
        double at = Math.Max(0, seconds);
        if (snap.Enabled)
        {
            var targets = new List<double>();
            var others = s.Project.Tracks.Where(x => !ReferenceEquals(x, t)).ToList();
            targets.AddRange(Snapping.EdgesOf(others, s, null).Select(e => e.TimelineSeconds));
            foreach (var o in others)
                foreach (var r in o.Ramps) { targets.Add(r.StartSeconds); targets.Add(r.EndSeconds); }
            for (int i = 0; i < t.Ramps.Count; i++)
                if (i != except) { targets.Add(t.Ramps[i].StartSeconds); targets.Add(t.Ramps[i].EndSeconds); }
            if (!isEnd)
            {
                // Positioned without the ramp being moved: everything before its new start is then
                // exactly where it will be once the ramp is placed there.
                var probe = t.Clone();
                if (except >= 0 && except < probe.Ramps.Count) probe.Ramps.RemoveAt(except);
                targets.AddRange(Snapping.EdgesOf(new[] { probe }, s, null).Select(e => e.TimelineSeconds));
            }

            double best = snap.RadiusSeconds;
            foreach (double x in targets)
            {
                double d = Math.Abs(x - at);
                if (x >= 0 && d <= best) { best = d; snappedTo = x; }
            }
            if (snappedTo is { } hit) at = hit;
        }
        return OnFrame(at, s);
    }

    private static double OnFrame(double seconds, ProjectSession s) => TimelineEditor.SecondsOf(s.SecondsToFrames(seconds), s);

    // ---------------------------------------------------------------- edits

    /// <summary>
    /// Puts a ramp on the track from <paramref name="start"/> to <paramref name="end"/>, going from
    /// the tempo it plays at the start to <paramref name="toSpeed"/>. Refused if it would overlap
    /// another ramp, is too short, or asks for a speed the engine cannot play.
    /// </summary>
    public static EditResult Add(TrackModel t, double start, double end, double toSpeed, ProjectSession s)
    {
        if (t.Bpm <= 0) return EditResult.No($"{t.Name}'s tempo is unknown, so a ramp has no BPM to start from.");
        if (!double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(toSpeed)) return EditResult.No("That is not a tempo ramp that can be played.");
        double a = OnFrame(start, s), b = OnFrame(end, s);
        if (a < 0) return EditResult.No("A tempo ramp cannot start before 0:00.");
        if (b - a < MinimumSeconds) return EditResult.No("Click further along — a ramp needs some length.");
        if (t.Ramps.Any(r => r.StartSeconds < b && a < r.EndSeconds)) return EditResult.No("That would run into another tempo ramp on this track.");
        if (toSpeed < TempoMap.LowestSpeed || toSpeed > TempoMap.HighestSpeed)
            return EditResult.No($"{t.Bpm * toSpeed:0.0} BPM is out of reach for {t.Name}: between {t.Bpm * TempoMap.LowestSpeed:0.0} and {t.Bpm * TempoMap.HighestSpeed:0.0} BPM.");

        double from = BpmAt(t, a), to = t.Bpm * toSpeed;
        var later = FirstSectionFrom(t, b, s);
        double before = later is null ? 0 : TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.StartFrameOf(later, s), s);

        t.Ramps.Add(new TempoRamp(a, b, toSpeed));
        t.Ramps.Sort((x, y) => x.StartSeconds.CompareTo(y.StartSeconds));

        string message = $"{t.Name}: tempo goes from {from:0.0} to {to:0.0} BPM between {a:0.00}s and {b:0.00}s, and stays at {to:0.0} after.";
        return new EditResult(true, message + LaterClause(t, later, before, s));
    }

    /// <summary>Changes the tempo a ramp arrives at; where it runs stays the same.</summary>
    public static EditResult SetEndSpeed(TrackModel t, int index, double toSpeed, ProjectSession s)
    {
        if (index < 0 || index >= t.Ramps.Count) return EditResult.No("That tempo ramp is no longer there.");
        if (toSpeed < TempoMap.LowestSpeed || toSpeed > TempoMap.HighestSpeed)
            return EditResult.No($"{t.Bpm * toSpeed:0.0} BPM is out of reach for {t.Name}: between {t.Bpm * TempoMap.LowestSpeed:0.0} and {t.Bpm * TempoMap.HighestSpeed:0.0} BPM.");
        var r = t.Ramps[index];
        if (r.ToSpeed == toSpeed) return EditResult.No($"Kept {t.Bpm * toSpeed:0.0} BPM.");
        var later = FirstSectionFrom(t, r.EndSeconds, s);
        double before = later is null ? 0 : TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.StartFrameOf(later, s), s);
        t.Ramps[index] = r with { ToSpeed = toSpeed };
        return new EditResult(true, $"{t.Name}: the ramp now goes to {t.Bpm * toSpeed:0.0} BPM." + LaterClause(t, later, before, s));
    }

    /// <summary>Takes a ramp away. The tempo before it carries on in its place.</summary>
    public static EditResult Remove(TrackModel t, int index, ProjectSession s)
    {
        if (index < 0 || index >= t.Ramps.Count) return EditResult.No("That tempo ramp is no longer there.");
        var later = FirstSectionFrom(t, t.Ramps[index].EndSeconds, s);
        double before = later is null ? 0 : TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.StartFrameOf(later, s), s);
        t.Ramps.RemoveAt(index);
        return new EditResult(true, $"{t.Name}: tempo ramp removed. Ctrl+Z puts it back." + LaterClause(t, later, before, s));
    }

    /// <summary>The first section starting at or after a point of the timeline, if any.</summary>
    internal static ClipModel? FirstSectionFrom(TrackModel t, double seconds, ProjectSession s)
        => t.Clips
            .Where(c => TimelineEditor.LengthFramesOf(c, s) > 0)
            .Select(c => (c, at: TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.StartFrameOf(c, s), s)))
            .Where(x => x.at >= seconds - 1e-9)
            .OrderBy(x => x.at)
            .Select(x => x.c)
            .FirstOrDefault();

    private static string LaterClause(TrackModel t, ClipModel? later, double before, ProjectSession s)
    {
        if (later is null) return "";
        double d = TimelineEditor.TimelineSecondsOfFrame(t, TimelineEditor.StartFrameOf(later, s), s) - before;
        if (Math.Abs(d) < 0.005) return "";
        return $" Anything later on this track now plays {Math.Abs(d):0.00}s {(d < 0 ? "sooner" : "later")}.";
    }
}

/// <summary>
/// Dragging one end of a tempo ramp (the <see cref="SectionTrim"/> pattern): every move works from
/// the ramp as it was when the drag began, so the drag can be taken back by dragging back. The end
/// cannot pass the ramp's other end, and neither end can run into a neighbouring ramp.
/// </summary>
public sealed class RampEdgeDrag
{
    private readonly ProjectSession _s;
    private readonly TrackModel _track;
    private readonly int _index;
    private readonly bool _end;
    private readonly TempoRamp _original;
    private readonly double _lo, _hi;

    public RampEdgeDrag(ProjectSession session, TrackModel track, int index, bool end)
    {
        _s = session;
        _track = track;
        _index = index;
        _end = end;
        _original = track.Ramps[index];
        double prevEnd = index > 0 ? track.Ramps[index - 1].EndSeconds : 0;
        double nextStart = index + 1 < track.Ramps.Count ? track.Ramps[index + 1].StartSeconds : double.PositiveInfinity;
        (_lo, _hi) = end
            ? (_original.StartSeconds + TempoRamps.MinimumSeconds, nextStart)
            : (prevEnd, _original.EndSeconds - TempoRamps.MinimumSeconds);
    }

    public string? SnappedTo { get; private set; }
    public double? SnapLineSeconds { get; private set; }
    public TempoRamp Ramp => _track.Ramps[_index];

    public void DragTo(double seconds, SnapSettings snap)
    {
        double at = TempoRamps.SnapPoint(_track, seconds, _end, _index, snap, _s, out var snapped);
        double clamped = Math.Clamp(at, _lo, _hi);
        // Limits are whole frames, so a clamped point is still on one.
        clamped = TimelineEditor.SecondsOf(_s.SecondsToFrames(clamped), _s);
        bool onSnap = snapped is not null && clamped == at;
        SnapLineSeconds = onSnap ? snapped : null;
        SnappedTo = onSnap ? "an edge" : null;
        _track.Ramps[_index] = _end ? _original with { EndSeconds = clamped } : _original with { StartSeconds = clamped };
    }

    public EditResult Finish()
    {
        var r = Ramp;
        return r == _original
            ? new EditResult(false, $"{_track.Name}: the ramp was left where it was.")
            : new EditResult(true, $"{_track.Name}: the ramp now runs from {r.StartSeconds:0.00}s to {r.EndSeconds:0.00}s.");
    }
}
