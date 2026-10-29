using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Benchmix.App.ViewModels;
using Benchmix.Core;

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
        AddHandler(Controls.Knob.EditRequestedEvent, new RoutedEventHandler(OnKnobEditRequested));
        TrackScroller.ScrollChanged += (_, e) => { if (_editingKnob is not null && e.VerticalChange != 0) FinishKnobEdit(); };
        // Every row rebuilt — New, Open, undo — takes its knobs with it: a box left open over one is dropped.
        _vm.Tracks.CollectionChanged += (_, _) => { if (_editingKnob is not null) { _editingKnob = null; KnobValueBox.Visibility = Visibility.Collapsed; } };
        if (App.IsScriptedRun) KeepOutOfTheWay();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _vm.Initialise();

        // A project double-clicked in Explorer opens first; songs passed with it are added to it.
        bool projectOpened = App.StartupProject is not { } project || await _vm.OpenProjectFileAsync(project);
        if (App.StartupProject is not null)
        {
            App.Log($"startup project: {_vm.Status}");
            _vm.ZoomFitCommand.Execute(null);
        }

        if (App.StartupFiles.Length > 0)
        {
            await _vm.AddFilesAsync(App.StartupFiles);
            _vm.ZoomFitCommand.Execute(null);
        }

        if (App.LoadCheck)
        {
            if (!projectOpened)
            {
                App.Log($"loadcheck: verdict=FAIL - the project given on the command line did not open ({_vm.Status})");
                EndVerificationRun(false);
                return;
            }
            foreach (var t in _vm.Tracks)
                App.Log($"loadcheck: '{t.Name}' loaded={t.IsLoaded} error={t.LoadError ?? "none"} sections={t.Clips.Count} peaks={(t.Clips.Any(c => c.Peaks is not null) ? "yes" : "no")}");
            bool allLoaded = _vm.Tracks.Count > 0 && _vm.Tracks.All(t => t.IsLoaded);
            string opened = App.StartupProject is null ? "" : $"the project {Path.GetFileName(App.StartupProject)} opened from the command line, and ";
            App.Log($"loadcheck: verdict={(allLoaded ? $"PASS - {opened}every track is ready to split" : "FAIL - a track is not marked loaded")}");
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
                // A placed tempo ramp going down, and a second one with its tempo being typed.
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                _vm.RampMode = true;
                _vm.PlaceRampPoint(demo, 50, free: true);
                _vm.PlaceRampPoint(demo, 58, free: true);
                _vm.CommitRampEntry(demo.Id, (demo.Model.Bpm * demo.Model.Speed * 0.97).ToString("0.0", inv));
                _vm.PlaceRampPoint(demo, 33, free: true);
                _vm.PlaceRampPoint(demo, 42, free: true);
                demo.RampBpmDraft = (demo.Model.Bpm * demo.Model.Speed * 1.05).ToString("0.0", inv);
                if (demo.Model.Ramps.Count != 1 || !demo.IsTypingRampBpm)
                {
                    App.Log($"ui probe failed: the sections demo has {demo.Model.Ramps.Count} tempo ramps, not 1, or no tempo being typed");
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
                // A 200 ms crossfade picked for the third repeat's splice (2026-10-28), so the lane
                // draws the X at the three joins: two standard ones and that one.
                if (empty.Clips.Count == 4) empty.Clips.OrderBy(c => c.TimelineStartSeconds).ElementAt(2).CrossfadeSeconds = 0.2;
                if (empty.Clips.Count != 4 || _vm.SelectedClip is null || empty.ClipVisuals.Count(v => v.CrossfadeSeconds > 0) != 3)
                {
                    App.Log($"ui probe failed: the editing demo left {empty.Clips.Count} sections on the empty track, not 4, or selected none, or draws {empty.ClipVisuals.Count(v => v.CrossfadeSeconds > 0)} crossfade X's, not 3");
                    EndVerificationRun(false);
                    return;
                }
                // Tempo ramps up and then down on the song, drawn with LOOP MODE still on.
                double songBpm = song.Model.Bpm;
                _vm.PlaceRampPoint(song, 22, free: true);
                _vm.PlaceRampPoint(song, 28, free: true);
                _vm.CommitRampEntry(song.Id, (songBpm * 1.04).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                _vm.PlaceRampPoint(song, 44, free: true);
                _vm.PlaceRampPoint(song, 52, free: true);
                _vm.CommitRampEntry(song.Id, songBpm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                if (song.Model.Ramps.Count != 2 || !_vm.LoopMode)
                {
                    App.Log($"ui probe failed: the editing demo has {song.Model.Ramps.Count} tempo ramps on the song, not 2, or LOOP MODE went off");
                    EndVerificationRun(false);
                    return;
                }
                _vm.SnapLineSeconds = 38;                               // as a drag would show it

                // A marked stretch on the song, 29 s to 33 s, between its two ramps. Put there directly:
                // MARK MODE would turn LOOP MODE off, and the loop handles are in this picture too.
                _vm.SetMark(song, Benchmix.Core.TimelineEditor.TrackFrameAt(song.Model, 29, _vm.Session),
                                  Benchmix.Core.TimelineEditor.TrackFrameAt(song.Model, 33, _vm.Session));
                if (song.MarkVisual is not { } mark || Math.Abs(mark.StartSeconds - 29) > 0.001 || Math.Abs(mark.EndSeconds - 33) > 0.001)
                {
                    App.Log("ui probe failed: the editing demo's marked stretch is not 29 s to 33 s on the song");
                    EndVerificationRun(false);
                    return;
                }

                // Two warp markers on the song, right of the last ramp (it ends at 52 s), so nothing
                // before them moves: one pinned at 54 s, and one pinned at 58 s with its beat dragged
                // 0.8 s later, so the stretch between them is labelled. Put in through the view-model,
                // as the mark is: WARP MODE would turn LOOP MODE off.
                _vm.PressWarp(song, 54);
                _vm.ReleaseWarp();
                double? beat = _vm.PressWarp(song, 58);
                _vm.BeginWarpDrag();
                _vm.DragWarpTo((beat ?? 58) + 0.8, free: true);
                _vm.ReleaseWarp();
                if (song.Model.Warps.Count != 2 || song.WarpVisuals is not [{ Label: null }, { Label: not null }] || !_vm.LoopMode)
                {
                    App.Log($"ui probe failed: the editing demo has {song.Model.Warps.Count} warp markers on the song, not 2 with the stretch between them labelled, or LOOP MODE went off");
                    EndVerificationRun(false);
                    return;
                }
                _vm.SnapLineSeconds = 38;                               // the drag's release took the guide away

                // The box for typing a knob's value, open over the song's TEMPO number.
                UpdateLayout();
                if (StripOf(song) is { } strip && Descendants<Controls.Knob>(strip).FirstOrDefault(k => k.Label == "TEMPO") is { } knob)
                {
                    BeginKnobEdit(knob);
                    KnobValueBox.Text = "-4.21";
                }
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

        // The wheel click pauses where the playhead is (2026-10-28), and the double-click's jump back
        // lands on the very frame play started from, with the real audio thread pulling. A Stop
        // that seeks while a block is in flight lands a block late. Eight tries, at different
        // moments of the device's cycle. The pause takes hold at a block boundary, so "where it is"
        // allows the block in flight — a fifth of a second is far more than one — and must be past
        // where play started, never back at it.
        bool returned = true, stayed = true;
        for (int i = 0; i < 8; i++)
        {
            long from = (long)((20.0 + i * 0.731) * s.SampleRate);
            s.Engine.Seek(from);                       // in frames, so the comparison below is exact
            _vm.PlayPauseCommand.Execute(null);
            await Task.Delay(90 + i * 17);
            long at = s.Engine.PositionFrames;
            _vm.WheelClick();                          // a single click: pause where it is
            await Task.Delay(60);
            long paused = s.Engine.PositionFrames;
            bool inPlace = s.Engine.State == Benchmix.Engine.Audio.TransportState.Paused
                        && paused >= at && paused - at <= s.SampleRate / 5 && paused > from;
            stayed &= inPlace;
            _vm.StopAndReturn();                       // the double-click's second press
            long landed = s.Engine.PositionFrames;
            App.Log($"playtest: wheel-click {i + 1} started={from} clicked-at={at} paused={paused} ({(inPlace ? "in place" : "NOT in place")}) returned={landed} off={landed - from}");
            returned &= landed == from;
        }

        bool passed = realtime && returned && stayed;
        string verdict = passed ? "PASS - device is pulling in realtime, a wheel-click pauses where it is, and a double-click lands back where play started"
                       : !realtime ? "FAIL - stream is not running at realtime"
                       : !stayed ? "FAIL - a wheel-click did not pause where the playhead was"
                       : "FAIL - a double-click did not land back where play started";
        App.Log($"playtest: state={s.Engine.State} verdict={verdict}");

        _vm.StopCommand.Execute(null);
        EndVerificationRun(passed);
    }

    /// <summary>
    /// Measures how far a pitched track lands from an untouched one, inside the shipped exe.
    ///
    /// Synthetic audio, no file needed: half a second of silence then a tone, rendered twice —
    /// once straight through, once through the phase vocoder. The onset of each is unambiguous,
    /// and the gap between them is the thing that was 7200 frames wrong.
    ///
    /// <para>
    /// Then the same for a tempo ramp (2026-10-11): seven short 431 Hz bursts, played untouched
    /// and through the phase vocoder speeding up from 1.0 to 1.08 over 17.5 s. Each burst must
    /// come out where the ramp puts it. Native interop and timing are proved here, in the exe
    /// itself, because that is where they can fail when the tests pass.
    /// </para>
    /// <para>
    /// And for warp markers (2026-10-18): the same bursts, pitched, through two moved markers, where
    /// the speed jumps at each and the voice ends its runs there. Where each burst should land is the
    /// warp written out in the check, not asked of the map.
    /// </para>
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

        static float[] RenderOf(Action<Benchmix.Engine.Audio.TrackVoice> configure,
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
            return buf;
        }
        static int OnsetFrom(float[] buf, int from)
        {
            for (int f = Math.Max(0, from); f < buf.Length / 2; f++) if (Math.Abs(buf[f * 2]) > 0.05f) return f;
            return -1;
        }
        static int Onset(Action<Benchmix.Engine.Audio.TrackVoice> configure,
                         Benchmix.Engine.Audio.AudioClip clip, int frames) => OnsetFrom(RenderOf(configure, clip, frames), 0);

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

            // --- a tempo ramp ---
            var bursts = new float[Rate * 20 * 2];
            for (int k = 0; k < 7; k++)
            {
                int at = (int)((1.0 + 2.5 * k) * Rate);
                for (int f = 0; f < Rate / 4; f++)
                {
                    float v = (float)(0.7 * Math.Sin(2 * Math.PI * 431 * f / Rate));
                    bursts[(at + f) * 2] = v;
                    bursts[(at + f) * 2 + 1] = v;
                }
            }
            var burstClip = Benchmix.Engine.Audio.AudioClip.FromSamples("ramp bursts", bursts, Rate);
            var map = Benchmix.Engine.Audio.TempoMap.Build(1.0, new[] { new Benchmix.Engine.Audio.TempoMap.Ramp(0.5 * Rate, 18.0 * Rate, 1.08) });
            var untouched = RenderOf(_ => { }, burstClip, Rate * 20);
            var ramped = RenderOf(t =>
            {
                t.Mode = Benchmix.Engine.Audio.StretchMode.Independent;
                t.Tempo = map;
            }, burstClip, Rate * 20);

            bool rampAligned = true;
            int from = 0;
            for (int k = 0; k < 7; k++)
            {
                int source = OnsetFrom(untouched, from);
                if (source < 0) { rampAligned = false; break; }
                from = source + Rate;                                   // past this burst
                double expected = map.RelAt(source);
                int heard = OnsetFrom(ramped, (int)expected - 2400);
                double off = heard - expected;
                App.Log($"latencycheck: tempo ramp burst {k + 1} untouched={source} expected={expected:F0} ramped={heard} drift={off:F0} frames");
                rampAligned &= heard >= 0 && Math.Abs(off) < 256;
            }

            // --- warp markers (2026-10-18) ---
            // The same bursts, pitched, at TEMPO 1.04 through two moved markers: the audio at 5 s heard
            // when the tempo reaches 5.3 s, and at 12 s when it reaches 12.4 s. Each burst must come out
            // where the warp puts it, worked out here: a burst at audio a is heard when the tempo has
            // reached a ÷ (5 / 5.3) before the first marker, 5.3 + (a − 5) ÷ (7 / 7.1) between them,
            // and 12.4 + (a − 12) after the last — so the bursts after it show the last marker's move
            // carried on, which an unmoved one could not (review, 2026-10-18); the tempo reaches T at
            // T ÷ 1.04. No burst is near a marker, where the speed jumps.
            const double WarpTempo = 1.04, A1 = 5.0, T1 = 5.3, A2 = 12.0, T2 = 12.4;
            var warpMap = Benchmix.Engine.Audio.TempoMap.Warped(Benchmix.Engine.Audio.TempoMap.Constant(WarpTempo), new[]
            {
                new Benchmix.Engine.Audio.TempoMap.Pin(A1 * Rate, T1 * Rate),
                new Benchmix.Engine.Audio.TempoMap.Pin(A2 * Rate, T2 * Rate),
            });
            var warped = RenderOf(t =>
            {
                t.Mode = Benchmix.Engine.Audio.StretchMode.Independent;
                t.Semitones = 0.01;
                t.Tempo = warpMap;
            }, burstClip, Rate * 20);

            bool warpAligned = true;
            from = 0;
            for (int k = 0; k < 7; k++)
            {
                int source = OnsetFrom(untouched, from);
                if (source < 0) { warpAligned = false; break; }
                from = source + Rate;
                double a = source / (double)Rate;
                double tempo = a < A1 ? a / (A1 / T1)
                             : a < A2 ? T1 + (a - A1) / ((A2 - A1) / (T2 - T1))
                             : T2 + (a - A2);
                double expected = tempo / WarpTempo * Rate;
                int heard = OnsetFrom(warped, (int)expected - 2400);
                double off = heard - expected;
                App.Log($"latencycheck: warp burst {k + 1} untouched={source} expected={expected:F0} warped={heard} drift={off:F0} frames");
                warpAligned &= heard >= 0 && Math.Abs(off) < 256;
            }

            bool passed = aligned && rampAligned && warpAligned;
            App.Log($"latencycheck: verdict={(passed ? "PASS - the two land together in the published exe, and so do a track through a tempo ramp and a pitched track through moved warp markers"
                                                     : !aligned ? $"FAIL - {drift} frames apart"
                                                     : !rampAligned ? "FAIL - a track through a tempo ramp lands out of time"
                                                                    : "FAIL - a track through warp markers lands out of time")}");
            return passed;
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
    /// Clicks a menu item the way UI Automation does: the real item, its real Click handler, with
    /// the menu shut (opening a popup could put it on his screen). The click may be dispatched, so
    /// the caller pumps until its effect shows.
    /// </summary>
    private static void ClickMenuItem(MenuItem item)
    {
        var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
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

        // Before anything is undone (an undo rebuilds the row): the tab row shows the one tab from
        // the start. In v2026.10.27.7 it showed only the + until the first undo or Open.
        UpdateLayout();
        Check($"the tab row shows the one tab from the start ({_vm.Tabs.Count} in the row, {Descendants<Border>(TabRow).Count(b => b.Name == "TabChip")} chip(s) drawn)",
              _vm.Tabs.Count == 1 && _vm.Tabs[0].IsCurrent && Descendants<Border>(TabRow).Count(b => b.Name == "TabChip") == 1);

        // The zoom keys, on the top row and on the number pad alike (Daniel, 2026-10-28: Ctrl+0 on
        // the number pad did nothing). All Ctrl-modified, so none can fire inside a text box.
        bool ZoomKey(Key key, ICommand command) => InputBindings.OfType<KeyBinding>()
            .Any(b => b.Key == key && b.Modifiers == ModifierKeys.Control && ReferenceEquals(b.Command, command));
        Check("Ctrl+= and Ctrl+NumPad+ both zoom in", ZoomKey(Key.OemPlus, _vm.ZoomInCommand) && ZoomKey(Key.Add, _vm.ZoomInCommand));
        Check("Ctrl+- and Ctrl+NumPad- both zoom out", ZoomKey(Key.OemMinus, _vm.ZoomOutCommand) && ZoomKey(Key.Subtract, _vm.ZoomOutCommand));
        Check("Ctrl+0 and Ctrl+NumPad0 both fit", ZoomKey(Key.D0, _vm.ZoomFitCommand) && ZoomKey(Key.NumPad0, _vm.ZoomFitCommand));

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

        // --- up and down (Daniel, 2026-10-27: "vertical scrolling is biggest pain at the moment"): a
        // right-drag pans the tracks up and down as well, and PageUp/PageDown, Ctrl+Home/Ctrl+End
        // scroll them from the keyboard. Eight empty tracks make room to scroll; undone after. ---
        int tracksBefore = _vm.Tracks.Count;
        for (int i = 0; i < 8; i++) _vm.AddEmptyTrackCommand.Execute(null);
        UpdateLayout();
        bool room = TrackScroller.ScrollableHeight > 100;
        Check($"eight empty tracks give the list room to scroll ({TrackScroller.ScrollableHeight:0} px)", room);
        KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, key) { RoutedEvent = PreviewKeyDownEvent };
        if (room && PresentationSource.FromVisual(this) is not null)
        {
            TrackScroller.ScrollToVerticalOffset(0);
            UpdateLayout();
            _scriptedPointerX = 500;
            _scriptedPointerY = 300;
            OnTimelineRightButtonDown(TimelineGrid, down);
            _scriptedPointerY = 220;                       // pulled up by 80 px: the tracks follow the hand
            OnTimelinePreviewMouseMove(TimelineGrid, move);
            UpdateLayout();
            double offset = TrackScroller.VerticalOffset;
            up.Handled = false;
            OnTimelineRightButtonUp(TimelineGrid, up);
            Check($"pulling up with the right button scrolls the tracks down by 80 px (offset {offset:0})", Math.Abs(offset - 80) < 0.5);
            Check("a pan up or down suppresses the clip menu too", ConsumeContextMenuSuppression());
            _scriptedPointerX = null;
            _scriptedPointerY = null;

            _scriptedFocus = this;
            TrackScroller.ScrollToVerticalOffset(0);
            UpdateLayout();
            var key = Press(Key.PageDown);
            OnWindowPreviewKeyDown(this, key);
            UpdateLayout();
            Check($"PageDown scrolls the tracks a screen down (offset {TrackScroller.VerticalOffset:0}, screen {TrackScroller.ViewportHeight:0} px)",
                  key.Handled && TrackScroller.VerticalOffset > 0);
            key = Press(Key.PageUp);
            OnWindowPreviewKeyDown(this, key);
            UpdateLayout();
            Check("PageUp scrolls them back up", key.Handled && TrackScroller.VerticalOffset == 0);
            _scriptedModifiers = ModifierKeys.Control;
            key = Press(Key.End);
            OnWindowPreviewKeyDown(this, key);
            UpdateLayout();
            Check($"Ctrl+End goes to the bottom (offset {TrackScroller.VerticalOffset:0} of {TrackScroller.ScrollableHeight:0})",
                  key.Handled && Math.Abs(TrackScroller.VerticalOffset - TrackScroller.ScrollableHeight) < 0.5);
            key = Press(Key.Home);
            OnWindowPreviewKeyDown(this, key);
            UpdateLayout();
            Check("Ctrl+Home goes back to the top", key.Handled && TrackScroller.VerticalOffset == 0);
            _scriptedModifiers = null;
            _scriptedFocus = BpmBox;
            key = Press(Key.PageDown);
            OnWindowPreviewKeyDown(this, key);
            UpdateLayout();
            Check("PageDown with PROJECT BPM focused is left to the box", !key.Handled && TrackScroller.VerticalOffset == 0);
            _scriptedFocus = null;

            // The plain wheel itself (Daniel, 2026-10-27): a notch down over a waveform, through the
            // lane's own routing up to the scroller, scrolls the tracks down; over the ruler the
            // ruler's handler does it.
            if (_vm.Tracks.FirstOrDefault(t => t.IsLoaded) is { } loaded && LaneOf(loaded) is { } lane0)
            {
                TrackScroller.ScrollToVerticalOffset(0);
                UpdateLayout();
                lane0.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = MouseWheelEvent, Source = lane0 });
                UpdateLayout();
                Check($"a plain notch of the wheel down over a waveform scrolls the tracks down (offset {TrackScroller.VerticalOffset:0})", TrackScroller.VerticalOffset > 0);
            }
            else notes.Add("no loaded track, so the plain wheel over a waveform is untested");
            TrackScroller.ScrollToVerticalOffset(0);
            UpdateLayout();
            var r0 = RulerHost.TranslatePoint(new Point(0, 0), this);
            _scriptedWindowPoint = new Point(r0.X + 300, r0.Y + RulerHost.ActualHeight / 2);
            _scriptedModifiers = ModifierKeys.None;
            var rulerWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = PreviewMouseWheelEvent };
            OnRulerWheel(RulerHost, rulerWheel);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            UpdateLayout();
            Check($"a plain notch down over the ruler scrolls the tracks down too (offset {TrackScroller.VerticalOffset:0})", rulerWheel.Handled && TrackScroller.VerticalOffset > 0);
            TrackScroller.ScrollToVerticalOffset(0);
            UpdateLayout();
        }
        Check("none of PageUp, PageDown, Home and End is a bare KeyBinding, so they cannot fire inside a text box",
              !InputBindings.OfType<KeyBinding>().Any(b => b.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End));
        for (int i = 0; i < 8; i++) _vm.UndoCommand.Execute(null);
        PumpUntil(() => _vm.Tracks.Count == tracksBefore);
        Check($"the eight empty tracks are undone again ({_vm.Tracks.Count} tracks, want {tracksBefore})", _vm.Tracks.Count == tracksBefore);

        // --- double-click on the ruler plays from that point ---
        // A scripted run has no audio device (App.UseAudioDevice), so this drives the offline
        // engine's transport — the same TransportSource the device pulls from — and nothing is
        // heard. The playhead does not advance offline; whether the transport is running is the
        // question, and the engine itself is asked, not the view-model's copy of it. Whether a
        // device really pulls in realtime is --playtest's job.
        _vm.MasterGain = 0;
        _vm.Session.Graph.MasterGain = 0;
        _vm.Session.Graph.SnapParameters();
        //
        // The presses go through the ruler's own handler with a scripted pointer and click count:
        // until 2026-10-11 these checks called PlayFrom directly, which proved nothing about the ruler.
        var engine = _vm.Session.Engine;
        double oneFrame = 1.0 / _vm.Session.SampleRate;
        Point Ruler(double s)
        {
            var o = RulerHost.TranslatePoint(new Point(0, 0), this);
            return new Point(o.X + (s - _vm.ScrollSeconds) * _vm.PixelsPerSecond, o.Y + RulerHost.ActualHeight / 2);
        }
        void DoubleClickRuler(double s)
        {
            var p = Ruler(s);
            _scriptedWindowPoint = p;
            RulerDown(RulerHost, p, 1); RulerUp(RulerHost);
            RulerDown(RulerHost, p, 2); RulerUp(RulerHost);
            _scriptedWindowPoint = null;
        }
        UpdateLayout();
        _vm.StopCommand.Execute(null);
        DoubleClickRuler(18.5);
        Check($"double-click on the ruler seeks (engine at {engine.PositionSeconds:F3}s)",
              Math.Abs(engine.PositionSeconds - 18.5) <= 2 * oneFrame);
        Check("double-click on the ruler starts the transport", engine.State == Benchmix.Engine.Audio.TransportState.Playing);

        DoubleClickRuler(25.0);
        Check($"double-click on the ruler while playing jumps there and keeps playing (engine at {engine.PositionSeconds:F3}s)",
              engine.State == Benchmix.Engine.Audio.TransportState.Playing && Math.Abs(engine.PositionSeconds - 25.0) <= 2 * oneFrame);
        _vm.StopCommand.Execute(null);

        _scriptedModifiers = ModifierKeys.Shift;
        _scriptedWindowPoint = Ruler(20);
        RulerDown(RulerHost, Ruler(20), 1); RulerUp(RulerHost);
        RulerDown(RulerHost, Ruler(20), 2);
        _scriptedWindowPoint = Ruler(22);
        RulerMove(RulerHost, Ruler(22));
        RulerUp(RulerHost);
        _scriptedWindowPoint = null;
        _scriptedModifiers = null;
        Check($"Shift+double-click on the ruler still sets a listening loop and does not play ({_vm.LoopStart:0.00}s to {_vm.LoopEnd:0.00}s)",
              _vm.LoopStart is { } ls && _vm.LoopEnd is { } le && Math.Abs(ls - 20) < 0.01 && Math.Abs(le - 22) < 0.01
              && engine.State != Benchmix.Engine.Audio.TransportState.Playing);
        _vm.ClearLoopCommand.Execute(null);

        // --- a click of the scroll wheel stops and goes back to where play started ---
        try { RunWheelClickGestures(Check, notes); }
        finally
        {
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _vm.StopCommand.Execute(null);
        }

        // --- the wheel over the ruler moves the playhead ---
        try { RunScrubGestures(Check, notes); }
        finally
        {
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _vm.StopCommand.Execute(null);
        }

        // --- a double-click on a lane, or under the tracks, plays from there ---
        try { RunDoubleClickGestures(Check, notes); }
        finally
        {
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _vm.StopCommand.Execute(null);
        }

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

            // --- several sections at once (Daniel, 2026-10-27: "MUST be able to select multiple clips
            // to copy/paste them"; built 2026-10-29) ---
            try { RunGroupClipboardGestures(Check, notes, t1); }
            finally
            {
                _scriptedModifiers = null;
                _scriptedWindowPoint = null;
            }
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

        // --- selecting several sections: Ctrl+click, Shift+click, Delete; and tooltips ---
        try { RunSelectionGestures(Check, notes); }
        finally
        {
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _scriptedFocus = null;
        }

        // --- selecting several tracks: Ctrl+press, Shift+press, a press on a button; Match BPM's right-click menu ---
        try { RunTrackSelectionGestures(Check, notes); }
        finally
        {
            _scriptedModifiers = null;
        }

        // --- renaming a track, and Space as a bare key that leaves text boxes alone ---
        try { RunRenameGestures(Check, notes); }
        finally
        {
            _scriptedFocus = null;
            _vm.CommitOpenRenames();
            _vm.StopCommand.Execute(null);
        }

        // --- dragging a track up or down ---
        try { RunReorderGestures(Check, notes); }
        finally
        {
            EndReorder();
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }

        // --- the selection box ---
        try { RunBandGestures(Check, notes); }
        finally
        {
            BandLost();
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }

        // --- dragging a group of selected sections ---
        try { RunGroupGestures(Check, notes); }
        finally
        {
            if (_dragClip is not null) LaneUp(LaneOf(_dragTrack!) ?? this);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _vm.StopCommand.Execute(null);
        }

        // --- typing a knob's value ---
        try { RunKnobTypingGestures(Check, notes); }
        finally { if (_editingKnob is not null) CancelKnobEdit(); }

        // --- joining sections back together ---
        try { RunJoinGestures(Check, notes); }
        finally
        {
            if (_dragClip is not null) LaneUp(LaneOf(_dragTrack!) ?? this);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }

        // --- tempo ramps ---
        try { RunTempoRampGestures(Check, notes); }
        finally
        {
            if (_rampGrab is { } held) LaneUp(LaneOf(held.Track) ?? this);
            _vm.RampMode = false;
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _scriptedFocus = null;
        }

        // --- marking a stretch ---
        try { RunMarkGestures(Check, notes); }
        finally
        {
            if (_markPress is { } held) LaneUp(LaneOf(held.Track) ?? this);
            _vm.MarkMode = false;
            _vm.StopCommand.Execute(null);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _scriptedFocus = null;
        }

        // --- warp markers ---
        try { RunWarpGestures(Check, notes); }
        finally
        {
            if (_warpPress is { } held) LaneUp(LaneOf(held.Track) ?? this);
            _vm.WarpMode = false;
            _vm.StopCommand.Execute(null);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _scriptedFocus = null;
        }

        // --- tabs ---
        try { RunTabGestures(Check, notes); }
        finally
        {
            _vm.StopCommand.Execute(null);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            _scriptedFocus = null;
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
    /// Copy, paste, repeat and cut with several sections selected (Daniel, 2026-10-27: "MUST be
    /// able to select multiple clips to copy/paste them"; built 2026-10-29), through the real
    /// commands: Ctrl+C with two selected copies both; Ctrl+V puts both down at the playhead on the
    /// track chosen, the same distance apart to the frame; Ctrl+D repeats the pair on the very frame
    /// it ends and the copies become the selection; Ctrl+X cuts both; pasted over a long section,
    /// what lay under the gap between the two is still there; a group off two tracks keeps its rows,
    /// and chosen on the last track gets an empty track added for its lower row; the lane menu names
    /// the count. Everything is undone after.
    /// </summary>
    private void RunGroupClipboardGestures(Action<string, bool> Check, List<string> notes, TrackViewModel t1)
    {
        var session = _vm.Session;
        int tracksBefore = _vm.Tracks.Count, clipsBefore = t1.Clips.Count, steps = 0;
        var ordered = t1.Clips.OrderBy(c => c.TimelineStartSeconds).ToList();
        if (ordered.Count < 3) { notes.Add("fewer than three sections, so copying several at once is untested"); return; }
        Guid aId = ordered[0].Id, cId = ordered[2].Id;
        ClipViewModel A() => t1.Clips.First(x => x.Id == aId);     // the rows are rebuilt after every edit
        ClipViewModel C() => t1.Clips.First(x => x.Id == cId);
        long span = TimelineEditor.StartFrameOf(C().Model, session) - TimelineEditor.StartFrameOf(A().Model, session);
        long need = span + TimelineEditor.LengthFramesOf(C().Model, session);

        _vm.SelectClip(A());
        _vm.ToggleClipSelection(C());
        _vm.CopyClipCommand.Execute(null);
        Check($"Ctrl+C with two sections selected copies both (\"{_vm.Status}\")", _vm.HasClipboard && _vm.Status.StartsWith("Copied 2 sections"));

        _vm.AddEmptyTrackCommand.Execute(null);
        steps++;
        var empty = _vm.SelectedTrack;
        Check("an empty track for the paste is there and selected", empty is not null && empty.IsEmpty && _vm.Tracks.Count == tracksBefore + 1);
        if (empty is null) return;
        _vm.SeekTo(200);
        _vm.PasteClipCommand.Execute(null);
        steps++;
        var put = empty.Clips.OrderBy(x => x.TimelineStartSeconds).ToList();
        Check($"Ctrl+V puts both down on the chosen track (got {put.Count}; \"{_vm.Status}\")", put.Count == 2 && _vm.Status.StartsWith("Pasted 2 sections"));
        if (put.Count == 2)
        {
            long apart = TimelineEditor.StartFrameOf(put[1].Model, session) - TimelineEditor.StartFrameOf(put[0].Model, session);
            Check($"the first lands at the playhead (at {put[0].TimelineStartSeconds:0.00}s)", Math.Abs(put[0].TimelineStartSeconds - 200) < 0.05);
            Check($"the second keeps its distance from the first, to the frame ({apart} frames, want {span})", apart == span);
            Check("both pasted sections are selected, the first the one commands act on", put.All(x => x.IsSelected) && ReferenceEquals(_vm.SelectedClip, put[0]));

            _vm.DuplicateClipCommand.Execute(null);
            steps++;
            var after = empty.Clips.OrderBy(x => x.TimelineStartSeconds).ToList();
            Check($"Ctrl+D with both selected repeats the pair (got {after.Count}; \"{_vm.Status}\")", after.Count == 4 && _vm.Status.StartsWith("Repeated 2 sections"));
            if (after.Count == 4)
            {
                Check($"the repeat starts on the very frame the pair ends ({TimelineEditor.StartFrameOf(after[2].Model, session)} vs {TimelineEditor.EndFrameOf(after[1].Model, session)})",
                      TimelineEditor.StartFrameOf(after[2].Model, session) == TimelineEditor.EndFrameOf(after[1].Model, session));
                Check("the copies are now the selection, so Ctrl+D again walks on", after[2].IsSelected && after[3].IsSelected && !after[0].IsSelected);
                _vm.CutClipCommand.Execute(null);
                steps++;
                Check($"Ctrl+X with both selected cuts both, leaving the gaps (got {empty.Clips.Count}; \"{_vm.Status}\")", empty.Clips.Count == 2 && _vm.Status.StartsWith("Cut 2 sections"));
            }
        }

        // What lies under the gap between the two stays: pasted into the middle of a long section,
        // the piece of it between them is still there.
        var longest = t1.Clips.OrderByDescending(x => x.TimelineLengthSeconds).First();
        if (TimelineEditor.LengthFramesOf(longest.Model, session) > need + session.SampleRate * 2)
        {
            _vm.SelectClip(A());
            _vm.ToggleClipSelection(C());
            _vm.CopyClipCommand.Execute(null);
            _vm.SelectedTrack = t1;
            _vm.SeekTo(longest.TimelineStartSeconds + 1);
            int before = t1.Clips.Count;
            _vm.PasteClipCommand.Execute(null);
            steps++;
            var landed = t1.Clips.Where(x => x.IsSelected).OrderBy(x => x.TimelineStartSeconds).ToList();
            bool between = landed.Count == 2 && t1.Clips.Any(x => !x.IsSelected
                && TimelineEditor.LengthFramesOf(x.Model, session) > 0
                && TimelineEditor.StartFrameOf(x.Model, session) >= TimelineEditor.EndFrameOf(landed[0].Model, session)
                && TimelineEditor.EndFrameOf(x.Model, session) <= TimelineEditor.StartFrameOf(landed[1].Model, session));
            Check($"pasted over a long section, what lay under the gap between the two is still there ({t1.Clips.Count} sections, was {before})", between);
        }
        else notes.Add("no section long enough to paste a pair into, so the gap rule is untested here");

        // A group off two tracks: its rows land on the chosen track and the one below; chosen on the
        // last track, an empty track is added for the lower row.
        if (empty.Clips.Count >= 1)
        {
            var e0 = empty.Clips.OrderBy(x => x.TimelineStartSeconds).First();
            _vm.SelectClip(A());
            _vm.ToggleClipSelection(e0);
            _vm.CopyClipCommand.Execute(null);
            Check($"a copy off two tracks says so (\"{_vm.Status}\")", _vm.Status.Contains("2 sections on 2 tracks"));
            _vm.SelectedTrack = empty;             // the last track: the lower row has nowhere to go yet
            _vm.SeekTo(400);
            int count = _vm.Tracks.Count;
            _vm.PasteClipCommand.Execute(null);
            steps++;
            var below = _vm.Tracks.LastOrDefault();
            Check($"pasted onto the last track, an empty track is added for the lower row ({_vm.Tracks.Count} tracks, was {count}; \"{_vm.Status}\")",
                  _vm.Tracks.Count == count + 1 && _vm.Status.Contains("Added") && below is not null && !ReferenceEquals(below, empty));
            if (below is not null && !ReferenceEquals(below, empty))
            {
                var top = empty.Clips.FirstOrDefault(x => x.IsSelected);
                var low = below.Clips.FirstOrDefault(x => x.IsSelected);
                Check($"the top row lands on the chosen track at the playhead and the lower row on the new track ({top?.TimelineStartSeconds:0.00}s / {low?.TimelineStartSeconds:0.00}s)",
                      top is not null && low is not null && Math.Abs(top.TimelineStartSeconds - 400) < 0.05 && low.TimelineStartSeconds >= 400 - 0.05);
            }
        }

        // The lane menu names the count while several are selected.
        var two = t1.Clips.OrderBy(x => x.TimelineStartSeconds).ToList();
        _vm.SelectClip(two[0]);
        if (two.Count >= 2) _vm.ToggleClipSelection(two[1]);
        if (LaneOf(t1) is { ContextMenu: { } menu } lane)
        {
            PrepareLaneMenu(lane);
            var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "").ToList();
            Check($"with two sections selected the lane menu says Cut 2 sections, Copy 2 sections, Repeat 2 sections and Delete 2 sections ({string.Join(" | ", headers.Where(h => h.Contains("section")))})",
                  headers.Contains("Cut 2 sections") && headers.Contains("Copy 2 sections") && headers.Contains("Repeat 2 sections") && headers.Contains("Delete 2 sections"));
            _vm.SelectClip(two[0]);
            PrepareLaneMenu(lane);
            headers = menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "").ToList();
            Check("with one selected it says Cut section and Copy section again", headers.Contains("Cut section") && headers.Contains("Copy section"));
        }

        for (int i = 0; i < steps; i++)
        {
            _vm.UndoCommand.Execute(null);
            PumpUntil(() => !_vm.IsBusy);
        }
        PumpUntil(() => _vm.Tracks.Count == tracksBefore && _vm.Tracks.First().Clips.Count == clipsBefore);
        Check($"undoing it all puts the tracks and sections back ({_vm.Tracks.Count} tracks, {_vm.Tracks.First().Clips.Count} sections; want {tracksBefore}, {clipsBefore})",
              _vm.Tracks.Count == tracksBefore && _vm.Tracks.First().Clips.Count == clipsBefore);
    }

    /// <summary>
    /// Selecting several tracks (Daniel, 2026-10-28: "must be able to select multiple tracks"),
    /// through the strip's real press handler with scripted keys: a plain press selects one,
    /// Ctrl+press flips one in or out, Shift+press takes the range, a press on a button keeps the
    /// selection, a right-click keeps or replaces it, a plain press on another strip lets it go;
    /// and Match BPM's right-click menu says what it would do. Two empty tracks are added for
    /// company and undone after.
    /// </summary>
    private void RunTrackSelectionGestures(Action<string, bool> Check, List<string> notes)
    {
        if (_vm.Tracks.Count == 0) { notes.Add("no track, so selecting several tracks is untested - pass a file to cover it"); return; }
        int before = _vm.Tracks.Count;
        _vm.AddEmptyTrackCommand.Execute(null);
        _vm.AddEmptyTrackCommand.Execute(null);
        UpdateLayout();
        var rows = _vm.Tracks.ToList();
        Check($"three tracks to choose between (got {rows.Count})", rows.Count >= 3);
        if (rows.Count < 3) return;
        var (a, b, c) = (rows[0], rows[1], rows[2]);
        var leftDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent };
        var rightDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = PreviewMouseRightButtonDownEvent };

        string Picked() => string.Join("+", _vm.SelectedTracks.Select(t => rows.IndexOf(t)));
        void Press(TrackViewModel t, ModifierKeys keys)
        {
            if (StripOf(t) is not { } strip) { Check($"the strip of track {rows.IndexOf(t)} is on screen", false); return; }
            _scriptedModifiers = keys;
            OnTrackHeaderClicked(strip, leftDown);
            _scriptedModifiers = ModifierKeys.None;         // the keys go up with the button
        }

        Press(b, ModifierKeys.None);
        Check($"a plain press on a strip selects that track alone (got {Picked()})", Picked() == "1" && ReferenceEquals(_vm.SelectedTrack, b));

        Press(c, ModifierKeys.Control);
        Check($"Ctrl+press on another strip adds its track (got {Picked()})", Picked() == "1+2");
        Check("the track Ctrl+press added is the one commands act on", ReferenceEquals(_vm.SelectedTrack, c));
        Check($"the status says what the selection is for (\"{_vm.Status}\")", _vm.Status.Contains("2 tracks selected") && _vm.Status.Contains("Match BPM"));

        Press(c, ModifierKeys.Control);
        Check($"Ctrl+press on a selected track takes it out again (got {Picked()})", Picked() == "1");
        Check("commands fall back to a track still selected", ReferenceEquals(_vm.SelectedTrack, b));

        Press(a, ModifierKeys.None);
        Press(c, ModifierKeys.Shift);
        Check($"Shift+press selects every track between (got {Picked()})", Picked() == "0+1+2");
        Check("after Shift+press the track pressed last is the one commands act on", ReferenceEquals(_vm.SelectedTrack, c));
        Press(b, ModifierKeys.Shift);
        Check($"a further Shift+press measures from the same anchor (got {Picked()})", Picked() == "0+1" && ReferenceEquals(_vm.SelectedTrack, b));

        // A press on one of a selected track's buttons keeps the selection: that is how Match key
        // and Match BPM's menu get to act on the lot. Raised for real as Mouse.PreviewMouseDown,
        // which tunnels from the window down to the button and is cracked into
        // PreviewMouseLeftButtonDown at each element on the way, the strip's handler included.
        // (PreviewMouseLeftButtonDown itself is a direct event: raised on the button, it reaches
        // the button alone, and the strip never hears of it.)
        var button = StripOf(a) is { } stripA ? Descendants<Button>(stripA).FirstOrDefault(x => x.Content as string == "Match key") : null;
        Check("the first track's strip has a Match key button", button is not null);
        if (button is not null)
        {
            _scriptedModifiers = ModifierKeys.None;
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check($"a press on a selected track's button keeps the selection and makes that track the one commands act on (got {Picked()}, acting on {rows.IndexOf(_vm.SelectedTrack!)})",
                  Picked() == "0+1" && ReferenceEquals(_vm.SelectedTrack, a));
            _scriptedModifiers = ModifierKeys.Control;
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            _scriptedModifiers = ModifierKeys.None;
            Check($"Ctrl on a button is left to the button, not read as a toggle (got {Picked()})", Picked() == "0+1");
        }

        // A right-click on a strip keeps a selection its track is part of (the menu then acts on
        // that track) and selects alone a track outside it.
        if (StripOf(b) is { } stripB) OnTrackHeaderRightClicked(stripB, rightDown);
        Check($"a right-click on a selected strip keeps the selection (got {Picked()})", Picked() == "0+1" && ReferenceEquals(_vm.SelectedTrack, b));
        if (StripOf(c) is { } stripC) OnTrackHeaderRightClicked(stripC, rightDown);
        Check($"a right-click on a strip outside the selection selects that track alone (got {Picked()})", Picked() == "2");

        // Match BPM's right-click menu: one item that says what it would do, with the average in it
        // — every track with a tempo while fewer than two are selected, the selected ones otherwise
        // — or greyed out, saying why, with fewer than two tempos. (An empty track that has been
        // given a section earlier in this run carries that song's tempo, so there may be several.)
        // The --buttontest run, with two songs, drives the item itself.
        Press(a, ModifierKeys.None);
        var matchBpm = StripOf(a) is { } stripA2 ? Descendants<Button>(stripA2).FirstOrDefault(x => x.Content as string == "Match BPM") : null;
        var menu = matchBpm?.ContextMenu;
        var item = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "average");
        Check("Match BPM has a right-click menu with the average item", item is not null);
        if (menu is not null && item is not null)
        {
            var withTempo = _vm.Tracks.Where(t => t.OriginalBpm > 0).ToList();
            FillMatchAverageMenu(menu);
            if (withTempo.Count >= 2)
            {
                var reference = withTempo.Contains(a) ? a : withTempo[0];
                double all = Benchmix.Engine.Analysis.Analyzer.AverageBpm(withTempo.Select(t => t.OriginalBpm).ToList(), reference.OriginalBpm);
                Check($"with one track selected the item offers every track with a tempo and their average ('{item.Header}'; want all {withTempo.Count} at {all:0.0})",
                      item.IsEnabled && item.Header is string h && h.Contains($"all {withTempo.Count} tracks") && h.Contains($"{all:0.0} BPM"));
                if (withTempo.FirstOrDefault(t => !ReferenceEquals(t, a)) is { } other && withTempo.Contains(a))
                {
                    Press(other, ModifierKeys.Control);
                    FillMatchAverageMenu(menu);
                    double two = Benchmix.Engine.Analysis.Analyzer.AverageBpm(new[] { a.OriginalBpm, other.OriginalBpm }, other.OriginalBpm);
                    Check($"with two selected it offers those two and their average ('{item.Header}'; want {two:0.0})",
                          item.IsEnabled && item.Header is string h2 && h2.Contains("the 2 selected tracks") && h2.Contains($"{two:0.0} BPM"));
                }
            }
            else
                Check($"with one track that has a tempo the item is greyed out and says why ('{item.Header}')",
                      !item.IsEnabled && item.Header is string h && h.Contains("add another track"));
        }

        // b is now the one commands act on within a selection of two: a plain press on its own strip,
        // and one in its own lane, must still let the rest go. (Setting SelectedTrack to what it
        // already is does nothing, which is what left the rest selected in the first run.)
        Press(b, ModifierKeys.None);
        Check($"a plain press on the strip of the track commands act on lets the rest go (got {Picked()})", Picked() == "1" && ReferenceEquals(_vm.SelectedTrack, b));
        Press(a, ModifierKeys.None);
        Press(b, ModifierKeys.Control);
        Check($"two tracks again, with the second the one commands act on (got {Picked()})", Picked() == "0+1" && ReferenceEquals(_vm.SelectedTrack, b));
        UpdateLayout();
        if (LaneOf(b) is { ActualHeight: > 0 } laneB)
        {
            var origin = laneB.TranslatePoint(new Point(0, 0), this);
            var inLane = new Point(origin.X + 6, origin.Y + laneB.ActualHeight / 2);
            _scriptedModifiers = ModifierKeys.None;
            _scriptedWindowPoint = inLane;
            LaneDown(laneB, inLane);
            LaneUp(laneB);
            _scriptedWindowPoint = null;
            Check($"a plain press in the lane of the track commands act on lets the rest go too (got {Picked()})", Picked() == "1" && ReferenceEquals(_vm.SelectedTrack, b));
        }
        else notes.Add("the second track's lane was not laid out, so the lane press letting a track selection go is untested");

        _vm.SelectedTrack = a;
        for (int n = _vm.Tracks.Count; n > before; n--)
        {
            int was = _vm.Tracks.Count;
            _vm.UndoCommand.Execute(null);
            PumpUntil(() => _vm.Tracks.Count < was);
        }
        Check($"the two empty tracks are undone (got {_vm.Tracks.Count}, want {before})", _vm.Tracks.Count == before);
    }

    /// <summary>
    /// Selecting more than one section (Daniel, 2026-10-03): Ctrl+click adds or removes one,
    /// Shift+click takes the range, Delete removes the lot — and every button explains itself on
    /// hover. Through the real lane handlers with a scripted pointer, like
    /// <see cref="RunEditingGestures"/>.
    /// </summary>
    private void RunSelectionGestures(Action<string, bool> Check, List<string> notes)
    {
        // --- every button, knob and slider carries a tooltip ---
        //
        // Parts of a control's own template (a scrollbar's arrows, a slider's thumb) are not
        // buttons the user reads, so they are left out.
        var silent = new List<string>();
        int examined = 0;
        foreach (var el in Descendants(this))
        {
            if (el is not (System.Windows.Controls.Primitives.ButtonBase or Controls.Knob or Slider)) continue;
            if (el is System.Windows.Controls.Primitives.RepeatButton) continue;
            if (el is FrameworkElement { TemplatedParent: Control }) continue;
            examined++;
            if (el is FrameworkElement { ToolTip: string { Length: > 0 } }) continue;
            silent.Add(el switch
            {
                System.Windows.Controls.Primitives.ButtonBase { Content: string s } => s,
                Controls.Knob k => k.Label,
                _ => el.GetType().Name,
            });
        }
        Check($"every button has a tooltip ({examined} checked{(silent.Count == 0 ? "" : $"; missing: {string.Join(", ", silent.Distinct())}")})",
              silent.Count == 0 && examined > 0);
        if (_vm.Tracks.Count == 0) notes.Add("no track rows, so the per-track buttons and knobs were not checked for tooltips");

        var track = _vm.Tracks.FirstOrDefault(t => t.IsLoaded && !t.IsEmpty);
        if (track is null) { notes.Add("no loaded track, so multi-selection is untested - pass a file to cover it"); return; }
        _vm.SelectedTrack = track;
        _vm.LoopMode = false;

        // Four sections to choose between, cut from the longest one if there are not enough yet.
        for (int guard = 0; track.Clips.Count < 4 && guard < 6; guard++)
        {
            var longest = track.Clips.OrderByDescending(c => c.TimelineLengthSeconds).First();
            _vm.SeekTo(longest.TimelineStartSeconds + longest.TimelineLengthSeconds / 2);
            _vm.SplitAtPlayheadCommand.Execute(null);
        }
        var ordered = track.Clips.OrderBy(c => c.TimelineStartSeconds).ToList();
        Check($"there are sections to select between (got {ordered.Count})", ordered.Count >= 4);
        if (ordered.Count < 4) return;

        UpdateLayout();
        PumpUntil(() => LaneOf(track) is { ActualHeight: > 0 });
        var lane = LaneOf(track);
        if (lane is null) { notes.Add("the lane was not laid out, so multi-selection is untested"); return; }
        double pps = _vm.PixelsPerSecond;

        Point Mid(ClipViewModel c)
        {
            var origin = lane.TranslatePoint(new Point(0, 0), this);
            double seconds = c.TimelineStartSeconds + c.TimelineLengthSeconds / 2;
            return new Point(origin.X + (seconds - _vm.ScrollSeconds) * pps, origin.Y + lane.ActualHeight / 2);
        }

        // A press and a release with no movement between, with exactly these keys held — never
        // the real keyboard's, which belongs to whoever is using the machine.
        void Click(ClipViewModel c, ModifierKeys with)
        {
            _scriptedModifiers = with;
            _scriptedWindowPoint = Mid(c);
            LaneDown(lane, _scriptedWindowPoint.Value);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            _scriptedModifiers = ModifierKeys.None;        // the keys go up with the button
        }
        int Selected() => _vm.SelectedClips.Count;
        _scriptedModifiers = ModifierKeys.None;

        Click(ordered[0], ModifierKeys.None);
        Check($"a plain click selects one section (got {Selected()})", Selected() == 1 && ordered[0].IsSelected);

        Click(ordered[1], ModifierKeys.Control);
        Check($"Ctrl+click adds a second section to the selection (got {Selected()})",
              Selected() == 2 && ordered[0].IsSelected && ordered[1].IsSelected);
        Check("the section Ctrl+click added is the one commands act on", ReferenceEquals(_vm.SelectedClip, ordered[1]));

        Click(ordered[1], ModifierKeys.Control);
        Check($"Ctrl+click on a selected section takes it out again (got {Selected()})",
              Selected() == 1 && ordered[0].IsSelected && !ordered[1].IsSelected);
        Check("commands fall back to a section that is still selected", ReferenceEquals(_vm.SelectedClip, ordered[0]));

        // --- Delete while a section is held under the mouse is refused ---
        _scriptedWindowPoint = Mid(ordered[0]);
        LaneDown(lane, _scriptedWindowPoint.Value);
        int held = track.Clips.Count;
        bool refused = !PressDelete();
        LaneUp(lane);
        _scriptedWindowPoint = null;
        Check("Delete while a section is held under the mouse is refused", refused && track.Clips.Count == held);

        Click(ordered[0], ModifierKeys.None);
        Click(ordered[2], ModifierKeys.Shift);
        Check($"Shift+click selects every section between (got {Selected()})",
              Selected() == 3 && ordered[0].IsSelected && ordered[1].IsSelected && ordered[2].IsSelected && !ordered[3].IsSelected);
        Check("after Shift+click the section clicked last is the one commands act on", ReferenceEquals(_vm.SelectedClip, ordered[2]));

        // --- the right-click menu acts on the section right-clicked, not the first selected ---
        // A right-click on a selected section calls MakePrimary; ClipOf is what every menu item
        // (fades, trims, move to playhead) reads.
        _vm.MakePrimary(ordered[0]);
        Check("the menu acts on the right-clicked section, not the first selected one",
              ReferenceEquals(ClipOf(lane), ordered[0]) && Selected() == 3);
        _vm.MakePrimary(ordered[2]);
        Check("right-clicking another selected section moves the menu's target to it",
              ReferenceEquals(ClipOf(lane), ordered[2]) && Selected() == 3);

        // --- Delete through the real key handler, with the focus scripted ---
        int before = track.Clips.Count;
        var source = PresentationSource.FromVisual(this);
        if (source is null)
        {
            notes.Add("no presentation source, so the Delete key handler was not driven");
        }
        else
        {
            KeyEventArgs Press() => new(Keyboard.PrimaryDevice, source, 0, Key.Delete) { RoutedEvent = PreviewKeyDownEvent };

            _scriptedFocus = BpmBox;
            var inBox = Press();
            OnWindowPreviewKeyDown(this, inBox);
            Check("Delete with the focus in PROJECT BPM is left to the box", !inBox.Handled && track.Clips.Count == before);

            _scriptedFocus = new ContextMenu();
            var inMenu = Press();
            OnWindowPreviewKeyDown(this, inMenu);
            Check("Delete while a menu is open is left to the menu", !inMenu.Handled && track.Clips.Count == before);

            _scriptedFocus = this;
            var onTimeline = Press();
            OnWindowPreviewKeyDown(this, onTimeline);
            Check($"Delete removes every selected section in one go (got {track.Clips.Count}, was {before})",
                  onTimeline.Handled && track.Clips.Count == before - 3);
            _scriptedFocus = null;
        }
        if (track.Clips.Count == before) { PressDelete(); }    // no source: at least exercise the path
        Check($"the status bar counts them (\"{_vm.Status}\")", _vm.Status.Contains("3 sections"));

        var trackId = track.Id;
        _vm.UndoCommand.Execute(null);
        PumpUntil(() => _vm.Tracks.FirstOrDefault(t => t.Id == trackId)?.Clips.Count == before);
        track = _vm.Tracks.FirstOrDefault(t => t.Id == trackId);
        Check($"one undo brings all three back (got {track?.Clips.Count})", track?.Clips.Count == before);

        // --- Delete is harmless with nothing selected, even with the playhead on a section ---
        _vm.SelectClip(null);
        if (track is not null)
        {
            _vm.SelectedTrack = track;
            var under = track.Clips.OrderBy(c => c.TimelineStartSeconds).Skip(1).First();
            _vm.SeekTo(under.TimelineStartSeconds + under.TimelineLengthSeconds / 2);
        }
        int untouched = track?.Clips.Count ?? -1;
        bool acted = PressDelete();
        Check("Delete with nothing selected deletes nothing, even with the playhead on a section",
              !acted && track?.Clips.Count == untouched);
        Check("Delete inside a text box is left to the text box", !BareKeyAppliesTo(new TextBox()));
        Check("Delete inside a menu is left to the menu", !BareKeyAppliesTo(new ContextMenu()) && !BareKeyAppliesTo(new MenuItem()));
        Check("Delete anywhere else is the timeline's", BareKeyAppliesTo(lane) && BareKeyAppliesTo(this) && BareKeyAppliesTo(null));
        Check("Delete is not a bare KeyBinding, so it cannot fire inside the PROJECT BPM box",
              !InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.Delete));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
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
    /// <summary>
    /// The scroll-wheel click (Daniel, 2026-10-11), raised on a real lane so it travels the route a
    /// real press does — the window's Preview first. The offline engine never moves by itself, so
    /// it is pulled on as a device would pull it: silently, at master zero.
    /// </summary>
    private void RunWheelClickGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        if (_vm.Tracks.FirstOrDefault() is not { IsLoaded: true } track || LaneOf(track) is not { ActualHeight: > 0 } lane)
        {
            notes.Add("no laid-out track, so the scroll-wheel click is untested - pass a file to cover it");
            return;
        }

        var s = _vm.Session;
        int rate = s.SampleRate;
        var block = new float[rate / 10 * s.Graph.Channels];
        void Listen(double seconds) { for (int i = 0; i < (int)Math.Round(seconds * 10); i++) s.Graph.Render(block, rate / 10); }
        MouseButtonEventArgs Press(MouseButton button) => new(Mouse.PrimaryDevice, 0, button) { RoutedEvent = PreviewMouseDownEvent };
        bool Stopped() => s.Engine.State == Benchmix.Engine.Audio.TransportState.Stopped;
        bool Paused() => s.Engine.State == Benchmix.Engine.Audio.TransportState.Paused;
        bool Playing() => s.Engine.State == Benchmix.Engine.Audio.TransportState.Playing;
        double frame = 1.0 / rate;

        // A click pauses exactly where the playhead is (Daniel, 2026-10-28); nothing moves.
        _vm.PlayFrom(30);
        Listen(1);
        var wheel = Press(MouseButton.Middle);
        lane.RaiseEvent(wheel);
        Check($"a click of the scroll wheel pauses exactly where the playhead is " +
              $"(engine at {s.Engine.PositionSeconds:0.000}s, {s.Engine.State}; expected 31.000s, paused)",
              Paused() && s.Engine.PositionFrames == 31L * rate && !_vm.IsPlaying);
        Check("the wheel-click is not passed on to whatever is under the pointer", wheel.Handled);

        // Space plays on from there, and the next click pauses where it has got to.
        _vm.PlayPauseCommand.Execute(null);
        Listen(1);
        lane.RaiseEvent(Press(MouseButton.Middle));
        Check($"Space plays on from the pause, and the next click pauses where it has got to " +
              $"(engine at {s.Engine.PositionSeconds:0.000}s, expected 32.000s)",
              Paused() && s.Engine.PositionFrames == 32L * rate);

        _vm.PlayFrom(30);
        var left = Press(MouseButton.Left);
        lane.RaiseEvent(left);
        Check("a left press is not a pause", Playing() && !left.Handled);

        // A double-click: stop, and go back to where play started — what the single click did until
        // 2026-10-28. Through the window's own Preview handler, as the wheel's presses arrive: the
        // click count the event carries is what tells a double-click from two single clicks.
        var setCount = typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.GetSetMethod(nonPublic: true)!;
        void DoubleClick()
        {
            foreach (int count in new[] { 1, 2 })
            {
                var click = Press(MouseButton.Middle);
                setCount.Invoke(click, new object[] { count });
                lane.RaiseEvent(click);
            }
        }
        _vm.ScrollSeconds = 20;
        _vm.PlayFrom(30);
        Listen(1);
        DoubleClick();
        Check($"a double-click of the scroll wheel stops and goes back to where play started, leaving the view alone " +
              $"(engine at {s.Engine.PositionSeconds:0.000}s, expected 30.000s; view from {_vm.ScrollSeconds:0.0}s, expected 20.0)",
              Stopped() && s.Engine.PositionFrames == 30L * rate && _vm.ScrollSeconds == 20);

        // Space's command, as the key runs it: pause at 31 s, then play on from there — and that is
        // where the double-click comes back to.
        _vm.PlayFrom(30);
        Listen(1);
        _vm.PlayPauseCommand.Execute(null);
        _vm.PlayPauseCommand.Execute(null);
        Listen(1);
        DoubleClick();
        Check($"after Space pauses and plays on, the double-click goes back to where Space started it " +
              $"(engine at {s.Engine.PositionSeconds:0.000}s, expected 31.000s)",
              Stopped() && s.Engine.PositionFrames == 31L * rate);

        // In the middle of dragging a section: playback stops, and the drag carries on to its drop.
        var clip = track.Clips.FirstOrDefault(c => c.TimelineLengthSeconds > 4);
        if (clip is null) { notes.Add("no section longer than 4 s, so a wheel-click mid-drag is untested"); return; }
        _vm.ScrollSeconds = Math.Max(0, clip.TimelineStartSeconds);
        UpdateLayout();
        double pps = _vm.PixelsPerSecond;
        var origin = lane.TranslatePoint(new Point(0, 0), this);
        Point At(double t) => new(origin.X + (t - _vm.ScrollSeconds) * pps, origin.Y + lane.ActualHeight / 2);
        double before = clip.TimelineStartSeconds, grabAt = before + 2;
        var id = clip.Id;

        _vm.PlayFrom(30);
        _scriptedModifiers = ModifierKeys.Control;                // free, so where it lands is exact
        _scriptedWindowPoint = At(grabAt);
        LaneDown(lane, At(grabAt));
        _scriptedWindowPoint = At(grabAt + 1);
        LaneMove(lane, At(grabAt + 1), true);
        lane.RaiseEvent(Press(MouseButton.Middle));
        bool stoppedMidDrag = Paused(), stillHeld = _vm.DragInProgress;
        _scriptedWindowPoint = At(grabAt + 2);
        LaneMove(lane, At(grabAt + 2), true);
        LaneUp(lane);
        _scriptedWindowPoint = null;
        _scriptedModifiers = null;
        var moved = _vm.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == id);
        double along = (moved?.TimelineStartSeconds ?? double.NaN) - before;
        Check($"a wheel-click in the middle of a drag pauses playback and the drag carries on " +
              $"(landed {along:0.000}s along, expected 2.000)",
              stoppedMidDrag && stillHeld && Math.Abs(along - 2) <= frame);

        _vm.UndoCommand.Execute(null);                             // put the song back for the checks that follow
        PumpUntil(() => _vm.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == id) is { } c
                        && Math.Abs(c.TimelineStartSeconds - before) <= frame);
    }

    /// <summary>
    /// Alt+wheel over the ruler and over the waveforms moves the playhead back and forth (the plain
    /// wheel did, 2026-10-12, until Daniel gave it to scrolling the tracks, 2026-10-27), through the
    /// ruler's and the tracks' own wheel handlers with a scripted pointer and modifiers. The engine
    /// itself is asked where the playhead is. The plain wheel is passed on everywhere, to scroll.
    /// </summary>
    private void RunScrubGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        if (RulerHost.ActualWidth < 400 || _vm.Tracks.FirstOrDefault() is not { IsLoaded: true } track || LaneOf(track) is not { ActualHeight: > 0 } lane)
        {
            notes.Add("no laid-out ruler and track, so the wheel over the ruler is untested - pass a file to cover it");
            return;
        }
        var engine = _vm.Session.Engine;
        double frame = 1.0 / _vm.Session.SampleRate;
        _vm.StopCommand.Execute(null);
        _vm.PixelsPerSecond = 40;
        _vm.ScrollSeconds = 0;
        UpdateLayout();
        double notch = ScrubPixelsPerNotch / _vm.PixelsPerSecond;           // 0.5 s at 40 px/s
        var r = RulerHost.TranslatePoint(new Point(0, 0), this);
        var overRuler = new Point(r.X + 300, r.Y + RulerHost.ActualHeight / 2);
        var l = lane.TranslatePoint(new Point(0, 0), this);
        var overLane = new Point(l.X + 300, l.Y + lane.ActualHeight / 2);
        bool Turn(Point at, int delta, bool ruler, ModifierKeys keys = ModifierKeys.None)
        {
            var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = PreviewMouseWheelEvent };
            _scriptedWindowPoint = at;
            _scriptedModifiers = keys;
            if (ruler) OnRulerWheel(RulerHost, e); else OnTimelineWheel(TrackScroller, e);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            return e.Handled;
        }
        bool At(double seconds) => Math.Abs(engine.PositionSeconds - seconds) <= frame;

        _vm.SeekTo(10);
        bool used = Turn(overRuler, 120, ruler: true, ModifierKeys.Alt);
        Check($"a notch of Alt+wheel up over the ruler moves the playhead {notch:0.00}s later (engine at {engine.PositionSeconds:0.000}s)", used && At(10 + notch));
        Turn(overRuler, -240, ruler: true, ModifierKeys.Alt);
        Check($"two notches down move it back {2 * notch:0.00}s (engine at {engine.PositionSeconds:0.000}s)", At(10 - notch));

        _vm.SeekTo(0.2);
        Turn(overRuler, -360, ruler: true, ModifierKeys.Alt);
        Check("it stops at 0:00", engine.PositionSeconds == 0);

        _vm.PlayFrom(30);
        Turn(overRuler, 120, ruler: true, ModifierKeys.Alt);
        Check($"while playing it carries on playing from the new spot (engine at {engine.PositionSeconds:0.000}s)",
              engine.State == Benchmix.Engine.Audio.TransportState.Playing && At(30 + notch));
        _vm.StopCommand.Execute(null);

        _vm.SeekTo(10);
        bool usedOverLane = Turn(overLane, 120, ruler: false, ModifierKeys.Alt);
        Check($"Alt+wheel up over a waveform moves the playhead {notch:0.00}s later too, and is used up (engine at {engine.PositionSeconds:0.000}s)", usedOverLane && At(10 + notch));
        Turn(overLane, -120, ruler: false, ModifierKeys.Alt);
        Check($"and a notch down moves it back (engine at {engine.PositionSeconds:0.000}s)", At(10));

        UpdateLayout();
        var b = BelowTracks.TranslatePoint(new Point(0, 0), this);
        if (BelowTracks.ActualHeight > 4)
        {
            bool belowUsed = Turn(new Point(b.X + 300, b.Y + 2), 120, ruler: false, ModifierKeys.Alt);
            Check($"under the last track Alt+wheel moves the playhead as well (engine at {engine.PositionSeconds:0.000}s)", belowUsed && At(10 + notch));
            _vm.SeekTo(10);
        }
        else notes.Add("no room under the last track on screen, so the wheel there is untested");

        // The plain wheel is the tracks' (Daniel, 2026-10-27): over a waveform and over the track
        // controls alike it is passed on to scroll them and leaves the playhead alone; over the
        // ruler, which is outside the scroller, it scrolls them itself.
        bool plainOverLane = !Turn(overLane, 120, ruler: false);
        Check("a plain notch over a waveform is passed on to scroll the tracks, and leaves the playhead alone", plainOverLane && At(10));
        var strip = StripOf(track)!;
        var st = strip.TranslatePoint(new Point(0, 0), this);
        bool passedOn = !Turn(new Point(st.X + 8, st.Y + strip.ActualHeight / 2), 120, ruler: false);
        Check("over the track controls on the left it is passed on too — a knob under the pointer turns, otherwise the tracks scroll — and leaves the playhead alone", passedOn && At(10));
        bool plainOverRuler = Turn(overRuler, 120, ruler: true);
        Check("a plain notch over the ruler is used to scroll the tracks, and leaves the playhead alone", plainOverRuler && At(10));

        // The knob under the pointer still takes the wheel, clicked or not (Daniel, 2026-10-27:
        // "retain functionality of hovering over knobs and wheeling to make adjustments there").
        // Seen from the strip above it: a wheel event the knob takes never reaches the strip.
        var knob = Descendants<Controls.Knob>(strip).FirstOrDefault();
        if (knob is null) notes.Add("no knob on the strip, so the wheel over a knob is untested");
        else
        {
            double was = knob.Value;
            bool reachedStrip = false;
            MouseWheelEventHandler seen = (_, _) => reachedStrip = true;
            strip.AddHandler(MouseWheelEvent, seen);
            knob.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = MouseWheelEvent, Source = knob });
            strip.RemoveHandler(MouseWheelEvent, seen);
            Check($"the wheel over a knob, clicked or not, turns it and goes no further (from {was:0.###} to {knob.Value:0.###})",
                  !reachedStrip && knob.Value != was);
            knob.Value = was;
        }

        double lanePps = _vm.PixelsPerSecond;
        Turn(overLane, 120, ruler: false, ModifierKeys.Control);
        Check("Ctrl+wheel over a waveform still zooms, and leaves the playhead alone", _vm.PixelsPerSecond > lanePps && At(10));
        _vm.PixelsPerSecond = 40;
        _vm.ScrollSeconds = 20;
        Turn(overLane, -120, ruler: false, ModifierKeys.Shift);
        Check("Shift+wheel over a waveform still scrolls sideways, and leaves the playhead alone", _vm.ScrollSeconds != 20 && At(10));
        _vm.ScrollSeconds = 0;
        UpdateLayout();

        double pps = _vm.PixelsPerSecond;
        Turn(overRuler, 120, ruler: true, ModifierKeys.Control);
        Check("Ctrl+wheel over the ruler still zooms, and leaves the playhead alone", _vm.PixelsPerSecond > pps && At(10));
        _vm.PixelsPerSecond = 40;
        _vm.ScrollSeconds = 20;
        Turn(overRuler, -120, ruler: true, ModifierKeys.Shift);
        Check("Shift+wheel over the ruler still scrolls sideways, and leaves the playhead alone", _vm.ScrollSeconds != 20 && At(10));

        _vm.ScrollSeconds = 0;
        double edge = _vm.ViewportSeconds - 0.2;
        _vm.SeekTo(edge);
        Turn(overRuler, 240, ruler: true, ModifierKeys.Alt);
        Check($"turned past the right edge of the view, the view follows the playhead (playhead {_vm.PositionSeconds:0.0}s, view {_vm.ScrollSeconds:0.0}s to {_vm.ScrollSeconds + _vm.ViewportSeconds:0.0}s)",
              _vm.ScrollSeconds > 0 && _vm.PositionSeconds >= _vm.ScrollSeconds && _vm.PositionSeconds <= _vm.ScrollSeconds + _vm.ViewportSeconds);
        _vm.ScrollSeconds = 0;
    }

    /// <summary>
    /// Tabs (Daniel, 2026-10-27): through the + above the tracks, the tab chips and the strip menu's
    /// Move to tab. Only the open tab's tracks are in the engine; a track sent to another tab leaves
    /// this one and is there; Ctrl+Z undoes only what was done on the tab that is open, and brings
    /// a sent track back from the other tab; a tab is renamed from the tab he is on, and one removed
    /// comes back with Ctrl+Z on the tab landed on, opened; Ctrl+Tab. One tab is left for the
    /// checks that follow.
    /// </summary>
    private void RunTabGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        if (_vm.Tracks.FirstOrDefault(t => t.IsLoaded) is not { } song) { notes.Add("no loaded track, so tabs are untested - pass a file to cover them"); return; }
        if (PresentationSource.FromVisual(this) is not { } source) { notes.Add("no presentation source, so tabs are untested"); return; }
        KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = PreviewKeyDownEvent };
        void Settle() { PumpUntil(() => !_vm.IsBusy); UpdateLayout(); }
        List<Border> Chips()
        {
            UpdateLayout();                                          // an undo rebuilds the row; its chips exist only once laid out
            return Descendants<Border>(TabRow).Where(b => b.Name == "TabChip").ToList();
        }
        void ClickChip(int i) { TabPress((TabViewModel)Chips()[i].DataContext, 1); Settle(); }

        int onMix = _vm.Tracks.Count;
        var songId = song.Id;
        Check($"the project starts with one tab, open ({_vm.Tabs.Count})", _vm.Tabs.Count == 1 && _vm.Tabs[0].IsCurrent);

        // --- + adds a tab and opens it, empty; the first tab keeps its tracks, out of the engine ---
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(AddTabButton);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        PumpUntil(() => _vm.Tabs.Count == 2 && !_vm.IsBusy);     // the peer's click is dispatched, not immediate
        UpdateLayout();
        Check($"+ adds a second tab and opens it, empty ({_vm.Tabs.Count} tabs, open '{_vm.CurrentTab?.Name}', {_vm.Tracks.Count} tracks on screen, {_vm.Session.Graph.Tracks.Count} in the engine; \"{_vm.Status}\")",
              _vm.Tabs.Count == 2 && _vm.Tabs[1].IsCurrent && _vm.Tracks.Count == 0 && _vm.Session.Graph.Tracks.Count == 0);
        if (_vm.Tabs.Count != 2) return;
        var tab2 = _vm.Tabs[1];
        Check($"the first tab's {onMix} track(s) are still in the project, on their tab", _vm.Session.Project.Tabs[0].Tracks.Count == onMix);

        // --- a click on the first tab's chip opens it again ---
        Check($"two tab chips are drawn ({Chips().Count})", Chips().Count == 2);
        if (Chips().Count < 2) return;
        ClickChip(0);
        Check($"a click on the first tab opens it: its tracks back on screen ({_vm.Tracks.Count}) and in the engine ({_vm.Session.Graph.Tracks.Count})",
              _vm.Tabs[0].IsCurrent && _vm.Tracks.Count == onMix && _vm.Session.Graph.Tracks.Count == onMix);

        // --- Move to tab, through the strip menu ---
        var row = _vm.Tracks.First(t => t.Id == songId);
        var menu = StripOf(row)?.ContextMenu;
        var move = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "Move to tab");
        Check("the strip's right-click menu offers 'Move to tab'", move is not null);
        if (menu is null || move is null) return;
        _vm.SelectedTrack = row;
        OnStripMenuOpened(menu, new RoutedEventArgs());
        var toTab2 = move.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == tab2.Name);
        Check($"it lists the other tab and not this one ({move.Items.Count} item(s))", toTab2 is not null && move.Items.Count == 1);
        if (toTab2 is null) return;
        OnMoveTrackToTabMenu(toTab2, new RoutedEventArgs());
        Settle();
        Check($"the track leaves this tab ({_vm.Tracks.Count} on screen, want {onMix - 1}), is on the other, and is out of the engine",
              _vm.Tracks.Count == onMix - 1 && _vm.Session.Project.Tabs[1].Tracks.Any(t => t.Id == songId) && _vm.Session.VoiceFor(song.Model) is null);
        Check($"the status bar says so (\"{_vm.Status}\")", _vm.Status.Contains("Moved") && _vm.Status.Contains(tab2.Name));

        // --- on the other tab it plays, and Ctrl+Z there has nothing to undo ---
        ClickChip(1);
        Check("on the other tab the track is on screen and in the engine", _vm.Tabs[1].IsCurrent && _vm.Tracks.Any(t => t.Id == songId) && _vm.Session.VoiceFor(song.Model) is not null);
        _vm.UndoCommand.Execute(null);
        Settle();
        Check($"Ctrl+Z on the other tab undoes nothing: the move was done from the first tab (\"{_vm.Status}\")",
              _vm.Tracks.Any(t => t.Id == songId) && _vm.Status.StartsWith("Nothing to undo"));

        // --- back on the first tab, Ctrl+Z brings the track back from the other tab ---
        ClickChip(0);
        _vm.UndoCommand.Execute(null);
        PumpUntil(() => !_vm.IsBusy && _vm.Tracks.Count == onMix);
        Check($"Ctrl+Z on the first tab brings the track back ({_vm.Tracks.Count} on screen, want {onMix}) and takes it from the other tab",
              _vm.Tracks.Count == onMix && _vm.Tracks.Any(t => t.Id == songId) && !_vm.Session.Project.Tabs[1].Tracks.Any(t => t.Id == songId));

        // --- renaming the other tab from this one: double-click its chip, type, Enter; Ctrl+Z here puts the name back ---
        TabPress((TabViewModel)Chips()[1].DataContext, 2);
        UpdateLayout();
        var box = Descendants<TextBox>(TabRow).FirstOrDefault(b => b.Name == "TabRenameBox" && b.Visibility == Visibility.Visible);
        Check("a double-click on a tab opens its name for typing", _vm.Tabs[1].IsRenaming && box is not null);
        if (box is not null)
        {
            box.Text = "Bites";
            OnTabRenameKeyDown(box, Press(Key.Enter));
            Settle();
            Check($"Enter keeps the new name on the chip and in the project ('{_vm.Tabs[1].Name}' / '{_vm.Session.Project.Tabs[1].Name}')",
                  !_vm.Tabs[1].IsRenaming && _vm.Tabs[1].Name == "Bites" && _vm.Session.Project.Tabs[1].Name == "Bites");
            _vm.UndoCommand.Execute(null);
            PumpUntil(() => !_vm.IsBusy && _vm.Tabs.Count == 2 && _vm.Tabs[1].Name != "Bites");
            Check($"Ctrl+Z on this tab puts the old name back ('{_vm.Tabs[1].Name}'), and this tab stays open", _vm.Tabs[1].Name == tab2.Name && _vm.Tabs[0].IsCurrent);
        }

        // --- removing the other tab while it is open lands here; Ctrl+Z here brings it back and opens it ---
        ClickChip(1);
        OnRemoveTabMenu(Chips()[1], new RoutedEventArgs());
        PumpUntil(() => !_vm.IsBusy && _vm.Tabs.Count == 1);
        Check($"Remove tab takes the open tab away and lands on the first ({_vm.Tabs.Count} tab(s), open '{_vm.CurrentTab?.Name}', {_vm.Tracks.Count} tracks)",
              _vm.Tabs.Count == 1 && _vm.Tabs[0].IsCurrent && _vm.Tracks.Count == onMix);
        _vm.UndoCommand.Execute(null);
        PumpUntil(() => !_vm.IsBusy && _vm.Tabs.Count == 2);
        Check($"Ctrl+Z on the tab landed on brings the removed tab back and opens it ({_vm.Tabs.Count} tabs, open '{_vm.CurrentTab?.Name}')",
              _vm.Tabs.Count == 2 && _vm.Tabs[1].IsCurrent);

        // --- Ctrl+Tab is bound, and goes to the next tab round the row ---
        Check("Ctrl+Tab and Ctrl+Shift+Tab are bound",
              InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.Tab && b.Modifiers == ModifierKeys.Control)
              && InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.Tab && b.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)));
        _vm.NextTabCommand.Execute(null);
        Settle();
        Check("Ctrl+Tab from the last tab goes round to the first", _vm.Tabs[0].IsCurrent);

        // --- one tab again for the checks that follow ---
        if (_vm.Tabs.Count > 1) _ = _vm.RemoveTabAsync(_vm.Tabs[^1]);
        PumpUntil(() => !_vm.IsBusy && _vm.Tabs.Count == 1);
        if (!_vm.Tabs[0].IsCurrent) { ClickChip(0); }
        Check($"the second tab is removed again ({_vm.Tabs.Count} tab, {_vm.Tracks.Count} tracks)", _vm.Tabs.Count == 1 && _vm.Tracks.Count == onMix);
    }

    /// <summary>
    /// Double-click to play (Daniel, 2026-10-11), through the lane's and the empty room's own press
    /// handlers with a scripted pointer and click count. The engine itself is asked where it is.
    /// </summary>
    private void RunDoubleClickGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        if (_vm.Tracks.FirstOrDefault() is not { IsLoaded: true } track || LaneOf(track) is not { ActualHeight: > 0 } lane)
        {
            notes.Add("no laid-out track, so double-click on a lane is untested - pass a file to cover it");
            return;
        }
        var clip = track.Clips.FirstOrDefault(c => c.TimelineLengthSeconds > 6);
        if (clip is null) { notes.Add("no section longer than 6 s, so double-click on a lane is untested"); return; }

        var engine = _vm.Session.Engine;
        double frame = 1.0 / _vm.Session.SampleRate;
        bool Playing() => engine.State == Benchmix.Engine.Audio.TransportState.Playing;
        // Where the handler itself puts the press, so the check does not depend on pixel rounding.
        bool PlayingAt(FrameworkElement surface, Point p)
            => Playing() && Math.Abs(engine.PositionSeconds - SecondsAtLaneX(LaneX(surface, p))) <= 2 * frame;
        ClipViewModel? Find(Guid id) => _vm.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == id);

        _vm.ScrollSeconds = Math.Max(0, clip.TimelineStartSeconds);
        UpdateLayout();
        double pps = _vm.PixelsPerSecond;
        Point At(FrameworkElement surface, double t)
        {
            var o = surface.TranslatePoint(new Point(0, 0), this);
            return new Point(o.X + (t - _vm.ScrollSeconds) * pps, o.Y + surface.ActualHeight / 2);
        }
        // Two presses as WPF delivers a double-click; the second is left held, as a hand holds it.
        void DoubleClick(Point p)
        {
            _scriptedWindowPoint = p;
            LaneDown(lane, p, 1); LaneUp(lane);
            LaneDown(lane, p, 2);
        }
        void MoveAndLetGo(Point p)
        {
            var to = new Point(p.X + 30, p.Y);
            _scriptedWindowPoint = to;
            LaneMove(lane, to, true);
            LaneUp(lane);
            _scriptedWindowPoint = null;
        }

        var id = clip.Id;
        double start = clip.TimelineStartSeconds, length = clip.LengthSeconds;

        _vm.StopCommand.Execute(null);
        var onSection = At(lane, start + 2.25);
        DoubleClick(onSection);
        Check($"a double-click on a section plays from where it was double-clicked (engine at {engine.PositionSeconds:0.000}s)",
              PlayingAt(lane, onSection));
        MoveAndLetGo(onSection);
        Check("the second press of a double-click drags nothing, even if the hand moves before letting go",
              Find(id) is { } a && a.TimelineStartSeconds == start);

        _vm.PlayFrom(start + 0.5);
        var later = At(lane, start + 3.1);
        DoubleClick(later);
        LaneUp(lane);
        Check($"a double-click while playing jumps there and keeps playing (engine at {engine.PositionSeconds:0.000}s)",
              PlayingAt(lane, later));

        _vm.StopCommand.Execute(null);
        var onEdge = new Point(At(lane, start).X + 2, onSection.Y);            // inside the 7 px trim grab
        DoubleClick(onEdge);
        MoveAndLetGo(onEdge);
        Check("a double-click on a section's edge plays and trims nothing",
              Playing() && Find(id) is { } b && b.LengthSeconds == length && b.TimelineStartSeconds == start);

        _vm.StopCommand.Execute(null);
        _scriptedModifiers = ModifierKeys.Shift;
        DoubleClick(onSection);
        LaneUp(lane);
        _scriptedModifiers = null;
        Check("a Shift+double-click on a lane is two Shift+clicks: it does not play", !Playing());

        double pastTheEnd = track.Clips.Max(c => c.TimelineEndSeconds) + 3;
        _vm.ScrollSeconds = Math.Max(0, pastTheEnd - 5);
        UpdateLayout();
        _vm.StopCommand.Execute(null);
        var inAGap = At(lane, pastTheEnd);
        DoubleClick(inAGap);
        LaneUp(lane);
        Check($"a double-click on empty lane space plays from there (engine at {engine.PositionSeconds:0.000}s)",
              PlayingAt(lane, inAGap));
        _scriptedWindowPoint = null;

        if (BelowTracks.ActualHeight < 10)
        {
            notes.Add($"no room under the tracks ({BelowTracks.ActualHeight:0} px), so a double-click there is untested");
            return;
        }
        _vm.StopCommand.Execute(null);
        var below = At(BelowTracks, _vm.ScrollSeconds + 2.5);
        _scriptedWindowPoint = below;
        BelowDown(BelowTracks, below, 1);
        BandUp();
        BelowDown(BelowTracks, below, 2);
        BandUp();
        _scriptedWindowPoint = null;
        Check($"a double-click under the last track plays from there (engine at {engine.PositionSeconds:0.000}s)",
              PlayingAt(BelowTracks, below));

        _vm.SelectClip(Find(id));
        _scriptedWindowPoint = below;
        BelowDown(BelowTracks, below, 1);
        BandUp();
        _scriptedWindowPoint = null;
        Check("a plain click under the last track lets go of the selected sections", _vm.SelectedClips.Count == 0);
    }

    /// <summary>
    /// Renaming a track (Daniel, 2026-10-11) and the bare Space key, through the window's own
    /// handlers. A scripted run never holds the real keyboard, so the name box is never actually
    /// focused: keys go to the handlers directly, with a scripted focused element.
    /// </summary>
    private void RunRenameGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        if (_vm.Tracks.FirstOrDefault() is not { } track || NameAreaOf(track) is not { } area || RenameBoxOf(track) is not { } box)
        {
            notes.Add("no track laid out, so renaming is untested - pass a file to cover it");
            return;
        }
        var source = PresentationSource.FromVisual(this);
        if (source is null) { notes.Add("no presentation source, so the rename keys were not driven"); return; }
        KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = PreviewKeyDownEvent };
        KeyboardFocusChangedEventArgs FocusTo(IInputElement? to) => new(Keyboard.PrimaryDevice, 0, box, to) { RoutedEvent = LostKeyboardFocusEvent };

        var id = track.Id;
        string original = track.Name;
        // Undo rebuilds every row, so the track and its parts are found again after each one.
        void Refetch()
        {
            UpdateLayout();
            track = _vm.Tracks.First(t => t.Id == id);
            area = NameAreaOf(track) ?? area;
            box = RenameBoxOf(track) ?? box;
        }
        void UndoAndWait() { _vm.UndoCommand.Execute(null); PumpUntil(() => _vm.Tracks.FirstOrDefault(t => t.Id == id)?.Name == original); Refetch(); }

        var onName = area.TranslatePoint(new Point(area.ActualWidth - 4, area.ActualHeight / 2), this);
        _scriptedWindowPoint = onName;                       // scripted: no mouse capture is taken
        NameDown(area, onName, null, 1);
        NameUp();
        Check("a single click on a track's name does not open it for typing", !track.IsRenaming);
        NameDown(area, onName, null, 2);
        _scriptedWindowPoint = null;
        UpdateLayout();
        Check("double-clicking a track's name opens it for typing", track.IsRenaming && box.Visibility == Visibility.Visible);
        Check($"the box starts with the current name ('{box.Text}')", box.Text == original);
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // let a queued focus run, if one was queued
        Check("the box is never given the keyboard in a scripted run", !box.IsKeyboardFocusWithin && !ReferenceEquals(Keyboard.FocusedElement, box));

        box.Text = original + " (renamed)";
        OnRenameKeyDown(box, Press(Key.Enter));
        Check($"Enter keeps the new name, on screen and in the project ('{track.Name}')",
              !track.IsRenaming && track.Name == original + " (renamed)" && track.Model.Name == track.Name);
        UndoAndWait();
        Check($"one undo puts the old name back ('{track.Name}')", track.Name == original);

        _vm.BeginRename(track);
        box.Text = "Not this one";
        OnRenameKeyDown(box, Press(Key.Escape));
        Check("Esc leaves the name as it was", !track.IsRenaming && track.Name == original);

        _vm.BeginRename(track);
        box.Text = "   ";
        OnRenameKeyDown(box, Press(Key.Enter));
        Check($"a blank name is refused and the old one kept (\"{_vm.Status}\")", track.Name == original && _vm.Status.Contains("needs a name"));

        _vm.BeginRename(track);
        WindowPress(MouseButton.Left, box);
        Check("a press inside the name box does not end the renaming", track.IsRenaming);
        OnRenameLostFocus(box, FocusTo(new ContextMenu()));
        Check("the box's own right-click menu taking the keyboard does not end the renaming", track.IsRenaming);
        OnRenameLostFocus(box, FocusTo(null));
        Check("switching to another program does not end the renaming", track.IsRenaming);
        box.Text = "Clicked away";
        WindowPress(MouseButton.Left, LaneOf(track));
        Check($"clicking elsewhere keeps what was typed ('{track.Name}')", !track.IsRenaming && track.Name == "Clicked away");

        // A name still open when an undo rebuilds the rows must not be written in afterwards.
        _vm.BeginRename(track);
        UndoAndWait();
        bool late = _vm.CommitRename(id, "Too late");
        Check("a name still being typed when an undo rebuilds the tracks is dropped, not written in afterwards",
              !late && track.Name == original && !track.IsRenaming && _vm.Tracks.All(t => t.Name != "Too late"));

        Check("the name says how to rename it on hover", NameTextOf(track)?.ToolTip is string tip && tip.Contains("double-click to rename"));
        Check("the control map labels the name box textbox:rename and PROJECT BPM textbox:projectBpm",
              LabelFor(box) == "textbox:rename" && LabelFor(BpmBox) == "textbox:projectBpm");

        // --- Space ---
        var engine = _vm.Session.Engine;
        Check("Space is not a bare KeyBinding, so it cannot fire inside a text box",
              !InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.Space));
        _vm.StopCommand.Execute(null);
        _scriptedFocus = this;
        var space = Press(Key.Space);
        OnWindowPreviewKeyDown(this, space);
        bool played = space.Handled && engine.State == Benchmix.Engine.Audio.TransportState.Playing;
        space = Press(Key.Space);
        OnWindowPreviewKeyDown(this, space);
        Check("Space on the timeline plays, and again pauses",
              played && space.Handled && engine.State == Benchmix.Engine.Audio.TransportState.Paused);

        _vm.StopCommand.Execute(null);
        _scriptedFocus = BpmBox;
        space = Press(Key.Space);
        OnWindowPreviewKeyDown(this, space);
        Check("Space with PROJECT BPM focused is left to the box",
              !space.Handled && engine.State == Benchmix.Engine.Audio.TransportState.Stopped);

        _vm.BeginRename(track);
        UpdateLayout();
        _scriptedFocus = box;
        space = Press(Key.Space);
        OnWindowPreviewKeyDown(this, space);
        Check("Space while typing a track's name is left to the box, so a name can have spaces in it",
              !space.Handled && engine.State == Benchmix.Engine.Audio.TransportState.Stopped);
        _vm.CancelRename(id);

        var link = Descendants(this).OfType<System.Windows.Controls.Primitives.ToggleButton>().FirstOrDefault(b => b.Content as string == "LINK");
        if (link is null) notes.Add("no LINK button found, so Space with a button focused is untested");
        else
        {
            _scriptedFocus = link;
            space = Press(Key.Space);
            OnWindowPreviewKeyDown(this, space);
            Check("Space with a track's button focused plays, and is used up before it can press that button again",
                  space.Handled && engine.State == Benchmix.Engine.Audio.TransportState.Playing);
            _vm.StopCommand.Execute(null);
        }
        _scriptedFocus = null;
    }

    /// <summary>
    /// Dragging a track up or down (Daniel, 2026-10-11), through the name row's own handlers with a
    /// scripted pointer. Leaves the order as it found it.
    /// </summary>
    private void RunReorderGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        if (_vm.Tracks.Count < 2) { notes.Add("fewer than two tracks, so dragging a track up or down is untested"); return; }
        List<Guid> Order() => _vm.Tracks.Select(t => t.Id).ToList();
        var original = Order();
        FrameworkElement? Row(TrackViewModel t) => TrackList.ItemContainerGenerator.ContainerFromItem(t) as FrameworkElement;
        Point Into(TrackViewModel t, double fraction)
        {
            var row = Row(t)!;
            return row.TranslatePoint(new Point(200, row.ActualHeight * fraction), this);
        }
        void UndoToOriginal()
        {
            _vm.UndoCommand.Execute(null);
            PumpUntil(() => Order().SequenceEqual(original));
            UpdateLayout();
        }
        // Press the track's grip, move through the middle, let go at `to`; returns whether the line showed.
        bool DragName(TrackViewModel t, Point to)
        {
            var area = NameAreaOf(t)!;
            var press = area.TranslatePoint(new Point(4, area.ActualHeight / 2), this);
            _scriptedWindowPoint = press;
            NameDown(area, press, null, 1);
            var mid = new Point(press.X, (press.Y + to.Y) / 2);
            _scriptedWindowPoint = mid;
            NameMove(mid, true);
            _scriptedWindowPoint = to;
            NameMove(to, true);
            bool shown = InsertLine.Visibility == Visibility.Visible;
            NameUp();
            _scriptedWindowPoint = null;
            return shown;
        }

        var first = _vm.Tracks[0];
        var second = _vm.Tracks[1];
        if (NameAreaOf(first) is not { } firstArea || Row(second) is null)
        {
            notes.Add("the rows are not laid out, so dragging a track up or down is untested");
            return;
        }

        var press = firstArea.TranslatePoint(new Point(4, firstArea.ActualHeight / 2), this);
        _scriptedWindowPoint = press;
        NameDown(firstArea, press, null, 1);
        var nudge = new Point(press.X, press.Y + 2);
        _scriptedWindowPoint = nudge;
        NameMove(nudge, true);
        NameUp();
        _scriptedWindowPoint = null;
        Check("a small pull on a track's name is a click, not a move", Order().SequenceEqual(original));

        var voice = _vm.Session.VoiceFor(first.Model);
        bool lineShown = DragName(first, Into(second, 0.75));
        Check("a line shows where the track will land while it is dragged, and goes away after",
              lineShown && InsertLine.Visibility != Visibility.Visible);
        Check($"dragging a track's name down past the next one moves it down (now place {_vm.Tracks.IndexOf(first) + 1})",
              _vm.Tracks.Count > 1 && _vm.Tracks[0].Id == second.Id && _vm.Tracks[1].Id == first.Id);
        Check("the project's order moves with the rows, so saving, undo and Shift+click see the same order",
              _vm.Tracks.Select(t => t.Model).SequenceEqual(_vm.Session.Project.Tracks));
        Check("the moved track keeps its voice: the sound engine is not touched",
              voice is not null && ReferenceEquals(_vm.Session.VoiceFor(first.Model), voice));
        Check($"the status bar says where it went (\"{_vm.Status}\")", _vm.Status.StartsWith("Moved") && _vm.Status.Contains("down"));
        UndoToOriginal();
        Check("one undo puts the order back", Order().SequenceEqual(original));

        first = _vm.Tracks[0];
        second = _vm.Tracks[1];
        DragName(second, Into(first, 0.25));
        Check($"dragging a track's name up past the one above moves it up (now place {_vm.Tracks.IndexOf(second) + 1})",
              _vm.Tracks[0].Id == second.Id && _vm.Tracks[1].Id == first.Id);
        UndoToOriginal();

        // A track cannot be moved while a section is held under the mouse: the section's drag is
        // mid-way through putting it somewhere, and every row would be rebuilt under it.
        first = _vm.Tracks[0];
        second = _vm.Tracks[1];
        var song = _vm.Tracks.FirstOrDefault(t => t.Clips.Any(c => c.TimelineLengthSeconds > 2));
        if (song is null || LaneOf(song) is not { } lane) notes.Add("no section to hold, so moving a track mid-drag is untested");
        else
        {
            var clip = song.Clips.First(c => c.TimelineLengthSeconds > 2);
            _vm.ScrollSeconds = Math.Max(0, clip.TimelineStartSeconds);
            UpdateLayout();
            var o = lane.TranslatePoint(new Point(0, 0), this);
            var grab = new Point(o.X + (clip.TimelineStartSeconds + 1 - _vm.ScrollSeconds) * _vm.PixelsPerSecond, o.Y + lane.ActualHeight / 2);
            _scriptedModifiers = ModifierKeys.Control;              // free, so letting go where it started moves nothing
            _scriptedWindowPoint = grab;
            LaneDown(lane, grab);
            var away = new Point(grab.X + 40, grab.Y);
            _scriptedWindowPoint = away;
            LaneMove(lane, away, true);
            bool held = _vm.DragInProgress;
            DragName(first, Into(second, 0.75));
            Check($"a track cannot be moved while a section is held under the mouse (\"{_vm.Status}\")",
                  held && Order().SequenceEqual(original) && _vm.Status == "Let go of the section first.");
            _scriptedWindowPoint = grab;
            LaneMove(lane, grab, true);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }

        UpdateLayout();
        Check("the grip explains itself on hover", GripOf(_vm.Tracks[0]) is { ToolTip: string { Length: > 0 } });
    }

    /// <summary>
    /// The selection box (Daniel, 2026-10-11), through the lanes' and the empty room's own handlers
    /// with a scripted pointer. What it should select is worked out here from the sections and rows
    /// on screen, not from the code that does the selecting.
    /// </summary>
    private void RunBandGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        var song = _vm.Tracks.FirstOrDefault(t => t.IsLoaded && t.Clips.Any(c => c.TimelineLengthSeconds > 14));
        if (song is null) { notes.Add("no section longer than 14 s, so the selection box is untested - pass a file to cover it"); return; }
        var songId = song.Id;
        double pps = _vm.PixelsPerSecond;
        FrameworkElement? Row(TrackViewModel t) => TrackList.ItemContainerGenerator.ContainerFromItem(t) as FrameworkElement;
        double LayerY(Point window) => TranslatePoint(window, BandLayer).Y;
        Point At(FrameworkElement surface, double t, double fraction = 0.5)
        {
            var o = surface.TranslatePoint(new Point(0, 0), this);
            return new Point(o.X + (t - _vm.ScrollSeconds) * pps, o.Y + surface.ActualHeight * fraction);
        }
        // Every section on a row the box covers that reaches into its time: the desktop's rule.
        HashSet<Guid> Expected(Point a, Point b, double from, double to)
        {
            double y0 = Math.Min(LayerY(a), LayerY(b)), y1 = Math.Max(LayerY(a), LayerY(b));
            double lo = Math.Min(from, to), hi = Math.Max(from, to);
            var ids = new HashSet<Guid>();
            foreach (var t in _vm.Tracks)
            {
                if (Row(t) is not { } row) continue;
                double top = row.TranslatePoint(new Point(0, 0), BandLayer).Y;
                if (top > y1 || top + row.ActualHeight <= y0) continue;
                foreach (var c in t.Clips)
                    if (c.TimelineLengthSeconds > 0 && c.TimelineStartSeconds < hi && c.TimelineEndSeconds > lo) ids.Add(c.Id);
            }
            return ids;
        }
        HashSet<Guid> Selected() => _vm.SelectedClips.Select(c => c.Id).ToHashSet();
        void Press(FrameworkElement surface, Point p)
        {
            _scriptedWindowPoint = p;
            if (ReferenceEquals(surface, BelowTracks)) BelowDown(surface, p); else LaneDown(surface, p);
        }
        void MoveTo(FrameworkElement surface, Point p)
        {
            _scriptedWindowPoint = p;
            if (ReferenceEquals(surface, BelowTracks)) BandMove(p, true); else LaneMove(surface, p, true);
        }
        void LetGo(FrameworkElement surface)
        {
            if (ReferenceEquals(surface, BelowTracks)) BandUp(); else LaneUp(surface);
            _scriptedWindowPoint = null;
        }

        // --- under the last track, while the rows still leave room there ---
        var bottom = _vm.Tracks[^1];
        if (BelowTracks.ActualHeight < 20 || Row(bottom) is null)
            notes.Add($"no room under the tracks ({BelowTracks.ActualHeight:0} px), so a box started there is untested");
        else
        {
            var from = At(BelowTracks, _vm.ScrollSeconds + 1, 0.3);
            var into = new Point(At(BelowTracks, _vm.ScrollSeconds + 20).X, Row(bottom)!.TranslatePoint(new Point(0, Row(bottom)!.ActualHeight / 2), this).Y);
            var wantBelow = Expected(from, into, _vm.ScrollSeconds + 1, _vm.ScrollSeconds + 20);
            // Raised as routed events on the room itself, so the press, move and release handlers
            // wired up in the XAML are the ones that run.
            _scriptedWindowPoint = from;
            BelowTracks.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
            _scriptedWindowPoint = into;
            BelowTracks.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = MouseMoveEvent });
            BelowTracks.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
            _scriptedWindowPoint = null;
            Check("a box from under the tracks is let go when the button is (its own press, move and release handlers ran)",
                  _band is null && BandRect.Visibility != Visibility.Visible);
            if (wantBelow.Count == 0) notes.Add("the bottom track has nothing in view, so a box from under the tracks selects nothing to compare");
            else Check($"a box started under the last track selects what it reaches on the rows above (got {Selected().Count}, expected {wantBelow.Count})",
                       Selected().SetEquals(wantBelow));
        }

        // --- a gap to start from: cut the song into 3 s pieces and take one out ---
        var piece = song.Clips.First(c => c.TimelineLengthSeconds > 14);
        double T = piece.TimelineStartSeconds + 2;
        _vm.SelectedTrack = song;
        foreach (var at in new[] { T, T + 3, T + 6, T + 9 }) { _vm.SeekTo(at); _vm.SplitAtPlayheadCommand.Execute(null); }
        song = _vm.Tracks.First(t => t.Id == songId);
        var hole = song.Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - (T + 3)) < 0.01);
        if (hole is null) { notes.Add("could not cut the song into pieces, so the selection box is untested"); return; }
        _vm.SelectClip(hole);
        PressDelete();
        _vm.ScrollSeconds = Math.Max(0, T - 3);
        UpdateLayout();
        song = _vm.Tracks.First(t => t.Id == songId);
        if (LaneOf(song) is not { ActualHeight: > 0 } lane) { notes.Add("the song's lane is not laid out, so the selection box is untested"); return; }
        var before = song.Clips.First(c => Math.Abs(c.TimelineStartSeconds - T) < 0.01);
        var after = song.Clips.First(c => Math.Abs(c.TimelineStartSeconds - (T + 6)) < 0.01);

        // The box reaches down into the next row when there is one on screen.
        // The box reaches half way into the row below, when there is one on screen, and a copy of
        // the piece before the gap is pasted there first so that row has something in the box's time.
        int index = _vm.Tracks.IndexOf(song);
        double reach = 0.5;
        TrackViewModel? below = null;
        if (index + 1 < _vm.Tracks.Count && Row(_vm.Tracks[index + 1]) is { } nextRow
            && nextRow.TranslatePoint(new Point(0, nextRow.ActualHeight / 2), this).Y
               < TrackScroller.TranslatePoint(new Point(0, TrackScroller.ViewportHeight), this).Y)
        {
            below = _vm.Tracks[index + 1];
            _vm.SelectClip(before);
            _vm.CopyClipCommand.Execute(null);
            _vm.SelectedTrack = below;
            _vm.SeekTo(T + 1);
            _vm.PasteClipCommand.Execute(null);
            var belowId = below.Id;
            PumpUntil(() => _vm.Tracks.FirstOrDefault(t => t.Id == belowId)?.Clips.Any(c => Math.Abs(c.TimelineStartSeconds - (T + 1)) < 0.01) == true);
            below = _vm.Tracks.First(t => t.Id == belowId);
            song = _vm.Tracks.First(t => t.Id == songId);
            lane = LaneOf(song)!;
            UpdateLayout();
            reach = 1.5;
        }
        else notes.Add("no second row on screen, so the box is checked on one row only");

        var start = At(lane, T + 4.5);
        var small = At(lane, T + 3.2, 0.6);
        var end = At(lane, T + 1.5, reach);

        // A click in the gap without dragging lets go of the selection, as it always has.
        _vm.SelectClip(after);
        Press(lane, start);
        LetGo(lane);
        Check("a click on empty lane space without dragging still lets go of the selection", _vm.SelectedClips.Count == 0);

        Press(lane, start);
        MoveTo(lane, small);
        int whileSmall = Selected().Count;
        MoveTo(lane, end);
        int sections = song.Clips.Count;
        bool deleteRefused = Selected().Count > 0 && !PressDelete() && _vm.Tracks.First(t => t.Id == songId).Clips.Count == sections;
        bool shown = BandRect.Visibility == Visibility.Visible;
        double left = Canvas.GetLeft(BandRect), width = BandRect.Width, top = Canvas.GetTop(BandRect), height = BandRect.Height;
        double wantLeft = (T + 1.5 - _vm.ScrollSeconds) * pps, wantTop = Math.Min(LayerY(start), LayerY(end));
        var whileBig = Selected();
        LetGo(lane);
        var want = Expected(start, end, T + 1.5, T + 4.5);

        Check($"dragging across empty lane space draws a box where the pointer went (at {left:0}, {top:0}, {width:0} x {height:0} px)",
              shown && Math.Abs(left - wantLeft) <= 1 && Math.Abs(width - 3 * pps) <= 1
              && Math.Abs(top - wantTop) <= 1 && Math.Abs(height - Math.Abs(LayerY(end) - LayerY(start))) <= 1);
        Check($"the selection follows the box while it is dragged ({whileSmall} while small, {whileBig.Count} when larger)",
              whileSmall < whileBig.Count && whileBig.SetEquals(want));
        bool reachedBelow = below is null || below.Clips.Any(c => want.Contains(c.Id));
        Check($"every section the box touches is selected, on every row it covers, even ones only partly inside (got {Selected().Count}, expected {want.Count})",
              want.Contains(before.Id) && !want.Contains(after.Id) && reachedBelow && Selected().SetEquals(want));
        Check("the box disappears when the button is let go", BandRect.Visibility != Visibility.Visible);
        Check("Delete is refused while a box is being dragged, even with sections in it selected", deleteRefused);
        Check($"after a box, the earliest section on the topmost row is the one commands act on, and the status bar counts them (\"{_vm.Status}\")",
              _vm.SelectedClip?.Id == before.Id && _vm.Status.Contains("selected"));

        // Ctrl as the box starts: it adds to what was selected.
        _vm.SelectClip(_vm.Tracks.SelectMany(t => t.Clips).First(c => c.Id == after.Id));
        _scriptedModifiers = ModifierKeys.Control;
        Press(lane, start);
        MoveTo(lane, end);
        LetGo(lane);
        _scriptedModifiers = null;
        Check($"Ctrl held as the box starts adds to the selection ({Selected().Count} selected)",
              Selected().Contains(after.Id) && want.IsSubsetOf(Selected()));

        // A Ctrl- or Shift-click on empty space — to pick the track Ctrl+V pastes to, say — leaves
        // the sections already picked alone, in a lane and under the tracks.
        var picked = Selected();
        foreach (var held in new[] { ModifierKeys.Control, ModifierKeys.Shift })
        {
            _scriptedModifiers = held;
            Press(lane, start);
            LetGo(lane);
            Check($"a {held}+click on empty lane space keeps the sections already selected ({Selected().Count} of {picked.Count})",
                  Selected().SetEquals(picked));
        }
        if (BelowTracks.ActualHeight >= 20)
        {
            _scriptedModifiers = ModifierKeys.Control;
            var under = At(BelowTracks, T, 0.3);
            Press(BelowTracks, under);
            LetGo(BelowTracks);
            Check($"a Control+click under the tracks keeps the sections already selected ({Selected().Count} of {picked.Count})",
                  Selected().SetEquals(picked));
        }
        _scriptedModifiers = null;

        // A right-press while a box is held — the start of a pan, say — keeps the box's selection.
        Press(lane, start);
        MoveTo(lane, end);
        var boxed = Selected();
        OnLaneRightButtonDown(lane, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = PreviewMouseRightButtonDownEvent });
        LetGo(lane);
        Check($"a right-press while a box is held keeps what the box selected ({Selected().Count} of {boxed.Count})",
              boxed.Count > 0 && Selected().SetEquals(boxed));

        // A box over nothing selects nothing.
        _vm.SelectClip(_vm.Tracks.SelectMany(t => t.Clips).First(c => c.Id == after.Id));
        Press(lane, At(lane, T + 3.3, 0.3));
        MoveTo(lane, At(lane, T + 5.7, 0.7));
        LetGo(lane);
        Check("a box over empty space selects nothing, and lets go of what was selected", _vm.SelectedClips.Count == 0);
    }

    /// <summary>
    /// Dragging several selected sections at once (Daniel, 2026-10-11), through the lanes' own
    /// press, move and release handlers with a scripted pointer. Sections are found again by id
    /// after every edit, because each one rebuilds the rows' sections.
    /// </summary>
    private void RunGroupGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        var song = _vm.Tracks.FirstOrDefault(t => t.IsLoaded && t.Clips.Any(c => c.TimelineLengthSeconds > 12));
        if (song is null) { notes.Add("no section longer than 12 s, so dragging a group is untested - pass a file to cover it"); return; }
        var songId = song.Id;
        double frame = 1.0 / _vm.Session.SampleRate, pps = _vm.PixelsPerSecond;
        TrackViewModel Track(Guid id) => _vm.Tracks.First(t => t.Id == id);
        ClipViewModel C(Guid id) => _vm.Tracks.SelectMany(t => t.Clips).First(c => c.Id == id);
        HashSet<Guid> Selected() => _vm.SelectedClips.Select(c => c.Id).ToHashSet();
        void Select(params Guid[] ids)
        {
            _vm.SelectClip(C(ids[0]));
            foreach (var id in ids.Skip(1)) _vm.ToggleClipSelection(C(id));
        }
        Point On(ClipViewModel c, double atSeconds)
        {
            var lane = LaneOf(c.Track)!;
            var o = lane.TranslatePoint(new Point(0, 0), this);
            return new Point(o.X + (atSeconds - _vm.ScrollSeconds) * pps, o.Y + lane.ActualHeight / 2);
        }
        Point Mid(ClipViewModel c) => On(c, (c.TimelineStartSeconds + c.TimelineEndSeconds) / 2);
        // Press, move through the middle, let go; returns what was held mid-way through.
        (bool Held, int Selected) Drag(ClipViewModel c, Point from, Point to)
        {
            var lane = LaneOf(c.Track)!;
            _scriptedWindowPoint = from;
            LaneDown(lane, from);
            var mid = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            _scriptedWindowPoint = mid;
            LaneMove(lane, mid, true);
            _scriptedWindowPoint = to;
            LaneMove(lane, to, true);
            var during = (_vm.DragInProgress, _vm.SelectedClips.Count);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            return during;
        }
        void Undo(Func<bool> done) { _vm.UndoCommand.Execute(null); PumpUntil(done); UpdateLayout(); }

        // Cut a long section into 2 s pieces to work with.
        var piece = song.Clips.First(c => c.TimelineLengthSeconds > 12);
        double U = piece.TimelineStartSeconds + 2;
        _vm.SelectedTrack = song;
        foreach (var at in new[] { U, U + 2, U + 4, U + 6 }) { _vm.SeekTo(at); _vm.SplitAtPlayheadCommand.Execute(null); }
        song = Track(songId);
        Guid IdAt(double t) => song.Clips.First(c => Math.Abs(c.TimelineStartSeconds - t) < 0.01).Id;
        var p1 = IdAt(U);
        var p2 = IdAt(U + 2);
        var p3 = IdAt(U + 4);
        _vm.ScrollSeconds = Math.Max(0, U - 2);
        UpdateLayout();

        // --- a click on one of several keeps them all while held; let go, it is just that one ---
        Select(p1, p2);
        var lane0 = LaneOf(song)!;
        _scriptedWindowPoint = Mid(C(p2));
        LaneDown(lane0, Mid(C(p2)));
        Check($"a plain press on one of several selected sections keeps them all selected while the button is down ({Selected().Count})",
              Selected().SetEquals(new[] { p1, p2 }));
        LaneUp(lane0);
        _scriptedWindowPoint = null;
        Check("letting go without moving leaves just that one selected", Selected().SetEquals(new[] { p2 }) && _vm.SelectedClip?.Id == p2);

        // --- dragging one drags them all, by the same amount, and the seam between them holds ---
        Select(p1, p2);
        double s1 = C(p1).TimelineStartSeconds, s2 = C(p2).TimelineStartSeconds;
        _scriptedModifiers = ModifierKeys.Control;                    // free, so the landing point is exact
        Drag(C(p2), Mid(C(p2)), new Point(Mid(C(p2)).X + 3 * pps, Mid(C(p2)).Y));
        _scriptedModifiers = null;
        Check($"dragging one of several selected sections moves them all by the same amount (+{C(p1).TimelineStartSeconds - s1:0.0000}s and +{C(p2).TimelineStartSeconds - s2:0.0000}s)",
              Math.Abs(C(p1).TimelineStartSeconds - s1 - 3) <= frame && Math.Abs(C(p2).TimelineStartSeconds - s2 - 3) <= frame);
        var timeline = _vm.Session.VoiceFor(song.Model)?.Timeline;
        long p1Start = Benchmix.Core.TimelineEditor.StartFrameOf(C(p1).Model, _vm.Session);
        Check("the seam between two sections moved together is still a seam, so it plays straight through",
              timeline is not null && Enumerable.Range(0, timeline.Count).Any(i => timeline[i].TrackStartFrame == p1Start && timeline.SeamAfter(i)));
        Check("both stay selected after the drop, and the one grabbed is the one commands act on",
              Selected().SetEquals(new[] { p1, p2 }) && _vm.SelectedClip?.Id == p2);
        Check($"the status bar says what moved (\"{_vm.Status}\")", _vm.Status.StartsWith("2 sections moved"));
        Undo(() => Math.Abs(C(p1).TimelineStartSeconds - s1) <= frame);
        Check("one undo puts the whole group back", Math.Abs(C(p1).TimelineStartSeconds - s1) <= frame && Math.Abs(C(p2).TimelineStartSeconds - s2) <= frame);

        // --- a group on one track goes onto another: an empty track, moved to sit just below ---
        song = Track(songId);
        _vm.AddEmptyTrackCommand.Execute(null);
        var empty = _vm.Tracks[^1];
        var emptyId = empty.Id;
        _vm.MoveTrack(empty, _vm.Tracks.IndexOf(song) + 1);
        UpdateLayout();
        empty = Track(emptyId);
        if (LaneOf(empty) is not { ActualHeight: > 0 } emptyLane)
        {
            notes.Add("the empty track's lane is not laid out, so dragging a group onto another track is untested");
            return;
        }
        Select(p1, p2);
        var downTo = new Point(Mid(C(p1)).X, emptyLane.TranslatePoint(new Point(0, emptyLane.ActualHeight / 2), this).Y);
        _scriptedModifiers = ModifierKeys.Control;
        Drag(C(p1), Mid(C(p1)), downTo);
        _scriptedModifiers = null;
        empty = Track(emptyId);
        Check($"a group on one track follows the pointer onto another track, together ({empty.Clips.Count} landed there)",
              empty.Clips.Any(c => c.Id == p1) && empty.Clips.Any(c => c.Id == p2)
              && Math.Abs(C(p1).TimelineStartSeconds - s1) <= frame && Math.Abs(C(p2).TimelineStartSeconds - s2) <= frame);
        Check("the empty track took the group's tempo and pitch", empty.Model.Speed == Track(songId).Model.Speed && empty.Model.Semitones == Track(songId).Model.Semitones);
        Check($"the status bar names the count and the track (\"{_vm.Status}\")", _vm.Status.Contains($"2 sections moved to {empty.Name}"));
        Undo(() => Track(songId).Clips.Any(c => c.Id == p1));

        // --- on two tracks: sideways only, each staying on its own ---
        _vm.SelectClip(C(p3));
        _vm.CopyClipCommand.Execute(null);
        _vm.SelectedTrack = Track(emptyId);
        _vm.SeekTo(U + 4);
        _vm.PasteClipCommand.Execute(null);
        PumpUntil(() => Track(emptyId).Clips.Count == 1);
        var copy = Track(emptyId).Clips.FirstOrDefault();
        if (copy is null) notes.Add("could not paste onto the empty track, so a group across tracks is untested");
        else
        {
            var copyId = copy.Id;
            double c3 = C(p3).TimelineStartSeconds, cc = copy.TimelineStartSeconds;
            Select(p3, copyId);
            var start = Mid(C(copyId));
            var downAndRight = new Point(start.X + 2 * pps, start.Y - LaneOf(Track(emptyId))!.ActualHeight);   // up a row and 2 s on
            _scriptedModifiers = ModifierKeys.Control;
            Drag(C(copyId), start, downAndRight);
            _scriptedModifiers = null;
            Check("a group on two tracks moves sideways only: each stays on its own track, both 2 s later",
                  Track(songId).Clips.Any(c => c.Id == p3) && Track(emptyId).Clips.Any(c => c.Id == copyId)
                  && Math.Abs(C(p3).TimelineStartSeconds - c3 - 2) <= frame && Math.Abs(C(copyId).TimelineStartSeconds - cc - 2) <= frame);
            Undo(() => Math.Abs(C(p3).TimelineStartSeconds - c3) <= frame);

            // Pulled straight up, a group on two tracks does not move — and that is no click either.
            Select(p3, copyId);
            var pullFrom = Mid(C(copyId));
            Drag(C(copyId), pullFrom, new Point(pullFrom.X + 1, pullFrom.Y - 60));
            Check($"a straight pull on a group across two tracks keeps the whole group selected ({Selected().Count} of 2)",
                  Selected().SetEquals(new[] { p3, copyId }) && Math.Abs(C(p3).TimelineStartSeconds - c3) <= frame);
        }

        // --- an edge of one is a trim of that one, and lets go of the rest ---
        Select(p1, p2);
        double len2 = C(p2).LengthSeconds, len1 = C(p1).LengthSeconds;
        var edge = new Point(On(C(p1), C(p1).TimelineStartSeconds).X + 2, Mid(C(p1)).Y);
        _scriptedWindowPoint = edge;
        LaneDown(LaneOf(Track(songId))!, edge);
        int onPress = Selected().Count;
        var edgeTo = new Point(edge.X + pps, edge.Y);
        _scriptedWindowPoint = edgeTo;
        LaneMove(LaneOf(Track(songId))!, edgeTo, true);
        int whileTrimming = Selected().Count;
        LaneUp(LaneOf(Track(songId))!);
        _scriptedWindowPoint = null;
        Check($"a drag on the edge of a selected section trims just that one and lets go of the rest ({onPress} selected on the press, {whileTrimming} once trimming)",
              onPress == 2 && whileTrimming == 1 && C(p1).LengthSeconds < len1 && C(p2).LengthSeconds == len2);
        Undo(() => C(p1).LengthSeconds == len1);

        // --- edits wait while a group is held: pressed, and then dragging ---
        HashSet<Guid> Ids() => _vm.Tracks.SelectMany(t => t.Clips).Select(c => c.Id).ToHashSet();
        // Each command on its own: refused, said so, and nothing added, removed or reloaded.
        string Refused(string phase)
        {
            var wrong = new List<string>();
            foreach (var (name, command) in new (string, ICommand)[]
                     { ("Ctrl+I", _vm.SplitAtPlayheadCommand), ("Ctrl+D", _vm.DuplicateClipCommand),
                       ("Ctrl+X", _vm.CutClipCommand), ("Ctrl+Z", _vm.UndoCommand) })
            {
                var before = Ids();
                _vm.Status = "";
                command.Execute(null);
                if (_vm.Status != "Let go of the section first." || !Ids().SetEquals(before) || _vm.IsBusy) wrong.Add(name);
            }
            return wrong.Count == 0 ? "" : $"{string.Join(", ", wrong)} went ahead {phase}";
        }
        Select(p1, p2);
        var lane1 = LaneOf(Track(songId))!;
        var grab = Mid(C(p1));
        _vm.SeekTo(C(p1).TimelineStartSeconds + 0.5);                   // a split here would cut the held section
        _scriptedModifiers = ModifierKeys.Control;
        _scriptedWindowPoint = grab;
        LaneDown(lane1, grab);
        string pressed = Refused("while a section was only pressed");
        var away = new Point(grab.X + 40, grab.Y);
        _scriptedWindowPoint = away;
        LaneMove(lane1, away, true);
        string dragging = _vm.DragInProgress ? Refused("during the drag") : "the drag did not start";
        _scriptedWindowPoint = grab;
        LaneMove(lane1, grab, true);
        LaneUp(lane1);
        _scriptedWindowPoint = null;
        _scriptedModifiers = null;
        Check($"Ctrl+I, Ctrl+D, Ctrl+X and Ctrl+Z each wait while a group is held, from the press on {pressed}{dragging}".TrimEnd(),
              pressed.Length == 0 && dragging.Length == 0);

        // --- double-clicking one of a group plays and keeps the group ---
        _vm.StopCommand.Execute(null);
        Select(p1, p2);
        var lane2 = LaneOf(Track(songId))!;
        var dbl = Mid(C(p2));
        _scriptedWindowPoint = dbl;
        LaneDown(lane2, dbl, 1); LaneUp(lane2);
        LaneDown(lane2, dbl, 2); LaneUp(lane2);
        _scriptedWindowPoint = null;
        Check($"double-clicking one of several selected sections plays and keeps them all selected ({Selected().Count})",
              _vm.Session.Engine.State == Benchmix.Engine.Audio.TransportState.Playing && Selected().SetEquals(new[] { p1, p2 }));

        // --- and on a member's edge, where a press would trim, it is still a double-click ---
        _vm.StopCommand.Execute(null);
        Select(p1, p2);
        var lane3 = LaneOf(Track(songId))!;
        var nearEdge = new Point(On(C(p2), C(p2).TimelineStartSeconds).X + 2, Mid(C(p2)).Y);
        _scriptedWindowPoint = nearEdge;
        LaneDown(lane3, nearEdge, 1); LaneUp(lane3);
        LaneDown(lane3, nearEdge, 2); LaneUp(lane3);
        _scriptedWindowPoint = null;
        Check($"double-clicking a selected section's edge plays and keeps the group selected ({Selected().Count})",
              _vm.Session.Engine.State == Benchmix.Engine.Audio.TransportState.Playing && Selected().SetEquals(new[] { p1, p2 })
              && C(p1).LengthSeconds == len1);

        // --- double-clicking empty space plays and keeps what was selected ---
        _vm.StopCommand.Execute(null);
        Select(p1, p2);
        double past = Track(songId).Clips.Max(c => c.TimelineEndSeconds) + 3;
        _vm.ScrollSeconds = Math.Max(0, past - 5);
        UpdateLayout();
        var gapLane = LaneOf(Track(songId))!;
        var o = gapLane.TranslatePoint(new Point(0, 0), this);
        var gap = new Point(o.X + (past - _vm.ScrollSeconds) * pps, o.Y + gapLane.ActualHeight / 2);
        _scriptedWindowPoint = gap;
        LaneDown(gapLane, gap, 1); LaneUp(gapLane);
        LaneDown(gapLane, gap, 2); LaneUp(gapLane);
        _scriptedWindowPoint = null;
        Check($"double-clicking empty space plays from there and keeps what was selected ({Selected().Count})",
              _vm.Session.Engine.State == Benchmix.Engine.Audio.TransportState.Playing && Selected().SetEquals(new[] { p1, p2 }));
        _vm.StopCommand.Execute(null);

        // --- short sections can still be grabbed by the middle and dragged as a group ---
        _vm.ScrollSeconds = Math.Max(0, U - 2);
        UpdateLayout();
        _vm.SelectedTrack = Track(songId);
        double V = U + 6.5;
        foreach (var at in new[] { V, V + 0.3, V + 0.6 }) { _vm.SeekTo(at); _vm.SplitAtPlayheadCommand.Execute(null); }
        var shortA = Track(songId).Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - V) < 0.01);
        var shortB = Track(songId).Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - (V + 0.3)) < 0.01);
        if (shortA is null || shortB is null) { notes.Add("could not cut short sections, so dragging them as a group is untested"); return; }
        var (a, b) = (shortA.Id, shortB.Id);
        double sa = shortA.TimelineStartSeconds, sb = shortB.TimelineStartSeconds;
        Select(a, b);
        _scriptedModifiers = ModifierKeys.Control;
        Drag(C(a), Mid(C(a)), new Point(Mid(C(a)).X + 2 * pps, Mid(C(a)).Y));
        _scriptedModifiers = null;
        Check($"two sections {0.3 * pps:0} px wide can be grabbed by the middle and dragged as a group",
              Math.Abs(C(a).TimelineStartSeconds - sa - 2) <= frame && Math.Abs(C(b).TimelineStartSeconds - sb - 2) <= frame);
    }

    /// <summary>
    /// Typing a knob's exact value (Daniel, 2026-10-12), through the knobs' own press handling and the
    /// box's own key handler. Puts every knob back as it found it, with Ctrl+Z.
    /// </summary>
    private void RunKnobTypingGestures(Action<string, bool> Check, List<string> notes)
    {
        UpdateLayout();
        var source = PresentationSource.FromVisual(this);
        var first = _vm.Tracks.FirstOrDefault(t => t.IsLoaded);
        if (source is null || first is null || StripOf(first) is not { } strip0)
        {
            notes.Add("no laid-out track strip, so typing a knob's value is untested - pass a file to cover it");
            return;
        }
        var id = first.Id;
        TrackViewModel Track() => _vm.Tracks.First(t => t.Id == id);
        Controls.Knob? KnobOf(string label) => StripOf(Track()) is { } st ? Descendants<Controls.Knob>(st).FirstOrDefault(k => k.Label == label) : null;
        if (new[] { "TEMPO", "PITCH", "LOW", "HIGH", "VOL" }.Any(l => KnobOf(l) is not { ActualHeight: > 0 }))
        {
            notes.Add("a knob was not laid out, so typing a knob's value is untested");
            return;
        }
        KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = PreviewKeyDownEvent };
        Point OnNumber(Controls.Knob k) => new(k.ActualWidth / 2, k.ActualHeight - 4);
        Point OnDial(Controls.Knob k) => new(k.ActualWidth / 2, (k.ActualHeight - 26) / 2);
        bool Open() => KnobValueBox.Visibility == Visibility.Visible;
        void Type(string label, string text)
        {
            KnobOf(label)!.Press(OnNumber(KnobOf(label)!), 2);
            KnobValueBox.Text = text;
            OnKnobValueKeyDown(KnobValueBox, Press(Key.Enter));
        }
        var m = Track().Model;
        var was = (m.Speed, m.Semitones, m.EqLow, m.EqHigh, m.Gain, Link: Track().LinkPitchToTempo);
        Track().LinkPitchToTempo = false;
        _vm.EndGesture();

        var tempo = KnobOf("TEMPO")!;
        tempo.Press(OnNumber(tempo), 2);
        Check($"double-clicking TEMPO's number opens a box over it, filled in with its value ({KnobValueBox.Text})",
              Open() && KnobValueBox.Text == tempo.EditText() && LabelFor(KnobValueBox) == "textbox:knobValue");
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // let a queued focus run, if one was queued
        Check("the box is never given the keyboard in a scripted run", Open() && !KnobValueBox.IsKeyboardFocusWithin && !ReferenceEquals(Keyboard.FocusedElement, KnobValueBox));
        KnobValueBox.Text = "-4.21";
        OnKnobValueKeyDown(KnobValueBox, Press(Key.Enter));
        Check($"typing -4.21 and Enter sets TEMPO to 4.21% slower, exactly, and shows it ({KnobOf("TEMPO")!.ValueText})",
              !Open() && m.Speed == 1 + Math.Round(-4.21, 2) / 100 && KnobOf("TEMPO")!.ValueText == "-4.21%");

        Type("PITCH", "\u22122.88");
        Check($"typing −2.88 sets PITCH to 2.88 semitones down ({KnobOf("PITCH")!.ValueText})", m.Semitones == -2.88 && KnobOf("PITCH")!.ValueText == "-2.88 st");
        Type("LOW", "-6.5");
        Check($"typing -6.5 sets LOW to -6.5 dB ({KnobOf("LOW")!.ValueText})", m.EqLow == Math.Pow(10, -6.5 / 20) && KnobOf("LOW")!.ValueText == "-6.5");
        Type("VOL", "kill");
        Check($"typing KILL sets VOL all the way down ({KnobOf("VOL")!.ValueText})", m.Gain == 0 && KnobOf("VOL")!.ValueText.Contains('∞'));

        tempo = KnobOf("TEMPO")!;
        tempo.Press(OnNumber(tempo), 2);
        KnobValueBox.Text = "60";
        OnKnobValueKeyDown(KnobValueBox, Press(Key.Enter));
        Check($"a value past the knob's range is refused, the box stays open and says the range (\"{_vm.Status}\")",
              Open() && m.Speed == 1 + Math.Round(-4.21, 2) / 100 && _vm.Status.Contains("-50.00%") && _vm.Status.Contains("+50.00%"));
        OnKnobValueKeyDown(KnobValueBox, Press(Key.Escape));
        Check("Esc keeps the value as it was", !Open() && m.Speed == 1 + Math.Round(-4.21, 2) / 100);

        var high = KnobOf("HIGH")!;
        high.Press(OnNumber(high), 2);
        KnobValueBox.Text = "3";
        WindowPress(MouseButton.Left, LaneOf(Track()));
        Check("clicking elsewhere keeps what was typed", !Open() && m.EqHigh == Math.Pow(10, 3.0 / 20));

        tempo = KnobOf("TEMPO")!;
        tempo.Press(OnDial(tempo), 2);
        Check("a double-click on the knob itself still resets it", m.Speed == 1 && !Open());

        Track().LinkPitchToTempo = true;
        var pitch = KnobOf("PITCH")!;
        pitch.Press(OnNumber(pitch), 2);
        Check($"with LINK on, PITCH cannot be typed and the status says why (\"{_vm.Status}\")", !Open() && _vm.Status.Contains("LINK"));
        Track().LinkPitchToTempo = false;

        // Each typed value, and the reset, is one undo step.
        void Undo(Func<bool> done) { _vm.UndoCommand.Execute(null); PumpUntil(done); UpdateLayout(); }
        var tempoNow = KnobOf("TEMPO")!;
        tempoNow.Press(OnNumber(tempoNow), 2);
        bool openBefore = Open();
        Undo(() => Track().Model.Speed != 1);
        Check("an undo that rebuilds the rows closes a knob's box left open over them", openBefore && !Open());
        bool resetUndone = Track().Model.Speed == 1 + Math.Round(-4.21, 2) / 100;
        Undo(() => Track().Model.EqHigh != Math.Pow(10, 3.0 / 20));
        bool highUndone = Track().Model.EqHigh == was.EqHigh;
        Check("one Ctrl+Z takes back the reset, and the next the typed HIGH — each one undo step", resetUndone && highUndone);
        Undo(() => Track().Model.Gain != 0);
        Undo(() => Track().Model.EqLow == was.EqLow);
        Undo(() => Track().Model.Semitones == was.Semitones);
        Undo(() => Track().Model.Speed == was.Speed);
        var back = Track().Model;
        if ((back.Speed, back.Semitones, back.EqLow, back.EqHigh, back.Gain) != (was.Speed, was.Semitones, was.EqLow, was.EqHigh, was.Gain))
            notes.Add("the knob typing checks could not undo back to where they started");

        // Opened only to read it, the box changes nothing; and a turn of a knob followed straight
        // away by a value typed into the same knob are two undo steps, not one.
        var tk = KnobOf("TEMPO")!;
        _vm.EndGesture();
        tk.Value = 93.0 / 102;                    // a turn, to a value off the box's 0.01% grid, as Match BPM makes
        double turned = Track().Model.Speed;
        tk.Press(OnNumber(tk), 2);
        WindowPress(MouseButton.Left, LaneOf(Track()));
        Check($"opening a knob's box only to read it ('{tk.EditText()}'), then clicking away, leaves the value exactly as it was",
              !Open() && Track().Model.Speed == turned);
        Type("TEMPO", "-3");
        double typedSpeed = Track().Model.Speed;
        Undo(() => Track().Model.Speed != 1 + Math.Round(-3.0, 2) / 100);
        Check($"a value typed straight after turning the same knob is its own undo step (turned {turned}, typed {typedSpeed}, after one undo {Track().Model.Speed})", Track().Model.Speed == turned);
        Undo(() => Track().Model.Speed != turned);
        if (Track().Model.Speed != was.Speed) notes.Add("the knob checks could not undo the last turn");
        Track().LinkPitchToTempo = was.Link;
    }

    /// <summary>
    /// Joining sections back into one (Daniel, 2026-10-12), as he described it: split out a piece,
    /// move it to another track, then put the song back — through Ctrl+J's command and the lane
    /// menu's own Click handler. Leaves the project as it found it.
    /// </summary>
    private void RunJoinGestures(Action<string, bool> Check, List<string> notes)
    {
        bool Bound(Key key, ICommand command) => InputBindings.OfType<KeyBinding>()
            .Any(b => b.Key == key && b.Modifiers == ModifierKeys.Control && ReferenceEquals(b.Command, command));
        Check("Ctrl+J is bound to joining sections", Bound(Key.J, _vm.JoinSectionsCommand));

        UpdateLayout();
        var song = _vm.Tracks.FirstOrDefault(t => t.IsLoaded && t.Clips.Any(c => c.TimelineLengthSeconds > 12));
        if (song is null) { notes.Add("no section longer than 12 s, so joining is untested - pass a file to cover it"); return; }
        var songId = song.Id;
        double frame = 1.0 / _vm.Session.SampleRate;
        TrackViewModel Song() => _vm.Tracks.First(t => t.Id == songId);
        int tracks = _vm.Tracks.Count, sections = song.Clips.Count;
        void Undo(Func<bool> done) { _vm.UndoCommand.Execute(null); PumpUntil(done); UpdateLayout(); }

        var piece = song.Clips.First(c => c.TimelineLengthSeconds > 12);
        double from = piece.TimelineStartSeconds, to = piece.TimelineEndSeconds, u = from + 2;
        _vm.SelectedTrack = song;
        foreach (var at in new[] { u, u + 4 }) { _vm.SeekTo(at); _vm.SplitAtPlayheadCommand.Execute(null); }
        ClipViewModel On(double start) => Song().Clips.First(c => Math.Abs(c.TimelineStartSeconds - start) < 0.01);
        var middle = On(u);
        var middleId = middle.Id;

        // Take the middle piece out onto a track of its own, as he does.
        _vm.AddEmptyTrackCommand.Execute(null);
        var other = _vm.Tracks[^1];
        _vm.BeginMove(middle);
        _vm.DragMoveTo(other, u, free: true);
        _vm.EndDrag();
        bool holed = Song().Clips.All(c => c.Id != middleId) && other.Clips.Any(c => c.Id == middleId);

        // One of the pieces left behind, and Ctrl+J.
        _vm.SelectClip(On(from));
        _vm.JoinSectionsCommand.Execute(null);
        var joined = Song().Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - from) < 0.01);
        Check($"after taking a piece out to another track, Ctrl+J on what is left puts the song back in one ({_vm.Status})",
              holed && joined is not null && Math.Abs(joined.TimelineEndSeconds - to) <= frame && _vm.Status.Contains("missing"));
        Check("the piece taken out stays where it was put", other.Clips.Any(c => c.Id == middleId && Math.Abs(c.TimelineStartSeconds - u) <= frame));

        Undo(() => Song().Clips.Any(c => Math.Abs(c.TimelineStartSeconds - (u + 4)) < 0.01));
        Check("one Ctrl+Z takes the join back, gap and all", Song().Clips.Count(c => c.TimelineStartSeconds >= from - 0.01 && c.TimelineStartSeconds < to) == 2);

        // The lane menu's item, with both pieces selected.
        _vm.SelectClip(On(from));
        _vm.ToggleClipSelection(On(u + 4));
        OnJoinSections(this, new RoutedEventArgs());
        joined = Song().Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - from) < 0.01);
        Check("Join sections in the lane menu joins the two selected", joined is not null && Math.Abs(joined.TimelineEndSeconds - to) <= frame);
        Undo(() => Song().Clips.Any(c => Math.Abs(c.TimelineStartSeconds - (u + 4)) < 0.01));

        // A loop's repeat is not part of the song carrying on.
        _vm.SelectClip(On(from));
        _vm.DuplicateClipCommand.Execute(null);
        int before = Song().Clips.Count;
        _vm.SelectClip(On(from));
        _vm.ToggleClipSelection(On(u));
        _vm.JoinSectionsCommand.Execute(null);
        Check($"a repeat is not joined to what it repeats, and the status says why ({_vm.Status})",
              Song().Clips.Count == before && _vm.Status.Contains("do not line up"));

        // Put everything back: the repeat, the move, the empty track, the two splits.
        for (int i = 0; i < 8 && (_vm.Tracks.Count != tracks || Song().Clips.Count != sections); i++)
        {
            var state = (_vm.Tracks.Count, Song().Clips.Count);
            Undo(() => (_vm.Tracks.Count, Song().Clips.Count) != state);
        }
        if (_vm.Tracks.Count != tracks || Song().Clips.Count != sections) notes.Add("the join checks could not undo back to where they started");
    }

    /// <summary>
    /// Tempo ramps (Daniel, 2026-10-11) through the lane's own handlers, with a scripted pointer,
    /// modifiers and focus: RAMP MODE, the two clicks, typing the tempo, moving the end, retyping it,
    /// removing the ramp, and one undo step for each. Leaves the project as it found it.
    /// </summary>
    private void RunTempoRampGestures(Action<string, bool> Check, List<string> notes)
    {
        bool Bound(Key key, ICommand command) => InputBindings.OfType<KeyBinding>()
            .Any(b => b.Key == key && b.Modifiers == ModifierKeys.Control && ReferenceEquals(b.Command, command));
        Check("Ctrl+R is bound to ramp mode", Bound(Key.R, _vm.ToggleRampModeCommand));
        Check("Escape is not a bare KeyBinding, so it cannot fire inside a text box",
              !InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.Escape));

        var toggle = Descendants<System.Windows.Controls.Primitives.ToggleButton>(this).FirstOrDefault(b => b.Content as string == "RAMP MODE");
        var source = PresentationSource.FromVisual(this);
        UpdateLayout();
        var first = _vm.Tracks.FirstOrDefault(t => t.IsLoaded && t.Model.Bpm > 0 && t.Clips.Count > 0 && t.Model.Ramps.Count == 0 && t.Model.Warps.Count == 0);
        if (toggle is null || source is null || first is null || LaneOf(first) is not { ActualHeight: > 0 } lane0)
        {
            notes.Add("no RAMP MODE toggle, or no laid-out track with a known tempo, so tempo ramps are untested in the window - pass a file to cover them");
            return;
        }
        KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = PreviewKeyDownEvent };
        void Toggle()
        {
            bool was = _vm.RampMode;
            var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
            ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            PumpUntil(() => _vm.RampMode != was);
        }

        var id = first.Id;
        TrackViewModel Track() => _vm.Tracks.First(t => t.Id == id);
        FrameworkElement Lane() => LaneOf(Track())!;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double frame = 1.0 / _vm.Session.SampleRate;
        double OnFrame(double sec) => Benchmix.Core.TimelineEditor.SecondsOf(_vm.Session.SecondsToFrames(sec), _vm.Session);

        // Room for 0-55 s across the lane.
        _vm.ScrollSeconds = 0;
        _vm.PixelsPerSecond = Math.Min(20, Math.Max(4, (lane0.ActualWidth - 120) / 55));
        double pps = _vm.PixelsPerSecond;
        UpdateLayout();
        Point At(FrameworkElement lane, double sec)
        {
            var o = lane.TranslatePoint(new Point(0, 0), this);
            return new Point(o.X + (sec - _vm.ScrollSeconds) * pps, o.Y + lane.ActualHeight / 2);
        }
        // A left press goes as WPF routes it: the window's Preview, then the lane, then the release.
        void Click(FrameworkElement lane, Point p, bool ctrl = true)
        {
            _scriptedModifiers = ctrl ? ModifierKeys.Control : ModifierKeys.None;
            _scriptedWindowPoint = p;
            WindowPress(MouseButton.Left, lane);
            LaneDown(lane, p);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }
        void Undo(Func<bool> done) { _vm.UndoCommand.Execute(null); PumpUntil(done); UpdateLayout(); }
        List<FrameworkElement> RemoveItems(FrameworkElement lane)
            => lane.ContextMenu?.Items.OfType<FrameworkElement>().Where(i => i.Tag as string == "removeRamp").ToList() ?? new();

        var before = Track().Clips.Select(c => (c.Id, At: c.TimelineStartSeconds)).ToList();
        double origin = Track().Model.TimelineOffsetSeconds, v0 = Track().Model.Speed, bpm = Track().Model.Bpm;
        double from = bpm * v0;
        string fromText = from.ToString("0.0", inv);

        // --- RAMP MODE ---
        _vm.LoopMode = false;
        _vm.WarpMode = false;
        _vm.RampMode = false;
        Toggle();
        Check("RAMP MODE turns on through its own toggle", _vm.RampMode);
        _vm.LoopMode = true;
        bool rampWentOff = !_vm.RampMode;
        Toggle();
        Check("loop mode and ramp mode are never on together", rampWentOff && _vm.RampMode && !_vm.LoopMode);

        // --- a track with no tempo starts nothing (before the first dot, so it cannot disturb it) ---
        int tracks = _vm.Tracks.Count;
        _vm.AddEmptyTrackCommand.Execute(null);
        UpdateLayout();
        if (_vm.Tracks.Count == tracks + 1 && LaneOf(_vm.Tracks[^1]) is { ActualHeight: > 0 } emptyLane)
        {
            Click(emptyLane, At(emptyLane, 20));
            Check($"a track whose tempo is unknown starts no ramp and says why (\"{_vm.Status}\")", !_vm.RampPending && _vm.Status.Contains("unknown"));
        }
        else notes.Add("the empty track was not laid out, so a ramp on a track with no tempo is untested");
        if (_vm.Tracks.Count > tracks) Undo(() => _vm.Tracks.Count == tracks);
        _vm.SelectedTrack = Track();

        // --- the first click, and Esc ---
        Click(Lane(), At(Lane(), 30));
        var dot = Track().RampVisuals.LastOrDefault();
        Check($"the first click puts a dot at 30.00s, filled in with the tempo it plays there ({fromText} BPM)",
              _vm.RampPending && dot is { Look: Controls.RampLook.StartOnly } d && d.StartSeconds == OnFrame(30) && d.FromBpm == from);

        _scriptedFocus = BpmBox;
        var esc = Press(Key.Escape);
        OnWindowPreviewKeyDown(this, esc);
        Check("Esc with PROJECT BPM focused is left to the box", !esc.Handled && _vm.RampPending);
        _scriptedFocus = this;
        esc = Press(Key.Escape);
        OnWindowPreviewKeyDown(this, esc);
        _scriptedFocus = null;
        Check("Esc takes the first dot away again", esc.Handled && !_vm.RampPending && Track().RampVisuals.Count == 0);

        // --- the second click opens the box; typing and Enter make the ramp ---
        Click(Lane(), At(Lane(), 30));
        Click(Lane(), At(Lane(), 45));
        UpdateLayout();
        var box = RampBpmBoxOf(Track());
        double x45 = (45 - _vm.ScrollSeconds) * pps;
        double gridWidth = (box?.Parent as FrameworkElement)?.ActualWidth ?? 0;
        double wantLeft = Math.Max(2, x45 + 70 <= gridWidth ? x45 + 10 : x45 - 70);
        Check($"the second click opens the tempo box beside the end point, filled in with {fromText}",
              box is { Visibility: Visibility.Visible } && box.Text == fromText && Math.Abs(box.Margin.Left - wantLeft) <= 1);
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // let a queued focus run, if one was queued
        Check("the box is never given the keyboard in a scripted run", box is { IsVisible: true } && !box.IsKeyboardFocusWithin && !ReferenceEquals(Keyboard.FocusedElement, box));
        Check("the control map labels the box textbox:rampBpm", box is not null && LabelFor(box) == "textbox:rampBpm");
        if (box is null) { notes.Add("the ramp's tempo box was not found, so typing a tempo is untested"); return; }

        // The second press of a double-click there, the lane's usual play gesture, leaves the box open.
        var again = At(Lane(), 45);
        _scriptedWindowPoint = again;
        WindowPress(MouseButton.Left, Lane(), clickCount: 2);
        LaneDown(Lane(), again, 2);
        LaneUp(Lane());
        _scriptedWindowPoint = null;
        Check("the second press of a double-click leaves the tempo box open and places no ramp", box.Visibility == Visibility.Visible && Track().Model.Ramps.Count == 0);
        var right = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = PreviewMouseRightButtonDownEvent };
        box.RaiseEvent(right);
        Check("a right-click inside the tempo box, for its own Cut, Copy and Paste, leaves the typing open", box.Visibility == Visibility.Visible && _vm.RampEntryOpen);

        string to1 = (from + 4.5).ToString("0.0", inv);
        double v1 = double.Parse(to1, inv) / bpm;
        box.Text = to1;
        OnRampBpmKeyDown(box, Press(Key.Enter));
        var m = Track().Model;
        Check($"typing {to1} and Enter makes the ramp 30.00s → 45.00s, ending at {to1} BPM",
              m.Ramps.Count == 1 && m.Ramps[0] == new Benchmix.Core.Model.TempoRamp(OnFrame(30), OnFrame(45), v1) && !Track().IsTypingRampBpm && !_vm.RampPending);
        var voice = _vm.Session.VoiceFor(m);
        Check("the engine plays the tempo map the project describes",
              voice is not null && !voice.Tempo.IsConstant && voice.Tempo.SameAs(_vm.Session.EngineTempoOf(m)));
        var wave = Descendants<Controls.WaveformView>(Lane()).FirstOrDefault();
        Check("the lane is handed the ramp to draw", wave?.Ramps is { Count: 1 } drawn && drawn[0].Look == Controls.RampLook.Placed && drawn[0].EndSeconds == OnFrame(45));

        // Where the tempo puts the sections: nothing before the ramp moves; one after it comes sooner.
        double a = OnFrame(30), b = OnFrame(45);
        var later = before.Where(x => x.At > 46).OrderBy(x => x.At).Select(x => Track().Clips.FirstOrDefault(c => c.Id == x.Id)).FirstOrDefault(c => c is not null);
        if (later is null || origin > a) notes.Add("no section after 46 s, or the track starts after 30 s, so where a ramp puts later sections is untested here");
        else
        {
            double fb = v0 * (a - origin) + (v0 + v1) / 2 * (b - a);
            double want = b + (later.TrackStartSeconds - fb) / v1;
            double was = before.First(x => x.Id == later.Id).At;
            Check($"a section after the ramp now comes {was - later.TimelineStartSeconds:0.00}s sooner, as the faster tempo says",
                  Math.Abs(later.TimelineStartSeconds - want) <= frame);
        }
        var earlier = before.Where(x => x.At < 29).Select(x => (Clip: Track().Clips.FirstOrDefault(c => c.Id == x.Id), x.At)).FirstOrDefault(x => x.Clip is not null);
        if (earlier.Clip is null) notes.Add("no section before 29 s, so that a ramp leaves earlier sections alone is untested here");
        else Check("a section before the ramp did not move", earlier.Clip.TimelineStartSeconds == earlier.At);

        double h = Lane().ActualHeight - 1, dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var g = Controls.RampLayout.Layout(Track().RampVisuals[0], pps, _vm.ScrollSeconds, h, dip);
        Check("it is drawn going up: first dot on the bottom edge, second on the top, joined by a line",
              g.StartDot.Y == h - Controls.RampLayout.Inset && g.EndDot.Y == Controls.RampLayout.Inset);

        // --- dragging the end dot ---
        var lane = Lane();
        var grab = At(lane, 45);
        var to = At(lane, 50);
        _scriptedModifiers = ModifierKeys.Control;
        _scriptedWindowPoint = grab;
        WindowPress(MouseButton.Left, lane);
        LaneDown(lane, grab);
        var midway = new Point((grab.X + to.X) / 2, grab.Y);
        _scriptedWindowPoint = midway;
        LaneMove(lane, midway, true);
        _vm.ToggleRampModeCommand.Execute(null);
        _vm.ToggleLoopModeCommand.Execute(null);
        bool stayedOn = _vm.RampMode && !_vm.LoopMode;
        _scriptedWindowPoint = to;
        LaneMove(lane, to, true);
        LaneUp(lane);
        _scriptedWindowPoint = null;
        _scriptedModifiers = null;
        Check("Ctrl+R and Ctrl+L wait while a ramp's dot is held", stayedOn);
        Check("dragging the end dot to 50 s moves only the end", m.Ramps.Count == 1 && m.Ramps[0].EndSeconds == OnFrame(50) && m.Ramps[0].StartSeconds == OnFrame(30));

        // --- clicking the end dot retypes its tempo ---
        Click(Lane(), At(Lane(), 50));
        UpdateLayout();
        box = RampBpmBoxOf(Track())!;
        Check($"clicking the end dot opens the box with {to1}", box.Visibility == Visibility.Visible && box.Text == to1 && _vm.RampEntryOpen);
        string to2 = (from + 6.03).ToString("0.00", inv);
        double v2 = double.Parse(to2, inv) / bpm;
        box.Text = to2;
        OnRampBpmKeyDown(box, Press(Key.Enter));
        Check($"typing {to2} and Enter changes only the end tempo", m.Ramps.Count == 1 && m.Ramps[0].ToSpeed == v2 && m.Ramps[0].EndSeconds == OnFrame(50));

        // The box shows it rounded; opened only to look and clicked away from, it keeps it exactly.
        Click(Lane(), At(Lane(), 50));
        UpdateLayout();
        string shown = RampBpmBoxOf(Track())!.Text;
        Click(Lane(), At(Lane(), 10));
        Check($"opening the box only to look ('{shown}'), then clicking away, keeps {to2} exactly",
              shown == (bpm * v2).ToString("0.0", inv) && m.Ramps[0].ToSpeed == v2 && !Track().IsTypingRampBpm && _vm.Status.Contains("Kept"));

        Click(Lane(), At(Lane(), 50));
        UpdateLayout();
        box = RampBpmBoxOf(Track())!;
        box.Text = (bpm * 4 + 50).ToString("0", inv);
        OnRampBpmKeyDown(box, Press(Key.Enter));
        Check($"a tempo it cannot reach is refused and the box stays open (\"{_vm.Status}\")",
              box.Visibility == Visibility.Visible && m.Ramps[0].ToSpeed == v2 && _vm.Status.Contains("between"));

        string to3 = (from + 8).ToString("0.0", inv);
        double v3 = double.Parse(to3, inv) / bpm;
        box.Text = to3;
        Click(Lane(), At(Lane(), 10));
        Check("clicking elsewhere keeps what was typed, and that click starts nothing else",
              m.Ramps.Count == 1 && m.Ramps[0].ToSpeed == v3 && !_vm.RampPending && !Track().IsTypingRampBpm);

        // --- what a press does not do in ramp mode ---
        Click(Lane(), At(Lane(), 40));
        Check($"a new ramp cannot start inside another (\"{_vm.Status}\")", !_vm.RampPending && _vm.Status.Contains("inside a tempo ramp"));

        var section = Track().Clips.FirstOrDefault(c => c.TimelineStartSeconds < 25 && c.TimelineEndSeconds > c.TimelineStartSeconds + 2 && c.TimelineStartSeconds + 1 < 28);
        if (section is null) notes.Add("no section before the ramp to press on, so that ramp mode leaves sections alone is untested");
        else
        {
            double was = section.TimelineStartSeconds, inside = Math.Min(section.TimelineEndSeconds - 0.5, 27);
            lane = Lane();
            var press = At(lane, Math.Max(was + 0.5, inside));
            var away = new Point(press.X + 40, press.Y);
            _scriptedWindowPoint = press;
            WindowPress(MouseButton.Left, lane);
            LaneDown(lane, press);
            _scriptedWindowPoint = away;
            LaneMove(lane, away, true);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            var after = Track().Clips.FirstOrDefault(c => c.Id == section.Id);
            Check("in ramp mode a drag across a section moves no section", after is not null && after.TimelineStartSeconds == was && !_vm.DragInProgress);
            _vm.CancelRamp();
        }

        // --- the right-click menu ---
        lane = Lane();
        LaneRightDown(lane, At(lane, 5));
        PrepareLaneMenu(lane);
        bool hiddenAway = RemoveItems(lane).All(i => i.Visibility == Visibility.Collapsed);
        LaneRightDown(lane, At(lane, 40));
        PrepareLaneMenu(lane);
        var items = RemoveItems(lane);
        Check("the right-click menu offers Remove tempo ramp over a ramp, and not elsewhere",
              hiddenAway && items.Count == 2 && items.All(i => i.Visibility == Visibility.Visible));

        // A right-press while a dot is held does not look under the pointer — and must not leave the
        // ramp from the right-click before it on offer, by an index that may now be another ramp.
        var dotPress = At(lane, 30);
        _scriptedWindowPoint = dotPress;
        LaneDown(lane, dotPress);
        LaneRightDown(lane, At(lane, 5));
        PrepareLaneMenu(lane);
        bool staleHidden = RemoveItems(lane).All(i => i.Visibility == Visibility.Collapsed);
        LaneUp(lane);
        _scriptedWindowPoint = null;
        Check("a right-click while a ramp's dot is held offers no Remove tempo ramp left over from an earlier right-click", staleHidden);
        LaneRightDown(lane, At(lane, 40));
        PrepareLaneMenu(lane);
        items = RemoveItems(lane);
        if (items.OfType<MenuItem>().FirstOrDefault() is { } remove)
        {
            OnRemoveRampMenu(remove, new RoutedEventArgs());
            var back = later is null ? null : Track().Clips.FirstOrDefault(c => c.Id == later.Id);
            Check("Remove tempo ramp takes it off, and the section after it goes back where it was",
                  m.Ramps.Count == 0 && (back is null || Math.Abs(back.TimelineStartSeconds - before.First(x => x.Id == back.Id).At) <= frame));
        }
        else notes.Add("no Remove tempo ramp item in the lane menu");

        // --- one undo step each ---
        bool UndoTo(Func<Benchmix.Core.Model.TrackModel, bool> state) { Undo(() => state(Track().Model)); return state(Track().Model); }
        bool u1 = UndoTo(x => x.Ramps.Count == 1 && x.Ramps[0].ToSpeed == v3);
        bool u2 = u1 && UndoTo(x => x.Ramps.Count == 1 && x.Ramps[0].ToSpeed == v2);
        bool u3 = u2 && UndoTo(x => x.Ramps.Count == 1 && x.Ramps[0].ToSpeed == v1 && x.Ramps[0].EndSeconds == OnFrame(50));
        bool u4 = u3 && UndoTo(x => x.Ramps.Count == 1 && x.Ramps[0].EndSeconds == OnFrame(45));
        bool u5 = u4 && UndoTo(x => x.Ramps.Count == 0);
        Check($"each ramp edit was one undo step: back to {to3}, then {to2}, then {to1}, then ending at 45 s, then none", u5);

        // --- RAMP MODE off drops a ramp that was only started ---
        Click(Lane(), At(Lane(), 30));
        bool pending = _vm.RampPending;
        Toggle();
        Check("RAMP MODE turns off through its toggle and drops an unfinished ramp", pending && !_vm.RampMode && !_vm.RampPending && Track().RampVisuals.Count == 0);
    }

    /// <summary>
    /// Marking a stretch (Daniel, 2026-10-12) through the lane's own handlers, with a scripted pointer,
    /// modifiers and focus: MARK MODE, a drag that marks, snapping, moving an end, a click that puts
    /// the playhead there, Shift+click, and Ctrl+C, Ctrl+V, Ctrl+X, Delete and Ctrl+D on the mark —
    /// one undo step each. Leaves the project as it found it.
    /// </summary>
    private void RunMarkGestures(Action<string, bool> Check, List<string> notes)
    {
        bool Bound(Key key, ICommand command) => InputBindings.OfType<KeyBinding>()
            .Any(b => b.Key == key && b.Modifiers == ModifierKeys.Control && ReferenceEquals(b.Command, command));
        Check("Ctrl+M is bound to mark mode", Bound(Key.M, _vm.ToggleMarkModeCommand));

        var toggle = Descendants<System.Windows.Controls.Primitives.ToggleButton>(this).FirstOrDefault(b => b.Content as string == "MARK MODE");
        var source = PresentationSource.FromVisual(this);
        UpdateLayout();
        var first = _vm.Tracks.FirstOrDefault(t => t.IsLoaded && t.Model.Ramps.Count == 0 && t.Model.Warps.Count == 0 && t.Clips.Any(c => c.TimelineLengthSeconds > 30));
        if (toggle is null || source is null || first is null || LaneOf(first) is not { ActualHeight: > 0 } lane0)
        {
            notes.Add("no MARK MODE toggle, or no laid-out track with a section over 30 s, so marking a stretch is untested in the window - pass a file to cover it");
            return;
        }
        void Toggle()
        {
            bool was = _vm.MarkMode;
            var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
            ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            PumpUntil(() => _vm.MarkMode != was);
        }

        var id = first.Id;
        var s = _vm.Session;
        int tracks = _vm.Tracks.Count, sections = first.Clips.Count;
        TrackViewModel Song() => _vm.Tracks.First(t => t.Id == id);
        FrameworkElement Lane() => LaneOf(Song())!;
        long F(TrackViewModel t, double timeline) => Benchmix.Core.TimelineEditor.TrackFrameAt(t.Model, timeline, s);
        bool Near(long a, long b) => Math.Abs(a - b) <= 1;          // a pointer lands to within a frame of where it was aimed
        void Undo(Func<bool> done) { _vm.UndoCommand.Execute(null); PumpUntil(done); UpdateLayout(); }
        KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = PreviewKeyDownEvent };

        var piece = Song().Clips.First(c => c.TimelineLengthSeconds > 30);
        double p0 = piece.TimelineStartSeconds;
        _vm.ScrollSeconds = p0;
        _vm.PixelsPerSecond = Math.Min(20, Math.Max(4, (lane0.ActualWidth - 120) / 35));
        double pps = _vm.PixelsPerSecond;
        UpdateLayout();
        Point At(FrameworkElement lane, double sec)
        {
            var o = lane.TranslatePoint(new Point(0, 0), this);
            return new Point(o.X + (sec - _vm.ScrollSeconds) * pps, o.Y + lane.ActualHeight / 2);
        }
        void Drag(FrameworkElement lane, Point from, Point to, ModifierKeys mods)
        {
            _scriptedModifiers = mods;
            _scriptedWindowPoint = from;
            WindowPress(MouseButton.Left, lane);
            LaneDown(lane, from);
            var mid = new Point((from.X + to.X) / 2, from.Y);
            _scriptedWindowPoint = mid;
            LaneMove(lane, mid, true);
            _scriptedWindowPoint = to;
            LaneMove(lane, to, true);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }
        void Click(FrameworkElement lane, Point p, ModifierKeys mods = ModifierKeys.None, int clickCount = 1)
        {
            _scriptedModifiers = mods;
            _scriptedWindowPoint = p;
            WindowPress(MouseButton.Left, lane, clickCount);
            LaneDown(lane, p, clickCount);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }

        // An edge to snap to inside the long section, 15 s in.
        _vm.SelectedTrack = Song();
        _vm.SeekTo(p0 + 15);
        _vm.SplitAtPlayheadCommand.Execute(null);
        long edge = Benchmix.Core.TimelineEditor.StartFrameOf(Song().Clips.First(c => Math.Abs(c.TimelineStartSeconds - (p0 + 15)) < 0.01).Model, s);
        _vm.SeekTo(p0 + 32);                       // the playhead well away, so nothing snaps to it by accident

        // --- MARK MODE ---
        _vm.LoopMode = false;
        _vm.RampMode = false;
        _vm.WarpMode = false;
        _vm.MarkMode = false;
        Toggle();
        Check("MARK MODE turns on through its own toggle", _vm.MarkMode);
        _vm.LoopMode = true;
        bool offForLoop = !_vm.MarkMode;
        Toggle();
        bool loopOff = !_vm.LoopMode;
        _vm.RampMode = true;
        bool offForRamp = !_vm.MarkMode;
        Toggle();
        Check("mark mode is never on together with loop mode or ramp mode", offForLoop && loopOff && offForRamp && !_vm.RampMode && _vm.MarkMode);

        // --- a drag marks a stretch; sections stay put and are let go ---
        var placed = Song().Clips.Select(c => (c.Id, c.TimelineStartSeconds)).ToList();
        _vm.SelectClip(Song().Clips.First());
        Drag(Lane(), At(Lane(), p0 + 3), At(Lane(), p0 + 9), ModifierKeys.Control);
        var m = Song().Mark;
        Check($"a drag across the lane marks that stretch of the track, where the pointer went (\"{_vm.Status}\")",
              m is { } m1 && Near(m1.Start, F(Song(), p0 + 3)) && Near(m1.End, F(Song(), p0 + 9)) && _vm.Status.StartsWith("Marked"));
        Check("in mark mode a drag moves no section, and the selected sections are let go",
              Song().Clips.Select(c => (c.Id, c.TimelineStartSeconds)).SequenceEqual(placed) && _vm.SelectedClips.Count == 0);
        var wave = Descendants<Controls.WaveformView>(Lane()).FirstOrDefault();
        Check("the lane is handed the mark to draw",
              wave?.Mark is { } drawn && Math.Abs(drawn.StartSeconds - (p0 + 3)) < 0.001 && Math.Abs(drawn.EndSeconds - (p0 + 9)) < 0.001);

        // --- snapping: a new mark ending 4 px past a section's edge ends exactly on it ---
        var edgeAt = At(Lane(), Benchmix.Core.TimelineEditor.TimelineSecondsOfFrame(Song().Model, edge, s));
        Drag(Lane(), At(Lane(), p0 + 11), new Point(edgeAt.X + 4, edgeAt.Y), ModifierKeys.None);
        Check("a mark's end snaps onto the exact frame of a section's edge nearby", Song().Mark is { } m2 && m2.End == edge && Near(m2.Start, F(Song(), p0 + 11)));
        Drag(Lane(), At(Lane(), p0 + 11), new Point(edgeAt.X + 4, edgeAt.Y), ModifierKeys.Control);
        Check("Ctrl lets go of the edge", Song().Mark is { } m3 && Near(m3.End, F(Song(), (edgeAt.X + 4 - At(Lane(), _vm.ScrollSeconds).X) / pps + _vm.ScrollSeconds)) && m3.End != edge);

        // --- dragging an end of the mark moves that end only ---
        Drag(Lane(), At(Lane(), p0 + 3), At(Lane(), p0 + 9), ModifierKeys.Control);
        long keptStart = Song().Mark!.Value.Start;
        Drag(Lane(), At(Lane(), p0 + 9), At(Lane(), p0 + 13), ModifierKeys.Control);
        Check("dragging the end of the mark moves that end, and the start stays exactly where it was",
              Song().Mark is { } m4 && m4.Start == keptStart && Near(m4.End, F(Song(), p0 + 13)));

        // --- a click puts the playhead there and keeps the mark ---
        var markBefore = Song().Mark;
        Click(Lane(), At(Lane(), p0 + 20));
        Check($"a click in mark mode puts the playhead there and keeps the mark (playhead at {_vm.PositionSeconds:0.000}s)",
              Math.Abs(_vm.PositionSeconds - (p0 + 20)) < 0.001 && Song().Mark == markBefore && ReferenceEquals(_vm.SelectedTrack, Song()));

        // --- Ctrl+C across the edge at 15 s, then a click on an empty track and Ctrl+V ---
        Drag(Lane(), At(Lane(), p0 + 12), At(Lane(), p0 + 18), ModifierKeys.Control);
        var (ms, me) = Song().Mark!.Value;
        var marked = Benchmix.Core.TimelineEditor.CopyStretch(Song().Model, ms, me, s);
        PrepareLaneMenu(Lane());
        var headers = Lane().ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        Check("with a stretch marked, the lane menu's Cut, Copy, Repeat and Delete say they act on it",
              headers.Contains("Cut marked stretch") && headers.Contains("Copy marked stretch") && headers.Contains("Repeat marked stretch") && headers.Contains("Delete marked stretch"));
        _vm.CopyClipCommand.Execute(null);
        Check($"Ctrl+C copies the marked stretch, both sections of it (\"{_vm.Status}\")",
              _vm.HasClipboard && _vm.Status.StartsWith("Copied") && _vm.Status.Contains("(2 sections)") && marked.Clips.Count == 2);
        _vm.AddEmptyTrackCommand.Execute(null);
        var emptyId = _vm.Tracks[^1].Id;
        TrackViewModel Empty() => _vm.Tracks.First(t => t.Id == emptyId);
        UpdateLayout();
        PumpUntil(() => LaneOf(Empty()) is { ActualHeight: > 0 });
        var emptyLane = LaneOf(Empty());
        if (emptyLane is null) { notes.Add("the empty track was not laid out, so pasting a marked stretch is untested"); return; }
        Click(emptyLane, At(emptyLane, p0 + 5));
        _vm.PasteClipCommand.Execute(null);
        var landed = Empty().Model.Clips.OrderBy(c => c.TrackStartSeconds).ToList();
        long at = F(Empty(), _vm.PositionSeconds);
        Check($"Ctrl+V pastes the stretch where the click put the playhead, on that track, the seam inside it intact ({landed.Count} sections)",
              landed.Count == 2
              && Benchmix.Core.TimelineEditor.StartFrameOf(landed[0], s) == at
              && Benchmix.Core.TimelineEditor.StartFrameOf(landed[1], s) == at + Benchmix.Core.TimelineEditor.StartFrameOf(marked.Clips[1], s)
              && landed[0].SourceStartSeconds == marked.Clips[0].SourceStartSeconds
              && landed[0].SourceEndSeconds == landed[1].SourceStartSeconds
              && landed[1].SourceEndSeconds == marked.Clips[1].SourceEndSeconds);
        Check("after a paste in mark mode the mark is on what landed, and only there",
              Empty().Mark == (at, at + (me - ms)) && Song().Mark is null && _vm.SelectedClips.Count == 0);

        // --- Ctrl+X takes it straight back out, leaving the gap ---
        _vm.CutClipCommand.Execute(null);
        Check($"Ctrl+X cuts the marked stretch, leaving the gap (\"{_vm.Status}\")", Empty().Model.Clips.Count == 0 && _vm.Status.StartsWith("Cut"));
        Undo(() => Empty().Model.Clips.Count == 2);
        Check("one undo puts the cut back, and the mark stays where it was", Empty().Model.Clips.Count == 2 && Empty().Mark == (at, at + (me - ms)));

        // --- Shift+click marks from the playhead; Delete removes the stretch ---
        Click(Lane(), At(Lane(), p0 + 21));
        Click(Lane(), At(Lane(), p0 + 24), ModifierKeys.Shift);
        long from = F(Song(), _vm.PositionSeconds);
        Check($"Shift+click marks from the playhead to there (\"{_vm.Status}\")",
              Song().Mark is { } m5 && m5.Start == from && Near(m5.End, F(Song(), p0 + 24)) && Empty().Mark is null);

        // Held under the mouse, the edits wait.
        var hold = At(Lane(), p0 + 22);
        _scriptedWindowPoint = hold;
        LaneDown(Lane(), hold);
        int heldCount = Song().Model.Clips.Count;
        bool deleteWaited = !PressDelete();
        _vm.CutClipCommand.Execute(null);
        _vm.DuplicateClipCommand.Execute(null);
        bool waited = deleteWaited && Song().Model.Clips.Count == heldCount && Song().Mark is not null;
        LaneUp(Lane());
        _scriptedWindowPoint = null;
        Check("Delete, Ctrl+X and Ctrl+D wait while the mouse is held down on a lane", waited);
        Click(Lane(), At(Lane(), p0 + 21));
        Click(Lane(), At(Lane(), p0 + 24), ModifierKeys.Shift);
        var (ds, de) = Song().Mark!.Value;

        _scriptedFocus = this;
        var del = Press(Key.Delete);
        OnWindowPreviewKeyDown(this, del);
        _scriptedFocus = null;
        Check($"Delete removes what is in the marked stretch and nothing else (\"{_vm.Status}\")",
              del.Handled && !Song().Model.Clips.Any(c => Benchmix.Core.TimelineEditor.StartFrameOf(c, s) < de && Benchmix.Core.TimelineEditor.EndFrameOf(c, s) > ds)
              && Song().Model.Clips.Any(c => Benchmix.Core.TimelineEditor.EndFrameOf(c, s) == ds) && Song().Model.Clips.Any(c => Benchmix.Core.TimelineEditor.StartFrameOf(c, s) == de));

        // --- Ctrl+D repeats the stretch straight after itself, and the mark walks onto the repeat ---
        Drag(Lane(), At(Lane(), p0 + 26), At(Lane(), p0 + 28), ModifierKeys.Control);
        var (rs, re) = Song().Mark!.Value;
        _vm.DuplicateClipCommand.Execute(null);
        Check($"Ctrl+D repeats the marked stretch on the very frame it ends, and the mark moves onto the repeat (\"{_vm.Status}\")",
              Song().Mark == (re, re + (re - rs)) && Song().Model.Clips.Any(c => Benchmix.Core.TimelineEditor.StartFrameOf(c, s) == re));

        // --- a double-click plays from there ---
        var beforeDouble = Song().Mark;
        Click(Lane(), At(Lane(), p0 + 2));
        Click(Lane(), At(Lane(), p0 + 2), clickCount: 2);
        bool played = _vm.Session.Engine.State == Benchmix.Engine.Audio.TransportState.Playing && Math.Abs(_vm.PositionSeconds - (p0 + 2)) < 0.01;
        _vm.StopCommand.Execute(null);
        Check("a double-click in mark mode plays from there, and leaves the mark as it was", played && Song().Mark == beforeDouble);

        // --- a turn of a knob, a click in mark mode, another turn: two undo steps (review, 2026-10-12) ---
        var knobTrack = Song();
        double speedBefore = knobTrack.Speed;
        knobTrack.Speed = speedBefore + 0.02;
        Click(Lane(), At(Lane(), p0 + 3));
        knobTrack.Speed = speedBefore + 0.04;
        Undo(() => Song().Model.Speed != speedBefore + 0.04);
        Check($"a click in mark mode ends a knob's turn, so the next turn of it is its own undo step (after one undo {Song().Model.Speed:0.####}, want {speedBefore + 0.02:0.####})",
              Song().Model.Speed == speedBefore + 0.02);
        Undo(() => Song().Model.Speed == speedBefore);

        // --- losing the mouse during a press is not a click: the playhead stays ---
        double playheadBefore = _vm.PositionSeconds;
        var lostAt = At(Lane(), p0 + 25);
        _scriptedWindowPoint = lostAt;
        LaneDown(Lane(), lostAt);
        _scriptedWindowPoint = null;
        OnLaneLostCapture(Lane(), new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = LostMouseCaptureEvent });
        Check("a press in mark mode that loses the mouse (Alt+Tab, a pan) is not a click: the playhead stays where it was",
              _vm.PositionSeconds == playheadBefore && !_vm.PointerHeld);

        // --- a song file put on a track in mark mode gets the mark, as a pasted section does ---
        if (App.StartupFiles.FirstOrDefault() is { } file)
        {
            _vm.AddEmptyTrackCommand.Execute(null);
            var fileTrackId = _vm.Tracks[^1].Id;
            var fileTask = _vm.PlaceFilesAsync(_vm.Tracks[^1], new[] { file }, p0 + 1);
            PumpUntil(() => fileTask.IsCompleted, 20000);
            var fileTrack = _vm.Tracks.FirstOrDefault(t => t.Id == fileTrackId);
            var placedFile = fileTrack?.Model.Clips.FirstOrDefault();
            Check($"a song file put on a track in mark mode moves the mark onto it, so Delete or Ctrl+X acts on that (\"{_vm.Status}\")",
                  fileTrack is not null && placedFile is not null && Song().Mark is null && _vm.SelectedClips.Count == 0
                  && fileTrack.Mark == (Benchmix.Core.TimelineEditor.StartFrameOf(placedFile, s), Benchmix.Core.TimelineEditor.EndFrameOf(placedFile, s)));
        }
        else notes.Add("no song on the command line, so a file put on a track in mark mode is untested");

        // --- MARK MODE off takes the mark away; Ctrl+C is about sections again ---
        Toggle();
        Check("MARK MODE turns off through its toggle and the mark goes with it", !_vm.MarkMode && _vm.MarkedTrack is null && Song().MarkVisual is null);
        PrepareLaneMenu(Lane());
        Check("without a mark the lane menu's items are about sections again",
              Lane().ContextMenu!.Items.OfType<MenuItem>().Any(i => i.Header as string == "Cut section"));

        // Put everything back: the repeat, the delete, the paste, the empty track, the split.
        for (int i = 0; i < 10 && (_vm.Tracks.Count != tracks || Song().Clips.Count != sections); i++)
        {
            var state = (_vm.Tracks.Count, _vm.Tracks.Sum(t => t.Clips.Count));
            Undo(() => (_vm.Tracks.Count, _vm.Tracks.Sum(t => t.Clips.Count)) != state);
        }
        if (_vm.Tracks.Count != tracks || Song().Clips.Count != sections) notes.Add("the mark checks could not undo back to where they started");
    }

    /// <summary>
    /// Warp markers (Daniel, 2026-10-18) through the lane's own handlers, with a scripted pointer and
    /// modifiers: WARP MODE, a click that pins a beat and changes nothing, a press-drag that pins one
    /// and moves it, snapping and Ctrl, how far the audio can be spread, the right-click menu, and one
    /// undo step for each. Where a beat is heard is checked against the warp written out here — the
    /// tempo, v0 × (t − origin), carried from marker to marker — never asked of the map. Leaves the
    /// project as it found it, with no warp markers, for the checks after it.
    /// </summary>
    private void RunWarpGestures(Action<string, bool> Check, List<string> notes)
    {
        bool Bound(Key key, ICommand command) => InputBindings.OfType<KeyBinding>()
            .Any(b => b.Key == key && b.Modifiers == ModifierKeys.Control && ReferenceEquals(b.Command, command));
        Check("Ctrl+W is bound to warp mode", Bound(Key.W, _vm.ToggleWarpModeCommand));

        var toggle = Descendants<System.Windows.Controls.Primitives.ToggleButton>(this).FirstOrDefault(b => b.Content as string == "WARP MODE");
        UpdateLayout();
        var first = _vm.Tracks.FirstOrDefault(t => t.IsLoaded && t.Model.Ramps.Count == 0 && t.Model.Warps.Count == 0 && t.Clips.Any(c => c.TimelineLengthSeconds > 30));
        if (toggle is null || first is null || LaneOf(first) is not { ActualHeight: > 0 } lane0)
        {
            notes.Add("no WARP MODE toggle, or no laid-out track with a section over 30 s and no tempo ramp, so warp markers are untested in the window - pass a file to cover them");
            return;
        }
        void Toggle()
        {
            bool was = _vm.WarpMode;
            var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
            ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            PumpUntil(() => _vm.WarpMode != was);
        }

        var id = first.Id;
        var s = _vm.Session;
        TrackViewModel Song() => _vm.Tracks.First(t => t.Id == id);
        FrameworkElement Lane() => LaneOf(Song())!;
        double frame = 1.0 / s.SampleRate;
        double OnFrame(double sec) => Benchmix.Core.TimelineEditor.SecondsOf(s.SecondsToFrames(sec), s);
        // Where marker i's beat is heard, as the app works it out; NaN — failing whatever compares it — if it is not there.
        double Heard(int i) => i < Song().Model.Warps.Count ? Benchmix.Core.WarpMarkers.TimelineSecondsOf(Song().Model, i) : double.NaN;
        var none = new Benchmix.Core.Model.WarpMarker(double.NaN, double.NaN);
        Benchmix.Core.Model.WarpMarker Marker(int i) => i < Song().Model.Warps.Count ? Song().Model.Warps[i] : none;
        void Undo(Func<bool> done) { _vm.UndoCommand.Execute(null); PumpUntil(done); UpdateLayout(); }
        (int, int, int) State() => (_vm.Tracks.Count, _vm.Tracks.Sum(t => t.Clips.Count), Song().Model.Warps.Count);
        var start = State();                // no warp marker: the track was picked for that
        void PutBack()
        {
            for (int i = 0; i < 12 && State() != start; i++)
            {
                var state = State();
                Undo(() => State() != state);
            }
            if (State() != start) notes.Add("the warp checks could not undo back to where they started");
        }

        var piece = Song().Clips.First(c => c.TimelineLengthSeconds > 30);
        double p0 = piece.TimelineStartSeconds;
        _vm.ScrollSeconds = p0;
        _vm.PixelsPerSecond = Math.Min(20, Math.Max(4, (lane0.ActualWidth - 120) / 35));
        double pps = _vm.PixelsPerSecond;
        UpdateLayout();
        Point At(FrameworkElement lane, double sec)
        {
            var o = lane.TranslatePoint(new Point(0, 0), this);
            return new Point(o.X + (sec - _vm.ScrollSeconds) * pps, o.Y + lane.ActualHeight / 2);
        }
        // A press, two moves and a release, as WPF routes them. Returns what the status bar and the
        // guide line said at the last move, before the release settles them.
        (string Status, double? Guide) Drag(FrameworkElement lane, Point from, Point to, ModifierKeys mods, Action? midway = null)
        {
            _scriptedModifiers = mods;
            _scriptedWindowPoint = from;
            WindowPress(MouseButton.Left, lane);
            LaneDown(lane, from);
            var mid = new Point((from.X + to.X) / 2, from.Y);
            _scriptedWindowPoint = mid;
            LaneMove(lane, mid, true);
            midway?.Invoke();
            _scriptedWindowPoint = to;
            LaneMove(lane, to, true);
            var seen = (_vm.Status, _vm.SnapLineSeconds);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
            return seen;
        }
        void Click(FrameworkElement lane, Point p, ModifierKeys mods = ModifierKeys.None, int clickCount = 1)
        {
            _scriptedModifiers = mods;
            _scriptedWindowPoint = p;
            WindowPress(MouseButton.Left, lane, clickCount);
            LaneDown(lane, p, clickCount);
            LaneUp(lane);
            _scriptedWindowPoint = null;
            _scriptedModifiers = null;
        }
        List<FrameworkElement> RemoveItems(FrameworkElement lane)
            => lane.ContextMenu?.Items.OfType<FrameworkElement>().Where(i => i.Tag as string == "removeWarp").ToList() ?? new();

        // A section after the second marker, which the warp moves; and on another track, a section
        // starting 20 s in, for a marker to snap to. The playhead well away, so nothing snaps to it.
        _vm.LoopMode = false;
        _vm.RampMode = false;
        _vm.MarkMode = false;
        _vm.WarpMode = false;
        _vm.SelectedTrack = Song();
        _vm.SeekTo(p0 + 25);
        _vm.SplitAtPlayheadCommand.Execute(null);
        var later = Song().Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - (p0 + 25)) < 0.01);
        var head = Song().Clips.FirstOrDefault(c => Math.Abs(c.TimelineStartSeconds - p0) < 0.01);
        _vm.SelectClip(head);
        _vm.CopyClipCommand.Execute(null);
        _vm.AddEmptyTrackCommand.Execute(null);
        var otherId = _vm.Tracks[^1].Id;
        TrackViewModel? Other() => _vm.Tracks.FirstOrDefault(t => t.Id == otherId);
        _vm.SelectedTrack = Other();
        _vm.SeekTo(p0 + 20);
        _vm.PasteClipCommand.Execute(null);
        PumpUntil(() => Other()?.Model.Clips.Count > 0);
        var pasted = Other()?.Model.Clips.OrderBy(c => c.TrackStartSeconds).FirstOrDefault();
        _vm.SeekTo(p0 + 32);
        _vm.SelectedTrack = Song();
        UpdateLayout();
        if (later is null || head is null || pasted is null)
        {
            notes.Add("the song could not be split at 25 s, or nothing landed on the other track, so warp markers are untested in the window");
            PutBack();
            return;
        }
        double edge = Benchmix.Core.TimelineEditor.TimelineSecondsOfFrame(Other()!.Model, Benchmix.Core.TimelineEditor.StartFrameOf(pasted, s), s);

        // --- WARP MODE ---
        Toggle();
        Check("WARP MODE turns on through its own toggle", _vm.WarpMode);
        _vm.LoopMode = true;
        bool offForLoop = !_vm.WarpMode;
        Toggle();
        bool loopOff = !_vm.LoopMode;
        _vm.RampMode = true;
        bool offForRamp = !_vm.WarpMode;
        Toggle();
        bool rampOff = !_vm.RampMode;
        _vm.MarkMode = true;
        bool offForMark = !_vm.WarpMode;
        Toggle();
        Check("warp mode is never on together with loop mode, ramp mode or mark mode",
              offForLoop && loopOff && offForRamp && rampOff && offForMark && !_vm.MarkMode && _vm.WarpMode);

        // --- a click pins the beat under it, and changes nothing ---
        double o = Song().Model.TimelineOffsetSeconds, v0 = Song().Model.Speed;
        double laterBefore = later.TimelineStartSeconds, laterAudio = later.TrackStartSeconds;
        var placed = Song().Clips.Select(c => (c.Id, c.TimelineStartSeconds)).ToList();
        var selectedBefore = _vm.SelectedClips.Select(c => c.Id).ToHashSet();
        Click(Lane(), At(Lane(), p0 + 5));
        var m = Song().Model;
        var voice = s.VoiceFor(m);
        Check($"a click in warp mode pins the beat there, on its frame, and changes nothing: no section moves or is selected, and the engine still plays the track untouched (\"{_vm.Status}\")",
              m.Warps.Count == 1 && m.Warps[0].TrackSeconds == m.Warps[0].TempoSeconds
              && m.Warps[0].TrackSeconds == OnFrame(m.Warps[0].TrackSeconds) && Math.Abs(m.Warps[0].TrackSeconds - (p0 + 5 - o) * v0) <= frame
              && Song().Clips.Select(c => (c.Id, c.TimelineStartSeconds)).SequenceEqual(placed)
              && _vm.SelectedClips.Select(c => c.Id).ToHashSet().SetEquals(selectedBefore)
              && voice is not null && voice.Tempo.IsConstant && !_vm.PointerHeld && _vm.Status.Contains("warp marker at"));
        var a = Marker(0);
        double heardA = Heard(0);

        Click(Lane(), At(Lane(), p0 + 5), clickCount: 2);
        Check("the second press of a double-click in warp mode does nothing: no second marker, no drag, no play",
              Song().Model.Warps.Count == 1 && !_vm.PointerHeld && !_vm.DragInProgress
              && _vm.Session.Engine.State != Benchmix.Engine.Audio.TransportState.Playing);

        Click(Lane(), At(Lane(), p0 + 8), ModifierKeys.Alt);
        Check($"Alt+click in warp mode only moves the playhead (to {_vm.PositionSeconds:0.000}s)",
              Math.Abs(_vm.PositionSeconds - (p0 + 8)) < 0.001 && Song().Model.Warps.Count == 1 && Marker(0) == a);
        _vm.SeekTo(p0 + 32);

        // --- a press-drag pins a second beat and moves it 1.5 s later, freely (Ctrl) ---
        var pressB = At(Lane(), p0 + 15);
        var dropAt = At(Lane(), p0 + 16.5);
        bool waited = false;
        Drag(Lane(), pressB, dropAt, ModifierKeys.Control, () =>
        {
            _vm.ToggleWarpModeCommand.Execute(null);
            _vm.ToggleRampModeCommand.Execute(null);
            _vm.ToggleLoopModeCommand.Execute(null);
            _vm.ToggleMarkModeCommand.Execute(null);
            waited = _vm.WarpMode && !_vm.RampMode && !_vm.LoopMode && !_vm.MarkMode && Song().Model.Warps.Count == 2;
        });
        Check("Ctrl+W, Ctrl+R, Ctrl+L and Ctrl+M wait while a warp marker is held", waited);

        m = Song().Model;
        var b = Marker(1);
        // Where B's beat was heard when it went down — the track was untouched, so at its audio over
        // the tempo — and where the pointer let go of it. Then the warp written out: B's audio is
        // heard when the tempo, v0 × (t − origin), reaches B's TempoSeconds, which the drop sets to
        // the tempo on the frame it was let go on.
        double grabB = o + b.TrackSeconds / v0;
        double drop = grabB + (dropAt.X - pressB.X) / pps;
        double tbDrop = (OnFrame(drop) - o) * v0;
        double heardB = Heard(1);
        Check($"a press-drag in warp mode pins the beat under the press and moves it to where it was let go ({drop:0.000}s; heard at {heardB:0.000}s)",
              m.Warps.Count == 2 && Math.Abs(b.TrackSeconds - (p0 + 15 - o) * v0) <= frame && Math.Abs(b.TempoSeconds - tbDrop) <= 1e-9
              && Math.Abs(o + b.TempoSeconds / v0 - drop) <= frame && Math.Abs(heardB - drop) <= frame);
        Check("the marker before it stays exactly where it was, on the same beat",
              Marker(0) == a && Math.Abs(Heard(0) - heardA) <= frame);

        // After the last marker the audio plays at its tempo again: a section there is heard where
        // the tempo reaches B's, plus how far its audio is past B's.
        var laterNow = Song().Clips.FirstOrDefault(c => c.Id == later.Id);
        double wantLater = o + (b.TempoSeconds + (laterAudio - b.TrackSeconds)) / v0;
        Check($"a section after the last marker moves with it, by the same {heardB - grabB:0.000}s, where the warp puts it",
              laterNow is not null && Math.Abs(laterNow.TimelineStartSeconds - wantLater) <= frame
              && Math.Abs((laterNow.TimelineStartSeconds - laterBefore) - (heardB - grabB)) <= frame);

        voice = s.VoiceFor(m);
        Check("the engine plays the warp the project describes, through the stretcher",
              voice is not null && voice.Tempo.IsWarped && !voice.Tempo.IsConstant && voice.Tempo.SameAs(s.EngineTempoOf(m)));

        // The audio between the two markers against its tempo: under 1, it plays slower.
        double spread = (b.TrackSeconds - a.TrackSeconds) / (b.TempoSeconds - a.TempoSeconds);
        string label = $"{(spread > 1 ? "+" : "−")}{Math.Abs(spread - 1) * 100:0.0}%";
        var wave = Descendants<Controls.WaveformView>(Lane()).FirstOrDefault();
        Check($"the lane is handed both markers to draw, the stretch between them labelled {label}",
              wave?.Warps is { Count: 2 } drawn && drawn[0].Label is null && drawn[1].Label == label
              && drawn[1].FromSeconds == drawn[0].Seconds && Math.Abs(drawn[1].Seconds - drop) <= frame);

        // --- snapping: dragged to 4 px past an edge on another track, the beat lands on the edge ---
        var onB = At(Lane(), heardB);
        var pastEdge = new Point(At(Lane(), edge).X + 4, onB.Y);
        var (snapStatus, guide) = Drag(Lane(), onB, pastEdge, ModifierKeys.None);
        double tbSnap = Marker(1).TempoSeconds;
        Check($"without Ctrl a beat snaps onto the edge of a section on another track 4 px away, and the guide line shows it (\"{snapStatus}\")",
              guide == edge && Math.Abs(Heard(1) - edge) <= frame && Math.Abs(o + tbSnap / v0 - edge) <= frame);

        double grabSnap = Heard(1);
        var onSnap = At(Lane(), grabSnap);
        var (_, freeGuide) = Drag(Lane(), onSnap, pastEdge, ModifierKeys.Control);
        double dropFree = grabSnap + (pastEdge.X - onSnap.X) / pps;
        double tbFree = Marker(1).TempoSeconds;
        Check("Ctrl lets go of the edge: the beat goes exactly where the pointer does",
              freeGuide is null && Math.Abs(Heard(1) - dropFree) <= frame && Math.Abs(o + tbFree / v0 - dropFree) <= frame && Math.Abs(Heard(1) - edge) > 2 * frame);

        // --- the audio can be spread to twice as fast, and no further ---
        var onFree = At(Lane(), Heard(1));
        var (limitStatus, _) = Drag(Lane(), onFree, At(Lane(), heardA + 1), ModifierKeys.Control);
        // Twice as fast is as far as it goes: B's tempo then comes after A's by half B's audio's distance from A's.
        double tbLimit = a.TempoSeconds + (Marker(1).TrackSeconds - a.TrackSeconds) / 2;
        double tbLimited = Marker(1).TempoSeconds;
        Check($"a beat dragged further than the audio can be spread stops where it plays twice as fast, and says so (\"{limitStatus}\")",
              limitStatus.Contains("as far as it goes") && Math.Abs(tbLimited - tbLimit) <= 1e-6 && Math.Abs(Heard(1) - (o + tbLimit / v0)) <= frame);

        // --- a tempo ramp left at the tempo its box was filled in with, inside the stretch the warp now
        // spreads, through the real box: it starts from the track's tempo, not the warp, so it ends
        // where it starts and moves nothing (review, 2026-10-18) ---
        if (Song().Model.Bpm > 0)
        {
            var model = Song().Model;
            double r0 = Heard(0) + (Heard(1) - Heard(0)) * 0.25, r1 = Heard(0) + (Heard(1) - Heard(0)) * 0.75;
            var sectionsBefore = Song().Clips.ToDictionary(c => c.Id, c => c.TimelineStartSeconds);
            double beat0 = Heard(0), beat1 = Heard(1);
            _vm.RampMode = true;
            bool rampPlaced = _vm.PlaceRampPoint(Song(), r0, free: true) && _vm.PlaceRampPoint(Song(), r1, free: true);
            bool committed = rampPlaced && _vm.CommitRampEntry(Song().Id, Song().RampBpmDraft);
            bool flat = committed && model.Ramps is [{ } ramp] && ramp.ToSpeed == Benchmix.Core.TimelineEditor.TempoSpeedAt(model, ramp.StartSeconds);
            bool stayed = Song().Clips.All(c => sectionsBefore.TryGetValue(c.Id, out double was) && Math.Abs(c.TimelineStartSeconds - was) <= 1e-9)
                          && Math.Abs(Heard(0) - beat0) <= 1e-9 && Math.Abs(Heard(1) - beat1) <= 1e-9;
            Undo(() => Song().Model.Ramps.Count == 0);
            _vm.WarpMode = true;
            Check("a tempo ramp left at the tempo its box was filled in with, inside a stretch the warp spreads, ends at the tempo it starts at and moves nothing",
                  flat && stayed && Song().Model.Ramps.Count == 0 && Song().Model.Warps.Count == 2 && _vm.WarpMode);
        }
        else notes.Add("the song's tempo is unknown, so a tempo ramp on a warped stretch is untested in the window");

        // --- losing the mouse during a press lets it go, and changes nothing ---
        var pressA = At(Lane(), Heard(0));
        _scriptedWindowPoint = pressA;
        LaneDown(Lane(), pressA);
        bool heldA = _vm.PointerHeld;
        _scriptedWindowPoint = null;
        OnLaneLostCapture(Lane(), new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = LostMouseCaptureEvent });
        Check("a press on a warp marker that loses the mouse (Alt+Tab, a pan) is let go, and changes nothing",
              heldA && !_vm.PointerHeld && !_vm.DragInProgress && Song().Model.Warps.Count == 2
              && Marker(0) == a && Marker(1).TempoSeconds == tbLimited);

        // --- the right-click menu ---
        var lane = Lane();
        double between = (Heard(0) + Heard(1)) / 2;
        LaneRightDown(lane, At(lane, between));
        PrepareLaneMenu(lane);
        bool hiddenAway = RemoveItems(lane).All(i => i.Visibility == Visibility.Collapsed);
        LaneRightDown(lane, At(lane, Heard(1)));
        PrepareLaneMenu(lane);
        var items = RemoveItems(lane);
        Check("the right-click menu offers Remove warp marker over a marker, and not elsewhere",
              hiddenAway && items.Count == 2 && items.All(i => i.Visibility == Visibility.Visible));

        // A right-press while a marker is held does not look under the pointer, and must not leave
        // the marker from the right-click before it on offer, by an index that may now be another.
        _scriptedWindowPoint = pressA;
        LaneDown(lane, pressA);
        LaneRightDown(lane, At(lane, Heard(1)));
        PrepareLaneMenu(lane);
        bool staleHidden = RemoveItems(lane).All(i => i.Visibility == Visibility.Collapsed);
        LaneUp(lane);
        _scriptedWindowPoint = null;
        Check("a right-click while a warp marker is held offers no Remove warp marker, not even one left over from an earlier right-click",
              staleHidden && Song().Model.Warps.Count == 2);

        LaneRightDown(lane, At(lane, Heard(1)));
        PrepareLaneMenu(lane);
        if (RemoveItems(lane).OfType<MenuItem>().FirstOrDefault() is { } remove) OnRemoveWarpMenu(remove, new RoutedEventArgs());
        var back = Song().Clips.FirstOrDefault(c => c.Id == later.Id);
        voice = s.VoiceFor(Song().Model);
        Check($"Remove warp marker takes the last one off, and the section after it goes back exactly where it was (\"{_vm.Status}\")",
              Song().Model.Warps.Count == 1 && Marker(0) == a && back is not null && back.TimelineStartSeconds == laterBefore
              && voice is not null && voice.Tempo.IsConstant);

        // --- under the tracks: no selection box, and a double-click still plays ---
        _vm.StopCommand.Execute(null);
        var below = At(BelowTracks, p0 + 12);
        _scriptedWindowPoint = below;
        BelowDown(BelowTracks, below, 1);
        bool noBand = _band is null;
        if (_band is not null) BandLost();
        BelowDown(BelowTracks, below, 2);
        _scriptedWindowPoint = null;
        bool playedBelow = _vm.Session.Engine.State == Benchmix.Engine.Audio.TransportState.Playing && Math.Abs(_vm.PositionSeconds - (p0 + 12)) < 0.01;
        _vm.StopCommand.Execute(null);
        Check("under the tracks a press in warp mode starts no selection box, and a double-click still plays from there", noBand && playedBelow);

        // --- WARP MODE off: the markers are part of the project, and stay ---
        Toggle();
        Check("WARP MODE turns off through its toggle, and the warp marker stays",
              !_vm.WarpMode && Song().Model.Warps.Count == 1 && Song().WarpVisuals.Count == 1);

        // --- one undo step each ---
        bool UndoTo(Func<Benchmix.Core.Model.TrackModel, bool> state) { Undo(() => state(Song().Model)); return state(Song().Model); }
        bool u1 = UndoTo(x => x.Warps.Count == 2 && x.Warps[1].TempoSeconds == tbLimited);
        bool u2 = u1 && UndoTo(x => x.Warps.Count == 2 && x.Warps[1].TempoSeconds == tbFree);
        bool u3 = u2 && UndoTo(x => x.Warps.Count == 2 && x.Warps[1].TempoSeconds == tbSnap);
        bool u4 = u3 && UndoTo(x => x.Warps.Count == 2 && x.Warps[1].TempoSeconds == b.TempoSeconds);
        bool u5 = u4 && UndoTo(x => x.Warps.Count == 1);
        bool u6 = u5 && UndoTo(x => x.Warps.Count == 0);
        Check("each warp edit was one undo step: the removal, the limit, the free drag, the snap, then the press that put the second marker down and moved it, then the first",
              u6);

        // Put everything back: the paste, the empty track, the split.
        PutBack();
    }

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

        // --- no bar snapping (2026-10-11): away from every edge, a plain drag lands where it is let go ---
        double bpmBefore = _vm.ProjectBpm;
        _vm.ProjectBpm = 120;                         // a bar line every 2 s; the old snap would have pulled 49.47 to 50
        Drag(emptyLane, At(emptyLane, 50), At(emptyLane, 51.37));
        _vm.ProjectBpm = bpmBefore;
        moved = empty.Clips.First(c => c.Id == sectionId);
        Check($"a plain drag far from any section lands exactly where it was dropped, not on a bar line " +
              $"(at {moved.TimelineStartSeconds:0.00000}s, expected 49.47000)",
              Math.Abs(moved.TimelineStartSeconds - 49.47) <= frame);

        // --- the playhead (Daniel, 2026-10-27): parked at 58 s, well away from the hole's edges at
        // 48 and 52, a section dropped 4 px short of it lands on it; so does one whose end is 4 px
        // past it; Ctrl frees it; and while playing the playhead is moving, so it is not a target ---
        const double ph = 58;
        double len = moved.TimelineLengthSeconds;
        double? DragStartTo(double target)
            => Drag(emptyLane, At(emptyLane, moved.TimelineStartSeconds + 1), At(emptyLane, target + 1));
        _vm.SeekTo(ph);
        guide = DragStartTo(ph - 4 / pps);
        moved = empty.Clips.First(c => c.Id == sectionId);
        Check($"a section dropped 4 px short of the playhead lands on it (at {moved.TimelineStartSeconds:0.00000}s, playhead at {ph})",
              Math.Abs(moved.TimelineStartSeconds - ph) <= frame);
        Check($"the guide line marks the playhead ({guide:0.000})", guide is { } gp && Math.Abs(gp - ph) <= frame);
        Check($"the status bar says it snapped to the playhead (\"{_vm.Status}\")", _vm.Status.Contains("snapped to the playhead"));

        DragStartTo(ph - len + 4 / pps);                      // its end 4 px past the playhead
        moved = empty.Clips.First(c => c.Id == sectionId);
        Check($"a section whose end is dropped 4 px past the playhead ends on it (ends at {moved.TimelineEndSeconds:0.00000}s)",
              Math.Abs(moved.TimelineEndSeconds - ph) <= frame);

        _scriptedModifiers = ModifierKeys.Control;
        DragStartTo(ph - 4 / pps);
        _scriptedModifiers = null;
        moved = empty.Clips.First(c => c.Id == sectionId);
        Check($"with Ctrl held it stops 4 px short of the playhead (at {moved.TimelineStartSeconds:0.00000}s, want {ph - 4 / pps:0.00000})",
              Math.Abs(moved.TimelineStartSeconds - (ph - 4 / pps)) <= frame);

        _scriptedModifiers = ModifierKeys.Control;
        DragStartTo(ph - 3);                                  // away again, freely
        _scriptedModifiers = null;
        moved = empty.Clips.First(c => c.Id == sectionId);
        _vm.PlayPauseCommand.Execute(null);
        bool playing = _vm.IsPlaying;
        DragStartTo(ph - 4 / pps);
        moved = empty.Clips.First(c => c.Id == sectionId);
        _vm.StopCommand.Execute(null);
        Check($"while playing, the playhead is not a target: the section stops 4 px short of it (playing: {playing}; at {moved.TimelineStartSeconds:0.00000}s, want {ph - 4 / pps:0.00000})",
              playing && Math.Abs(moved.TimelineStartSeconds - (ph - 4 / pps)) <= frame);

        _scriptedModifiers = ModifierKeys.Control;            // back where the checks below expect it
        DragStartTo(49.47);
        _scriptedModifiers = null;
        moved = empty.Clips.First(c => c.Id == sectionId);
        Check($"put back at 49.47 for the loop checks (at {moved.TimelineStartSeconds:0.00000}s)", Math.Abs(moved.TimelineStartSeconds - 49.47) <= frame);

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

        // --- the crossfade X (Daniel, 2026-10-28): drawn at every crossfaded join, its length picked from the lane menu ---
        UpdateLayout();
        var loopLane = LaneOf(empty);
        var xfadeMenu = loopLane?.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "xfade");
        var fifty = xfadeMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "0.05");
        var standard = xfadeMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "0");
        Check("the lane menu offers 'Crossfade from the section before', with the 5 ms standard and 50 ms in it", fifty is not null && standard is not null);
        if (repeats.Count == 4 && loopLane is not null && fifty is not null && standard is not null)
        {
            static double XAt(TrackViewModel t, ClipViewModel c)
                => t.ClipVisuals.FirstOrDefault(v => Math.Abs(v.TimelineStartSeconds - c.TimelineStartSeconds) < 1e-9)?.CrossfadeSeconds ?? -1;
            Check($"the first repeat, with nothing before it, is drawn with no X ({XAt(empty, repeats[0]) * 1000:0.#} ms)", XAt(empty, repeats[0]) == 0);
            Check($"each later repeat's start is drawn with the standard 5 ms X ({XAt(empty, repeats[1]) * 1000:0.#} / {XAt(empty, repeats[2]) * 1000:0.#} / {XAt(empty, repeats[3]) * 1000:0.#} ms)",
                  Math.Abs(XAt(empty, repeats[1]) - 0.005) < 1e-9 && Math.Abs(XAt(empty, repeats[2]) - 0.005) < 1e-9 && Math.Abs(XAt(empty, repeats[3]) - 0.005) < 1e-9);

            _vm.SelectClip(repeats[1]);
            PrepareLaneMenu(loopLane);
            Check("with nothing picked, the menu ticks the 5 ms standard", standard.IsChecked && !fifty.IsChecked);

            ClickMenuItem(fifty);
            PumpUntil(() => repeats[1].Model.CrossfadeSeconds == 0.05);
            Check($"50 ms through the real menu item sets the second repeat's crossfade ({repeats[1].Model.CrossfadeSeconds * 1000:0.#} ms)", repeats[1].Model.CrossfadeSeconds == 0.05);
            var voiced = _vm.Session.VoiceFor(empty.Model)?.Timeline;
            Check($"the engine's section carries it ({(voiced is { Count: 4 } ? voiced[1].CrossfadeSeconds * 1000 : -1):0.#} ms)", voiced is { Count: 4 } && voiced[1].CrossfadeSeconds == 0.05);
            Check($"the lane draws that X 50 ms wide and the next one still 5 ms ({XAt(empty, repeats[1]) * 1000:0.#} / {XAt(empty, repeats[2]) * 1000:0.#} ms)",
                  Math.Abs(XAt(empty, repeats[1]) - 0.05) < 1e-9 && Math.Abs(XAt(empty, repeats[2]) - 0.005) < 1e-9);
            Check($"the status says so (\"{_vm.Status}\")", _vm.Status.Contains("50 ms") && _vm.Status.Contains("from the section before"));
            PrepareLaneMenu(loopLane);
            Check("the menu now ticks 50 ms", fifty.IsChecked && !standard.IsChecked);

            _vm.UndoCommand.Execute(null);
            PumpUntil(() => _vm.Tracks.FirstOrDefault(t => t.Id == emptyId)?.Clips.All(c => c.Model.CrossfadeSeconds == 0) == true);
            var emptyAgain = _vm.Tracks.First(t => t.Id == emptyId);
            var again = emptyAgain.Clips.OrderBy(c => c.TimelineStartSeconds).ToList();
            Check($"one Ctrl+Z puts the standard back ({(again.Count == 4 ? again[1].Model.CrossfadeSeconds * 1000 : -1):0.#} ms; {again.Count} sections)",
                  again.Count == 4 && again[1].Model.CrossfadeSeconds == 0 && Math.Abs(XAt(emptyAgain, again[1]) - 0.005) < 1e-9);

            // A section with nothing butted against it keeps the length for later, and the status says so.
            if (again.Count == 4)
            {
                _vm.SelectClip(again[0]);
                ClickMenuItem(fifty);
                PumpUntil(() => again[0].Model.CrossfadeSeconds == 0.05);
                Check($"on the first repeat, with nothing before it, 50 ms is kept but draws no X ({XAt(emptyAgain, again[0]) * 1000:0.#} ms; \"{_vm.Status}\")",
                      again[0].Model.CrossfadeSeconds == 0.05 && XAt(emptyAgain, again[0]) == 0 && _vm.Status.Contains("takes effect"));
                _vm.UndoCommand.Execute(null);
                PumpUntil(() => _vm.Tracks.FirstOrDefault(t => t.Id == emptyId)?.Clips.All(c => c.Model.CrossfadeSeconds == 0) == true);
            }
            empty = _vm.Tracks.First(t => t.Id == emptyId);
        }

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
        // Kept for the moved-project check at the end, after New has emptied the project.
        string bedFile = _vm.Tracks.Count > 0 ? _vm.Tracks[0].Model.SourcePath : "";
        string topperFile = _vm.Tracks.Count > 1 ? _vm.Tracks[1].Model.SourcePath : "";
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

        await CheckMatchAverageAsync(Check, Click, Buttons, Labels);
        await CheckSplitThroughMenuAsync(Check, Labels);
        await CheckRenameThroughMenuAsync(Check, Labels);
        await CheckTempoRampButtonsAsync(Check, Click, Buttons, Labels);
        await CheckWarpButtonsAsync(Check, Click, Buttons, Labels);
        await CheckSoloButtonsAsync(Check);
        await CheckRenderHeardAsync(Check);
        await CheckDuplicateThroughMenuAsync(Check);
        await CheckUndoResetAndNewAsync(Check, Click, Buttons);
        await CheckRelinkAsync(Check, bedFile, topperFile);

        foreach (var p in pass) App.Log("buttontest: PASS - " + p);
        foreach (var f in fail) App.Log("buttontest: FAIL - " + f);
        App.Log($"buttontest: verdict={(fail.Count == 0 ? $"PASS - {pass.Count} checks" : $"FAIL - {fail.Count} of {pass.Count + fail.Count}")}");
        return fail.Count == 0;
    }

    /// <summary>
    /// "Split into vocals + instrumental" pressed through the real right-click menu item, on the
    /// topper, and the result checked against the original.
    ///
    /// <para>
    /// Until 2026-10-03 this item did nothing at all. Its command was bound by looking up the
    /// window, and a right-click menu is not inside the window, so the binding came back empty and
    /// a click went nowhere, with nothing on screen to say so. Every check of separation went
    /// straight to the engine and passed; none went through the menu, which is the only way in.
    /// </para>
    /// </summary>
    /// <summary>
    /// "Rename track" on a strip's right-click menu (Daniel, 2026-10-11), pressed through its own
    /// automation peer without opening the popup — an open popup would appear on his screen.
    /// </summary>
    private async Task CheckRenameThroughMenuAsync(Action<string, bool> check, Func<string> labels)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        await Settle();

        var topper = _vm.Tracks.Count == 2 ? _vm.Tracks[1] : null;
        var item = topper is null ? null : StripOf(topper)?.ContextMenu?.Items.OfType<MenuItem>()
                                               .FirstOrDefault(m => m.Header as string == "Rename track");
        check("the track strip's right-click menu offers 'Rename track'", item is not null);
        if (topper is null || item is null) return;

        // What the right-click itself does before the menu opens: select the track under it. The
        // bed is selected first, so renaming the wrong track cannot pass.
        _vm.SelectedTrack = _vm.Tracks[0];
        if (StripOf(topper) is { } strip)
            OnTrackHeaderRightClicked(strip, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                                             { RoutedEvent = PreviewMouseRightButtonDownEvent });
        check("a right-click on a track's strip selects that track, so its menu acts on it", ReferenceEquals(_vm.SelectedTrack, topper));

        var id = topper.Id;
        string old = topper.Name, renamed = old + " (renamed)";
        try
        {
            var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        catch (Exception ex)
        {
            check($"the menu item can be clicked ({ex.GetType().Name}: {ex.Message})", false);
            return;
        }
        await Settle();

        var box = RenameBoxOf(topper);
        check($"clicking it opens that track's name for typing, filled in with the current name ('{box?.Text}'; status '{_vm.Status}')",
              topper.IsRenaming && box is { Visibility: Visibility.Visible } && box.Text == old);
        if (box is null || PresentationSource.FromVisual(this) is not { } source) return;

        box.Text = renamed;
        OnRenameKeyDown(box, new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = PreviewKeyDownEvent });
        await Settle();
        check($"typing a name and pressing Enter renames it on screen and in the project ('{topper.Name}')",
              topper.Name == renamed && topper.Model.Name == renamed && labels().Contains(renamed));

        _vm.UndoCommand.Execute(null);
        await Settle();
        string? back = _vm.Tracks.FirstOrDefault(t => t.Id == id)?.Name;
        check($"one Ctrl+Z puts the old name back ('{back}')", back == old);
    }

    /// <summary>
    /// Match BPM's right-click (Daniel, 2026-10-28: "matches all selected songs to the average bpm
    /// between them all") and Match key with several tracks selected, through the real strip press,
    /// the real menu item and the real button: the bed at 93 and the topper at 102 both land on
    /// 97.5, PROJECT BPM follows, one Ctrl+Z puts all three back; then, the topper's key read as Bm
    /// for the test, Match key on the bed shifts the topper −3 and leaves the bed alone.
    /// </summary>
    private async Task CheckMatchAverageAsync(Action<string, bool> check, Func<Button, Task> click,
                                              Func<string, List<Button>> buttons, Func<string> labels)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        if (_vm.Tracks.Count != 2) { check("two tracks to average", false); return; }
        var bedId = _vm.Tracks[0].Id;
        var topperId = _vm.Tracks[1].Id;
        TrackViewModel Bed() => _vm.Tracks.First(t => t.Id == bedId);
        TrackViewModel Topper() => _vm.Tracks.First(t => t.Id == topperId);
        double projectBefore = _vm.ProjectBpm;

        _vm.SelectedTrack = Bed();
        if (StripOf(Topper()) is not { } strip) { check("the topper's strip is on screen", false); return; }
        _scriptedModifiers = ModifierKeys.Control;
        OnTrackHeaderClicked(strip, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent });
        _scriptedModifiers = null;
        check($"Ctrl+click on the topper's strip selects both tracks ({_vm.SelectedTracks.Count})", _vm.SelectedTracks.Count == 2);

        var match = buttons("Match BPM");
        var menu = match.Count == 2 ? match[0].ContextMenu : null;
        var item = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "average");
        check("the bed's Match BPM has the right-click item", item is not null);
        if (menu is null || item is null) return;
        FillMatchAverageMenu(menu);
        check($"the item names the two tracks and their average ('{item.Header}')",
              item.IsEnabled && item.Header is string h && h.Contains("the 2 selected tracks") && h.Contains("97.5 BPM"));

        try
        {
            var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        catch (Exception ex) { check($"the item can be clicked ({ex.GetType().Name}: {ex.Message})", false); return; }
        await Settle();

        var after = labels();
        check("the bed is stretched up to 97.5", System.Text.RegularExpressions.Regex.IsMatch(after, @"97\.5 BPM\s+\(was 93\.0\)"));
        check("the topper is stretched down to 97.5", System.Text.RegularExpressions.Regex.IsMatch(after, @"97\.5 BPM\s+\(was 102\.0\)"));
        check($"PROJECT BPM is now the average ({_vm.ProjectBpm})", Math.Abs(_vm.ProjectBpm - 97.5) < 1e-9);
        check($"the status says what it did (\"{_vm.Status}\")",
              _vm.Status.Contains("97.5 BPM") && _vm.Status.Contains("+4.8%") && _vm.Status.Contains("-4.4%") && _vm.Status.Contains("PROJECT BPM is now 97.5"));

        _vm.UndoCommand.Execute(null);
        await Settle();
        after = labels();
        check("one Ctrl+Z puts both tempos back",
              System.Text.RegularExpressions.Regex.IsMatch(after, @"93\.0 BPM") && System.Text.RegularExpressions.Regex.IsMatch(after, @"102\.0 BPM") && !after.Contains("(was"));
        check($"and PROJECT BPM back ({_vm.ProjectBpm}, was {projectBefore})", Math.Abs(_vm.ProjectBpm - projectBefore) < 1e-9);
        check($"the selection survives the undo ({_vm.SelectedTracks.Count})", _vm.SelectedTracks.Count == 2);

        // Match key between the two: the bed's button is pressed, so the bed stays and the topper
        // shifts. Both songs are tagged G#m, so the topper's key is read as Bm for this — three
        // semitones up — and put back after.
        string? wasKey = Topper().Model.Key;
        Topper().Model.Key = "Bm";
        if (_vm.SelectedTracks.Count != 2) { _vm.SelectedTrack = Bed(); _vm.ToggleTrackSelection(Topper()); }
        var key = buttons("Match key");
        if (key.Count == 2)
        {
            await click(key[0]);
            await Settle();
            check($"Match key on the bed with both selected shifts the topper, not the bed (topper {Topper().Semitones:+0;-0;0}, bed {Bed().Semitones:+0;-0;0})",
                  Math.Abs(Topper().Semitones - (-3)) < 1e-9 && Math.Abs(Bed().Semitones) < 1e-9);
            check($"the status says so (\"{_vm.Status}\")", _vm.Status.Contains("sit with") && _vm.Status.Contains("-3"));
            _vm.UndoCommand.Execute(null);
            await Settle();
            check($"one Ctrl+Z puts the topper back ({Topper().Semitones:+0;-0;0})", Math.Abs(Topper().Semitones) < 1e-9);
        }
        else check("two Match key buttons", false);
        Topper().Model.Key = wasKey;

        _vm.SelectedTrack = Bed();
        check($"a plain selection lets the topper go ({_vm.SelectedTracks.Count})", _vm.SelectedTracks.Count == 1);
    }

    private async Task CheckSplitThroughMenuAsync(Action<string, bool> check, Func<string> labels)
    {
        var topper = _vm.Tracks.Count == 2 ? _vm.Tracks[1] : null;
        var lane = topper is null ? null : LaneOf(topper);
        var item = lane?.ContextMenu?.Items.OfType<MenuItem>()
                       .FirstOrDefault(m => m.Header as string == "Split into vocals + instrumental");
        check("the lane's right-click menu offers 'Split into vocals + instrumental'", item is not null);
        if (topper is null || item is null) return;

        // What the right-click itself does before the menu opens. The bed is selected first so a
        // split of the wrong track cannot pass.
        _vm.SelectedTrack = _vm.Tracks[0];
        _vm.SelectedTrack = topper;
        var original = topper.Model;
        var sections = original.Clips.Select(c => (c.TrackStartSeconds, c.SourceStartSeconds, c.SourceEndSeconds)).ToList();

        try
        {
            var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        catch (Exception ex)
        {
            check($"the menu item can be clicked ({ex.GetType().Name}: {ex.Message})", false);
            return;
        }

        // The peer queues the click. A split that started shows within a moment; one that never
        // starts must fail now rather than after the long wait for one that did.
        var deadline = Environment.TickCount64 + 3000;
        while (!_vm.IsBusy && _vm.Tracks.Count == 2 && Environment.TickCount64 < deadline)
            await Task.Delay(50);
        bool started = _vm.IsBusy || _vm.Tracks.Count > 2;
        check($"clicking it starts separating (status: '{_vm.Status}')", started);
        if (!started) return;

        deadline = Environment.TickCount64 + 180_000;
        while (_vm.IsBusy && Environment.TickCount64 < deadline) await Task.Delay(250);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        UpdateLayout();

        var vocals = _vm.Tracks.FirstOrDefault(t => t.Name.EndsWith("— vocals"));
        var instrumental = _vm.Tracks.FirstOrDefault(t => t.Name.EndsWith("— instrumental"));
        check($"it finishes with a vocals track and an instrumental track ({_vm.Tracks.Count} tracks; status: '{_vm.Status}')",
              _vm.Tracks.Count == 4 && vocals is not null && instrumental is not null);
        check("the status bar reports the split", labels().Contains($"Split {topper.Name} in "));
        check("the original is muted, not removed", _vm.Tracks.Contains(topper) && topper.Muted);
        if (vocals is null || instrumental is null) return;

        // Measured against the original, not as a number on its own: a separation that ran but
        // put nothing in the vocal stem would still leave two tracks behind.
        static double Db(float[]? x)
        {
            if (x is null || x.Length == 0) return -200;
            double s = 0;
            foreach (var v in x) s += v * (double)v;
            double r = Math.Sqrt(s / x.Length);
            return r <= 1e-12 ? -200 : 20 * Math.Log10(r);
        }
        double mixDb = Db(_vm.Session.ClipFor(original)?.Samples);
        double vocalDb = Db(_vm.Session.ClipFor(vocals.Model)?.Samples);
        double instDb = Db(_vm.Session.ClipFor(instrumental.Model)?.Samples);
        App.Log($"buttontest: split levels mix={mixDb:F1}dB vocals={vocalDb:F1}dB instrumental={instDb:F1}dB");
        check($"the vocal stem carries the voice: {vocalDb - mixDb:F1} dB against the original (a song with no voice measured -44)",
              vocalDb - mixDb > -20);
        check($"the instrumental is not silent: {instDb - mixDb:F1} dB against the original", instDb - mixDb > -20);

        // Section for section, frame for frame: the stems must land exactly on the original, or
        // they play out of time with it and with everything matched to it.
        foreach (var stem in new[] { vocals, instrumental })
        {
            var got = stem.Model.Clips.Select(c => (c.TrackStartSeconds, c.SourceStartSeconds, c.SourceEndSeconds)).ToList();
            check($"'{stem.Name}' lands exactly on the original's sections", got.SequenceEqual(sections)
                  && stem.Model.TimelineOffsetSeconds == original.TimelineOffsetSeconds);
        }

        _vm.UndoCommand.Execute(null);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var back = _vm.Tracks.FirstOrDefault(t => t.Model.Id == original.Id);
        check($"one Ctrl+Z undoes the whole split ({_vm.Tracks.Count} tracks, original muted={back?.Muted})",
              _vm.Tracks.Count == 2 && back is { Muted: false });
    }

    /// <summary>
    /// Undo, Reset and New through the real commands and the real Reset button. Each check is one
    /// of the defects found on 2026-10-03 while the user guide was being checked against the app,
    /// and fails without its fix.
    /// </summary>
    /// <summary>
    /// Match BPM and Reset on a track with a tempo ramp (2026-10-11), through the real buttons: Match
    /// BPM sets the tempo before the ramp and leaves where the ramp goes; Reset takes the ramp away
    /// in the same step; and each of those, and the ramp itself, is one Ctrl+Z.
    /// </summary>
    private async Task CheckTempoRampButtonsAsync(Action<string, bool> check, Func<Button, Task> click,
                                                  Func<string, List<Button>> buttons, Func<string> labels)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        async Task Undo() { _vm.UndoCommand.Execute(null); await Settle(); }

        if (_vm.Tracks.Count != 2 || _vm.Tracks[1].Model.Bpm <= 0) { check("a topper with a known tempo to put a tempo ramp on", false); return; }
        var id = _vm.Tracks[1].Id;
        TrackViewModel Topper() => _vm.Tracks.First(t => t.Id == id);
        double bpm = Topper().Model.Bpm, speedBefore = Topper().Model.Speed;

        _vm.PlaceRampPoint(Topper(), 20, free: true);
        _vm.PlaceRampPoint(Topper(), 30, free: true);
        bool made = _vm.CommitRampEntry(id, "100");
        await Settle();
        check("a tempo ramp on the topper, from 20 s to 30 s, ending at 100.0 BPM",
              made && Topper().Model.Ramps is [{ StartSeconds: 20, EndSeconds: 30 } r] && r.ToSpeed == 100.0 / bpm);

        var match = buttons("Match BPM");
        var reset = buttons("Reset");
        if (match.Count != 2 || reset.Count < 2) { check("Match BPM and Reset buttons for the ramped topper", false); return; }

        await click(match[1]);
        double matched = Topper().Model.Speed;
        check($"Match BPM sets the tempo before the ramp ({bpm * matched:0.0} BPM) and leaves where the ramp goes",
              matched != speedBefore && Topper().Model.Ramps is [{ } kept] && kept.ToSpeed == 100.0 / bpm
              && labels().Contains("tempo ramp still goes to 100.0 BPM"));

        await click(reset[1]);
        check("Reset also removes the tempo ramp", Topper().Model.Ramps.Count == 0 && Topper().Model.Speed == 1
              && _vm.Session.VoiceFor(Topper().Model)?.Tempo.IsConstant == true && labels().Contains("tempo ramps removed"));

        await Undo();
        check("one Ctrl+Z brings back the ramp with the matched tempo", Topper().Model.Ramps.Count == 1 && Topper().Model.Speed == matched);
        await Undo();
        bool matchUndone = Topper().Model.Ramps.Count == 1 && Topper().Model.Speed == speedBefore;
        await Undo();
        check("two more Ctrl+Z take back the match and the ramp, one each", matchUndone && Topper().Model.Ramps.Count == 0);
    }

    /// <summary>
    /// Match BPM and Reset on a track with warp markers (2026-10-18), through the real buttons. Match
    /// BPM leaves the markers as they are: they are on the audio, counted in its tempo, so their beats
    /// move with the new tempo and stay pinned. Reset takes them away in the same one step as the
    /// track's tempo ramp. Each of those, and each marker, is one Ctrl+Z.
    /// </summary>
    private async Task CheckWarpButtonsAsync(Action<string, bool> check, Func<Button, Task> click,
                                             Func<string, List<Button>> buttons, Func<string> labels)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        async Task Undo() { _vm.UndoCommand.Execute(null); await Settle(); }

        if (_vm.Tracks.Count != 2 || _vm.Tracks[1].Model.Bpm <= 0 || _vm.Tracks[1].Model.Ramps.Count > 0 || _vm.Tracks[1].Model.Warps.Count > 0)
        {
            check("a topper with a known tempo, and no tempo ramp or warp marker yet, to put warp markers on", false);
            return;
        }
        var id = _vm.Tracks[1].Id;
        TrackViewModel Topper() => _vm.Tracks.First(t => t.Id == id);
        double bpm = Topper().Model.Bpm, speedBefore = Topper().Model.Speed, origin = Topper().Model.TimelineOffsetSeconds;
        double frame = 1.0 / _vm.Session.SampleRate;
        double Heard(Benchmix.Core.Model.TrackModel m, int i) => Benchmix.Core.WarpMarkers.TimelineSecondsOf(m, i);

        // Two markers through the view-model, as a press in the lane puts them: one pinned at 40 s,
        // and one pinned at 60 s with its beat moved half a second later.
        _vm.WarpMode = true;
        _vm.PressWarp(Topper(), 40);
        _vm.ReleaseWarp();
        double? grab = _vm.PressWarp(Topper(), 60);
        _vm.BeginWarpDrag();
        _vm.DragWarpTo((grab ?? 60) + 0.5, free: true);
        _vm.ReleaseWarp();
        _vm.WarpMode = false;
        await Settle();
        var placed = Topper().Model.Warps.ToList();
        // With no ramp, a marker's beat is heard where the tempo reaches its TempoSeconds: origin + TempoSeconds ÷ TEMPO.
        check("two warp markers on the topper, at 40 s and 60 s, the second one's beat moved 0.5 s later",
              grab is { } g && placed is [{ } w0, { } w1] && w0.TrackSeconds == w0.TempoSeconds && w1.TrackSeconds != w1.TempoSeconds
              && Math.Abs(origin + w1.TempoSeconds / speedBefore - (g + 0.5)) <= frame);

        var match = buttons("Match BPM");
        var reset = buttons("Reset");
        if (match.Count != 2 || reset.Count < 2 || placed.Count != 2) { check("Match BPM and Reset buttons for the warped topper", false); return; }

        await click(match[1]);
        double matched = Topper().Model.Speed;
        var m = Topper().Model;
        check($"Match BPM changes the tempo ({bpm * matched:0.0} BPM) and leaves the warp markers as they were, so their beats move with it and stay pinned",
              matched != speedBefore && m.Warps.SequenceEqual(placed)
              && Math.Abs(Heard(m, 0) - (origin + placed[0].TempoSeconds / matched)) <= frame
              && Math.Abs(Heard(m, 1) - (origin + placed[1].TempoSeconds / matched)) <= frame
              && labels().Contains("2 warp markers moved with their beats"));

        // A tempo ramp before them too, so Reset has both to take away.
        _vm.PlaceRampPoint(Topper(), 20, free: true);
        _vm.PlaceRampPoint(Topper(), 30, free: true);
        bool ramped = _vm.CommitRampEntry(id, "100");
        await Settle();

        await click(reset[1]);
        m = Topper().Model;
        check("Reset takes the warp markers away in the same step as the tempo ramp, and the engine plays the track untouched again",
              ramped && m.Warps.Count == 0 && m.Ramps.Count == 0 && m.Speed == 1
              && _vm.Session.VoiceFor(m)?.Tempo.IsConstant == true && labels().Contains("tempo ramps and warp markers removed"));

        await Undo();
        m = Topper().Model;
        check("one Ctrl+Z brings back the warp markers and the tempo ramp, with the matched tempo",
              m.Warps.SequenceEqual(placed) && m.Ramps.Count == 1 && m.Speed == matched);
        await Undo();
        bool rampUndone = Topper().Model.Ramps.Count == 0 && Topper().Model.Warps.SequenceEqual(placed);
        await Undo();
        bool matchUndone = Topper().Model.Speed == speedBefore && Topper().Model.Warps.SequenceEqual(placed);
        await Undo();
        bool secondUndone = Topper().Model.Warps.Count == 1 && Topper().Model.Warps[0] == placed[0];
        await Undo();
        check("four more Ctrl+Z take back the ramp, the match, the second marker with its move, and the first, one each",
              rampUndone && matchUndone && secondUndone && Topper().Model.Warps.Count == 0);
    }

    /// <summary>
    /// The S buttons, pressed as buttons (Daniel, 2026-10-27): a click solos that track alone, a
    /// click on another moves the solo rather than adding one, Ctrl+click adds, a click on the only
    /// soloed track turns it off, and one Ctrl+Z takes back a click however many tracks it changed.
    /// What is checked is the engine's own mute, not the lit button.
    /// </summary>
    private async Task CheckSoloButtonsAsync(Action<string, bool> check)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        List<System.Windows.Controls.Primitives.ToggleButton> Solos()
            => Descendants<System.Windows.Controls.Primitives.ToggleButton>(this)
                   .Where(b => b.IsVisible && b.Content as string == "S").ToList();

        // A mouse click on a ToggleButton is its toggle and then its Click event; the automation
        // peer only does the first, so the second is raised here as the button would raise it.
        async Task Press(int row, bool ctrl)
        {
            var button = Solos()[row];
            _scriptedModifiers = ctrl ? ModifierKeys.Control : ModifierKeys.None;
            var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(button);
            ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
            _scriptedModifiers = null;
            await Settle();
        }

        // What plays, as the engine has it, and what the two buttons show: "+" heard, "-" silent.
        string State()
        {
            var lit = Solos();
            return string.Concat(_vm.Tracks.Select((t, i) =>
                (_vm.Session.VoiceFor(t.Model) is { Muted: false } ? "+" : "-")
                + (i < lit.Count && lit[i].IsChecked == true ? "S" : "_")));
        }

        await Settle();
        if (_vm.Tracks.Count != 2 || Solos().Count != 2 || _vm.Tracks.Any(t => t.Muted || t.Soloed))
        {
            check($"two unmuted, unsoloed tracks to solo (found {_vm.Tracks.Count}, {Solos().Count} S buttons)", false);
            return;
        }

        await Press(0, ctrl: false);
        check($"a click on the bed's S plays the bed alone ({State()}, want +S-_)", State() == "+S-_");
        check("the status bar says how to hear another with it", _vm.Status.Contains("Ctrl+click"));

        await Press(1, ctrl: false);
        check($"a click on the topper's S moves the solo to it, not adding it ({State()}, want -_+S) — "
            + "two soloed tracks would play when he asked for one", State() == "-_+S");

        await Press(0, ctrl: true);
        check($"Ctrl+click on the bed's S adds it, so both play ({State()}, want +S+S)", State() == "+S+S");

        await Press(1, ctrl: false);
        check($"a click on one of two soloed tracks leaves it alone soloed ({State()}, want -_+S)", State() == "-_+S");

        await Press(1, ctrl: false);
        check($"a click on the only soloed track turns solo off ({State()}, want +_+_)", State() == "+_+_");

        _vm.UndoCommand.Execute(null);
        await Settle();
        check($"Ctrl+Z puts that solo back ({State()}, want -_+S)", State() == "-_+S");
        _vm.UndoCommand.Execute(null);
        await Settle();
        check($"one Ctrl+Z takes back a click that changed both tracks ({State()}, want +S+S)", State() == "+S+S");

        _vm.RedoCommand.Execute(null);
        _vm.RedoCommand.Execute(null);
        await Settle();
        check($"Ctrl+Y twice ends with nothing soloed ({State()}, want +_+_)", State() == "+_+_");
    }

    /// <summary>
    /// A render holds only what can be heard and ends where the last audible track ends (Daniel,
    /// 2026-10-27): through the view-model's render — the Render button's work once its dialog
    /// has a name — to a file in the temp folder, with the longer song muted and then with the
    /// shorter one soloed, reading the file back; and the button itself refuses, saying why, when
    /// nothing can be heard.
    /// </summary>
    private async Task CheckRenderHeardAsync(Action<string, bool> check)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        await Settle();
        if (_vm.Tracks.Count != 2 || _vm.Tracks.Any(t => t.Muted || t.Soloed))
        {
            check($"two unmuted, unsoloed tracks to render (found {_vm.Tracks.Count})", false);
            return;
        }

        long rate = _vm.Session.SampleRate;
        long EndOf(TrackViewModel t) => _vm.Session.VoiceFor(t.Model) is { } v ? v.TimelineOffsetFrames + v.TimelineLengthFrames : 0;
        var (longer, shorter) = EndOf(_vm.Tracks[0]) >= EndOf(_vm.Tracks[1])
            ? (_vm.Tracks[0], _vm.Tracks[1]) : (_vm.Tracks[1], _vm.Tracks[0]);
        long want = EndOf(shorter) + 2 * rate;                     // the shorter song, then the 2 s tail
        check($"the two songs differ in length, so the checks below can tell ({EndOf(longer)} and {EndOf(shorter)} frames)",
              EndOf(longer) > EndOf(shorter));

        static long FramesIn(string path)
        {
            using var reader = new NAudio.Wave.WaveFileReader(path);
            return reader.Length / reader.WaveFormat.BlockAlign;
        }

        var dir = Path.Combine(Path.GetTempPath(), "benchmix-verify", "render-check");
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, "render-check.wav");
        try
        {
            longer.Muted = true;                                    // what the M button's binding does
            await _vm.RenderToAsync(wav, Benchmix.Engine.Audio.RenderFormat.Pcm16);
            await Settle();
            long got = FramesIn(wav);
            check($"with the longer song muted, the file ends 2 s after the shorter one ({got} frames, want {want}) — "
                + "otherwise the muted song's length is silence on the end of the file", got == want);
            check($"the status names the muted song as left out (\"{_vm.Status}\")", _vm.Status.Contains($"{longer.Name} (muted)"));

            longer.Muted = false;
            _vm.Solo(shorter, alongside: false);                    // what a click on its S does
            await _vm.RenderToAsync(wav, Benchmix.Engine.Audio.RenderFormat.Pcm16);
            await Settle();
            got = FramesIn(wav);
            check($"with the shorter song soloed, the file ends 2 s after it ({got} frames, want {want})", got == want);
            check($"the status names the other song as left out, not soloed (\"{_vm.Status}\")", _vm.Status.Contains($"{longer.Name} (not soloed)"));
            _vm.Solo(shorter, alongside: false);                    // and off again

            longer.Muted = true;
            shorter.Muted = true;
            _vm.RenderCommand.Execute(null);
            await Settle();
            check($"with every track muted the Render button refuses and says why (\"{_vm.Status}\")",
                  _vm.Status.Contains("nothing to render") && _vm.Status.Contains("muted"));
            longer.Muted = false;
            shorter.Muted = false;
            await Settle();
        }
        finally
        {
            try { if (File.Exists(wav)) File.Delete(wav); } catch { }
        }
    }

    /// <summary>
    /// Duplicate track (Daniel, 2026-10-27) through the real strip menu item: the copy sits right
    /// below the topper with the same sections at the same places, the same knobs, its own name,
    /// selected and in the mix; one Ctrl+Z takes it away.
    /// </summary>
    /// <summary>
    /// A project that moved (Daniel, 2026-10-28): written to a temp folder with its songs beside it —
    /// one under its own name, one renamed — and paths that point nowhere, then opened the way a
    /// double-click opens one. Then Find missing file for a song that is nowhere, through the
    /// view-model (the real item opens a file box, which a scripted run must never show), the strip
    /// menu's item being offered only where it is needed. Runs last: it replaces the project.
    /// </summary>
    private async Task CheckRelinkAsync(Action<string, bool> check, string bedPath, string topperPath)
    {
        if (!File.Exists(bedPath) || !File.Exists(topperPath)) { check("the two songs to move a project away from", false); return; }
        string dir = Path.Combine(Path.GetTempPath(), "benchmix-verify", "moved-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string bedCopy = Path.Combine(dir, Path.GetFileName(bedPath));
            string topperCopy = Path.Combine(dir, "renamed" + Path.GetExtension(topperPath));
            File.Copy(bedPath, bedCopy);
            File.Copy(topperPath, topperCopy);
            string nowhereBed = Path.Combine(@"C:\nowhere", Path.GetFileName(bedPath));
            string nowhereTopper = Path.Combine(@"C:\nowhere", Path.GetFileName(topperPath));
            string lost = Path.Combine(@"C:\nowhere", "lost" + Path.GetExtension(bedPath));
            var project = new Benchmix.Core.Model.BenchmixProject { SampleRate = _vm.Session.SampleRate };
            project.Tracks.Add(new Benchmix.Core.Model.TrackModel { Name = "Bed", SourcePath = nowhereBed, Clips = { new Benchmix.Core.Model.ClipModel { SourcePath = nowhereBed } } });
            project.Tracks.Add(new Benchmix.Core.Model.TrackModel { Name = "Topper", SourcePath = nowhereTopper, Clips = { new Benchmix.Core.Model.ClipModel { SourcePath = nowhereTopper } } });
            project.Tracks.Add(new Benchmix.Core.Model.TrackModel { Name = "Lost", SourcePath = lost, Clips = { new Benchmix.Core.Model.ClipModel { SourcePath = lost } } });
            project.SourceFingerprints[nowhereTopper] = Benchmix.Core.Persistence.SourceRelinker.Fingerprint(topperPath)!;   // as remembered when it was added
            string path = Path.Combine(dir, "moved.benchmix");
            Benchmix.Core.Persistence.ProjectStore.Save(project, path);

            bool opened = await _vm.OpenProjectFileAsync(path);
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
            var rows = _vm.Tracks.ToList();
            check($"a moved project opens ({rows.Count} tracks; \"{_vm.Status}\")", opened && rows.Count == 3);
            if (rows.Count != 3) return;
            check($"its song under its own name is found next to the project (Bed loaded={rows[0].IsLoaded}, at '{rows[0].Model.SourcePath}')",
                  rows[0].IsLoaded && string.Equals(rows[0].Model.SourcePath, bedCopy, StringComparison.OrdinalIgnoreCase));
            check($"its renamed song is found by its fingerprint (Topper loaded={rows[1].IsLoaded}, at '{Path.GetFileName(rows[1].Model.SourcePath)}')",
                  rows[1].IsLoaded && string.Equals(rows[1].Model.SourcePath, topperCopy, StringComparison.OrdinalIgnoreCase));
            check($"the song that is nowhere still reads Missing ('{rows[2].LoadError}')",
                  !rows[2].IsLoaded && rows[2].LoadError is { } err && err.StartsWith("Missing:"));
            check($"the status says what was found and what is not (\"{_vm.Status}\")",
                  _vm.Status.Contains("Found 2 songs") && _vm.Status.Contains("1 track's audio is missing"));

            // The strip menu offers Find missing file on the track that needs it, and not on one that does not.
            MenuItem? Find(ContextMenu? m) => m?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == "findfile");
            if (StripOf(rows[2]) is { ContextMenu: { } lostMenu } lostStrip && StripOf(rows[0]) is { ContextMenu: { } bedMenu } bedStrip)
            {
                lostMenu.PlacementTarget = lostStrip;
                OnStripMenuOpened(lostMenu, new RoutedEventArgs());
                bedMenu.PlacementTarget = bedStrip;
                OnStripMenuOpened(bedMenu, new RoutedEventArgs());
                check("the strip menu offers 'Find missing file…' on the missing track and not on a loaded one",
                      Find(lostMenu) is { Visibility: Visibility.Visible } && Find(bedMenu) is { Visibility: Visibility.Collapsed });
            }
            else check("strips with menus to open", false);

            await _vm.RelinkAsync(lost, topperCopy);
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
            check($"Find missing file loads the chosen file and the lane is no longer Missing (loaded={rows[2].IsLoaded}; \"{_vm.Status}\")",
                  rows[2].IsLoaded && rows[2].LoadError is null
                  && string.Equals(rows[2].Model.SourcePath, topperCopy, StringComparison.OrdinalIgnoreCase) && _vm.Status.StartsWith("Found "));

            _vm.SaveTo(path);
            var saved = Benchmix.Core.Persistence.ProjectStore.Load(path).Project;
            check("Save keeps the found places and every song's fingerprint",
                  saved.Tracks.All(t => File.Exists(t.SourcePath) && t.Clips.All(c => File.Exists(c.SourcePath)))
                  && saved.SourceFingerprints.ContainsKey(bedCopy) && saved.SourceFingerprints.ContainsKey(topperCopy));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private async Task CheckDuplicateThroughMenuAsync(Action<string, bool> check)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        await Settle();
        if (_vm.Tracks.Count != 2) { check($"two tracks to duplicate one of (found {_vm.Tracks.Count})", false); return; }
        var topper = _vm.Tracks[1];
        var item = StripOf(topper)?.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "Duplicate track");
        check("the track strip's right-click menu offers 'Duplicate track'", item is not null);
        if (item is null) return;

        _vm.SelectedTrack = _vm.Tracks[0];
        if (StripOf(topper) is { } strip)
            OnTrackHeaderRightClicked(strip, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                                             { RoutedEvent = PreviewMouseRightButtonDownEvent });
        var topperId = topper.Id;
        int count = _vm.Tracks.Count;
        topper.EqLow = 0.5;
        _vm.EndGesture();                                          // a knob to carry over
        var places = topper.Clips.Select(c => (c.TimelineStartSeconds, c.TimelineEndSeconds)).ToList();
        try
        {
            var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        }
        catch (Exception ex)
        {
            check($"the menu item can be clicked ({ex.GetType().Name}: {ex.Message})", false);
            return;
        }
        await Settle();

        var copy = _vm.Tracks.Count == count + 1 ? _vm.Tracks[2] : null;
        check($"the copy appears right below the topper ({_vm.Tracks.Count} tracks; third is '{copy?.Name}')",
              copy is not null && copy.Id != topperId && _vm.Tracks[1].Id == topperId);
        if (copy is null) return;
        check($"it is named after the topper ('{copy.Name}', want '{topper.Name} copy')", copy.Name == topper.Name + " copy");
        var copied = copy.Clips.Select(c => (c.TimelineStartSeconds, c.TimelineEndSeconds)).ToList();
        check($"it has the same {places.Count} section(s) at the same places", copied.SequenceEqual(places));
        check($"it has the same knobs and tempo (LOW {copy.EqLow}, TEMPO {copy.Speed})",
              copy.EqLow == 0.5 && copy.Speed == topper.Speed && copy.Semitones == topper.Semitones);
        check("it is the selected track", ReferenceEquals(_vm.SelectedTrack, copy));
        check("it is in the mix: it has a voice, not muted", _vm.Session.VoiceFor(copy.Model) is { Muted: false });
        check($"the status bar says what happened (\"{_vm.Status}\")", _vm.Status.Contains("Duplicated") && _vm.Status.Contains(copy.Name));

        var copyId = copy.Id;
        _vm.UndoCommand.Execute(null);
        await Settle();
        check($"one Ctrl+Z takes the copy away ({_vm.Tracks.Count} tracks, want {count})",
              _vm.Tracks.Count == count && _vm.Tracks.All(t => t.Id != copyId));
        _vm.UndoCommand.Execute(null);                             // and the next, the knob
        await Settle();
        check("the Ctrl+Z after it puts the knob back", _vm.Tracks.First(t => t.Id == topperId).EqLow == 1);
    }

    private async Task CheckUndoResetAndNewAsync(Action<string, bool> check, Func<Button, Task> click,
                                                 Func<string, List<Button>> buttons)
    {
        async Task Settle()
        {
            while (_vm.IsBusy) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        await Settle();
        if (_vm.Tracks.Count != 2) { check($"two tracks to work on (found {_vm.Tracks.Count})", false); return; }
        var topperId = _vm.Tracks[1].Id;
        TrackViewModel Topper() => _vm.Tracks.First(t => t.Id == topperId);
        string bedPath = _vm.Tracks[0].Model.SourcePath;

        // --- an undo leaves the playhead where it was ---
        _vm.SelectedTrack = Topper();
        _vm.SeekTo(37.5);
        int sections = Topper().Clips.Count;
        _vm.SplitAtPlayheadCommand.Execute(null);
        check("a split to undo", Topper().Clips.Count == sections + 1);
        _vm.UndoCommand.Execute(null);
        await Settle();
        // The engine's position, not the view-model's: on screen a timer copies the engine's onto
        // the playhead, and nothing ticks it in a scripted run — reading the view-model passed
        // against the very bug.
        double engineAt = _vm.Session.Engine.PositionSeconds;
        check($"Ctrl+Z leaves the playhead where it was (37.500s; the engine is at {engineAt:0.000}s) — "
            + "at 0:00 the next split or paste lands at the start of the song",
              engineAt == 37.5 && _vm.PositionSeconds == 37.5 && Topper().Clips.Count == sections);

        // --- a refused command does not use up a press of Ctrl+Z ---
        _vm.SelectedTrack = Topper();
        _vm.SplitAtPlayheadCommand.Execute(null);                 // real: at 37.5
        _vm.SeekTo(9999);
        _vm.SplitAtPlayheadCommand.Execute(null);                 // refused: nothing there
        string refused = _vm.Status;
        _vm.UndoCommand.Execute(null);
        await Settle();
        check($"after a refused split ('{refused}'), one Ctrl+Z undoes the split before it ({Topper().Clips.Count} sections, want {sections})",
              Topper().Clips.Count == sections);

        // --- Reset is one undo step, through the real button ---
        var t = Topper();
        t.EqLow = 0.5; _vm.EndGesture();
        t.Gain = 0.5; _vm.EndGesture();
        t.Speed = 1.1; _vm.EndGesture();
        var reset = buttons("Reset");
        if (reset.Count >= 2)
        {
            await click(reset[1]);
            check("Reset puts the knobs back", Topper() is { EqLow: 1, Gain: 1, Speed: 1 });
            _vm.UndoCommand.Execute(null);
            await Settle();
            var back = Topper();
            check($"one Ctrl+Z undoes the whole Reset (LOW {back.EqLow}, VOL {back.Gain}, TEMPO {back.Speed}; want 0.5, 0.5, 1.1)",
                  back is { EqLow: 0.5, Gain: 0.5, Speed: 1.1 });
        }
        else check($"Reset buttons for the Reset check (found {reset.Count})", false);

        // --- an undo after saving counts as a change ---
        var dir = Path.Combine(Path.GetTempPath(), "benchmix-verify", "undo-check");
        try
        {
            Directory.CreateDirectory(dir);
            _vm.SaveTo(Path.Combine(dir, "undo-check.benchmix"));
            bool clean = !_vm.WindowTitle.EndsWith("*");
            _vm.UndoCommand.Execute(null);
            await Settle();
            check($"an undo after saving marks the project changed (title '{_vm.WindowTitle}') — otherwise closing does not warn",
                  clean && _vm.WindowTitle.EndsWith("*"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // --- New leaves nothing selected and nothing unsaved ---
        await _vm.NewCommand.ExecuteAsync(null);
        await Settle();
        check($"after New no track is selected (selected: {_vm.SelectedTrack?.Name ?? "none"}) — a paste would go to a track no longer there",
              _vm.Tracks.Count == 0 && _vm.SelectedTrack is null);
        check($"after New the project is not marked changed (title '{_vm.WindowTitle}')", !_vm.WindowTitle.EndsWith("*"));
        await _vm.AddFilesAsync(new[] { bedPath });
        await Settle();
        check("a song added after New becomes the selected track",
              _vm.Tracks.Count == 1 && ReferenceEquals(_vm.SelectedTrack, _vm.Tracks[0]));
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
        TextBox { Name: "BpmBox" } => "textbox:projectBpm",
        TextBox { Name: "RenameBox" } => "textbox:rename",
        TextBox { Name: "RampBpmBox" } => "textbox:rampBpm",
        TextBox { Name: "KnobValueBox" } => "textbox:knobValue",
        TextBox => "textbox",
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
        TakeKeyboardFromText();
        RulerDown((FrameworkElement)sender, WindowPoint(e), e.ClickCount);
        e.Handled = true;
    }

    /// <summary>
    /// A press on the ruler, apart from its event so the gesture test can make one — a double-click
    /// included, whose ClickCount WPF lets nobody set.
    /// </summary>
    private void RulerDown(FrameworkElement host, Point window, int clickCount)
    {
        double seconds = SecondsAtRulerX(LaneX(host, window));

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _settingLoop = true;
            _loopAnchorSeconds = seconds;
            _vm.SetLoop(seconds, seconds);
        }
        else if (clickCount >= 2)
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

        if (_scriptedWindowPoint is null) host.CaptureMouse();
    }

    private void OnRulerMouseMove(object sender, MouseEventArgs e) => RulerMove((FrameworkElement)sender, WindowPoint(e));

    private void RulerMove(FrameworkElement host, Point window)
    {
        if (!_scrubbing && !_settingLoop) return;
        double seconds = SecondsAtRulerX(LaneX(host, window));

        if (_settingLoop) _vm.SetLoop(_loopAnchorSeconds, seconds);
        else _vm.SeekTo(seconds);
    }

    private void OnRulerMouseUp(object sender, MouseButtonEventArgs e) => RulerUp((FrameworkElement)sender);

    private void RulerUp(FrameworkElement host)
    {
        if (host.IsMouseCaptured) host.ReleaseMouseCapture();
        _vm.EndGesture();
        _scrubbing = false;
        _settingLoop = false;
    }

    // ---------------- under the last track ----------------

    private void OnBelowTracksMouseDown(object sender, MouseButtonEventArgs e)
    {
        TakeKeyboardFromText();
        if (BelowDown((FrameworkElement)sender, WindowPoint(e), e.ClickCount)) e.Handled = true;
    }

    /// <summary>
    /// A press in the empty room under the last track. A double-click plays from there, as on a lane
    /// or the ruler; Alt+click puts the playhead there; a plain click lets go of the selected
    /// sections, as a click on empty lane space does. It selects no track: there is none there.
    /// </summary>
    private bool BelowDown(FrameworkElement surface, Point window, int clickCount = 1)
    {
        double at = SecondsAtLaneX(LaneX(surface, window));
        bool shift = Modifiers.HasFlag(ModifierKeys.Shift), ctrl = Modifiers.HasFlag(ModifierKeys.Control);
        var memo = _clickMemo;
        _clickMemo = null;

        if (clickCount >= 2 && !shift && !ctrl)
        {
            if (memo is { } before) _vm.RestoreSelection(before);
            _vm.PlayFrom(at);
            return true;
        }
        if (Modifiers.HasFlag(ModifierKeys.Alt)) { _vm.SeekTo(at); return true; }
        if (_vm.RampMode || _vm.WarpMode) return true;     // in ramp or warp mode a press acts on ramps or markers only
        if (_vm.MarkMode) { _vm.SeekTo(at); return true; } // and in mark mode it is a place in time, as on a lane
        BandDown(surface, window, additive: shift || ctrl);
        return true;
    }

    private void OnBelowTracksMouseMove(object sender, MouseEventArgs e) => BandMove(WindowPoint(e), e.LeftButton == MouseButtonState.Pressed);

    private void OnBelowTracksMouseUp(object sender, MouseButtonEventArgs e) => BandUp();

    private void OnBelowTracksLostCapture(object sender, MouseEventArgs e) => BandLost();

    // ---------------- the selection box ----------------

    /// <summary>
    /// A selection box being dragged: where it started, in timeline seconds and in the rows' own
    /// coordinates, so it stays put if they scroll under it; and what it adds to.
    /// </summary>
    private sealed class Band
    {
        public required FrameworkElement Surface;
        public required Point Origin;
        public required double AnchorSeconds;
        public required double AnchorY;
        public required bool Additive;
        public required IReadOnlySet<Guid> Keep;
        public bool Started;
        public HashSet<Guid>? Shown;
    }

    private Band? _band;

    private void BandDown(FrameworkElement surface, Point window, bool additive)
    {
        var p = BandPoint(window);
        _band = new Band
        {
            Surface = surface,
            Origin = window,
            AnchorSeconds = SecondsAtLaneX(p.X),
            AnchorY = p.Y,
            Additive = additive,
            Keep = additive ? _vm.SelectedClips.Select(c => c.Id).ToHashSet() : new HashSet<Guid>(),
        };
        _vm.PointerHeld = true;
        if (_scriptedWindowPoint is null) surface.CaptureMouse();
    }

    /// <summary>
    /// A window point in the box layer's coordinates, held to the lanes on screen. There is no
    /// auto-scroll, so the box must not reach rows or times nobody can see.
    /// </summary>
    private Point BandPoint(Point window)
    {
        double top = TrackScroller.TranslatePoint(new Point(0, 0), this).Y;
        var held = new Point(window.X, Math.Clamp(window.Y, top, top + TrackScroller.ViewportHeight));
        var p = TranslatePoint(held, BandLayer);
        return new Point(Math.Clamp(p.X, 0, BandLayer.ActualWidth), p.Y);
    }

    /// <summary>
    /// Past 4 px the box is drawn, and every section it touches — on every row it covers — is
    /// selected as it grows, as on the Windows desktop.
    /// </summary>
    private void BandMove(Point window, bool pressed)
    {
        var b = _band;
        if (b is null || !(pressed || _scriptedWindowPoint is not null)) return;
        if (!b.Started)
        {
            if (Math.Abs(window.X - b.Origin.X) < 4 && Math.Abs(window.Y - b.Origin.Y) < 4) return;
            b.Started = true;
        }

        var p = BandPoint(window);
        double seconds = SecondsAtLaneX(p.X), pps = _vm.PixelsPerSecond;
        double x0 = (Math.Min(b.AnchorSeconds, seconds) - _vm.ScrollSeconds) * pps;
        double x1 = (Math.Max(b.AnchorSeconds, seconds) - _vm.ScrollSeconds) * pps;
        double y0 = Math.Min(b.AnchorY, p.Y), y1 = Math.Max(b.AnchorY, p.Y);
        Canvas.SetLeft(BandRect, x0);
        Canvas.SetTop(BandRect, y0);
        BandRect.Width = Math.Max(1, x1 - x0);
        BandRect.Height = Math.Max(1, y1 - y0);
        BandRect.Visibility = Visibility.Visible;

        int first = -1, last = -1;
        for (int i = 0; i < _vm.Tracks.Count; i++)
        {
            if (TrackList.ItemContainerGenerator.ContainerFromItem(_vm.Tracks[i]) is not FrameworkElement row) continue;
            double rowTop = row.TranslatePoint(new Point(0, 0), BandLayer).Y;
            if (rowTop <= y1 && rowTop + row.ActualHeight > y0) { if (first < 0) first = i; last = i; }
        }
        var touched = first < 0 ? Array.Empty<(Benchmix.Core.Model.TrackModel, Benchmix.Core.Model.ClipModel)>()
                                : _vm.SectionsInBox(first, last, b.AnchorSeconds, seconds);
        var ids = touched.Select(t => t.Clip.Id).ToHashSet();
        if (b.Shown is not null && ids.SetEquals(b.Shown)) return;     // nothing new to draw
        b.Shown = ids;
        _vm.SelectBand(b.Keep, touched);
    }

    private void BandUp()
    {
        var b = _band;
        _band = null;                                  // state first: the release re-enters via LostMouseCapture
        if (b is null) return;
        _vm.PointerHeld = false;
        BandRect.Visibility = Visibility.Collapsed;
        if (_scriptedWindowPoint is null && b.Surface.IsMouseCaptured) b.Surface.ReleaseMouseCapture();
        if (b.Started) _vm.FinishBand();
        else if (!b.Additive)
        {
            _clickMemo = _vm.CaptureSelection();      // a double-click there to listen keeps the selection
            _vm.SelectClip(null);                      // a click on empty space lets go of the selection
        }
        _vm.EndGesture();
    }

    /// <summary>Losing the mouse ends a box as if let go; before it started it was no click at all.</summary>
    private void BandLost()
    {
        if (_band is { Started: true }) { BandUp(); return; }
        if (_band is not null) _vm.PointerHeld = false;
        _band = null;
        BandRect.Visibility = Visibility.Collapsed;
    }

    // ---------------- lanes: select, drag sections, seek ----------------

    /// <summary>Feeds the real lane width to the view-model, which Fit and the scrollbar both need.</summary>
    private void OnLaneSizeChanged(object sender, SizeChangedEventArgs e)
        => _vm.LaneWidthPx = e.NewSize.Width;

    private void OnTrackHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        // A press inside the name being typed is for the text: taking the keyboard away from it
        // would end the renaming before the click could place the caret.
        if (!IsInRenameBox(e.OriginalSource as DependencyObject)) TakeKeyboardFromText();
        if (((FrameworkElement)sender).DataContext is TrackViewModel vm)
            SelectTrackByPress(vm, e.OriginalSource as DependencyObject, Modifiers);
    }

    /// <summary>
    /// What a press on a strip does to the selection (Daniel, 2026-10-28: "must be able to select
    /// multiple tracks"). On one of the track's buttons or knobs — Match key, Match BPM's
    /// right-click — the track becomes the one commands act on and a selection it is part of is
    /// kept, so those commands can act on the lot. Elsewhere on the strip a plain press selects
    /// the track alone, Ctrl flips it in or out, and Shift takes every track between the anchor
    /// and it. The keys are read here and not passed on: Ctrl on the S button still means "solo
    /// alongside".
    /// </summary>
    internal void SelectTrackByPress(TrackViewModel vm, DependencyObject? pressedOn, ModifierKeys keys)
    {
        if (IsOnStripControl(pressedOn)) { _vm.MakeTrackPrimary(vm); return; }
        if ((keys & ModifierKeys.Shift) != 0) _vm.SelectTrackRange(vm);
        else if ((keys & ModifierKeys.Control) != 0) _vm.ToggleTrackSelection(vm);
        else _vm.SelectTrackAlone(vm);
    }

    /// <summary>True for a press on a strip's button, knob, slider or text box — its controls, as against its background.</summary>
    private static bool IsOnStripControl(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is Border { Name: "Strip" }) return false;
            if (d is System.Windows.Controls.Primitives.ButtonBase or Controls.Knob or TextBox or Slider) return true;
        }
        return false;
    }

    /// <summary>
    /// A right-click on a strip makes its track the one the menu acts on. A selection it is part
    /// of is kept (2026-10-29), so Match BPM's right-click can offer the average of the lot.
    /// </summary>
    private void OnTrackHeaderRightClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TrackViewModel vm) _vm.MakeTrackPrimary(vm);
    }

    // ---------------- renaming a track ----------------

    private void OnNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (NameDown((FrameworkElement)sender, WindowPoint(e), e.OriginalSource as DependencyObject, e.ClickCount)) e.Handled = true;
    }

    private void OnNameMouseMove(object sender, MouseEventArgs e) => NameMove(WindowPoint(e), e.LeftButton == MouseButtonState.Pressed);

    private void OnNameMouseUp(object sender, MouseButtonEventArgs e) => NameUp();

    /// <summary>Losing the mouse mid-drag (Alt+Tab, a dialog) puts nothing anywhere: the track stays.</summary>
    private void OnNameLostCapture(object sender, MouseEventArgs e)
    {
        if (_reorderTrack is not null) EndReorder();
    }

    /// <summary>
    /// A press on a track's grip, colour or name, apart from its event so the gesture test can make
    /// one. A double-click on it renames; a single press arms a drag up or down.
    /// </summary>
    private bool NameDown(FrameworkElement area, Point window, DependencyObject? pressedOn, int clickCount)
    {
        if (area.DataContext is not TrackViewModel t || t.IsRenaming || IsInRenameBox(pressedOn)) return false;
        if (clickCount >= 2) { EndReorder(); return _vm.BeginRename(t); }

        _reorderTrack = t;
        _reorderArea = area;
        _reorderOrigin = window;
        _reordering = false;
        _reorderTo = -1;
        _vm.PointerHeld = true;
        if (_scriptedWindowPoint is null) area.CaptureMouse();
        return true;
    }

    /// <summary>
    /// Dragging a track: past 4 px up or down it is a move, and a line shows where it will land. The
    /// rows themselves stay put until the drop — moving one while it holds the mouse would lose it.
    /// No auto-scroll: the pointer is held to the rows on screen.
    /// </summary>
    private void NameMove(Point window, bool pressed)
    {
        if (_reorderTrack is null || !(pressed || _scriptedWindowPoint is not null)) return;
        if (!_reordering)
        {
            if (Math.Abs(window.Y - _reorderOrigin.Y) < 4) return;
            _reordering = true;
            _vm.Status = $"Moving {_reorderTrack.Name}: let go where the line is.";
        }

        int from = _vm.Tracks.IndexOf(_reorderTrack);
        double top = TrackScroller.TranslatePoint(new Point(0, 0), this).Y;
        double y = Math.Clamp(window.Y, top, top + TrackScroller.ViewportHeight);

        // The row the pointer is in the upper half of goes below it; past the last, to the bottom.
        int insert = _vm.Tracks.Count;
        for (int i = 0; i < _vm.Tracks.Count; i++)
        {
            if (TrackList.ItemContainerGenerator.ContainerFromItem(_vm.Tracks[i]) is not FrameworkElement row) continue;
            if (y < row.TranslatePoint(new Point(0, 0), this).Y + row.ActualHeight / 2) { insert = i; break; }
        }
        _reorderTo = insert > from ? insert - 1 : insert;

        if (from < 0 || _reorderTo == from) { InsertLine.Visibility = Visibility.Collapsed; return; }
        var edgeOf = _vm.Tracks[Math.Min(insert, _vm.Tracks.Count - 1)];
        if (TrackList.ItemContainerGenerator.ContainerFromItem(edgeOf) is FrameworkElement edge)
        {
            double at = edge.TranslatePoint(new Point(0, insert < _vm.Tracks.Count ? 0 : edge.ActualHeight), ReorderLayer).Y;
            Canvas.SetTop(InsertLine, at - 1);
            InsertLine.Width = ReorderLayer.ActualWidth;
            InsertLine.Visibility = Visibility.Visible;
        }
    }

    private void NameUp()
    {
        var track = _reorderTrack;
        bool reordering = _reordering;
        int to = _reorderTo;
        EndReorder();                                    // state first: the release re-enters via LostMouseCapture
        if (track is null) return;
        if (reordering && to >= 0) _vm.MoveTrack(track, to);
        _vm.EndGesture();
    }

    private void EndReorder()
    {
        var area = _reorderArea;
        if (_reorderTrack is not null) _vm.PointerHeld = false;
        _reorderTrack = null;
        _reorderArea = null;
        _reordering = false;
        _reorderTo = -1;
        InsertLine.Visibility = Visibility.Collapsed;
        if (_scriptedWindowPoint is null && area is { IsMouseCaptured: true }) area.ReleaseMouseCapture();
    }

    private TrackViewModel? _reorderTrack;
    private FrameworkElement? _reorderArea;
    private Point _reorderOrigin;
    private bool _reordering;
    private int _reorderTo = -1;

    /// <summary>
    /// The strip menu's "Rename track". A menu item only inherits its track's DataContext once the
    /// menu has opened; the right-press that opens it has already selected that track, so that is
    /// the fall-back.
    /// </summary>
    private void OnRenameTrackMenu(object sender, RoutedEventArgs e)
    {
        if ((TrackOf(sender) ?? _vm.SelectedTrack) is { } t) _vm.BeginRename(t);
    }

    /// <summary>The strip menu's Duplicate track (Daniel, 2026-10-27): a copy right below it.</summary>
    private void OnDuplicateTrackMenu(object sender, RoutedEventArgs e)
    {
        if ((TrackOf(sender) ?? _vm.SelectedTrack) is { } t) _vm.DuplicateTrack(t);
    }

    // ---------------- tabs (2026-10-27) ----------------

    private static TabViewModel? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as TabViewModel;

    /// <summary>A click on a tab opens it; a double-click opens its name for typing.</summary>
    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;
        TabPress(tab, e.ClickCount);
        e.Handled = true;
    }

    private void TabPress(TabViewModel tab, int clickCount)
    {
        if (clickCount >= 2) _vm.BeginTabRename(tab);
        else _ = _vm.SwitchToTabAsync(tab);
    }

    private void OnRenameTabMenu(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) _vm.BeginTabRename(tab); }
    private void OnMoveTabLeftMenu(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) _vm.MoveTab(tab, -1); }
    private void OnMoveTabRightMenu(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) _vm.MoveTab(tab, +1); }
    private void OnRemoveTabMenu(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } tab) _ = _vm.RemoveTabAsync(tab); }

    private void OnTabRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TabViewModel t } box) return;
        if (e.Key == Key.Enter) { _vm.CommitTabRename(t.Id, box.Text); ReturnKeyboardToTimeline(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.CancelTabRename(t.Id); ReturnKeyboardToTimeline(); e.Handled = true; }
    }

    /// <summary>As <see cref="OnRenameLostFocus"/>: the keyboard going elsewhere in this window keeps what was typed.</summary>
    private void OnTabRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: TabViewModel t } box) return;
        if (e.NewFocus is not DependencyObject to || to is System.Windows.Controls.Primitives.MenuBase or MenuItem) return;
        if (!ReferenceEquals(to, this) && !(to is Visual v && v.IsDescendantOf(this))) return;
        _vm.CommitTabRename(t.Id, box.Text);
    }

    /// <summary>
    /// The strip menu opening: its Move to tab item gets one item per other tab, built here because
    /// the tabs change. Each is a Click handler, as every menu item is (MenuWiringTests).
    /// </summary>
    private void OnStripMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        // Find missing file only while a song this track reads from is not where the project says (2026-10-28).
        var track = (menu.PlacementTarget as FrameworkElement)?.DataContext as TrackViewModel ?? _vm.SelectedTrack;
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "findfile") is { } find)
            find.Visibility = track is not null && !_vm.Session.HasAllAudio(track.Model) ? Visibility.Visible : Visibility.Collapsed;
        var move = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "Move to tab");
        if (move is null) return;
        FillMoveToTab(move);
    }

    /// <summary>
    /// Match BPM's right-click menu (Daniel, 2026-10-28): its one item says what it will do, with the
    /// average in it, as the menu opens — or is greyed out and says why not.
    /// </summary>
    private void OnMatchBpmMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu }) FillMatchAverageMenu(menu);
    }

    internal void FillMatchAverageMenu(ContextMenu menu)
    {
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "average") is not { } item) return;
        var (picked, average, fromSelection) = _vm.AverageTempoTarget();
        item.IsEnabled = average is not null;
        item.Header = average is { } bpm
            ? $"Match {(fromSelection ? $"the {picked.Count} selected tracks" : $"all {picked.Count} tracks")} to their average, {bpm:0.0} BPM"
            : picked.Count == 1 ? "Match tracks to their average tempo — add another track with a tempo first"
                                : "Match tracks to their average tempo — no track has a tempo yet";
    }

    private void OnMatchAverageMenu(object sender, RoutedEventArgs e) => _vm.MatchTempoToAverage();

    /// <summary>The strip menu's Find missing file (Daniel, 2026-10-28): a box to point at the song, then every section from it follows.</summary>
    private void OnFindMissingFileMenu(object sender, RoutedEventArgs e)
    {
        if ((TrackOf(sender) ?? _vm.SelectedTrack) is { } t) _ = _vm.FindMissingFileAsync(t);
    }

    private void FillMoveToTab(MenuItem move)
    {
        move.Items.Clear();
        foreach (var tab in _vm.Tabs.Where(t => !t.IsCurrent))
        {
            var item = new MenuItem { Header = tab.Name, Tag = tab };
            item.Click += OnMoveTrackToTabMenu;
            move.Items.Add(item);
        }
        if (move.Items.Count == 0) move.Items.Add(new MenuItem { Header = "No other tab — add one with the + above the tracks", IsEnabled = false });
    }

    private void OnMoveTrackToTabMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TabViewModel tab } item) return;
        if ((TrackOf(item) ?? _vm.SelectedTrack) is { } t) _vm.MoveTrackToTab(t, tab);
    }

    /// <summary>
    /// The S button (Daniel, 2026-10-27): a click solos that track alone; Ctrl+click adds it to the
    /// ones already soloed. The button's own toggle is only a picture — the binding is one-way, and
    /// the rows are refreshed from the project afterwards.
    /// </summary>
    private void OnSoloClick(object sender, RoutedEventArgs e)
    {
        if (TrackOf(sender) is { } t) _vm.Solo(t, alongside: (Modifiers & ModifierKeys.Control) != 0);
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TrackViewModel t } box) return;
        if (e.Key == Key.Enter) { _vm.CommitRename(t.Id, box.Text); ReturnKeyboardToTimeline(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.CancelRename(t.Id); ReturnKeyboardToTimeline(); e.Handled = true; }
    }

    /// <summary>
    /// The keyboard leaving the name box keeps what was typed — but only when it went somewhere else
    /// in this window, which is clicking elsewhere. Switching to another program (no new focus) or
    /// opening the box's own right-click menu (a popup, outside the window) leaves the typing open.
    /// </summary>
    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: TrackViewModel t } box) return;
        if (e.NewFocus is not DependencyObject to || to is System.Windows.Controls.Primitives.MenuBase or MenuItem) return;
        if (!ReferenceEquals(to, this) && !(to is Visual v && v.IsDescendantOf(this))) return;
        _vm.CommitRename(t.Id, box.Text);
    }

    /// <summary>
    /// Puts the keyboard in the name box when it opens, with the old name selected so typing replaces
    /// it. Never in a scripted run: asking Windows for the keyboard would activate the hidden window.
    /// Deferred to ContextIdle, after a closing menu has handed the keyboard back to where it was.
    /// </summary>
    private void OnRenameBoxShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible || App.IsScriptedRun) return;
        Dispatcher.BeginInvoke(() => { if (box.IsVisible) { box.Focus(); box.SelectAll(); } },
                               System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    // ---------------- typing a knob's value (Daniel, 2026-10-12) ----------------

    /// <summary>The knob whose value is being typed, or null.</summary>
    private Controls.Knob? _editingKnob;

    /// <summary>What the box opened with. Left as it is, nothing is set: the value it rounds stays exact.</summary>
    private string _knobOpenedWith = "";

    private void OnKnobEditRequested(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Controls.Knob k) BeginKnobEdit(k);
    }

    /// <summary>Opens the box over a knob's number, filled in with its value and selected, so typing replaces it.</summary>
    private void BeginKnobEdit(Controls.Knob k)
    {
        if (k.EditUnit == Controls.KnobUnit.None) return;
        if (_editingKnob is not null) FinishKnobEdit();
        if (k.EditUnit == Controls.KnobUnit.Semitones && k.DataContext is TrackViewModel { LinkPitchToTempo: true })
        {
            _vm.Status = "LINK is on, so the pitch follows TEMPO. Turn LINK off to set the pitch on its own.";
            return;
        }
        _editingKnob = k;
        UpdateLayout();
        var at = k.TranslatePoint(new Point(0, k.ActualHeight), KnobEditLayer);
        double width = Math.Max(56, k.ActualWidth);
        KnobValueBox.Width = width;
        Canvas.SetLeft(KnobValueBox, at.X + (k.ActualWidth - width) / 2);
        Canvas.SetTop(KnobValueBox, at.Y - KnobValueBox.Height);
        KnobValueBox.Text = _knobOpenedWith = k.EditText();
        KnobValueBox.Visibility = Visibility.Visible;
        string how = k.EditUnit switch
        {
            Controls.KnobUnit.Percent => "as a percentage (-4.21 is 4.21% slower)",
            Controls.KnobUnit.Semitones => "in semitones (-2.88 is 2.88 semitones lower)",
            _ => "in dB (-6.5, or KILL)",
        };
        _vm.Status = $"Type the {k.Label} {how}, then press Enter. Esc keeps it as it was.";
        // Never in a scripted run: asking Windows for the keyboard would activate the hidden window.
        if (!App.IsScriptedRun)
            Dispatcher.BeginInvoke(() => { if (KnobValueBox.IsVisible) { KnobValueBox.Focus(); KnobValueBox.SelectAll(); } },
                                   System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Enter: sets the knob to what was typed, as one undo step. True when the box closed.</summary>
    private bool CommitKnobEdit(string? text)
    {
        if (_editingKnob is not { } k) return false;
        if (k.DataContext is not TrackViewModel t || !_vm.Tracks.Contains(t) || PresentationSource.FromVisual(k) is null) { CloseKnobEdit(); return false; }   // its row was rebuilt
        // Opened only to read it: nothing changes, so a value off the box's rounding — after Match
        // BPM, say — stays exactly what it was, and no undo step is made.
        if ((text ?? "").Trim() == _knobOpenedWith) { CancelKnobEdit(); return true; }
        var (ok, value, why) = k.ParseTyped(text);
        if (!ok) { _vm.Status = why; return false; }
        CloseKnobEdit();
        _vm.EndGesture();            // its own undo step, not part of a turn of the same knob just before
        k.Value = value;
        _vm.EndGesture();
        _vm.Status = $"{k.Label} set to {k.ValueText}.";
        return true;
    }

    private void CancelKnobEdit()
    {
        if (_editingKnob is not { } k) return;
        CloseKnobEdit();
        _vm.Status = $"{k.Label} kept at {k.ValueText}.";
    }

    /// <summary>A click elsewhere: keeps what was typed if the knob can take it, and keeps the old value if not.</summary>
    private void FinishKnobEdit()
    {
        if (_editingKnob is { } k && k.ParseTyped(KnobValueBox.Text).Ok) CommitKnobEdit(KnobValueBox.Text);
        else CancelKnobEdit();
    }

    private void CloseKnobEdit()
    {
        _editingKnob = null;
        KnobValueBox.Visibility = Visibility.Collapsed;
        ReturnKeyboardToTimeline();
    }

    private void OnKnobValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitKnobEdit(KnobValueBox.Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelKnobEdit(); e.Handled = true; }
    }

    /// <summary>As the name box: the keyboard leaving for elsewhere in this window keeps what was typed; leaving the app does not.</summary>
    private void OnKnobValueLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editingKnob is null) return;
        if (e.NewFocus is not DependencyObject to || to is System.Windows.Controls.Primitives.MenuBase or MenuItem) return;
        if (!ReferenceEquals(to, this) && !(to is Visual v && v.IsDescendantOf(this))) return;
        FinishKnobEdit();
    }

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private static bool IsInRenameBox(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBox { Name: "RenameBox" }) return true;
        return false;
    }

    private static bool IsInTabRenameBox(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBox { Name: "TabRenameBox" }) return true;
        return false;
    }

    /// <summary>Hands the keyboard back to the timeline after typing, so Delete and Space work again.</summary>
    private void ReturnKeyboardToTimeline()
    {
        if (!App.IsScriptedRun) Keyboard.Focus(this);
    }

    /// <summary>
    /// A click of the scroll wheel, anywhere in the window, stops and goes back to where play last
    /// started (Daniel, 2026-10-11). It is the window's Preview, so it is seen first — also in the
    /// middle of a drag, while a lane holds the mouse — and it is used up, so nothing under the
    /// pointer acts on it.
    /// </summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (WindowPress(e.ChangedButton, e.OriginalSource as DependencyObject, e.ClickCount)) e.Handled = true;
    }

    /// <summary>
    /// Every press in the window, before anything under the pointer sees it; true when it was used
    /// up. A press anywhere but in the name being typed keeps that name, as clicking away does in
    /// Explorer — a click on a lane moves no keyboard focus, so the box's own focus loss cannot be
    /// relied on for that.
    /// </summary>
    private bool WindowPress(MouseButton button, DependencyObject? pressedOn, int clickCount = 1)
    {
        if (!IsInRenameBox(pressedOn)) _vm.CommitOpenRenames();
        if (!IsInTabRenameBox(pressedOn)) _vm.CommitOpenTabRenames();
        if (_editingKnob is not null && !ReferenceEquals(pressedOn, KnobValueBox) && !IsInside(pressedOn, KnobValueBox)) FinishKnobEdit();

        // A press anywhere but in a ramp's BPM box keeps the tempo typed there, if the track can
        // reach it, and is used up doing so: it does not also start a new ramp.
        _pressEndedRampEntry = false;
        if (_vm.RampEntryOpen && !IsInRampBox(pressedOn) && clickCount < 2)
        {
            _vm.FinishRampEntry(applyIfValid: true);
            _pressEndedRampEntry = true;
        }
        if (button != MouseButton.Middle) return false;
        // A click of the wheel pauses exactly where the playhead is (Daniel, 2026-10-28); a
        // double-click stops and goes back to where play started. Its first click has already
        // paused, which moves nothing, so the second lands exactly where play started.
        if (clickCount >= 2) _vm.StopAndReturn();
        else _vm.WheelClick();
        return true;
    }

    /// <summary>
    /// The bare keys that act: Delete removes the selected sections, Space plays and pauses, PageUp
    /// and PageDown scroll the tracks (with Ctrl+Home and Ctrl+End, 2026-10-27). They are handled
    /// here, not as KeyBindings, because a bare key bound on the Window also fires from inside a
    /// text box — Space would start playback instead of typing a space into a track's name — so
    /// they act only when the keyboard focus is not in a text box or a menu. Being the window's
    /// Preview, Space also comes here before a focused button, which would otherwise take it as a
    /// press of itself.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Scrolling the tracks from the keyboard (Daniel, 2026-10-27): PageUp and PageDown a screen
        // at a time, Ctrl+Home and Ctrl+End to the top and the bottom. Not from a text box, where
        // Home and End move the caret, and a focused knob keeps bare Home for "back to normal".
        if ((e.Key is Key.PageUp or Key.PageDown && Modifiers == ModifierKeys.None)
            || (e.Key is Key.Home or Key.End && Modifiers == ModifierKeys.Control))
        {
            if (!BareKeyAppliesTo(e.OriginalSource as IInputElement ?? FocusedForKeys)) return;
            switch (e.Key)
            {
                case Key.PageUp: TrackScroller.PageUp(); break;
                case Key.PageDown: TrackScroller.PageDown(); break;
                case Key.Home: TrackScroller.ScrollToTop(); break;
                default: TrackScroller.ScrollToBottom(); break;
            }
            e.Handled = true;
            return;
        }

        // Escape drops a tempo ramp being placed, or keeps the tempo a retyped one had. Only while
        // there is one, and not from another text box, where Escape is that box's own.
        if (e.Key == Key.Escape && Modifiers == ModifierKeys.None && _vm.RampPending)
        {
            var focused = e.OriginalSource as IInputElement ?? FocusedForKeys;
            if (IsInRampBox(focused as DependencyObject) || BareKeyAppliesTo(focused))
            {
                _vm.CancelRamp();
                ReturnKeyboardToTimeline();
                e.Handled = true;
                return;
            }
        }

        if (e.Key is not (Key.Delete or Key.Space) || Modifiers != ModifierKeys.None) return;

        // OriginalSource is the element the key was raised on — the focused one, including one
        // inside an open menu's popup, which Keyboard.FocusedElement does not report. Typing gets
        // the key, held or not: a held Delete in PROJECT BPM deletes more than one character.
        if (!BareKeyAppliesTo(e.OriginalSource as IInputElement ?? FocusedForKeys)) return;
        if (e.IsRepeat) { e.Handled = true; return; }              // a held key is one press

        if (e.Key == Key.Space) { _vm.PlayPauseCommand.Execute(null); e.Handled = true; return; }
        if (PressDelete()) e.Handled = true;
    }

    /// <summary>
    /// Whether a Delete or Space press with this element focused is for the timeline rather than for
    /// text being typed or a menu being used.
    /// </summary>
    public static bool BareKeyAppliesTo(IInputElement? focused)
        => focused is not (System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
                           or System.Windows.Controls.Primitives.MenuBase or MenuItem);

    /// <summary>
    /// What the Delete key does: removes every selected section, and nothing when none is selected
    /// — or while a section is held under the mouse, armed or dragging, because deleting it would
    /// leave the drag holding a section that is no longer on the track, and the drop would then
    /// clear whatever is under its ghost.
    /// </summary>
    private bool PressDelete()
    {
        if (_band is not null) { _vm.Status = "Let go of the selection box first."; return false; }
        if (_dragClip is not null) { _vm.Status = "Let go of the section first."; return false; }
        if (_markPress is not null) { _vm.Status = "Let go of the mark first."; return false; }
        if (_warpPress is not null) { _vm.Status = "Let go of the warp marker first."; return false; }
        return _vm.DeleteSelection(fallBackToPlayhead: false);
    }

    /// <summary>How close to a section's edge counts as grabbing the edge rather than the body.</summary>
    private const double EdgeGrabPixels = 7;

    private enum DragKind { Move, TrimStart, TrimEnd, LoopStart, LoopEnd }

    private TrackViewModel? _dragTrack;
    private ClipViewModel? _dragClip;
    private DragKind _dragKind;

    /// <summary>
    /// A Ctrl+click waiting for its release: it flips the section in or out of the selection only
    /// if the pointer did not move, because Ctrl held through a drag means "place it freely".
    /// </summary>
    private ClipViewModel? _pendingToggle;

    /// <summary>A plain press on one of several selected sections: they stay selected unless it is let go without moving.</summary>
    private ClipViewModel? _pendingCollapse;
    private bool _dragGroup;
    private bool _groupCanChangeTrack;

    /// <summary>The selection before a click's release changed it, for that click's double-click to put back.</summary>
    private MainViewModel.SelectionMemo? _clickMemo;

    /// <summary>The gesture test's stand-in for the focused element; a scripted run never holds the keyboard.</summary>
    private IInputElement? _scriptedFocus;
    private IInputElement? FocusedForKeys => _scriptedFocus ?? Keyboard.FocusedElement;
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

    /// <summary>
    /// Ctrl, Shift and Alt as a gesture sees them. A scripted run never reads the real keyboard:
    /// Daniel types while the checks run, and a Ctrl he happened to be holding turned snapping off,
    /// made a double-click two Ctrl+clicks and left Space unhandled — 13 checks failed at random on
    /// 2026-10-11 until this.
    /// </summary>
    private ModifierKeys Modifiers => _scriptedModifiers ?? (App.IsScriptedRun ? ModifierKeys.None : Keyboard.Modifiers);

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

        // One of several selected sections, if short, would be all edge at 7 px a side and could
        // never be grabbed by its middle to drag the group. Its edges get a quarter of it each. A
        // section on its own keeps its full handles, so trimming and looping it are as they were.
        double grab = clip.IsSelected && _vm.SelectedClips.Count > 1
            ? Math.Min(EdgeGrabPixels, (right - left) / 4)
            : EdgeGrabPixels;
        if (Math.Abs(x - left) <= grab) return DragKind.TrimStart;
        if (Math.Abs(x - right) <= grab) return DragKind.TrimEnd;
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
    private FrameworkElement? LaneOf(TrackViewModel t) => RowPart<FrameworkElement>(t, "Lane");
    private FrameworkElement? StripOf(TrackViewModel t) => RowPart<FrameworkElement>(t, "Strip");
    private FrameworkElement? NameAreaOf(TrackViewModel t) => RowPart<FrameworkElement>(t, "NameArea");
    private TextBlock? NameTextOf(TrackViewModel t) => RowPart<TextBlock>(t, "NameText");
    private TextBox? RenameBoxOf(TrackViewModel t) => RowPart<TextBox>(t, "RenameBox");
    private TextBlock? GripOf(TrackViewModel t) => RowPart<TextBlock>(t, "Grip");

    /// <summary>A named element of a track's row, from the row template.</summary>
    private T? RowPart<T>(TrackViewModel t, string name) where T : class
    {
        if (TrackList.ItemContainerGenerator.ContainerFromItem(t) is not ContentPresenter row) return null;
        row.ApplyTemplate();
        return row.ContentTemplate?.FindName(name, row) as T;
    }

    private void OnLaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        TakeKeyboardFromText();
        if (LaneDown((FrameworkElement)sender, WindowPoint(e), e.ClickCount)) e.Handled = true;
    }

    /// <summary>
    /// A real press on the timeline takes the keyboard away from a text box, so Delete reaches the
    /// sections and a tempo typed into PROJECT BPM is committed (its binding updates on LostFocus).
    /// Only when the focus really is in a text box — a knob that holds focus keeps it — and never
    /// in a scripted run: the hidden window does not hold the keyboard, and asking Windows for it
    /// would activate the window, the one thing a scripted run must never do.
    /// </summary>
    private void TakeKeyboardFromText()
    {
        if (App.IsScriptedRun) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) Keyboard.Focus(this);
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
        _pendingToggle = null;                         // losing the mouse is not a click
        _pendingCollapse = null;
        if (_band is not null) BandLost();
        else if (_markPress is not null && !_markDragging)
        {
            // A press in mark mode that never moved: losing the mouse is not a click either, so the
            // playhead stays where it was (review, 2026-10-12).
            _markPress = null;
            _vm.PointerHeld = false;
        }
        else if (_dragClip is not null || _rampGrab is not null || _markPress is not null || _warpPress is not null) LaneUp((FrameworkElement)sender);
    }

    private bool LaneDown(FrameworkElement lane, Point window, int clickCount = 1)
    {
        if (lane.DataContext is not TrackViewModel vm) return false;

        // A plain press in a lane selects that track alone; with Ctrl or Shift — a section being
        // added to the selection — the track becomes the one commands act on and a track selection
        // it is part of is kept (2026-10-29).
        if (Modifiers.HasFlag(ModifierKeys.Control) || Modifiers.HasFlag(ModifierKeys.Shift)) _vm.MakeTrackPrimary(vm);
        else _vm.SelectTrackAlone(vm);
        // In ramp mode a double-click's second press does nothing: its first press already put a
        // point down, and may have opened the box it would otherwise close straight away.
        if (_vm.RampMode) { _clickMemo = null; return clickCount >= 2 || RampDown(lane, vm, window); }
        if (_vm.MarkMode) { _clickMemo = null; return MarkDown(lane, vm, window, clickCount); }
        // In warp mode too: the first press already put a marker down, or grabbed one.
        if (_vm.WarpMode) { _clickMemo = null; return clickCount >= 2 || WarpDown(lane, vm, window); }
        double x = LaneX(lane, window);
        var memo = _clickMemo;
        _clickMemo = null;

        // A double-click plays from where it lands (Daniel, 2026-10-11), on a section or between
        // them, and that is all the second press does: it arms no drag and no trim, so a hand that
        // moves a little before letting go moves nothing. It also puts back what the first click's
        // release changed — a group collapsed to one, a selection let go — so double-clicking to
        // listen keeps a group. With Shift or Ctrl held it is two ordinary clicks — as on the
        // ruler, where Shift+double-click still sets a loop.
        if (clickCount >= 2 && !Modifiers.HasFlag(ModifierKeys.Shift) && !Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (memo is { } before) _vm.RestoreSelection(before);
            _vm.PlayFrom(SecondsAtLaneX(x));
            return true;
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Alt-click puts the playhead here without disturbing anything.
            _vm.SeekTo(SecondsAtLaneX(x));
            return true;
        }

        var clip = ClipAtLaneX(vm, x);

        // A press on empty lane space arms a selection box (Daniel, 2026-10-11). Let go without
        // dragging and it is the click it always was: it lets go of the selection — or, with Ctrl
        // or Shift held, leaves the selection alone.
        if (clip is null)
        {
            BandDown(lane, window, additive: Modifiers.HasFlag(ModifierKeys.Shift) || Modifiers.HasFlag(ModifierKeys.Control));
            return true;
        }

        // Shift+click selects the range from the anchor — the section clicked before this — to
        // this one, and that is all it does: no drag starts from it.
        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _vm.SelectClipRange(clip);
            return true;
        }

        // A section whose audio never loaded has no usable source span, so there is nothing to
        // trim or repeat. Treat it as a move instead.
        var kind = clip.SourceDurationSeconds <= 0.001 ? DragKind.Move : KindAt(clip, x);

        // A press on the body of one of several selected sections grabs them all (Daniel,
        // 2026-10-11). The selection is kept while the button is down and collapses to this one
        // only if it is let go without moving, as in Explorer. An edge is one section's trim.
        bool member = clip.IsSelected && _vm.SelectedClips.Count > 1;
        bool group = kind == DragKind.Move && member;

        // Ctrl+click flips this section in or out of the selection — but only if the press is
        // released without moving, because Ctrl held through a drag means "place it freely" and
        // that drag must still start from here. The press only arms the toggle. A press on one of
        // several — its edge too, so a double-click there to listen keeps the group — keeps them all
        // until it is let go; a trim, once it starts, is of that one section alone.
        if (Modifiers.HasFlag(ModifierKeys.Control)) _pendingToggle = clip;
        else if (member) { _vm.MakePrimary(clip); _pendingCollapse = clip; }
        else _vm.SelectClip(clip);

        _dragTrack = vm;
        _dragClip = clip;
        _dragKind = kind;
        _dragGroup = group;
        _groupCanChangeTrack = group && _vm.SelectedClips.Select(c => c.Track).Distinct().Count() == 1;
        _vm.PointerHeld = true;

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
        if (_band is not null) { BandMove(window, pressed); return; }

        if (_rampGrab is { } g)
        {
            if (!(pressed || _scriptedWindowPoint is not null)) return;
            double moved = window.X - _rampGrabOrigin.X;
            if (!_rampDragging)
            {
                if (Math.Abs(moved) < 4) return;
                _rampDragging = true;
                _vm.BeginRampDotDrag(g.Track, g.Index, g.End);
            }
            _vm.DragRampDotTo(g.Seconds + moved / Math.Max(0.01, _vm.PixelsPerSecond), free: Modifiers.HasFlag(ModifierKeys.Control));
            _vm.RecomputeLength();
            return;
        }
        if (_markPress is { } mp)
        {
            if (!(pressed || _scriptedWindowPoint is not null)) return;
            double moved = window.X - _markPressOrigin.X;
            if (!_markDragging)
            {
                if (Math.Abs(moved) < 4) return;
                _markDragging = true;
                if (mp.Edge is { } startEnd) _vm.BeginMarkEdge(mp.Track, startEnd);
                else _vm.BeginMark(mp.Track, mp.GrabSeconds, free: Modifiers.HasFlag(ModifierKeys.Control));
            }
            _vm.DragMarkTo(mp.GrabSeconds + moved / Math.Max(0.01, _vm.PixelsPerSecond), free: Modifiers.HasFlag(ModifierKeys.Control));
            return;
        }
        if (_warpPress is { } wp)
        {
            if (!(pressed || _scriptedWindowPoint is not null)) return;
            double moved = window.X - _warpPressOrigin.X;
            if (!_warpDragging)
            {
                if (Math.Abs(moved) < 4) return;
                _warpDragging = true;
                _vm.BeginWarpDrag();
            }
            _vm.DragWarpTo(wp.GrabSeconds + moved / Math.Max(0.01, _vm.PixelsPerSecond), free: Modifiers.HasFlag(ModifierKeys.Control));
            return;
        }
        if (_vm.WarpMode)
        {
            if (lane.DataContext is TrackViewModel wh)
                lane.Cursor = WarpMarkerAt(wh, LaneX(lane, window)) >= 0 ? Cursors.SizeWE : Cursors.Cross;
            return;
        }
        if (_vm.MarkMode)
        {
            if (lane.DataContext is TrackViewModel mh)
                lane.Cursor = MarkEdgeAt(mh, LaneX(lane, window)) is not null ? Cursors.SizeWE : Cursors.IBeam;
            return;
        }
        if (_vm.RampMode)
        {
            if (lane.DataContext is TrackViewModel rh)
            {
                double rx = LaneX(lane, window), ry = window.Y - lane.TranslatePoint(new Point(0, 0), this).Y;
                lane.Cursor = RampDotAt(rh, lane, rx, ry) is not null ? Cursors.SizeWE : Cursors.Cross;
            }
            return;
        }

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
            // starts on a vertical pull alone, which is how a section goes to the track below —
            // but not a group spread over several tracks, which only goes sideways.
            bool vertical = _dragKind == DragKind.Move && (!_dragGroup || _groupCanChangeTrack);
            bool started = Math.Abs(dx) >= 4 || (vertical && Math.Abs(dy) >= 8);
            if (!started)
            {
                // Pulled up or down a group that can only go sideways: that is not a click, so it
                // must not collapse the group, or toggle the section out of it, when let go.
                if (Math.Abs(dy) >= 8 && (_pendingCollapse is not null || _pendingToggle is not null))
                {
                    _pendingCollapse = null;
                    _pendingToggle = null;
                    _vm.Status = "Sections on more than one track move sideways only.";
                }
                return;
            }
            _dragging = true;

            switch (_dragKind)
            {
                case DragKind.Move when _dragGroup: _dragGroup = _vm.BeginGroupMove(_dragClip); break;
                case DragKind.Move: _vm.BeginMove(_dragClip); break;
                case DragKind.TrimStart: _vm.BeginTrim(_dragClip, start: true); break;
                case DragKind.TrimEnd: _vm.BeginTrim(_dragClip, start: false); break;
                case DragKind.LoopStart: _vm.BeginLoop(_dragClip, left: true); break;
                case DragKind.LoopEnd: _vm.BeginLoop(_dragClip, left: false); break;
            }
        }

        // Ctrl is the one exception to snapping everywhere: it lets go of section edges and
        // whole repeats, and the drag goes exactly where the pointer does.
        bool free = Modifiers.HasFlag(ModifierKeys.Control);
        double delta = dx / Math.Max(0.01, _vm.PixelsPerSecond);

        switch (_dragKind)
        {
            case DragKind.Move when _dragGroup:
                _vm.DragGroupTo(_vm.GroupCanChangeTrack ? TrackAtWindowY(window.Y) : null, _dragStartTimelineStart + delta, free);
                break;
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
        if (_band is not null) { BandUp(); return; }

        if (_rampGrab is { } g)
        {
            // State first: releasing capture raises LostMouseCapture, which comes straight back here.
            bool dragged = _rampDragging;
            _rampGrab = null;
            _rampDragging = false;
            _vm.PointerHeld = false;
            if (_scriptedWindowPoint is null && lane.IsMouseCaptured) lane.ReleaseMouseCapture();
            if (dragged) _vm.EndDrag();
            else if (g.End) _vm.BeginRetypeRamp(g.Track, g.Index);
            else _vm.Status = "The start of a ramp always takes the tempo the track is already playing. Drag it to move it; click the end dot to change the tempo.";
            _vm.EndGesture();
            return;
        }

        if (_markPress is { } mp)
        {
            // State first: releasing capture raises LostMouseCapture, which comes straight back here.
            bool dragged = _markDragging;
            _markPress = null;
            _markDragging = false;
            _vm.PointerHeld = false;
            if (_scriptedWindowPoint is null && lane.IsMouseCaptured) lane.ReleaseMouseCapture();
            if (dragged) _vm.EndMarkDrag();
            else _vm.MarkModeClick(mp.Track, mp.PressSeconds);
            _vm.EndGesture();
            return;
        }

        if (_warpPress is not null)
        {
            // State first: releasing capture raises LostMouseCapture, which comes straight back here.
            _warpPress = null;
            _warpDragging = false;
            _vm.PointerHeld = false;
            if (_scriptedWindowPoint is null && lane.IsMouseCaptured) lane.ReleaseMouseCapture();
            _vm.ReleaseWarp();
            _vm.EndGesture();
            return;
        }

        // State first: releasing capture raises LostMouseCapture, which comes straight back here.
        bool dragging = _dragging;
        var toggle = _pendingToggle;
        var collapse = _pendingCollapse;
        _dragTrack = null;
        _dragClip = null;
        _dragging = false;
        _dragGroup = false;
        _pendingToggle = null;
        _pendingCollapse = null;
        _vm.PointerHeld = false;

        if (_scriptedWindowPoint is null && lane.IsMouseCaptured) lane.ReleaseMouseCapture();
        if (dragging) _vm.EndDrag();
        else
        {
            if (toggle is not null) _vm.ToggleClipSelection(toggle);   // a Ctrl+click that stayed put
            else if (collapse is not null)
            {
                // A click on one of several selected sections: just that one, now. Remembered,
                // so the second press of a double-click can put the group back.
                _clickMemo = _vm.CaptureSelection();
                _vm.SelectClip(collapse);
            }
            _vm.EndGesture();
        }
    }

    /// <summary>
    /// A right-click selects the track and the section under it before the menu opens, so the
    /// menu acts on what was clicked — "Paste at playhead" included, which pastes onto the
    /// selected track.
    /// </summary>
    private void OnLaneRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A right-click in a ramp's tempo box is for the box's own Cut, Copy and Paste: it must not
        // take the keyboard away from it, which would finish the typing.
        if (IsInRampBox(e.OriginalSource as DependencyObject)) return;
        LaneRightDown((FrameworkElement)sender, WindowPoint(e));
    }

    private void LaneRightDown(FrameworkElement lane, Point window)
    {
        _rampUnderMenu = null;                       // never one from an earlier right-click
        _warpUnderMenu = null;
        if (lane.DataContext is not TrackViewModel vm) return;

        // The left button is still holding a box or a section: a right-press — the start of a pan,
        // say, to reach further — must not swap what they hold for what is under the pointer.
        if (_band is not null || _dragClip is not null || _rampGrab is not null || _markPress is not null || _warpPress is not null) return;
        TakeKeyboardFromText();
        _vm.SelectedTrack = vm;
        double x = LaneX(lane, window);
        int under = Benchmix.Core.TempoRamps.IndexUnder(vm.Model, SecondsAtLaneX(x), 4 / Math.Max(0.01, _vm.PixelsPerSecond));
        _rampUnderMenu = under >= 0 ? (vm, vm.Model.Ramps[under]) : null;
        int marker = WarpMarkerAt(vm, x);
        _warpUnderMenu = marker >= 0 ? (vm, vm.Model.Warps[marker]) : null;
        var clip = ClipAtLaneX(vm, x);

        // Right-clicking one of several selected sections keeps the selection, so the menu's
        // Delete can remove them all; anywhere else it selects what is under the pointer.
        if (clip is not null && clip.IsSelected) _vm.MakePrimary(clip);
        else _vm.SelectClip(clip);
    }

    private void OnLaneContextMenuOpening(object sender, ContextMenuEventArgs e) => PrepareLaneMenu((FrameworkElement)sender);

    /// <summary>
    /// Sets the lane menu up for where it was opened: loop mode's tick, "Remove tempo ramp" only over
    /// a ramp and "Remove warp marker" only over a marker, and — while a stretch is marked — Cut,
    /// Copy, Repeat and Delete named for the mark, which is what they then act on.
    /// </summary>
    private void PrepareLaneMenu(FrameworkElement lane)
    {
        if (lane.ContextMenu is not { } menu) return;
        bool overRamp = _rampUnderMenu is { } r && ReferenceEquals(r.Track, lane.DataContext);
        bool overWarp = _warpUnderMenu is { } w && ReferenceEquals(w.Track, lane.DataContext);
        bool marked = _vm.MarkedTrack is not null;
        int several = _vm.SelectedClips.Count(c => _vm.Tracks.Contains(c.Track));     // two or more: the keys act on them all (2026-10-29)
        string Named(string one, string verb) => marked ? $"{verb} marked stretch" : several >= 2 ? $"{verb} {several} sections" : one;
        var section = ClipOf(lane);                 // for the crossfade length in force, ticked
        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            if (item is MenuItem { Tag: "loopmode" } loop) loop.IsChecked = _vm.LoopMode;
            if (item is MenuItem { Tag: "xfade" } xfade)
                foreach (var choice in xfade.Items.OfType<MenuItem>())
                    choice.IsChecked = section is not null && TagSeconds(choice) == section.CrossfadeSeconds;
            if (item.Tag as string == "removeRamp") item.Visibility = overRamp ? Visibility.Visible : Visibility.Collapsed;
            // Over a ramp and a marker both, the two Remove items sit together under the ramp's separator.
            if (item is MenuItem { Tag: "removeWarp" }) item.Visibility = overWarp ? Visibility.Visible : Visibility.Collapsed;
            if (item is Separator { Tag: "removeWarp" }) item.Visibility = overWarp && !overRamp ? Visibility.Visible : Visibility.Collapsed;
            if (item is MenuItem { Tag: "cut" } cut) cut.Header = Named("Cut section", "Cut");
            if (item is MenuItem { Tag: "copy" } copy) copy.Header = Named("Copy section", "Copy");
            if (item is MenuItem { Tag: "repeat" } repeat) repeat.Header = Named("Repeat this section", "Repeat");
            if (item is MenuItem { Tag: "delete" } delete) delete.Header = Named("Delete section", "Delete");
        }
    }

    private void OnRemoveRampMenu(object sender, RoutedEventArgs e)
    {
        if ((TrackOf(sender) ?? _vm.SelectedTrack) is { } t && _rampUnderMenu is { } r && ReferenceEquals(r.Track, t))
        {
            int i = t.Model.Ramps.IndexOf(r.Ramp);     // the ramp right-clicked, wherever it now sorts
            if (i >= 0) _vm.RemoveRamp(t, i);
            else _vm.Status = "That tempo ramp is no longer there.";
        }
        else _vm.Status = "Right-click on a tempo ramp to remove it.";
    }

    private void OnRemoveWarpMenu(object sender, RoutedEventArgs e)
    {
        if ((TrackOf(sender) ?? _vm.SelectedTrack) is { } t && _warpUnderMenu is { } w && ReferenceEquals(w.Track, t))
        {
            int i = t.Model.Warps.IndexOf(w.Marker);   // the marker right-clicked, by value: the old index could name another one
            if (i >= 0) _vm.RemoveWarp(t, i);
            else _vm.Status = "That warp marker is no longer there.";
        }
        else _vm.Status = "Right-click on a warp marker to remove it.";
    }

    // ---------------- tempo ramps in the lane (Daniel, 2026-10-11) ----------------

    /// <summary>A press on a ramp's dot, waiting to see whether it is a click or a drag.</summary>
    private (TrackViewModel Track, int Index, bool End, double Seconds)? _rampGrab;
    private bool _rampDragging;
    private Point _rampGrabOrigin;
    private (TrackViewModel Track, Benchmix.Core.Model.TempoRamp Ramp)? _rampUnderMenu;

    /// <summary>Set by <see cref="WindowPress"/>: that press kept the tempo being typed, and does nothing else.</summary>
    private bool _pressEndedRampEntry;

    /// <summary>
    /// A left press on a lane in ramp mode. On a dot it grabs that end of its ramp — a drag moves it,
    /// a click on the end dot retypes its tempo; anywhere else it puts a ramp's next point down.
    /// Sections are not selected or dragged.
    /// </summary>
    private bool RampDown(FrameworkElement lane, TrackViewModel vm, Point window)
    {
        if (_pressEndedRampEntry) { _pressEndedRampEntry = false; return true; }
        double x = LaneX(lane, window), y = window.Y - lane.TranslatePoint(new Point(0, 0), this).Y;
        if (Modifiers.HasFlag(ModifierKeys.Alt)) { _vm.SeekTo(SecondsAtLaneX(x)); return true; }

        if (RampDotAt(vm, lane, x, y) is { } dot)
        {
            _rampGrab = (vm, dot.Index, dot.End, dot.Seconds);
            _rampGrabOrigin = window;
            _rampDragging = false;
            _vm.PointerHeld = true;
            if (_scriptedWindowPoint is null) lane.CaptureMouse();
            return true;
        }
        _vm.PlaceRampPoint(vm, SecondsAtLaneX(x), free: Modifiers.HasFlag(ModifierKeys.Control));
        return true;
    }

    /// <summary>The placed ramp's dot under a lane point, the end winning where both are; laid out as it is drawn.</summary>
    private (int Index, bool End, double Seconds)? RampDotAt(TrackViewModel vm, FrameworkElement lane, double x, double y)
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var visuals = vm.RampVisuals;
        for (int i = 0; i < visuals.Count && i < vm.Model.Ramps.Count; i++)
        {
            var g = Controls.RampLayout.Layout(visuals[i], _vm.PixelsPerSecond, _vm.ScrollSeconds, lane.ActualHeight - 1, dip);
            if (Math.Abs(x - g.EndDot.X) <= Controls.RampLayout.GrabPixels || g.EndLabel.Contains(new Point(x, y)))
                return (i, true, vm.Model.Ramps[i].EndSeconds);
            if (Math.Abs(x - g.StartDot.X) <= Controls.RampLayout.GrabPixels)
                return (i, false, vm.Model.Ramps[i].StartSeconds);
        }
        return null;
    }

    // ---------------- warp markers in the lane (Daniel, 2026-10-18) ----------------

    /// <summary>
    /// A press in WARP MODE holding a marker — the one it was on, or the one it put down — waiting to
    /// see whether it becomes a drag: the track, and where the marker's beat was heard at the press.
    /// </summary>
    private (TrackViewModel Track, double GrabSeconds)? _warpPress;
    private bool _warpDragging;
    private Point _warpPressOrigin;
    private (TrackViewModel Track, Benchmix.Core.Model.WarpMarker Marker)? _warpUnderMenu;

    /// <summary>
    /// A left press on a lane in WARP MODE. On a marker it grabs it — a drag moves the beat it is on;
    /// anywhere else on the track it puts a marker down there, which changes nothing heard, and a
    /// drag on from there moves that beat. Alt+click only moves the playhead. Sections are not
    /// selected or dragged.
    /// </summary>
    private bool WarpDown(FrameworkElement lane, TrackViewModel vm, Point window)
    {
        double x = LaneX(lane, window);
        if (Modifiers.HasFlag(ModifierKeys.Alt)) { _vm.SeekTo(SecondsAtLaneX(x)); return true; }

        // Before PointerHeld is set: the view-model refuses a press while something is already held.
        if (_vm.PressWarp(vm, SecondsAtLaneX(x)) is not { } grab) return true;     // refused: the status bar says why
        _warpPress = (vm, grab);
        _warpPressOrigin = window;
        _warpDragging = false;
        _vm.PointerHeld = true;
        if (_scriptedWindowPoint is null) lane.CaptureMouse();
        return true;
    }

    /// <summary>The warp marker under a lane x, nearest first, within its grab; −1 when none is. Laid out as it is drawn.</summary>
    private int WarpMarkerAt(TrackViewModel vm, double x)
    {
        int i = Controls.WarpLayout.IndexAt(vm.WarpVisuals, x, _vm.PixelsPerSecond, _vm.ScrollSeconds);
        return i < vm.Model.Warps.Count ? i : -1;
    }

    // ---------------- marking a stretch (Daniel, 2026-10-12) ----------------

    /// <summary>
    /// A press on a lane in MARK MODE, waiting to see whether it is a click or a drag: where it was
    /// pressed, where the drag counts from (the end of the mark grabbed, or the press), and which end
    /// was grabbed — null for a new mark.
    /// </summary>
    private (TrackViewModel Track, double PressSeconds, double GrabSeconds, bool? Edge)? _markPress;
    private bool _markDragging;
    private Point _markPressOrigin;

    /// <summary>
    /// A left press on a lane in MARK MODE. Dragged, it marks a stretch of that track — or, on an end
    /// of the mark already there, moves that end; clicked, it puts the playhead there. A double-click
    /// plays from there, as everywhere on the timeline. Shift+click marks from the playhead to there.
    /// Sections are not selected or dragged.
    /// </summary>
    private bool MarkDown(FrameworkElement lane, TrackViewModel vm, Point window, int clickCount)
    {
        double x = LaneX(lane, window), at = SecondsAtLaneX(x);
        bool shift = Modifiers.HasFlag(ModifierKeys.Shift), ctrl = Modifiers.HasFlag(ModifierKeys.Control);
        if (clickCount >= 2 && !shift && !ctrl) { _vm.PlayFrom(at); return true; }
        if (shift) { _vm.MarkFromPlayhead(vm, at, free: ctrl); return true; }

        var edge = MarkEdgeAt(vm, x);
        double grab = edge is { } startEnd && vm.MarkVisual is { } m ? (startEnd ? m.StartSeconds : m.EndSeconds) : at;
        _markPress = (vm, at, grab, edge);
        _markPressOrigin = window;
        _markDragging = false;
        _vm.PointerHeld = true;
        if (_scriptedWindowPoint is null) lane.CaptureMouse();
        return true;
    }

    /// <summary>
    /// Which end of the mark on this track a lane x is on, if either: true for its start, false for
    /// its end. On a mark too narrow to tell, the nearer one.
    /// </summary>
    private bool? MarkEdgeAt(TrackViewModel vm, double x)
    {
        if (vm.MarkVisual is not { } m) return null;
        double left = (m.StartSeconds - _vm.ScrollSeconds) * _vm.PixelsPerSecond;
        double right = (m.EndSeconds - _vm.ScrollSeconds) * _vm.PixelsPerSecond;
        double toLeft = Math.Abs(x - left), toRight = Math.Abs(x - right);
        if (Math.Min(toLeft, toRight) > EdgeGrabPixels) return null;
        return toLeft <= toRight;
    }

    private void OnRampBpmKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TrackViewModel t } box) return;
        if (e.Key == Key.Enter)
        {
            if (_vm.CommitRampEntry(t.Id, box.Text)) ReturnKeyboardToTimeline();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.CancelRamp();
            ReturnKeyboardToTimeline();
            e.Handled = true;
        }
    }

    /// <summary>
    /// The keyboard leaving the box keeps the tempo typed, as a click elsewhere does — but not when
    /// it left the app (no new focus), or went into a menu or popup.
    /// </summary>
    private void OnRampBpmLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not DependencyObject to || to is System.Windows.Controls.Primitives.MenuBase or MenuItem) return;
        if (!ReferenceEquals(to, this) && !(to is Visual v && v.IsDescendantOf(this))) return;
        if (_vm.RampEntryOpen) _vm.FinishRampEntry(applyIfValid: true);
    }

    /// <summary>
    /// Puts the keyboard in the box when it opens, with the tempo selected so typing replaces it.
    /// Never in a scripted run: asking Windows for the keyboard would activate the hidden window.
    /// </summary>
    private void OnRampBpmBoxShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible || App.IsScriptedRun) return;
        Dispatcher.BeginInvoke(() => { if (box.IsVisible) { box.Focus(); box.SelectAll(); } },
                               System.Windows.Threading.DispatcherPriority.Input);
    }

    private static bool IsInRampBox(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBox { Name: "RampBpmBox" }) return true;
        return false;
    }

    private TextBox? RampBpmBoxOf(TrackViewModel t) => RowPart<TextBox>(t, "RampBpmBox");

    private void OnLoopModeMenu(object sender, RoutedEventArgs e) => _vm.LoopMode = ((MenuItem)sender).IsChecked;

    private void OnAddEmptyTrackMenu(object sender, RoutedEventArgs e) => _vm.AddEmptyTrackCommand.Execute(null);

    // ---------------- clip context menu ----------------

    private static TrackViewModel? TrackOf(object sender)
        => (sender as FrameworkElement)?.DataContext as TrackViewModel;

    /// <summary>
    /// The section a menu item acts on: whichever one is selected on that track, which the
    /// right-click itself just set.
    /// </summary>
    /// <summary>
    /// The section a menu item acts on: the one the right-click made primary when it is on this
    /// track, otherwise whichever one is selected there. With several selected, the first in the
    /// list is not the one under the pointer.
    /// </summary>
    private ClipViewModel? ClipOf(object sender)
    {
        if (TrackOf(sender) is not { } track) return null;
        if (_vm.SelectedClip is { IsSelected: true } primary && track.Clips.Contains(primary)) return primary;
        return track.Clips.FirstOrDefault(c => c.IsSelected);
    }

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

    /// <summary>
    /// The crossfade at this section's start (Daniel, 2026-10-28): the length he picked for the
    /// splice, or 0 for the standard 5 ms. The item carries its track while the menu is open; the
    /// probes click the real item with the menu shut, and then it is the selected section.
    /// </summary>
    private void OnCrossfade(object sender, RoutedEventArgs e)
    {
        var c = ClipOf(sender) ?? (_vm.SelectedClip is { IsSelected: true } selected ? selected : null);
        if (c is null) { _vm.Status = "Click a section first."; return; }
        _vm.PushUndo("crossfade", null);
        c.CrossfadeSeconds = TagSeconds(sender);
        _vm.Status = $"{c.Track.Name}: {c.CrossfadeText}.";
    }

    private void OnTrimStartToPlayhead(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        // How far into the song the playhead is, after the track's tempo and its tempo ramps.
        double into = Benchmix.Core.TimelineEditor.TrackSecondsAt(c.Track.Model, _vm.PositionSeconds) - c.TrackStartSeconds;
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
        double into = Benchmix.Core.TimelineEditor.TrackSecondsAt(c.Track.Model, _vm.PositionSeconds) - c.TrackStartSeconds + c.SourceStartSeconds;
        if (into <= c.SourceStartSeconds + 0.05) { _vm.Status = "The playhead is at or before the section's start."; return; }

        _vm.BeginTrim(c, start: false);
        _vm.DragTrimTo(_vm.PositionSeconds, free: true);
        _vm.EndDrag();
    }

    private void OnClearTrim(object sender, RoutedEventArgs e)
    {
        if (ClipOf(sender) is not { } c) { _vm.Status = "Click a section first."; return; }
        _vm.ClearTrim(c);
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
    private void OnJoinSections(object sender, RoutedEventArgs e) => _vm.JoinSectionsCommand.Execute(null);
    private void OnCutSection(object sender, RoutedEventArgs e) => _vm.CutClipCommand.Execute(null);
    private void OnCopySection(object sender, RoutedEventArgs e) => _vm.CopyClipCommand.Execute(null);
    private void OnPasteSection(object sender, RoutedEventArgs e) => _vm.PasteClipCommand.Execute(null);
    private void OnDuplicateSection(object sender, RoutedEventArgs e) => _vm.DuplicateClipCommand.Execute(null);
    private void OnDeleteSection(object sender, RoutedEventArgs e) => _vm.DeleteClipCommand.Execute(null);
    private void OnSplitStems(object sender, RoutedEventArgs e) => _vm.SplitStemsCommand.Execute(TrackOf(sender));

    // ---------------- right-drag: pan the timeline, sideways and up and down ----------------

    private bool _panArmed;
    private bool _panning;
    private bool _suppressContextMenu;
    private double _panOriginX, _panOriginY;
    private double _panStartScroll, _panStartVertical;
    private Cursor? _cursorBeforePan;

    /// <summary>Scripted pointer position for <c>--gesturetest</c>; null in normal use.</summary>
    private double? _scriptedPointerX, _scriptedPointerY;

    private double PointerX(MouseEventArgs e) => _scriptedPointerX ?? e.GetPosition(TimelineGrid).X;

    // With a scripted X and no scripted Y the pointer is taken not to have moved up or down: the
    // real mouse, which Daniel may be using meanwhile, must not leak into a pan check.
    private double PointerY(MouseEventArgs e)
        => _scriptedPointerY ?? (_scriptedPointerX is not null ? _panOriginY : e.GetPosition(TimelineGrid).Y);

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
        _panOriginY = _scriptedPointerY ?? (_scriptedPointerX is not null ? 0 : e.GetPosition(TimelineGrid).Y);
        _panStartScroll = _vm.ScrollSeconds;
        _panStartVertical = TrackScroller.VerticalOffset;
    }

    private void OnTimelinePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panArmed) return;

        // The button can be released outside the window, where no up event reaches us.
        if (_scriptedPointerX is null && e.RightButton != MouseButtonState.Pressed) { EndPan(); return; }

        double dx = PointerX(e) - _panOriginX, dy = PointerY(e) - _panOriginY;
        if (!_panning)
        {
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;       // same slop as a clip drag, so a click never pans
            _panning = true;
            if (_scriptedPointerX is null) TimelineGrid.CaptureMouse();
            _cursorBeforePan = Cursor;
            Cursor = TrackScroller.ScrollableHeight > 0 ? Cursors.ScrollAll : Cursors.ScrollWE;
        }

        // Grab-and-drag: pulling right reveals earlier audio, so the view scrolls back; pulling
        // down reveals the tracks above (Daniel, 2026-10-27: "vertical scrolling is biggest pain").
        _vm.ScrollSeconds = _panStartScroll - dx / Math.Max(0.01, _vm.PixelsPerSecond);
        TrackScroller.ScrollToVerticalOffset(_panStartVertical - dy);
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

    // ---------------- wheel: zoom, scroll and scrub ----------------

    private void OnTimelineWheel(object sender, MouseWheelEventArgs e)
    {
        // A plain turn is left alone, anywhere over the tracks: the ScrollViewer scrolls them up and
        // down, or the knob under the pointer turns (Knob.OnMouseWheel). Daniel, 2026-10-27:
        // "vertical scroll when mouse is in the main area. hold alt, and it scrubs. retain
        // functionality of hovering over knobs and wheeling to make adjustments there." Before that,
        // a plain turn over the waveforms moved the playhead (2026-10-12).
        if (TimelineWheel(WindowPoint(e), e.Delta)) e.Handled = true;
    }

    private void OnRulerWheel(object sender, MouseWheelEventArgs e)
    {
        // The ruler is outside the ScrollViewer, so a plain turn there scrolls the tracks by hand:
        // the wheel scrolls them wherever it is over the track area.
        if (!TimelineWheel(WindowPoint(e), e.Delta)) ScrollTracksByWheel(e.Delta);
        e.Handled = true;
    }

    /// <summary>A notch of the plain wheel over the ruler: what the ScrollViewer does for one over the tracks.</summary>
    private void ScrollTracksByWheel(int delta)
        => TrackScroller.ScrollToVerticalOffset(TrackScroller.VerticalOffset
                                                - delta / (double)Mouse.MouseWheelDeltaForOneLine * SystemParameters.WheelScrollLines * 16);

    /// <summary>How far one notch of Alt+wheel moves the playhead, on screen.</summary>
    private const double ScrubPixelsPerNotch = 20;

    /// <summary>
    /// A turn of the wheel on the timeline: Ctrl zooms about the pointer, Shift scrolls sideways,
    /// and Alt moves the playhead back and forth (the plain wheel's job from 2026-10-12 until
    /// 2026-10-27, when Daniel gave the plain wheel to scrolling the tracks) — up is later, a notch
    /// is the same distance on screen whatever the zoom, and the view follows it. A plain turn is
    /// not used here: it is for the tracks. True when it was used.
    /// </summary>
    private bool TimelineWheel(Point window, int delta)
    {
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Zoom about the pointer, so the thing under the cursor stays under the cursor.
            double x = Math.Max(0, window.X - 392);
            double timeUnderCursor = _vm.ScrollSeconds + x / _vm.PixelsPerSecond;

            double factor = delta > 0 ? 1.2 : 1 / 1.2;
            _vm.PixelsPerSecond = Math.Clamp(_vm.PixelsPerSecond * factor, 2, 400);
            _vm.ScrollSeconds = Math.Max(0, timeUnderCursor - x / _vm.PixelsPerSecond);
            return true;
        }
        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _vm.ScrollSeconds = Math.Max(0, _vm.ScrollSeconds - delta / 120.0 * (60 / _vm.PixelsPerSecond) * 4);
            return true;
        }
        if (!Modifiers.HasFlag(ModifierKeys.Alt)) return false;

        // From where the playhead is going: while playing, a seek can still be waiting for the audio
        // thread, and a tick can put the old position on screen meanwhile. Turned fast, notches built
        // on that would be lost.
        double notches = delta / (double)Mouse.MouseWheelDeltaForOneLine;
        double here = _vm.Session.Graph.TargetPositionFrames / (double)_vm.Session.SampleRate;
        double to = here + notches * ScrubPixelsPerNotch / Math.Max(0.01, _vm.PixelsPerSecond);
        if (_vm.TotalSeconds > 0) to = Math.Min(to, _vm.TotalSeconds);
        to = Math.Max(0, to);
        _vm.SeekTo(to);

        // Keep the playhead on screen while it is being turned along.
        double view = _vm.ViewportSeconds;
        if (view > 0)
        {
            if (to < _vm.ScrollSeconds) _vm.ScrollSeconds = Math.Max(0, to - view * 0.1);
            else if (to > _vm.ScrollSeconds + view) _vm.ScrollSeconds = to - view * 0.9;
        }
        return true;
    }
}
