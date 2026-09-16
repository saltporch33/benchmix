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
    public const int CurrentSchemaVersion = 1;

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

/// <summary>One track: where its audio comes from, where it sits, and what is done to it.</summary>
public sealed class TrackModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";

    /// <summary>Where the clip starts on the timeline.</summary>
    public double TimelineOffsetSeconds { get; set; }

    /// <summary>Trim into the source file.</summary>
    public double SourceStartSeconds { get; set; }

    /// <summary>Trim out of the source file; negative means play to the end.</summary>
    public double SourceEndSeconds { get; set; } = -1;

    /// <summary>1.0 is the original tempo.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Pitch offset in semitones, independent of speed.</summary>
    public double Semitones { get; set; }

    public StretchMode Mode { get; set; } = StretchMode.Independent;
    public bool PreserveFormants { get; set; } = true;

    public double EqLow { get; set; } = 1.0;
    public double EqMid { get; set; } = 1.0;
    public double EqHigh { get; set; } = 1.0;

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

    public TrackModel Clone() => (TrackModel)MemberwiseClone();
}
