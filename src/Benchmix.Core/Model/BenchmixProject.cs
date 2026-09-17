using System.Text.Json.Serialization;
using Benchmix.Engine.Audio;
using Benchmix.Engine.Dsp;

namespace Benchmix.Core.Model;

/// <summary>
/// A saved mashup. Entirely non-destructive: nothing here is rendered audio, only a pointer to
/// each source file and the handful of numbers describing what to do with it. Loading a project
/// re-derives the sound from the originals, so a project file stays a few kilobytes no matter how
/// long the mix is, and the source files are never modified.
/// </summary>
public sealed class BenchmixProject
{
    /// <summary>
    /// 2 added <see cref="TrackModel.Clips"/>: a track holds a list of sections rather than one.
    /// A version 1 file migrates to a single clip covering what the track used to cover, which is
    /// arithmetically identical, so old projects open and sound the same.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "Untitled";
    public int SampleRate { get; set; } = 48000;
    public double MasterGain { get; set; } = 1.0;
    public bool LimiterEnabled { get; set; } = true;
    public EqMode EqMode { get; set; } = EqMode.Isolator;

    public double? LoopStartSeconds { get; set; }
    public double? LoopEndSeconds { get; set; }

    /// <summary>Tempo the whole mashup is built around; tracks are matched to this.</summary>
    public double ProjectBpm { get; set; }

    public List<TrackModel> Tracks { get; set; } = new();

    [JsonIgnore]
    public bool HasSolo => Tracks.Any(t => t.Soloed);

    public BenchmixProject Clone()
    {
        return new BenchmixProject
        {
            SchemaVersion = SchemaVersion,
            Name = Name,
            SampleRate = SampleRate,
            MasterGain = MasterGain,
            LimiterEnabled = LimiterEnabled,
            EqMode = EqMode,
            LoopStartSeconds = LoopStartSeconds,
            LoopEndSeconds = LoopEndSeconds,
            ProjectBpm = ProjectBpm,
            Tracks = Tracks.Select(t => t.Clone()).ToList(),
        };
    }
}

/// <summary>
/// One section of audio on a track.
///
/// <para>
/// <see cref="TrackStartSeconds"/> is measured in the track's <b>own, unstretched</b> timebase,
/// not on the timeline. A clip's real timeline position is
/// <c>track.TimelineOffsetSeconds + TrackStartSeconds / track.Speed</c>. Holding it this way means
/// changing a track's tempo rescales its whole arrangement together — a loop cut on bar lines is
/// still a loop afterwards — and it keeps <c>Speed</c> out of the structural path entirely, so
/// turning the TEMPO knob never rebuilds the clip list.
/// </para>
/// </summary>
public sealed class ClipModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Which file this section comes from. Usually the track's own, but a section pasted from
    /// another song keeps pointing at that song.
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Where this section starts in the track's own unstretched timebase.</summary>
    public double TrackStartSeconds { get; set; }

    /// <summary>First second of the source file used.</summary>
    public double SourceStartSeconds { get; set; }

    /// <summary>One past the last second used; negative means play to the end of the file.</summary>
    public double SourceEndSeconds { get; set; } = -1;

    /// <summary>Fade in at this section's start. Zero still declicks a cut edge.</summary>
    public double FadeInSeconds { get; set; }

    /// <summary>Fade out at this section's end. Zero still declicks a cut edge.</summary>
    public double FadeOutSeconds { get; set; }

    public ClipModel Clone() => (ClipModel)MemberwiseClone();
}

/// <summary>One track: where its audio comes from, where it sits, and what is done to it.</summary>
public sealed class TrackModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>
    /// The track's own song. Clips normally come from here; one pasted from elsewhere does not,
    /// which is why the path that matters for loading audio is <see cref="ClipModel.SourcePath"/>.
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Where this track's origin sits on the timeline. Clips are placed relative to it.</summary>
    public double TimelineOffsetSeconds { get; set; }

    /// <summary>The sections this track plays, in order and never overlapping.</summary>
    public List<ClipModel> Clips { get; set; } = new();

    // ---- version 1 fields, read on load and then folded into Clips ----
    // Nullable so that once migrated they are omitted from the file rather than written as zeros.

    /// <summary>Version 1 only. Superseded by <see cref="ClipModel.SourceStartSeconds"/>.</summary>
    public double? SourceStartSeconds { get; set; }

    /// <summary>Version 1 only. Superseded by <see cref="ClipModel.SourceEndSeconds"/>.</summary>
    public double? SourceEndSeconds { get; set; }

    /// <summary>1.0 is the original tempo.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Pitch offset in semitones, independent of speed.</summary>
    public double Semitones { get; set; }

    public StretchMode Mode { get; set; } = StretchMode.Independent;
    public bool PreserveFormants { get; set; } = true;

    public double EqLow { get; set; } = 1.0;
    public double EqMid { get; set; } = 1.0;
    public double EqHigh { get; set; } = 1.0;

    /// <summary>Version 1 only. Superseded by <see cref="ClipModel.FadeInSeconds"/>.</summary>
    public double? FadeInSeconds { get; set; }

    /// <summary>Version 1 only. Superseded by <see cref="ClipModel.FadeOutSeconds"/>.</summary>
    public double? FadeOutSeconds { get; set; }

    public double Gain { get; set; } = 1.0;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Soloed { get; set; }

    public string ColorHex { get; set; } = "#3E7BFA";

    // ---- cached analysis, so a project reopens without re-reading every file ----
    public double Bpm { get; set; }
    public string? BpmSource { get; set; }
    public string? Key { get; set; }
    public string? Camelot { get; set; }
    public string? KeySource { get; set; }

    /// <summary>Original tempo before any speed change — what the match maths works from.</summary>
    [JsonIgnore]
    public double EffectiveBpm => Bpm * Speed;

    /// <summary>
    /// How far this track reaches in its own unstretched timebase, i.e. the end of its last clip.
    /// </summary>
    [JsonIgnore]
    public double TrackEndSeconds
    {
        get
        {
            double end = 0;
            foreach (var c in Clips)
                end = Math.Max(end, c.TrackStartSeconds + Math.Max(0, ClipLength(c)));
            return end;
        }
    }

    /// <summary>
    /// A clip's length in source seconds, or 0 when its end is unknown. A negative
    /// <see cref="ClipModel.SourceEndSeconds"/> means "to the end of the file", which only the
    /// session can resolve, so callers that need the real number ask it instead.
    /// </summary>
    private static double ClipLength(ClipModel c)
        => c.SourceEndSeconds < 0 ? 0 : c.SourceEndSeconds - c.SourceStartSeconds;

    /// <summary>
    /// Deep, because of <see cref="Clips"/>. MemberwiseClone alone would hand the undo snapshot the
    /// same list the live project is about to edit, so every "previous state" on the stack would
    /// silently track the present and undo would do nothing.
    /// </summary>
    public TrackModel Clone()
    {
        var copy = (TrackModel)MemberwiseClone();
        copy.Clips = Clips.Select(c => c.Clone()).ToList();
        return copy;
    }
}
