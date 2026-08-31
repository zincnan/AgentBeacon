using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AgentBeacon.Indicator;

/// <summary>
/// Applies the Win32 WS_EX_NOACTIVATE extended window style to a WPF
/// window's HWND so showing the window does not steal focus from the
/// foreground app. This is a thin helper around SetWindowLong — not an
/// abstraction. Used in addition to ShowActivated=False on the XAML side,
/// which on its own is not always sufficient for transient notification
/// surfaces.
/// </summary>
internal static class NoActivateHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void EnsureNoActivate(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
        {
            // HWND not created yet — SourceInitialized will fire before Show
            // returns and we'll hook there too. Most uses call this right
            // after Show() so Handle is valid.
            window.SourceInitialized += (_, _) => ApplyStyle(helper.Handle);
            return;
        }
        ApplyStyle(hwnd);
    }

    private static void ApplyStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var current = GetWindowLong(hwnd, GWL_EXSTYLE);
        var desired = current | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        if (desired != current)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, desired);
        }
    }
}
