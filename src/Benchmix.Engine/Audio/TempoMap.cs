namespace Benchmix.Engine.Audio;

/// <summary>
/// How far through its own, unstretched audio a track has got at each point of the timeline,
/// measured from its origin — its speed, added up over time.
///
/// <para>
/// With tempo ramps (Daniel, 2026-10-11) the speed is piecewise linear in timeline time, so the
/// position is piecewise quadratic, and both it and its inverse are closed form. With no ramp it is
/// exactly the arithmetic it replaced — <c>rel × speed</c> and <c>T ÷ speed</c>, the same
/// expressions — so a track without a ramp renders bit for bit as it did before ramps existed.
/// Before the first ramp a ramped map is that arithmetic too, so nothing before a ramp moves.
/// </para>
/// <para>
/// With warp markers (Daniel, 2026-10-18) there is a second stage after that one. The map above —
/// the knob and the ramps — says how far the track's <i>tempo</i> has carried it; each marker then
/// pins a point of the track's audio to a point of that tempo, and between two markers the audio
/// is spread evenly. The position is the warp of the tempo's position, so the speed is the tempo's
/// times the warp's slope, and it jumps where a marker is, as in Ableton. A map with no marker, or
/// whose markers have none of them been moved, is the one before them, the very same object.
/// </para>
/// <para>
/// The unit is the caller's: the engine builds it in frames, the editing code in seconds. It is
/// immutable, built on the caller's thread, and published to the render thread by reference swap,
/// as a <see cref="ClipTimeline"/> is. Nothing here allocates once built.
/// </para>
/// </summary>
public sealed class TempoMap
{
    /// <summary>One ramp, origin-relative: from the speed in effect at <see cref="From"/> to <see cref="ToSpeed"/> at <see cref="To"/>.</summary>
    public readonly record struct Ramp(double From, double To, double ToSpeed);

    /// <summary>
    /// One warp marker, origin-relative: the track's audio at <see cref="Audio"/> plays at the moment
    /// its tempo — the knob and its ramps — has carried it to <see cref="Tempo"/>. A marker that has
    /// not been moved has the two equal.
    /// </summary>
    public readonly record struct Pin(double Audio, double Tempo);

    /// <summary>The speeds the engine can play, as the TEMPO knob's model range.</summary>
    public const double LowestSpeed = 0.25, HighestSpeed = 4.0;

    /// <summary>
    /// How much slower or faster than its tempo the audio between two warp markers may be spread:
    /// half to twice. Warping is for a beat that has drifted, not for changing a song's tempo.
    /// </summary>
    public const double LowestWarp = 0.5, HighestWarp = 2.0;

    // The builders that play and draw (LenientWarped) keep a spread this much past the limits, and
    // an edit (TryWarped) stops this much short of the engine's speeds. The editor builds in seconds
    // and the engine in frames; a marker at a limit is then never kept by one and dropped by the
    // other for a rounding of the units — that dropped the whole warp in the engine while the lane
    // still drew it (review, 2026-10-18). The engine's own range is never stretched: the voice
    // refuses a map outside it.
    private const double Slack = 1e-9;

    // Segment i covers [_lo[i], _lo[i + 1]); the first starts at minus infinity and the last runs to
    // plus infinity. Within it the position is RefF + V·dx + ½·B·dx², with dx = x − RefX, and _loF[i]
    // is the position at _lo[i]. _pointX/_pointV are the breakpoints, for MaxSpeedOver.
    private readonly double[] _lo, _loF, _refX, _refF, _v, _b, _pointX, _pointV;

    // A warped map: the unwarped one, and its markers sorted by both coordinates. _slope[0] is the
    // warp's slope from the origin to the first marker, _slope[i] from marker i − 1 to marker i,
    // and _slope[n] = 1 after the last: there the audio plays at its tempo again.
    private readonly TempoMap? _tempo;
    private readonly double[] _pinAudio = Array.Empty<double>(), _pinTempo = Array.Empty<double>(), _slope = Array.Empty<double>();

