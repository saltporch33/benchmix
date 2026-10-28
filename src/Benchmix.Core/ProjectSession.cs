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
    public static ProjectSession CreateEmpty()
    {
        PlaybackEngine engine;
        try { engine = PlaybackEngine.Open(); }
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

    /// <summary>
    /// True when every file this track's sections read from is decoded and ready. An empty track
    /// reads from nothing, so it is ready — asking only after its own song would report every empty
    /// track as a missing file.
    /// </summary>
    public bool HasAllAudio(TrackModel track) => SourcePathsOf(track).All(_clips.ContainsKey);

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

    /// <summary>
    /// Adds a track with nothing on it: somewhere to paste or drag sections so that two can sound
    /// at once, or so one can get its own EQ. It has a voice from the start, so the first section
    /// put on it plays without anything else having to happen.
    /// </summary>
    public TrackModel AddEmptyTrack()
    {
        var model = new TrackModel { Name = NextTrackName(), ColorHex = NextColour() };
        Project.Tracks.Add(model);
        EnsureVoice(model);
        ApplyAll();
        return model;
    }

    /// <summary>
    /// A copy of a track, right below it (Daniel, 2026-10-27: "duplicate a track with the existing
    /// clips on it"): the same sections at the same places, the same knobs, mute, solo and LINK,
    /// the same tempo ramps and warp markers and the same origin, under its own name and colour.
    /// Its sections are its own — editing one side never touches the other — and it plays at once,
    /// since the song is already decoded.
    /// </summary>
    public (TrackModel? Copy, EditResult Result) DuplicateTrack(TrackModel source)
    {
        int at = Project.Tracks.IndexOf(source);
        if (at < 0) return (null, EditResult.No("That track is no longer in the project."));

        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        foreach (var c in copy.Clips) c.Id = Guid.NewGuid();
        copy.Name = CopyNameFor(source.Name);
        copy.ColorHex = NextColour();
        Project.Tracks.Insert(at + 1, copy);
        EnsureVoice(copy);
        ApplyAll();

        var with = new List<string> { copy.Clips.Count == 1 ? "its section" : $"its {copy.Clips.Count} sections", "knobs" };
        if (copy.Ramps.Count > 0) with.Add("tempo ramps");
        if (copy.Warps.Count > 0) with.Add("warp markers");
        string list = with.Count == 2 ? $"{with[0]} and {with[1]}" : string.Join(", ", with.Take(with.Count - 1)) + " and " + with[^1];
        string soloed = copy.Soloed ? " Both are soloed, so both play." : "";
        return (copy, new EditResult(true, $"Duplicated {source.Name} as \"{copy.Name}\", right below it, with {list}.{soloed} Ctrl+Z takes it away."));
    }

    /// <summary>"Bed copy", or "Bed copy 2" and on when that name is taken.</summary>
    private string CopyNameFor(string name)
    {
        var taken = Project.Tracks.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string first = CleanTrackName($"{name} copy") ?? "copy";
        if (!taken.Contains(first)) return first;
        for (int n = 2; ; n++)
        {
            string next = CleanTrackName($"{name} copy {n}") ?? $"copy {n}";
            if (!taken.Contains(next)) return next;
        }
    }

    /// <summary>The longest name a track keeps; a longer one is cut there.</summary>
    public const int MaximumTrackNameLength = 80;

    /// <summary>
    /// A typed track name, cleaned: tabs, line breaks and other control characters become spaces,
    /// the ends are trimmed, and it is cut at <see cref="MaximumTrackNameLength"/>. Null when nothing
    /// is left, because a track with no name is a lane nobody can tell apart in a status message.
    /// </summary>
    public static string? CleanTrackName(string? typed)
    {
        if (typed is null) return null;
        string name = new string(typed.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (name.Length > MaximumTrackNameLength)
        {
            int cut = char.IsHighSurrogate(name[MaximumTrackNameLength - 1]) ? MaximumTrackNameLength - 1 : MaximumTrackNameLength;
            name = name[..cut].TrimEnd();
        }
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Renames a track (Daniel, 2026-10-11). A blank name is refused and the old one kept; the same
    /// name again is not a change. Only the name changes — nothing is heard, nothing moves.
    /// </summary>
    public EditResult RenameTrack(TrackModel track, string? typed)
    {
        if (!Project.Tracks.Contains(track)) return EditResult.No("That track is no longer in the project.");
        if (CleanTrackName(typed) is not { } name) return EditResult.No($"A track needs a name — kept \"{track.Name}\".");
        if (name == track.Name) return EditResult.No($"Kept the name \"{track.Name}\".");
        string old = track.Name;
        track.Name = name;
        return new EditResult(true, $"Renamed \"{old}\" to \"{name}\".");
    }

    /// <summary>
    /// Moves a track up or down the list (Daniel, 2026-10-11). Only the order changes. Each voice is
    /// found by its track's id, so the engine is not touched and the mix plays on as it was. The
    /// order on screen is this order: Shift+click ranges and Match key read it.
    /// </summary>
    public EditResult MoveTrack(TrackModel track, int toIndex)
    {
        int from = Project.Tracks.IndexOf(track);
        if (from < 0) return EditResult.No("That track is no longer in the project.");
        int to = Math.Clamp(toIndex, 0, Project.Tracks.Count - 1);
        if (to == from) return EditResult.No($"{track.Name} is already there.");
        Project.Tracks.RemoveAt(from);
        Project.Tracks.Insert(to, track);
        return new EditResult(true, $"Moved {track.Name} {(to < from ? "up" : "down")}, to place {to + 1} of {Project.Tracks.Count}.");
    }

    /// <summary>
    /// The S button (Daniel, 2026-10-27). A click makes this the one track soloed and turns every
    /// other solo off; a click on the only soloed track turns it off. <paramref name="alongside"/>
    /// (Ctrl+click) adds this track to the ones already soloed, or takes it away from them, so two
    /// can still be heard together. The caller pushes the engine with <see cref="ApplyAll"/>.
    /// </summary>
    public EditResult Solo(TrackModel track, bool alongside)
    {
        if (!Project.Tracks.Contains(track)) return EditResult.No("That track is no longer in the project.");
        string muted = track.Muted ? $" {track.Name} is muted, so it stays silent: click its M to hear it." : "";

        if (alongside)
        {
            track.Soloed = !track.Soloed;
            int soloed = Project.Tracks.Count(t => t.Soloed);
            if (!track.Soloed)
                return new EditResult(true, soloed == 0
                    ? $"{track.Name} no longer soloed. Every track plays."
                    : $"{track.Name} no longer soloed. {soloed} soloed track{(soloed == 1 ? "" : "s")} still play.");
            return new EditResult(true, $"Soloed {track.Name} too: {soloed} tracks play.{muted}");
        }

        if (track.Soloed && Project.Tracks.All(t => t == track || !t.Soloed))
        {
            track.Soloed = false;
            return new EditResult(true, $"{track.Name} no longer soloed. Every track plays.");
        }

        foreach (var t in Project.Tracks) t.Soloed = t == track;
        return new EditResult(true, $"Soloed {track.Name}: only it plays. Ctrl+click another S to hear that one too.{muted}");
    }

    private string NextTrackName()
    {
        var taken = Project.Tracks.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = Project.Tracks.Count + 1; ; n++)
            if (!taken.Contains($"Track {n}")) return $"Track {n}";
    }

    /// <summary>Decodes a file into the shared cache if it is not there already.</summary>
    public async Task<AudioClip> EnsureAudioAsync(string path, CancellationToken cancel = default)
    {
        if (_clips.TryGetValue(path, out var cached)) return cached;
        var loaded = await Task.Run(() =>
        {
            var c = AudioClip.Load(path, SampleRate, null, cancel);
            c.EnsurePeaks();
            return c;
        }, cancel);
        _clips[path] = loaded;
        return loaded;
    }

    /// <summary>
    /// Puts a whole audio file on an existing track at a timeline position, overwriting what is
    /// under it — pasting a file rather than a section. On an empty track the file becomes the
    /// track's song, with its own tempo and key; on any other it is a section from another song and
    /// takes that track's tempo, pitch and EQ, like any cross-song paste.
    /// </summary>
    public async Task<EditResult> PlaceFileAsync(TrackModel track, string path, double timelineSeconds,
                                                 CancellationToken cancel = default)
    {
        // Loading takes a moment, and in that moment the track can be removed, or an undo can swap
        // the whole project for an earlier copy. Carrying on regardless gave the removed track a
        // voice again — audio in every render with no track on screen to mute it.
        await EnsureAudioAsync(path, cancel);
        if (!Project.Tracks.Contains(track)) return EditResult.No("That track went away while the file was loading.");

        // Refuse before changing anything: a file too short to place must not rename the empty
        // track and take it over, or later pastes onto it would no longer adopt their song.
        var whole = new ClipModel { SourcePath = path };
        if (TimelineEditor.LengthOf(whole, this) < TimelineEditor.MinimumSectionSeconds)
            return EditResult.No($"{Path.GetFileName(path)} is too short to place.");

        TrackAnalysis? analysis = null;
        if (TimelineEditor.IsEmptyDeck(track))
        {
            analysis = await Task.Run(() => Analyzer.Analyze(path, cancel), cancel);
            if (!Project.Tracks.Contains(track)) return EditResult.No("That track went away while the file was loading.");
        }

        // Checked again after the analysis: something may have been put on it meanwhile, and then
        // it is not an empty track any more — the file is a section from another song on it.
        if (analysis is not null && TimelineEditor.IsEmptyDeck(track))
        {
            track.SourcePath = path;
            track.Bpm = analysis.Bpm;
            track.BpmSource = analysis.BpmSource.ToString();
            track.Key = analysis.Key;
            track.Camelot = analysis.Camelot;
            track.KeySource = analysis.KeySource.ToString();
            if (TimelineEditor.IsAutoName(track.Name))
                track.Name = analysis.Title ?? Path.GetFileNameWithoutExtension(path);
            if (Project.ProjectBpm <= 0 && analysis.Bpm > 0) Project.ProjectBpm = analysis.Bpm;
        }

        var result = TimelineEditor.Paste(track, new ClipModel { SourcePath = path }, timelineSeconds, this);
        EnsureVoice(track);
        Apply(track);
        return result;
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

    /// <summary>
    /// The tempo map a track's voice plays: from its origin, in engine frames, at speeds the engine
    /// can play. With no tempo ramp, the one speed — exactly what setting Speed used to give it.
    /// </summary>
    public TempoMap EngineTempoOf(TrackModel model)
    {
        double v0 = Math.Clamp(model.Speed, TempoMap.LowestSpeed, TempoMap.HighestSpeed);
        long origin = SecondsToFrames(model.TimelineOffsetSeconds);
        var tempo = model.Ramps.Count == 0
            ? TempoMap.Constant(v0)
            : TempoMap.Lenient(v0, model.Ramps.Select(r => new TempoMap.Ramp(
                SecondsToFrames(r.StartSeconds) - origin, SecondsToFrames(r.EndSeconds) - origin, r.ToSpeed)));
        if (model.Warps.Count == 0) return tempo;
        // Warp markers, in frames: both coordinates exactly as far in frames as they are in seconds,
        // not rounded to a frame, so this is the editor's map scaled and both keep or drop the same
        // markers — a project saved at one rate and opened at another would otherwise round a marker
        // at the spread limit just past it here and play the track unwarped (review, 2026-10-18).
        // An unmoved marker's tempo is that very number: seconds times the rate is a hair off a
        // whole number for many frames, and a hair would count as moved and put an untouched track
        // through the stretcher. With none moved this is the tempo map itself.
        return TempoMap.LenientWarped(tempo, model.Warps.Select(w =>
        {
            double audio = w.TrackSeconds * SampleRate;
            return new TempoMap.Pin(audio, w.TrackSeconds == w.TempoSeconds ? audio : w.TempoSeconds * SampleRate);
        }));
    }

    internal TrackModel AddSeparatedTrack(TrackModel source, string path, string name, string colour)
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
            Ramps = source.Ramps.ToList(),       // the same origin, so the very same tempo map
            Warps = source.Warps.ToList(),       // and the same warp: a stem must stay on its song's beats
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

        // The sections are rebuilt only when they have actually changed. Apply runs on every pixel
        // of a knob drag, and swapping in a new timeline drops the voice's binding to the section
        // it is playing — which would re-prime the vocoder sixty times a second while someone
        // nudges the TEMPO knob.
        var next = BuildTimeline(model);

        // Origin, sections and tempo go to the engine in one step. A ramped track's tempo is
        // measured from its origin, so a block that saw one of them without the others — a paste
        // before the origin, mid-playback — would play the wrong audio for a block.
        v.Place(SecondsToFrames(model.TimelineOffsetSeconds), v.Timeline.Matches(next) ? v.Timeline : next, EngineTempoOf(model));
        v.Mode = model.Mode;
        v.Semitones = model.Semitones;
        v.PreserveFormants = model.PreserveFormants;

        v.Eq.Mode = Project.EqMode;
        v.Eq.SetGains(model.EqLow, model.EqMid, model.EqHigh);
        v.Gain = model.Gain;
        v.Pan = model.Pan;

        v.Muted = !IsHeard(model);
    }

    /// <summary>
    /// True when the track plays in the mix — in the speakers and in a render alike. Solo is a
    /// project-level idea: soloing one track silences the others, and an explicit mute still wins
    /// over being soloed.
    /// </summary>
    public bool IsHeard(TrackModel track) => !track.Muted && (!Project.HasSolo || track.Soloed);

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

    /// <summary>
    /// Renders what can be heard (<see cref="IsHeard"/>), ending where the last audible track ends.
    /// </summary>
    public Task<RenderResult> RenderAsync(string path, RenderOptions options,
                                          IProgress<double>? progress = null,
                                          CancellationToken cancel = default)
    {
        // Renders on a worker so the UI keeps painting. Playback is stopped by the caller first;
        // the graph is not safe to pull from two threads at once.
        return Task.Run(() => Renderer.Render(Graph, path, options, progress, cancel), cancel);
    }

    /// <summary>
    /// The tracks with sections on them that a render would leave out, each with why: "muted", or
    /// "not soloed" while another track is. Empty when the render is the whole project.
    /// </summary>
    public List<(TrackModel Track, string Why)> LeftOutOfRender()
        => Project.Tracks.Where(t => t.Clips.Count > 0 && !IsHeard(t))
                         .Select(t => (t, t.Muted ? "muted" : "not soloed")).ToList();

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
