using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>Streaming M3U/M3U8 parser – live channels, films and series episodes.</summary>
public static class M3uParser
{
    static readonly Regex AttrRx = new(@"([\w\-]+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    static readonly Regex SeriesRx = new(@"^(?<name>.*?)[\s\._\-]*S(?<s>\d{1,2})\s*[\._\-]?\s*E(?<e>\d{1,3})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex YearRx = new(@"\((19|20)\d{2}\)", RegexOptions.Compiled);

    public static CatalogData Parse(TextReader reader)
    {
        var data = new CatalogData();
        var liveCats = new Dictionary<string, Category>();
        var vodCats = new Dictionary<string, Category>();
        var serCats = new Dictionary<string, Category>();
        var seriesByKey = new Dictionary<string, Series>();

        Dictionary<string, string> attrs = null;
        string name = null, group = null;
        int num = 0;
        string line;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                var a = ParseAttrs(line);
                var epg = Get(a, "url-tvg") ?? Get(a, "x-tvg-url");
                if (epg != null) data.SourceEpgUrl = epg.Split(',')[0].Trim();
                continue;
            }
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                attrs = ParseAttrs(line);
                name = ExtractName(line) ?? Get(attrs, "tvg-name") ?? "Untitled";
                group = Get(attrs, "group-title");
                continue;
            }
            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                group ??= line.Substring(8).Trim();
                continue;
            }
            if (line.StartsWith('#') || attrs == null) continue;

            var url = line;
            var logo = Get(attrs, "tvg-logo");
            var g = string.IsNullOrWhiteSpace(group) ? "Uncategorised" : group.Trim();

            switch (Classify(url))
            {
                case Kind.Movie:
                    AddCat(vodCats, data.VodCategories, g);
                    var y = YearRx.Match(name);
                    data.Movies.Add(new Movie
                    {
                        Id = url, Url = url, Name = name, Poster = logo, CategoryId = g,
                        Year = y.Success ? y.Value.Trim('(', ')') : null, Added = data.Movies.Count,
                    });
                    break;

                case Kind.Series:
                    AddCat(serCats, data.SeriesCategories, g);
                    var m = SeriesRx.Match(name);
                    var sname = m.Success ? m.Groups["name"].Value.Trim(' ', '-', '_', '.', '|') : name;
                    if (sname.Length == 0) sname = name;
                    var key = g + "|" + sname.ToLowerInvariant();
                    if (!seriesByKey.TryGetValue(key, out var series))
                    {
                        series = new Series { Id = "s:" + key, Name = sname, Cover = logo, CategoryId = g, Episodes = new(), Added = data.Series.Count };
                        seriesByKey[key] = series;
                        data.Series.Add(series);
                    }
                    series.Episodes.Add(new Episode
                    {
                        Id = url, Url = url, Title = name, Image = logo,
                        Season = m.Success ? int.Parse(m.Groups["s"].Value) : 1,
                        Number = m.Success ? int.Parse(m.Groups["e"].Value) : series.Episodes.Count + 1,
                    });
                    break;

                default:
                    AddCat(liveCats, data.LiveCategories, g);
                    num++;
                    data.Channels.Add(new Channel
                    {
                        Id = url, Url = url, Num = num, Name = name, Logo = logo, CategoryId = g,
                        EpgId = Get(attrs, "tvg-id"),
                    });
                    break;
            }
            attrs = null; name = null; group = null;
        }
        return data;
    }

    enum Kind { Live, Movie, Series }

    static Kind Classify(string url)
    {
        if (url.Contains("/series/", StringComparison.OrdinalIgnoreCase)) return Kind.Series;
        if (url.Contains("/movie/", StringComparison.OrdinalIgnoreCase)) return Kind.Movie;
        var path = url.Split('?')[0];
        if (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase))
            return Kind.Movie;
        return Kind.Live;
    }

    static void AddCat(Dictionary<string, Category> map, List<Category> list, string g)
    {
        if (map.ContainsKey(g)) return;
        var c = new Category { Id = g, Name = g };
        map[g] = c;
        list.Add(c);
    }

    static Dictionary<string, string> ParseAttrs(string line)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRx.Matches(line)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    static string Get(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>The display name is everything after the first comma that follows the attributes.</summary>
    static string ExtractName(string line)
    {
        int lastQuote = line.LastIndexOf('"');
        int comma = line.IndexOf(',', lastQuote < 0 ? 0 : lastQuote);
        if (comma < 0) return null;
        var n = line.Substring(comma + 1).Trim();
        return n.Length == 0 ? null : n;
    }
}
