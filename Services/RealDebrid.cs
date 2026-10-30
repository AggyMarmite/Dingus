using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Dingus.Services;

public sealed class RdUser
{
    public string Username { get; init; }
    public string Type { get; init; }
    public DateTime? Expiration { get; init; }
    public bool Premium => Type == "premium";
    public int DaysLeft => Expiration is { } e ? Math.Max(0, (int)Math.Ceiling((e - DateTime.UtcNow).TotalDays)) : 0;
}

/// <summary>A file in your Real-Debrid cloud (an unrestricted link or a finished torrent's file).</summary>
public sealed class RdFile
{
    public string Id { get; init; }
    public string Name { get; init; }
    public long Bytes { get; init; }
    public DateTime? Added { get; init; }
    /// <summary>A playable address, when there's one already (downloads); torrents' files need unlocking first.</summary>
    public string Download { get; init; }
    /// <summary>The Real-Debrid link to unlock (torrent files).</summary>
    public string Link { get; init; }
    public string Source { get; init; } // "download" or "torrent"
}

public sealed class RdDevice
{
    public string DeviceCode { get; init; }
    public string UserCode { get; init; }
    public string VerificationUrl { get; init; }
    public string DirectUrl { get; init; }
    public int Interval { get; init; }
    public int ExpiresIn { get; init; }
}

/// <summary>Why a torrent source couldn't be played yet (Real-Debrid is still fetching it, or it can't).</summary>
public sealed class RdNotReadyException : Exception
{
    public RdNotReadyException(string message) : base(message) { }
}

/// <summary>
/// Real-Debrid: sign in with a code on real-debrid.com (or paste an API token), see your account, play the files in
/// your Real-Debrid cloud, and play torrent sources from add-ons through it – Real-Debrid fetches the torrent and hands
/// back an ordinary web address, so Dingus never downloads torrents itself. Shared by the Windows and Linux apps.
/// </summary>
public static class RealDebrid
{
    /// <summary>Real-Debrid's public app id for open-source apps (device sign-in).</summary>
    const string OpenSourceClientId = "X245A4XAIBGVM";
    const string Api = "https://api.real-debrid.com/rest/1.0/";
    const string OAuth = "https://api.real-debrid.com/oauth/v2/";

    sealed class StateFile
    {
        public string ClientId { get; set; }
        public string ClientSecret { get; set; } // protected
        public string AccessToken { get; set; }  // protected
        public string RefreshToken { get; set; } // protected (none for a pasted API token)
        public DateTime ExpiresUtc { get; set; }
    }

    static StateFile _state;
    static readonly object Gate = new();
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    static readonly SemaphoreSlim Refreshing = new(1);
    static string FilePath => Path.Combine(AppPaths.Root, "realdebrid.json");

    public static event Action Changed;

