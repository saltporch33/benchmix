namespace Benchmix.Engine.Analysis;

/// <summary>
/// Where a beat falls, in seconds of the audio it was looked for in; how clearly it stood out; and
/// the tempo that stretch actually has, in its own clock.
/// </summary>
public readonly record struct BeatPhaseResult(double Seconds, double Confidence, double Bpm);

/// <summary>
/// Finds where the beats fall in a stretch of audio whose tempo is roughly known. A comb that sums
/// the onset envelope at every beat period, slid through one period, peaks where the beats are.
/// Built for Sync (Daniel, 2026-10-28: "an automated snap button that realigns to match peaks like
/// kicks and snares"), 2026-10-29.
///
/// <para>
/// The envelope is the same spectral flux the tempo detector reads, at a 512-sample window and a
/// 32-sample hop — 1.3 ms at the rate it is given — and the comb's peak is refined between hops.
/// The period is first refined on the stretch itself (<see cref="RefinePeriod"/>): a tag is
/// rounded to a tenth of a BPM and a song drifts, and over twelve seconds a comb a few BPM off
/// smears over nothing. Flux peaks while a hit is moving into the window, before the hit itself,
/// by about the same for every hit on every track; <see cref="LagSeconds"/> is that much, measured
/// on clicks (<c>BeatSyncTests</c>), and is added back so the time returned names the hit. Sync
/// takes a difference of two of these, so the lag, and most of what is left of it, cancels there.
/// </para>
/// </summary>
public static class BeatPhase
{
    public const int FftSize = 512;
    public const int Hop = 32;

    /// <summary>How long before a hit its flux peaks: a hit is read this much early, and corrected by this much.</summary>
    public static double LagSeconds(int rate) => 0.955 * FftSize / rate;

    /// <summary>
    /// The beat nearest <paramref name="nearSeconds"/> in <paramref name="mono"/> at about
    /// <paramref name="bpm"/>. Confidence is 0 when the stretch is too short or has nothing in it,
    /// and near 1 when one phase stands clear of every other; an offbeat as strong as the beat
    /// pulls it towards 0, which is right — then there is no telling the two apart.
    /// </summary>
    public static BeatPhaseResult Find(float[] mono, int rate, double bpm, double nearSeconds)
    {
        if (bpm <= 0 || mono.Length < FftSize * 4) return new(nearSeconds, 0, bpm);
        var (env, fps) = OnsetEnvelope.Compute(mono, rate, FftSize, Hop);

        // The first frame's flux is the whole spectrum arriving at once — every bin up from nothing
        // — and on anything but silence it towers over every hit. Read as a beat, it put the beat at
        // the window's start wherever the window started, so a second Sync slid the track by the
        // same amount again (his two songs, 2026-10-29). The click-train tests begin in silence and
        // never saw it; one begins loud now.
        if (env.Length > 0) env[0] = 0;

        double period = 60.0 / bpm * fps;                      // one beat, in envelope frames
        if (period < 4 || env.Length < period * 2) return new(nearSeconds, 0, bpm);
        period = RefinePeriod(env, period);

        // The comb: every phase within one beat, scored by the envelope at it and at every beat after it.
        int steps = (int)Math.Ceiling(period);
        var score = new double[steps];
        for (int p = 0; p < steps; p++)
        {
            double sum = 0;
            int n = 0;
            for (double x = p; x + 1 < env.Length; x += period)
            {
                int i = (int)x;
                double frac = x - i;
                sum += env[i] * (1 - frac) + env[i + 1] * frac;
                n++;
            }
            score[p] = n > 0 ? sum / n : 0;
        }

        int best = 0;
        double peak = double.NegativeInfinity;
        for (int p = 0; p < steps; p++)
            if (score[p] > peak) { peak = score[p]; best = p; }
        if (peak <= 0) return new(nearSeconds, 0, bpm);

        // Between hops: a parabola through the peak and its neighbours, round the beat.
        double y0 = score[(best - 1 + steps) % steps], y1 = peak, y2 = score[(best + 1) % steps];
        double den = y0 - 2 * y1 + y2;
        double phase = best + (Math.Abs(den) > 1e-12 ? Math.Clamp(0.5 * (y0 - y2) / den, -1, 1) : 0);

        // The next-best phase at least a quarter beat away: the offbeat when it is as strong, otherwise the floor.
        double second = 0;
        for (int p = 0; p < steps; p++)
        {
            double away = Math.Abs(p - best);
            away = Math.Min(away, steps - away);
            if (away >= period / 4 && score[p] > second) second = score[p];
        }
        double confidence = Math.Max(0, 1 - second / peak);

        double near = nearSeconds * fps;
        double beat = phase + period * Math.Round((near - phase) / period);
        // The comb's lattice is pinned where the stretch's hits are densest, and a period a hair
        // off carries it a frame or two by here. So the beat is read again where it was asked for:
        // nine beats around it, slid a tenth of a frame at a time through an eighth of a beat.
        beat += LocalOffset(env, beat, period);
        return new(beat / fps + LagSeconds(rate), confidence, 60.0 * fps / period);
    }

    /// <summary>
    /// How far, in frames, the hits around <paramref name="beat"/> — four beats either side — sit
    /// from the lattice's tooth there, within an eighth of a beat: the sum of the envelope at the
    /// nine teeth peaks where the hits are.
    /// </summary>
    static double LocalOffset(float[] env, double beat, double period)
    {
        double bestQ = 0, bestV = double.NegativeInfinity;
        for (double q = -period / 8; q <= period / 8; q += 0.1)
        {
            double sum = 0;
            int n = 0;
            for (int k = -4; k <= 4; k++)
            {
                double x = beat + q + k * period;
                if (x < 0 || x + 1 >= env.Length) continue;
                int i = (int)x;
                double frac = x - i;
                sum += env[i] * (1 - frac) + env[i + 1] * frac;
                n++;
            }
            if (n > 0 && sum / n > bestV) { bestV = sum / n; bestQ = q; }
        }
        return bestQ;
    }

    /// <summary>
    /// The beat period this stretch actually has, within a tenth either side of the one given: the
    /// envelope's autocorrelation peaks there, and a parabola through the peak lands between
    /// frames. The given period is kept when nothing in that range stands up.
    /// </summary>
    public static double RefinePeriod(float[] env, double period)
    {
        int lo = Math.Max(2, (int)Math.Floor(period * 0.9));
        int hi = Math.Min(env.Length / 2, (int)Math.Ceiling(period * 1.1));
        if (hi <= lo + 2) return period;

        double mu = 0;
        foreach (var v in env) mu += v;
        mu /= env.Length;

        var ac = new double[hi + 2];
        for (int lag = lo - 1; lag <= hi + 1 && lag < env.Length; lag++)      // one each side, for the parabola
        {
            double s = 0;
            int n = 0;
            for (int i = 0; i + lag < env.Length; i++) { s += (env[i] - mu) * (env[i + lag] - mu); n++; }
            ac[lag] = n > 0 ? s / n : 0;
        }

        int best = lo;
        double bestV = double.NegativeInfinity;
        for (int lag = lo; lag <= hi; lag++)
            if (ac[lag] > bestV) { bestV = ac[lag]; best = lag; }
        if (bestV <= 0) return period;

        double y0 = ac[best - 1], y1 = bestV, y2 = ac[best + 1];
        double den = y0 - 2 * y1 + y2;
        double delta = Math.Abs(den) > 1e-12 ? Math.Clamp(0.5 * (y0 - y2) / den, -1, 1) : 0;
        return best + delta;
    }
}
