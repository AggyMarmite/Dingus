using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace Dingus.Services;

public class PlexServer
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string AccessToken { get; init; }
    public bool Owned { get; init; }
    public List<(string Uri, bool Local, bool Relay)> Connections { get; init; } = new();
    /// <summary>The address that answered (set by <see cref="Plex.ConnectAsync"/>).</summary>
    public string BaseUrl { get; set; }
}

public class PlexLibrary
{
    public string Key { get; init; }
    public string Title { get; init; }
    public string Type { get; init; } // movie | show
}

/// <summary>A film, show, season or episode on your Plex server.</summary>
public class PlexItem
{
    public string RatingKey { get; init; }
    public string Type { get; init; } // movie | show | season | episode
    public string Title { get; init; }
    public string Year { get; init; }
    public string Summary { get; init; }
    public string Thumb { get; init; }
    public string Art { get; init; }
    public string ShowTitle { get; init; }      // grandparentTitle (episodes)
    public string ShowThumb { get; init; }      // grandparentThumb / parentThumb
    public string ShowKey { get; init; }        // grandparentRatingKey
    public int Season { get; init; }            // parentIndex
    public int Index { get; init; }             // episode / season number
    public long DurationMs { get; init; }
    public long ViewOffsetMs { get; init; }
    public int ViewCount { get; init; }
    public int LeafCount { get; init; }
    public int ViewedLeafCount { get; init; }
    public string PartKey { get; init; }
    public long AddedAt { get; init; }

    public bool Watched => Type is "movie" or "episode" ? ViewCount > 0 && ViewOffsetMs == 0 : LeafCount > 0 && ViewedLeafCount >= LeafCount;
    public double Progress => DurationMs > 0 && ViewOffsetMs > 0 ? Math.Clamp((double)ViewOffsetMs / DurationMs, 0, 1) : 0;
    public bool HasProgress => Progress > 0.01;

    // For the poster templates.
    public string Image { get; set; }
    public string Subtitle => Type switch
    {
        "episode" => $"{ShowTitle} · S{Season} E{Index}",
        "show" => LeafCount > 0 ? $"{LeafCount} episodes" : Year,
        "season" => LeafCount > 0 ? $"{LeafCount} episodes" : "",
        _ => Year,
    };
}

/// <summary>
/// Plex: sign in with a PIN at plex.tv/link (no password typed into Dingus), find your server, browse
/// films and shows, play them directly from the server, and keep watch progress in sync with Plex.
/// </summary>
public static class Plex
{
    const string Product = "Dingus";
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static PlexServer Server { get; private set; }
    public static event Action Changed;

    static Models.AppSettings S => SettingsStore.Current;
    public static bool SignedIn => !string.IsNullOrEmpty(S.PlexToken);
    static string UserToken => Secret.Unprotect(S.PlexToken);
    static string ServerToken => Server?.AccessToken ?? UserToken;

    public static string ClientId
    {
        get
        {
            if (string.IsNullOrEmpty(S.PlexClientId)) { S.PlexClientId = Guid.NewGuid().ToString("N"); SettingsStore.Save(); }
            return S.PlexClientId;
        }
    }

    static HttpRequestMessage Req(HttpMethod m, string url, string token = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("Accept", "application/json");
        r.Headers.Add("X-Plex-Product", Product);
        r.Headers.Add("X-Plex-Version", typeof(Plex).Assembly.GetName().Version?.ToString(3) ?? "1.0");
        r.Headers.Add("X-Plex-Client-Identifier", ClientId);
        r.Headers.Add("X-Plex-Platform", "Windows");
        r.Headers.Add("X-Plex-Device", "PC");
        r.Headers.Add("X-Plex-Device-Name", Environment.MachineName + " (Dingus)");
        if (!string.IsNullOrEmpty(token)) r.Headers.Add("X-Plex-Token", token);
        return r;
    }

    static async Task<JsonDocument> GetJson(string url, string token, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        using var resp = await Client.SendAsync(Req(HttpMethod.Get, url, token), cts.Token);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
        return await JsonDocument.ParseAsync(s, cancellationToken: cts.Token);
    }

    // ------------------------------------------------------------------ sign in

