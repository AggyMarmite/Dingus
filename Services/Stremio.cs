using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

// =====================================================================================
// Stremio add-ons: the open add-on format Stremio uses. An add-on is a web address serving a manifest
// (what it offers), catalogues (lists of titles), details ("meta") and sources ("streams") as JSON.
// Shared by the Windows and Linux apps.
// =====================================================================================

public sealed class StremioResource
{
    public string Name { get; set; }
    public List<string> Types { get; set; }
    public List<string> IdPrefixes { get; set; }
}

public sealed class StremioExtra
{
    public string Name { get; set; }
    public bool IsRequired { get; set; }
    public List<string> Options { get; set; }
}

public sealed class StremioCatalog
{
    public string Type { get; set; }
    public string Id { get; set; }
    public string Name { get; set; }
    public List<StremioExtra> Extra { get; set; } = new();
    [JsonIgnore] public bool CanSearch => Extra.Any(e => e.Name == "search");
    /// <summary>Only works with a search (or another value you'd have to pick) – not one to browse.</summary>
    [JsonIgnore] public bool NeedsInput => Extra.Any(e => e.IsRequired && e.Name != "skip");
    [JsonIgnore] public bool CanPage => Extra.Any(e => e.Name == "skip");
}

public sealed class StremioAddon
{
    /// <summary>The add-on's manifest address (ends in /manifest.json).</summary>
    public string TransportUrl { get; set; }
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Logo { get; set; }
    public string Version { get; set; }
    public List<string> Types { get; set; } = new();
    public List<string> IdPrefixes { get; set; }
    public List<StremioResource> Resources { get; set; } = new();
    public List<StremioCatalog> Catalogs { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Came from your Stremio account (replaced when the account's add-ons are brought in again).</summary>
    public bool FromAccount { get; set; }

    [JsonIgnore] public string Base => TransportUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)
        ? TransportUrl[..^"/manifest.json".Length] : TransportUrl.TrimEnd('/');

    /// <summary>Whether it offers <paramref name="resource"/> ("catalog", "meta", "stream") for this kind of title and id.</summary>
    public bool Serves(string resource, string type, string id)
    {
        foreach (var r in Resources)
        {
            if (r.Name != resource) continue;
            var types = r.Types ?? Types;
            if (types != null && types.Count > 0 && type != null && !types.Contains(type)) continue;
            var prefixes = r.IdPrefixes ?? IdPrefixes;
            if (id != null && prefixes != null && prefixes.Count > 0 && !prefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal))) continue;
            return true;
        }
        return false;
    }
}

public sealed class StremioVideo
{
    public string Id { get; set; }
    public string Title { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public DateTime? Released { get; set; }
    public string Thumbnail { get; set; }
    public string Overview { get; set; }
}

public sealed class StremioMeta
{
    public string Id { get; set; }
    public string Type { get; set; }
    public string Name { get; set; }
    public string Poster { get; set; }
    public string Background { get; set; }
    public string Logo { get; set; }
    public string Description { get; set; }
    public string ReleaseInfo { get; set; }
    public string Runtime { get; set; }
    public string ImdbRating { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Cast { get; set; } = new();
    public List<string> Director { get; set; } = new();
    public List<StremioVideo> Videos { get; set; } = new();
    /// <summary>The add-on it came from (its catalogue) – asked first for details.</summary>
    public StremioAddon From { get; set; }
}

public enum StreamKind { Direct, Torrent, External }

public sealed class StremioStream
{
    public StremioAddon Addon { get; set; }
    public string Name { get; set; }
    public string Title { get; set; }
    public string Url { get; set; }
    public string InfoHash { get; set; }
    public int? FileIdx { get; set; }
    public string ExternalUrl { get; set; }
    public string Filename { get; set; }
    public Dictionary<string, string> Headers { get; set; }
    public StreamKind Kind => Url != null ? StreamKind.Direct : InfoHash != null ? StreamKind.Torrent : StreamKind.External;
}

public static class Stremio
{
    public const string CinemetaUrl = "https://v3-cinemeta.strem.io/manifest.json";

