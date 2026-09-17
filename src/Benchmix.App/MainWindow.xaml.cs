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

    // section drag
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

        if (App.LoadCheck)
        {
            foreach (var t in _vm.Tracks)
                App.Log($"loadcheck: '{t.Name}' loaded={t.IsLoaded} error={t.LoadError ?? "none"} sections={t.Clips.Count} peaks={(t.Clips.Any(c => c.Peaks is not null) ? "yes" : "no")}");
            bool allLoaded = _vm.Tracks.Count > 0 && _vm.Tracks.All(t => t.IsLoaded);
            App.Log($"loadcheck: verdict={(allLoaded ? "PASS - every track is ready to split" : "FAIL - a track is not marked loaded")}");
            EndVerificationRun(allLoaded);
            return;
        }

        if (App.LatencyCheck)
        {
            EndVerificationRun(RunLatencyCheck());
            return;
        }

        if (App.GestureTest)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            EndVerificationRun(RunGestureTest());
            return;
        }

        if (App.StemTestFile is { } stemFile) { await RunStemTestAsync(stemFile); return; }

        if (App.PlayTestSeconds > 0) { await RunPlayTestAsync(App.PlayTestSeconds); return; }

        if (App.UiProbePath is { } probe)
        {
            // Give layout, the waveform peaks and the first render tick a moment to settle,
            // otherwise the probe photographs a half-built window.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            await Task.Delay(1200);

            if (App.DemoSections && _vm.Tracks.FirstOrDefault() is { } demo)
            {
                _vm.SelectedTrack = demo;
                foreach (double at in new[] { 30.0, 45.0, 60.0 })
                {
                    _vm.SeekTo(at);
                    _vm.SplitAtPlayheadCommand.Execute(null);
                }
                _vm.SelectClip(demo.Clips.Count > 1 ? demo.Clips[1] : demo.Clips.FirstOrDefault());
                _vm.SeekTo(100);          // clear of the section edges, so the outline is visible
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                await Task.Delay(400);
            }
            CaptureTo(probe);
            App.Log($"ui probe written to {probe}");
            EndVerificationRun();
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
            bool separated = Db(result.Primary.Samples) > -100;
            App.Log($"stemtest: verdict={(separated ? "PASS - separation ran in the published app" : "FAIL - vocals stem is silent")}");
            EndVerificationRun(separated);
            return;
        }
        catch (Exception ex)
        {
            App.Log("stemtest: FAILED - " + ex);
        }
        EndVerificationRun(false);
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
        bool realtime = ratio is > 0.95 and < 1.05;
        App.Log($"playtest: state={s.Engine.State} verdict={(realtime ? "PASS - device is pulling in realtime" : "FAIL - stream is not running at realtime")}");

        _vm.StopCommand.Execute(null);
        EndVerificationRun(realtime);
    }

    /// <summary>
    /// Measures how far a pitched track lands from an untouched one, inside the shipped exe.
    ///
    /// Synthetic audio, no file needed: half a second of silence then a tone, rendered twice —
    /// once straight through, once through the phase vocoder. The onset of each is unambiguous,
    /// and the gap between them is the thing that was 7200 frames wrong.
    /// </summary>
    private static bool RunLatencyCheck()
    {
        const int Rate = 48000;
        const int SilentFrames = Rate / 2;

        var samples = new float[Rate * 2 * 2];
        for (int f = SilentFrames; f < Rate * 2; f++)
        {
            float v = (float)(0.7 * Math.Sin(2 * Math.PI * 1000 * (f - SilentFrames) / Rate));
            samples[f * 2] = v;
            samples[f * 2 + 1] = v;
        }
        var clip = Benchmix.Engine.Audio.AudioClip.FromSamples("latency", samples, Rate);

        static int Onset(Action<Benchmix.Engine.Audio.TrackVoice> configure,
                         Benchmix.Engine.Audio.AudioClip clip, int frames)
        {
            using var graph = new Benchmix.Engine.Audio.MixGraph(Rate);
            var t = graph.AddTrack();
            t.SetClip(clip);
            configure(t);
            graph.LimiterEnabled = false;
            graph.Seek(0);
            graph.SnapParameters();

            var buf = new float[frames * 2];
            for (int done = 0; done < frames;)
            {
                int n = Math.Min(4096, frames - done);
                graph.Render(buf.AsSpan(done * 2, n * 2), n);
                done += n;
            }
            for (int f = 0; f < frames; f++) if (Math.Abs(buf[f * 2]) > 0.05f) return f;
            return -1;
        }

        try
        {
            int plain = Onset(_ => { }, clip, Rate * 2);
            int pitched = Onset(t =>
            {
                t.Mode = Benchmix.Engine.Audio.StretchMode.Independent;
                t.Semitones = 0.01;
            }, clip, Rate * 2);

            int drift = pitched - plain;
            App.Log($"latencycheck: untouched onset={plain} pitched onset={pitched} "
                  + $"drift={drift} frames ({drift * 1000.0 / Rate:F1} ms)");
            bool aligned = Math.Abs(drift) < 256 && plain > 0;
            App.Log($"latencycheck: verdict={(aligned ? "PASS - the two land together in the published exe" : $"FAIL - {drift} frames apart")}");
            return aligned;
        }
        catch (Exception ex)
        {
            App.Log("latencycheck: FAILED - " + ex);
            return false;
        }
    }

    /// <summary>
    /// Runs the dispatcher until <paramref name="done"/> is true or the wait runs out. The gesture
    /// test is synchronous but some commands are not, and reading a result while it is still in
    /// flight would report a failure that is really a race in the test.
    /// </summary>
    private static void PumpUntil(Func<bool> done, int millis = 4000)
    {
        var until = Environment.TickCount64 + millis;
        while (!done() && Environment.TickCount64 < until)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Drives the right-drag and double-click gestures through the real handlers with a scripted
    /// pointer, and logs what each one did. No physical input is synthesised, so this can run while
    /// someone else is using the machine.
    /// </summary>
    private bool RunGestureTest()
    {
        var pass = new List<string>();
        var fail = new List<string>();
        void Check(string what, bool ok) => (ok ? pass : fail).Add(what);

        // Handled cannot even be read on args with no RoutedEvent, so each one is stamped with the
        // event its handler is actually wired to in XAML.
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        { RoutedEvent = PreviewMouseRightButtonDownEvent };
        var move = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = PreviewMouseMoveEvent };
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        { RoutedEvent = PreviewMouseRightButtonUpEvent };

        // Scrolling is clamped to MaxScrollSeconds, which is derived from the project length. Run
        // with no files and there is nowhere to scroll, so every pan check would pass or fail on
        // whether a track happened to be loaded rather than on the code under test. Give the view
        // its own room instead, and prove we got it.
        _vm.LaneWidthPx = 1000;
        _vm.PixelsPerSecond = 40;
        _vm.TotalSeconds = 600;
        Check($"the test has scroll room (max {_vm.MaxScrollSeconds:F0}s)", _vm.MaxScrollSeconds > 60);

        // --- a plain right-click must leave the view alone and let the menu open ---
        _vm.ScrollSeconds = 12;
        double before = _vm.ScrollSeconds;

        _scriptedPointerX = 500;
        OnTimelineRightButtonDown(TimelineGrid, down);
        _scriptedPointerX = 502;                       // inside the 4 px slop: still a click
        OnTimelinePreviewMouseMove(TimelineGrid, move);
        up.Handled = false;
        OnTimelineRightButtonUp(TimelineGrid, up);

        Check("a right-click does not scroll", Math.Abs(_vm.ScrollSeconds - before) < 1e-9);
        Check("a right-click does not swallow its own mouse-up", !up.Handled);
        Check("a right-click leaves the clip menu alone", !ConsumeContextMenuSuppression());

        // --- a right-drag must pan, and must eat the menu that would otherwise follow ---
        _vm.ScrollSeconds = 30;
        before = _vm.ScrollSeconds;
        Check($"the drag starts with room to scroll back (at {before:F1}s)", before > 3.5);

        _scriptedPointerX = 500;
        OnTimelineRightButtonDown(TimelineGrid, down);
        _scriptedPointerX = 620;                       // dragged right by 120 px = 3 s at 40 px/s
        OnTimelinePreviewMouseMove(TimelineGrid, move);
        double afterDrag = _vm.ScrollSeconds;
        up.Handled = false;
        OnTimelineRightButtonUp(TimelineGrid, up);

        Check($"dragging right scrolls back 3.00 s (got {before - afterDrag:F2})",
              Math.Abs((before - afterDrag) - 3.0) < 0.001);
        Check("a pan suppresses the clip menu", ConsumeContextMenuSuppression());

        // Suppression is one-shot: the click after a pan must get its menu back.
        _scriptedPointerX = 500;
        OnTimelineRightButtonDown(TimelineGrid, down);
        up.Handled = false;
        OnTimelineRightButtonUp(TimelineGrid, up);
        Check("the click after a pan gets its menu back", !ConsumeContextMenuSuppression());

        // --- panning must stop at the ends rather than running off into negative time ---
        _vm.ScrollSeconds = 1;
        Check("the clamp check starts away from zero", _vm.ScrollSeconds > 0.5);
        _scriptedPointerX = 500;
        OnTimelineRightButtonDown(TimelineGrid, down);
        _scriptedPointerX = 500 + 100_000;             // yank far past the start
        OnTimelinePreviewMouseMove(TimelineGrid, move);
        OnTimelineRightButtonUp(TimelineGrid, up);
        Check($"panning clamps at zero (got {_vm.ScrollSeconds:F2})", _vm.ScrollSeconds >= -1e-9);

        _scriptedPointerX = null;

        // --- double-click on the ruler plays from that point ---
        // Silent, like --playtest: this really does start the audio device, and someone is using
        // this machine. Whether the transport runs is the question, not what it sounds like.
        _vm.MasterGain = 0;
        _vm.Session.Graph.MasterGain = 0;
        _vm.Session.Graph.SnapParameters();
        _vm.StopCommand.Execute(null);
        _vm.PlayFrom(18.5);
        Check($"double-click seeks (got {_vm.PositionSeconds:F2})", Math.Abs(_vm.PositionSeconds - 18.5) < 0.05);
        Check("double-click starts the transport", _vm.IsPlaying || _vm.Session.Engine.IsOffline);
        if (_vm.Session.Engine.IsOffline) pass.Add("(no audio device here, so 'is playing' is untested)");

        _vm.PlayFrom(25.0);
        Check("double-click while playing keeps playing", _vm.IsPlaying || _vm.Session.Engine.IsOffline);
        _vm.StopCommand.Execute(null);

        // --- sections: split, repeat, copy, paste, delete, undo ---
        //
        // None of this is reachable from the test project, which references Core and Engine but
        // not the app. Driving the real commands here is the only evidence that the wiring between
        // a keystroke and the model actually exists.
        if (_vm.Tracks.FirstOrDefault() is { } t0 && t0.IsLoaded)
        {
            _vm.SelectedTrack = t0;
            Check($"a freshly loaded track is one section (got {t0.Clips.Count})", t0.Clips.Count == 1);

            _vm.SeekTo(10);
            _vm.SplitAtPlayheadCommand.Execute(null);
            Check($"split makes two sections (got {t0.Clips.Count})", t0.Clips.Count == 2);

            // The halves must meet exactly, or the engine stops seeing a seam and the join ticks.
            bool meet = t0.Clips.Count == 2
                     && t0.Clips[0].SourceEndSeconds == t0.Clips[1].SourceStartSeconds;
            Check("the two halves meet exactly where the first stops", meet);

            _vm.SeekTo(20);
            _vm.SplitAtPlayheadCommand.Execute(null);
            Check($"splitting again makes three (got {t0.Clips.Count})", t0.Clips.Count == 3);

            // Repeat the middle section: this is the loop-building gesture.
            _vm.SelectClip(t0.Clips[1]);
            double middleLength = t0.Clips[1].LengthSeconds;
            _vm.DuplicateClipCommand.Execute(null);
            Check($"repeat adds a section (got {t0.Clips.Count})", t0.Clips.Count == 4);
            Check($"the repeat is the same length ({_vm.SelectedClip?.LengthSeconds:0.00} vs {middleLength:0.00})",
                  _vm.SelectedClip is not null
                  && Math.Abs(_vm.SelectedClip.LengthSeconds - middleLength) < 0.01);
            Check("the repeat becomes the selection, so holding the key walks along",
                  _vm.SelectedClip is not null && _vm.SelectedClip.IsSelected);

            // Delete must leave a hole rather than closing it.
            var after = t0.Clips.Last();
            double afterStart = after.TrackStartSeconds;
            _vm.SelectClip(t0.Clips[0]);
            _vm.DeleteClipCommand.Execute(null);
            var stillThere = t0.Clips.FirstOrDefault(c => Math.Abs(c.TrackStartSeconds - afterStart) < 1e-6);
            Check("deleting a section leaves the others where they were", stillThere is not null);

            // Undo must put it back. It runs through the async reload path, so the dispatcher is
            // pumped until it lands rather than read while it is still in flight.
            _vm.UndoCommand.Execute(null);
            PumpUntil(() => _vm.Tracks.FirstOrDefault()?.Clips.Count == 4);
            Check($"undo restores the deleted section (got {_vm.Tracks.FirstOrDefault()?.Clips.Count})",
                  _vm.Tracks.FirstOrDefault()?.Clips.Count == 4);

            // Copy and paste onto the same track.
            var t1 = _vm.Tracks.First();
            _vm.SelectedTrack = t1;
            _vm.SelectClip(t1.Clips[0]);
            _vm.CopyClipCommand.Execute(null);
            Check("copying fills the clipboard", _vm.HasClipboard);

            // Pasting into the middle of a section splits it, so the count rises by two: the head
            // of what was there, the pasted audio, and the tail. What matters is that the pasted
            // section landed at the playhead.
            int countBeforePaste = t1.Clips.Count;
            _vm.SeekTo(120);
            _vm.PasteClipCommand.Execute(null);
            Check($"paste lands a section at the playhead (at {_vm.SelectedClip?.TimelineStartSeconds:0.00}s)",
                  _vm.SelectedClip is not null
                  && Math.Abs(_vm.SelectedClip.TimelineStartSeconds - 120) < 0.05);
            Check($"paste overwrites rather than stacking (got {t1.Clips.Count}, was {countBeforePaste})",
                  t1.Clips.Count > countBeforePaste);

            // Nothing may overlap, whatever was pasted where.
            var ordered = t1.Clips.OrderBy(c => c.TrackStartSeconds).ToList();
            bool overlap = false;
            for (int i = 0; i + 1 < ordered.Count; i++)
                if (ordered[i + 1].TrackStartSeconds < ordered[i].TrackStartSeconds + ordered[i].LengthSeconds - 1e-6)
                    overlap = true;
            Check("no two sections overlap after a paste", !overlap);
        }
        else
        {
            pass.Add("(no track loaded, so the section commands are untested - pass a file to cover them)");
        }

        // --- a knob dragged past its end stop must not leave a negative gain in the model ---
        if (_vm.Tracks.FirstOrDefault() is { } track)
        {
            track.EqLow = -0.01;                   // what a two-way binding writes past the bottom stop
            Check($"a knob past the bottom stops at zero in the model (got {track.Model.EqLow})",
                  track.Model.EqLow == 0);
            track.EqLow = 1;
        }
        else
        {
            pass.Add("(no track loaded, so the knob clamp is untested - pass a file to cover it)");
        }

        foreach (var p in pass) App.Log("gesturetest: PASS - " + p);
        foreach (var f in fail) App.Log("gesturetest: FAIL - " + f);
        App.Log($"gesturetest: verdict={(fail.Count == 0 ? $"PASS - {pass.Count} checks" : $"FAIL - {fail.Count} of {pass.Count + fail.Count}")}");
        return fail.Count == 0;
    }

    /// <summary>
    /// Ends a verification run, carrying its verdict into the process exit code.
    ///
    /// <para>
    /// The exit code is the point. Every one of these switches used to exit 0 whether it passed or
    /// failed, so the PASS/FAIL line in the log was the only evidence — fine while a person is
    /// reading the log, useless to anything that is not.
    /// </para>
    /// <para>
    /// Shutdown() alone is also not enough from inside Loaded: measured, the process was still
    /// alive an hour later, still holding the audio device and the exe — which is how a later build
    /// failed with a file lock. Closing the window normally does exit, so the watchdog is about the
    /// switches rather than about the app.
    /// </para>
    /// </summary>
    private void EndVerificationRun(bool passed = true)
    {
        _vm.Dispose();
        App.Log($"verification run: exit code {(passed ? 0 : 1)}");
        Application.Current.Shutdown(passed ? 0 : 1);

        var watchdog = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            App.Log("verification run: shutdown did not take, exiting hard");
            Environment.Exit(passed ? 0 : 1);
        };
        watchdog.Start();
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
            using (var fs = File.Create(path)) encoder.Save(fs);

            WriteControlMap(Path.ChangeExtension(path, ".map.tsv"), dpi);
        }
        catch (Exception ex)
        {
            App.Log("ui probe failed: " + ex);
        }
    }

    /// <summary>
    /// Writes where every labelled control actually sits in the captured image.
    ///
    /// This exists so documentation screenshots can be annotated against real coordinates instead
    /// of someone eyeballing pixel positions — and so the annotations stay correct when the layout
    /// changes, because the next capture reports the new positions.
    /// </summary>
    private void WriteControlMap(string path, DpiScale dpi)
    {
        var rows = new List<string> { "label\tx\ty\twidth\theight" };
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        void Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0 && fe.ActualHeight > 0)
                {
                    var label = LabelFor(fe);
                    if (label is not null)
                    {
                        // Duplicates are expected — six knobs per track, a Match BPM per track —
                        // so each repeat gets an index, in visual-tree order (top to bottom).
                        seen.TryGetValue(label, out int n);
                        seen[label] = n + 1;
                        string key = n == 0 ? label : $"{label}#{n + 1}";

                        var p = fe.TransformToAncestor(this).Transform(new Point(0, 0));
                        rows.Add(string.Join('\t',
                            key,
                            (int)Math.Round(p.X * dpi.DpiScaleX),
                            (int)Math.Round(p.Y * dpi.DpiScaleY),
                            (int)Math.Round(fe.ActualWidth * dpi.DpiScaleX),
                            (int)Math.Round(fe.ActualHeight * dpi.DpiScaleY)));
                    }
                }
                Walk(child);
            }
        }

        Walk(this);
        File.WriteAllLines(path, rows);
        App.Log($"ui probe: control map written to {path} ({rows.Count - 1} controls)");
    }

    /// <summary>A stable, human-meaningful name for a control, or null to leave it out of the map.</summary>
    private static string? LabelFor(FrameworkElement fe) => fe switch
    {
        Controls.Knob k when !string.IsNullOrEmpty(k.Label) => "knob:" + k.Label,
        Controls.LevelMeter => "meter",
        Controls.WaveformView => "waveform",
        Controls.TimelineRuler => "ruler",
        Controls.PlayheadOverlay => "playhead",
        System.Windows.Controls.Primitives.ToggleButton t when t.Content is string ts => "toggle:" + ts,
        Button b when b.Content is string bs => "button:" + bs,
        TextBox => "textbox:projectBpm",
        Slider => "slider:master",
        TextBlock tb when tb.Text is { Length: > 0 } s && s.Length < 60 => "text:" + s,
        _ => null,
    };

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
        else if (e.ClickCount == 2)
        {
            // Double-click plays from here. Scrubbing is deliberately switched off for the rest of
            // the gesture: the button is still down, and without this any jitter before you lift it
            // would drag the playhead straight back off the spot you just started playing from.
            // OnRulerMouseUp releases the capture unconditionally, so nothing leaks.
            _scrubbing = false;
            _vm.PlayFrom(seconds);
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

    /// <summary>How close to a section's edge counts as grabbing the edge rather than the body.</summary>
    private const double EdgeGrabPixels = 7;

    private enum DragKind { Move, TrimStart, TrimEnd }

    private ClipViewModel? _dragClip;
    private DragKind _dragKind;
    private double _dragStartTrackStart;
    private double _dragStartSourceStart;
    private double _dragStartSourceEnd;

    private double SecondsAtLaneX(double x) => Math.Max(0, _vm.ScrollSeconds + x / _vm.PixelsPerSecond);

    /// <summary>The section under a lane x position, or null in a gap.</summary>
    private ClipViewModel? ClipAtLaneX(TrackViewModel vm, double x)
    {
        double t = SecondsAtLaneX(x);
        foreach (var c in vm.Clips)
            if (t >= c.TimelineStartSeconds && t < c.TimelineEndSeconds) return c;
        return null;
    }

    /// <summary>Which part of a section the pointer is over, in a lane's own coordinates.</summary>
    private DragKind HitTestClip(ClipViewModel clip, double x)
    {
        double left = (clip.TimelineStartSeconds - _vm.ScrollSeconds) * _vm.PixelsPerSecond;
        double right = (clip.TimelineEndSeconds - _vm.ScrollSeconds) * _vm.PixelsPerSecond;

        if (Math.Abs(x - left) <= EdgeGrabPixels) return DragKind.TrimStart;
        if (Math.Abs(x - right) <= EdgeGrabPixels) return DragKind.TrimEnd;
        return DragKind.Move;
    }

    private void OnLaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        var host = (FrameworkElement)sender;
        if (host.DataContext is not TrackViewModel vm) return;

        _vm.SelectedTrack = vm;
        double x = e.GetPosition(host).X;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Alt-click puts the playhead here without disturbing anything.
            _vm.SeekTo(SecondsAtLaneX(x));
            e.Handled = true;
            return;
        }

        var clip = ClipAtLaneX(vm, x);
        _vm.SelectClip(clip);
        if (clip is null) { e.Handled = true; return; }     // clicking a gap just clears the selection

        _dragTrack = vm;
        _dragClip = clip;
        _dragKind = HitTestClip(clip, x);

        // A section whose audio never loaded has no usable source span, so there is nothing to
        // trim and the clamps below would be handed min > max. Treat it as a move instead.
        if (clip.SourceDurationSeconds <= 0.001) _dragKind = DragKind.Move;

        _dragOrigin = e.GetPosition(this);
        _dragStartTrackStart = clip.TrackStartSeconds;
        _dragStartOffset = vm.TimelineOffsetSeconds;
        _dragStartSourceStart = clip.SourceStartSeconds;
        _dragStartSourceEnd = clip.SourceEndSeconds < 0 ? clip.SourceDurationSeconds : clip.SourceEndSeconds;
        _dragging = false;
        host.CaptureMouse();
        e.Handled = true;
    }

    private void OnLaneMouseMove(object sender, MouseEventArgs e)
    {
        var host = (FrameworkElement)sender;

        // Hovering near an edge shows the resize cursor, so trimming is discoverable without a manual.
        if (_dragClip is null && host.DataContext is TrackViewModel hover)
        {
            var over = ClipAtLaneX(hover, e.GetPosition(host).X);
            host.Cursor = over is not null && HitTestClip(over, e.GetPosition(host).X) != DragKind.Move
                ? Cursors.SizeWE
                : Cursors.Arrow;
        }

        if (_dragClip is null || _dragTrack is null || e.LeftButton != MouseButtonState.Pressed) return;

        double dx = e.GetPosition(this).X - _dragOrigin.X;

        // A few pixels of slop so a click does not nudge a section by accident.
        if (!_dragging && Math.Abs(dx) < 4) return;
        _dragging = true;

        double deltaSeconds = dx / _vm.PixelsPerSecond;
        double speed = Math.Max(0.01, _dragTrack.Speed);

        switch (_dragKind)
        {
            case DragKind.Move:
            {
                // A section moves within its own track, in the track's unstretched timebase, so
                // dragging it does not drag the whole deck out of line with the other songs.
                double targetTimeline = Snap(Math.Max(0, TimelineStartOf(_dragStartTrackStart) + deltaSeconds));
                double trackStart = Math.Max(0, (targetTimeline - _dragTrack.TimelineOffsetSeconds) * speed);
                _dragClip.TrackStartSeconds = trackStart;
                _vm.Status = $"{_dragTrack.Name}: section at {TimelineStartOf(trackStart):0.00}s";
                break;
            }

            case DragKind.TrimStart:
            {
                // Dragging the left edge eats into the source AND moves the section, so the audio
                // under the pointer stays put instead of sliding away.
                double deltaSource = deltaSeconds * speed;
                double lowestStart = Math.Max(0, _dragStartSourceStart - _dragStartTrackStart);
                double newStart = Math.Clamp(_dragStartSourceStart + deltaSource,
                                             lowestStart,
                                             Math.Max(lowestStart, _dragStartSourceEnd - 0.05));
                double consumed = newStart - _dragStartSourceStart;

                _dragClip.SourceStartSeconds = newStart;
                _dragClip.TrackStartSeconds = Math.Max(0, _dragStartTrackStart + consumed);
                _vm.Status = $"{_dragTrack.Name}: section {_dragClip.LengthSeconds:0.0}s";
                break;
            }

            case DragKind.TrimEnd:
            {
                double deltaSource = deltaSeconds * speed;
                double floor = Math.Min(_dragClip.SourceStartSeconds + 0.05, _dragClip.SourceDurationSeconds);
                double newEnd = Math.Clamp(_dragStartSourceEnd + deltaSource,
                                           floor,
                                           Math.Max(floor, _dragClip.SourceDurationSeconds));
                _dragClip.SourceEndSeconds = newEnd;
                _vm.Status = $"{_dragTrack.Name}: section {_dragClip.LengthSeconds:0.0}s";
                break;
            }
        }

        _vm.RecomputeLength();
    }

    /// <summary>Where a position in the dragged track's own timebase sits on the timeline.</summary>
    private double TimelineStartOf(double trackSeconds)
        => (_dragTrack?.TimelineOffsetSeconds ?? 0) + trackSeconds / Math.Max(0.01, _dragTrack?.Speed ?? 1);

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
        if (_dragging && _dragClip is not null && _dragTrack is not null)
        {
            _vm.Status = _dragKind == DragKind.Move
                ? $"{_dragTrack.Name}: section moved to {_dragClip.TimelineStartSeconds:0.00}s."
                : $"{_dragTrack.Name}: section is {_dragClip.LengthSeconds:0.0}s.";
        }
        _dragTrack = null;
        _dragClip = null;
        _dragging = false;
    }

    // ---------------- clip context menu ----------------

    private static TrackViewModel? TrackOf(object sender)
        => (sender as FrameworkElement)?.DataContext as TrackViewModel;

    /// <summary>
    /// The section a menu item acts on: whichever one is selected on that track, which the
    /// right-click itself just set.
    /// </summary>
    private ClipViewModel? ClipOf(object sender)
        => TrackOf(sender)?.Clips.FirstOrDefault(c => c.IsSelected);

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
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        _vm.PushUndo("fade in", null);
        c.FadeInSeconds = TagSeconds(sender);
        _vm.Status = $"{c.Track.Name}: {c.FadeText}.";
    }

    private void OnFadeOut(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        _vm.PushUndo("fade out", null);
        c.FadeOutSeconds = TagSeconds(sender);
        _vm.Status = $"{c.Track.Name}: {c.FadeText}.";
    }

    private void OnTrimStartToPlayhead(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        double into = (_vm.PositionSeconds - c.TimelineStartSeconds) * c.Track.Speed;
        if (into <= 0) { _vm.Status = "The playhead is before this section."; return; }

        // Clamp against this section's own end, not the whole file's. Past it, the trim would
        // invert and the section would vanish entirely.
        double end = c.SourceEndSeconds < 0 ? c.SourceDurationSeconds : c.SourceEndSeconds;
        if (c.SourceStartSeconds + into >= end - 0.05)
        {
            _vm.Status = "The playhead is past the end of this section.";
            return;
        }

        _vm.PushUndo("trim", null);
        c.SourceStartSeconds += into;
        c.TrackStartSeconds += into;
        _vm.RecomputeLength();
        _vm.Status = $"{c.Track.Name}: {c.TrimText}.";
    }

    private void OnTrimEndToPlayhead(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        double into = (_vm.PositionSeconds - c.TimelineStartSeconds) * c.Track.Speed + c.SourceStartSeconds;
        if (into <= c.SourceStartSeconds + 0.05) { _vm.Status = "The playhead is at or before the section's start."; return; }

        _vm.PushUndo("trim", null);
        c.SourceEndSeconds = Math.Min(into, c.SourceDurationSeconds);
        _vm.RecomputeLength();
        _vm.Status = $"{c.Track.Name}: {c.TrimText}.";
    }

    private void OnClearTrim(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        _vm.PushUndo("clear trim", null);
        c.SourceStartSeconds = 0;
        c.SourceEndSeconds = -1;
        _vm.RecomputeLength();
        _vm.Status = $"{c.Track.Name}: full length restored.";
    }

    private void OnMoveToPlayhead(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        _vm.PushUndo("move", null);
        c.TrackStartSeconds = Math.Max(0, (_vm.PositionSeconds - c.Track.TimelineOffsetSeconds) * c.Track.Speed);
        _vm.RecomputeLength();
        _vm.Status = $"{c.Track.Name}: section moved to {c.TimelineStartSeconds:0.00}s.";
    }

    private void OnSplitHere(object sender, RoutedEventArgs e) => _vm.SplitAtPlayheadCommand.Execute(null);
    private void OnCutSection(object sender, RoutedEventArgs e) => _vm.CutClipCommand.Execute(null);
    private void OnCopySection(object sender, RoutedEventArgs e) => _vm.CopyClipCommand.Execute(null);
    private void OnPasteSection(object sender, RoutedEventArgs e) => _vm.PasteClipCommand.Execute(null);
    private void OnDuplicateSection(object sender, RoutedEventArgs e) => _vm.DuplicateClipCommand.Execute(null);
    private void OnDeleteSection(object sender, RoutedEventArgs e) => _vm.DeleteClipCommand.Execute(null);

    // ---------------- right-drag: pan the timeline ----------------

    private bool _panArmed;
    private bool _panning;
    private bool _suppressContextMenu;
    private double _panOriginX;
    private double _panStartScroll;
    private Cursor? _cursorBeforePan;

    /// <summary>Scripted pointer position for <c>--gesturetest</c>; null in normal use.</summary>
    private double? _scriptedPointerX;

    private double PointerX(MouseEventArgs e) => _scriptedPointerX ?? e.GetPosition(TimelineGrid).X;

    /// <summary>
    /// Arms a pan without taking capture. Capture is what would break the clip menu: a
    /// ContextMenu belongs to the lane Border, and WPF opens it from the bubbling mouse-up on that
    /// Border. Capturing here would redirect that mouse-up to this Grid and the menu would never
    /// open again. So nothing is captured, and nothing is handled, until the pointer has actually
    /// moved far enough to mean "pan" rather than "right-click".
    /// </summary>
    private void OnTimelineRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panArmed = true;
        _panning = false;
        // Cleared here rather than only when the menu opens: if marking the preview up handled
        // succeeds in stopping the event, ContextMenuOpening never fires and never clears it, and
        // a stale flag would eat the NEXT right-click's menu.
        _suppressContextMenu = false;
        _panOriginX = PointerX(e);
        _panStartScroll = _vm.ScrollSeconds;
    }

    private void OnTimelinePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panArmed) return;

        // The button can be released outside the window, where no up event reaches us.
        if (_scriptedPointerX is null && e.RightButton != MouseButtonState.Pressed) { EndPan(); return; }

        double dx = PointerX(e) - _panOriginX;
        if (!_panning)
        {
            if (Math.Abs(dx) < 4) return;       // same slop as a clip drag, so a click never pans
            _panning = true;
            if (_scriptedPointerX is null) TimelineGrid.CaptureMouse();
            _cursorBeforePan = Cursor;
            Cursor = Cursors.ScrollWE;
        }

        // Grab-and-drag: pulling right reveals earlier audio, so the view scrolls back.
        _vm.ScrollSeconds = _panStartScroll - dx / Math.Max(0.01, _vm.PixelsPerSecond);
    }

    /// <summary>
    /// Ends the gesture and, after a real pan, stops the context menu from following it.
    ///
    /// Two mechanisms, deliberately. Marking the <b>preview</b> up handled should stop the event
    /// before the bubbling mouse-up that WPF's PopupControlService opens the menu from — but that
    /// depends on how WPF re-raises the button-specific events, which is not ours to rely on. So
    /// the flag is the real guarantee: <see cref="OnTimelineContextMenuOpening"/> cancels the menu
    /// outright, and that path is the one the gesture test exercises.
    /// </summary>
    private void OnTimelineRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool panned = _panning;
        EndPan();
        if (!panned) return;
        _suppressContextMenu = true;
        e.Handled = true;
    }

    /// <summary>True once, if the gesture that just ended was a pan rather than a click.</summary>
    private bool ConsumeContextMenuSuppression()
    {
        if (!_suppressContextMenu) return false;
        _suppressContextMenu = false;
        return true;
    }

    private void OnTimelineContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ConsumeContextMenuSuppression()) e.Handled = true;
    }

    private void EndPan()
    {
        if (_panning)
        {
            if (_scriptedPointerX is null) TimelineGrid.ReleaseMouseCapture();
            Cursor = _cursorBeforePan;
            _cursorBeforePan = null;
        }
        _panArmed = false;
        _panning = false;
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
