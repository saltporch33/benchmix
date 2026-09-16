using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Benchmix.App.ViewModels;

namespace Benchmix.App;

/// <summary>
/// The shell. Bindings live in XAML; pointer gestures live here, because dragging a clip and
/// scrubbing a ruler are about pixels and capture, not about state.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    // ruler drag
    private bool _scrubbing;
    private double _loopAnchorSeconds;
    private bool _settingLoop;

    // clip drag
    private TrackViewModel? _dragTrack;
    private Point _dragOrigin;
    private double _dragStartOffset;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _vm.Initialise();

        if (App.StartupFiles.Length > 0)
        {
            await _vm.AddFilesAsync(App.StartupFiles);
            _vm.ZoomFitCommand.Execute(null);
        }

        if (App.StemTestFile is { } stemFile) { await RunStemTestAsync(stemFile); return; }

        if (App.PlayTestSeconds > 0) { await RunPlayTestAsync(App.PlayTestSeconds); return; }

        if (App.UiProbePath is { } probe)
        {
            // Give layout, the waveform peaks and the first render tick a moment to settle,
            // otherwise the probe photographs a half-built window.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            await Task.Delay(1200);
            CaptureTo(probe);
            App.Log($"ui probe written to {probe}");
            _vm.Dispose();
            Application.Current.Shutdown();
            return;
        }

        if (App.AutoCloseSeconds > 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(App.AutoCloseSeconds),
            };
            timer.Tick += (_, _) => { timer.Stop(); _vm.Dispose(); Application.Current.Shutdown(); };
            timer.Start();
        }
    }

    /// <summary>Separates a short excerpt and logs what happened, to prove the shipped exe can.</summary>
    private async Task RunStemTestAsync(string file)
    {
        try
        {
            App.Log($"stemtest: model installed = {Benchmix.Engine.Stems.StemSeparator.IsModelInstalled}");

            var full = Benchmix.Engine.Audio.AudioClip.Load(file, 44100);
            int frames = (int)Math.Min(full.FrameCount - 44100 * 40, 44100 * 20);
            var slice = new float[frames * 2];
            Array.Copy(full.Samples, 44100 * 40 * 2, slice, 0, slice.Length);
            var clip = Benchmix.Engine.Audio.AudioClip.FromSamples(file, slice, 44100);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Benchmix.Engine.Stems.StemSeparator.SeparateAsync(clip);
            sw.Stop();

            static double Db(float[] x)
            {
                double s = 0;
                foreach (var v in x) s += v * (double)v;
                double r = Math.Sqrt(s / Math.Max(1, x.Length));
                return r <= 1e-12 ? -200 : 20 * Math.Log10(r);
            }

            App.Log($"stemtest: provider={result.ExecutionProvider} seconds={sw.Elapsed.TotalSeconds:F1} "
                  + $"bandlimit={result.BandLimitHz:F0}Hz");
            App.Log($"stemtest: mix={Db(clip.Samples):F1}dB vocals={Db(result.Primary.Samples):F1}dB "
                  + $"instrumental={Db(result.Secondary.Samples):F1}dB");
            App.Log($"stemtest: verdict={(Db(result.Primary.Samples) > -100 ? "PASS - separation ran in the published app" : "FAIL - vocals stem is silent")}");
        }
        catch (Exception ex)
        {
            App.Log("stemtest: FAILED - " + ex);
        }
        _vm.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Runs the transport silently and measures how fast the audio device pulls frames. If the
    /// playhead advances at ~1x wall clock, WASAPI really is pulling from our mix graph in
    /// realtime; if it sits still, the stream is dead however healthy the init looked.
    /// </summary>
    private async Task RunPlayTestAsync(double seconds)
    {
        var s = _vm.Session;
        App.Log($"playtest: device='{s.Engine.DeviceName}' rate={s.SampleRate} offline={s.Engine.IsOffline} tracks={_vm.Tracks.Count}");

        _vm.MasterGain = 0;                       // silent: this is a pipeline check, not a performance
        s.Graph.MasterGain = 0;
        s.Graph.SnapParameters();
        _vm.SeekTo(20);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long startFrame = s.Engine.PositionFrames;
        _vm.PlayPauseCommand.Execute(null);

        await Task.Delay(TimeSpan.FromSeconds(seconds));

        sw.Stop();
        long endFrame = s.Engine.PositionFrames;
        double advancedSeconds = (endFrame - startFrame) / (double)s.SampleRate;
        double ratio = advancedSeconds / sw.Elapsed.TotalSeconds;

        App.Log($"playtest: wall={sw.Elapsed.TotalSeconds:F2}s audio-advanced={advancedSeconds:F2}s ratio={ratio:F3}");
        App.Log($"playtest: state={s.Engine.State} verdict={(ratio is > 0.95 and < 1.05 ? "PASS - device is pulling in realtime" : "FAIL - stream is not running at realtime")}");

        _vm.StopCommand.Execute(null);
        _vm.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Renders the live window to a PNG at its real DPI.</summary>
    private void CaptureTo(string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            if (w < 1 || h < 1) return;

            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            App.Log("ui probe failed: " + ex);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.CanClose()) { e.Cancel = true; return; }
        _vm.Dispose();
    }

    // ---------------- drag and drop ----------------

    private static readonly string[] AudioExtensions =
        { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma", ".ogg", ".opus", ".aif", ".aiff", ".mp4", ".m4b" };

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasAudio(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (!HasAudio(e)) return;
        var files = ((string[])e.Data.GetData(DataFormats.FileDrop)!)
            .Where(IsAudio)
            .ToArray();
        if (files.Length == 0) return;

        // Drop position decides where on the timeline the clip lands, which is what you expect
        // when you aim at a spot rather than at the window.
        double at = 0;
        var pos = e.GetPosition(this);
        if (pos.X > 392) at = Math.Max(0, _vm.ScrollSeconds + (pos.X - 392) / _vm.PixelsPerSecond);

        await _vm.AddFilesAsync(files, at);
    }

    private static bool HasAudio(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop)
           && ((string[])e.Data.GetData(DataFormats.FileDrop)!).Any(IsAudio);

    private static bool IsAudio(string path)
        => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ---------------- ruler: scrub and loop ----------------

    private double SecondsAtRulerX(double x) => Math.Max(0, _vm.ScrollSeconds + x / _vm.PixelsPerSecond);

    private void OnRulerMouseDown(object sender, MouseButtonEventArgs e)
    {
        var host = (FrameworkElement)sender;
        double seconds = SecondsAtRulerX(e.GetPosition(host).X);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _settingLoop = true;
            _loopAnchorSeconds = seconds;
            _vm.SetLoop(seconds, seconds);
        }
        else
        {
            _scrubbing = true;
            _vm.SeekTo(seconds);
        }

        host.CaptureMouse();
        e.Handled = true;
    }

    private void OnRulerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubbing && !_settingLoop) return;
        var host = (FrameworkElement)sender;
        double seconds = SecondsAtRulerX(e.GetPosition(host).X);

        if (_settingLoop) _vm.SetLoop(_loopAnchorSeconds, seconds);
        else _vm.SeekTo(seconds);
    }

    private void OnRulerMouseUp(object sender, MouseButtonEventArgs e)
    {
        ((FrameworkElement)sender).ReleaseMouseCapture();
        _vm.EndGesture();
        _scrubbing = false;
        _settingLoop = false;
    }

    // ---------------- lanes: select, drag clips, seek ----------------

    /// <summary>Feeds the real lane width to the view-model, which Fit and the scrollbar both need.</summary>
    private void OnLaneSizeChanged(object sender, SizeChangedEventArgs e)
        => _vm.LaneWidthPx = e.NewSize.Width;

    private void OnTrackHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TrackViewModel vm) _vm.SelectedTrack = vm;
    }

    /// <summary>How close to a clip edge counts as grabbing the edge rather than the body.</summary>
    private const double EdgeGrabPixels = 7;

    private enum DragKind { Move, TrimStart, TrimEnd }

    private DragKind _dragKind;
    private double _dragStartSourceStart;
    private double _dragStartSourceEnd;

    /// <summary>Which part of a clip the pointer is over, in a lane's own coordinates.</summary>
    private DragKind HitTestClip(TrackViewModel vm, double x)
    {
        double left = (vm.TimelineOffsetSeconds - _vm.ScrollSeconds) * _vm.PixelsPerSecond;
        double right = left + vm.TimelineDurationSeconds * _vm.PixelsPerSecond;

        if (Math.Abs(x - left) <= EdgeGrabPixels) return DragKind.TrimStart;
        if (Math.Abs(x - right) <= EdgeGrabPixels) return DragKind.TrimEnd;
        return DragKind.Move;
    }

    private void OnLaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        var host = (FrameworkElement)sender;
        if (host.DataContext is not TrackViewModel vm) return;

        _vm.SelectedTrack = vm;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Alt-click puts the playhead here without disturbing the clip.
            _vm.SeekTo(Math.Max(0, _vm.ScrollSeconds + e.GetPosition(host).X / _vm.PixelsPerSecond));
            e.Handled = true;
            return;
        }

        _dragTrack = vm;
        _dragKind = HitTestClip(vm, e.GetPosition(host).X);

        // A track that failed to load has no usable source span, so there is nothing to trim and
        // the clamps below would be handed min > max. Treat the whole lane as a move instead.
        if (vm.SourceDurationSeconds <= 0.001) _dragKind = DragKind.Move;
        _dragOrigin = e.GetPosition(this);
        _dragStartOffset = vm.TimelineOffsetSeconds;
        _dragStartSourceStart = vm.SourceStartSeconds;
        _dragStartSourceEnd = vm.SourceEndSeconds < 0 ? vm.SourceDurationSeconds : vm.SourceEndSeconds;
        _dragging = false;
        host.CaptureMouse();
        e.Handled = true;
    }

    private void OnLaneMouseMove(object sender, MouseEventArgs e)
    {
        var host = (FrameworkElement)sender;

        // Hovering near an edge shows the resize cursor, so trimming is discoverable without a manual.
        if (_dragTrack is null && host.DataContext is TrackViewModel hover)
        {
            host.Cursor = HitTestClip(hover, e.GetPosition(host).X) == DragKind.Move
                ? Cursors.Arrow
                : Cursors.SizeWE;
        }

        if (_dragTrack is null || e.LeftButton != MouseButtonState.Pressed) return;

        double dx = e.GetPosition(this).X - _dragOrigin.X;

        // A few pixels of slop so a click does not nudge a clip by accident.
        if (!_dragging && Math.Abs(dx) < 4) return;
        _dragging = true;

        double deltaSeconds = dx / _vm.PixelsPerSecond;

        switch (_dragKind)
        {
            case DragKind.Move:
            {
                double target = Snap(Math.Max(0, _dragStartOffset + deltaSeconds));
                _dragTrack.TimelineOffsetSeconds = target;
                _vm.Status = $"{_dragTrack.Name} starts at {target:0.00}s";
                break;
            }

            case DragKind.TrimStart:
            {
                // Dragging the left edge eats into the source AND moves the clip, so the audio
                // under the pointer stays put instead of sliding away. The two must be clamped
                // together: the clip cannot start before timeline zero, so cap how much source the
                // edge may give back, rather than clamping the offset separately and letting the
                // audio slide later.
                double deltaSource = deltaSeconds * _dragTrack.Speed;
                double lowestStart = Math.Max(0, _dragStartSourceStart - _dragStartOffset * _dragTrack.Speed);
                double newStart = Math.Clamp(_dragStartSourceStart + deltaSource,
                                             lowestStart,
                                             Math.Max(lowestStart, _dragStartSourceEnd - 0.05));
                double consumed = newStart - _dragStartSourceStart;

                _dragTrack.SourceStartSeconds = newStart;
                _dragTrack.TimelineOffsetSeconds = Math.Max(0, _dragStartOffset + consumed / _dragTrack.Speed);
                _vm.Status = $"{_dragTrack.Name}: trimmed to {_dragTrack.TrimmedDurationSeconds:0.0}s";
                break;
            }

            case DragKind.TrimEnd:
            {
                double deltaSource = deltaSeconds * _dragTrack.Speed;
                double floor = Math.Min(_dragTrack.SourceStartSeconds + 0.05, _dragTrack.SourceDurationSeconds);
                double newEnd = Math.Clamp(_dragStartSourceEnd + deltaSource,
                                           floor,
                                           Math.Max(floor, _dragTrack.SourceDurationSeconds));
                _dragTrack.SourceEndSeconds = newEnd;
                _vm.Status = $"{_dragTrack.Name}: trimmed to {_dragTrack.TrimmedDurationSeconds:0.0}s";
                break;
            }
        }

        _vm.RecomputeLength();
    }

    /// <summary>
    /// Snaps to bar lines unless Ctrl is held. Mashups are assembled on bars, so snapping is the
    /// helpful default and holding a key is the exception.
    /// </summary>
    private double Snap(double seconds)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _vm.ProjectBpm <= 10) return seconds;
        double bar = 4 * 60.0 / _vm.ProjectBpm;
        return Math.Round(seconds / bar) * bar;
    }

    private void OnLaneMouseUp(object sender, MouseButtonEventArgs e)
    {
        ((FrameworkElement)sender).ReleaseMouseCapture();
        _vm.EndGesture();
        if (_dragging && _dragTrack is not null)
        {
            _vm.Status = _dragKind == DragKind.Move
                ? $"{_dragTrack.Name} moved to {_dragTrack.TimelineOffsetSeconds:0.00}s."
                : $"{_dragTrack.Name} trimmed to {_dragTrack.TrimmedDurationSeconds:0.0}s.";
        }
        _dragTrack = null;
        _dragging = false;
    }

    // ---------------- clip context menu ----------------

    private static TrackViewModel? TrackOf(object sender)
        => (sender as FrameworkElement)?.DataContext as TrackViewModel;

    /// <summary>
    /// Parses a menu Tag invariantly. These are literals in XAML, so they are always written with a
    /// dot — parsed with a comma-decimal culture, "0.25" reads as 25 and a quarter-second fade
    /// becomes a twenty-five second one.
    /// </summary>
    private static double TagSeconds(object sender)
        => double.TryParse((sender as FrameworkElement)?.Tag?.ToString(),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void OnFadeIn(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is not { } t) return;
        t.FadeInSeconds = TagSeconds(sender);
        _vm.Status = $"{t.Name}: {t.FadeText}.";
    }

    private void OnFadeOut(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is not { } t) return;
        t.FadeOutSeconds = TagSeconds(sender);
        _vm.Status = $"{t.Name}: {t.FadeText}.";
    }

    private void OnTrimStartToPlayhead(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is not { } t) return;
        double into = (_vm.PositionSeconds - t.TimelineOffsetSeconds) * t.Speed;
        if (into <= 0) { _vm.Status = "The playhead is before this clip."; return; }

        // Clamp against this clip's own end, not the whole file's. Past it, the trim would invert
        // and the clip would vanish entirely.
        double end = t.SourceEndSeconds < 0 ? t.SourceDurationSeconds : t.SourceEndSeconds;
        if (t.SourceStartSeconds + into >= end - 0.05)
        {
            _vm.Status = "The playhead is past the end of this clip.";
            return;
        }

        t.SourceStartSeconds = t.SourceStartSeconds + into;
        t.TimelineOffsetSeconds = _vm.PositionSeconds;
        _vm.RecomputeLength();
        _vm.Status = $"{t.Name}: {t.TrimText}.";
    }

    private void OnTrimEndToPlayhead(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is not { } t) return;
        double into = (_vm.PositionSeconds - t.TimelineOffsetSeconds) * t.Speed + t.SourceStartSeconds;
        if (into <= t.SourceStartSeconds + 0.05) { _vm.Status = "The playhead is at or before the clip's start."; return; }

        t.SourceEndSeconds = Math.Min(into, t.SourceDurationSeconds);
        _vm.RecomputeLength();
        _vm.Status = $"{t.Name}: {t.TrimText}.";
    }

    private void OnClearTrim(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is not { } t) return;
        t.SourceStartSeconds = 0;
        t.SourceEndSeconds = -1;
        _vm.RecomputeLength();
        _vm.Status = $"{t.Name}: full length restored.";
    }

    private void OnMoveToPlayhead(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is not { } t) return;
        t.TimelineOffsetSeconds = _vm.PositionSeconds;
        _vm.RecomputeLength();
        _vm.Status = $"{t.Name} moved to {t.TimelineOffsetSeconds:0.00}s.";
    }

    // ---------------- wheel: zoom and scroll ----------------

    private void OnTimelineWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Zoom about the pointer, so the thing under the cursor stays under the cursor.
            double x = Math.Max(0, e.GetPosition(this).X - 392);
            double timeUnderCursor = _vm.ScrollSeconds + x / _vm.PixelsPerSecond;

            double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
            _vm.PixelsPerSecond = Math.Clamp(_vm.PixelsPerSecond * factor, 2, 400);
            _vm.ScrollSeconds = Math.Max(0, timeUnderCursor - x / _vm.PixelsPerSecond);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _vm.ScrollSeconds = Math.Max(0, _vm.ScrollSeconds - e.Delta / 120.0 * (60 / _vm.PixelsPerSecond) * 4);
            e.Handled = true;
        }
        // otherwise fall through to the ScrollViewer's own vertical scrolling
    }
}
