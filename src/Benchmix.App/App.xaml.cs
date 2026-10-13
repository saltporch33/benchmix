using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Benchmix.App;

public partial class App : Application
{
    /// <summary>Files named on the command line, loaded once the window is up.</summary>
    public static string[] StartupFiles { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// A .benchmix project to open as the window comes up: Windows passes it when one is
    /// double-clicked in Explorer (Daniel, 2026-10-12). Until then it was taken for a song, and
    /// "could not load" it.
    /// </summary>
    public static string? StartupProject { get; private set; }

    /// <summary>Seconds to wait before closing, for scripted runs. Zero means stay open.</summary>
    public static double AutoCloseSeconds { get; private set; }

    /// <summary>
    /// When set, the window renders itself to this PNG and the app exits. Capturing from inside
    /// WPF via <c>RenderTargetBitmap</c> is the only reliable way to get the real pixels — screen
    /// grabs and <c>PrintWindow</c> both lose parts of a DWM-composited window, and a screen grab
    /// also means stealing focus from whoever is using the machine.
    /// </summary>
    public static string? UiProbePath { get; private set; }

    /// <summary>
    /// Seconds to run a silent playback check: master at zero, transport running, measuring how
    /// fast the audio device actually pulls frames from the mix. Silent on purpose — proving the
    /// pipeline runs should not mean blasting music at whoever is at the keyboard.
    /// </summary>
    public static double PlayTestSeconds { get; private set; }

    /// <summary>
    /// When set, separates 20 seconds of this file and logs the result, then exits. Proves the
    /// published single-file exe really carries the ONNX Runtime native libraries — something a
    /// plain launch never exercises, because nothing touches them until you split a track.
    /// </summary>
    public static string? StemTestFile { get; private set; }

    /// <summary>
    /// Logs whether each startup track ended up actually ready, then exits. Exists because a track
    /// can look fine on screen while the app still considers its audio unloaded, which silently
    /// blocks stem separation.
    /// </summary>
    public static bool LoadCheck { get; private set; }

    /// <summary>
    /// Drives the pointer gestures through WPF's real event routing and logs what happened, then
    /// exits. Right-drag panning has to suppress a context menu that belongs to a different
    /// element, and whether that works is a question about WPF's own class handlers rather than
    /// about our arithmetic — so it gets answered by running it, not by reasoning about it. No
    /// physical input is synthesised, so this never moves the pointer out from under whoever is
    /// using the machine.
    /// </summary>
    public static bool GestureTest { get; private set; }

    /// <summary>
    /// Renders a pitched track and an untouched one and logs how far apart they land, then exits.
    ///
    /// The compensation for the phase vocoder's latency reaches into the native library through a
    /// P/Invoke that nothing else uses, and a single-file self-contained exe is exactly where a new
    /// native entry point fails to resolve. Proving it in the tests proves it in the dev build; this
    /// proves it in the thing actually on Daniel's machine.
    /// </summary>
    public static bool LatencyCheck { get; private set; }

    /// <summary>
    /// Splits the first track into sections and selects one before taking a --uiprobe shot, so the
    /// section drawing can actually be looked at. None of the lane drawing is reachable from the
    /// test project, so a picture is the only evidence that it works.
    /// </summary>
    public static bool DemoSections { get; private set; }

    /// <summary>
    /// Sets up the 2026-09-24 editing features before a --uiprobe shot: an empty track, a section
    /// dragged onto it and looped, loop mode on. Like --demosections, a picture is the only
    /// evidence that the lanes draw these — none of the drawing is reachable from the tests.
    /// </summary>
    public static bool DemoEditing { get; private set; }

    /// <summary>
    /// Presses the real Match BPM, Reset and Match key buttons through their own automation peers
    /// and reads the real labels back, then exits: what scripts/ui-verify.ps1 did from outside with
    /// UI Automation. It moved inside on 2026-09-24 because the outside client made Windows
    /// activate the hidden window — measured, at its very first call — taking focus from whoever
    /// was working. From inside, nothing reaches across processes and nothing is activated.
    /// </summary>
    public static bool ButtonTest { get; private set; }

    /// <summary>
    /// True for a scripted run: any verification switch, or <c>--autoclose</c>. Someone is using
    /// this machine while those run, so they keep entirely out of the way — the window opens beyond
    /// the edge of every monitor, has no taskbar button, never takes focus, and never shows a
    /// dialog. (Minimising was not enough: a minimised window still has a taskbar button, and
    /// closing a scripted run with its unsaved project used to pop up "Discard them?" until a
    /// watchdog killed it two seconds later.)
    /// </summary>
    public static bool IsScriptedRun => UiProbePath is not null || PlayTestSeconds > 0 || StemTestFile is not null
                                      || LoadCheck || GestureTest || ButtonTest || LatencyCheck || AutoCloseSeconds > 0;

    /// <summary>
    /// Whether this run opens the audio device. Every scripted run but <c>--playtest</c>, whose
    /// whole point is the device, works without one: the transport state and the renderer are the
    /// same — only the playhead does not advance on its own — and nothing reaches the speakers or
    /// disturbs whatever else is playing through them.
    /// </summary>
    public static bool UseAudioDevice => !IsScriptedRun || PlayTestSeconds > 0;

    /// <summary>Where crashes and startup breadcrumbs go — written even when no window ever appears.</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Benchmix", "benchmix.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never be the thing that breaks the app */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Handlers first: a failure while parsing App.xaml or building the window has to be written
        // down too, otherwise all anyone gets is an exit code.
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log("FATAL (domain): " + (ex.ExceptionObject?.ToString() ?? "unknown"));

        Log($"=== start === args: {string.Join(" ", e.Args)}");

        // Strict on purpose. A switch whose value is missing or unreadable used to be skipped
        // quietly, and the run became an ordinary session: on screen, taking focus, on the real
        // audio device at full volume — the opposite of what a scripted run promises. Now it is
        // written down and the app exits before any window exists.
        var files = new List<string>();
        string? refused = null;
        string? missing = null;
        var args = e.Args;

        bool Seconds(ref int i, out double value)
        {
            value = 0;
            // Finite, positive, and short enough for a timer to hold. NaN in particular parses,
            // fails every comparison, and would have left the run an ordinary session.
            if (i + 1 >= args.Length
                || !double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out value)
                || !double.IsFinite(value) || value <= 0 || value > int.MaxValue / 1000.0) return false;
            i++;
            return true;
        }

