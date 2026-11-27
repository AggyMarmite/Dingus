using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>What each computer uses to bring a WireGuard tunnel up and down (WireGuard for Windows, NetworkManager on Linux).</summary>
public interface IVpnBackend
{
    /// <summary>Null when it can be used; otherwise what's missing, in plain English.</summary>
    string Problem { get; }
    /// <summary>One-time set-up (it may ask for permission). True when ready.</summary>
    Task<bool> PrepareAsync();
    /// <summary>Brings the tunnel up with this WireGuard config (replacing any location already connected).</summary>
    Task<bool> UpAsync(string config, CancellationToken ct);
    Task DownAsync();
    /// <summary>Whether the tunnel is up as far as the computer knows.</summary>
    Task<bool> IsUpAsync();
}

/// <summary>
/// Dingus's own VPN for Windows and Linux, as on the Fire TV: WireGuard config files from your VPN company (one per
/// location), connect / switch / off, checked every 10 seconds, reconnected when it drops, and – when your playlist or a
/// channel stops working through a location – it keeps trying other locations by itself until one works.
/// Shared by both apps; each has a small backend that actually brings the tunnel up.
/// </summary>
public static partial class Vpn
{
    public enum State { Off, Connecting, On, Searching, Dropped }

    public record Location(string Name, string File);

    private static partial IVpnBackend CreateBackend();
    static IVpnBackend _backend;
    public static IVpnBackend Backend => _backend ??= CreateBackend();

    static AppSettings S => SettingsStore.Current;

    public static State Status { get; private set; } = State.Off;
    /// <summary>The location in use (or being tried).</summary>
    public static Location Current { get; private set; }
    /// <summary>A short line for the title bar and Settings ("UK London", "Trying Netherlands Amsterdam… · 7 tried so far").</summary>
    public static string Note { get; private set; } = "";

    /// <summary>Something changed (status, location, note). Raised on a background thread – screens move to their own.</summary>
    public static event Action Changed;
    /// <summary>Now connected to a (new) location that works: streams that stopped can start again.</summary>
    public static event Action Switched;

    static readonly SemaphoreSlim Gate = new(1, 1);
    static CancellationTokenSource _searchCts;
    static Timer _watch;

    // ------------------------------------------------------------------ locations (config files)

    public static string Folder { get { var d = Path.Combine(AppPaths.Root, "vpn"); Directory.CreateDirectory(d); return d; } }

    public static List<Location> Locations() =>
        Directory.GetFiles(Folder, "*.conf").Select(f => new Location(Pretty(Path.GetFileNameWithoutExtension(f)), f))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>"uk-london_wg" → "UK London".</summary>
    static string Pretty(string name)
    {
        var words = Regex.Split(name.Replace('_', ' ').Replace('-', ' '), @"\s+").Where(w => w.Length > 0 && !w.Equals("wg", StringComparison.OrdinalIgnoreCase)).ToList();
        return string.Join(" ", words.Select(w => w.Length <= 3 && w.All(char.IsLetter) && w.ToLowerInvariant() is "uk" or "us" or "usa" or "uae" ? w.ToUpperInvariant()
            : char.ToUpperInvariant(w[0]) + w[1..])).Trim() is { Length: > 0 } s ? s : name;
    }

