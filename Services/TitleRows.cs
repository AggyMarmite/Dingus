using Dingus.Models;
using Dingus.Views;

namespace Dingus.Services;

/// <summary>
/// The rows of wide title-art cards on Films, TV Shows and Home – Trending this week, Top rated and "Because you
/// watched…" – matched against the playlist, plus filling in pictures, logos and the age / score tags as they're found.
/// Shared by Windows and Linux; the screens pass a way to run things on their UI thread.
/// </summary>
public static class TitleRows
{
    static readonly object Lock = new();
    static object _filmsSrc, _showsSrc;
    static Tmdb.Index<Movie> _films;
    static Tmdb.Index<Series> _shows;

    static Tmdb.Index<Movie> Films(CatalogData d)
    {
        lock (Lock)
        {
            if (_films == null || !ReferenceEquals(_filmsSrc, d.Movies)) { _filmsSrc = d.Movies; _films = new Tmdb.Index<Movie>(d.Movies, m => m.Name, m => m.Year); }
            return _films;
        }
    }

    static Tmdb.Index<Series> Shows(CatalogData d)
    {
        lock (Lock)
        {
            if (_shows == null || !ReferenceEquals(_showsSrc, d.Series)) { _showsSrc = d.Series; _shows = new Tmdb.Index<Series>(d.Series, s => s.Name, s => s.Year); }
            return _shows;
        }
    }

    static WideCard Card(Movie m, string number = null) => new()
    {
        Id = m.Id, IsMovie = true, Title = Titles.Clean(m.Name), Year = m.Year, Number = number,
        Image = Tmdb.CachedBackdrop(false, m.Id) ?? m.Poster, Logo = Tmdb.CachedLogo(false, m.Id),
    };

    static WideCard Card(Series s, string number = null) => new()
    {
        Id = s.Id, IsMovie = false, Title = Titles.Clean(s.Name), Year = s.Year, Number = number,
        Image = Tmdb.CachedBackdrop(true, s.Id) ?? s.Cover, Logo = Tmdb.CachedLogo(true, s.Id),
    };

    /// <summary>A chart (trending or top rated) as cards for what's in the playlist, numbered. Off the UI thread.</summary>
    public static async Task<List<WideCard>> ChartAsync(string list, bool tv, int max = 20)
    {
        var data = DataService.Instance.Catalog;
        var works = await Tmdb.ChartAsync(list, tv);
        if (works.Count == 0) return new();
        return await Task.Run(() =>
        {
            var ds = DataService.Instance;
            int n = 0;
            if (tv)
            {
                var hidden = ds.HiddenSet("series");
                return Tmdb.Match(Shows(data), works, max * 2).Where(s => !hidden.Contains(s.CategoryId)).Take(max).Select(s => Card(s, (++n).ToString())).ToList();
            }
            var hiddenVod = ds.HiddenSet("vod");
            return Tmdb.Match(Films(data), works, max * 2).Where(m => !hiddenVod.Contains(m.CategoryId)).Take(max).Select(m => Card(m, (++n).ToString())).ToList();
        });
    }

    /// <summary>
    /// "Because you watched …": titles like the last film (or show) you watched that are in the playlist.
    /// Returns the title it's based on and the cards, or (null, empty).
    /// </summary>
    public static async Task<(string Title, List<WideCard> Cards)> BecauseAsync(bool tv, int max = 20)
    {
        var data = DataService.Instance.Catalog;
        var last = HistoryStore.Recent(tv ? "episode" : "movie").FirstOrDefault(e => (tv ? e.SeriesId : e.ItemId) != null && (e.Finished || e.PositionMs > 600_000));
        if (last == null) return (null, new());
        string id = tv ? last.SeriesId : last.ItemId;
        string name, year;
        if (tv) { var s = data.Series.FirstOrDefault(x => x.Id == id); if (s == null) return (null, new()); name = s.Name; year = s.Year; }
        else { var m = data.Movies.FirstOrDefault(x => x.Id == id); if (m == null) return (null, new()); name = m.Name; year = m.Year; }
        var works = await Tmdb.SimilarAsync(tv, id, name, year, null);
        if (works.Count == 0) return (null, new());
        var cards = await Task.Run(() =>
        {
            var ds = DataService.Instance;
            if (tv)
            {
                var hidden = ds.HiddenSet("series");
                return Tmdb.Match(Shows(data), works, max * 2).Where(s => s.Id != id && !hidden.Contains(s.CategoryId)).Take(max).Select(s => Card(s)).ToList();
            }
            var hiddenVod = ds.HiddenSet("vod");
            return Tmdb.Match(Films(data), works, max * 2).Where(m => m.Id != id && !hiddenVod.Contains(m.CategoryId)).Take(max).Select(m => Card(m)).ToList();
        });
        return (Titles.Clean(name), cards);
    }

