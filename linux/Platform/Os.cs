using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace Dingus.Services;

/// <summary>
/// The few things that differ between the Linux and Mac builds of the Avalonia app.
/// </summary>
public static class Os
{
    public static readonly bool Mac = OperatingSystem.IsMacOS();

    /// <summary>The modifier for shortcuts: ⌘ on a Mac, Ctrl on Linux.</summary>
    public static KeyModifiers Command => Mac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>True when the shortcut modifier (⌘ / Ctrl) is held.</summary>
    public static bool HasCommand(KeyModifiers m) => m.HasFlag(Command) || (!Mac && m.HasFlag(KeyModifiers.Control));

    /// <summary>Mac: "arm64" (Apple Silicon) or "x64" (Intel).</summary>
    public static string MacArch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>Mac: the Dingus.app bundle we're running from (null when run from a plain folder).</summary>
    public static string MacBundle
    {
        get
        {
            // …/Dingus.app/Contents/MacOS/dingus
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
            var app = dir.Parent?.Parent;
            return dir.Name == "MacOS" && app != null && app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app.FullName : null;
        }
    }

    /// <summary>Opens a web page, file or folder with the system's default app.</summary>
    public static void Open(string target)
    {
        try
        {
            var psi = new ProcessStartInfo(Mac ? "open" : "xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>Runs a command and waits for it; returns its exit code (-1 if it couldn't start).</summary>
    public static int Run(string file, string args, int timeoutMs = 120_000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return -1;
            p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch (Exception ex) { Log.Error(ex); return -1; }
    }
}