    static StateFile State
    {
        get
        {
            lock (Gate)
            {
                if (_state != null) return _state;
                try { if (File.Exists(FilePath)) _state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(FilePath)); }
                catch (Exception ex) { Log.Error(ex); }
                return _state ??= new StateFile();
            }
        }
    }

    static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(State)); }
        catch (Exception ex) { Log.Error(ex); }
    }

    public static bool Connected => !string.IsNullOrEmpty(State.AccessToken);

    public static void Disconnect()
    {
        lock (Gate) _state = new StateFile();
        try { File.Delete(FilePath); } catch { }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ signing in

    /// <summary>Step 1: a short code for you to enter on real-debrid.com/device.</summary>
    public static async Task<RdDevice> StartDeviceAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync($"{OAuth}device/code?client_id={OpenSourceClientId}&new_credentials=yes", ct));
        var r = doc.RootElement;
        return new RdDevice
        {
            DeviceCode = r.Str("device_code"),
            UserCode = r.Str("user_code"),
            VerificationUrl = r.Str("verification_url") ?? "https://real-debrid.com/device",
            DirectUrl = r.Str("direct_verification_url"),
            Interval = Math.Max(2, r.Int("interval")),
            ExpiresIn = r.Int("expires_in") is var e && e > 0 ? e : 600,
        };
    }

    /// <summary>Step 2: waits until you've entered the code, then signs in. False if it ran out of time.</summary>
    public static async Task<bool> WaitForDeviceAsync(RdDevice d, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(d.ExpiresIn);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(d.Interval), ct);
            string id, secret;
            try
            {
                using var resp = await Client.GetAsync($"{OAuth}device/credentials?client_id={OpenSourceClientId}&code={Uri.EscapeDataString(d.DeviceCode)}", ct);
                if (!resp.IsSuccessStatusCode) continue; // not entered yet
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                id = doc.RootElement.Str("client_id");
                secret = doc.RootElement.Str("client_secret");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) continue;
            }
            catch (HttpRequestException) { continue; }
            await TokenAsync(id, secret, d.DeviceCode, ct);
            return true;
        }
        return false;
    }

    static async Task TokenAsync(string clientId, string clientSecret, string code, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["grant_type"] = "http://oauth.net/grant_type/device/1.0",
        });
        using var resp = await Client.PostAsync(OAuth + "token", form, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        lock (Gate)
        {
            State.ClientId = clientId;
            State.ClientSecret = Secret.Protect(clientSecret);
            State.AccessToken = Secret.Protect(r.Str("access_token"));
            State.RefreshToken = Secret.Protect(r.Str("refresh_token"));
            State.ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, r.Int("expires_in")) - 60);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>The other way in: your private API token from real-debrid.com/apitoken.</summary>
    public static async Task UseApiTokenAsync(string token)
    {
        token = (token ?? "").Trim();
        if (token.Length < 10) throw new InvalidOperationException("That doesn't look like a Real-Debrid API token.");
        lock (Gate)
        {
            _state = new StateFile { AccessToken = Secret.Protect(token), ExpiresUtc = DateTime.MaxValue };
        }
        try { await UserAsync(); }
        catch { lock (Gate) _state = new StateFile(); throw; }
        Save();
        Changed?.Invoke();
    }

    static async Task<string> AccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        var s = State;
        if (!forceRefresh && DateTime.UtcNow < s.ExpiresUtc) return Secret.Unprotect(s.AccessToken);
        if (string.IsNullOrEmpty(s.RefreshToken)) return Secret.Unprotect(s.AccessToken); // a pasted token doesn't expire this way
        await Refreshing.WaitAsync(ct);
        try
        {
            if (!forceRefresh && DateTime.UtcNow < State.ExpiresUtc) return Secret.Unprotect(State.AccessToken);
            await TokenAsync(s.ClientId, Secret.Unprotect(s.ClientSecret), Secret.Unprotect(s.RefreshToken), ct);
            return Secret.Unprotect(State.AccessToken);
        }
        finally { Refreshing.Release(); }
    }

    // ------------------------------------------------------------------ the API

    static async Task<JsonElement> CallAsync(HttpMethod method, string path, Dictionary<string, string> form = null, CancellationToken ct = default)
    {
        if (!Connected) throw new InvalidOperationException("Real-Debrid isn't connected (Settings → Real-Debrid).");
        for (int attempt = 0; ; attempt++)
        {
            var token = await AccessTokenAsync(ct, forceRefresh: attempt > 0);
            using var req = new HttpRequestMessage(method, Api + path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (form != null) req.Content = new FormUrlEncodedContent(form);
            using var resp = await Client.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && !string.IsNullOrEmpty(State.RefreshToken)) continue;
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string msg = null;
                try { using var e = JsonDocument.Parse(text); msg = e.RootElement.Str("error"); } catch { }
                if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Real-Debrid signed you out – connect it again in Settings.");
                throw new InvalidOperationException("Real-Debrid: " + Friendly(msg) + $" ({(int)resp.StatusCode})");
            }
            if (string.IsNullOrWhiteSpace(text)) return default;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
    }

    static string Friendly(string error) => error switch
    {
        null or "" => "something went wrong",
        "permission_denied" => "your account can't do that",
        "bad_token" => "the sign-in has expired",
        "hoster_unavailable" => "that host isn't available right now",
        "infringing_file" => "that file has been removed",
        "too_many_requests" => "too many requests – try again in a minute",
        _ => error.Replace('_', ' '),
    };

    public static async Task<RdUser> UserAsync(CancellationToken ct = default)
    {
        var r = await CallAsync(HttpMethod.Get, "user", ct: ct);
        DateTime? exp = DateTime.TryParse(r.Str("expiration"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var e) ? e : null;
        return new RdUser { Username = r.Str("username"), Type = r.Str("type"), Expiration = exp };
    }

    static readonly string[] VideoExt = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".m2ts", ".webm", ".wmv", ".mpg", ".mpeg" };
    static bool IsVideo(string name) => VideoExt.Any(x => (name ?? "").EndsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>Video files in your Real-Debrid cloud: unrestricted downloads and finished torrents, newest first.</summary>
    public static async Task<List<RdFile>> FilesAsync(CancellationToken ct = default)
    {
        var res = new List<RdFile>();
        var downloads = await CallAsync(HttpMethod.Get, "downloads?limit=100", ct: ct);
        if (downloads.ValueKind == JsonValueKind.Array)
            foreach (var d in downloads.EnumerateArray())
            {
                var name = d.Str("filename");
                if (!IsVideo(name) && !(d.Str("mimeType") ?? "").StartsWith("video")) continue;
                res.Add(new RdFile
                {
                    Id = d.Str("id"), Name = name, Bytes = d.Long("filesize"), Download = d.Str("download"), Link = d.Str("link"),
                    Added = DateTime.TryParse(d.Str("generated"), out var g) ? g : null, Source = "download",
                });
            }
        var torrents = await CallAsync(HttpMethod.Get, "torrents?limit=100", ct: ct);
        if (torrents.ValueKind == JsonValueKind.Array)
            foreach (var t in torrents.EnumerateArray())
            {
                if (t.Str("status") != "downloaded") continue;
                var links = t.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => x.GetString()).ToList() : new();
                if (links.Count == 0) continue;
                // One entry per torrent (its biggest file is unlocked when you play it).
                res.Add(new RdFile
                {
                    Id = t.Str("id"), Name = t.Str("filename"), Bytes = t.Long("bytes"), Link = links[0],
                    Added = DateTime.TryParse(t.Str("added"), out var a) ? a : null, Source = "torrent",
                });
            }
        // The same file can be both (a torrent you've already unlocked) – keep one.
        return res.GroupBy(f => (f.Name ?? "") + "|" + f.Bytes).Select(g => g.OrderBy(f => f.Download == null ? 1 : 0).First())
            .OrderByDescending(f => f.Added ?? DateTime.MinValue).ToList();
    }

    /// <summary>A Real-Debrid link → a playable address.</summary>
    public static async Task<string> UnrestrictAsync(string link, CancellationToken ct = default)
    {
        var r = await CallAsync(HttpMethod.Post, "unrestrict/link", new() { ["link"] = link }, ct);
        return r.Str("download") ?? throw new InvalidOperationException("Real-Debrid didn't return an address to play.");
    }

    /// <summary>Your cloud file → a playable address.</summary>
    public static async Task<string> PlayableAsync(RdFile f, CancellationToken ct = default) =>
        f.Download ?? await UnrestrictAsync(f.Link, ct);

    /// <summary>
    /// A torrent source → a playable address: adds it to Real-Debrid (or finds it there already), picks the right file
    /// (the add-on's file name or position, else the biggest video), and unlocks it. Popular titles are usually cached,
    /// so this takes a second or two. If Real-Debrid has to fetch it first, throws <see cref="RdNotReadyException"/>
    /// saying how far it's got – it carries on fetching, so trying again later works.
    /// </summary>
    public static async Task<string> ResolveTorrentAsync(string infoHash, int? fileIdx, string fileName, IProgress<string> progress, CancellationToken ct)
    {
        infoHash = infoHash.ToLowerInvariant();
        string id = null;
        var mine = await CallAsync(HttpMethod.Get, "torrents?limit=100", ct: ct);
        if (mine.ValueKind == JsonValueKind.Array)
            foreach (var t in mine.EnumerateArray())
                if (string.Equals(t.Str("hash"), infoHash, StringComparison.OrdinalIgnoreCase) && t.Str("status") is "downloaded" or "downloading" or "queued" or "waiting_files_selection")
                { id = t.Str("id"); break; }
        if (id == null)
        {
            progress?.Report("Adding it to Real-Debrid…");
            var added = await CallAsync(HttpMethod.Post, "torrents/addMagnet", new() { ["magnet"] = "magnet:?xt=urn:btih:" + infoHash }, ct);
            id = added.Str("id") ?? throw new InvalidOperationException("Real-Debrid didn't take the torrent.");
        }

        for (int i = 0; i < 40; i++)
        {
            var info = await CallAsync(HttpMethod.Get, "torrents/info/" + id, ct: ct);
            var status = info.Str("status");
            switch (status)
            {
                case "magnet_conversion":
                    progress?.Report("Real-Debrid is reading the torrent…");
                    break;
                case "waiting_files_selection":
                    {
                        var pick = PickFile(info, fileIdx, fileName);
                        if (pick == null) throw new InvalidOperationException("There's no video file in that torrent.");
                        await CallAsync(HttpMethod.Post, "torrents/selectFiles/" + id, new() { ["files"] = pick }, ct);
                        progress?.Report("Getting it ready…");
                        break;
                    }
                case "downloaded":
                    {
                        var links = info.TryGetProperty("links", out var l) && l.ValueKind == JsonValueKind.Array
                            ? l.EnumerateArray().Select(x => x.GetString()).ToList() : new();
                        var link = PickLink(info, links, fileIdx, fileName) ?? throw new InvalidOperationException("Real-Debrid has no file to play.");
                        progress?.Report("Starting…");
                        return await UnrestrictAsync(link, ct);
                    }
                case "queued":
                case "downloading":
                case "compressing":
                case "uploading":
                    if (i >= 6)
                        throw new RdNotReadyException($"Real-Debrid hasn't got this one yet – it's fetching it now ({info.Long("progress")}%). Try again in a few minutes, or pick another source.");
                    progress?.Report($"Real-Debrid is fetching it ({info.Long("progress")}%)…");
                    break;
                default: // error, virus, dead, magnet_error
                    throw new InvalidOperationException("Real-Debrid can't get this one (" + (status ?? "unknown").Replace('_', ' ') + "). Try another source.");
            }
            await Task.Delay(i < 4 ? 800 : 1500, ct);
        }
        throw new RdNotReadyException("Real-Debrid is taking a while with this one – try again shortly, or pick another source.");
    }

    static List<(int Id, string Path, long Bytes, bool Selected)> Files(JsonElement info)
    {
        var res = new List<(int, string, long, bool)>();
        if (info.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var f in files.EnumerateArray())
                res.Add((f.Int("id"), f.Str("path") ?? "", f.Long("bytes"), f.Int("selected") == 1));
        return res;
    }

    /// <summary>The file to fetch: the add-on's file name, else its position in the torrent, else the biggest video.</summary>
    static string PickFile(JsonElement info, int? fileIdx, string fileName)
    {
        var files = Files(info);
        var videos = files.Where(f => IsVideo(f.Path)).ToList();
        if (fileName != null && videos.FirstOrDefault(f => f.Path.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) || f.Path == fileName) is { Path: not null } named && named.Id > 0)
            return named.Id.ToString();
        if (fileIdx is int ix && files.FirstOrDefault(f => f.Id == ix + 1) is { Path: not null } byIdx && byIdx.Id > 0 && IsVideo(byIdx.Path))
            return byIdx.Id.ToString();
        if (videos.Count == 0) return null;
        return videos.OrderByDescending(f => f.Bytes).First().Id.ToString();
    }

    /// <summary>Links come one per selected file, in file order – find the one for the wanted file.</summary>
    static string PickLink(JsonElement info, List<string> links, int? fileIdx, string fileName)
    {
        if (links.Count == 0) return null;
        var selected = Files(info).Where(f => f.Selected).ToList();
        if (selected.Count != links.Count) return links[0];
        int want = -1;
        if (fileName != null) want = selected.FindIndex(f => f.Path.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) || f.Path == fileName);
        if (want < 0 && fileIdx is int ix) want = selected.FindIndex(f => f.Id == ix + 1);
        if (want < 0) want = selected.Select((f, i) => (f, i)).Where(x => IsVideo(x.f.Path)).OrderByDescending(x => x.f.Bytes).Select(x => x.i).DefaultIfEmpty(0).First();
        return links[Math.Clamp(want, 0, links.Count - 1)];
    }
}
