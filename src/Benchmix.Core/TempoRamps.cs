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
}