    private TempoMap(double speed)
    {
        IsConstant = true;
        BaseSpeed = MinSpeed = MaxSpeed = speed;
        _lo = _loF = _refX = _refF = _v = _b = _pointX = _pointV = Array.Empty<double>();
    }

    private TempoMap(TempoMap tempo, double[] audio, double[] tempoAt)
    {
        _tempo = tempo;
        _pinAudio = audio;
        _pinTempo = tempoAt;
        int n = audio.Length;
        _slope = new double[n + 1];
        _slope[0] = audio[0] / tempoAt[0];
        for (int i = 1; i < n; i++) _slope[i] = (audio[i] - audio[i - 1]) / (tempoAt[i] - tempoAt[i - 1]);
        _slope[n] = 1;
        _lo = _loF = _refX = _refF = _v = _b = _pointX = _pointV = Array.Empty<double>();

        BaseSpeed = tempo.BaseSpeed;
        double min = double.PositiveInfinity, max = 0;
        for (int i = 0; i <= n; i++)
        {
            var (x0, x1) = SpanOfSlope(i);
            min = Math.Min(min, _slope[i] * tempo.MinSpeedOver(x0, x1));
            max = Math.Max(max, _slope[i] * tempo.MaxSpeedOver(x0, x1));
        }
        MinSpeed = min;
        MaxSpeed = max;
    }

    private TempoMap(double baseSpeed, double[] points, double[] speeds)
    {
        // A ramp that begins before the origin gets a breakpoint at the origin itself, so the map
        // can be measured outward from it in both directions and is exactly 0 there.
        var xl = points.ToList();
        var vl = speeds.ToList();
        int origin = -1;
        if (xl[0] < 0)
        {
            int k = xl.FindIndex(x => x >= 0);
            if (k >= 0 && xl[k] == 0) origin = k;
            else
            {
                double at0 = k < 0 ? vl[^1] : vl[k - 1] + (vl[k] - vl[k - 1]) * (0 - xl[k - 1]) / (xl[k] - xl[k - 1]);
                origin = k < 0 ? xl.Count : k;
                xl.Insert(origin, 0);
                vl.Insert(origin, at0);
            }
        }
        var xs = xl.ToArray();
        var vs = vl.ToArray();

        BaseSpeed = baseSpeed;
        _pointX = xs;
        _pointV = vs;
        MinSpeed = Math.Min(baseSpeed, vs.Min());
        MaxSpeed = Math.Max(baseSpeed, vs.Max());

        int n = xs.Length, segments = n + 1;
        _lo = new double[segments];
        _loF = new double[segments];
        _refX = new double[segments];
        _refF = new double[segments];
        _v = new double[segments];
        _b = new double[segments];

        _lo[0] = double.NegativeInfinity;
        _v[0] = vs[0];
        for (int i = 1; i < segments; i++)
        {
            _lo[i] = xs[i - 1];
            _v[i] = vs[i - 1];
            _b[i] = i < n ? (vs[i] - vs[i - 1]) / (xs[i] - xs[i - 1]) : 0;
        }

        // The segment that starts at the origin — or, when every ramp is after it, the first one,
        // which then is speed × x, bit for bit — is the anchor: position 0 at x = 0.
        int anchor = origin < 0 ? 0 : origin + 1;
        _refX[anchor] = 0;
        _refF[anchor] = 0;

        // After it, each segment starts from the one before's own formula at the boundary, so both
        // give the identical double there and the position cannot step at a breakpoint.
        for (int i = anchor + 1; i < segments; i++)
        {
            _refX[i] = _lo[i];
            _refF[i] = Formula(i - 1, _lo[i]);
        }
        // Before it, each segment is measured back from its own end, with the speed at that end.
        for (int i = anchor - 1; i >= 0; i--)
        {
            _refX[i] = _lo[i + 1];
            _refF[i] = i + 1 == anchor || _refX[i + 1] == _lo[i + 1] ? _refF[i + 1] : Formula(i + 1, _lo[i + 1]);
            _v[i] = vs[i];
        }

        _loF[0] = double.NegativeInfinity;
        for (int i = 1; i < segments; i++) _loF[i] = _refX[i] == _lo[i] ? _refF[i] : Formula(i, _lo[i]);
    }

