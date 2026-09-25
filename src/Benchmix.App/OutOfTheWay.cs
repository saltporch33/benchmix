using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Benchmix.App;

/// <summary>
/// Keeps a scripted run's window where it cannot interrupt whoever is using the machine, and
/// measures whether it did — from the window handle, in physical pixels, by the same rules Windows
/// itself uses. Reading back WPF properties the app had just set proved nothing; these read what
/// Windows actually did with the window.
/// </summary>
internal static class OutOfTheWay
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const uint MONITOR_DEFAULTTONULL = 0;

    // The 32-bit forms, deliberately: the extended style is 32 bits wide, and these exist on
    // every architecture, where the ...Ptr forms are 64-bit only.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect32 rect, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    private static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    /// <summary>
    /// Marks the window as one Windows never activates — not when it is shown, and not when the
    /// person at the machine closes or minimises the window they were using, which is when Windows
    /// hands activation to the next window in line. Without this the hidden window could be
    /// chosen, and their typing would go into it: Ctrl+S opening a save dialog on their screen.
    /// Called from SourceInitialized, after the handle exists and before WPF first shows it.
    /// </summary>
    public static void NeverActivate(Window w)
    {
        var h = Handle(w);
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    /// <summary>True when Windows could activate the window — the never-activate style is missing.</summary>
    public static bool CanBeActivated(Window w) => (GetWindowLong(Handle(w), GWL_EXSTYLE) & WS_EX_NOACTIVATE) == 0;

    /// <summary>
    /// Whether Windows gives the window a taskbar button: a visible window gets one if it asks for
    /// one outright, or if it has no owner and is not a tool window.
    /// </summary>
    public static bool HasTaskbarButton(Window w)
    {
        var h = Handle(w);
        if (!IsWindowVisible(h)) return false;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        bool owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
        return (ex & WS_EX_APPWINDOW) != 0 || (!owned && (ex & WS_EX_TOOLWINDOW) == 0);
    }

    /// <summary>True when any part of the window is on any monitor, asked of Windows directly.</summary>
    public static bool IsOnAnyMonitor(Window w)
    {
        var h = Handle(w);
        return GetWindowRect(h, out var r) && MonitorFromRect(ref r, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }
}