        bool Value(ref int i, out string value)
        {
            value = "";
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return false;
            value = args[++i];
            return true;
        }

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--autoclose":
                    if (Seconds(ref i, out var s)) AutoCloseSeconds = s;
                    else refused ??= "--autoclose needs a number of seconds";
                    break;
                case "--uiprobe":
                    // The capture deletes whatever is at this path and writes a picture there, so it
                    // only ever takes a .png. With the arguments the wrong way round it would have
                    // been handed a song, and deleted it.
                    if (Value(ref i, out var u) && string.Equals(Path.GetExtension(u), ".png", StringComparison.OrdinalIgnoreCase))
                        UiProbePath = u;
                    else refused ??= "--uiprobe needs a .png path to write";
                    break;
                case "--loadcheck":
                    LoadCheck = true;
                    break;
                case "--gesturetest":
                    GestureTest = true;
                    break;
                case "--buttontest":
                    ButtonTest = true;
                    break;
                case "--latencycheck":
                    LatencyCheck = true;
                    break;
                case "--demosections":
                    DemoSections = true;
                    break;
                case "--demoediting":
                    DemoEditing = true;
                    break;
                case "--stemtest":
                    if (Value(ref i, out var f)) StemTestFile = f;
                    else refused ??= "--stemtest needs a song to separate";
                    break;
                case "--playtest":
                    if (Seconds(ref i, out var t)) PlayTestSeconds = t;
                    else refused ??= "--playtest needs a number of seconds";
                    break;
                default:
                    if (File.Exists(args[i]) && string.Equals(Path.GetExtension(args[i]), Benchmix.Core.Persistence.ProjectStore.Extension, StringComparison.OrdinalIgnoreCase))
                        StartupProject ??= args[i];            // the first project; a second cannot open beside it
                    else if (File.Exists(args[i])) files.Add(args[i]);
                    else if (args[i].StartsWith('-') || args[i].StartsWith('/')) refused ??= $"unknown switch {args[i]}";
                    else missing ??= args[i];
                    break;
            }
        }

        // The demo switches only set a --uiprobe shot up; alone they would quietly be an ordinary
        // session. And a scripted run given a song that is not there would pass having tested
        // nothing, so it is refused too. (An ordinary launch with a stale path just opens.)
        if ((DemoSections || DemoEditing) && UiProbePath is null)
            refused ??= "--demosections and --demoediting only work with --uiprobe";
        if (missing is not null && IsScriptedRun) refused ??= $"no such file: {missing}";

        if (refused is not null)
        {
            Log($"refused to start: {refused}. Nothing was opened.");
            Environment.Exit(1);
        }

        StartupFiles = files.ToArray();
        Log($"startup files: {StartupFiles.Length}{(StartupProject is null ? "" : $", project: {Path.GetFileName(StartupProject)}")}, autoclose: {AutoCloseSeconds}"
          + (IsScriptedRun ? $", scripted run: off-screen, no taskbar button, audio device: {(UseAudioDevice ? "yes" : "none")}" : ""));

        base.OnStartup(e);
        Log("base.OnStartup returned");
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("FATAL (dispatcher): " + e.Exception);
        if (IsScriptedRun)
        {
            // No dialog in a scripted run: it would land on the screen of whoever is working, and
            // it would sit there waiting for a click nobody is going to make. Fail the run instead.
            Log("scripted run: failed on an unhandled error, exit code 1");
            Environment.Exit(1);
        }
        MessageBox.Show($"{e.Exception.GetType().Name}: {e.Exception.Message}\n\nDetails in {LogPath}",
                        "Benchmix hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
