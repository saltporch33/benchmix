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
/// The unit is the caller's: the engine builds it in frames, the editing code in seconds. It is
/// immutable, built on the caller's thread, and published to the render thread by reference swap,
/// as a <see cref="ClipTimeline"/> is. Nothing here allocates once built.
/// </para>
/// </summary>
public sealed class TempoMap
{
    /// <summary>One ramp, origin-relative: from the speed in effect at <see cref="From"/> to <see cref="ToSpeed"/> at <see cref="To"/>.</summary>
    public readonly record struct Ramp(double From, double To, double ToSpeed);

    /// <summary>The speeds the engine can play, as the TEMPO knob's model range.</summary>
    public const double LowestSpeed = 0.25, HighestSpeed = 4.0;

    // Segment i covers [_lo[i], _lo[i + 1]); the first starts at minus infinity and the last runs to
    // plus infinity. Within it the position is RefF + V·dx + ½·B·dx², with dx = x − RefX, and _loF[i]
    // is the position at _lo[i]. _pointX/_pointV are the breakpoints, for MaxSpeedOver.
    private readonly double[] _lo, _loF, _refX, _refF, _v, _b, _pointX, _pointV;

    private TempoMap(double speed)
    {
        IsConstant = true;
        BaseSpeed = MinSpeed = MaxSpeed = speed;
        _lo = _loF = _refX = _refF = _v = _b = _pointX = _pointV = Array.Empty<double>();
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

    public bool IsConstant { get; }

    /// <summary>The speed before the first ramp — the TEMPO knob.</summary>
    public double BaseSpeed { get; }

    public double MinSpeed { get; }
    public double MaxSpeed { get; }

    /// <summary>The track's position at timeline point <paramref name="x"/>, from its origin.</summary>
    public double PositionAt(double x)
    {
        if (IsConstant) return x * BaseSpeed;
        return Formula(SegmentAt(x), x);
    }

    /// <summary>The timeline point at which the track reaches <paramref name="position"/>: the inverse of <see cref="PositionAt"/>.</summary>
    public double RelAt(double position)
    {
        if (IsConstant) return position / BaseSpeed;
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
        double max = Math.Max(SpeedAt(x0), SpeedAt(x1));
        for (int i = 0; i < _pointX.Length; i++)
            if (_pointX[i] > x0 && _pointX[i] < x1 && _pointV[i] > max) max = _pointV[i];
        return max;
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
        if (IsConstant != other.IsConstant || BaseSpeed != other.BaseSpeed) return false;
        return IsConstant || (_pointX.AsSpan().SequenceEqual(other._pointX) && _pointV.AsSpan().SequenceEqual(other._pointV));
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
