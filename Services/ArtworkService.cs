using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Finds artwork for a programme (a show's title → that show's picture): first the TV guide's own image, then a matching
/// show or film in your playlist, then TVmaze (a free TV database – looked up once per title and remembered).
/// </summary>
public static class ArtworkService
{
    static readonly HttpClient Client = MakeClient();
    static Dictionary<string, string> _cache; // normalised title → image URL ("" = nothing found)
    static readonly object Lock = new();
    static string CacheFile => Path.Combine(AppPaths.Root, "artwork-cache.json");

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Dingus (Windows IPTV player)");
        return c;
    }

    // "Live: Team A v Team B", "My Show (R)", "New: My Show", "My Show S3 E2", "My Show: Christmas Special" …
    static readonly Regex Noise = new(@"^(live|new|premiere|repeat|exclusive)\s*[:\-]\s*|\s*\((r|repeat|hd|new|\d{4})\)\s*$|\s*[:\-]?\s*s\d+\s*e\d+.*$|\s*-\s*series\s+\d+.*$|\s*:\s*episode\s+\d+.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Trim();
        for (int i = 0; i < 3; i++) t = Noise.Replace(t, "").Trim();
        return t;
    }

    /// <summary>Artwork for a programme, or null. Never throws.</summary>
    public static async Task<string> FindAsync(Programme p)
    {
        if (p == null) return null;
        try
        {
            var own = p.ImageUrl;
            if (!string.IsNullOrEmpty(own)) return own;

            var title = CleanTitle(p.Title);
            var key = EpgService.Norm(title);
            if (key.Length < 2) return null;

            // Your own Films / TV Shows lists.
            var cat = DataService.Instance.Catalog;
            var show = cat.Series.FirstOrDefault(s => s.Cover != null && EpgService.Norm(s.Name) == key);
            if (show != null) return show.Cover;
            var film = cat.Movies.FirstOrDefault(m => m.Poster != null && EpgService.Norm(StripYear(m.Name)) == key);
            if (film != null) return film.Poster;

            if (!SettingsStore.Current.OnlineArtwork) return null;

            // TVmaze (remembered, including "not found", so each title is asked about once).
            LoadCache();
            lock (Lock) if (_cache.TryGetValue(key, out var hit)) return hit.Length == 0 ? null : hit;
            string url = null;
            using (var resp = await Client.GetAsync("https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(title)))
            {
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                        url = (img.TryGetProperty("original", out var o) ? o.GetString() : null) ??
                              (img.TryGetProperty("medium", out var m) ? m.GetString() : null);
                }
                else if ((int)resp.StatusCode == 429) return null; // busy – try again another time, don't remember
            }
            lock (Lock) _cache[key] = url ?? "";
            SaveCache();
            return url;
        }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    static string StripYear(string name) => Regex.Replace(name ?? "", @"\s*\(\d{4}\)\s*$", "");

    static void LoadCache()
    {
        lock (Lock)
        {
            if (_cache != null) return;
            try
            {
                _cache = File.Exists(CacheFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile)) ?? new()
                    : new();
            }
            catch { _cache = new(); }
            if (_cache.Count > 3000) _cache.Clear(); // keep it small
        }
    }

    static void SaveCache()
    {
        try
        {
            string json;
            lock (Lock) json = JsonSerializer.Serialize(_cache);
            File.WriteAllText(CacheFile, json);
        }
        catch (Exception ex) { Log.Error(ex); }
    }
}