    /// <summary>Someone's films and shows that are in the playlist (newest first). Off the UI thread.</summary>
    public static async Task<(List<Movie> Films, List<Series> Shows)> PersonAsync(string personId)
    {
        var data = DataService.Instance.Catalog;
        var works = await Tmdb.PersonWorksAsync(personId);
        return await Task.Run(() =>
        {
            int Y(string y) => int.TryParse((y ?? "").Split('-')[0], out var n) ? n : 0;
            var films = Tmdb.Match(Films(data), works.Where(w => !w.Tv), 200).OrderByDescending(m => Y(m.Year)).ToList();
            var shows = Tmdb.Match(Shows(data), works.Where(w => w.Tv), 200).OrderByDescending(s => Y(s.Year)).ToList();
            return (films, shows);
        });
    }

    // ------------------------------------------------------------------ filling in as they're found

    static readonly SemaphoreSlim Gate = new(3);

    /// <summary>Looks up the background picture and logo of each card that hasn't got them yet (a few at a time).</summary>
    public static void FillImages(IEnumerable<WideCard> cards, Action<Action> ui)
    {
        if (!Tmdb.Enabled) return;
        foreach (var c in cards.ToList())
        {
            if (c.HasLogo && c.Image != null) continue;
            _ = Task.Run(async () =>
            {
                await Gate.WaitAsync();
                try
                {
                    var (logo, back) = await Tmdb.ImagesAsync(!c.IsMovie, c.Id, c.Title, c.Year, c.TmdbId);
                    if (logo != null || back != null) ui(() => { if (logo != null) c.Logo = logo; if (back != null) c.Image = back; });
                }
                catch { }
                finally { Gate.Release(); }
            });
        }
    }

    static readonly HashSet<string> Asked = new();

    /// <summary>The age and score tags for posters: what's known straight away, and the rest looked up a few at a time.</summary>
    public static void FillRatings(IEnumerable<PosterItem> items, bool tv, Action<Action> ui)
    {
        foreach (var p in items)
        {
            if (p.HasAge || p.HasScore) continue;
            var known = Tmdb.CachedRatings(tv, p.Id);
            if (known != null) { p.Age = known.Age; p.Score = Tmdb.ScoreText(known); continue; }
            if (!Tmdb.Enabled) continue;
            lock (Asked) if (!Asked.Add((tv ? "t:" : "m:") + p.Id)) continue;
            var year = p.Source is Movie m ? m.Year : (p.Source as Series)?.Year;
            _ = Task.Run(async () =>
            {
                await Gate.WaitAsync();
                try
                {
                    var r = await Tmdb.RatingsAsync(tv, p.Id, p.Title, year, null);
                    if (r != null) ui(() => { p.Age = r.Age; p.Score = Tmdb.ScoreText(r); });
                }
                catch { }
                finally { Gate.Release(); }
            });
        }
    }
}

/// <summary>Search's recent searches: a search is remembered when you open one of its results.</summary>
public static class RecentSearches
{
    const int Max = 8;
    public static IReadOnlyList<string> All => SettingsStore.Current.RecentSearches;

    public static void Add(string q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return;
        var l = SettingsStore.Current.RecentSearches;
        l.RemoveAll(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase));
        l.Insert(0, q);
        if (l.Count > Max) l.RemoveRange(Max, l.Count - Max);
        SettingsStore.Save();
    }

    public static void Remove(string q)
    {
        if (SettingsStore.Current.RecentSearches.RemoveAll(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase)) > 0) SettingsStore.Save();
    }
}
