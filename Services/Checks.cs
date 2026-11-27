using System.Diagnostics;
using System.Net.Http;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Settings → Help → Check everything, as on the Fire TV: tests each part of Dingus in turn and says, in plain English,
/// how to fix anything that isn't working. Shared by Windows and Linux; the screens only list the results.
/// </summary>
public static class Checks
{
    public enum State { Ok, Warn, Fail }
    public record Result(string Title, string Detail, State State);

    /// <summary>Runs every check, reporting each result as it's ready. Never throws.</summary>
    public static async Task RunAsync(Action<Result> add, CancellationToken ct = default)
    {
        var ds = DataService.Instance;
        var p = ds.Active;

        // Internet
        bool online = false;
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var r = await c.GetAsync("https://connectivitycheck.gstatic.com/generate_204", ct);
            online = (int)r.StatusCode is 204 or 200;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { }
        add(online ? new Result("Internet", "Connected", State.Ok)
                   : new Result("Internet", "Not connected – check your Wi-Fi or cable, then check again", State.Fail));
        if (p == null) { add(new Result("Playlist", "No playlist yet – add one in Settings → Playlists", State.Fail)); return; }

        // Playlist login and server
        if (p.Type == PlaylistType.Xtream)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                var acc = await new XtreamClient(p).LoginAsync(cts.Token);
                long ms = sw.ElapsedMilliseconds;
                ds.Catalog.Account = acc;
                var expires = acc.ExpiresUtc is { } e
                    ? $"Expires {e.ToLocalTime():d MMM yyyy} (in {Math.Max(0, (int)(e - DateTime.UtcNow).TotalDays)} days)"
                    : "No expiry date";
                bool expired = acc.ExpiresUtc is { } x && x < DateTime.UtcNow;
                add(new Result("Playlist login", $"{p.Name}  ·  {expires}", expired ? State.Fail : acc.ExpiresUtc is { } soon && soon - DateTime.UtcNow < TimeSpan.FromDays(7) ? State.Warn : State.Ok));
                add(new Result("Server", $"{(Failover.Index(p) == 0 ? "Main server" : Failover.Status(p))}  ·  {ms} ms",
                    ms > 3000 ? State.Warn : State.Ok));
                if (acc.MaxConnections > 0)
                    add(acc.ActiveConnections >= acc.MaxConnections
                        ? new Result("Connections", $"{acc.ActiveConnections} of {acc.MaxConnections} in use  ·  close Dingus on another device, or wait a minute for a dropped one to clear", State.Warn)
                        : new Result("Connections", $"{acc.ActiveConnections} of {acc.MaxConnections} in use", State.Ok));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                bool refused = Failover.Refused(ex);
                add(new Result("Playlist login", refused
                    ? "The server refused the login – check the username and password in Settings → Playlists (or that your subscription is active)"
                    : "Couldn't reach the server – " + Http.Friendly(ex) + (Failover.HasBackups(p) ? "" : ". Adding a backup server address in the playlist's settings can help"), State.Fail));
            }
        }
        else
        {
            add(ds.Catalog.Channels.Count > 0
                ? new Result("Playlist", $"{p.Name}  ·  {ds.Catalog.Channels.Count:N0} channels", State.Ok)
                : new Result("Playlist", "No channels loaded – check the playlist address in Settings → Playlists, then refresh", State.Fail));
        }

        // TV guide
        var epg = ds.Epg;
        int channels = ds.Catalog.Channels.Count;
        var age = DateTime.UtcNow - p.LastEpgRefreshUtc;
        string when = p.LastEpgRefreshUtc == default ? "never updated" : age < TimeSpan.FromHours(1) ? "updated within the hour" : age < TimeSpan.FromDays(1) ? $"updated {(int)age.TotalHours} h ago" : $"updated {(int)age.TotalDays} days ago";
        add(epg.MatchedChannels == 0
            ? new Result("TV guide", "No listings – add a guide address in Settings → TV guide, or refresh it", State.Warn)
            : new Result("TV guide", $"{epg.MatchedChannels:N0} of {channels:N0} channels have listings  ·  {when}", age > TimeSpan.FromDays(2) ? State.Warn : State.Ok));

        // Films and shows
        int films = ds.Catalog.Movies.Count, shows = ds.Catalog.Series.Count;
        add(films + shows > 0
            ? new Result("Films and TV shows", $"{films:N0} films  ·  {shows:N0} shows", State.Ok)
            : new Result("Films and TV shows", "None in your playlist (or not loaded yet – try Refresh playlist)", State.Warn));

        // Film database (logos, ratings, cast)
        add(Tmdb.Enabled
            ? new Result("Film details", "Title logos, ratings and cast are on", State.Ok)
            : new Result("Film details", SettingsStore.Current.OnlineArtwork ? "Not available in this version" : "Off – turn on online artwork in Settings to get title logos, ratings and cast", State.Warn));

        // VPN
        if (SettingsStore.Current.VpnOn)
            add(Vpn.Status switch
            {
                Vpn.State.On => new Result("VPN", $"Connected  ·  {Vpn.Note}", State.Ok),
                Vpn.State.Searching => new Result("VPN", $"{Vpn.Note}  ·  it carries on by itself until one works", State.Warn),
                _ => new Result("VPN", $"Not connected ({Vpn.Note})  ·  try another location in Settings → VPN", State.Fail),
            });
        else if (Vpn.Locations().Count > 0)
            add(new Result("VPN", "Off", State.Ok));

        // Updates
        try
        {
            var latest = await Updater.LatestAsync();
            add(latest != null && Updater.IsNewer(latest.Version)
                ? new Result("Updates", $"Version {latest.Version.ToString(3)} is available – Settings → About → Check for updates", State.Warn)
                : new Result("Updates", $"Up to date (version {Updater.Current.ToString(3)})", State.Ok));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { add(new Result("Updates", "Couldn't check for updates right now", State.Warn)); }
    }
}
