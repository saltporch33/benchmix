using Benchmix.Core.Model;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;

namespace Benchmix.Core;

/// <summary>
/// Ties a saved <see cref="BenchmixProject"/> to a live <see cref="PlaybackEngine"/>: loads the
/// audio a project refers to, keeps each <see cref="TrackVoice"/> matching its
/// <see cref="TrackModel"/>, and renders.
///
/// The model is the truth and the voices follow it. Nothing reads state back out of the audio
/// engine, so there is exactly one place a value can live and saving can never miss a change
/// someone made directly to a voice.
/// </summary>
public sealed class ProjectSession : IDisposable
{
    private readonly Dictionary<Guid, TrackVoice> _voices = new();
    private readonly Dictionary<string, AudioClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private ProjectSession(PlaybackEngine engine, BenchmixProject project)
    {
        Engine = engine;
        Project = project;
    }

    public PlaybackEngine Engine { get; }
    public BenchmixProject Project { get; private set; }
    public MixGraph Graph => Engine.Graph;
    public int SampleRate => Engine.SampleRate;

    /// <summary>Raised when a track finishes loading, so the UI can draw its waveform.</summary>
    public event EventHandler<TrackModel>? TrackLoaded;

    /// <summary>Raised when a track could not be loaded. The UI shows it; nothing throws at it.</summary>
    public event EventHandler<(TrackModel Track, string Message)>? TrackFailed;

    /// <summary>
    /// Opens the audio device and starts an empty project. If no device can be opened — none
    /// present, all of them in use, a driver in a bad mood — this falls back to an offline engine
    /// rather than failing: you can still build and render a mashup, just not hear it live.
    /// </summary>
    public static ProjectSession CreateEmpty(string? deviceId = null)
    {
        PlaybackEngine engine;
        try { engine = PlaybackEngine.Open(deviceId); }
        catch { engine = PlaybackEngine.OpenOffline(); }

        var project = new BenchmixProject { SampleRate = engine.SampleRate };
        return new ProjectSession(engine, project);
    }

    /// <summary>An engine-only session with no audio device, for tests and headless rendering.</summary>
    public static ProjectSession CreateOffline(int sampleRate = 48000)
    {
        var engine = PlaybackEngine.OpenOffline(sampleRate);
        return new ProjectSession(engine, new BenchmixProject { SampleRate = sampleRate });
    }

    public AudioClip? ClipFor(TrackModel track)
        => _clips.TryGetValue(track.SourcePath, out var c) ? c : null;

    public TrackVoice? VoiceFor(TrackModel track)
        => _voices.TryGetValue(track.Id, out var v) ? v : null;

    /// <summary>Returns this track's voice, creating an empty one if it does not have one yet.</summary>
    public TrackVoice EnsureVoice(TrackModel track)
    {
        if (_voices.TryGetValue(track.Id, out var existing)) return existing;
        var voice = Graph.AddTrack();
        _voices[track.Id] = voice;
        return voice;
    }

    public long SecondsToFrames(double seconds) => (long)Math.Round(seconds * SampleRate);
    public double FramesToSeconds(long frames) => frames / (double)SampleRate;

    /// <summary>
    /// Adds a file as a new track: decodes it, analyses tempo and key, and places it at
    /// <paramref name="atSeconds"/>.
    /// </summary>
    public async Task<TrackModel> AddTrackAsync(string path, double atSeconds = 0,
                                                CancellationToken cancel = default)
    {
        var analysis = await Task.Run(() => Analyzer.Analyze(path, cancel), cancel).ConfigureAwait(false);

        var model = new TrackModel
        {
            Name = analysis.Title ?? Path.GetFileNameWithoutExtension(path),
            SourcePath = path,
            TimelineOffsetSeconds = Math.Max(0, atSeconds),
            Bpm = analysis.Bpm,
            BpmSource = analysis.BpmSource.ToString(),
            Key = analysis.Key,
            Camelot = analysis.Camelot,
            KeySource = analysis.KeySource.ToString(),
            ColorHex = NextColour(),
        };

        Project.Tracks.Add(model);
        if (Project.ProjectBpm <= 0 && analysis.Bpm > 0) Project.ProjectBpm = analysis.Bpm;

        await LoadAudioAsync(model, cancel).ConfigureAwait(false);
        return model;
    }

