using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Dingus.Services;

public static class NativeWindow
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_CAPTION_COLOR = 35;
    const int DWMWA_TEXT_COLOR = 36;

    /// <summary>
    /// Dark native title bar (Windows 10 20H1+). On Windows 11 it's tinted to match the navigation
    /// rail, a step lighter than the page so it stands out as a title bar.
    /// </summary>
    public static void ApplyDarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int caption = ThemeService.TitleBarColorRef(); // matches the navigation rail (theme's surface colour)
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            int text = 0x00F8F2EE;    // #EEF2F8
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            int border = 0x003B2B23;  // #232B3B
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch { }
    }

    /// <summary>Starts a native window drag (used by the borderless mini player).</summary>
    public static void BeginDrag(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2;
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        catch { }
    }
}