    sealed class StateFile
    {
        public string AuthKey { get; set; }  // protected (see Secret)
        public string Email { get; set; }
        public List<StremioAddon> Addons { get; set; } = new();
    }

    static StateFile _state;
    static readonly object Gate = new();
    static readonly HttpClient Client = MakeClient();
    static string FilePath => Path.Combine(AppPaths.Root, "stremio.json");

    /// <summary>Add-ons were added, removed, switched on/off, or brought in from your account.</summary>
    public static event Action Changed;

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Dingus/1.0");
        return c;
    }

    static StateFile State
    {
        get
        {
            lock (Gate)
            {
                if (_state != null) return _state;
                try
                {
                    if (File.Exists(FilePath)) _state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(FilePath));
                }
                catch (Exception ex) { Log.Error(ex); }
                if (_state == null)
                {
                    // First time: Stremio's own film & TV catalogue (titles, posters, episodes – no streams).
                    _state = new StateFile();
                    _state.Addons.Add(new StremioAddon { TransportUrl = CinemetaUrl, Name = "Cinemeta", Id = "com.linvo.cinemeta" });
                }
                _state.Addons ??= new();
                return _state;
            }
        }
    }

    static void Save()
    {
        StateFile s;
        lock (Gate) s = _state;
        if (s == null) return;
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    public static IReadOnlyList<StremioAddon> Addons { get { lock (Gate) return State.Addons.ToList(); } }
    public static IEnumerable<StremioAddon> Enabled => Addons.Where(a => a.Enabled && a.Resources.Count > 0);
    public static bool SignedIn => !string.IsNullOrEmpty(State.AuthKey);
    public static string Email => State.Email;
    static string AuthKey => Secret.Unprotect(State.AuthKey);

    /// <summary>Fetches the manifests of add-ons that don't have theirs yet (the default one, the first time).</summary>
    public static async Task EnsureManifestsAsync()
    {
        foreach (var a in Addons.Where(a => a.Resources.Count == 0).ToList())
        {
            try
            {
                var fresh = await FetchManifestAsync(a.TransportUrl);
                fresh.Enabled = a.Enabled;
                fresh.FromAccount = a.FromAccount;
                Replace(a, fresh);
            }
            catch (Exception ex) { Log.Info($"Stremio add-on {a.TransportUrl}: {Http.Friendly(ex)}"); }
        }
    }

    static void Replace(StremioAddon old, StremioAddon fresh)
    {
        lock (Gate)
        {
            var l = State.Addons;
            int i = l.IndexOf(old);
            if (i >= 0) l[i] = fresh; else l.Add(fresh);
        }
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ installing

    /// <summary>"stremio://…", a bare address or one without "/manifest.json" → the manifest's address.</summary>
    public static string NormaliseUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url["stremio://".Length..];
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        if (!url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)) url = url.TrimEnd('/') + "/manifest.json";
        return url;
    }

    /// <summary>Adds an add-on by its address (or updates it if it's already there).</summary>
    public static async Task<StremioAddon> InstallAsync(string url)
    {
        var a = await FetchManifestAsync(NormaliseUrl(url));
        var existing = Addons.FirstOrDefault(x => x.TransportUrl == a.TransportUrl || (x.Id != null && x.Id == a.Id && x.Base == a.Base));
        if (existing != null) { a.FromAccount = existing.FromAccount; Replace(existing, a); }
        else Replace(null, a);
        return a;
    }

    public static void Remove(StremioAddon a)
    {
        lock (Gate) State.Addons.RemoveAll(x => x.TransportUrl == a.TransportUrl);
        Save();
        Changed?.Invoke();
    }

    public static void SetEnabled(StremioAddon a, bool on)
    {
        lock (Gate) foreach (var x in State.Addons) if (x.TransportUrl == a.TransportUrl) x.Enabled = on;
        Save();
        Changed?.Invoke();
    }