    /// <summary>One speed everywhere — a track with no ramp.</summary>
    public static TempoMap Constant(double speed) => new(speed);

    /// <summary>
    /// A map from <paramref name="baseSpeed"/> and ramps that must already be sorted, each of some
    /// length, never overlapping (butting is fine) and at speeds the engine can play. Anything else
    /// throws: this is the strict builder, for tests. The editor builds with <see cref="Lenient"/>.
    /// </summary>
    public static TempoMap Build(double baseSpeed, ReadOnlySpan<Ramp> ramps)
    {
        if (!(baseSpeed >= LowestSpeed && baseSpeed <= HighestSpeed)) throw new ArgumentOutOfRangeException(nameof(baseSpeed));
        if (ramps.Length == 0) return Constant(baseSpeed);

        var xs = new List<double>();
        var vs = new List<double>();
        double held = baseSpeed, end = double.NegativeInfinity;
        foreach (var r in ramps)
        {
            if (!double.IsFinite(r.From) || !double.IsFinite(r.To) || !(r.To > r.From))
                throw new ArgumentException($"A ramp from {r.From} to {r.To} has no length.", nameof(ramps));
            if (r.From < end) throw new ArgumentException("Ramps overlap.", nameof(ramps));
            if (!(r.ToSpeed >= LowestSpeed && r.ToSpeed <= HighestSpeed))
                throw new ArgumentException($"A ramp to speed {r.ToSpeed} is out of reach.", nameof(ramps));

            if (xs.Count == 0 || r.From != xs[^1]) { xs.Add(r.From); vs.Add(held); }
            xs.Add(r.To);
            vs.Add(r.ToSpeed);
            held = r.ToSpeed;
            end = r.To;
        }
        return new TempoMap(baseSpeed, xs.ToArray(), vs.ToArray());
    }

    /// <summary>
    /// A map that never throws: the speeds clamped to what the engine can play, ramps sorted, and
    /// any ramp of no length or overlapping one before it left out. For everything that draws, hit-
    /// tests or plays, where a slip in a project file must not take the app down.
    /// </summary>
    public static TempoMap Lenient(double baseSpeed, IEnumerable<Ramp> ramps)
    {
        double b = double.IsFinite(baseSpeed) ? Math.Clamp(baseSpeed, LowestSpeed, HighestSpeed) : 1.0;
        var kept = new List<Ramp>();
        double end = double.NegativeInfinity;
        foreach (var r in ramps.Where(r => double.IsFinite(r.From) && double.IsFinite(r.To) && double.IsFinite(r.ToSpeed) && r.To > r.From)
                               .OrderBy(r => r.From))
        {
            if (r.From < end) continue;
            kept.Add(r with { ToSpeed = Math.Clamp(r.ToSpeed, LowestSpeed, HighestSpeed) });
            end = r.To;
        }
        return Build(b, kept.ToArray());
    }

    /// <summary>
    /// <paramref name="tempo"/> with warp markers after it. They must already be sorted, strictly
    /// increasing in both coordinates, after the origin, and spread no further than
    /// <see cref="LowestWarp"/>–<see cref="HighestWarp"/> between each two (and from the origin to the
    /// first); and the speeds that make must be ones the engine can play. Anything else throws: this
    /// is the strict builder, for tests. With no marker moved, it is <paramref name="tempo"/> itself.
    /// </summary>
    public static TempoMap Warped(TempoMap tempo, ReadOnlySpan<Pin> pins)
    {
        if (tempo._tempo is not null) throw new ArgumentException("That map is already warped.", nameof(tempo));
        return TryWarped(tempo, pins, out var map)
            ? map
            : throw new ArgumentException("Those warp markers are out of order, spread the audio too far, or ask for a speed the engine cannot play.", nameof(pins));
    }

