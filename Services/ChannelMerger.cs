using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Works out which playlist entries are the same channel ("UK: BBC One", "UK: BBC One HD",
/// "UK: BBC One FHD (Backup)") so the guide can show them as one row, and orders the versions
/// best-first for playback and automatic fallback.
/// </summary>
public static class ChannelMerger
{
    // Country/region tag at the start ("UK:", "UK |", "[US]", "|DE|") – kept, so UK and US Fox stay separate.
    static readonly Regex CountryRx = new(@"^\s*(?:\[\s*([a-z]{2,3})\s*\]|\|\s*([a-z]{2,3})\s*\||([a-z]{2,3})\s*[:|]|([A-Z]{2,3})\s+-\s+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex BackupRx = new(@"\b(?:backup|back\s*up|bkp|b/u|alt(?:ernative)?|reserve|mirror|feed\s*\d|server\s*\d|link\s*\d|\(\s*\d\s*\))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex UhdRx = new(@"\b(?:uhd|4k|2160p?|ultra\s*hd)\b|ᵁᴴᴰ", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex FhdRx = new(@"\b(?:fhd|1080[pi]?|full\s*hd)\b|ᶠᴴᴰ", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex HdRx = new(@"\b(?:hd|720p?|hq)\b|ᴴᴰ|ʰᵈ", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SdRx = new(@"\b(?:sd|576[pi]?|480p?|lq|low)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Same key = same channel. Empty when the name gives nothing to go on (never merged).</summary>
    public static string Key(Channel c)
    {
        var name = c.Name ?? "";
        var m = CountryRx.Match(name);
        string country = "";
        if (m.Success)
            for (int g = 1; g <= 4; g++)
                if (m.Groups[g].Success) { country = m.Groups[g].Value.ToLowerInvariant(); break; }
        var core = EpgService.Norm(BackupRx.Replace(name, " "));
        return core.Length < 2 ? "" : country + "|" + core;
    }

    /// <summary>4 = 4K, 3 = 1080p, 2 = HD/unknown, 1 = SD.</summary>
    public static int Rank(Channel c)
    {
        var n = c.Name ?? "";
        if (UhdRx.IsMatch(n)) return 4;
        if (FhdRx.IsMatch(n)) return 3;
        if (SdRx.IsMatch(n)) return 1;
        return 2; // "HD" or nothing stated – most IPTV streams without a tag are HD
    }

    public static bool IsBackup(Channel c) => BackupRx.IsMatch(c.Name ?? "");

    public static string Label(Channel c) => Rank(c) switch
    {
        4 => "4K",
        3 => "1080p",
        1 => "SD",
        _ => HdRx.IsMatch(c.Name ?? "") ? "HD" : "Standard",
    } + (IsBackup(c) ? " (backup)" : "");

    /// <summary>
    /// Versions in the order to try them: main streams before backups, best quality first – but
    /// never above <paramref name="maxRank"/> unless nothing else exists (e.g. on battery: 1080p max).
    /// </summary>
    public static List<Channel> Order(IReadOnlyList<Channel> variants, int maxRank)
    {
        return variants
            .Select((c, i) => (c, i, r: Rank(c), b: IsBackup(c)))
            .OrderBy(x => x.r > maxRank ? 1 : 0)          // within the quality cap first
            .ThenBy(x => x.b ? 1 : 0)                     // main before backup
            .ThenByDescending(x => x.r > maxRank ? -x.r : x.r) // best first (over-cap ones: lowest first)
            .ThenBy(x => x.i)                             // keep playlist order otherwise
            .Select(x => x.c)
            .ToList();
    }

    /// <summary>The highest quality to pick right now, from Settings and (in battery-aware mode) the power source.</summary>
    public static int CurrentMaxRank()
    {
        var s = SettingsStore.Current;
        int cap = s.PreferredQuality switch { "1080" => 3, "720" => 2, _ => 4 };
        if (s.BatteryAware && PowerService.OnBattery) cap = Math.Min(cap, 3); // 4K is heavy on battery
        return cap;
    }
}
