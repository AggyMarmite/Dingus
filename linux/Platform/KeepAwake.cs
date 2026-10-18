using System.Diagnostics;

namespace Dingus.Services;

/// <summary>
/// Stops the screen blanking and the computer sleeping while something plays (and the computer sleeping
/// while a recording runs). Linux: systemd-inhibit, the standard way on Linux desktops. Mac: caffeinate.
/// </summary>
public static class KeepAwake
{
    static Process _watch, _record;

    public static void Set(bool on) => Toggle(ref _watch, on, "idle:sleep", "Watching TV", "-di");
    public static void SetRecording(bool on) => Toggle(ref _record, on, "sleep", "Recording", "-i");

    static void Toggle(ref Process p, bool on, string what, string why, string macFlags)
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
            var psi = Os.Mac
                // -w: also ends by itself if Dingus quits unexpectedly
                ? new ProcessStartInfo("caffeinate", $"{macFlags} -w {Environment.ProcessId}")
                : new ProcessStartInfo("systemd-inhibit", $"--what={what} --who=Dingus --why=\"{why}\" --mode=block sleep infinity");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
            p = Process.Start(psi);
        }
        catch { p = null; } // not every Linux system has systemd – nothing else to do
    }
}

/// <summary>Kept for the shared recording code.</summary>
public static class PowerService
{
    public static event Action Changed { add { } remove { } }
    public static bool OnBattery => false;
}
