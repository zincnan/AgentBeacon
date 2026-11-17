using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace AgentBeacon.Indicator;

/// <summary>Screen work areas and no-activate placement in physical pixels.</summary>
internal static class WindowScreenGeometry
{
    public static Rect GetWorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var screen = hwnd == IntPtr.Zero
            ? WinForms.Screen.PrimaryScreen
            : WinForms.Screen.FromHandle(hwnd);
        if (screen is null) throw new InvalidOperationException("No display available.");
        var work = screen.WorkingArea;
        return new Rect(work.Left, work.Top, work.Width, work.Height);
    }

    public static void Move(Window window, double left, double top)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // PerMonitorV2 HWNDs accept screen pixels, including negative origins.
        if (!SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top),
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