    /// <summary>
    /// <paramref name="tempo"/> with warp markers after it, never throwing: markers sorted by where
    /// in the audio they are, and any that is not after the one before it in both coordinates, or
    /// spreads the audio out of reach, left out. If what is left would ask the engine for a speed it
    /// cannot play, the warp is left out altogether. For everything that draws, hit-tests or plays.
    /// </summary>
    public static TempoMap LenientWarped(TempoMap tempo, IEnumerable<Pin> pins)
    {
        if (tempo._tempo is not null) return tempo;
        var kept = new List<Pin>();
        double a = 0, t = 0;
        foreach (var p in pins.Where(p => double.IsFinite(p.Audio) && double.IsFinite(p.Tempo)).OrderBy(p => p.Audio))
        {
            if (!(p.Audio > a) || !(p.Tempo > t)) continue;
            double slope = (p.Audio - a) / (p.Tempo - t);
            if (!(slope >= LowestWarp * (1 - Slack) && slope <= HighestWarp * (1 + Slack))) continue;
            kept.Add(p);
            (a, t) = (p.Audio, p.Tempo);
        }
        if (kept.All(p => p.Audio == p.Tempo)) return tempo;
        var map = new TempoMap(tempo, kept.Select(p => p.Audio).ToArray(), kept.Select(p => p.Tempo).ToArray());
        return map.MinSpeed < LowestSpeed || map.MaxSpeed > HighestSpeed ? tempo : map;
    }

    /// <summary>
    /// <see cref="Warped"/> without the throwing: false, and no map, for markers it would refuse.
    /// For an edit that must stop short of an impossible warp rather than have it dropped.
    /// </summary>
    public static bool TryWarped(TempoMap tempo, ReadOnlySpan<Pin> pins, out TempoMap map)
    {
        map = tempo;
        if (tempo._tempo is not null) return false;
        double a = 0, t = 0;
        bool moved = false;
        foreach (var p in pins)
        {
            if (!double.IsFinite(p.Audio) || !double.IsFinite(p.Tempo) || !(p.Audio > a) || !(p.Tempo > t)) return false;
            double slope = (p.Audio - a) / (p.Tempo - t);
            if (!(slope >= LowestWarp && slope <= HighestWarp)) return false;
            moved |= p.Audio != p.Tempo;
            (a, t) = (p.Audio, p.Tempo);
        }
        if (!moved) return true;
        var all = pins.ToArray();
        var warped = new TempoMap(tempo, all.Select(p => p.Audio).ToArray(), all.Select(p => p.Tempo).ToArray());
        if (warped.MinSpeed < LowestSpeed * (1 + Slack) || warped.MaxSpeed > HighestSpeed * (1 - Slack)) return false;
        map = warped;
        return true;
    }

    /// <summary>
    /// How far the tempo has got when the audio is at <paramref name="audio"/>, through these markers
    /// (sorted, as for <see cref="Warped"/>): where a marker put down there now would keep it. In a
    /// stretch no marker has changed it is <paramref name="audio"/> itself, exactly — so a marker
    /// placed there is unmoved, and changes nothing.
    /// </summary>
    public static double TempoOf(ReadOnlySpan<Pin> pins, double audio)
    {
        int i = 0;
        while (i < pins.Length && pins[i].Audio <= audio) i++;
        // The stretch from marker i − 1 (or the origin) to marker i (or on forever).
        double a0 = i == 0 ? 0 : pins[i - 1].Audio, t0 = i == 0 ? 0 : pins[i - 1].Tempo;
        if (a0 == t0 && (i == pins.Length || pins[i].Audio == pins[i].Tempo)) return audio;
        if (i == pins.Length) return t0 + (audio - a0);
        double slope = (pins[i].Audio - a0) / (pins[i].Tempo - t0);
        return i == 0 ? audio / slope : t0 + (audio - a0) / slope;
    }

    /// <summary>Whether the map has warp markers in it, at least one of them moved.</summary>
    public bool IsWarped => _tempo is not null;