    /// <summary>Decodes a track's audio (shared between tracks pointing at the same file) and wires its voice.</summary>
    public async Task LoadAudioAsync(TrackModel model, CancellationToken cancel = default)
    {
        try
        {
            if (!_clips.TryGetValue(model.SourcePath, out var clip))
            {
                clip = await Task.Run(() => AudioClip.Load(model.SourcePath, SampleRate, null, cancel), cancel)
                                 .ConfigureAwait(false);
                _clips[model.SourcePath] = clip;
            }

            var voice = EnsureVoice(model);
            voice.SetClip(clip);
            Apply(model);
            TrackLoaded?.Invoke(this, model);
        }
        catch (OperationCanceledException) { throw; }
        catch (ClipLoadException ex)
        {
            TrackFailed?.Invoke(this, (model, ex.Message));
        }
        catch (Exception ex)
        {
            TrackFailed?.Invoke(this, (model, $"Unexpected error loading this file: {ex.Message}"));
        }
    }

    /// <summary>Pushes one track's settings into its voice. Cheap; safe to call on every knob move.</summary>
    public void Apply(TrackModel model)
    {
        if (!_voices.TryGetValue(model.Id, out var v)) return;

        v.TimelineOffsetFrames = SecondsToFrames(model.TimelineOffsetSeconds);
        v.SourceStartFrame = SecondsToFrames(model.SourceStartSeconds);
        v.SourceEndFrame = model.SourceEndSeconds < 0 ? -1 : SecondsToFrames(model.SourceEndSeconds);

        v.Mode = model.Mode;
        v.Speed = model.Speed;
        v.Semitones = model.Semitones;
        v.PreserveFormants = model.PreserveFormants;

        v.Eq.SetGains(model.EqLow, model.EqMid, model.EqHigh);
        v.Gain = model.Gain;
        v.Pan = model.Pan;

        // Solo is a project-level idea: soloing one track mutes the others, and an explicit mute
        // still wins over being soloed.
        bool anySolo = Project.HasSolo;
        v.Muted = model.Muted || (anySolo && !model.Soloed);
    }

    public void ApplyAll()
    {
        foreach (var t in Project.Tracks) Apply(t);
        Graph.MasterGain = Project.MasterGain;
        Graph.LimiterEnabled = Project.LimiterEnabled;
        Engine.LoopRegion = Project is { LoopStartSeconds: { } s, LoopEndSeconds: { } e } && e > s
            ? (SecondsToFrames(s), SecondsToFrames(e))
            : null;
    }

    public void RemoveTrack(TrackModel model)
    {
        if (_voices.Remove(model.Id, out var v)) Graph.RemoveTrack(v);
        Project.Tracks.Remove(model);
        ReleaseUnusedClips();
        ApplyAll();
    }

    /// <summary>Loads a project's audio, replacing whatever is open.</summary>
    public async Task OpenAsync(BenchmixProject project, CancellationToken cancel = default)
    {
        Graph.Clear();
        _voices.Clear();
        _clips.Clear();

        Project = project;
        foreach (var t in project.Tracks)
            await LoadAudioAsync(t, cancel).ConfigureAwait(false);

        ApplyAll();
        Engine.Seek(0);
    }

    /// <summary>
    /// Sets <paramref name="track"/>'s speed so it lands on <paramref name="targetBpm"/>, folding
    /// half/double-time readings into the same octave first, and returns what it did.
    /// </summary>
    public (double Speed, double FromBpm) MatchTempo(TrackModel track, double targetBpm)
    {
        if (track.Bpm <= 0 || targetBpm <= 0) return (track.Speed, track.Bpm);
        double from = Analyzer.FoldToNearestOctave(track.Bpm, targetBpm);
        track.Speed = Math.Clamp(Analyzer.SpeedToMatch(from, targetBpm), 0.25, 4.0);
        Apply(track);
        return (track.Speed, from);
    }

    /// <summary>
    /// Suggests a pitch shift so <paramref name="track"/> sits harmonically with
    /// <paramref name="reference"/>. Returns null when either key is unknown.
    /// </summary>
    public int? SuggestSemitones(TrackModel track, TrackModel reference)
        => Keys.SemitonesBetween(track.Key, reference.Key);

    public Task<RenderResult> RenderAsync(string path, RenderOptions options,
                                          IProgress<double>? progress = null,
                                          CancellationToken cancel = default)
    {
        // Renders on a worker so the UI keeps painting. Playback is stopped by the caller first;
        // the graph is not safe to pull from two threads at once.
        return Task.Run(() => Renderer.Render(Graph, path, options, progress, cancel), cancel);
    }

    private void ReleaseUnusedClips()
    {
        var used = Project.Tracks.Select(t => t.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _clips.Keys.Where(k => !used.Contains(k)).ToList())
            _clips.Remove(key);
    }

    private static readonly string[] Palette =
    {
        "#3E7BFA", "#12A594", "#F76B15", "#8E4EC6", "#E93D82", "#30A46C", "#F5C518", "#E5484D",
    };

    private string NextColour() => Palette[Project.Tracks.Count % Palette.Length];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Engine.Dispose();
        _clips.Clear();
        _voices.Clear();
    }
}
