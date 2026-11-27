using System.Diagnostics;

namespace Dingus.Services;

public static partial class Vpn
{
    static partial IVpnBackend CreateBackend() => new LinuxVpn();
}

/// <summary>
/// The VPN on Linux, through NetworkManager (nmcli), which most desktops have: Dingus keeps one WireGuard connection
/// called "dingus" and switches location by replacing it. Your desktop may ask for your password the first time.
/// </summary>
sealed class LinuxVpn : IVpnBackend
{
    const string Name = "dingus";

    static string Nmcli => new[] { "/usr/bin/nmcli", "/bin/nmcli", "/usr/local/bin/nmcli" }.FirstOrDefault(File.Exists);

    public string Problem => Os.Mac ? "The built-in VPN isn't available on Mac yet."
        : Nmcli == null ? "The VPN needs NetworkManager (nmcli), which most Linux desktops have – install it with your package manager, then try again." : null;

    static async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(Nmcli ?? "nmcli") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) Log.Info($"nmcli {string.Join(' ', args)}: {stderr.Trim()}");
        return (p.ExitCode, stdout + stderr);
    }

    public Task<bool> PrepareAsync() => Task.FromResult(Problem == null);

    public async Task<bool> UpAsync(string config, CancellationToken ct)
    {
        if (Problem != null) return false;
        var dir = Path.Combine(Path.GetTempPath(), "dingus-vpn");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Name + ".conf"); // (the file's name is the connection's name)
        await File.WriteAllTextAsync(file, config, ct);
        try
        {
            await RunAsync("connection", "down", Name);
            await RunAsync("connection", "delete", Name);
            var (imp, _) = await RunAsync("connection", "import", "type", "wireguard", "file", file);
            if (imp != 0) return false;
            await RunAsync("connection", "modify", Name, "connection.autoconnect", "no");
            var (up, _) = await RunAsync("connection", "up", Name);
            return up == 0;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    public async Task DownAsync()
    {
        if (Problem != null) return;
        await RunAsync("connection", "down", Name);
    }

    public async Task<bool> IsUpAsync()
    {
        if (Problem != null) return false;
        var (_, outp) = await RunAsync("-t", "-f", "GENERAL.STATE", "connection", "show", "--active", Name);
        return outp.Contains("activated", StringComparison.OrdinalIgnoreCase);
    }
}
