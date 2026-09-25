using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Benchmix.App;

public partial class App : Application
{
    /// <summary>Files named on the command line, loaded once the window is up.</summary>
    public static string[] StartupFiles { get; private set; } = Array.Empty<string>();

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

        var files = new List<string>();
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--autoclose":
                    if (i + 1 < e.Args.Length && double.TryParse(e.Args[++i], out var s)) AutoCloseSeconds = s;
                    break;
                case "--uiprobe":
                    if (i + 1 < e.Args.Length) UiProbePath = e.Args[++i];
                    break;
                case "--loadcheck":
                    LoadCheck = true;
                    break;
                case "--gesturetest":
                    GestureTest = true;
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
                    if (i + 1 < e.Args.Length) StemTestFile = e.Args[++i];
                    break;
                case "--playtest":
                    if (i + 1 < e.Args.Length && double.TryParse(e.Args[++i], out var t)) PlayTestSeconds = t;
                    break;
                default:
                    if (File.Exists(e.Args[i])) files.Add(e.Args[i]);
                    break;
            }
        }
        StartupFiles = files.ToArray();
        Log($"startup files: {StartupFiles.Length}, autoclose: {AutoCloseSeconds}");

        base.OnStartup(e);
        Log("base.OnStartup returned");
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("FATAL (dispatcher): " + e.Exception);
        MessageBox.Show($"{e.Exception.GetType().Name}: {e.Exception.Message}\n\nDetails in {LogPath}",
                        "Benchmix hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
