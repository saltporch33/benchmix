// Starts a Benchmix process quietly (QuietProcess) and watches it from the outside (IntrusionWatch),
// recording whether it ever got in the way of
// whoever is using the machine. Loaded by scripts\verify-exe.ps1 and scripts\ui-verify.ps1 with
// Add-Type -Path, so it is written for the C# 5 compiler Windows PowerShell 5.1 ships with.
//
// It asks Windows the same questions Windows asks itself: whose window is in the foreground, which
// of the process's visible windows are on a monitor, which would get a taskbar button, and which
// could be activated. The app makes the same checks from inside (OutOfTheWay.cs); this is the
// independent half.
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/// <summary>
/// Starts a process without the "working in background" pointer. An ordinary launch of a GUI
/// program puts the arrow-and-spinner on whoever's pointer it is, wherever it is, until the new
/// program's first message loop — a second or so for a compressed single-file exe, every run.
/// STARTF_FORCEOFFFEEDBACK turns that off. Holds the process handle from creation, so the exit
/// code can always be read, however quickly the process ends.
/// </summary>
public sealed class QuietProcess : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
                                      uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const int STARTF_FORCEOFFFEEDBACK = 0x80;
    IntPtr _process;

    public int Id { get; private set; }

    public QuietProcess(string exe, string arguments)
    {
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.dwFlags = STARTF_FORCEOFFFEEDBACK;
        var commandLine = new StringBuilder("\"" + exe + "\" " + arguments);
        PROCESS_INFORMATION pi;
        if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hThread);
        _process = pi.hProcess;
        Id = pi.dwProcessId;
    }

    public bool WaitForExit(int milliseconds) { return WaitForSingleObject(_process, (uint)milliseconds) == 0; }
    public bool HasExited { get { return WaitForSingleObject(_process, 0) == 0; } }
    public int ExitCode { get { uint code; GetExitCodeProcess(_process, out code); return (int)code; } }
    public void Kill() { TerminateProcess(_process, 1); WaitForSingleObject(_process, 5000); }

    public void Dispose()
    {
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
    }
}

public static class Intrusion
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_APPWINDOW = 0x00040000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const uint GW_OWNER = 4;
    const uint MONITOR_DEFAULTTONULL = 0;

    public static bool OwnsForeground(int pid)
    {
        uint p;
        GetWindowThreadProcessId(GetForegroundWindow(), out p);
        return p == (uint)pid;
    }

    /// <summary>Checks every visible window of the process.</summary>
    public static void Inspect(int pid, out bool onScreen, out bool taskbarButton, out bool activatable)
    {
        bool a = false, b = false, c = false;
        EnumProc visit = delegate (IntPtr h, IntPtr l)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != (uint)pid || !IsWindowVisible(h)) return true;

            RECT r;
            if (GetWindowRect(h, out r) && MonitorFromRect(ref r, MONITOR_DEFAULTTONULL) != IntPtr.Zero) a = true;

            int ex = GetWindowLong(h, GWL_EXSTYLE);
            bool owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
            if ((ex & WS_EX_APPWINDOW) != 0 || (!owned && (ex & WS_EX_TOOLWINDOW) == 0)) b = true;
            if ((ex & WS_EX_NOACTIVATE) == 0) c = true;
            return true;
        };
        EnumWindows(visit, IntPtr.Zero);
        GC.KeepAlive(visit);
        onScreen = a;
        taskbarButton = b;
        activatable = c;
    }
}

/// <summary>
/// Samples one process on its own thread until stopped, so a script blocked in a UI Automation
/// call or a sleep does not leave gaps in the watch.
/// </summary>
public sealed class IntrusionWatch
{
    readonly int _pid;
    readonly Thread _thread;
    volatile bool _stop;

    public volatile bool TookFocus;
    public volatile bool OnScreen;
    public volatile bool TaskbarButton;
    public volatile bool Activatable;
    public int Samples;

    public IntrusionWatch(int pid)
    {
        _pid = pid;
        _thread = new Thread(Run);
        _thread.IsBackground = true;
        _thread.Start();
    }

    void Run()
    {
        while (!_stop)
        {
            if (Intrusion.OwnsForeground(_pid)) TookFocus = true;
            bool a, b, c;
            Intrusion.Inspect(_pid, out a, out b, out c);
            if (a) OnScreen = true;
            if (b) TaskbarButton = true;
            if (c) Activatable = true;
            Interlocked.Increment(ref Samples);
            Thread.Sleep(5);
        }
    }

    public void Stop()
    {
        _stop = true;
        _thread.Join(2000);
    }

    public bool GotInTheWay { get { return TookFocus || OnScreen || TaskbarButton || Activatable; } }

    public string Describe()
    {
        return string.Format("focus taken: {0}  on screen: {1}  taskbar button: {2}  activatable: {3}  ({4} samples)",
            TookFocus ? "YES" : "no", OnScreen ? "YES" : "no", TaskbarButton ? "YES" : "no",
            Activatable ? "YES" : "no", Samples);
    }
}
