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
    /// 3 added <see cref="TrackModel.Ramps"/>, tempo ramps (2026-10-11). An older build refuses a
    /// version 3 file rather than opening it, dropping its ramps without a word and saving over them.
    /// 4 added <see cref="TrackModel.Warps"/>, warp markers (2026-10-18), for the same reason.
    /// 5 added <see cref="Tabs"/> (2026-10-27, Daniel: tabs "like an excel workbook", each its own
    /// arrangement): the tracks, the project tempo and the listening loop moved into a tab. An older
    /// build refuses a version 5 file rather than opening only one of its tabs and saving over the rest.
    /// 6 added <see cref="ClipModel.CrossfadeSeconds"/> (2026-10-28, Daniel: "the crossfade thingy
    /// when im splicing a clip into another one… a very narrow tall X"): a chosen crossfade length
    /// at a splice. An older build refuses a version 6 file rather than opening it with every splice
    /// back at 5 ms and saving over the lengths.
    /// </summary>
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "Untitled";
    public int SampleRate { get; set; } = 48000;
    public double MasterGain { get; set; } = 1.0;
    public bool LimiterEnabled { get; set; } = true;
    public EqMode EqMode { get; set; } = EqMode.Isolator;

    /// <summary>
    /// The tabs (2026-10-27): separate arrangements in one file, each with its own tracks, project
    /// tempo and listening loop. Only the open one, <see cref="CurrentTab"/>, plays and renders.
    /// Never empty: a project always has at least one tab.
    /// </summary>
    public List<TabModel> Tabs { get; set; } = new() { new TabModel() };

    /// <summary>Which tab is open. Saved, so a project reopens on the tab it was left on.</summary>
    public int CurrentTabIndex { get; set; }

    /// <summary>
    /// Each song's fingerprint by its path (2026-10-28, Daniel's "if I ever rename the source sound
    /// files"): the file's size and a hash of its first 256 KB, taken when the song is added and at
    /// every save (<c>SourceRelinker</c>). When a song is not where the project says, a file next to
    /// the project with the same fingerprint is it, whatever it is called now. A lookup table only:
    /// an older Benchmix ignores it and loses nothing, so the file's version did not change for it.
    /// </summary>
    public Dictionary<string, string> SourceFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public TabModel CurrentTab
    {
        get
        {
            if (Tabs.Count == 0) Tabs.Add(new TabModel());
            CurrentTabIndex = Math.Clamp(CurrentTabIndex, 0, Tabs.Count - 1);
            return Tabs[CurrentTabIndex];
        }
    }

    // ---- the open tab's own, forwarded, so the editor reads and writes "the project's tracks" as
    // it always has. What is on screen, in the engine and in a render is the open tab. ----

    /// <summary>The open tab's tracks.</summary>
    [JsonIgnore]
    public List<TrackModel> Tracks { get => CurrentTab.Tracks; set => CurrentTab.Tracks = value; }

    /// <summary>The open tab's tempo: what the mashup on it is built around; tracks are matched to this.</summary>
    [JsonIgnore]
    public double ProjectBpm { get => CurrentTab.ProjectBpm; set => CurrentTab.ProjectBpm = value; }

    [JsonIgnore]
    public double? LoopStartSeconds { get => CurrentTab.LoopStartSeconds; set => CurrentTab.LoopStartSeconds = value; }

    [JsonIgnore]
    public double? LoopEndSeconds { get => CurrentTab.LoopEndSeconds; set => CurrentTab.LoopEndSeconds = value; }

    // ---- version 4 and earlier wrote these at the top of the file; read on load, then folded into
    // the one tab such a file becomes (ProjectStore.Migrate) and nulled, so they are not written again. ----

    [JsonPropertyName("tracks")]
    public List<TrackModel>? LegacyTracks { get; set; }

    [JsonPropertyName("projectBpm")]
    public double? LegacyProjectBpm { get; set; }

    [JsonPropertyName("loopStartSeconds")]
    public double? LegacyLoopStartSeconds { get; set; }

    [JsonPropertyName("loopEndSeconds")]
    public double? LegacyLoopEndSeconds { get; set; }

    /// <summary>Every track on every tab.</summary>
    [JsonIgnore]
    public IEnumerable<TrackModel> AllTracks => Tabs.SelectMany(t => t.Tracks);

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
            Tabs = Tabs.Select(t => t.Clone()).ToList(),
            CurrentTabIndex = CurrentTabIndex,
            SourceFingerprints = new Dictionary<string, string>(SourceFingerprints, StringComparer.OrdinalIgnoreCase),
        };
    }
}

/// <summary>
/// One tab (2026-10-27): an arrangement of its own — its tracks, the tempo they are matched to and
/// its listening loop. Like a sheet in a workbook: the tabs share a file, the master level and the
/// limiter, nothing else, and only the open one plays or renders.
/// </summary>
public sealed class TabModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Mix";
    public List<TrackModel> Tracks { get; set; } = new();

    /// <summary>Tempo this tab's mashup is built around; its tracks are matched to this.</summary>
    public double ProjectBpm { get; set; }

    public double? LoopStartSeconds { get; set; }
    public double? LoopEndSeconds { get; set; }

    /// <summary>Deep, for the same reason as <see cref="TrackModel.Clone"/>.</summary>
    public TabModel Clone() => new()
    {
        Id = Id,
        Name = Name,
        Tracks = Tracks.Select(t => t.Clone()).ToList(),
        ProjectBpm = ProjectBpm,
        LoopStartSeconds = LoopStartSeconds,
        LoopEndSeconds = LoopEndSeconds,
    };
}

