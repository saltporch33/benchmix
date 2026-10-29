using Benchmix.Core.Model;
using Benchmix.Engine.Analysis;

namespace Benchmix.Core;

/// <summary>
/// What Sync would do: the whole frames to slide the track by (0 when it is in time already, or
/// when it cannot be synced — <see cref="Ok"/> tells the two apart), and the sentence to say.
/// </summary>
public sealed record SyncPlan(bool Ok, long Frames, string Message, double Confidence = 0);

/// <summary>
/// Sync (Daniel, 2026-10-28: "after bpm matching, also an automated snap button that realigns to
/// match peaks like kicks and snares… typically called 'sync'"; built 2026-10-29): slides a track
/// along the timeline so its beats land on another track's, by less than half a beat either way.
///
/// <para>
/// Each track's beat is found near one moment — the playhead — in its own audio
/// (<see cref="BeatPhase"/>, in the section under that moment or the nearest one), put on the
/// timeline through the track's tempo (<see cref="TimelineEditor.TimelineSecondsAt"/>, so a
/// warped track is read where its warp put it), and the difference, folded into half a beat of the
/// reference's beat on the timeline, is how far the track moves: as whole frames, by its origin
/// (<see cref="TimelineEditor.SlideTrack"/>), so nothing on the track changes its place on the
/// track. A track whose tempo does not match the reference's is refused rather than lined up for
/// a moment, and one with tempo ramps is refused because the move would change what tempo its
/// sections play at. A track whose audio plays at a tempo other than the one it says — a wrong
/// tag — is synced where the playhead is and told so, because it will drift off again.
/// </para>
/// </summary>
public static class BeatSync
{
    /// <summary>Seconds of audio the beat is looked for in, around the playhead.</summary>
    public const double WindowSeconds = 12;

    /// <summary>
    /// Below this the comb saw nothing it could call a beat — silence, a pad, or an offbeat as
    /// strong as the beat. A guess, not a measurement: no music has been through this by ear.
    /// </summary>
    public const double MinConfidence = 0.1;

    /// <summary>Tempos within this fraction of each other count as matched (a tag rounded to 0.1 BPM is this far from its audio).</summary>
    public const double TempoTolerance = 0.005;

    /// <summary>Within this of the beat a track is in time: nothing moves, and nothing goes on the undo stack.</summary>
    public const double InTimeSeconds = 0.002;

    /// <summary>A stretch whose tempo reads further than this from what the track says gets a warning: its tag is probably wrong.</summary>
    public const double TagTolerance = 0.02;

    /// <summary>Works out the move without making it. <see cref="Slide"/> makes it.</summary>
    public static SyncPlan Plan(TrackModel track, TrackModel reference, double atSeconds, ProjectSession s)
    {
        if (ReferenceEquals(track, reference)) return new(false, 0, "A track cannot sync to itself.");
        if (track.Ramps.Count > 0)
            return new(false, 0, $"{track.Name} has tempo ramps, so Sync would change what tempo its sections play at — move it by hand.");
        if (track.Clips.Count == 0 || reference.Clips.Count == 0)
            return new(false, 0, $"{(track.Clips.Count == 0 ? track.Name : reference.Name)} has no sections to sync.");
        if (track.Bpm <= 0 || reference.Bpm <= 0)
            return new(false, 0, $"{(track.Bpm <= 0 ? track.Name : reference.Name)} has no tempo — set one by hand.");

        double refBpm = reference.Bpm * TimelineEditor.SpeedAt(reference, atSeconds);
        double bpm = Analyzer.FoldToNearestOctave(track.Bpm * TimelineEditor.SpeedAt(track, atSeconds), refBpm);
        if (Math.Abs(bpm - refBpm) > TempoTolerance * refBpm)
            return new(false, 0, $"{track.Name} plays at {bpm:F1} BPM and {reference.Name} at {refBpm:F1} — Match BPM first, or the beats drift apart again.");

        var mine = BeatNear(track, atSeconds, s);
        if (mine.Confidence < MinConfidence)
            return new(false, 0, $"Couldn't find {track.Name}'s beat near the playhead — move the playhead to where its drums play.");
        var theirs = BeatNear(reference, atSeconds, s);
        if (theirs.Confidence < MinConfidence)
            return new(false, 0, $"Couldn't find {reference.Name}'s beat near the playhead — move the playhead to where its drums play.");
        double confidence = Math.Min(mine.Confidence, theirs.Confidence);
        string tags = TagNote(track, mine.Bpm) + TagNote(reference, theirs.Bpm);

        double period = 60.0 / refBpm;                              // a beat of the reference, on the timeline
        double d = theirs.Seconds - mine.Seconds;
        d -= period * Math.Round(d / period);                       // to the nearest beat: never more than half a beat
        if (Math.Abs(d) <= InTimeSeconds)
            return new(true, 0, $"{track.Name} is already in time with {reference.Name}.{tags}", confidence);

        long frames = s.SecondsToFrames(d);
        if (s.SecondsToFrames(track.TimelineOffsetSeconds) + frames < 0)
            frames += s.SecondsToFrames(period);                    // no room before 0:00: on to the next beat instead
        return new(true, frames, $"Slid {track.Name} {Describe(frames, s)} to land its beats on {reference.Name}'s.{tags}", confidence);
    }