    /// <summary>Starts a sign-in: returns the PIN's id and the code you enter at plex.tv/link.</summary>
    public static async Task<(long Id, string Code)> StartPinAsync()
    {
        using var resp = await Client.SendAsync(Req(HttpMethod.Post, "https://plex.tv/api/v2/pins?strong=true"));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("id").GetInt64(), doc.RootElement.GetProperty("code").GetString());
    }

    public static string AuthUrl(string code) =>
        $"https://app.plex.tv/auth#?clientID={ClientId}&code={Uri.EscapeDataString(code)}&context%5Bdevice%5D%5Bproduct%5D={Product}";

    /// <summary>Returns the account token once you've approved the sign-in on plex.tv, otherwise null.</summary>
    public static async Task<string> CheckPinAsync(long id)
    {
        using var doc = await GetJson($"https://plex.tv/api/v2/pins/{id}", null);
        return doc.RootElement.TryGetProperty("authToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
    }

    public static void SaveToken(string token)
    {
        S.PlexToken = Secret.Protect(token);
        SettingsStore.Save();
        Changed?.Invoke();
    }

    public static void SignOut()
    {
        S.PlexToken = null;
        S.PlexServerId = null;
        Server = null;
        SettingsStore.Save();
        Changed?.Invoke();
    }

    /// <summary>Opens plex.tv in the browser and waits (up to 5 minutes) for you to approve Dingus.</summary>
    public static async Task<bool> SignInAsync(Action<string> showCode, CancellationToken ct)
    {
        var (id, code) = await StartPinAsync();
        showCode(code);
        try { Process.Start(new ProcessStartInfo(AuthUrl(code)) { UseShellExecute = true }); } catch { }
        var until = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(2000, ct);
            string token = null;
            try { token = await CheckPinAsync(id); } catch (HttpRequestException) { }
            if (!string.IsNullOrEmpty(token))
            {
                SaveToken(token);
                return await ConnectAsync();
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ servers

    public static async Task<List<PlexServer>> ServersAsync()
    {
        using var doc = await GetJson("https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1", UserToken);
        var list = new List<PlexServer>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (!(Str(r, "provides") ?? "").Contains("server")) continue;
            var s = new PlexServer
            {
                Id = Str(r, "clientIdentifier"),
                Name = Str(r, "name"),
                AccessToken = Str(r, "accessToken"),
                Owned = r.TryGetProperty("owned", out var o) && o.ValueKind == JsonValueKind.True,
            };
            if (r.TryGetProperty("connections", out var cs))
                foreach (var c in cs.EnumerateArray())
                    s.Connections.Add((Str(c, "uri"), Bool(c, "local"), Bool(c, "relay")));
            list.Add(s);
        }
        return list.OrderByDescending(x => x.Owned).ToList();
    }

    /// <summary>Finds your server and the quickest address that answers (home network first, then remote, then relay).</summary>
    public static async Task<bool> ConnectAsync(string serverId = null)
    {
        if (!SignedIn) return false;
        var servers = await ServersAsync();
        var s = servers.FirstOrDefault(x => x.Id == (serverId ?? S.PlexServerId)) ?? servers.FirstOrDefault();
        if (s == null) return false;
        foreach (var c in s.Connections.OrderBy(c => c.Relay).ThenByDescending(c => c.Local))
        {
            try
            {
                using var doc = await GetJson(c.Uri.TrimEnd('/') + "/identity", s.AccessToken, timeout: TimeSpan.FromSeconds(c.Local ? 3 : 6));
                s.BaseUrl = c.Uri.TrimEnd('/');
                Server = s;
                if (S.PlexServerId != s.Id) { S.PlexServerId = s.Id; SettingsStore.Save(); }
                Changed?.Invoke();
                return true;
            }
            catch { }
        }
        return false;
    }

    static async Task<bool> EnsureConnected() => Server != null || await ConnectAsync();

    // ------------------------------------------------------------------ libraries

    public static async Task<List<PlexLibrary>> LibrariesAsync()
    {
        if (!await EnsureConnected()) return new();
        using var doc = await GetJson($"{Server.BaseUrl}/library/sections", ServerToken);
        var list = new List<PlexLibrary>();
        if (doc.RootElement.GetProperty("MediaContainer").TryGetProperty("Directory", out var dirs))
            foreach (var d in dirs.EnumerateArray())
            {
                var type = Str(d, "type");
                if (type is "movie" or "show")
                    list.Add(new PlexLibrary { Key = Str(d, "key"), Title = Str(d, "title"), Type = type });
            }
        return list;
    }

    public static Task<List<PlexItem>> ItemsAsync(string sectionKey) => List($"/library/sections/{sectionKey}/all");
    public static Task<List<PlexItem>> ChildrenAsync(string ratingKey) => List($"/library/metadata/{ratingKey}/children");

    /// <summary>Films and episodes you're part-way through (Plex's "Continue Watching").</summary>
    public static async Task<List<PlexItem>> ContinueWatchingAsync()
    {
        try { return await List("/hubs/continueWatching/items"); }
        catch { return await List("/library/onDeck"); } // older servers
    }

    /// <summary>Full details of one item (includes the file to play).</summary>
    public static async Task<PlexItem> DetailsAsync(string ratingKey) => (await List($"/library/metadata/{ratingKey}")).FirstOrDefault();

    static async Task<List<PlexItem>> List(string path)
    {
        if (!await EnsureConnected()) return new();
        using var doc = await GetJson(Server.BaseUrl + path, ServerToken);
        var list = new List<PlexItem>();
        if (!doc.RootElement.GetProperty("MediaContainer").TryGetProperty("Metadata", out var items)) return list;
        foreach (var m in items.EnumerateArray())
        {
            string part = null;
            if (m.TryGetProperty("Media", out var media) && media.GetArrayLength() > 0 &&
                media[0].TryGetProperty("Part", out var parts) && parts.GetArrayLength() > 0)
                part = Str(parts[0], "key");
            var type = Str(m, "type");
            var thumb = Str(m, "thumb");
            var it = new PlexItem
            {
                RatingKey = Str(m, "ratingKey"),
                Type = type,
                Title = Str(m, "title"),
                Year = Str(m, "year"),
                Summary = Str(m, "summary"),
                Thumb = thumb,
                Art = Str(m, "art"),
                ShowTitle = Str(m, "grandparentTitle") ?? Str(m, "parentTitle"),
                ShowThumb = Str(m, "grandparentThumb") ?? Str(m, "parentThumb"),
                ShowKey = Str(m, "grandparentRatingKey") ?? Str(m, "parentRatingKey"),
                Season = (int)Num(m, "parentIndex"),
                Index = (int)Num(m, "index"),
                DurationMs = Num(m, "duration"),
                ViewOffsetMs = Num(m, "viewOffset"),
                ViewCount = (int)Num(m, "viewCount"),
                LeafCount = (int)Num(m, "leafCount"),
                ViewedLeafCount = (int)Num(m, "viewedLeafCount"),
                AddedAt = Num(m, "addedAt"),
                PartKey = part,
            };
            // Episodes show their own still in a season list; in "continue watching" they use the show's poster.
            bool posterRow = path.Contains("continueWatching") || path.Contains("onDeck");
            bool still = type == "episode" && !posterRow;
            it.Image = ImageUrl(type == "episode" && posterRow ? it.ShowThumb ?? thumb : thumb, still ? 356 : 240, still ? 200 : 360);
            list.Add(it);
        }
        return list;
    }

    public static string ImageUrl(string path, int w, int h) =>
        string.IsNullOrEmpty(path) || Server == null ? null
            : $"{Server.BaseUrl}/photo/:/transcode?width={w}&height={h}&minSize=1&upscale=1&url={Uri.EscapeDataString(path)}&X-Plex-Token={ServerToken}";

    /// <summary>The address VLC plays: the original file, straight from the server (no transcoding).</summary>
    public static string StreamUrl(PlexItem it) =>
        it?.PartKey == null || Server == null ? null : $"{Server.BaseUrl}{it.PartKey}?X-Plex-Token={ServerToken}";

    // ------------------------------------------------------------------ watch progress

    /// <summary>Tells Plex where you are (so other Plex apps can carry on from here).</summary>
    public static void ReportProgress(string ratingKey, string state, long timeMs, long durationMs)
    {
        if (Server == null || ratingKey == null) return;
        var url = $"{Server.BaseUrl}/:/timeline?ratingKey={ratingKey}&key=%2Flibrary%2Fmetadata%2F{ratingKey}&state={state}&time={Math.Max(0, timeMs)}&duration={Math.Max(0, durationMs)}";
        _ = Fire(url);
    }

    /// <summary>Marks a film or episode as watched.</summary>
    public static void MarkWatched(string ratingKey)
    {
        if (Server == null || ratingKey == null) return;
        _ = Fire($"{Server.BaseUrl}/:/scrobble?key={ratingKey}&identifier=com.plexapp.plugins.library");
    }

    static async Task Fire(string url)
    {
        try { using var r = await Client.SendAsync(Req(HttpMethod.Get, url, ServerToken)); }
        catch (Exception ex) { Log.Error(ex); }
    }

    // ------------------------------------------------------------------ JSON helpers

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), out var l2) ? l2 : 0,
            _ => 0,
        } : 0;

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
