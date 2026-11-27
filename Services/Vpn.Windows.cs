using System.Diagnostics;
using System.ServiceProcess;

namespace Dingus.Services;

public static partial class Vpn
{
    static partial IVpnBackend CreateBackend() => new WindowsVpn();
}

/// <summary>
/// The VPN on Windows, through WireGuard for Windows (WireGuard's own app): Dingus sets up one tunnel called "Dingus"
/// the first time (one "allow changes" prompt), and from then on switches location by rewriting its config and
/// restarting it – no more prompts.
/// </summary>
sealed class WindowsVpn : IVpnBackend
{
    const string Tunnel = "Dingus";
    static string ServiceName => "WireGuardTunnel$" + Tunnel;
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dingus", "vpn");
    static string ConfPath => Path.Combine(Dir, Tunnel + ".conf");

    static string WireGuardExe
    {
        get
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var p = Path.Combine(root, "WireGuard", "wireguard.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public string Problem => WireGuardExe == null
        ? "The VPN uses WireGuard's free app for Windows – install it from wireguard.com (Dingus offers to), then turn the VPN on again."
        : null;

    static bool Installed()
    {
        try { using var sc = new ServiceController(ServiceName); _ = sc.Status; return true; }
        catch { return false; }
    }

    public async Task<bool> PrepareAsync()
    {
        if (WireGuardExe == null) return false;
        if (Installed() && File.Exists(ConfPath)) return true;
        // One elevated step: a folder Dingus can write the config to, the tunnel, and permission for Dingus to start
        // and stop it from then on (so switching location never asks again).
        Directory.CreateDirectory(Path.GetTempPath());
        var placeholder = Path.Combine(Path.GetTempPath(), "dingus-vpn-first.conf");
        var first = Vpn.Locations().FirstOrDefault();
        File.WriteAllText(placeholder, first != null ? File.ReadAllText(first.File) : "");
        var script = Path.Combine(Path.GetTempPath(), "dingus-vpn-setup.cmd");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            $"mkdir \"{Dir}\" 2>nul",
            $"copy /y \"{placeholder}\" \"{ConfPath}\" >nul",
            $"icacls \"{Dir}\" /grant *S-1-5-32-545:(OI)(CI)M /T >nul",
            $"\"{WireGuardExe}\" /installtunnelservice \"{ConfPath}\"",
            "timeout /t 3 /nobreak >nul",
            $"sc sdset \"{ServiceName}\" D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;CCLCSWRPWPDTLOCRRC;;;AU) >nul",
            $"sc stop \"{ServiceName}\" >nul",
            ""));
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return false;
            await p.WaitForExitAsync();
        }
        catch (Exception ex) { Log.Error(ex); return false; } // (permission not given)
        finally { try { File.Delete(placeholder); File.Delete(script); } catch { } }
        return Installed();
    }

    public async Task<bool> UpAsync(string config, CancellationToken ct)
    {
        if (!Installed() && !await PrepareAsync()) return false;
        using var sc = new ServiceController(ServiceName);
        await StopAsync(sc, ct);
        File.WriteAllText(ConfPath, config);
        sc.Start();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)), ct);
        sc.Refresh();
        return sc.Status == ServiceControllerStatus.Running;
    }

    static async Task StopAsync(ServiceController sc, CancellationToken ct)
    {
        sc.Refresh();
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)), ct);
    }

    public async Task DownAsync()
    {
        if (!Installed()) return;
        using var sc = new ServiceController(ServiceName);
        try { await StopAsync(sc, CancellationToken.None); } catch (Exception ex) { Log.Error(ex); }
    }

    public Task<bool> IsUpAsync()
    {
        try { using var sc = new ServiceController(ServiceName); return Task.FromResult(sc.Status == ServiceControllerStatus.Running); }
        catch { return Task.FromResult(false); }
    }
}
