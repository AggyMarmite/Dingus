using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Film and show details from TMDB, as on the Fire TV: title logos and background pictures, the UK age rating and
/// score, the cast with photos (and each person's other titles in your playlist), similar titles, the trending and
/// top rated charts, and where an episode's intro, recap and credits are (TheIntroDB). Answers are kept on disk so
/// each title is only looked up once. This product uses the TMDB API but is not endorsed or certified by TMDB.
/// </summary>
public static class Tmdb
{
    public const string Trending = "trending", TopRated = "top";

    public record Ratings(string Age, double Score, int Votes);
    public record Actor(string Id, string Name, string Role, string Photo);
    public record Work(string Title, int Year, bool Tv, string TmdbId = "", string Also = null);
    public record Part(long StartMs, long EndMs); // EndMs -1 = to the end
    public record Segments(Part Intro, Part Recap, Part Credits);

    static readonly HttpClient Client = MakeClient();
    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Dingus");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>The key built in (a GitHub secret put in at build time); empty in a home-made build.</summary>
    static readonly string Key = typeof(Tmdb).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "TmdbKey")?.Value?.Trim() ?? "";

    /// <summary>Whether lookups can be made (a key is built in and online artwork isn't switched off).</summary>
    public static bool Enabled => Key.Length > 0 && SettingsStore.Current.OnlineArtwork;

    static string Dir { get { var d = Path.Combine(AppPaths.Root, "tmdb"); Directory.CreateDirectory(d); return d; } }

    // ------------------------------------------------------------------ the kept answers

    /// <summary>A small dictionary kept in a file: each title's logo, ratings and so on, looked up once.</summary>
    sealed class Store
    {
        readonly string _file;
        Dictionary<string, string> _map;
        readonly object _lock = new();
        Timer _save;
        public Store(string name) { _file = name; }
        string FilePath => Path.Combine(Dir, _file);
        void Load()
        {
            if (_map != null) return;
            try { _map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new(); }
            catch { _map = new(); }
        }
        public bool TryGet(string k, out string v) { lock (_lock) { Load(); return _map.TryGetValue(k, out v); } }
        public void Set(string k, string v)
        {
            lock (_lock)
            {
                Load();
                _map[k] = v ?? "";
                _save ??= new Timer(_ => Flush(), null, 2000, Timeout.Infinite);
            }
        }
        void Flush()
        {
            string json;
            lock (_lock) { json = JsonSerializer.Serialize(_map); _save?.Dispose(); _save = null; }
            try { File.WriteAllText(FilePath, json); } catch (Exception ex) { Log.Error(ex); }
        }
    }

    static readonly Store Logos = new("logos.json"), Backdrops = new("backdrops.json"), Ids = new("ids.json"), Rating = new("ratings.json");

    static string KeyFor(bool tv, string id) => (tv ? "tv:" : "movie:") + id;

    // ------------------------------------------------------------------ requests

    static async Task<JsonNode> GetAsync(string path, CancellationToken ct = default)
    {
        bool bearer = Key.Length > 60; // the long "read access token" goes in a header, the short key in the address
        var url = "https://api.themoviedb.org/3" + path + (bearer ? "" : (path.Contains('?') ? "&" : "?") + "api_key=" + Key);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);
        using var resp = await Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    static string S(JsonNode n, string name) => n?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?[name]?.ToString();
    static double D(JsonNode n, string name) { try { return n?[name]?.GetValue<double>() ?? 0; } catch { return double.TryParse(S(n, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0; } }
    static int Year(string date) => date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : 0;
    static int YearOf(string y) => int.TryParse((y ?? "").Trim().Split('-', ' ')[0], out var n) ? n : 0;

    /// <summary>The TMDB number of a film or show: the provider's if it gave one, otherwise a search by title and year.</summary>
    public static async Task<string> IdForAsync(bool tv, string itemId, string title, string year, string known, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(known) && known != "0" && known.All(char.IsDigit)) return known;
        var k = KeyFor(tv, itemId);
        if (Ids.TryGet(k, out var kept)) return kept.Length > 0 ? kept : null;
        if (!Enabled) return null;
        var name = Titles.Clean(title);
        int y = YearOf(year);
        if (y == 0) { var m = Regex.Match(title ?? "", @"\((19|20)\d\d\)"); if (m.Success) y = int.Parse(m.Value.Trim('(', ')')); }
        var type = tv ? "tv" : "movie";
        var q = Uri.EscapeDataString(name);
        var yr = y > 0 ? (tv ? "&first_air_date_year=" : "&year=") + y : "";
        var results = (await GetAsync($"/search/{type}?query={q}{yr}&include_adult=false", ct))?["results"]?.AsArray();
        if ((results == null || results.Count == 0) && y > 0)
            results = (await GetAsync($"/search/{type}?query={q}&include_adult=false", ct))?["results"]?.AsArray();
        var id = results?.FirstOrDefault()?["id"]?.ToString();
        Ids.Set(k, id ?? "");
        return id;
    }

    // ------------------------------------------------------------------ logo and background

    /// <summary>The title logo if it's already known (no network): null = none or not looked up.</summary>
    public static string CachedLogo(bool tv, string itemId) => Logos.TryGet(KeyFor(tv, itemId), out var v) && v.Length > 0 ? v : null;
    public static string CachedBackdrop(bool tv, string itemId) => Backdrops.TryGet(KeyFor(tv, itemId), out var v) && v.Length > 0 ? v : null;

    /// <summary>Looks up the title logo and a background picture (once per title). Never throws.</summary>
    public static async Task<(string Logo, string Backdrop)> ImagesAsync(bool tv, string itemId, string title, string year, string tmdbId, CancellationToken ct = default)
    {
        var k = KeyFor(tv, itemId);
        if (Logos.TryGet(k, out var l) && Backdrops.TryGet(k, out var b)) return (l.Length > 0 ? l : null, b.Length > 0 ? b : null);
        if (!Enabled) return (null, null);
        try
        {
            var id = await IdForAsync(tv, itemId, title, year, tmdbId, ct);
            string logo = null, back = null;
            if (id != null)
            {
                var o = await GetAsync($"/{(tv ? "tv" : "movie")}/{id}/images?include_image_language=en,null", ct);
                double best = -1;
                foreach (var x in o?["logos"]?.AsArray() ?? new JsonArray())
                {
                    var lang = S(x, "iso_639_1");
                    var score = (lang == "en" ? 10 : string.IsNullOrEmpty(lang) ? 5 : 0) + D(x, "vote_average") + (D(x, "aspect_ratio") >= 1.5 ? 1 : 0);
                    if (score > best) { best = score; logo = "https://image.tmdb.org/t/p/w500" + S(x, "file_path"); }
                }
                best = -1;
                foreach (var x in o?["backdrops"]?.AsArray() ?? new JsonArray())
                {
                    var score = (string.IsNullOrEmpty(S(x, "iso_639_1")) ? 10 : 0) + D(x, "vote_average");
                    if (score > best) { best = score; back = "https://image.tmdb.org/t/p/w1280" + S(x, "file_path"); }
                }
            }
            Logos.Set(k, logo); Backdrops.Set(k, back);
            return (logo, back);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); return (null, null); } // not kept: tried again another time
    }

    // ------------------------------------------------------------------ ratings

    /// <summary>The UK certificate and score if already known (no network).</summary>
    public static Ratings CachedRatings(bool tv, string itemId) => Rating.TryGet(KeyFor(tv, itemId), out var v) ? ParseRatings(v) : null;

    static Ratings ParseRatings(string v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        var p = v.Split('|');
        return new Ratings(p[0].Length > 0 ? p[0] : null,
            p.Length > 1 && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0,
            p.Length > 2 && int.TryParse(p[2], out var n) ? n : 0);
    }

    /// <summary>The UK age rating and TMDB's score (once per title). Never throws.</summary>
    public static async Task<Ratings> RatingsAsync(bool tv, string itemId, string title, string year, string tmdbId, CancellationToken ct = default)
    {
        var k = KeyFor(tv, itemId);
        if (Rating.TryGet(k, out var kept)) return ParseRatings(kept);
        if (!Enabled) return null;
        try
        {
            var id = await IdForAsync(tv, itemId, title, year, tmdbId, ct);
            Ratings r = null;
            if (id != null)
            {
                var type = tv ? "tv" : "movie";
                var o = await GetAsync($"/{type}/{id}?append_to_response=" + (tv ? "content_ratings" : "release_dates"), ct);
                string age = null;
                foreach (var c in o?[tv ? "content_ratings" : "release_dates"]?["results"]?.AsArray() ?? new JsonArray())
                {
                    if (S(c, "iso_3166_1") != "GB") continue;
                    if (tv) age = S(c, "rating");
                    else foreach (var d in c?["release_dates"]?.AsArray() ?? new JsonArray())
                        if (!string.IsNullOrWhiteSpace(S(d, "certification"))) { age = S(d, "certification"); break; }
                    break;
                }
                r = new Ratings(string.IsNullOrWhiteSpace(age) ? null : age.Trim(), D(o, "vote_average"), (int)D(o, "vote_count"));
            }
            Rating.Set(k, r == null ? "" : $"{r.Age}|{r.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{r.Votes}");
            return r;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    /// <summary>"7.4" when there's a score worth showing (enough votes), else null.</summary>
    public static string ScoreText(Ratings r) => r != null && r.Score > 0 && r.Votes >= 5 ? r.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : null;

    // ------------------------------------------------------------------ cast and people

    static readonly Dictionary<string, List<Actor>> CastCache = new();

    /// <summary>The director (or a show's creator) then the main cast, with photos (kept for the session). Never throws.</summary>
    public static async Task<List<Actor>> CastAsync(bool tv, string itemId, string title, string year, string tmdbId, CancellationToken ct = default)
    {
        var k = KeyFor(tv, itemId);
        lock (CastCache) if (CastCache.TryGetValue(k, out var c)) return c;
        if (!Enabled) return new();
        try
        {
            var id = await IdForAsync(tv, itemId, title, year, tmdbId, ct);
            var list = new List<Actor>();
            if (id != null)
            {
                var o = await GetAsync(tv ? $"/tv/{id}/aggregate_credits" : $"/movie/{id}/credits", ct);
                const string img = "https://image.tmdb.org/t/p/w185";
                string Photo(JsonNode x) => S(x, "profile_path") is { } p && p.StartsWith('/') ? img + p : null;
                var director = (o?["crew"]?.AsArray() ?? new JsonArray()).FirstOrDefault(x =>
                    S(x, "job") == "Director" || (x?["jobs"]?.AsArray() ?? new JsonArray()).Any(j => S(j, "job") == "Director"));
                if (director != null) list.Add(new Actor(S(director, "id"), S(director, "name"), "Director", Photo(director)));
                foreach (var x in (o?["cast"]?.AsArray() ?? new JsonArray()).Take(15))
                {
                    var role = S(x, "character");
                    if (string.IsNullOrEmpty(role)) role = S(x?["roles"]?.AsArray()?.FirstOrDefault(), "character") ?? "";
                    list.Add(new Actor(S(x, "id"), S(x, "name"), role, Photo(x)));
                }
            }
            lock (CastCache) CastCache[k] = list;
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); return new(); }
    }

    static readonly string[] CrewJobs = { "Director", "Screenplay", "Writer", "Creator", "Executive Producer", "Novel" };

    /// <summary>Everything someone's been in (acting first, then directing and writing). Never throws.</summary>
    public static async Task<List<Work>> PersonWorksAsync(string personId, CancellationToken ct = default)
    {
        var works = new List<Work>();
        if (!Enabled || string.IsNullOrEmpty(personId)) return works;
        try
        {
            var o = await GetAsync($"/person/{personId}/combined_credits", ct);
            var seen = new HashSet<string>();
            foreach (var part in new[] { "cast", "crew" })
                foreach (var c in o?[part]?.AsArray() ?? new JsonArray())
                {
                    bool tv = S(c, "media_type") == "tv";
                    if (part == "crew" && !CrewJobs.Contains(S(c, "job"))) continue;
                    var t = S(c, tv ? "name" : "title");
                    if (string.IsNullOrEmpty(t) || !seen.Add($"{tv}:{S(c, "id")}")) continue;
                    var orig = S(c, tv ? "original_name" : "original_title");
                    works.Add(new Work(t, Year(S(c, tv ? "first_air_date" : "release_date")), tv, S(c, "id") ?? "", orig != t ? orig : null));
                }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); }
        return works;
    }

    /// <summary>A YouTube link to the official trailer (for titles the provider gave none). Never throws.</summary>
    public static async Task<string> TrailerAsync(bool tv, string itemId, string title, string year, string tmdbId, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        try
        {
            var id = await IdForAsync(tv, itemId, title, year, tmdbId, ct);
            if (id == null) return null;
            var vids = ((await GetAsync($"/{(tv ? "tv" : "movie")}/{id}/videos?include_video_language=en,null", ct))?["results"]?.AsArray() ?? new JsonArray())
                .Where(v => S(v, "site") == "YouTube").ToList();
            var pick = vids.FirstOrDefault(v => S(v, "type") == "Trailer" && v?["official"]?.ToString() == "true")
                       ?? vids.FirstOrDefault(v => S(v, "type") == "Trailer") ?? vids.FirstOrDefault(v => S(v, "type") == "Teaser");
            return pick == null ? null : "https://www.youtube.com/watch?v=" + S(pick, "key");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    // ------------------------------------------------------------------ similar titles and charts

    static readonly Dictionary<string, List<Work>> SimilarCache = new();

    /// <summary>TMDB's recommendations (then similar titles) for a film or show (kept for the session). Never throws.</summary>
    public static async Task<List<Work>> SimilarAsync(bool tv, string itemId, string title, string year, string tmdbId, CancellationToken ct = default)
    {
        var k = KeyFor(tv, itemId);
        lock (SimilarCache) if (SimilarCache.TryGetValue(k, out var c)) return c;
        var works = new List<Work>();
        if (!Enabled) return works;
        try
        {
            var id = await IdForAsync(tv, itemId, title, year, tmdbId, ct);
            if (id != null)
                foreach (var path in new[] { "recommendations", "similar" })
                {
                    foreach (var o in (await GetAsync($"/{(tv ? "tv" : "movie")}/{id}/{path}", ct))?["results"]?.AsArray() ?? new JsonArray())
                    {
                        var t = S(o, tv ? "name" : "title");
                        if (!string.IsNullOrEmpty(t)) works.Add(new Work(t, Year(S(o, tv ? "first_air_date" : "release_date")), tv, S(o, "id") ?? ""));
                    }
                    if (works.Count >= 30) break;
                }
            lock (SimilarCache) SimilarCache[k] = works;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); }
        return works;
    }

    /// <summary>
    /// A chart (trending today, or top rated of all time) as TMDB lists it – kept on disk, looked up again every few
    /// hours (trending) or once a day (top rated). Match it to the playlist with <see cref="Match"/>. Never throws.
    /// </summary>
    public static async Task<List<Work>> ChartAsync(string list, bool tv, CancellationToken ct = default)
    {
        var file = Path.Combine(Dir, $"chart-{list}-{(tv ? "tv" : "movie")}.json");
        var maxAge = list == Trending ? TimeSpan.FromHours(4) : TimeSpan.FromHours(24);
        List<Work> kept = null;
        try { if (File.Exists(file)) kept = JsonSerializer.Deserialize<List<Work>>(File.ReadAllText(file)); } catch { }
        if (kept != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < maxAge) return kept;
        if (!Enabled) return kept ?? new();
        try
        {
            var type = tv ? "tv" : "movie";
            var path = list == Trending ? $"/trending/{type}/day" : $"/{type}/top_rated";
            int pages = list == Trending ? 3 : 10;
            var works = new List<Work>();
            for (int page = 1; page <= pages; page++)
            {
                var r = (await GetAsync($"{path}?page={page}&language=en-GB", ct))?["results"]?.AsArray();
                if (r == null || r.Count == 0) break;
                foreach (var o in r)
                {
                    var t = S(o, tv ? "name" : "title");
                    if (!string.IsNullOrEmpty(t)) works.Add(new Work(t, Year(S(o, tv ? "first_air_date" : "release_date")), tv, S(o, "id") ?? ""));
                }
            }
            if (works.Count > 0) { File.WriteAllText(file, JsonSerializer.Serialize(works)); return works; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); }
        return kept ?? new();
    }

    // ------------------------------------------------------------------ matching to the playlist

    static readonly Regex NotAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);
    static readonly Regex TrailingYear = new(@"\s*([\-–:|(\[]\s*)?((?:19|20)\d\d)[)\]]?\s*$", RegexOptions.Compiled);
    static readonly Regex Tags = new(@"\s*[\[(][^\])]*[\])]\s*$", RegexOptions.Compiled);

    static string Norm(string s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return NotAlnum.Replace(sb.ToString().Replace("&", " and "), "");
    }

    /// <summary>The ways a title can be written: as is, without a bracketed tag or its year, without "The" at the start.</summary>
    static HashSet<string> Keys(string title, int year)
    {
        var o = new HashSet<string>();
        var t = (title ?? "").Trim();
        for (int i = 0; i < 3; i++)
        {
            o.Add(Norm(t));
            var y = TrailingYear.Match(t);
            var noYear = y.Success && (y.Groups[1].Length > 0 || (year > 0 && Math.Abs(int.Parse(y.Groups[2].Value) - year) <= 1)) ? t[..y.Index] : t;
            var n = Tags.Replace(noYear, "").Trim();
            if (n.Length == 0 || n == t) break;
            t = n;
        }
        foreach (var k in o.ToList()) if (k.StartsWith("the") && k.Length > 6) o.Add(k[3..]);
        o.Remove("");
        return o;
    }

    /// <summary>An index of a playlist's titles for matching (build once per list, off the UI thread for big lists).</summary>
    public sealed class Index<T>
    {
        internal readonly Dictionary<string, List<(T Item, int Year)>> ByKey = new();
        public bool IsEmpty => ByKey.Count == 0;
        public Index(IEnumerable<T> items, Func<T, string> name, Func<T, string> year)
        {
            foreach (var x in items)
            {
                var y = YearOf(year(x));
                var title = Titles.Clean(name(x));
                foreach (var k in Keys(title, y))
                {
                    if (!ByKey.TryGetValue(k, out var l)) ByKey[k] = l = new List<(T, int)>(1);
                    l.Add((x, y));
                }
            }
        }
    }

    /// <summary>What of <paramref name="works"/> is in the playlist, in the same order (no repeats).</summary>
    public static List<T> Match<T>(Index<T> ix, IEnumerable<Work> works, int max = 40)
    {
        var res = new List<T>();
        var seen = new HashSet<T>();
        if (ix == null || ix.IsEmpty) return res;
        foreach (var w in works)
        {
            var cands = new List<(T Item, int Year)>();
            foreach (var name in new[] { w.Title, w.Also }.Where(n => !string.IsNullOrEmpty(n)))
                foreach (var k in Keys(name, w.Year))
                    if (ix.ByKey.TryGetValue(k, out var l)) cands.AddRange(l);
            if (cands.Count == 0) continue;
            (T Item, int Year)? pick = null;
            if (w.Year == 0) pick = cands[0];
            else
            {
                var fit = cands.FirstOrDefault(c => c.Year <= 0 || Math.Abs(w.Year - c.Year) <= 1);
                if (!EqualityComparer<T>.Default.Equals(fit.Item, default)) pick = fit;
                else if (Norm(w.Title).Length >= 6)
                {
                    var near = cands.OrderBy(c => Math.Abs(w.Year - c.Year)).First();
                    if (w.Tv || Math.Abs(w.Year - near.Year) <= 3) pick = near;
                }
            }
            if (pick is { } p && seen.Add(p.Item)) { res.Add(p.Item); if (res.Count >= max) break; }
        }
        return res;
    }

    // ------------------------------------------------------------------ intros

    static readonly Dictionary<string, Segments> IntroCache = new();

    /// <summary>Where an episode's intro, recap and credits are (TheIntroDB, by the show's TMDB number). Never throws.</summary>
    public static async Task<Segments> SegmentsAsync(string showTmdbId, int season, int episode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(showTmdbId) || season <= 0 || episode <= 0) return null;
        var k = $"{showTmdbId}:{season}:{episode}";
        lock (IntroCache) if (IntroCache.TryGetValue(k, out var c)) return c;
        try
        {
            using var resp = await Client.GetAsync($"https://api.theintrodb.org/v4/media?tmdb_id={showTmdbId}&season={season}&episode={episode}", ct);
            Segments s = null;
            if (resp.IsSuccessStatusCode)
            {
                var o = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                Part P(string name)
                {
                    var p = o?[name]?.AsArray()?.FirstOrDefault();
                    if (p == null) return null;
                    long start = p["start_ms"] is JsonValue a && a.TryGetValue<long>(out var st) ? st : 0;
                    long end = p["end_ms"] is JsonValue b && b.TryGetValue<long>(out var en) ? en : -1;
                    return end != -1 && end <= start + 3000 ? null : new Part(start, end);
                }
                s = new Segments(P("intro"), P("recap"), P("credits"));
            }
            else if ((int)resp.StatusCode != 404) return null; // (not kept: tried again next time)
            lock (IntroCache) IntroCache[k] = s;
            return s;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error(ex); return null; }
    }
}