/// <summary>
/// One section of audio on a track.
///
/// <para>
/// <see cref="TrackStartSeconds"/> is measured in the track's <b>own, unstretched</b> timebase,
/// not on the timeline. A clip's real timeline position is
/// <c>track.TimelineOffsetSeconds + TrackStartSeconds / track.Speed</c> on a track with no tempo ramp,
/// and in general <c>TimelineEditor.TimelineSecondsAt</c>. Holding it this way means
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

    /// <summary>
    /// How long the crossfade is where this section starts on the very frame the one before it ends
    /// (2026-10-28, Daniel: "the crossfade thingy… a very narrow tall X"). 0 is the standard 5 ms
    /// every such join has had since 2026-09-24; anything else is the length he picked from the
    /// lane's menu. It belongs to this section's start, and does nothing where that start is a seam,
    /// a gap, or has a fade of its own — the engine's rule (<c>ClipTimeline.ButtJoinAfter</c>), which
    /// <c>TimelineEditor.CrossfadeAt</c> repeats for the lane's X.
    /// </summary>
    public double CrossfadeSeconds { get; set; }

    /// <summary>
    /// Silenced in place (Daniel, 2026-10-28: "retain looped sections on timeline but gray them out
    /// to temporarily deactivate them"). The section keeps its place and its audio; the engine plays
    /// a gap where it is, and the lane draws it faded. Version 7 of the file: an older Benchmix
    /// would play it, so it refuses the file instead.
    /// </summary>
    public bool Muted { get; set; }

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

    /// <summary>
    /// Tempo ramps (Daniel, 2026-10-11): spans of the timeline over which the track's tempo goes in a
    /// straight line, in BPM, from whatever it plays at the start to <see cref="TempoRamp.ToSpeed"/>,
    /// and holds there after. In order, never overlapping. A ramp sits on the timeline, not on the
    /// audio: moving a section under it changes the tempo it plays at, and editing a ramp moves every
    /// later section on the track, as turning TEMPO does.
    /// </summary>
    public List<TempoRamp> Ramps { get; set; } = new();

    /// <summary>
    /// Warp markers (Daniel, 2026-10-18): each pins a point of the track's audio — a beat — to the
    /// moment it should be heard, and the audio between two markers is spread evenly to fit. They sit
    /// on the audio, in the track's own timebase, so turning TEMPO or adding a ramp carries them
    /// along with the beats they are on; but they belong to the track, not to a section, so a
    /// section dragged past one is warped by where it now is. In order, never closer than
    /// <see cref="Benchmix.Core.WarpMarkers.MinimumSeconds"/>. See <see cref="WarpMarker"/>.
    /// </summary>
    public List<WarpMarker> Warps { get; set; } = new();

    // ---- version 1 fields, read on load and then folded into Clips ----
    // Nullable so that once migrated they are omitted from the file rather than written as zeros.

    /// <summary>Version 1 only. Superseded by <see cref="ClipModel.SourceStartSeconds"/>.</summary>
    public double? SourceStartSeconds { get; set; }

    /// <summary>Version 1 only. Superseded by <see cref="ClipModel.SourceEndSeconds"/>.</summary>
    public double? SourceEndSeconds { get; set; }

    /// <summary>1.0 is the original tempo. On a track with tempo ramps, the tempo before the first one — the TEMPO knob.</summary>
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

    /// <summary>The tempo it plays at before any tempo ramp: the song's own BPM times the TEMPO knob.</summary>
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
        copy.Ramps = new List<TempoRamp>(Ramps);           // records: copying the list is deep enough
        copy.Warps = new List<WarpMarker>(Warps);
        return copy;
    }
}

/// <summary>
/// One tempo ramp (Daniel, 2026-10-11): from whatever the track plays at <paramref name="StartSeconds"/>
/// to <paramref name="ToSpeed"/> at <paramref name="EndSeconds"/>, in a straight line — straight in
/// BPM, since BPM is the song's tempo times the speed — then holding there. Timeline seconds, on
/// whole frames. The start's tempo is never stored, so the tempo can never jump where a ramp begins.
/// </summary>
public sealed record TempoRamp(double StartSeconds, double EndSeconds, double ToSpeed);

/// <summary>
/// One warp marker (Daniel, 2026-10-18): the audio at <paramref name="TrackSeconds"/> — a point of the
/// track's own timebase, as <see cref="ClipModel.TrackStartSeconds"/>, on a whole frame — plays at the
/// moment the track's tempo (the TEMPO knob and its ramps, before any warp) has carried it to
/// <paramref name="TempoSeconds"/>. A marker nobody has moved has the two equal, and changes nothing.
/// Between two markers the audio is spread evenly; after the last, it plays at its tempo again.
/// </summary>
public sealed record WarpMarker(double TrackSeconds, double TempoSeconds);
