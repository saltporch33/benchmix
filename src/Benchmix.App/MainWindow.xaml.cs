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
    private readonly MainViewModel _vm = new(App.UseAudioDevice, interactive: !App.IsScriptedRun);

    // ruler drag
    private bool _scrubbing;
    private double _loopAnchorSeconds;
    private bool _settingLoop;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        if (App.IsScriptedRun) KeepOutOfTheWay();
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

        if (App.ButtonTest)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            EndVerificationRun(await RunButtonTestAsync());
            return;
        }

        if (App.StemTestFile is { } stemFile) { await RunStemTestAsync(stemFile); return; }

        if (App.PlayTestSeconds > 0) { await RunPlayTestAsync(App.PlayTestSeconds); return; }

        if (App.UiProbePath is { } probe)
        {
            // An old picture or control map left behind would look like this run's output.
            foreach (var old in new[] { probe, Path.ChangeExtension(probe, ".map.tsv") })
                try { if (File.Exists(old)) File.Delete(old); } catch { }

            // A demo needs a song to demonstrate on. Without one the shot would show an empty
            // project and still pass.
            if ((App.DemoSections || App.DemoEditing) && _vm.Tracks.FirstOrDefault() is not { IsLoaded: true })
            {
                App.Log("ui probe failed: the demo needs a song that loads, and none did - nothing was demonstrated");
                EndVerificationRun(false);
                return;
            }

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
                if (demo.Clips.Count != 4 || _vm.SelectedClip is null)
                {
                    App.Log($"ui probe failed: the sections demo made {demo.Clips.Count} sections, not 4, or selected none");
                    EndVerificationRun(false);
                    return;
                }
                _vm.SeekTo(100);          // clear of the section edges, so the outline is visible
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                await Task.Delay(400);
            }
            if (App.DemoEditing && _vm.Tracks.FirstOrDefault() is { } song)
            {
                _vm.PixelsPerSecond = 22;
                _vm.ScrollSeconds = 20;
                _vm.SelectedTrack = song;
                foreach (double at in new[] { 34.0, 38.0 })
                {
                    _vm.SeekTo(at);
                    _vm.SplitAtPlayheadCommand.Execute(null);
                }
                _vm.AddEmptyTrackCommand.Execute(null);
                var empty = _vm.Tracks[^1];

                // Move the 4 s section onto the empty track, then loop it three more times.
                var section = song.Clips.First(c => Math.Abs(c.TimelineStartSeconds - 34) < 0.01);
                _vm.BeginMove(section);
                _vm.DragMoveTo(empty, 34, free: true);
                _vm.EndDrag();
                var moved = empty.Clips.First();
                _vm.LoopMode = true;
                _vm.BeginLoop(moved, left: false);
                _vm.DragLoopTo(moved.TimelineEndSeconds + 12, free: false);
                _vm.EndDrag();
                _vm.AddEmptyTrackCommand.Execute(null);                 // and one still empty
                _vm.SelectedTrack = empty;
                _vm.SelectClip(empty.Clips.OrderBy(c => c.TimelineStartSeconds).First());
                if (empty.Clips.Count != 4 || _vm.SelectedClip is null)
                {
                    App.Log($"ui probe failed: the editing demo left {empty.Clips.Count} sections on the empty track, not 4, or selected none");
                    EndVerificationRun(false);
                    return;
                }
                _vm.SnapLineSeconds = 38;                               // as a drag would show it
                _vm.SeekTo(60);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                await Task.Delay(400);
            }
            bool captured = CaptureTo(probe);
            if (captured) App.Log($"ui probe written to {probe}");
            EndVerificationRun(captured);
            return;
        }

        if (App.AutoCloseSeconds > 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(App.AutoCloseSeconds),
            };
            timer.Tick += (_, _) => { timer.Stop(); EndVerificationRun(); };
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
        var notes = new List<string>();          // what could not be tested — never counted as a pass
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
        // A scripted run has no audio device (App.UseAudioDevice), so this drives the offline
        // engine's transport — the same TransportSource the device pulls from — and nothing is
        // heard. The playhead does not advance offline; whether the transport is running is the
        // question, and the engine itself is asked, not the view-model's copy of it. Whether a
        // device really pulls in realtime is --playtest's job.
        _vm.MasterGain = 0;
        _vm.Session.Graph.MasterGain = 0;
        _vm.Session.Graph.SnapParameters();
        _vm.StopCommand.Execute(null);
        _vm.PlayFrom(18.5);
        Check($"double-click seeks (got {_vm.PositionSeconds:F2})", Math.Abs(_vm.PositionSeconds - 18.5) < 0.05);
        Check("double-click starts the transport", _vm.Session.Engine.State == Benchmix.Engine.Audio.TransportState.Playing);

        _vm.PlayFrom(25.0);
        Check("double-click while playing keeps playing", _vm.Session.Engine.State == Benchmix.Engine.Audio.TransportState.Playing);
        _vm.StopCommand.Execute(null);

        if (App.StartupFiles.Length > 0)
            Check($"the song given on the command line loaded ({_vm.Tracks.FirstOrDefault()?.LoadError ?? "ok"})",
                  _vm.Tracks.FirstOrDefault()?.IsLoaded == true);

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
            notes.Add("no track loaded, so the section commands are untested - pass a file to cover them");
        }

        // --- empty tracks, paste target, dragging between tracks, snapping, loop mode ---
        try { RunEditingGestures(Check, notes); }
        finally
        {
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _vm.LoopMode = false;
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
            notes.Add("no track loaded, so the knob clamp is untested - pass a file to cover it");
        }

        foreach (var p in pass) App.Log("gesturetest: PASS - " + p);
        foreach (var f in fail) App.Log("gesturetest: FAIL - " + f);
        foreach (var n in notes) App.Log("gesturetest: NOTE - " + n);
        string untested = notes.Count == 0 ? "" : $", {notes.Count} areas untested";
        App.Log($"gesturetest: verdict={(fail.Count == 0 ? $"PASS - {pass.Count} checks{untested}" : $"FAIL - {fail.Count} of {pass.Count + fail.Count}{untested}")}");
        return fail.Count == 0;
    }

    /// <summary>
    /// The 2026-09-24 editing features, through the real lane handlers with a scripted pointer.
    ///
    /// <para>
    /// Pointer positions are worked out from where the lanes actually are in the laid-out window,
    /// so this also proves that a y position picks the track the eye would pick. Nothing is sent to
    /// the real mouse or keyboard, and the Windows clipboard is only ever read — pasting a file
    /// copied in Explorer is not exercised here, because setting that up would overwrite the
    /// clipboard of whoever is using the machine.
    /// </para>
    /// </summary>
    private void RunEditingGestures(Action<string, bool> Check, List<string> notes)
    {
        // The shortcuts are XAML wiring, which no unit test can see.
        bool Bound(Key key, ICommand command) => InputBindings.OfType<KeyBinding>()
            .Any(b => b.Key == key && b.Modifiers == ModifierKeys.Control && ReferenceEquals(b.Command, command));
        Check("Ctrl+T is bound to add an empty track", Bound(Key.T, _vm.AddEmptyTrackCommand));
        Check("Ctrl+L is bound to loop mode", Bound(Key.L, _vm.ToggleLoopModeCommand));

        // A new P/Invoke, and a single-file exe is exactly where those fail to resolve.
        uint sequence = 0;
        try { sequence = MainViewModel.ClipboardSequence(); }
        catch (Exception ex) { App.Log("gesturetest: clipboard counter threw " + ex.Message); }
        Check($"the Windows clipboard counter resolves in this build (it reads {sequence})", sequence > 0);

        if (_vm.Tracks.FirstOrDefault() is not { IsLoaded: true } song)
        {
            notes.Add("no track loaded, so the editing gestures are untested - pass a file to cover them");
            return;
        }

        _vm.PixelsPerSecond = 40;
        _vm.ScrollSeconds = 40;
        double pps = _vm.PixelsPerSecond;
        double frame = 1.0 / _vm.Session.SampleRate;

        // --- an empty track ---
        int count = _vm.Tracks.Count;
        _vm.AddEmptyTrackCommand.Execute(null);
        var empty = _vm.Tracks[^1];
        Check("+ Empty track adds a track", _vm.Tracks.Count == count + 1);
        Check("the new track is empty and not reported as a missing song",
              empty.IsEmpty && empty.IsLoaded && empty.LoadError is null);
        Check("the new track is selected, so Ctrl+V goes to it", ReferenceEquals(_vm.SelectedTrack, empty));

        // --- paste lands on the selected track, at the playhead ---
        _vm.SelectedTrack = song;
        _vm.SeekTo(48);
        _vm.SplitAtPlayheadCommand.Execute(null);
        _vm.SeekTo(52);
        _vm.SplitAtPlayheadCommand.Execute(null);
        var four = song.Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - 48) < 0.01);
        Check($"a 4 s section to work with ({four?.TimelineLengthSeconds:0.00}s)",
              four is not null && Math.Abs(four.TimelineLengthSeconds - 4) < 0.01);
        if (four is null) return;

        _vm.SelectClip(four);
        _vm.CopyClipCommand.Execute(null);
        _vm.SelectedTrack = empty;
        _vm.SeekTo(44.5);
        _vm.PasteClipCommand.Execute(null);
        PumpUntil(() => empty.Clips.Count == 1);
        var pasted = empty.Clips.FirstOrDefault();
        Check("paste lands on the selected track, not the one it was copied from", pasted is not null);
        Check($"paste lands at the playhead (at {pasted?.TimelineStartSeconds:0.0000}s, playhead at 44.5)",
              pasted is not null && Math.Abs(pasted.TimelineStartSeconds - 44.5) <= frame);
        Check("the empty track took the tempo and pitch the section was playing at",
              empty.Model.Speed == song.Model.Speed && empty.Model.Semitones == song.Model.Semitones);

        // --- undo keeps the paste target where it was ---
        _vm.UndoCommand.Execute(null);
        var emptyId = empty.Id;
        PumpUntil(() => _vm.Tracks.FirstOrDefault(t => t.Id == emptyId) is { IsEmpty: true });
        Check("undo keeps the selected track selected", _vm.SelectedTrack?.Id == emptyId);
        empty = _vm.Tracks.First(t => t.Id == emptyId);                 // undo rebuilt every row
        song = _vm.Tracks.First(t => t.Id == song.Id);
        four = song.Clips.First(c => Math.Abs(c.TimelineStartSeconds - 48) < 0.01);

        UpdateLayout();
        PumpUntil(() => LaneOf(empty) is { ActualHeight: > 0 });
        var songLane = LaneOf(song);
        var emptyLane = LaneOf(empty);
        Check($"both lanes are laid out ({songLane?.ActualHeight:0} and {emptyLane?.ActualHeight:0} px high)",
              songLane is { ActualHeight: > 0 } && emptyLane is { ActualHeight: > 0 });
        if (songLane is null || emptyLane is null) return;

        Point At(FrameworkElement lane, double seconds)
        {
            var origin = lane.TranslatePoint(new Point(0, 0), this);
            return new Point(origin.X + (seconds - _vm.ScrollSeconds) * pps, origin.Y + lane.ActualHeight / 2);
        }

        // Presses, moves and releases through the lane handlers; returns the guide line shown just
        // before letting go.
        double? Drag(FrameworkElement lane, Point from, Point to)
        {
            _scriptedWindowPoint = from;
            LaneDown(lane, from);
            var mid = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            _scriptedWindowPoint = mid;
            LaneMove(lane, mid, true);
            _scriptedWindowPoint = to;
            LaneMove(lane, to, true);
            double? guide = _vm.SnapLineSeconds;
            LaneUp(lane);
            _scriptedWindowPoint = null;
            return guide;
        }

        Check("the pointer over the empty lane picks the empty track",
              ReferenceEquals(TrackAtWindowY(At(emptyLane, 50).Y), empty));
        Check("the pointer over the song's lane picks the song",
              ReferenceEquals(TrackAtWindowY(At(songLane, 50).Y), song));

        // --- a right-click selects the lane it lands on ---
        _vm.SelectedTrack = song;
        OnLaneRightButtonDown(emptyLane, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        { RoutedEvent = PreviewMouseRightButtonDownEvent });
        Check("a right-click selects that track, so the menu's Paste goes there", ReferenceEquals(_vm.SelectedTrack, empty));

        // --- drag the section down onto the empty track, 3 s later ---
        var sectionId = four.Id;
        _scriptedModifiers = ModifierKeys.Control;                  // free, so the landing point is exact
        Drag(songLane, At(songLane, 50), At(emptyLane, 53));
        _scriptedModifiers = null;

        var moved = empty.Clips.FirstOrDefault(c => c.Id == sectionId);
        Check("a section dragged down lands on the other track",
              moved is not null && song.Clips.All(c => c.Id != sectionId));
        Check($"it lands where it was dropped (at {moved?.TimelineStartSeconds:0.0000}s, expected 51)",
              moved is not null && Math.Abs(moved.TimelineStartSeconds - 51) <= frame);
        Check("the empty track took the section's tempo and pitch", empty.Model.Speed == song.Model.Speed);
        Check("the dropped section is still the selection", _vm.SelectedClip?.Id == sectionId);
        Check("the track it landed on becomes the selected track", ReferenceEquals(_vm.SelectedTrack, empty));
        if (moved is null) return;

        // --- snapping: dropped 6 px from the hole's edge at 48 s, it lands exactly on it ---
        var guide = Drag(emptyLane, At(emptyLane, 53), At(emptyLane, 53 - 2.85));
        moved = empty.Clips.First(c => c.Id == sectionId);
        Check($"a section dropped near another's edge snaps to it (at {moved.TimelineStartSeconds:0.00000}s)",
              Math.Abs(moved.TimelineStartSeconds - 48) <= frame);
        Check($"a guide line marks the edge it snapped to ({guide:0.000})", guide is { } g && Math.Abs(g - 48) <= frame);

        // --- Ctrl lets go of snapping ---
        _scriptedModifiers = ModifierKeys.Control;
        Drag(emptyLane, At(emptyLane, 50), At(emptyLane, 50.1));
        _scriptedModifiers = null;
        moved = empty.Clips.First(c => c.Id == sectionId);
        Check($"with Ctrl held it goes exactly where it was put (at {moved.TimelineStartSeconds:0.00000}s)",
              Math.Abs(moved.TimelineStartSeconds - 48.1) <= frame);

        // --- loop mode: drag the right end three lengths further ---
        _vm.LoopMode = true;
        double end = moved.TimelineEndSeconds;
        var grab = At(emptyLane, end - 2 / pps);
        Drag(emptyLane, grab, new Point(grab.X + 12 * pps, grab.Y));
        var repeats = empty.Clips.OrderBy(c => c.TimelineStartSeconds).ToList();
        Check($"loop mode turns the drag into repeats (got {repeats.Count} sections, expected 4)", repeats.Count == 4);
        Check("every repeat is the same length",
              repeats.All(c => Math.Abs(c.LengthSeconds - repeats[0].LengthSeconds) < 1e-9));
        var timeline = _vm.Session.VoiceFor(empty.Model)?.Timeline;
        Check("each repeat butts exactly against the last, so the engine crossfades every join",
              timeline is { Count: 4 } && timeline.ButtJoinAfter(0) && timeline.ButtJoinAfter(1) && timeline.ButtJoinAfter(2));

        // --- and one undo takes the whole loop back ---
        _vm.UndoCommand.Execute(null);
        PumpUntil(() => _vm.Tracks.FirstOrDefault(t => t.Id == emptyId)?.Clips.Count == 1);
        Check("one undo takes the whole loop back", _vm.Tracks.FirstOrDefault(t => t.Id == emptyId)?.Clips.Count == 1);
        _vm.LoopMode = false;

        // --- a song file put onto an empty track goes on that track, where it was aimed ---
        _vm.AddEmptyTrackCommand.Execute(null);
        var target = _vm.Tracks[^1];
        var placing = _vm.PlaceFilesAsync(target, new[] { song.Model.SourcePath }, 10);
        PumpUntil(() => placing.IsCompleted, 20000);
        var placed = target.Clips.FirstOrDefault();
        Check($"a file placed on an empty track lands there at 10.00s (at {placed?.TimelineStartSeconds:0.0000}s)",
              placed is not null && Math.Abs(placed.TimelineStartSeconds - 10) <= frame);
        Check($"and the track becomes that song ('{target.Name}')", target.Model.SourcePath == song.Model.SourcePath);

        // --- the menu's "Move section to playhead" goes the same way as a drag: exactly there ---
        UpdateLayout();
        if (placed is not null && LaneOf(target) is { } targetLane)
        {
            _vm.SelectClip(placed);
            _vm.SeekTo(3.25);
            OnMoveToPlayhead(targetLane, new RoutedEventArgs());
            var after = target.Clips.FirstOrDefault(c => c.Id == placed.Id);
            Check($"'Move section to playhead' lands it exactly there (at {after?.TimelineStartSeconds:0.00000}s)",
                  after is not null && Math.Abs(after.TimelineStartSeconds - 3.25) <= frame);
        }
    }

    /// <summary>
    /// The checks scripts/ui-verify.ps1 used to make from outside with UI Automation, made from
    /// inside: the real buttons pressed through their own automation peers — the same object an
    /// outside client ends up calling — and the real labels read back from the window. Expects the
    /// two songs ui-verify uses: Still D.R.E. at 93 BPM and T.R.O.Y. at 102, both G#m.
    /// </summary>
    private async Task<bool> RunButtonTestAsync()
    {
        var pass = new List<string>();
        var fail = new List<string>();
        void Check(string what, bool ok) => (ok ? pass : fail).Add(what);

        string Labels() => string.Join("|", Descendants<TextBlock>(this).Where(t => t.IsVisible).Select(t => t.Text));
        List<Button> Buttons(string content) => Descendants<Button>(this).Where(b => b.IsVisible && b.Content as string == content).ToList();

        async Task Click(Button button)
        {
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            // The peer queues the click; let it and the relayout after it run.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }

        UpdateLayout();
        var before = Labels();
        Check("both tracks loaded", before.Contains("Added 2 tracks"));
        Check("bed tempo from tag", System.Text.RegularExpressions.Regex.IsMatch(before, @"93\.0 BPM"));
        Check("topper tempo from tag", System.Text.RegularExpressions.Regex.IsMatch(before, @"102\.0 BPM"));
        Check("keys read from tags", before.Contains("G#m"));

        var match = Buttons("Match BPM");
        Check($"two Match BPM buttons (found {match.Count})", match.Count == 2);
        if (match.Count == 2)
        {
            await Click(match[1]);
            var after = Labels();
            Check("topper stretched to 93", System.Text.RegularExpressions.Regex.IsMatch(after, @"93\.0 BPM\s+\(was 102\.0\)"));
            Check("status explains it", after.Contains("-8.8% to hit 93.0 BPM"));
        }

        var reset = Buttons("Reset");
        Check($"Reset buttons present (found {reset.Count})", reset.Count >= 2);
        if (reset.Count >= 2)
        {
            await Click(reset[1]);
            var after = Labels();
            Check("topper back to 102", System.Text.RegularExpressions.Regex.IsMatch(after, @"102\.0 BPM"));
            Check("status confirms reset", after.Contains("original tempo, pitch and EQ"));
        }

        var key = Buttons("Match key");
        Check($"Match key buttons present (found {key.Count})", key.Count >= 2);
        if (key.Count >= 2)
        {
            await Click(key[1]);
            // Both tracks are G#m, so the right answer is no shift at all.
            Check("same key recognised", Labels().Contains("same key"));
        }

        foreach (var p in pass) App.Log("buttontest: PASS - " + p);
        foreach (var f in fail) App.Log("buttontest: FAIL - " + f);
        App.Log($"buttontest: verdict={(fail.Count == 0 ? $"PASS - {pass.Count} checks" : $"FAIL - {fail.Count} of {pass.Count + fail.Count}")}");
        return fail.Count == 0;
    }

    /// <summary>Every visual descendant of a given type, in tree order.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
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
        if (App.IsScriptedRun)
        {
            // Measured from the window handle, as Windows sees it — not read back from the WPF
            // properties this run set itself, which could not have come out any other way.
            bool onScreen = OutOfTheWay.IsOnAnyMonitor(this);
            bool taskbar = OutOfTheWay.HasTaskbarButton(this);
            bool activatable = OutOfTheWay.CanBeActivated(this);
            string device = _vm.Session.Engine.IsOffline ? "none" : $"'{_vm.Session.Engine.DeviceName}'";
            App.Log($"scripted run: off-screen={(onScreen ? "NO" : "yes")} taskbar button={(taskbar ? "YES" : "no")} "
                  + $"can be activated={(activatable ? "YES" : "no")} took focus={(_activatedDuringScriptedRun ? "YES" : "no")} "
                  + $"audio device={device}");
            if (onScreen || taskbar || activatable || _activatedDuringScriptedRun)
            {
                App.Log("scripted run: FAIL - it could get in the way of whoever was using the machine");
                passed = false;
            }
        }

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
    private bool CaptureTo(string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            if (w < 1 || h < 1)
            {
                App.Log($"ui probe failed: the window has no size ({w}x{h}), nothing to capture");
                return false;
            }

            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var fs = File.Create(path)) encoder.Save(fs);

            WriteControlMap(Path.ChangeExtension(path, ".map.tsv"), dpi);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("ui probe failed: " + ex);
            return false;
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
        // A scripted run always has an unsaved project, and asking about it would put a dialog on
        // the screen of whoever is working. It has nothing to keep.
        if (!App.IsScriptedRun && !_vm.CanClose()) { e.Cancel = true; return; }
        _vm.Dispose();
    }

    private bool _activatedDuringScriptedRun;

    /// <summary>
    /// Puts a scripted run where it cannot interrupt anyone: beyond the edge of every monitor, with
    /// no taskbar button, never activated. It is still a real, shown window — layout, rendering,
    /// UI Automation and RenderTargetBitmap all work exactly as they do on screen — it is just
    /// somewhere nobody is looking.
    /// </summary>
    private void KeepOutOfTheWay()
    {
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = SystemParameters.VirtualScreenLeft - Width - 2000;
        Top = SystemParameters.VirtualScreenTop - Height - 2000;

        // ShowActivated only covers the first show. This covers the rest of its life: Windows
        // never activates it, not even when the person at the machine closes or minimises their
        // own window and Windows looks for the next one to hand focus to.
        SourceInitialized += (_, _) => OutOfTheWay.NeverActivate(this);

        // Evidence rather than hope: if the window ever takes focus, the run says so and fails.
        Activated += (_, _) =>
        {
            _activatedDuringScriptedRun = true;
            App.Log("scripted run: the window was activated — it took focus from whoever was working");
        };
        // Launched minimised anyway, it would sit on the desktop as a title bar with no taskbar
        // button to hide behind; put it back where it belongs.
        StateChanged += (_, _) => { if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; };
    }



    // ---------------- drag and drop ----------------

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

        // Aimed at an empty track's lane, the song goes onto that track rather than a new one —
        // that is what the empty track was made for. Anywhere else still adds a track per file.
        if (pos.X > 392 && TrackAtWindowY(pos.Y) is { IsEmpty: true } empty)
        {
            await _vm.PlaceFilesAsync(empty, files, at);
            return;
        }

        await _vm.AddFilesAsync(files, at);
    }

    private static bool HasAudio(DragEventArgs e)
        => e.Data.GetDataPresent(DataFormats.FileDrop)
           && ((string[])e.Data.GetData(DataFormats.FileDrop)!).Any(IsAudio);

    private static bool IsAudio(string path) => MainViewModel.IsAudioFile(path);

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

    // ---------------- lanes: select, drag sections, seek ----------------

    /// <summary>Feeds the real lane width to the view-model, which Fit and the scrollbar both need.</summary>
    private void OnLaneSizeChanged(object sender, SizeChangedEventArgs e)
        => _vm.LaneWidthPx = e.NewSize.Width;

    private void OnTrackHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TrackViewModel vm) _vm.SelectedTrack = vm;
    }

    /// <summary>How close to a section's edge counts as grabbing the edge rather than the body.</summary>
    private const double EdgeGrabPixels = 7;

    private enum DragKind { Move, TrimStart, TrimEnd, LoopStart, LoopEnd }

    private TrackViewModel? _dragTrack;
    private ClipViewModel? _dragClip;
    private DragKind _dragKind;
    private Point _dragOrigin;
    private double _dragStartTimelineStart;
    private double _dragStartEdge;
    private bool _dragging;

    /// <summary>
    /// Scripted pointer for <c>--gesturetest</c>: a window position and the modifier keys held.
    /// Null in normal use. The lane gestures take positions rather than event args so the test can
    /// drive the very same code a mouse does without synthesising any real input.
    /// </summary>
    private Point? _scriptedWindowPoint;
    private ModifierKeys? _scriptedModifiers;

    private ModifierKeys Modifiers => _scriptedModifiers ?? Keyboard.Modifiers;

    private Point WindowPoint(MouseEventArgs e) => _scriptedWindowPoint ?? e.GetPosition(this);

    /// <summary>A window position as an x inside one lane.</summary>
    private double LaneX(FrameworkElement lane, Point window) => window.X - lane.TranslatePoint(new Point(0, 0), this).X;

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

    /// <summary>
    /// What pressing here would do. In loop mode an outer edge repeats the section instead of
    /// trimming it; the edge between two repeats of a loop is inside it, and moves the repeat.
    /// </summary>
    private DragKind KindAt(ClipViewModel clip, double x)
    {
        var edge = HitTestClip(clip, x);
        if (edge == DragKind.Move || !_vm.LoopMode) return edge;
        bool left = edge == DragKind.TrimStart;
        return _vm.CanLoopFrom(clip, left) ? (left ? DragKind.LoopStart : DragKind.LoopEnd) : DragKind.Move;
    }

    /// <summary>
    /// The track whose row is under a window y, or null outside the visible lanes. Only rows the
    /// scroller is actually showing count, or a drag over the ruler could land on a track scrolled
    /// out of view above it.
    /// </summary>
    private TrackViewModel? TrackAtWindowY(double y)
    {
        double top = TrackScroller.TranslatePoint(new Point(0, 0), this).Y;
        if (y < top || y > top + TrackScroller.ViewportHeight) return null;

        foreach (var t in _vm.Tracks)
        {
            if (TrackList.ItemContainerGenerator.ContainerFromItem(t) is not FrameworkElement row) continue;
            double rowTop = row.TranslatePoint(new Point(0, 0), this).Y;
            if (y >= rowTop && y < rowTop + row.ActualHeight) return t;
        }
        return null;
    }

    /// <summary>The lane element of a track's row, for the gesture test to aim at.</summary>
    private FrameworkElement? LaneOf(TrackViewModel t)
    {
        if (TrackList.ItemContainerGenerator.ContainerFromItem(t) is not ContentPresenter row) return null;
        row.ApplyTemplate();
        return row.ContentTemplate?.FindName("Lane", row) as FrameworkElement;
    }

    private void OnLaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (LaneDown((FrameworkElement)sender, WindowPoint(e))) e.Handled = true;
    }

    private void OnLaneMouseMove(object sender, MouseEventArgs e)
        => LaneMove((FrameworkElement)sender, WindowPoint(e), e.LeftButton == MouseButtonState.Pressed);

    private void OnLaneMouseUp(object sender, MouseButtonEventArgs e) => LaneUp((FrameworkElement)sender);

    /// <summary>
    /// Alt+Tab, a dialog, or a right-click mid-drag takes the mouse away without a button-up.
    /// The drag is finished as if it had been let go — otherwise the drop, the only place overlap
    /// is resolved, never happens, and two sections are left sounding at once.
    /// </summary>
    private void OnLaneLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragClip is not null) LaneUp((FrameworkElement)sender);
    }

    private bool LaneDown(FrameworkElement lane, Point window)
    {
        if (lane.DataContext is not TrackViewModel vm) return false;

        _vm.SelectedTrack = vm;
        double x = LaneX(lane, window);

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Alt-click puts the playhead here without disturbing anything.
            _vm.SeekTo(SecondsAtLaneX(x));
            return true;
        }

        var clip = ClipAtLaneX(vm, x);
        _vm.SelectClip(clip);
        if (clip is null) return true;                 // clicking a gap just clears the selection

        _dragTrack = vm;
        _dragClip = clip;
        _dragKind = KindAt(clip, x);

        // A section whose audio never loaded has no usable source span, so there is nothing to
        // trim or repeat. Treat it as a move instead.
        if (clip.SourceDurationSeconds <= 0.001) _dragKind = DragKind.Move;

        _dragOrigin = window;
        _dragStartTimelineStart = clip.TimelineStartSeconds;
        _dragStartEdge = _dragKind is DragKind.TrimStart or DragKind.LoopStart
            ? clip.TimelineStartSeconds
            : clip.TimelineEndSeconds;
        _dragging = false;
        if (_scriptedWindowPoint is null) lane.CaptureMouse();
        return true;
    }

    private void LaneMove(FrameworkElement lane, Point window, bool pressed)
    {
        // Hovering near an edge shows what grabbing it would do, so trimming and looping are
        // discoverable without a manual.
        if (_dragClip is null && lane.DataContext is TrackViewModel hover)
        {
            double hx = LaneX(lane, window);
            var over = ClipAtLaneX(hover, hx);
            lane.Cursor = over is null ? Cursors.Arrow : KindAt(over, hx) switch
            {
                DragKind.TrimStart or DragKind.TrimEnd => Cursors.SizeWE,
                DragKind.LoopStart => Cursors.ScrollW,
                DragKind.LoopEnd => Cursors.ScrollE,
                _ => Cursors.Arrow,
            };
        }

        if (_dragClip is null || _dragTrack is null || !(pressed || _scriptedWindowPoint is not null)) return;

        double dx = window.X - _dragOrigin.X;
        double dy = window.Y - _dragOrigin.Y;

        if (!_dragging)
        {
            // A few pixels of slop so a click does not nudge a section by accident. A move also
            // starts on a vertical pull alone, which is how a section goes to the track below.
            bool started = Math.Abs(dx) >= 4 || (_dragKind == DragKind.Move && Math.Abs(dy) >= 8);
            if (!started) return;
            _dragging = true;

            switch (_dragKind)
            {
                case DragKind.Move: _vm.BeginMove(_dragClip); break;
                case DragKind.TrimStart: _vm.BeginTrim(_dragClip, start: true); break;
                case DragKind.TrimEnd: _vm.BeginTrim(_dragClip, start: false); break;
                case DragKind.LoopStart: _vm.BeginLoop(_dragClip, left: true); break;
                case DragKind.LoopEnd: _vm.BeginLoop(_dragClip, left: false); break;
            }
        }

        // Ctrl is the one exception to snapping everywhere: it lets go of section edges, bar
        // lines and whole repeats, and the drag goes exactly where the pointer does.
        bool free = Modifiers.HasFlag(ModifierKeys.Control);
        double delta = dx / Math.Max(0.01, _vm.PixelsPerSecond);

        switch (_dragKind)
        {
            case DragKind.Move:
                _vm.DragMoveTo(TrackAtWindowY(window.Y), _dragStartTimelineStart + delta, free);
                break;
            case DragKind.TrimStart:
            case DragKind.TrimEnd:
                _vm.DragTrimTo(_dragStartEdge + delta, free);
                break;
            case DragKind.LoopStart:
            case DragKind.LoopEnd:
                _vm.DragLoopTo(_dragStartEdge + delta, free);
                break;
        }
        _vm.RecomputeLength();
    }

    private void LaneUp(FrameworkElement lane)
    {
        // State first: releasing capture raises LostMouseCapture, which comes straight back here.
        bool dragging = _dragging;
        _dragTrack = null;
        _dragClip = null;
        _dragging = false;

        if (_scriptedWindowPoint is null && lane.IsMouseCaptured) lane.ReleaseMouseCapture();
        if (dragging) _vm.EndDrag();
        else _vm.EndGesture();
    }

    /// <summary>
    /// A right-click selects the track and the section under it before the menu opens, so the
    /// menu acts on what was clicked — "Paste at playhead" included, which pastes onto the
    /// selected track.
    /// </summary>
    private void OnLaneRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var lane = (FrameworkElement)sender;
        if (lane.DataContext is not TrackViewModel vm) return;
        _vm.SelectedTrack = vm;
        _vm.SelectClip(ClipAtLaneX(vm, e.GetPosition(lane).X));
    }

    private void OnLaneContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (((FrameworkElement)sender).ContextMenu is not { } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Tag as string == "loopmode") item.IsChecked = _vm.LoopMode;
    }

    private void OnLoopModeMenu(object sender, RoutedEventArgs e) => _vm.LoopMode = ((MenuItem)sender).IsChecked;

    private void OnAddEmptyTrackMenu(object sender, RoutedEventArgs e) => _vm.AddEmptyTrackCommand.Execute(null);

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

        _vm.BeginTrim(c, start: true);
        _vm.DragTrimTo(_vm.PositionSeconds, free: true);
        _vm.EndDrag();
    }

    private void OnTrimEndToPlayhead(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        double into = (_vm.PositionSeconds - c.TimelineStartSeconds) * c.Track.Speed + c.SourceStartSeconds;
        if (into <= c.SourceStartSeconds + 0.05) { _vm.Status = "The playhead is at or before the section's start."; return; }

        _vm.BeginTrim(c, start: false);
        _vm.DragTrimTo(_vm.PositionSeconds, free: true);
        _vm.EndDrag();
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
        // The same path as a drag with Ctrl held: exact, before the track's start if need be, and
        // overwriting what it lands on rather than leaving two sections sounding at once.
        _vm.BeginMove(c);
        _vm.DragMoveTo(c.Track, _vm.PositionSeconds, free: true);
        _vm.EndDrag();
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