    /// <summary>Makes the move <see cref="Plan"/> worked out, and tells the engine.</summary>
    public static long Slide(TrackModel track, long frames, ProjectSession s)
    {
        long moved = TimelineEditor.SlideTrack(track, frames, s);
        s.Apply(track);
        return moved;
    }

    /// <summary>
    /// The beat of <paramref name="track"/> nearest <paramref name="atSeconds"/> of the timeline, as
    /// a timeline position, read from the section under that moment or the nearest section, over
    /// <see cref="WindowSeconds"/> of its audio. The tempo comes back in the track's own clock.
    /// </summary>
    public static BeatPhaseResult BeatNear(TrackModel track, double atSeconds, ProjectSession s)
    {
        double trackSeconds = TimelineEditor.TrackSecondsAt(track, atSeconds);
        var clip = TimelineEditor.ClipAt(track, trackSeconds, s)
                   ?? track.Clips.MinBy(c => Math.Min(Math.Abs(c.TrackStartSeconds - trackSeconds),
                                                      Math.Abs(c.TrackStartSeconds + TimelineEditor.LengthOf(c, s) - trackSeconds)));
        if (clip is null || s.AudioFor(clip) is not { } audio) return new(atSeconds, 0, track.Bpm);

        double length = TimelineEditor.LengthOf(clip, s);
        double inClip = Math.Clamp(trackSeconds - clip.TrackStartSeconds, 0, length);     // where in the section to look
        double from = Math.Max(0, inClip - WindowSeconds / 2);
        double to = Math.Min(length, from + WindowSeconds);
        from = Math.Max(0, to - WindowSeconds);
        long srcStart = s.SecondsToFrames(Math.Max(0, clip.SourceStartSeconds) + from);
        long srcEnd = Math.Min(audio.FrameCount, s.SecondsToFrames(Math.Max(0, clip.SourceStartSeconds) + to));
        if (srcEnd - srcStart < s.SampleRate) return new(atSeconds, 0, track.Bpm);

        // Mono at half the rate: a beat is a drum, not a cymbal, and the comb runs at the same frames a second either way.
        int n = (int)((srcEnd - srcStart) / 2);
        var mono = new float[n];
        var samples = audio.Samples;
        for (int i = 0; i < n; i++)
        {
            long f = (srcStart + 2L * i) * 2;
            mono[i] = 0.25f * (samples[f] + samples[f + 1] + samples[f + 2] + samples[f + 3]);
        }
        var found = BeatPhase.Find(mono, s.SampleRate / 2, track.Bpm, inClip - from);
        double beatTrackSeconds = clip.TrackStartSeconds + from + found.Seconds;
        return new(TimelineEditor.TimelineSecondsAt(track, beatTrackSeconds), found.Confidence, found.Bpm);
    }

    private static string TagNote(TrackModel track, double measuredBpm)
        => Math.Abs(measuredBpm - track.Bpm) > TagTolerance * track.Bpm
            ? $" {track.Name} plays at {measuredBpm:F1} BPM here, not the {track.Bpm:F1} it says, so the two will drift apart again — check its tempo."
            : "";

    private static string Describe(long frames, ProjectSession s)
    {
        double ms = Math.Abs(frames) * 1000.0 / s.SampleRate;
        string amount = ms >= 100 ? $"{ms / 1000:0.00} s" : $"{ms:0.#} ms";
        return $"{amount} {(frames < 0 ? "earlier" : "later")}";
    }
}
