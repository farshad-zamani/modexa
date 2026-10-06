using System.Runtime.InteropServices;

namespace Modexa.App;

/// <summary>Native helpers for the chromeless (WindowChrome) main window.</summary>
internal static class NativeMethods
{
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    /// <summary>
    /// How far (in device pixels) a maximized window extends past its monitor's work area on each
    /// side. Windows maximizes a resizable window to the work area plus its resize frame, so a
    /// custom-chrome window must inset its content by this much.
    /// </summary>
    internal static (int Left, int Top, int Right, int Bottom) GetMaximizedOverhang(IntPtr hwnd)
    {
        try
        {
            if (!GetWindowRect(hwnd, out var win)) return default;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return default;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return default;

            var work = mi.rcWork;
            return (Math.Max(0, work.Left - win.Left),
                    Math.Max(0, work.Top - win.Top),
                    Math.Max(0, win.Right - work.Right),
                    Math.Max(0, win.Bottom - work.Bottom));
        }
        catch
        {
            return default;
        }
    }
}
