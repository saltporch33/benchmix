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
