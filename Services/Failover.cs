using System.IO;
using System.Net.Http;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Backup server addresses for a playlist. A refresh tries the main address first, then each backup in order, and
/// uses the first that answers; streams then play from that one until the next refresh (which tries the main address
/// first again, so it goes back by itself). The same login is used on every address; for an M3U playlist the backups
/// are alternative links.
/// </summary>
public static class Failover
{
    /// <summary>The main address, then the backups.</summary>
    public static List<string> Addresses(PlaylistConfig p)
    {
        var all = new List<string> { p.Type == PlaylistType.Xtream ? p.Server : p.M3uUrl };
        all.AddRange(p.BackupServers.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        return all;
    }

    public static bool HasBackups(PlaylistConfig p) => p != null && p.BackupServers.Any(a => !string.IsNullOrWhiteSpace(a));

    /// <summary>Which address is in use: 0 = the main one.</summary>
    public static int Index(PlaylistConfig p) => p.ActiveServerIndex >= 0 && p.ActiveServerIndex < Addresses(p).Count ? p.ActiveServerIndex : 0;

    /// <summary>The address in use now.</summary>
    public static string Active(PlaylistConfig p) => Addresses(p)[Index(p)];

    /// <summary>"Using backup server 1" when a backup is in use, else null.</summary>
    public static string Status(PlaylistConfig p) => p != null && Index(p) > 0 ? $"Using backup server {Index(p)}" : null;

    /// <summary>Stream addresses made before a switch still carry the old server: this swaps in the one in use.</summary>
    public static string Rewrite(PlaylistConfig p, string url)
    {
        if (p == null || url == null || p.Type != PlaylistType.Xtream || !HasBackups(p)) return url;
        var now = XtreamClient.NormalizeServer(Active(p));
        foreach (var a in Addresses(p))
        {
            var b = XtreamClient.NormalizeServer(a);
            if (!string.Equals(b, now, StringComparison.OrdinalIgnoreCase) && url.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase))
                return now + url.Substring(b.Length);
        }
        return url;
    }

    /// <summary>Whether the error means the server can't be reached (as opposed to a wrong login or a bad playlist).</summary>
    public static bool IsDown(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode == null || (int)h.StatusCode >= 500 || (int)h.StatusCode == 404,
        TaskCanceledException => true,   // a timeout (the caller has already ruled out the user cancelling)
        TimeoutException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        _ => false,
    };

    /// <summary>The playlist window's box: one address per line (blank lines and repeats dropped).</summary>
    public static List<string> ParseList(string text, string main) =>
        (text ?? "").Split('\n', '\r').Select(a => a.Trim()).Where(a => a.Length > 0)
            .Where(a => !string.Equals(a, main?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The Save check for an Xtream login: the main server, or else the first backup that answers (a refused login stops at once).</summary>
    public static async Task SignInAsync(string server, IList<string> backups, string user, string pass, CancellationToken ct)
    {
        Exception first = null;
        foreach (var a in new[] { server }.Concat(backups ?? Array.Empty<string>()))
        {
            try { await new XtreamClient(a, user, pass).LoginAsync(ct); return; }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsDown(ex)) { first ??= ex; }
        }
        throw first ?? new HttpRequestException("Couldn't connect to the server");
    }

    /// <summary>A quick "is anyone there?": any answer counts, even a refused login (that's the login's problem, not the server's).</summary>
    static async Task<bool> ReachableAsync(PlaylistConfig p, string address, CancellationToken ct)
    {
        string url;
        if (p.Type == PlaylistType.Xtream)
            url = XtreamClient.NormalizeServer(address) + "/player_api.php?username=" + Uri.EscapeDataString(p.Username ?? "") + "&password=" + Uri.EscapeDataString(p.Password ?? "");
        else
        {
            url = (address ?? "").Trim();
            if (File.Exists(url)) return true;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    /// <summary>Runs <paramref name="load"/> with the main address, then each backup (skipping ahead only when the server can't be reached).</summary>
    public static async Task<T> LoadAsync<T>(PlaylistConfig p, Func<Task<T>> load, CancellationToken ct)
    {
        var all = Addresses(p);
        if (all.Count == 1) return await load();
        int original = p.ActiveServerIndex;
        Exception first = null;
        for (int i = 0; i < all.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(all[i])) continue;
            if (!await ReachableAsync(p, all[i], ct)) { first ??= new HttpRequestException("Couldn't connect to the server"); continue; }
            p.ActiveServerIndex = i;
            try
            {
                var r = await load();
                if (i != original) SettingsStore.Save();
                return r;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsDown(ex)) { first ??= ex; }
        }
        p.ActiveServerIndex = original;
        throw first ?? new HttpRequestException("Couldn't connect to the server");
    }

    /// <summary>A stream won't start because the server is unreachable: moves to the next address that answers. True if it moved.</summary>
    public static async Task<bool> RecoverAsync(PlaylistConfig p, CancellationToken ct)
    {
        var all = Addresses(p);
        if (all.Count < 2) return false;
        int cur = Index(p);
        for (int step = 1; step < all.Count; step++)
        {
            int i = (cur + step) % all.Count;
            if (string.IsNullOrWhiteSpace(all[i]) || !await ReachableAsync(p, all[i], ct)) continue;
            p.ActiveServerIndex = i;
            SettingsStore.Save();
            return true;
        }
        return false;
    }
}
