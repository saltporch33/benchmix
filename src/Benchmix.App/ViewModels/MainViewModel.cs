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

    public double TimelineWidth => Math.Max(600, (TotalSeconds + 30) * PixelsPerSecond);

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
                Status = "The audio device stopped. Check it is still connected, then press play.";
            });

            ProjectBpm = Session.Project.ProjectBpm;
            Status = $"Audio ready on {Session.Engine.DeviceName} at {Session.SampleRate / 1000.0:0.#} kHz.";
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
        IsPlaying = Session.Engine.State == TransportState.Playing;

        // Meter ballistics: jump to a new peak instantly, fall away slowly. Showing the raw
        // per-block peak would flicker far too fast to read anything from.
        var m = Session.Graph.Meters;
        MasterLevelLeft = Ballistic(MasterLevelLeft, m.MasterPeakLeft);
        MasterLevelRight = Ballistic(MasterLevelRight, m.MasterPeakRight);
        MasterClipped = m.MasterClipped;

        for (int i = 0; i < Tracks.Count && i < m.TrackPeak.Length; i++)
            Tracks[i].MeterLevel = Ballistic(Tracks[i].MeterLevel, m.TrackPeak[i]);
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
        PixelsPerSecond = Math.Clamp(1000 / TotalSeconds, 2, 400);
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
                var vm = new TrackViewModel(Session, model, MarkDirty);
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
    private void New()
    {
        if (!ConfirmDiscard()) return;
        if (_session is null) return;

        Session.Graph.Clear();
        Tracks.Clear();
        Session.Project.Tracks.Clear();
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
            foreach (var m in result.Project.Tracks)
            {
                var vm = new TrackViewModel(Session, m, MarkDirty);
                vm.NotifyAudioLoaded();
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
            Status = result.UsedFallback ? result.Note! : $"Opened {Path.GetFileName(dlg.FileName)}.";
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
        long resume = Session.Engine.PositionFrames;

        _rendering = true;
        IsBusy = true;
        Status = "Rendering...";
        try
        {
            var progress = new Progress<double>(p => BusyProgress = p);
            var options = new RenderOptions { Format = format, TailSeconds = 2.0, ApplyLimiter = true };
            var result = await Session.RenderAsync(dlg.FileName, options, progress);

            Status = $"Rendered {result.Seconds:0.0}s to {Path.GetFileName(result.Path)} in {result.ElapsedSeconds:0.0}s"
                   + (result.Clipped ? " — the limiter had to pull peaks down; consider lowering master." : ".");
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
                var vm = new TrackViewModel(Session, m, MarkDirty);
                vm.NotifyAudioLoaded();
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

    private void PushUndo(string label)
    {
        if (_session is null) return;
        _undo.Push(Session.Project, label);
    }

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
        if (_session is null) return;
        Session.Project.ProjectBpm = value;
        MarkDirty();
    }

    partial void OnPixelsPerSecondChanged(double value) => OnPropertyChanged(nameof(TimelineWidth));
    partial void OnTotalSecondsChanged(double value) => OnPropertyChanged(nameof(TimelineWidth));

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
