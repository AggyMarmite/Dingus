using System.Net.Http;
using System.Text.Json;

namespace Dingus.Services;

/// <summary>
/// Team crests for the Events page, from ESPN's public team lists (no account needed). The lists – each league's
/// clubs with their crest and colour – are fetched about once a week and kept on disk; Dingus only ever asks for
/// whole lists, never for your teams by name. ESPN's feed isn't an official service: if it changes or stops,
/// games simply show the guide's artwork or a drawn card instead.
/// </summary>
public static class TeamCrests
{
    /// <summary>Leagues whose clubs are looked up, most-watched first (when two clubs share a name, the first wins).</summary>
    static readonly string[] Leagues =
    {
        "eng.1", "eng.2", "eng.3", "eng.4", "eng.5", "sco.1", "sco.2", "uefa.champions", "uefa.europa", "uefa.europa.conf",
        "esp.1", "ita.1", "ger.1", "fra.1", "por.1", "ned.1", "bel.1", "tur.1", "irl.1", "usa.1", "mex.1", "bra.1", "arg.1",
        "sau.1", "fifa.world", "uefa.euro", "eng.w.1",
    };

    public sealed class Crest
    {
        public string Name { get; set; }
        public List<string> Names { get; set; } = new();
        public string Logo { get; set; }
        public string Color { get; set; }
    }

    sealed class CacheFile
    {
        public DateTime FetchedUtc { get; set; }
        public List<Crest> Teams { get; set; } = new();
    }

    static readonly HttpClient Client = MakeClient();
    static Dictionary<string, Crest> _index = new();
    static Task _loading;
    static readonly object Gate = new();

    static string FilePath => Path.Combine(AppPaths.Root, "team-crests.json");

    /// <summary>How many clubs are known (0 until loaded).</summary>
    public static int Count { get; private set; }
    /// <summary>Why the last fetch failed, or null.</summary>
    public static string Problem { get; private set; }

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Dingus/1.0");
        return c;
    }

    /// <summary>Loads the crests (from disk, or ESPN when that's over a week old). Safe to call often.</summary>
    public static Task EnsureLoadedAsync()
    {
        lock (Gate) return _loading ??= Task.Run(LoadAsync);
    }

    static async Task LoadAsync()
    {
        CacheFile cache = null;
        try
        {
            if (File.Exists(FilePath)) cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Log.Error(ex); }
        if (cache != null) Use(cache.Teams);
        if (cache != null && cache.Teams.Count > 0 && DateTime.UtcNow - cache.FetchedUtc < TimeSpan.FromDays(7)) return;

        var all = new List<Crest>();
        int failed = 0;
        foreach (var chunk in Leagues.Chunk(4))
        {
            var lists = await Task.WhenAll(chunk.Select(async l =>
            {
                try { return await FetchLeague(l); }
                catch (Exception ex) { Interlocked.Increment(ref failed); Problem = $"{l}: {Http.Friendly(ex)}"; return new List<Crest>(); }
            }));
            foreach (var l in lists) all.AddRange(l);
        }
        if (all.Count == 0) { Log.Info("Team crests: none fetched (" + (Problem ?? "no teams") + ")"); return; }
        if (failed == 0) Problem = null;
        Use(all);
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new CacheFile { FetchedUtc = DateTime.UtcNow, Teams = all })); }
        catch (Exception ex) { Log.Error(ex); }
    }

    static async Task<List<Crest>> FetchLeague(string league)
    {
        var res = new List<Crest>();
        var json = await Client.GetStringAsync($"https://site.api.espn.com/apis/site/v2/sports/soccer/{league}/teams");
        using var doc = JsonDocument.Parse(json);
        Collect(doc.RootElement, res);
        return res;
    }

    /// <summary>Finds every team in the response (anything with a display name and logos), wherever it sits.</summary>
    static void Collect(JsonElement e, List<Crest> res)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Collect(x, res); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (e.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String &&
            e.TryGetProperty("logos", out var logos) && logos.ValueKind == JsonValueKind.Array)
        {
            string logo = null;
            foreach (var l in logos.EnumerateArray())
                if (l.TryGetProperty("href", out var h) && h.ValueKind == JsonValueKind.String) { logo = h.GetString(); break; }
            if (logo != null)
            {
                var c = new Crest { Name = dn.GetString(), Logo = logo };
                foreach (var f in new[] { "displayName", "shortDisplayName", "name", "location", "nickname" })
                    if (e.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                        c.Names.Add(v.GetString());
                if (e.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.String && col.GetString()?.Length == 6)
                    c.Color = "#" + col.GetString();
                res.Add(c);
                return;
            }
        }
        foreach (var p in e.EnumerateObject()) Collect(p.Value, res);
    }

    static void Use(List<Crest> teams)
    {
        var idx = new Dictionary<string, Crest>();
        foreach (var t in teams)
            foreach (var n in t.Names)
            {
                var k = Events.Norm(n);
                if (k.Trim().Length >= 3) idx.TryAdd(k, t);
            }
        lock (Gate) { _index = idx; Count = teams.Count; }
    }

    /// <summary>The crest for a team as a guide writes it ("Man Utd", "Spurs"…), or null.</summary>
    public static Crest Find(string team)
    {
        if (string.IsNullOrWhiteSpace(team)) return null;
        Dictionary<string, Crest> idx;
        lock (Gate) idx = _index;
        if (idx.Count == 0) return null;
        foreach (var v in Events.Variants(team))
            if (idx.TryGetValue(v, out var c)) return c;
        return null;
    }
}
