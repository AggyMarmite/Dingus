using System.Text.RegularExpressions;

namespace Dingus.Services;

/// <summary>Small text helpers shared by the pages: tidy titles and file sizes.</summary>
public static class Titles
{
    static readonly Regex Clutter = new(@"^\s*(?:[A-Z]{2,4}\s*[|:\-–]\s*)+|\s*[\[(]?(?:4k|uhd|2160p|1080p|720p|fhd|hd|multi|sub|dub)[\])]?\s*$|\s*\(\d{4}\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"EN | The Long Journey (2021) 4K" → "The Long Journey".</summary>
    public static string Clean(string name)
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

    /// <summary>"1.2 GB", "640 MB".</summary>
    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>
    /// Removes settings files from features that have been taken out of Dingus (they could hold sign-in tokens).
    /// </summary>
    public static void RemoveRetiredFiles()
    {
        foreach (var f in new[] { "stremio.json", "realdebrid.json" })
            try { var p = Path.Combine(AppPaths.Root, f); if (File.Exists(p)) File.Delete(p); } catch (Exception ex) { Log.Error(ex); }
    }
}