    public static async Task<StremioAddon> FetchManifestAsync(string transportUrl)
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync(transportUrl));
        return ParseManifest(doc.RootElement, transportUrl);
    }

    static StremioAddon ParseManifest(JsonElement m, string transportUrl)
    {
        var a = new StremioAddon
        {
            TransportUrl = transportUrl,
            Id = m.Str("id"),
            Name = m.Str("name") ?? "Add-on",
            Description = m.Str("description"),
            Logo = m.Str("logo"),
            Version = m.Str("version"),
            Types = StrList(m, "types"),
            IdPrefixes = m.TryGetProperty("idPrefixes", out var ip) && ip.ValueKind == JsonValueKind.Array ? StrList(m, "idPrefixes") : null,
        };
        if (m.TryGetProperty("resources", out var res) && res.ValueKind == JsonValueKind.Array)
            foreach (var r in res.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String) a.Resources.Add(new StremioResource { Name = r.GetString() });
                else if (r.ValueKind == JsonValueKind.Object)
                    a.Resources.Add(new StremioResource
                    {
                        Name = r.Str("name"),
                        Types = r.TryGetProperty("types", out _) ? StrList(r, "types") : null,
                        IdPrefixes = r.TryGetProperty("idPrefixes", out _) ? StrList(r, "idPrefixes") : null,
                    });
            }
        if (m.TryGetProperty("catalogs", out var cats) && cats.ValueKind == JsonValueKind.Array)
            foreach (var c in cats.EnumerateArray())
            {
                var cat = new StremioCatalog { Type = c.Str("type"), Id = c.Str("id"), Name = c.Str("name") };
                if (c.TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Array)
                    foreach (var e in ex.EnumerateArray())
                        cat.Extra.Add(new StremioExtra
                        {
                            Name = e.Str("name"),
                            IsRequired = e.TryGetProperty("isRequired", out var req) && req.ValueKind == JsonValueKind.True,
                            Options = StrList(e, "options"),
                        });
                // The older way of saying the same thing.
                foreach (var n in StrList(c, "extraSupported"))
                    if (!cat.Extra.Any(x => x.Name == n)) cat.Extra.Add(new StremioExtra { Name = n });
                foreach (var n in StrList(c, "extraRequired"))
                {
                    var x = cat.Extra.FirstOrDefault(y => y.Name == n);
                    if (x == null) cat.Extra.Add(new StremioExtra { Name = n, IsRequired = true }); else x.IsRequired = true;
                }
                if (cat.Type != null && cat.Id != null) a.Catalogs.Add(cat);
            }
        return a;
    }

    static List<string> StrList(JsonElement e, string name)
    {
        var res = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) res.Add(x.GetString());
        return res;
    }

    // ------------------------------------------------------------------ your Stremio account

    static async Task<JsonElement> ApiAsync(string method, object body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Client.PostAsync("https://api.strem.io/api/" + method, content);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException(err.ValueKind == JsonValueKind.Object ? err.Str("message") ?? "Stremio said no" : err.ToString());
        if (!resp.IsSuccessStatusCode) resp.EnsureSuccessStatusCode();
        if (!root.TryGetProperty("result", out var result)) throw new InvalidOperationException("Stremio sent an empty reply");
        return result.Clone();
    }

    /// <summary>Signs in to Stremio (the password isn't kept – only the sign-in key Stremio gives back) and brings in your add-ons.</summary>
    public static async Task<int> SignInAsync(string email, string password)
    {
        var r = await ApiAsync("login", new { type = "Login", email, password, facebook = false });
        var key = r.Str("authKey");
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Stremio didn't sign you in");
        lock (Gate)
        {
            State.AuthKey = Secret.Protect(key);
            State.Email = r.TryGetProperty("user", out var u) ? u.Str("email") ?? email : email;
        }
        Save();
        return await PullAccountAddonsAsync();
    }

    /// <summary>Brings in the add-ons on your Stremio account (replacing the ones it brought in before). Returns how many.</summary>
    public static async Task<int> PullAccountAddonsAsync()
    {
        if (!SignedIn) return 0;
        var r = await ApiAsync("addonCollectionGet", new { type = "AddonCollectionGet", authKey = AuthKey, update = true });
        var fresh = new List<StremioAddon>();
        if (r.TryGetProperty("addons", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var x in list.EnumerateArray())
            {
                var url = x.Str("transportUrl");
                if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var a = x.TryGetProperty("manifest", out var m) && m.ValueKind == JsonValueKind.Object ? ParseManifest(m, url) : await FetchManifestAsync(url);
                    a.FromAccount = true;
                    fresh.Add(a);
                }
                catch (Exception ex) { Log.Info($"Stremio add-on {url}: {ex.Message}"); }
            }
        lock (Gate)
        {
            var keep = State.Addons.Where(a => !a.FromAccount && !fresh.Any(f => f.TransportUrl == a.TransportUrl)).ToList();
            // Keep your on/off choices for add-ons that were already here.
            foreach (var f in fresh)
                if (State.Addons.FirstOrDefault(a => a.TransportUrl == f.TransportUrl) is { } old) f.Enabled = old.Enabled;
            State.Addons = fresh.Concat(keep).ToList();
        }
        Save();
        Changed?.Invoke();
        return fresh.Count;
    }

    /// <summary>Signs out of Stremio and removes the add-ons it brought in (ones you added by address stay).</summary>
    public static void SignOut()
    {
        lock (Gate)
        {
            State.AuthKey = null;
            State.Email = null;
            State.Addons.RemoveAll(a => a.FromAccount);
            if (State.Addons.Count == 0) State.Addons.Add(new StremioAddon { TransportUrl = CinemetaUrl, Name = "Cinemeta", Id = "com.linvo.cinemeta" });
        }
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ catalogues, details, sources

    static async Task<JsonElement> GetAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var text = await Client.GetStringAsync(url, cts.Token);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    static string Esc(string s) => Uri.EscapeDataString(s ?? "");

    /// <summary>A page of a catalogue (100-ish titles), optionally searched or from further in.</summary>
    public static async Task<List<StremioMeta>> CatalogAsync(StremioAddon a, StremioCatalog c, string search = null, int skip = 0,
        CancellationToken ct = default)
    {
        var extra = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) extra.Add("search=" + Esc(search.Trim()));
        if (skip > 0) extra.Add("skip=" + skip);
        var url = $"{a.Base}/catalog/{Esc(c.Type)}/{Esc(c.Id)}{(extra.Count > 0 ? "/" + string.Join("&", extra) : "")}.json";
        var root = await GetAsync(url, ct);
        var res = new List<StremioMeta>();
        if (root.TryGetProperty("metas", out var metas) && metas.ValueKind == JsonValueKind.Array)
            foreach (var m in metas.EnumerateArray())
            {
                var meta = ParseMeta(m);
                if (meta?.Id == null) continue;
                meta.Type ??= c.Type;
                meta.From = a;
                res.Add(meta);
            }
        return res;
    }

    /// <summary>Searches every catalogue that can be searched (films and shows), each add-on's results kept apart.</summary>
    public static async Task<List<(StremioAddon Addon, StremioCatalog Catalog, List<StremioMeta> Items)>> SearchAsync(string q, CancellationToken ct = default)
    {
        var jobs = Enabled.SelectMany(a => a.Catalogs.Where(c => c.CanSearch).Select(c => (a, c))).Take(12).ToList();
        var results = await Task.WhenAll(jobs.Select(async j =>
        {
            try { return (j.a, j.c, await CatalogAsync(j.a, j.c, q, 0, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return (j.a, j.c, new List<StremioMeta>()); }
        }));
        return results.Where(r => r.Item3.Count > 0).ToList();
    }

    /// <summary>A title's details (episodes for a show) – from the add-on it came from, else any that has them.</summary>
    public static async Task<StremioMeta> MetaAsync(string type, string id, StremioAddon prefer = null, CancellationToken ct = default)
    {
        var order = Enabled.Where(a => a.Serves("meta", type, id)).OrderBy(a => a == prefer || a.TransportUrl == prefer?.TransportUrl ? 0 : 1).ToList();
        foreach (var a in order)
        {
            try
            {
                var root = await GetAsync($"{a.Base}/meta/{Esc(type)}/{Esc(id)}.json", ct);
                if (root.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    var meta = ParseMeta(m);
                    if (meta == null) continue;
                    meta.Type ??= type;
                    meta.Id ??= id;
                    meta.From = a;
                    return meta;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { Log.Info($"{a.Name} meta: {Http.Friendly(ex)}"); }
        }
        return null;
    }

    static StremioMeta ParseMeta(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return null;
        var meta = new StremioMeta
        {
            Id = m.Str("id") ?? m.Str("imdb_id"),
            Type = m.Str("type"),
            Name = m.Str("name"),
            Poster = m.Str("poster"),
            Background = m.Str("background"),
            Logo = m.Str("logo"),
            Description = m.Str("description"),
            ReleaseInfo = m.Str("releaseInfo") ?? m.Str("year"),
            Runtime = m.Str("runtime"),
            ImdbRating = m.Str("imdbRating"),
            Genres = StrList(m, "genres"),
            Cast = StrList(m, "cast"),
            Director = StrList(m, "director"),
        };
        if (meta.Genres.Count == 0) meta.Genres = StrList(m, "genre");
        if (m.TryGetProperty("videos", out var vids) && vids.ValueKind == JsonValueKind.Array)
            foreach (var v in vids.EnumerateArray())
            {
                var vid = new StremioVideo
                {
                    Id = v.Str("id"),
                    Title = v.Str("title") ?? v.Str("name"),
                    Season = v.Int("season"),
                    Episode = v.Int("episode") is var ep && ep > 0 ? ep : v.Int("number"),
                    Thumbnail = v.Str("thumbnail"),
                    Overview = v.Str("overview") ?? v.Str("description"),
                };
                if (DateTime.TryParse(v.Str("released") ?? v.Str("firstAired"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var rel))
                    vid.Released = rel;
                if (vid.Id != null) meta.Videos.Add(vid);
            }
        return meta;
    }

    /// <summary>
    /// Asks every add-on that has sources for this title, all at once; <paramref name="onAddon"/> is called as each one
    /// answers (off the window's thread), so the quick ones show straight away.
    /// </summary>
    public static async Task StreamsAsync(string type, string id, Action<StremioAddon, List<StremioStream>, string> onAddon, CancellationToken ct = default)
    {
        var addons = Enabled.Where(a => a.Serves("stream", type, id)).ToList();
        await Task.WhenAll(addons.Select(async a =>
        {
            var list = new List<StremioStream>();
            string error = null;
            try
            {
                var root = await GetAsync($"{a.Base}/stream/{Esc(type)}/{Esc(id)}.json", ct);
                if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                    foreach (var s in streams.EnumerateArray())
                        if (ParseStream(s, a) is { } st) list.Add(st);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { error = Http.Friendly(ex); }
            if (!ct.IsCancellationRequested) onAddon(a, list, error);
        }));
    }

    /// <summary>How many add-ons will be asked for sources for this title.</summary>
    public static int StreamAddonCount(string type, string id) => Enabled.Count(a => a.Serves("stream", type, id));

    static StremioStream ParseStream(JsonElement s, StremioAddon a)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        var st = new StremioStream
        {
            Addon = a,
            Name = s.Str("name"),
            Title = s.Str("description") ?? s.Str("title"),
            Url = s.Str("url"),
            InfoHash = s.Str("infoHash")?.ToLowerInvariant(),
            FileIdx = s.TryGetProperty("fileIdx", out var fi) && fi.ValueKind == JsonValueKind.Number ? fi.GetInt32() : null,
            ExternalUrl = s.Str("externalUrl") ?? (s.Str("ytId") is { } yt ? "https://www.youtube.com/watch?v=" + yt : null),
        };
        if (s.TryGetProperty("behaviorHints", out var bh) && bh.ValueKind == JsonValueKind.Object)
        {
            st.Filename = bh.Str("filename");
            if (bh.TryGetProperty("proxyHeaders", out var ph) && ph.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object)
            {
                st.Headers = new();
                foreach (var p in req.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) st.Headers[p.Name] = p.Value.GetString();
            }
        }
        if (st.Url != null && !st.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) st.Url = null; // only web addresses play
        if (st.Url == null && st.InfoHash == null && st.ExternalUrl == null) return null;
        return st;
    }

    // ------------------------------------------------------------------ your playlist's titles → Stremio

    static readonly Regex Clutter = new(@"^\s*(?:[A-Z]{2,4}\s*[|:\-–]\s*)+|\s*[\[(]?(?:4k|uhd|2160p|1080p|720p|fhd|hd|multi|sub|dub)[\])]?\s*$|\s*\(\d{4}\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"EN | The Long Journey (2021) 4K" → "The Long Journey".</summary>
    public static string CleanTitle(string name)
    {
        var s = name ?? "";
        for (int i = 0; i < 4; i++)
        {
            var t = Clutter.Replace(s, "").Trim();
            if (t == s) break;
            s = t;
        }
        return s.Length > 0 ? s : (name ?? "").Trim();
    }

    /// <summary>
    /// Finds a playlist film or show in Stremio's catalogue (by name, and year when there is one) – so its sources can
    /// be looked up. Null when nothing matches.
    /// </summary>
    public static async Task<StremioMeta> FindAsync(string type, string name, string year, CancellationToken ct = default)
    {
        var q = CleanTitle(name);
        if (q.Length < 2) return null;
        if (year == null && Regex.Match(name ?? "", @"\((\d{4})\)") is { Success: true } ym) year = ym.Groups[1].Value;
        var cat = Enabled.SelectMany(a => a.Catalogs.Where(c => c.Type == type && c.CanSearch).Select(c => (a, c)))
            .OrderBy(x => x.a.TransportUrl == CinemetaUrl ? 0 : 1).FirstOrDefault();
        List<StremioMeta> hits;
        if (cat.a != null) hits = await CatalogAsync(cat.a, cat.c, q, 0, ct);
        else
        {
            // No searchable catalogue switched on – Stremio's own one still knows the titles.
            var cinemeta = new StremioAddon { TransportUrl = CinemetaUrl };
            hits = await CatalogAsync(cinemeta, new StremioCatalog { Type = type, Id = "top" }, q, 0, ct);
        }
        if (hits.Count == 0) return null;
        var nq = Events.Norm(q);
        StremioMeta best = null;
        int bestScore = -1;
        foreach (var (h, i) in hits.Select((h, i) => (h, i)))
        {
            int score = 0;
            if (Events.Norm(h.Name) == nq) score += 4;
            else if (Events.Norm(h.Name).Contains(nq) || nq.Contains(Events.Norm(h.Name))) score += 1;
            if (year != null && (h.ReleaseInfo ?? "").StartsWith(year)) score += 3;
            score -= Math.Min(i, 3); // the catalogue's own order
            if (score > bestScore) { bestScore = score; best = h; }
        }
        return bestScore >= 1 ? best : null;
    }

    // ------------------------------------------------------------------ for the page

    static readonly Regex QualityRx = new(@"\b(2160p|4k|uhd|1440p|1080p|720p|576p|480p|sd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SizeRx = new(@"(\d+(?:[.,]\d+)?)\s?(GB|MB|GiB|MiB)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static int QualityRank(string q) => q switch { "4K" => 0, "1440p" => 1, "1080p" => 2, "720p" => 3, "576p" or "480p" => 4, "SD" => 5, _ => 6 };

    /// <summary>The sources, as rows: best first – ones that play now, then by picture quality, in each add-on's order.</summary>
    /// <summary>
    /// "Add playlist" → Stremio: signs in (bringing in your account's add-ons) and/or adds an add-on by its address.
    /// Returns what happened, for the dialog; throws with a message to show when neither worked.
    /// </summary>
    public static async Task<string> SetUpAsync(string email, string password, string addonUrl)
    {
        email = (email ?? "").Trim();
        addonUrl = (addonUrl ?? "").Trim();
        if (email.Length == 0 && addonUrl.Length == 0)
            throw new InvalidOperationException("Sign in with your Stremio email and password, or paste an add-on's address.");
        if (email.Length > 0 && string.IsNullOrEmpty(password)) throw new InvalidOperationException("Type your Stremio password too.");
        var done = new List<string>();
        if (email.Length > 0)
        {
            try { int n = await SignInAsync(email, password); done.Add(n == 1 ? "signed in, 1 add-on" : $"signed in, {n} add-ons"); }
            catch (Exception ex) when (ex is not InvalidOperationException) { throw new InvalidOperationException("Couldn't sign in to Stremio: " + Http.Friendly(ex) + "."); }
        }
        if (addonUrl.Length > 0)
        {
            try { var a = await InstallAsync(addonUrl); done.Add($"added {a.Name}"); }
            catch (Exception ex) { throw new InvalidOperationException((done.Count > 0 ? "Signed in, but couldn't add that add-on: " : "Couldn't add that add-on: ") + (ex is InvalidOperationException ? ex.Message : Http.Friendly(ex)) + "."); }
        }
        return "Stremio: " + string.Join(", ", done);
    }

    /// <summary>
    /// For the Search page: films and shows from your add-ons' searches (films first), leaving out ones your playlist
    /// already has (<paramref name="have"/>: normalised names). Nothing when Stremio isn't set up.
    /// </summary>
    public static async Task<List<StremioMeta>> SearchTitlesAsync(string q, ISet<string> have, CancellationToken ct = default)
    {
        if (!IsSetUp || q.Length < 2) return new();
        var found = await SearchAsync(q, ct);
        var seen = new HashSet<string>();
        var res = new List<StremioMeta>();
        foreach (var (_, c, list) in found.Where(r => r.Catalog.Type is "movie" or "series").OrderBy(r => r.Catalog.Type == "movie" ? 0 : 1))
            foreach (var m in list)
                if (seen.Add(m.Type + ":" + m.Id) && !have.Contains(m.Type + ":" + Events.Norm(m.Name))) res.Add(m);
        return res;
    }

    /// <summary>The "Continue watching" key for a Stremio film or episode.</summary>
    public static string ResumeKey(StremioMeta meta, StremioVideo episode) => "st:" + (episode?.Id ?? meta.Id);

    /// <summary>The player item for a Stremio film or episode at a source's address.</summary>
    public static VodItem ToVod(StremioMeta meta, StremioVideo ep, string url, string fileName = null)
    {
        string ext = null;
        if (!string.IsNullOrEmpty(fileName)) ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) try { ext = Path.GetExtension(new Uri(url).AbsolutePath); } catch { }
        return new VodItem
        {
            Kind = "stremio",
            Title = meta.Name,
            Subtitle = ep == null ? meta.ReleaseInfo : $"S{ep.Season:00}E{ep.Episode:00}  ·  {ep.Title}",
            Url = url,
            Image = meta.Poster,
            ResumeKey = ResumeKey(meta, ep),
            ItemId = ep?.Id ?? meta.Id,
            Ext = string.IsNullOrEmpty(ext) ? null : ext,
        };
    }

    /// <summary>The line under "Sources": how many, who's still answering, and what's needed for torrents.</summary>
    public static string SourcesStatus(List<StremioStream> streams, int asked, int answered, List<string> errors)
    {
        int n = streams.Count;
        var parts = new List<string> { n == 0 ? (answered < asked ? "Nothing yet" : "No sources found") : n == 1 ? "1 source" : $"{n} sources" };
        if (answered < asked) parts.Add($"still asking {asked - answered} add-on{(asked - answered == 1 ? "" : "s")}…");
        if (errors.Count > 0 && answered >= asked) parts.Add("didn't answer: " + string.Join(", ", errors.Select(e => e.Split(':')[0])));
        if (n > 0 && streams.Any(s => s.Kind == StreamKind.Torrent) && !RealDebrid.Connected) parts.Add("torrent sources need Real-Debrid (Settings)");
        return string.Join("  ·  ", parts);
    }

    /// <summary>A catalogue's name for the Films / TV Shows list: "Popular", "Top", "Trending"… (the page says what they are).</summary>
    public static string CatalogName(StremioCatalog c, StremioAddon a)
    {
        var name = string.IsNullOrWhiteSpace(c.Name) ? "Catalogue" : c.Name.Trim();
        return Enabled.Count(x => x.Catalogs.Any(k => k.Type == c.Type)) > 1 ? $"{name} · {a.Name}" : name;
    }

    /// <summary>The catalogues of one kind ("movie" or "series") from your switched-on add-ons, for Films / TV Shows.</summary>
    public static List<(StremioAddon Addon, StremioCatalog Catalog)> CatalogsOf(string type) =>
        Enabled.SelectMany(a => a.Catalogs.Where(c => c.Type == type && !c.NeedsInput).Select(c => (a, c))).ToList();

    /// <summary>
    /// Set up enough to show in Films / TV Shows: signed in, or an add-on with sources (Stremio's own catalogue alone
    /// has titles but nothing to play).
    /// </summary>
    public static bool IsSetUp => SignedIn || Addons.Any(a => a.Enabled && a.TransportUrl != CinemetaUrl);

    public static List<Views.StremioSourceItem> Items(IEnumerable<StremioStream> streams, bool rdConnected) => streams
        .Select((s, i) =>
        {
            var text = (s.Name ?? "") + "\n" + (s.Title ?? "");
            var qm = QualityRx.Match(text);
            var q = !qm.Success ? null : qm.Value.ToLowerInvariant() switch { "2160p" or "4k" or "uhd" => "4K", "sd" => "SD", var v => v };
            var size = SizeRx.Match(text);
            var lines = (s.Title ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var title = lines.FirstOrDefault() ?? s.Filename ?? s.Name ?? "Source";
            var rest = string.Join("  ·  ", lines.Skip(1).Take(2));
            var nameLine = (s.Name ?? "").Replace("\n", " ").Trim();
            var detail = string.Join("  ·  ", new[] { s.Addon?.Name, nameLine != s.Addon?.Name ? nameLine : null, rest.Length > 0 ? rest : size.Success ? size.Value : null }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            bool rdLink = s.Url != null && s.Url.Contains("real-debrid", StringComparison.OrdinalIgnoreCase);
            var item = s.Kind switch
            {
                StreamKind.Direct => new Views.StremioSourceItem
                {
                    Stream = s, Quality = q ?? "Play", Title = title, Detail = detail, Kind = rdLink ? "Real-Debrid" : "Direct",
                    CanPlay = true, PlayText = "Play",
                },
                StreamKind.Torrent => new Views.StremioSourceItem
                {
                    Stream = s, Quality = q ?? "Torrent", Title = title, Detail = detail, Kind = "Torrent",
                    CanPlay = rdConnected, PlayText = rdConnected ? "Play" : "Needs Real-Debrid",
                    Hint = rdConnected ? "Plays through your Real-Debrid account" : "Connect Real-Debrid in Settings to play torrent sources",
                },
                _ => new Views.StremioSourceItem
                {
                    Stream = s, Quality = q ?? "Web", Title = title, Detail = detail, Kind = "Web page", CanPlay = true, PlayText = "Open",
                    Hint = "Opens in your browser",
                },
            };
            return (item, i, rank: (item.CanPlay ? 0 : 100) + (s.Kind == StreamKind.External ? 50 : 0) + QualityRank(item.Quality));
        })
        .OrderBy(x => x.rank).ThenBy(x => x.i).Select(x => x.item).ToList();

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>The add-ons for Settings: name, what each offers, where it came from.</summary>
    public static List<Views.AddonItem> SettingsItems() => Addons.Select(a =>
    {
        var offers = new List<string>();
        if (a.Resources.Any(r => r.Name == "catalog") && a.Catalogs.Count > 0) offers.Add("catalogues");
        if (a.Resources.Any(r => r.Name == "meta")) offers.Add("details");
        if (a.Resources.Any(r => r.Name == "stream")) offers.Add("sources");
        var bits = new List<string>();
        if (a.Resources.Count == 0) bits.Add("not loaded yet");
        else if (offers.Count > 0) bits.Add(char.ToUpper(offers[0][0]) + string.Join(", ", offers)[1..]);
        if (!string.IsNullOrEmpty(a.Version)) bits.Add("v" + a.Version);
        bits.Add(a.FromAccount ? "from your Stremio account" : a.TransportUrl == CinemetaUrl ? "built in" : "added by address");
        return new Views.AddonItem { Addon = a, Detail = string.Join("  ·  ", bits) };
    }).ToList();
}
