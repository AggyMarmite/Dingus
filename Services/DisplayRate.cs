using System.Runtime.InteropServices;

namespace Dingus.Services;

/// <summary>
/// Adaptive frame rate: while the full-screen player is open, switch the monitor's refresh rate to
/// a multiple of the video's frame rate (25/50 fps → 50 Hz, 23.976 → 48/120 Hz, 59.94 → 60 Hz),
/// so motion is smooth with no judder. The change is temporary – Windows puts the original rate
/// back when we call <see cref="Restore"/>, or automatically if the app closes or crashes.
/// </summary>
public static class DisplayRate
{
    static string _changedDevice;
    static readonly object Gate = new();

    /// <summary>The rate the display is currently switched to by us, or null.</summary>
    public static bool IsChanged { get { lock (Gate) return _changedDevice != null; } }

    /// <returns>The new refresh rate in Hz if the display was switched; null if no change was needed or possible.</returns>
    public static int? Match(double fps, IntPtr hwnd)
    {
        if (fps < 10 || fps > 125 || hwnd == IntPtr.Zero) return null;
        try
        {
            var device = DeviceFor(hwnd);
            if (device == null) return null;

            var cur = NewDevMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref cur)) return null;

            // Refresh rates this monitor offers at its current resolution and colour depth.
            var rates = new SortedSet<int>();
            var dm = NewDevMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
                if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight &&
                    dm.dmBitsPerPel == cur.dmBitsPerPel && dm.dmDisplayFrequency > 1)
                    rates.Add(dm.dmDisplayFrequency);

            int? best = Pick(fps, rates);
            if (best == null || best == cur.dmDisplayFrequency) return null;

            var target = cur;
            target.dmDisplayFrequency = best.Value;
            target.dmFields = DM_DISPLAYFREQUENCY;
            // CDS_FULLSCREEN = temporary: never written to the registry.
            if (ChangeDisplaySettingsEx(device, ref target, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero) != DISP_CHANGE_SUCCESSFUL)
                return null;
            lock (Gate) _changedDevice = device;
            return best;
        }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    /// <summary>Puts the display back to its normal refresh rate (no-op if we didn't change it).</summary>
    public static void Restore()
    {
        string device;
        lock (Gate) { device = _changedDevice; _changedDevice = null; }
        if (device == null) return;
        try { ChangeDisplaySettingsEx(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>Lowest offered rate (≥ 48 Hz where possible) that's a whole multiple of the frame rate.</summary>
    static int? Pick(double fps, SortedSet<int> rates)
    {
        int? best = null;
        foreach (var r in rates)
        {
            if (r < 23 || r > 165) continue;
            for (int k = 1; k <= 6; k++)
            {
                // Windows lists 59.94 Hz as 59 and 23.976 Hz as 23, so allow just under 1 Hz of difference.
                if (Math.Abs(r - k * fps) < 0.99)
                {
                    if (best == null || (best < 48 && r >= 48)) best = r;
                    break;
                }
            }
            if (best >= 48) break; // rates are ascending: the first good one ≥ 48 Hz wins
        }
        return best;
    }

    static string DeviceFor(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) ? mi.szDevice : null;
    }

    static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    // ------------------------------------------------------------------ Win32

    const int ENUM_CURRENT_SETTINGS = -1;
    const int DM_DISPLAYFREQUENCY = 0x400000;
    const int CDS_FULLSCREEN = 0x4;
    const int DISP_CHANGE_SUCCESSFUL = 0;
    const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr devMode, IntPtr hwnd, int flags, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
}
