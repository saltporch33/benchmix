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

    private readonly bool _useAudioDevice;
    private readonly bool _interactive;

    /// <param name="useAudioDevice">
    /// False for a scripted check that does not need to be heard: it runs on the offline engine
    /// and never opens the speakers. Play, pause, stop, seek and render go through the same
    /// transport and the same graph, but nothing pulls the graph in real time, so the playhead
    /// does not move on its own while playing. A check that needs it to move needs the device.
    /// </param>
    /// <param name="interactive">
    /// False for a scripted run: no dialog of any kind — no file pickers, no "discard changes?",
    /// no download prompt. Someone else is using the machine, and a dialog would land on their
    /// screen and wait for a click nobody is going to make.
    /// </param>
    public MainViewModel(bool useAudioDevice = true, bool interactive = true)
    {
        _useAudioDevice = useAudioDevice;
        _interactive = interactive;
        Tracks = new ObservableCollection<TrackViewModel>();

        // 30 Hz is enough for a playhead to look smooth and costs nothing.
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _tick.Tick += OnTick;
    }

    public ObservableCollection<TrackViewModel> Tracks { get; }

    public ProjectSession Session => _session ??= _useAudioDevice ? ProjectSession.CreateEmpty() : ProjectSession.CreateOffline();

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

    // ---------------- moving a track up or down ----------------

    /// <summary>
    /// Moves a track to another place in the list (Daniel, 2026-10-11): the row on screen and the
    /// project together, as one undo step. The engine is not touched — nothing is heard to change.
    /// </summary>
    public bool MoveTrack(TrackViewModel track, int toIndex)
    {
        if (_session is null) return false;
        int from = Tracks.IndexOf(track);
        if (from < 0) return false;                                // a row left over from before an undo
        if (DragInProgress) { Status = "Let go of the section first."; return false; }
        if (IsBusy) { Status = "Wait for that to finish, then move the track."; return false; }
        int to = Math.Clamp(toIndex, 0, Tracks.Count - 1);
        if (to == from) { Status = $"{track.Name} left where it was."; return false; }

        PushUndo($"move {track.Name}");
        var result = Session.MoveTrack(track.Model, to);
        if (!result.Changed) { Status = result.Message; return false; }
        Tracks.Move(from, to);
        MarkDirty();
        Status = result.Message;
        return true;
    }

    // ---------------- renaming a track ----------------

    /// <summary>
    /// Opens a track's name for typing (Daniel, 2026-10-11): a double-click on the name, or the
    /// strip's right-click menu. One at a time — opening another keeps what was typed in the first.
    /// </summary>
    public bool BeginRename(TrackViewModel track)
    {
        if (_session is null || !Tracks.Contains(track)) return false;
        if (track.IsRenaming) return true;
        if (DragInProgress) { Status = "Let go of the section first."; return false; }
        if (IsBusy) { Status = "Wait for that to finish, then rename the track."; return false; }

        CommitOpenRenames();
        track.RenameDraft = track.Name;
        track.IsRenaming = true;
        Status = $"Type a new name for {track.Name}, then press Enter. Esc keeps the old one.";
        return true;
    }

    /// <summary>
    /// Keeps what was typed as the track's name: Enter, or clicking anywhere else. Found by id in
    /// the current project, so a box left over from before an undo writes nothing. A blank name, or
    /// the same one, changes nothing and costs no press of Ctrl+Z.
    /// </summary>
    public bool CommitRename(Guid trackId, string? typed)
    {
        var track = Tracks.FirstOrDefault(t => t.Id == trackId);
        if (track is null || !track.IsRenaming) return false;
        track.IsRenaming = false;

        string? name = ProjectSession.CleanTrackName(typed);
        if (name is null || name == track.Model.Name)
        {
            Status = name is null ? $"A track needs a name — kept \"{track.Name}\"." : $"Kept the name \"{track.Name}\".";
            return false;
        }

        PushUndo($"rename {track.Name}");
        var result = Session.RenameTrack(track.Model, typed);
        track.RefreshFromModel();
        MarkDirty();
        Status = result.Message;
        return result.Changed;
    }

    /// <summary>Esc: the name stays as it was.</summary>
    public void CancelRename(Guid trackId)
    {
        var track = Tracks.FirstOrDefault(t => t.Id == trackId);
        if (track is null || !track.IsRenaming) return;
        track.IsRenaming = false;
        Status = $"Name left as \"{track.Name}\".";
    }

    /// <summary>Keeps whatever is being typed in any track's name, as clicking elsewhere does.</summary>
    public void CommitOpenRenames()
    {
        foreach (var t in Tracks.Where(t => t.IsRenaming).ToList()) CommitRename(t.Id, t.RenameDraft);
    }

    /// <summary>
    /// What a click of the scroll wheel does (Daniel, 2026-10-11): stop, and go back to where play
    /// last started — a double-click, a seek, or Space after a pause — so Space plays the same
    /// stretch again. The Stop button still goes back to 0:00.
    /// </summary>
    public void StopAndReturn()
    {
        if (_session is null) return;
        if (_rendering) { Status = "Still rendering — the transport resumes when it finishes."; return; }
        Session.Engine.Stop();
        PositionSeconds = Session.Engine.PositionSeconds;
        IsPlaying = false;
        Status = $"Stopped, back at {PositionSeconds:0.00}s where it started playing.";
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
        if (!_interactive) { Status = "A scripted run shows no dialogs."; return; }
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
                if (Session.HasAllAudio(model)) vm.NotifyAudioLoaded();
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

        double speed = 1, from = 0;
        AsOneUndoStep($"match {track.Name} to {target:F1} BPM", () =>
        {
            (speed, from) = Session.MatchTempo(track.Model, target);
            track.Speed = speed;
        });
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

        AsOneUndoStep($"match {track.Name} key", () =>
        {
            track.LinkPitchToTempo = false;
            track.Semitones = semis.Value;
        });
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
        if (track.Model.Clips.Count == 0 || Session.ClipFor(track.Model) is null)
        {
            Status = "There is no song on this track to split.";
            return;
        }
        if (!track.IsLoaded) { Status = "That track's audio has not finished loading."; return; }
        if (_splitting) { Status = "Already separating a track — one at a time."; return; }

        bool firstRun = !Benchmix.Engine.Stems.StemSeparator.IsModelInstalled;
        if (firstRun)
        {
            if (!_interactive) { Status = "A scripted run shows no dialogs."; return; }
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

        AsOneUndoStep($"reset {track.Name}", () =>
        {
            track.Speed = 1;
            track.Semitones = 0;
            track.EqLow = track.EqMid = track.EqHigh = 1;
            track.Gain = 1;
        });
        MarkDirty();
        Status = $"{track.Name} back to its original tempo, pitch and EQ.";
    }

    // ---------------- project ----------------

    [RelayCommand]
    private async Task NewAsync()
    {
        CommitOpenRenames();
        if (!ConfirmDiscard()) return;
        if (_session is null) return;

        // Go through OpenAsync so the graph, the voices, the decoded clips and the loop region are
        // all released. Clearing the collections in place leaves the audio in memory and the old
        // loop still wrapping playback in a project that no longer has one.
        await Session.OpenAsync(new BenchmixProject { SampleRate = Session.SampleRate });
        Tracks.Clear();
        // The selection goes with the tracks. Left behind, it pointed at a track no longer on
        // screen: Ctrl+V and Ctrl+I reported success on it while nothing appeared, and songs added
        // afterwards never became selected because something already was.
        SelectClip(null);
        SelectedTrack = null;
        LoopStart = null;
        LoopEnd = null;
        MasterGain = 1.0;
        ProjectBpm = 0;                 // before _dirty is cleared: setting it marks the project changed
        _projectPath = null;
        _undo.Clear();
        _dirty = false;
        RecomputeLength();
        UpdateTitle();
        Status = "New project.";
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        CommitOpenRenames();
        if (!ConfirmDiscard()) return;
        if (!_interactive) { Status = "A scripted run shows no dialogs."; return; }

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
                if (Session.HasAllAudio(m)) vm.NotifyAudioLoaded();
                else
                {
                    // The file moved or was deleted. Say so on the track rather than presenting an
                    // empty lane as if the project opened cleanly.
                    vm.LoadError = $"Missing: {MissingFileOf(m)}";
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
        if (!_interactive) { Status = "A scripted run shows no dialogs."; return; }
        var dlg = new SaveFileDialog
        {
            Title = "Save project",
            Filter = ProjectStore.FileFilter,
            FileName = (Session.Project.Name is { Length: > 0 } n ? n : "Mashup") + ProjectStore.Extension,
        };
        if (dlg.ShowDialog() != true) return;
        SaveTo(dlg.FileName);
    }

    internal void SaveTo(string path)
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
        if (!_interactive) { Status = "A scripted run shows no dialogs."; return; }

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

    /// <summary>
    /// Rebuilds the view after undo or redo.
    ///
    /// <para>
    /// The selected track and section are carried across by id. Dropping back to the first track
    /// made every undo quietly move the paste target: undo a paste on track three, press Ctrl+V
    /// again, and it landed on track one.
    /// </para>
    /// </summary>
    private async Task ReloadFromProjectAsync(BenchmixProject project, string message)
    {
        // Every row is about to be rebuilt; a name half-typed into one of them goes with it, rather
        // than being written later into a track that is no longer in the project.
        foreach (var t in Tracks) t.IsRenaming = false;

        var keepTrack = SelectedTrack?.Id;
        var keepClip = SelectedClip?.Id;
        var keepSelected = SelectedClips.Select(c => c.Id).ToHashSet();

        // Undo changes the edit, not where you are listening: the playhead and the listening loop
        // stay as they are. Reloading rewinds to 0:00, so until 2026-10-03 every Ctrl+Z sent the
        // next split or paste to the start of the song, and a loop changed since the step being
        // undone came back in the engine while the band on screen still showed the new one.
        double at = PositionSeconds;
        long returnTo = Session.Engine.StopReturnFrame;     // re-seeking below would move it to here
        project.LoopStartSeconds = Session.Project.LoopStartSeconds;
        project.LoopEndSeconds = Session.Project.LoopEndSeconds;

        IsBusy = true;
        try
        {
            await Session.OpenAsync(project);
            SeekTo(at);
            Session.Engine.StopReturnFrame = returnTo;
            Tracks.Clear();
            foreach (var m in project.Tracks)
            {
                var vm = NewTrackViewModel(m);
                if (Session.HasAllAudio(m)) vm.NotifyAudioLoaded();
                else vm.LoadError = $"Missing: {MissingFileOf(m)}";
                Tracks.Add(vm);
            }
            SelectedTrack = Tracks.FirstOrDefault(t => t.Id == keepTrack) ?? Tracks.FirstOrDefault();
            SelectClip(Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == keepClip));
            foreach (var c in Tracks.SelectMany(t => t.Clips))
                if (keepSelected.Contains(c.Id)) c.IsSelected = true;      // the rest of a multi-selection
            foreach (var t in Tracks) t.NotifyClipsChanged();
            ProjectBpm = project.ProjectBpm;
            MasterGain = project.MasterGain;
            RecomputeLength();
            MarkDirty();                // an undo after saving is a change from what was saved
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

    /// <summary>
    /// The track the clipboard's section was copied off, as it was then. Pasted onto an empty
    /// track, the section takes this track's tempo and pitch, so it plays exactly as it did.
    /// </summary>
    private TrackModel? _clipboardFrom;

    /// <summary>
    /// Windows' clipboard counter at the moment of the last copy. If it has moved on and Windows
    /// now holds audio files, those were copied more recently than our section, so Ctrl+V pastes
    /// them — the most recent copy wins, wherever it was made.
    /// </summary>
    private uint _clipboardSequenceAtCopy;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>Windows' clipboard counter, exposed so the gesture test can prove it resolves in the published exe.</summary>
    public static uint ClipboardSequence() => GetClipboardSequenceNumber();

    /// <summary>True when something has been copied and can be pasted.</summary>
    public bool HasClipboard => _clipboard is not null;

    private void Remember(ClipViewModel clip)
    {
        _clipboard = TimelineEditor.Copy(clip.Model);
        _clipboardFrom = clip.Track.Model.Clone();
        try { _clipboardSequenceAtCopy = GetClipboardSequenceNumber(); } catch { _clipboardSequenceAtCopy = 0; }
        OnPropertyChanged(nameof(HasClipboard));
    }

    /// <summary>Selects one section, clearing any other. Null clears the selection entirely.</summary>
    public void SelectClip(ClipViewModel? clip)
    {
        foreach (var t in Tracks)
            foreach (var c in t.Clips)
                if (c.IsSelected && !ReferenceEquals(c, clip)) c.IsSelected = false;

        if (clip is not null) clip.IsSelected = true;
        SelectedClip = clip;
        _rangeAnchorId = clip?.Id;
        foreach (var t in Tracks) t.NotifyClipsChanged();
    }

    /// <summary>Every selected section, on every track. Usually one; Ctrl+click and Shift+click make more.</summary>
    public IReadOnlyList<ClipViewModel> SelectedClips
        => Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).ToList();

    /// <summary>
    /// Where a Shift+click measures from: the section last selected by a plain click, a Ctrl+click
    /// or a right-click. Kept by id, because the view-models are rebuilt after every edit.
    /// <see cref="SelectedClip"/> is a different thing — the section commands act on, which a
    /// Shift+click moves to the section just clicked while the anchor stays put.
    /// </summary>
    private Guid? _rangeAnchorId;

    /// <summary>
    /// Ctrl+click: flips one section in or out of the selection, leaving the rest as they are.
    /// The section just added becomes the target and the anchor; taking the target out hands both
    /// to another selected section.
    /// </summary>
    public void ToggleClipSelection(ClipViewModel clip)
    {
        clip.IsSelected = !clip.IsSelected;
        if (clip.IsSelected) { SelectedClip = clip; _rangeAnchorId = clip.Id; }
        else if (ReferenceEquals(SelectedClip, clip))
        {
            SelectedClip = SelectedClips.LastOrDefault();
            _rangeAnchorId = SelectedClip?.Id;
        }
        foreach (var t in Tracks) t.NotifyClipsChanged();
        ReportSelection();
    }

    /// <summary>
    /// Shift+click: selects every section between the anchor and this one — on every track from
    /// the one to the other, and only sections wholly inside the time they span, so a whole song
    /// on a neighbouring track is not swept up by two short pieces. The anchor stays where it was,
    /// so a further Shift+click measures from the same place; the section just clicked becomes
    /// the one commands act on. With nothing to measure from it is a plain click.
    /// </summary>
    public void SelectClipRange(ClipViewModel target)
    {
        var anchor = Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == _rangeAnchorId)
                     ?? (SelectedClip is { } s && Tracks.Contains(s.Track) ? SelectedClip : null);
        if (anchor is null) { SelectClip(target); return; }

        var range = SectionSelection.Range(Session.Project.Tracks,
                                           anchor.Track.Model, anchor.Model,
                                           target.Track.Model, target.Model, Session);
        var ids = range.Select(r => r.Clip.Id).ToHashSet();
        foreach (var t in Tracks)
            foreach (var c in t.Clips)
                c.IsSelected = ids.Contains(c.Id);
        SelectedClip = target;
        foreach (var t in Tracks) t.NotifyClipsChanged();
        ReportSelection();
    }

    /// <summary>Makes an already-selected section the one commands act on, keeping the rest selected.</summary>
    public void MakePrimary(ClipViewModel clip)
    {
        if (!clip.IsSelected) { SelectClip(clip); return; }
        SelectedClip = clip;
        _rangeAnchorId = clip.Id;
    }

    /// <summary>The sections a selection box over these rows and times touches, topmost track first.</summary>
    public IReadOnlyList<(TrackModel Track, ClipModel Clip)> SectionsInBox(int firstRow, int lastRow, double fromSeconds, double toSeconds)
        => SectionSelection.Touching(Tracks.Select(t => t.Model).ToList(), firstRow, lastRow, fromSeconds, toSeconds, Session);

    /// <summary>
    /// A selection box (Daniel, 2026-10-11): exactly what it touches, plus <paramref name="keep"/> —
    /// what was selected already, when Ctrl or Shift was held as it started. The first section it
    /// touches, earliest on the topmost track, becomes the one commands act on and where the next
    /// Shift+click measures from.
    /// </summary>
    public void SelectBand(IReadOnlySet<Guid> keep, IReadOnlyList<(TrackModel Track, ClipModel Clip)> touched)
    {
        var ids = new HashSet<Guid>(keep);
        foreach (var (_, c) in touched) ids.Add(c.Id);
        var all = Tracks.SelectMany(t => t.Clips).ToList();
        foreach (var c in all) c.IsSelected = ids.Contains(c.Id);

        var first = touched.Count > 0 ? all.FirstOrDefault(c => c.Id == touched[0].Clip.Id) : null;
        SelectedClip = first ?? (SelectedClip is { IsSelected: true } current && all.Contains(current)
                                     ? current : all.LastOrDefault(c => c.IsSelected));
        _rangeAnchorId = SelectedClip?.Id;
        foreach (var t in Tracks) t.NotifyClipsChanged();
    }

    /// <summary>The box was let go: say what it selected.</summary>
    public void FinishBand() => ReportSelection();

    private void ReportSelection()
    {
        var selected = SelectedClips;
        int tracks = selected.Select(c => c.Track).Distinct().Count();
        Status = selected.Count switch
        {
            0 => "Nothing selected.",
            1 => "1 section selected. Ctrl+click adds another; Shift+click selects a range.",
            _ => $"{selected.Count} sections selected on {tracks} track{(tracks == 1 ? "" : "s")}. Delete removes them; Ctrl+click adds or removes one.",
        };
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
        track.RefreshFromModel();
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
        Remember(clip);
        Status = $"Copied {clip.LengthSeconds:0.0}s. Click a track, then Ctrl+V drops it there at the playhead.";
    }

    [RelayCommand]
    private void CutClip()
    {
        if (DragInProgress) { Status = "Let go of the section first."; return; }
        if (TargetClip() is not { } clip) { Status = "Click a section to cut it."; return; }

        Remember(clip);

        var track = clip.Track;
        PushUndo($"{track.Name}: cut section");
        var result = TimelineEditor.Delete(track.Model, clip.Model);
        AfterClipEdit(track, result with { Message = $"Cut {clip.LengthSeconds:0.0}s. The gap is left as it is." });
    }

    [RelayCommand]
    private void DeleteClip() => DeleteSelection(fallBackToPlayhead: true);

    /// <summary>
    /// Deletes every selected section, as one undo step, leaving the gaps. The menu item falls
    /// back to the section under the playhead when nothing is selected; the Delete key does not,
    /// so a stray press with nothing selected removes nothing.
    /// </summary>
    public bool DeleteSelection(bool fallBackToPlayhead)
    {
        if (_session is null) return false;
        if (DragInProgress) { Status = "Let go of the section first."; return false; }

        var selected = SelectedClips.Where(c => Tracks.Contains(c.Track)).ToList();
        if (selected.Count == 0)
        {
            if (!fallBackToPlayhead || TargetClip() is not { } under) { Status = "Click a section to delete it."; return false; }
            selected.Add(under);
        }

        PushUndo(selected.Count == 1 ? $"{selected[0].Track.Name}: delete section" : $"delete {selected.Count} sections");
        var result = SectionSelection.DeleteAll(selected.Select(c => (c.Track.Model, c.Model)));
        if (!result.Changed) { Status = result.Message; return false; }

        foreach (var track in selected.Select(c => c.Track).Distinct())
        {
            Session.Apply(track.Model);
            track.RefreshFromModel();
            track.RebuildClips();
        }
        SelectedClip = null;
        foreach (var t in Tracks) t.NotifyClipsChanged();
        RecomputeLength();
        MarkDirty();
        Status = result.Message;
        return true;
    }

    /// <summary>
    /// Pastes onto the <b>selected track</b> at the <b>playhead</b> — always both, whatever section
    /// happens to be selected and wherever it came from.
    ///
    /// <para>
    /// If audio files were copied in Explorer more recently than anything was copied here, those
    /// are what gets pasted, each whole file end to end from the playhead.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task PasteClipAsync()
    {
        if (_session is null) return;

        var files = NewerAudioFilesOnClipboard();
        var track = SelectedTrack;
        if (track is null && Tracks.Count == 0 && (_clipboard is not null || files.Count > 0))
        {
            AddEmptyTrack();
            track = SelectedTrack;
        }
        if (track is null) { Status = "Click a track to paste onto."; return; }

        if (files.Count > 0) { await PlaceFilesAsync(track, files, PositionSeconds); return; }

        if (_clipboard is null) { Status = "Nothing has been copied yet."; return; }

        PushUndo($"{track.Name}: paste section");
        var result = TimelineEditor.Paste(track.Model, _clipboard, PositionSeconds, Session, _clipboardFrom);
        AfterClipEdit(track, result with { Message = $"{track.Name}: {result.Message}" }, result.Clip);
    }

    private static readonly string[] AudioExtensions =
        { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma", ".ogg", ".opus", ".aif", ".aiff", ".mp4", ".m4b" };

    public static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Audio files on Windows' clipboard that were put there after our last copy.</summary>
    private List<string> NewerAudioFilesOnClipboard()
    {
        try
        {
            if (_clipboard is not null && GetClipboardSequenceNumber() == _clipboardSequenceAtCopy) return new();
            if (!Clipboard.ContainsFileDropList()) return new();
            return Clipboard.GetFileDropList().Cast<string>().Where(IsAudioFile).Where(File.Exists).ToList();
        }
        catch
        {
            // Another program can hold the clipboard open for a moment. That is not worth an error;
            // the section copied here is still there to paste.
            return new();
        }
    }

    /// <summary>
    /// Puts whole audio files on one existing track, end to end from <paramref name="at"/> — a
    /// paste of files copied in Explorer, or files dropped onto an empty track's lane.
    /// </summary>
    public async Task PlaceFilesAsync(TrackViewModel track, IReadOnlyList<string> files, double at)
    {
        if (files.Count == 0) return;
        double from = at;
        IsBusy = true;
        try
        {
            // Decode everything before changing anything, so a file that will not open fails
            // cleanly: nothing half-placed, and no undo step that undoes nothing.
            foreach (var path in files)
            {
                Status = $"Loading {Path.GetFileName(path)}...";
                await Session.EnsureAudioAsync(path);
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not open that file: {ex.Message}";
            IsBusy = false;
            return;
        }

        // A file too short to place is left out here, before anything is recorded for undo.
        files = files.Where(p => (Session.AudioFor(new ClipModel { SourcePath = p })?.DurationSeconds ?? 0)
                                 >= TimelineEditor.MinimumSectionSeconds).ToList();
        if (files.Count == 0)
        {
            Status = "That file is too short to place.";
            IsBusy = false;
            return;
        }

        PushUndo($"{track.Name}: add {files.Count} file{(files.Count == 1 ? "" : "s")}");
        EditResult? last = null;
        int placedCount = 0;
        try
        {
            foreach (var path in files)
            {
                last = await Session.PlaceFileAsync(track.Model, path, at);
                if (!last.Changed) break;
                placedCount++;
                if (last.Clip is { } placed)
                    at = TimelineEditor.TimelineSecondsOfFrame(track.Model, TimelineEditor.EndFrameOf(placed, Session), Session);
            }
        }
        catch (Exception ex)
        {
            last = EditResult.No($"Could not place that file: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        if (!Tracks.Contains(track)) { Status = last?.Message ?? "That track went away while the file was loading."; return; }
        if (ProjectBpm <= 0 && track.Model.Bpm > 0) ProjectBpm = track.Model.Bpm;

        // Whatever did land is drawn and counted as a change, even if a later file failed.
        string what = files.Count == 1 ? Path.GetFileName(files[0]) : $"{placedCount} of {files.Count} files";
        var message = placedCount == files.Count
            ? $"{track.Name}: put {what} at {from:0.00}s."
            : $"{track.Name}: put {what} at {from:0.00}s — {last?.Message}";
        if (placedCount > 0) AfterClipEdit(track, new EditResult(true, message, last?.Clip), last?.Clip);
        else Status = last?.Message ?? "Nothing was placed.";
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

    // ---------------- empty tracks and loop mode ----------------

    /// <summary>Adds a track with nothing on it, selects it, and says what to do with it.</summary>
    [RelayCommand]
    private void AddEmptyTrack()
    {
        PushUndo("add empty track");
        var model = Session.AddEmptyTrack();
        var vm = NewTrackViewModel(model);
        vm.NotifyAudioLoaded();
        Tracks.Add(vm);
        SelectedTrack = vm;
        SelectClip(null);
        RecomputeLength();
        MarkDirty();
        Status = $"Added {model.Name}. Ctrl+V pastes onto it at the playhead, or drag a section onto it.";
    }

    /// <summary>
    /// While on, dragging either end of a section repeats it rather than trimming it. These are
    /// the same repeats Ctrl+D makes — sections on the track — not the Shift-drag listening loop.
    /// </summary>
    [ObservableProperty] private bool _loopMode;

    [RelayCommand]
    private void ToggleLoopMode() => LoopMode = !LoopMode;

    partial void OnLoopModeChanged(bool value)
        => Status = value
            ? "Loop mode on: drag either end of a section to repeat it. Hold Ctrl for part of a repeat."
            : "Loop mode off: dragging the end of a section trims it again.";

    // ---------------- dragging sections ----------------
    //
    // The window turns the pointer into timeline positions and target tracks; everything about
    // what that does to the project lives in Core (SectionMove, SectionTrim, LoopDrag), where the
    // test project can reach it. This is the glue: one undo step per gesture, and keeping the
    // lanes drawn from the model as it changes underneath them.

    private SectionMove? _move;
    private SectionTrim? _trim;
    private LoopDrag? _loopDrag;

    /// <summary>
    /// True while a section is being dragged. Removing a section then would leave the drag holding
    /// one that is no longer on the track, and its drop would clear what is under the ghost.
    /// </summary>
    public bool DragInProgress => _move is not null || _trim is not null || _loopDrag is not null;
    private TrackViewModel? _dragHome;
    private Guid _dragClipId;

    /// <summary>Where a drag is lined up against another section's edge, for the guide line.</summary>
    [ObservableProperty] private double? _snapLineSeconds;

    private SnapSettings SnapFor(bool free) => new(!free, PixelsPerSecond);

    /// <summary>The track a section is currently on during a move.</summary>
    public TrackViewModel? MovingOn => _move is null ? null : Tracks.FirstOrDefault(t => ReferenceEquals(t.Model, _move.Current));

    public void BeginMove(ClipViewModel clip)
    {
        EndDrag();
        PushUndo($"{clip.Track.Name}: move section");
        _move = new SectionMove(Session, clip.Track.Model, clip.Model);
        _dragClipId = clip.Id;
    }

    /// <summary>Moves the section being dragged. A null target keeps it on the track it is on.</summary>
    public void DragMoveTo(TrackViewModel? target, double proposedStart, bool free)
    {
        if (_move is null) return;
        var from = MovingOn;
        var to = target ?? from;
        if (from is null || to is null) return;

        _move.DragTo(to.Model, proposedStart, SnapFor(free));
        Redraw(from);
        if (!ReferenceEquals(from, to)) Redraw(to);
        SelectById(_dragClipId);
        SnapLineSeconds = _move.SnapLineSeconds;

        double at = TimelineEditor.TimelineSecondsAt(to.Model, _move.Clip.TrackStartSeconds);
        Status = $"{to.Name}: section at {at:0.00}s{(_move.SnappedTo is { } s ? $" — on {s}" : "")}";
    }

    /// <summary>
    /// The menu's "Clear trim": uncovers the section's song as far as dragging each end outward
    /// could, as one undo step. See <see cref="SectionTrim.Clear"/> for what it used to do.
    /// </summary>
    public void ClearTrim(ClipViewModel clip)
    {
        if (DragInProgress) { Status = "Let go of the section first."; return; }
        var track = clip.Track;
        PushUndo($"{track.Name}: clear trim");
        SectionTrim.Clear(Session, track.Model, clip.Model);
        Redraw(track);
        SelectById(clip.Id);
        RecomputeLength();
        MarkDirty();
        Status = $"{track.Name}: trim cleared, as far as the sections either side allow — the section is {SelectedClip?.LengthSeconds ?? 0:0.0}s.";
    }

    public void BeginTrim(ClipViewModel clip, bool start)
    {
        EndDrag();
        PushUndo($"{clip.Track.Name}: trim section");
        _trim = new SectionTrim(Session, clip.Track.Model, clip.Model, start);
        _dragHome = clip.Track;
        _dragClipId = clip.Id;
    }

    public void DragTrimTo(double proposedEdge, bool free)
    {
        if (_trim is null || _dragHome is null) return;
        _trim.DragTo(proposedEdge, SnapFor(free));
        Redraw(_dragHome);
        SelectById(_dragClipId);
        SnapLineSeconds = _trim.SnapLineSeconds;
        Status = $"{_dragHome.Name}: section {SelectedClip?.LengthSeconds ?? 0:0.0}s{(_trim.SnappedTo is null ? "" : " — on a section edge")}";
    }

    /// <summary>
    /// Whether dragging this end of this section in loop mode repeats it. The edge between two
    /// repeats of an existing loop does not — it is inside the loop — so that is a plain move.
    /// </summary>
    public bool CanLoopFrom(ClipViewModel clip, bool left)
        => LoopRun.Of(clip.Track.Model, clip.Model, Session).IsOuterEdge(clip.Model, left);

    public void BeginLoop(ClipViewModel clip, bool left)
    {
        EndDrag();
        PushUndo($"{clip.Track.Name}: loop section");
        _loopDrag = new LoopDrag(Session, clip.Track.Model, clip.Model, left);
        _dragHome = clip.Track;
        _dragClipId = _loopDrag.Base.Id;
    }

    public void DragLoopTo(double proposedEdge, bool free)
    {
        if (_loopDrag is null || _dragHome is null) return;
        var result = _loopDrag.DragTo(proposedEdge, wholeRepeats: !free);
        Redraw(_dragHome);
        SelectById(_dragClipId);
        Status = $"{_dragHome.Name}: {result.Message}";
    }

    /// <summary>Ends whichever drag is running and settles it.</summary>
    public void EndDrag()
    {
        if (_move is not null)
        {
            var origin = Tracks.FirstOrDefault(t => ReferenceEquals(t.Model, _move.Origin));
            var result = _move.Commit();
            var home = MovingOn;
            if (origin is not null) Redraw(origin);
            if (home is not null)
            {
                Redraw(home);
                SelectedTrack = home;
            }
            SelectById(_dragClipId);
            Status = result.Message;
        }
        else if (_trim is not null && _dragHome is not null)
        {
            Status = $"{_dragHome.Name}: section is {SelectedClip?.LengthSeconds ?? 0:0.0}s.";
        }
        else if (_loopDrag is not null && _dragHome is not null)
        {
            Status = _loopDrag.Repeats == 0 && !_loopDrag.Partial
                ? $"{_dragHome.Name}: loop left as the one section."
                : $"{_dragHome.Name}: looped {_loopDrag.Repeats + 1}×{(_loopDrag.Partial ? " and a part" : "")}. Each repeat is its own section now.";
        }

        bool changed = _move is not null || _trim is not null || _loopDrag is not null;
        _move = null;
        _trim = null;
        _loopDrag = null;
        _dragHome = null;
        SnapLineSeconds = null;
        EndGesture();
        if (changed)
        {
            RecomputeLength();
            MarkDirty();
        }
    }

    /// <summary>Pushes a track to the engine and redraws it from the model.</summary>
    private void Redraw(TrackViewModel track)
    {
        Session.Apply(track.Model);
        track.RefreshFromModel();
        track.RebuildClips();
    }

    private void SelectById(Guid id)
        => SelectClip(Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == id));

    /// <summary>The first file a track needs that is not loaded, for the "missing" message.</summary>
    private string MissingFileOf(TrackModel track)
        => ProjectSession.SourcePathsOf(track).FirstOrDefault(p => Session.AudioFor(new ClipModel { SourcePath = p }) is null)
           ?? track.SourcePath;

    /// <summary>
    /// Records the project state before a gesture. Continuous gestures — a knob drag, a clip drag —
    /// coalesce into one step: pass the same <paramref name="coalesceKey"/> for every event of the
    /// gesture and only the first takes a snapshot.
    /// </summary>
    public void PushUndo(string label, string? coalesceKey = null)
    {
        if (_session is null || _oneUndoStep) return;
        if (coalesceKey is not null && coalesceKey == _lastCoalesceKey) return;

        _lastCoalesceKey = coalesceKey;
        _undo.Push(Session.Project, label);
    }

    /// <summary>Ends a gesture, so the next one of the same kind starts a fresh undo step.</summary>
    public void EndGesture() => _lastCoalesceKey = null;

    private bool _oneUndoStep;

    /// <summary>
    /// Runs a command that turns several knobs as one undo step. Each knob snapshots itself before
    /// it moves, so until 2026-10-03 Reset left six steps behind and Match key two: one Ctrl+Z put
    /// back only the last knob touched, and undoing Match key left LINK off.
    /// </summary>
    private void AsOneUndoStep(string label, Action change)
    {
        PushUndo(label);
        _oneUndoStep = true;
        try { change(); }
        finally { _oneUndoStep = false; }
        EndGesture();
    }

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
        // NaN or infinity here would propagate into the ruler's tick loop, where the loop bound
        // never terminates and the window hangs for good.
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
            max = Math.Max(max, t.TimelineEndSeconds);
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
        if (!_dirty || !_interactive) return true;
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