    /// <summary>Copies WireGuard config files in (from Add files… or dropped on Settings). Returns how many were added.</summary>
    public static int AddFiles(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths)
        {
            try
            {
                var text = File.ReadAllText(p);
                if (!text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase)) continue;
                File.WriteAllText(Path.Combine(Folder, Path.GetFileName(p).EndsWith(".conf", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(p) : Path.GetFileName(p) + ".conf"), text);
                n++;
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        Raise();
        return n;
    }

    public static void RemoveLocation(Location l)
    {
        try { File.Delete(l.File); } catch (Exception ex) { Log.Error(ex); }
        Raise();
    }

    static string DnsFor(string choice) => choice switch
    {
        "cloudflare" => "1.1.1.1, 1.0.0.1",
        "google" => "8.8.8.8, 8.8.4.4",
        "quad9" => "9.9.9.9, 149.112.112.112",
        null or "" or "own" => null,
        var custom => custom,
    };

    /// <summary>The config to bring up: the location's file, with the DNS setting applied.</summary>
    static string ConfigFor(Location l)
    {
        var text = File.ReadAllText(l.File).Replace("\r\n", "\n");
        var dns = DnsFor(S.VpnDns);
        if (dns != null)
        {
            text = Regex.Replace(text, @"^\s*DNS\s*=.*$\n?", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"(\[Interface\][^\n]*\n)", $"$1DNS = {dns}\n", RegexOptions.IgnoreCase);
        }
        return text;
    }

    // ------------------------------------------------------------------ on, off, switching

    static void Set(State s, string note = null)
    {
        Status = s;
        Note = note ?? s switch
        {
            State.On => Current?.Name ?? "Connected",
            State.Connecting => $"Connecting to {Current?.Name}…",
            State.Searching => "Finding a location…",
            State.Dropped => "Reconnecting…",
            _ => "Off",
        };
        Raise();
    }

    static void Raise() { try { Changed?.Invoke(); } catch (Exception ex) { Log.Error(ex); } }

    /// <summary>Called when Dingus starts: connects if "Connect when Dingus starts" is on.</summary>
    public static void Startup()
    {
        _watch ??= new Timer(_ => _ = WatchAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        if (S.VpnOn && S.VpnConnectOnStart && Locations().Count > 0) _ = TurnOnAsync();
        else if (S.VpnOn) { S.VpnOn = false; SettingsStore.Save(); }
    }

    /// <summary>Turns the VPN on with the chosen location (or the first). Returns a problem to show, or null.</summary>
    public static async Task<string> TurnOnAsync(Location pick = null)
    {
        if (Backend.Problem is { } problem) return problem;
        var all = Locations();
        if (all.Count == 0) return "Add your VPN company's WireGuard config files first.";
        var loc = pick ?? all.FirstOrDefault(l => l.Name == S.VpnLocation) ?? all[0];
        S.VpnOn = true;
        S.VpnLocation = loc.Name;
        SettingsStore.Save();
        if (!await Backend.PrepareAsync()) { S.VpnOn = false; SettingsStore.Save(); Set(State.Off); return Backend.Problem ?? "The VPN couldn't be set up – permission wasn't given."; }
        _searchCts?.Cancel();
        var (ok, why) = await ConnectAsync(loc, checkPlaylist: true, CancellationToken.None);
        if (ok) return null;
        if (S.VpnAutoFind) { StartSearch(loc); return null; }
        return why;
    }

    public static async Task TurnOffAsync()
    {
        _searchCts?.Cancel();
        S.VpnOn = false;
        SettingsStore.Save();
        await Gate.WaitAsync();
        try { await Backend.DownAsync(); } catch (Exception ex) { Log.Error(ex); }
        finally { Gate.Release(); }
        Current = null;
        Set(State.Off);
        Switched?.Invoke();
    }

    /// <summary>Connects to one location and checks it really works (and that your playlist answers through it).</summary>
    static async Task<(bool Ok, string Why)> ConnectAsync(Location loc, bool checkPlaylist, CancellationToken ct, string note = null)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Current = loc;
            Set(note != null ? State.Searching : State.Connecting, note);
            if (!await Backend.UpAsync(ConfigFor(loc), ct)) return (false, $"{loc.Name} didn't connect");
            // Genuinely connected: the internet answers through it (only then does it say connected).
            if (!await AnswersAsync("https://connectivitycheck.gstatic.com/generate_204", 12, ct)) return (false, $"{loc.Name} connected but nothing gets through");
            if (checkPlaylist && !await PlaylistWorksAsync(ct)) return (false, $"{loc.Name} doesn't work with your playlist");
            S.VpnLocation = loc.Name;
            Remember(loc);
            SettingsStore.Save();
            Set(State.On);
            _ = Task.Run(async () => { await DataService.Instance.SignInNowAsync(); Switched?.Invoke(); });
            return (true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); return (false, $"{loc.Name}: {Http.Friendly(ex)}"); }
        finally { Gate.Release(); }
    }

