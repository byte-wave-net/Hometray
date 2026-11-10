using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Hometray;

internal static class WindowHelper
{
    public static IntPtr Handle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    public static double GetScale(Window window) => GetDpiForWindow(Handle(window)) / 96.0;

    public static void BringToFront(Window window) => SetForegroundWindow(Handle(window));

    /// <summary>Windows 11 rounded corners also for windows without a title bar.</summary>
    public static void UseRoundedCorners(Window window)
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
        int pref = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(Handle(window), DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }
    }

    /// <summary>Color of the thin DWM window border (Windows 11).</summary>
    public static void SetBorderColor(Window window, byte r, byte g, byte b)
    {
        const int DWMWA_BORDER_COLOR = 34;
        int colorRef = r | (g << 8) | (b << 16); // COLORREF 0x00BBGGRR
        try { DwmSetWindowAttribute(Handle(window), DWMWA_BORDER_COLOR, ref colorRef, sizeof(int)); } catch { }
    }

    /// <summary>Moves the window with the mouse (for custom title bars): hand the drag to Windows.</summary>
    public static void BeginDrag(Window window)
    {
        const uint WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2;
        ReleaseCapture();
        SendMessageW(Handle(window), WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    public static void Launch(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    public static void SetAppIcon(Window window)
    {
        try { window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

