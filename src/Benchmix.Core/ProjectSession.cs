using Benchmix.Core.Model;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;
using Benchmix.Engine.Stems;

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

    /// <summary>
    /// The decoded audio behind one section. A section pasted from another song points at that
    /// song, so this is the lookup that matters for drawing and for rendering; the track's own
    /// <see cref="TrackModel.SourcePath"/> only says which song the track is nominally about.
    /// </summary>
    public AudioClip? AudioFor(ClipModel clip)
        => _clips.TryGetValue(clip.SourcePath, out var c) ? c : null;

    /// <summary>Every distinct file a track's sections read from.</summary>
    public static IEnumerable<string> SourcePathsOf(TrackModel track)
        => track.Clips.Select(c => c.SourcePath)
                      .Append(track.SourcePath)
                      .Where(p => !string.IsNullOrEmpty(p))
                      .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a track's sections into what the engine plays. Positions convert straight across:
    /// <see cref="ClipModel.TrackStartSeconds"/> is already in the track's own unstretched
    /// timebase, which is the same clock the engine uses.
    /// </summary>
    private ClipTimeline BuildTimeline(TrackModel model)
    {
        var clips = new List<VoiceClip>(model.Clips.Count);
        foreach (var c in model.Clips)
        {
            var audio = AudioFor(c);
            if (audio is null) continue;
            clips.Add(new VoiceClip
            {
                Audio = audio,
                TrackStartFrame = SecondsToFrames(Math.Max(0, c.TrackStartSeconds)),
                SourceStartFrame = SecondsToFrames(Math.Max(0, c.SourceStartSeconds)),
                SourceEndFrame = c.SourceEndSeconds < 0 ? -1 : SecondsToFrames(c.SourceEndSeconds),
                FadeInSeconds = Math.Max(0, c.FadeInSeconds),
                FadeOutSeconds = Math.Max(0, c.FadeOutSeconds),
            });
        }
        return new ClipTimeline(clips);
    }

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

    /// <summary>
    /// Bytes of decoded audio currently held. Clips live in RAM so scrubbing is free, which is a
    /// good trade at two or three songs and a bad one at a two-hour DJ set — so the UI can see the
    /// number and say something before the machine starts swapping.
    /// </summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            long total = 0;
            foreach (var c in _clips.Values) total += (long)c.Samples.Length * sizeof(float);
            return total;
        }
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
        var analysis = await Task.Run(() => Analyzer.Analyze(path, cancel), cancel);

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
        model.Clips.Add(new ClipModel { SourcePath = path });

        Project.Tracks.Add(model);
        if (Project.ProjectBpm <= 0 && analysis.Bpm > 0) Project.ProjectBpm = analysis.Bpm;

        await LoadAudioAsync(model, cancel);
        return model;
    }

    /// <summary>Where separated stems are written, so they behave like any other source file.</summary>
    public static string StemDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Benchmix", "stems");

    /// <summary>
    /// Splits a track into vocals and instrumental and adds both as new tracks, inheriting the
    /// original's tempo, key, position and speed so they line up with it immediately.
    ///
    /// The stems are written to disk as WAVs rather than held only in memory: that way a saved
    /// project can reopen them, and they can be dragged into anything else.
    /// </summary>
    public async Task<(TrackModel Vocals, TrackModel Instrumental)> SplitStemsAsync(
        TrackModel source, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var clip = ClipFor(source) ?? throw new InvalidOperationException("That track's audio is not loaded.");

        var result = await StemSeparator.SeparateAsync(clip, useGpu: true, progress, cancel)
                                             ;

        string stem = SanitiseFileName(Path.GetFileNameWithoutExtension(source.SourcePath));
        var vocalsPath = Path.Combine(StemDirectory, $"{stem} - vocals.wav");
        var instPath = Path.Combine(StemDirectory, $"{stem} - instrumental.wav");

        await Task.Run(() =>
        {
            result.Primary.SaveWav(vocalsPath);
            result.Secondary.SaveWav(instPath);
        }, cancel);

        var vocals = AddSeparatedTrack(source, vocalsPath, $"{source.Name} — vocals", "#E93D82");
        var instrumental = AddSeparatedTrack(source, instPath, $"{source.Name} — instrumental", "#12A594");

        // The stems are already decoded; reuse them rather than reading the WAVs back.
        _clips[vocalsPath] = AudioClip.FromSamples(vocalsPath, result.Primary.Samples, clip.SampleRate);
        _clips[instPath] = AudioClip.FromSamples(instPath, result.Secondary.Samples, clip.SampleRate);
        _clips[vocalsPath].EnsurePeaks();
        _clips[instPath].EnsurePeaks();

        EnsureVoice(vocals);
        EnsureVoice(instrumental);

        // The original is muted rather than removed: the stems replace it in the mix, but keeping
        // it means one click undoes the whole idea if the separation is not good enough.
        source.Muted = true;
        ApplyAll();

        TrackLoaded?.Invoke(this, vocals);
        TrackLoaded?.Invoke(this, instrumental);
        LastStemResult = result;
        return (vocals, instrumental);
    }

    /// <summary>Details of the most recent separation, for the UI to report.</summary>
    public StemResult? LastStemResult { get; private set; }

    private TrackModel AddSeparatedTrack(TrackModel source, string path, string name, string colour)
    {
        var model = new TrackModel
        {
            Name = name,
            SourcePath = path,
            ColorHex = colour,
            // Inherit everything that positions it against the original, so the stems drop straight
            // into place instead of needing to be re-matched.
            TimelineOffsetSeconds = source.TimelineOffsetSeconds,
            Speed = source.Speed,
            Semitones = source.Semitones,
            Mode = source.Mode,
            PreserveFormants = source.PreserveFormants,
            Bpm = source.Bpm,
            BpmSource = source.BpmSource,
            Key = source.Key,
            Camelot = source.Camelot,
            KeySource = source.KeySource,
        };

        // The stem covers exactly what the original covered, section for section, so the two land
        // on top of each other without being re-matched.
        foreach (var c in source.Clips)
        {
            var copy = c.Clone();
            copy.Id = Guid.NewGuid();
            copy.SourcePath = path;
            model.Clips.Add(copy);
        }
        if (model.Clips.Count == 0) model.Clips.Add(new ClipModel { SourcePath = path });

        Project.Tracks.Add(model);
        return model;
    }

    private static string SanitiseFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }

    /// <summary>Decodes a track's audio (shared between tracks pointing at the same file) and wires its voice.</summary>
    public async Task LoadAudioAsync(TrackModel model, CancellationToken cancel = default)
    {
        try
        {
            // Every file this track's sections read from, which after a cross-song paste is more
            // than one. Missing audio would otherwise show as a section that silently never plays.
            foreach (var path in SourcePathsOf(model))
            {
                if (_clips.ContainsKey(path)) continue;
                var loaded = await Task.Run(() =>
                {
                    var c = AudioClip.Load(path, SampleRate, null, cancel);
                    // Build the waveform envelope here, on the worker. Left to the UI thread it
                    // would run on first paint — a ~100 ms scan of the whole track, right when the
                    // window is trying to draw.
                    c.EnsurePeaks();
                    return c;
                }, cancel);
                _clips[path] = loaded;
            }

            EnsureVoice(model);
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

        // The sections are rebuilt only when they have actually changed. Apply runs on every pixel
        // of a knob drag, and swapping in a new timeline drops the voice's binding to the section
        // it is playing — which would re-prime the vocoder sixty times a second while someone
        // nudges the TEMPO knob.
        var next = BuildTimeline(model);
        if (!v.Timeline.Matches(next)) v.SetTimeline(next);

        v.Mode = model.Mode;
        v.Speed = model.Speed;
        v.Semitones = model.Semitones;
        v.PreserveFormants = model.PreserveFormants;

        v.Eq.Mode = Project.EqMode;
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

    /// <summary>
    /// Loads a project's audio, replacing whatever is open.
    ///
    /// <para>
    /// Already-decoded audio is deliberately kept. Undo and redo come through here, and an undo of
    /// a section edit changes no audio at all — dropping the cache would re-decode every song from
    /// disk to put a clip back where it was, which on a four-minute file is a multi-second stall on
    /// every Ctrl+Z. Anything the new project no longer refers to is released afterwards.
    /// </para>
    /// </summary>
    public async Task OpenAsync(BenchmixProject project, CancellationToken cancel = default)
    {
        Graph.Clear();
        _voices.Clear();

        Project = project;
        foreach (var t in project.Tracks)
            await LoadAudioAsync(t, cancel);

        ReleaseUnusedClips();
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
        var used = Project.Tracks.SelectMany(SourcePathsOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
