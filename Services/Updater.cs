using System.Net.Http;
using System.Text.Json;

namespace Dingus.Services;

/// <summary>
/// In-app updates from the GitHub releases page: checks the latest release once a day, offers it,
/// downloads the installer and runs it silently (it closes Dingus, updates it and opens it again).
/// </summary>
public static partial class Updater
{
    public const string Repo = "AggyMarmite/Dingus";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    static readonly HttpClient Client = MakeClient();

    public record Release(Version Version, string Tag, string Notes, string InstallerUrl, long Size, string PageUrl);

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Dingus/{Current.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>The newest release on GitHub, or null if there isn't one with an installer.</summary>
    public static async Task<Release> LatestAsync()
    {
        using var resp = await Client.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        string url = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
#if LINUX
            if (name.StartsWith("Dingus-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
#else
            if (name.StartsWith("Dingus-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
#endif
            {
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
            }
        }
        if (url == null) return null;
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : ReleasesPage;
        return new Release(v, tag, notes, url, size, page);
    }

    public static bool IsNewer(Version v) => v > new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));

    public static async Task<string> DownloadAsync(Release r, Action<double> progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Dingus-update");
        Directory.CreateDirectory(dir);
#if LINUX
        var path = Path.Combine(dir, $"Dingus-{r.Version.ToString(3)}{AssetSuffix}");
#else
        var path = Path.Combine(dir, $"Dingus-Setup-{r.Version.ToString(3)}.exe");
#endif
        using var resp = await Client.GetAsync(r.InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? r.Size;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0 && DateTime.UtcNow - last > TimeSpan.FromMilliseconds(150))
                {
                    last = DateTime.UtcNow;
                    progress(done * 100.0 / total);
                }
            }
        }
        if (r.Size > 0 && new FileInfo(path).Length != r.Size) throw new IOException("the download was incomplete");
        return path;
    }

    /// <summary>Release notes without the markdown clutter.</summary>
    public static string Tidy(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var lines = md.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !l.StartsWith("**Full Changelog**"))
            .Select(l => l.StartsWith("## ") ? l[3..].ToUpperInvariant() : l.StartsWith("* ") || l.StartsWith("- ") ? "•  " + l[2..] : l)
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\*\*(.+?)\*\*", "$1"))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @" by @\S+ in https://\S+", ""));
        return string.Join("\n", lines).Trim();
    }
}