    /// <summary>
    /// The first timeline point after <paramref name="x"/> where the speed jumps — a warp marker —
    /// or +∞. The vocoder is fed one speed per run, so a run that crossed one would spread the jump
    /// across itself; the voice ends its runs here instead.
    /// </summary>
    public double NextJumpAfter(double x)
    {
        if (_tempo is null) return double.PositiveInfinity;
        int i = SlopeIndexAt(_tempo.PositionAt(x));
        for (; i < _pinTempo.Length; i++)
        {
            double at = _tempo.RelAt(_pinTempo[i]);
            if (at > x) return at;
        }
        return double.PositiveInfinity;
    }

    /// <summary>The map without its warp markers: the knob and the ramps alone. Itself when it has none.</summary>
    public TempoMap Unwarped => _tempo ?? this;

    public bool IsConstant { get; }

    /// <summary>
    /// The speed before the first ramp — the TEMPO knob. Under warp markers it is still the knob,
    /// which the audio before the first marker plays at only if that stretch was not moved.
    /// </summary>
    public double BaseSpeed { get; }

    public double MinSpeed { get; }
    public double MaxSpeed { get; }

    /// <summary>The track's position at timeline point <paramref name="x"/>, from its origin.</summary>
    public double PositionAt(double x)
    {
        if (IsConstant) return x * BaseSpeed;
        if (_tempo is { } tempo) return Warp(tempo.PositionAt(x));
        return Formula(SegmentAt(x), x);
    }

    /// <summary>The timeline point at which the track reaches <paramref name="position"/>: the inverse of <see cref="PositionAt"/>.</summary>
    public double RelAt(double position)
    {
        if (IsConstant) return position / BaseSpeed;
        if (_tempo is { } tempo) return tempo.RelAt(Unwarp(position));
        int i = _loF.Length - 1;
        while (i > 0 && position < _loF[i]) i--;
        double dp = position - _refF[i];
        double v = _v[i], b = _b[i];
        // The stable root of ½·b·dx² + v·dx − dp = 0: no cancellation when b is tiny.
        double dx = b == 0 ? dp / v : 2 * dp / (v + Math.Sqrt(Math.Max(0, v * v + 2 * b * dp)));
        return _refX[i] + dx;
    }

    /// <summary>The speed at timeline point <paramref name="x"/>.</summary>
    public double SpeedAt(double x)
    {
        if (IsConstant) return BaseSpeed;
        if (_tempo is { } tempo) return _slope[SlopeIndexAt(tempo.PositionAt(x))] * tempo.SpeedAt(x);
        int i = SegmentAt(x);
        return _b[i] == 0 ? _v[i] : _v[i] + _b[i] * (x - _refX[i]);
    }

    /// <summary>How long on the timeline a stretch of the track takes, from <paramref name="fromPosition"/> on.</summary>
    public double DurationOf(double fromPosition, double length)
        => IsConstant ? length / BaseSpeed : RelAt(fromPosition + length) - RelAt(fromPosition);

    /// <summary>The fastest the track plays anywhere between two timeline points.</summary>
    public double MaxSpeedOver(double x0, double x1)
    {
        if (IsConstant) return BaseSpeed;
        if (_tempo is { } tempo)
        {
            // Each stretch between markers is the tempo's speed times its own slope; at a marker
            // the speed jumps, so both sides of one inside the run count.
            double peak = 0;
            int i = SlopeIndexAt(tempo.PositionAt(x0)), last = SlopeIndexAt(tempo.PositionAt(x1));
            for (double from = x0; ; i++)
            {
                double to = i >= last ? x1 : tempo.RelAt(_pinTempo[i]);
                peak = Math.Max(peak, _slope[i] * tempo.MaxSpeedOver(from, Math.Max(from, to)));
                if (i >= last) return peak;
                from = to;
            }
        }
        double max = Math.Max(SpeedAt(x0), SpeedAt(x1));
        for (int i = 0; i < _pointX.Length; i++)
            if (_pointX[i] > x0 && _pointX[i] < x1 && _pointV[i] > max) max = _pointV[i];
        return max;
    }

