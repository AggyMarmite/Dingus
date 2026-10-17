using System.Diagnostics;

namespace Dingus.Services;

/// <summary>
/// Stops the screen blanking and the computer sleeping while something plays (and the computer sleeping
/// while a recording runs), using systemd-inhibit – the standard way on Linux desktops.
/// </summary>
public static class KeepAwake
{
    static Process _watch, _record;

    public static void Set(bool on) => Toggle(ref _watch, on, "idle:sleep", "Watching TV");
    public static void SetRecording(bool on) => Toggle(ref _record, on, "sleep", "Recording");

    static void Toggle(ref Process p, bool on, string what, string why)
    {
        if (on == (p != null && !p.HasExited)) return;
        if (!on)
        {
            try { p?.Kill(); } catch { }
            p = null;
            return;
        }
        try
        {
            p = Process.Start(new ProcessStartInfo("systemd-inhibit", $"--what={what} --who=Dingus --why=\"{why}\" --mode=block sleep infinity")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            });
        }
        catch { p = null; } // not every system has systemd – nothing else to do
    }
}

/// <summary>Kept for the shared recording code.</summary>
public static class PowerService
{
    public static event Action Changed { add { } remove { } }
    public static bool OnBattery => false;
}