    static async Task<bool> AnswersAsync(string url, int seconds, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var r = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { await Task.Delay(1500, ct); }
        }
        return false;
    }

    /// <summary>The playlist signs in through this location, and the last channel you watched (or another) starts.</summary>
    static async Task<bool> PlaylistWorksAsync(CancellationToken ct)
    {
        var ds = DataService.Instance;
        var p = ds.Active;
        if (p == null) return true;
        if (p.Type == PlaylistType.Xtream)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await new XtreamClient(p).LoginAsync(cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
        }
        // A channel actually starts: the last one watched, else one from the list.
        var chans = ds.Catalog.Channels;
        var ch = chans.FirstOrDefault(c => c.Id == S.LastChannelId) ?? (chans.Count > 0 ? chans[Random.Shared.Next(chans.Count)] : null);
        var url = ch == null ? null : ds.StreamUrl(ch);
        if (url == null) return true;
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.20 LibVLC/3.0.20");
            using var r = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)r.StatusCode;
            return code is >= 200 and < 400 || code == 404; // (a 404 is that one channel, not the location)
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    static void Remember(Location loc)
    {
        var l = S.VpnWorked;
        l.Remove(loc.Name);
        l.Insert(0, loc.Name);
        if (l.Count > 20) l.RemoveRange(20, l.Count - 20);
    }

    // ------------------------------------------------------------------ keeping it working

    static int _drops;
    static DateTime _nextTryUtc;

    /// <summary>Every 10 seconds: still up? If it dropped, reconnect (waiting a little longer each time).</summary>
    static async Task WatchAsync()
    {
        if (!S.VpnOn || Status is State.Connecting or State.Searching || Current == null) return;
        bool up;
        try { up = await Backend.IsUpAsync() && await AnswersAsync("https://connectivitycheck.gstatic.com/generate_204", 6, CancellationToken.None); }
        catch { up = false; }
        if (up) { if (Status != State.On) Set(State.On); _drops = 0; return; }
        if (DateTime.UtcNow < _nextTryUtc) { if (Status != State.Dropped) Set(State.Dropped); return; }
        int[] waits = { 0, 3, 10, 30, 60, 120 };
        _nextTryUtc = DateTime.UtcNow.AddSeconds(waits[Math.Min(_drops, waits.Length - 1)]);
        _drops++;
        Set(State.Dropped);
        var (ok, _) = await ConnectAsync(Current, checkPlaylist: false, CancellationToken.None);
        if (!ok && S.VpnAutoFind && _drops >= 3) StartSearch(Current);
    }

    /// <summary>The playlist or a stream isn't working through the VPN: look for a location that works (when that's on).</summary>
    public static void Blocked()
    {
        if (!S.VpnOn || !S.VpnAutoFind || Status == State.Searching) return;
        StartSearch(Current);
    }

    public static bool Searching => Status == State.Searching;

    /// <summary>Keeps trying other locations, a few at a time, until one works with the playlist (or the VPN's turned off).</summary>
    static void StartSearch(Location from)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            int tried = 0;
            var ct = cts.Token;
            try
            {
                while (!ct.IsCancellationRequested && S.VpnOn)
                {
                    var all = Locations();
                    // Ones that worked before first, then the rest; the one that just failed last.
                    var order = all.OrderBy(l => S.VpnWorked.IndexOf(l.Name) is var i && i >= 0 ? i : 1000)
                        .ThenBy(l => l.Name == from?.Name ? 1 : 0).ToList();
                    foreach (var loc in order)
                    {
                        if (ct.IsCancellationRequested || !S.VpnOn) return;
                        tried++;
                        var (ok, _) = await ConnectAsync(loc, checkPlaylist: true, ct, $"Trying {loc.Name}…  ·  {tried} tried so far");
                        if (ok) return;
                    }
                    // Tried them all: a minute's rest, then round again.
                    Set(State.Searching, $"None worked yet  ·  {tried} tried  ·  trying again shortly");
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error(ex); }
        });
    }

    /// <summary>The kill switch: nothing plays while the VPN is meant to be on but isn't connected.</summary>
    public static bool HoldStreams => S.VpnOn && S.VpnKillSwitch && Status != State.On;
}