    /// <summary>The slowest the unwarped map plays anywhere between two timeline points, for a warped map's range.</summary>
    private double MinSpeedOver(double x0, double x1)
    {
        if (IsConstant) return BaseSpeed;
        double min = Math.Min(SpeedAt(x0), SpeedAt(x1));
        for (int i = 0; i < _pointX.Length; i++)
            if (_pointX[i] > x0 && _pointX[i] < x1 && _pointV[i] < min) min = _pointV[i];
        return min;
    }

    /// <summary>The track frame at a whole timeline frame — by the floor the render loop uses.</summary>
    public long TrackFrameAt(long rel) => (long)Math.Floor(PositionAt(rel));

    /// <summary>
    /// The first timeline frame whose track frame is at or past <paramref name="trackFrame"/>, by
    /// the same floor as <see cref="TrackFrameAt"/>. The inverse alone disagrees with that floor by
    /// one in floating point for some frames; the two loops make them agree, as they always have.
    /// </summary>
    public long FirstRelAt(long trackFrame)
    {
        long r = (long)Math.Ceiling(RelAt(trackFrame));
        while (TrackFrameAt(r) < trackFrame) r++;
        while (TrackFrameAt(r - 1) >= trackFrame) r--;
        return r;
    }

    /// <summary>Whether two maps describe the same tempo, number for number.</summary>
    public bool SameAs(TempoMap other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (_tempo is not null || other._tempo is not null)
            return _tempo is not null && other._tempo is not null && _tempo.SameAs(other._tempo)
                && _pinAudio.AsSpan().SequenceEqual(other._pinAudio) && _pinTempo.AsSpan().SequenceEqual(other._pinTempo);
        if (IsConstant != other.IsConstant || BaseSpeed != other.BaseSpeed) return false;
        return IsConstant || (_pointX.AsSpan().SequenceEqual(other._pointX) && _pointV.AsSpan().SequenceEqual(other._pointV));
    }

    // ---------------------------------------------------------------- the warp

    /// <summary>
    /// Where the audio is when the tempo has got to <paramref name="t"/>. Exactly a marker's audio at
    /// its tempo, so the audio at a marker lands where it was put; through the origin before the first,
    /// so the track is still at 0 there.
    /// </summary>
    private double Warp(double t)
    {
        int i = SlopeIndexAt(t);
        return i == 0 ? _slope[0] * t : _pinAudio[i - 1] + _slope[i] * (t - _pinTempo[i - 1]);
    }

    /// <summary>The inverse of <see cref="Warp"/>: how far the tempo has got when the audio is at <paramref name="a"/>.</summary>
    private double Unwarp(double a)
    {
        int i = LastAtOrBefore(_pinAudio, a) + 1;
        return i == 0 ? a / _slope[0] : _pinTempo[i - 1] + (a - _pinAudio[i - 1]) / _slope[i];
    }

    /// <summary>Which slope applies at tempo position <paramref name="t"/>: 0 before the first marker, i from marker i − 1 on.</summary>
    private int SlopeIndexAt(double t) => LastAtOrBefore(_pinTempo, t) + 1;

    /// <summary>The timeline span over which slope <paramref name="i"/> applies, open-ended at both ends of the map.</summary>
    private (double From, double To) SpanOfSlope(int i)
        => (i == 0 ? double.NegativeInfinity : _tempo!.RelAt(_pinTempo[i - 1]),
            i == _pinTempo.Length ? double.PositiveInfinity : _tempo!.RelAt(_pinTempo[i]));

    /// <summary>The last index whose value is at or before <paramref name="v"/>, or −1. The values are sorted.</summary>
    private static int LastAtOrBefore(double[] values, double v)
    {
        int lo = 0, hi = values.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (values[mid] <= v) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private int SegmentAt(double x)
    {
        int i = _lo.Length - 1;
        while (i > 0 && x < _lo[i]) i--;
        return i;
    }

    private double Formula(int i, double x)
    {
        double dx = x - _refX[i];
        return _b[i] == 0 ? _refF[i] + _v[i] * dx : _refF[i] + _v[i] * dx + 0.5 * _b[i] * dx * dx;
    }
}
