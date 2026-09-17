using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Benchmix.Core;
using Benchmix.Core.Model;
using Benchmix.Core.Persistence;
using Benchmix.Engine.Analysis;
using Benchmix.Engine.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Benchmix.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _tick;
    private readonly UndoStack _undo = new();
    private ProjectSession? _session;
    private string? _projectPath;
    private bool _dirty;

    // The renderer pulls blocks from the same MixGraph the audio device pulls from. Two threads
    // doing that at once would interleave their reads and corrupt both. Playback is therefore
    // locked out for the duration of a render.
    private bool _rendering;

    // Separation is GPU-bound and memory-hungry; two at once would fight for both.
    private bool _splitting;

    public MainViewModel()
    {
        Tracks = new ObservableCollection<TrackViewModel>();

        // 30 Hz is enough for a playhead to look smooth and costs nothing.
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _tick.Tick += OnTick;
    }

    public ObservableCollection<TrackViewModel> Tracks { get; }

    public ProjectSession Session => _session ??= ProjectSession.CreateEmpty();

    [ObservableProperty] private TrackViewModel? _selectedTrack;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _totalSeconds;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _pixelsPerSecond = 28;
    [ObservableProperty] private double _scrollSeconds;
    [ObservableProperty] private double _projectBpm;
    [ObservableProperty] private double _masterGain = 1.0;
    [ObservableProperty] private float _masterLevelLeft;
    [ObservableProperty] private float _masterLevelRight;
    [ObservableProperty] private bool _masterClipped;
    [ObservableProperty] private string _status = "Ready.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _busyProgress;
    [ObservableProperty] private double? _loopStart;
    [ObservableProperty] private double? _loopEnd;
    [ObservableProperty] private string _windowTitle = "Benchmix";

    /// <summary>
    /// Width of the lane area in pixels, pushed in by the window on layout. Zoom-to-fit and the
    /// horizontal scrollbar both need the real number: assuming 1000 makes Fit leave the tail of a
    /// mix off-screen on a small window, and makes the scrollbar claim most of the mix is hidden
    /// when all of it is visible.
    /// </summary>
    [ObservableProperty] private double _laneWidthPx = 1000;

    public double TimelineWidth => Math.Max(600, (TotalSeconds + 30) * PixelsPerSecond);

    /// <summary>Seconds currently visible in the lane.</summary>
    public double ViewportSeconds => LaneWidthPx / Math.Max(0.01, PixelsPerSecond);

    /// <summary>Furthest the view can scroll before it runs past the end of the mix.</summary>
    public double MaxScrollSeconds => Math.Max(0, TotalSeconds + 5 - ViewportSeconds);

    /// <summary>Starts audio. Called once the window is up, so a device failure can be shown, not thrown.</summary>
    public void Initialise()
    {
        try
        {
            _ = Session;                          // opens the device
            Session.TrackLoaded += (_, model) => OnUi(() =>
            {
                var vm = Tracks.FirstOrDefault(t => t.Id == model.Id);
                vm?.NotifyAudioLoaded();
                RecomputeLength();
            });
            Session.TrackFailed += (_, e) => OnUi(() =>
            {
                var vm = Tracks.FirstOrDefault(t => t.Id == e.Track.Id);
                if (vm is not null) { vm.LoadError = e.Message; vm.IsLoaded = false; }
                Status = e.Message;
            });
            Session.Engine.PlaybackStoppedUnexpectedly += (_, _) => OnUi(() =>
            {
                IsPlaying = false;
                // Deliberately does NOT say "press play": once the stream is gone the engine
                // refuses to start again, so telling someone to press play sends them in circles.
                Status = "The audio device stopped — reconnect it and restart Benchmix to hear playback again. "
                       + "Your project is fine, and you can still edit and render.";
            });

            ProjectBpm = Session.Project.ProjectBpm;
            Status = Session.Engine.IsOffline
                ? "No audio output available. You can still load, edit and render files — you just will not hear them."
                : $"Audio ready on {Session.Engine.DeviceName} at {Session.SampleRate / 1000.0:0.#} kHz.";
            _tick.Start();
        }
        catch (Exception ex)
        {
            Status = $"No audio output available ({ex.Message}). You can still load and render files.";
        }
    }

    /// <summary>Instant attack, ~8 dB/second decay at the 30 Hz tick.</summary>
    private static float Ballistic(float current, float peak)
        => peak >= current ? peak : Math.Max(peak, current * 0.90f);

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_session is null) return;

        PositionSeconds = Session.Engine.PositionSeconds;
        IsPlaying = Session.Engine.State == TransportState.Playing && !Session.Engine.DeviceLost;

        // Dispose any voice the render thread has finished with since a track was removed.
        Session.Graph.CollectRetired();

        // Meter ballistics: jump to a new peak instantly, fall away slowly. Showing the raw
        // per-block peak would flicker far too fast to read anything from.
        var m = Session.Graph.Meters;
        MasterLevelLeft = Ballistic(MasterLevelLeft, m.MasterPeakLeft);
        MasterLevelRight = Ballistic(MasterLevelRight, m.MasterPeakRight);
        MasterClipped = m.MasterClipped;

        // Read the peak off each track's own voice. Indexing TrackPeak by row number is wrong the
        // moment any track failed to load, because a failed track has no voice and every meter
        // after it shows a different track's audio.
        foreach (var t in Tracks)
        {
            float peak = Session.VoiceFor(t.Model)?.Peak ?? 0f;
            t.MeterLevel = Ballistic(t.MeterLevel, peak);
        }
    }

    // ---------------- transport ----------------

    [RelayCommand]
    private void PlayPause()
    {
        if (_session is null) return;
        if (_rendering) { Status = "Still rendering — playback resumes when it finishes."; return; }
        Session.Engine.TogglePlay();
        IsPlaying = Session.Engine.State == TransportState.Playing;
    }

    [RelayCommand]
    private void Stop()
    {
        if (_session is null) return;
        if (_rendering) { Status = "Still rendering — the transport resumes when it finishes."; return; }
        Session.Engine.Stop();
        Session.Engine.Seek(0);
        PositionSeconds = 0;
        IsPlaying = false;
    }

    public void SeekTo(double seconds)
    {
        if (_session is null || _rendering) return;
        Session.Engine.SeekSeconds(Math.Max(0, seconds));
        PositionSeconds = Math.Max(0, seconds);
    }

    /// <summary>
    /// Moves the playhead and plays from there. Already playing means carry on from the new spot
    /// rather than stop — double-clicking around the timeline is how you audition, and pausing on
    /// every other double-click would make that useless.
    /// </summary>
    public void PlayFrom(double seconds)
    {
        if (_session is null) return;
        if (_rendering) { Status = "Still rendering — playback resumes when it finishes."; return; }

        SeekTo(seconds);
        if (Session.Engine.State != TransportState.Playing) Session.Engine.TogglePlay();
        IsPlaying = Session.Engine.State == TransportState.Playing;
        Status = IsPlaying ? $"Playing from {seconds:0.00}s." : "Nothing to play yet.";
    }

    [RelayCommand]
    private void ClearLoop()
    {
        LoopStart = null;
        LoopEnd = null;
        if (_session is null) return;
        Session.Project.LoopStartSeconds = null;
        Session.Project.LoopEndSeconds = null;
        Session.Engine.LoopRegion = null;
        Status = "Loop cleared.";
    }

    public void SetLoop(double a, double b)
    {
        double s = Math.Max(0, Math.Min(a, b)), e = Math.Max(a, b);
        if (e - s < 0.05) { ClearLoop(); return; }

        LoopStart = s;
        LoopEnd = e;
        if (_session is null) return;
        Session.Project.LoopStartSeconds = s;
        Session.Project.LoopEndSeconds = e;
        Session.Engine.LoopRegion = (Session.SecondsToFrames(s), Session.SecondsToFrames(e));
        Status = $"Looping {s:0.0}s to {e:0.0}s.";
    }

    [RelayCommand]
    private void ZoomIn() => PixelsPerSecond = Math.Min(400, PixelsPerSecond * 1.35);

    [RelayCommand]
    private void ZoomOut() => PixelsPerSecond = Math.Max(2, PixelsPerSecond / 1.35);

    [RelayCommand]
    private void ZoomFit()
    {
        if (TotalSeconds <= 0) return;
        // Measure the lane rather than assuming one; "Fit" that leaves the last minute off the
        // right edge is worse than no Fit at all.
        double lane = LaneWidthPx > 50 ? LaneWidthPx : 1000;
        PixelsPerSecond = Math.Clamp(lane / (TotalSeconds * 1.02), 2, 400);
        ScrollSeconds = 0;
    }

    // ---------------- tracks ----------------

    [RelayCommand]
    private async Task AddTracksAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add audio",
            Multiselect = true,
            Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.wma;*.ogg;*.opus;*.aiff;*.aif|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        await AddFilesAsync(dlg.FileNames);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths, double atSeconds = 0)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;

        IsBusy = true;
        try
        {
            int i = 0;
            foreach (var path in list)
            {
                Status = $"Loading {Path.GetFileName(path)}...";
                BusyProgress = i / (double)list.Count;

                PushUndo($"add {Path.GetFileName(path)}");
                var model = await Session.AddTrackAsync(path, atSeconds);

                var vm = NewTrackViewModel(model);
                // The session raises TrackLoaded/TrackFailed while AddTrackAsync is still running —
                // before this row exists — so those handlers cannot find it and their state is lost.
                // Settle the row here instead, from what the session actually ended up holding.
                if (Session.ClipFor(model) is not null) vm.NotifyAudioLoaded();
                else vm.LoadError = $"Could not load {Path.GetFileName(path)}.";

                Tracks.Add(vm);
                SelectedTrack ??= vm;

                if (ProjectBpm <= 0 && model.Bpm > 0) ProjectBpm = model.Bpm;
                i++;
            }

            RecomputeLength();

            double gb = Session.EstimatedMemoryBytes / (1024.0 * 1024 * 1024);
            string added = list.Count == 1 ? $"Added {Path.GetFileName(list[0])}." : $"Added {list.Count} tracks.";
            Status = gb >= 2.0
                ? $"{added} Heads up: {gb:0.1} GB of audio is loaded — Benchmix keeps whole songs in memory so scrubbing stays instant."
                : added;

            MarkDirty();
        }
        catch (Exception ex)
        {
            Status = $"Could not add that file: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyProgress = 0;
        }
    }

    [RelayCommand]
    private void RemoveTrack(TrackViewModel? track)
    {
        track ??= SelectedTrack;
        if (track is null || _session is null) return;

        PushUndo($"remove {track.Name}");
        Session.RemoveTrack(track.Model);
        Tracks.Remove(track);
        if (SelectedTrack == track) SelectedTrack = Tracks.FirstOrDefault();
        RecomputeLength();
        MarkDirty();
        Status = $"Removed {track.Name}.";
    }

    /// <summary>Stretches a track to the project tempo and says by how much.</summary>
    [RelayCommand]
    private void MatchTempo(TrackViewModel? track)
    {
        track ??= SelectedTrack;
        if (track is null || _session is null) return;

        double target = ProjectBpm > 0 ? ProjectBpm : track.OriginalBpm;
        if (target <= 0 || track.OriginalBpm <= 0)
        {
            Status = "That track has no tempo to match — set one by hand.";
            return;
        }

        PushUndo($"match {track.Name} to {target:F1} BPM");
        var (speed, from) = Session.MatchTempo(track.Model, target);
        track.Speed = speed;
        RecomputeLength();
        MarkDirty();

        string folded = Math.Abs(from - track.OriginalBpm) > 0.01 ? $" (read as {from:F1}, half/double-time)" : "";
        Status = $"{track.Name}: {(speed - 1) * 100:+0.0;-0.0}% to hit {target:F1} BPM{folded}.";
    }

    /// <summary>Suggests and applies a pitch shift so a track sits with the project's key reference.</summary>
    [RelayCommand]
    private void MatchKey(TrackViewModel? track)
    {
        track ??= SelectedTrack;
        if (track is null || _session is null) return;

        var reference = Tracks.FirstOrDefault(t => t != track);
        if (reference is null) { Status = "Add a second track to match against."; return; }

        var semis = Session.SuggestSemitones(track.Model, reference.Model);
        if (semis is null)
        {
            Status = $"Can't suggest a shift — {(track.Model.Key is null ? track.Name : reference.Name)} has no key.";
            return;
        }

        PushUndo($"match {track.Name} key");
        track.LinkPitchToTempo = false;
        track.Semitones = semis.Value;
        MarkDirty();

        string caveat = track.KeyIsEstimate || reference.KeyIsEstimate
            ? "  Both keys aren't confirmed, so trust your ears over this number."
            : "";
        Status = $"{track.Name}: {Keys.Describe(track.Model.Key, reference.Model.Key)} — shifted {semis.Value:+0;-0;0}.{caveat}";
    }

    /// <summary>
    /// Splits a track into vocals and instrumental, adding both as new tracks and muting the
    /// original. Runs on the GPU, takes roughly twenty seconds per song, and downloads a 64 MB
    /// model the first time only.
    /// </summary>
    [RelayCommand]
    private async Task SplitStemsAsync(TrackViewModel? track)
    {
        track ??= SelectedTrack;
        if (track is null || _session is null) return;
        if (!track.IsLoaded) { Status = "That track's audio has not finished loading."; return; }
        if (_splitting) { Status = "Already separating a track — one at a time."; return; }

        bool firstRun = !Benchmix.Engine.Stems.StemSeparator.IsModelInstalled;
        if (firstRun)
        {
            var ok = MessageBox.Show(
                """
                Splitting a song into vocals and instrumental needs a 64 MB model, downloaded once.

                It runs on your GPU, on this machine. Nothing is uploaded anywhere.

                Download it now?
                """,
                "Benchmix", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) { Status = "Stem separation cancelled."; return; }
        }

        _splitting = true;
        IsBusy = true;
        PushUndo($"split {track.Name}");
        Status = firstRun ? "Downloading the separation model..." : $"Separating {track.Name}...";

        try
        {
            var progress = new Progress<double>(p => BusyProgress = p);
            var (vocals, instrumental) = await Session.SplitStemsAsync(track.Model, progress);

            foreach (var m in new[] { vocals, instrumental })
            {
                var vm = NewTrackViewModel(m);
                vm.NotifyAudioLoaded();
                Tracks.Add(vm);
            }
            track.RefreshFromModel();          // it was muted by the split

            var r = Session.LastStemResult;
            Status = r is null
                ? $"Split {track.Name} into vocals and instrumental."
                : $"Split {track.Name} in {r.Seconds:0.0}s on {r.ExecutionProvider}. "
                  + $"The vocal stem is band-limited to {r.BandLimitHz / 1000:0.0} kHz — that is the model, not a bug.";

            RecomputeLength();
            MarkDirty();
        }
        catch (Exception ex)
        {
            Status = $"Separation failed: {ex.Message}";
        }
        finally
        {
            _splitting = false;
            IsBusy = false;
            BusyProgress = 0;
        }
    }

    [RelayCommand]
    private void ResetTrack(TrackViewModel? track)
    {
        track ??= SelectedTrack;
        if (track is null) return;

        PushUndo($"reset {track.Name}");
        track.Speed = 1;
        track.Semitones = 0;
        track.EqLow = track.EqMid = track.EqHigh = 1;
        track.Gain = 1;
        MarkDirty();
        Status = $"{track.Name} back to its original tempo, pitch and EQ.";
    }

    // ---------------- project ----------------

    [RelayCommand]
    private async Task NewAsync()
    {
        if (!ConfirmDiscard()) return;
        if (_session is null) return;

        // Go through OpenAsync so the graph, the voices, the decoded clips and the loop region are
        // all released. Clearing the collections in place leaves the audio in memory and the old
        // loop still wrapping playback in a project that no longer has one.
        await Session.OpenAsync(new BenchmixProject { SampleRate = Session.SampleRate });
        Tracks.Clear();
        LoopStart = null;
        LoopEnd = null;
        MasterGain = 1.0;
        _projectPath = null;
        _undo.Clear();
        _dirty = false;
        ProjectBpm = 0;
        RecomputeLength();
        UpdateTitle();
        Status = "New project.";
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (!ConfirmDiscard()) return;

        var dlg = new OpenFileDialog { Title = "Open project", Filter = ProjectStore.FileFilter };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            var result = ProjectStore.Load(dlg.FileName);
            await Session.OpenAsync(result.Project);

            Tracks.Clear();
            int missing = 0;
            foreach (var m in result.Project.Tracks)
            {
                var vm = NewTrackViewModel(m);
                if (Session.ClipFor(m) is not null) vm.NotifyAudioLoaded();
                else
                {
                    // The file moved or was deleted. Say so on the track rather than presenting an
                    // empty lane as if the project opened cleanly.
                    vm.LoadError = $"Missing: {m.SourcePath}";
                    missing++;
                }
                Tracks.Add(vm);
            }

            _projectPath = dlg.FileName;
            ProjectBpm = result.Project.ProjectBpm;
            MasterGain = result.Project.MasterGain;
            LoopStart = result.Project.LoopStartSeconds;
            LoopEnd = result.Project.LoopEndSeconds;
            SelectedTrack = Tracks.FirstOrDefault();

            _undo.Clear();
            _dirty = false;
            RecomputeLength();
            UpdateTitle();
            Status = missing > 0
                ? $"Opened {Path.GetFileName(dlg.FileName)}, but {missing} track{(missing == 1 ? "'s audio is" : "s' audio is")} missing — those lanes are empty and will render as silence."
                : result.UsedFallback ? result.Note! : $"Opened {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not open that project: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Save()
    {
        if (_projectPath is null) { SaveAs(); return; }
        SaveTo(_projectPath);
    }

    [RelayCommand]
    private void SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save project",
            Filter = ProjectStore.FileFilter,
            FileName = (Session.Project.Name is { Length: > 0 } n ? n : "Mashup") + ProjectStore.Extension,
        };
        if (dlg.ShowDialog() != true) return;
        SaveTo(dlg.FileName);
    }

    private void SaveTo(string path)
    {
        try
        {
            Session.Project.Name = Path.GetFileNameWithoutExtension(path);
            Session.Project.ProjectBpm = ProjectBpm;
            Session.Project.MasterGain = MasterGain;
            Session.Project.LoopStartSeconds = LoopStart;
            Session.Project.LoopEndSeconds = LoopEnd;

            ProjectStore.Save(Session.Project, path, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            _projectPath = path;
            _dirty = false;
            UpdateTitle();
            Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RenderAsync()
    {
        if (_session is null || Tracks.Count == 0) { Status = "Nothing to render yet."; return; }

        var dlg = new SaveFileDialog
        {
            Title = "Render mashup",
            Filter = "WAV, 24-bit (*.wav)|*.wav|WAV, 16-bit (*.wav)|*.wav|WAV, 32-bit float (*.wav)|*.wav",
            FileName = (Session.Project.Name is { Length: > 0 } n ? n : "Mashup") + ".wav",
        };
        if (dlg.ShowDialog() != true) return;

        var format = dlg.FilterIndex switch
        {
            2 => RenderFormat.Pcm16,
            3 => RenderFormat.Float32,
            _ => RenderFormat.Pcm24,
        };

        bool wasPlaying = IsPlaying;
        Session.Engine.Pause();
        // Pausing stops the next block; this waits out the one already in flight, so the renderer
        // never pulls the graph at the same moment the audio device is.
        Session.Engine.WaitForIdle();
        long resume = Session.Engine.PositionFrames;

        _rendering = true;
        IsBusy = true;
        Status = "Rendering...";
        try
        {
            var progress = new Progress<double>(p => BusyProgress = p);
            var options = new RenderOptions
            {
                Format = format,
                TailSeconds = 2.0,
                ApplyLimiter = Session.Project.LimiterEnabled,
            };
            var result = await Session.RenderAsync(dlg.FileName, options, progress);

            double peakDb = result.PeakBefore > 1e-6 ? 20 * Math.Log10(result.PeakBefore) : -120;
            Status = $"Rendered {result.Seconds:0.0}s to {Path.GetFileName(result.Path)} in {result.ElapsedSeconds:0.0}s, "
                   + $"peaking at {peakDb:+0.0;-0.0} dBFS"
                   + (result.Clipped
                        ? " — the limiter pulled that down. Lower the master if you would rather it did not."
                        : ".");
        }
        catch (Exception ex)
        {
            Status = $"Render failed: {ex.Message}";
        }
        finally
        {
            _rendering = false;
            IsBusy = false;
            BusyProgress = 0;
            Session.Engine.Seek(resume);
            if (wasPlaying) Session.Engine.Play();
        }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_session is null) return;
        var restored = _undo.Undo(Session.Project);
        if (restored is null) { Status = "Nothing to undo."; return; }
        _ = ReloadFromProjectAsync(restored, "Undone.");
    }

    [RelayCommand]
    private void Redo()
    {
        if (_session is null) return;
        var restored = _undo.Redo(Session.Project);
        if (restored is null) { Status = "Nothing to redo."; return; }
        _ = ReloadFromProjectAsync(restored, "Redone.");
    }

    private async Task ReloadFromProjectAsync(BenchmixProject project, string message)
    {
        IsBusy = true;
        try
        {
            await Session.OpenAsync(project);
            Tracks.Clear();
            foreach (var m in project.Tracks)
            {
                var vm = NewTrackViewModel(m);
                if (Session.ClipFor(m) is not null) vm.NotifyAudioLoaded();
                else vm.LoadError = $"Missing: {m.SourcePath}";
                Tracks.Add(vm);
            }
            SelectedTrack = Tracks.FirstOrDefault();
            ProjectBpm = project.ProjectBpm;
            MasterGain = project.MasterGain;
            RecomputeLength();
            Status = message;
        }
        finally { IsBusy = false; }
    }

    // ---------------- sections: split, cut, copy, paste ----------------

    /// <summary>
    /// The section commands all act on one selected section, not on a time range.
    ///
    /// <para>
    /// A time range would have meant reusing the loop region, which is a <i>transport</i> gesture —
    /// shift-dragging the ruler to audition a passage would then also be arming a destructive one.
    /// Selecting the block you can already see and click is both smaller to learn and impossible
    /// to trigger by accident, and it costs one extra keystroke: split at each end, click the
    /// middle, then repeat.
    /// </para>
    /// </summary>
    [ObservableProperty] private ClipViewModel? _selectedClip;

    private ClipModel? _clipboard;

    /// <summary>True when something has been copied and can be pasted.</summary>
    public bool HasClipboard => _clipboard is not null;

    /// <summary>Selects one section, clearing any other. Null clears the selection entirely.</summary>
    public void SelectClip(ClipViewModel? clip)
    {
        foreach (var t in Tracks)
            foreach (var c in t.Clips)
                if (c.IsSelected && !ReferenceEquals(c, clip)) c.IsSelected = false;

        if (clip is not null) clip.IsSelected = true;
        SelectedClip = clip;
        foreach (var t in Tracks) t.NotifyClipsChanged();
    }

    /// <summary>The section a command should act on, falling back to the one under the playhead.</summary>
    private ClipViewModel? TargetClip()
    {
        if (SelectedClip is not null && Tracks.Contains(SelectedClip.Track)) return SelectedClip;
        if (SelectedTrack is null) return null;
        return SelectedTrack.Clips.FirstOrDefault(
            c => PositionSeconds >= c.TimelineStartSeconds && PositionSeconds < c.TimelineEndSeconds);
    }

    /// <summary>Republishes a track after its sections changed, and reports what happened.</summary>
    private void AfterClipEdit(TrackViewModel track, EditResult result, ClipModel? select = null)
    {
        if (!result.Changed) { Status = result.Message; return; }

        Session.Apply(track.Model);
        track.RebuildClips();
        RecomputeLength();
        MarkDirty();

        var chosen = select is null ? null : track.Clips.FirstOrDefault(c => c.Id == select.Id);
        SelectClip(chosen);
        Status = result.Message;
    }

    [RelayCommand]
    private void SplitAtPlayhead()
    {
        if (_session is null || SelectedTrack is not { } track) { Status = "Select a track first."; return; }

        PushUndo($"{track.Name}: split");
        var result = TimelineEditor.Split(track.Model, PositionSeconds, Session);
        AfterClipEdit(track, result, result.Clip);
    }

    [RelayCommand]
    private void CopyClip()
    {
        if (TargetClip() is not { } clip) { Status = "Click a section to copy it."; return; }
        _clipboard = TimelineEditor.Copy(clip.Model);
        OnPropertyChanged(nameof(HasClipboard));
        Status = $"Copied {clip.LengthSeconds:0.0}s. Ctrl+V drops it at the playhead.";
    }

    [RelayCommand]
    private void CutClip()
    {
        if (TargetClip() is not { } clip) { Status = "Click a section to cut it."; return; }

        _clipboard = TimelineEditor.Copy(clip.Model);
        OnPropertyChanged(nameof(HasClipboard));

        var track = clip.Track;
        PushUndo($"{track.Name}: cut section");
        var result = TimelineEditor.Delete(track.Model, clip.Model);
        AfterClipEdit(track, result with { Message = $"Cut {clip.LengthSeconds:0.0}s. The gap is left as it is." });
    }

    [RelayCommand]
    private void DeleteClip()
    {
        if (TargetClip() is not { } clip) { Status = "Click a section to delete it."; return; }

        var track = clip.Track;
        PushUndo($"{track.Name}: delete section");
        AfterClipEdit(track, TimelineEditor.Delete(track.Model, clip.Model));
    }

    [RelayCommand]
    private void PasteClip()
    {
        if (_session is null) return;
        if (_clipboard is null) { Status = "Nothing has been copied yet."; return; }
        if (SelectedTrack is not { } track) { Status = "Select a track to paste onto."; return; }

        PushUndo($"{track.Name}: paste section");
        var result = TimelineEditor.Paste(track.Model, _clipboard, PositionSeconds, Session);
        AfterClipEdit(track, result, result.Clip);
    }

    [RelayCommand]
    private void DuplicateClip()
    {
        if (_session is null) return;
        if (TargetClip() is not { } clip) { Status = "Click a section to repeat it."; return; }

        var track = clip.Track;
        PushUndo($"{track.Name}: repeat section");
        var result = TimelineEditor.Duplicate(track.Model, clip.Model, Session);

        // The copy becomes the selection, so holding Ctrl+D walks a loop along the timeline
        // instead of stacking every repeat on the same spot.
        AfterClipEdit(track, result, result.Clip);
    }

    /// <summary>
    /// Records the project state before a gesture. Continuous gestures — a knob drag, a clip drag —
    /// coalesce into one step: pass the same <paramref name="coalesceKey"/> for every event of the
    /// gesture and only the first takes a snapshot.
    /// </summary>
    public void PushUndo(string label, string? coalesceKey = null)
    {
        if (_session is null) return;
        if (coalesceKey is not null && coalesceKey == _lastCoalesceKey) return;

        _lastCoalesceKey = coalesceKey;
        _undo.Push(Session.Project, label);
    }

    /// <summary>Ends a gesture, so the next one of the same kind starts a fresh undo step.</summary>
    public void EndGesture() => _lastCoalesceKey = null;

    private string? _lastCoalesceKey;

    /// <summary>Builds a track view-model wired to undo, so knob moves are undoable like everything else.</summary>
    private TrackViewModel NewTrackViewModel(TrackModel model)
        => new(Session, model, MarkDirty, (label, key) => PushUndo(label, key));

    // ---------------- plumbing ----------------

    partial void OnMasterGainChanged(double value)
    {
        if (_session is null) return;
        Session.Graph.MasterGain = value;
        Session.Project.MasterGain = value;
        MarkDirty();
    }

    partial void OnProjectBpmChanged(double value)
    {
        // NaN or infinity here would propagate into bar snapping and into the ruler's tick loop,
        // where the loop bound never terminates and the window hangs for good.
        if (!double.IsFinite(value) || value < 0)
        {
            Status = "That tempo is not a number.";
            ProjectBpm = 0;
            return;
        }
        if (value > 400) { ProjectBpm = 400; return; }

        if (_session is null) return;
        Session.Project.ProjectBpm = value;
        MarkDirty();
    }

    partial void OnPixelsPerSecondChanged(double value) => NotifyViewportChanged();
    partial void OnTotalSecondsChanged(double value) => NotifyViewportChanged();
    partial void OnLaneWidthPxChanged(double value) => NotifyViewportChanged();

    partial void OnScrollSecondsChanged(double value)
    {
        double max = MaxScrollSeconds;
        if (value < 0) ScrollSeconds = 0;
        else if (value > max) ScrollSeconds = max;
    }

    private void NotifyViewportChanged()
    {
        OnPropertyChanged(nameof(TimelineWidth));
        OnPropertyChanged(nameof(ViewportSeconds));
        OnPropertyChanged(nameof(MaxScrollSeconds));
        if (ScrollSeconds > MaxScrollSeconds) ScrollSeconds = MaxScrollSeconds;
    }

    partial void OnSelectedTrackChanged(TrackViewModel? oldValue, TrackViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    public void RecomputeLength()
    {
        double max = 0;
        foreach (var t in Tracks)
            max = Math.Max(max, t.TimelineOffsetSeconds + t.TimelineDurationSeconds);
        TotalSeconds = max;
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
        RecomputeLength();
    }

    private void UpdateTitle()
    {
        string name = _projectPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(_projectPath);
        WindowTitle = $"Benchmix — {name}{(_dirty ? " *" : "")}";
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show("This project has unsaved changes. Discard them?", "Benchmix",
                                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return r == MessageBoxResult.OK;
    }

    public bool CanClose() => ConfirmDiscard();

    public void Dispose()
    {
        _tick.Stop();
        _session?.Dispose();
    }
}
