#if LINUX
using Avalonia.Media;
#else
using System.Windows.Media;
#endif
using Dingus.Models;

namespace Dingus.Services;

/// <summary>Colour-codes guide programmes by their XMLTV category (sport, film, news, kids, factual, entertainment).</summary>
public static class Genres
{
    static readonly SolidColorBrush Sport = Make(0x22, 0xC5, 0x5E);
    static readonly SolidColorBrush Film = Make(0xA8, 0x55, 0xF7);
    static readonly SolidColorBrush News = Make(0x3B, 0x82, 0xF6);
    static readonly SolidColorBrush Kids = Make(0xF5, 0x9E, 0x0B);
    static readonly SolidColorBrush Factual = Make(0x14, 0xB8, 0xA6);
    static readonly SolidColorBrush Entertainment = Make(0xEC, 0x48, 0x99);
    static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
#if !LINUX
        br.Freeze();
#endif
        return br;
    }

    /// <summary>Colour for a genre, or null (no stripe) when it's unknown.</summary>
#if LINUX
    public static IBrush Brush(string genre)
#else
    public static Brush Brush(string genre)
#endif
    {
        if (string.IsNullOrWhiteSpace(genre)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(genre, out var hit)) return hit;
            var g = genre.ToLowerInvariant();
            SolidColorBrush b =
                Has(g, "sport", "football", "soccer", "racing", "motor", "f1", "cricket", "rugby", "golf", "tennis", "boxing", "darts", "snooker", "nfl", "nba", "ufc", "wrestling") ? Sport :
                Has(g, "film", "movie", "cinema") ? Film :
                Has(g, "news", "current affairs", "weather", "politic", "business") ? News :
                Has(g, "kid", "child", "animation", "cartoon", "family") ? Kids :
                Has(g, "documentar", "factual", "nature", "history", "science", "educat", "travel") ? Factual :
                Has(g, "entertain", "drama", "comedy", "soap", "reality", "series", "show", "music", "talk", "game") ? Entertainment :
                null;
            Cache[genre] = b;
            return b;
        }
    }

    static readonly System.Text.RegularExpressions.Regex MatchRx = new(@"\b(?:v|vs)\.?\s", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex SportChannelRx = new(@"sport|tnt|dazn|eurosport|premier|f1|racing|golf|cricket|nfl|nba|espn|bein|skysp", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Sport, from the guide's genre – or, when the guide has no genres, "Team v Team" / "Live …" on a sports channel.</summary>
    public static bool IsSport(Programme p, string channelName)
    {
        if (p == null) return false;
        if (!string.IsNullOrWhiteSpace(p.Genre)) return ReferenceEquals(Brush(p.Genre), Sport);
        bool sportsChannel = channelName != null && SportChannelRx.IsMatch(channelName);
        return sportsChannel && (MatchRx.IsMatch(p.Title ?? "") || (p.Title ?? "").StartsWith("Live", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsFilm(Programme p) => p != null && !string.IsNullOrWhiteSpace(p.Genre) && ReferenceEquals(Brush(p.Genre), Film);

    static bool Has(string g, params string[] words)
    {
        foreach (var w in words) if (g.Contains(w)) return true;
        return false;
    }
}
