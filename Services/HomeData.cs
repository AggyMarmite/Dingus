using Dingus.Models;
using Dingus.Views;

namespace Dingus.Services;

/// <summary>What Home's rows are made of – shared by the Windows and Linux apps.</summary>
public static class HomeData
{
    /// <summary>The <paramref name="n"/> items with the biggest <paramref name="key"/>, biggest first – one pass, no full sort.
    /// Ties keep the playlist's order, like a stable sort would.</summary>
    public static List<T> Newest<T>(IEnumerable<T> items, Func<T, long> key, int n)
    {
        var heap = new PriorityQueue<(T Item, int Index), (long Key, int Index)>(n + 1, Comparer<(long Key, int Index)>.Create((a, b) =>
            a.Key != b.Key ? a.Key.CompareTo(b.Key) : b.Index.CompareTo(a.Index)));
        int i = 0;
        foreach (var it in items)
        {
            heap.Enqueue((it, i), (key(it), i));
            if (heap.Count > n) heap.Dequeue(); // drops the oldest
            i++;
        }
        var res = new List<(T Item, int Index, long Key)>(heap.Count);
        while (heap.TryDequeue(out var e, out var pr)) res.Add((e.Item, e.Index, pr.Key));
        res.Reverse();
        return res.Select(x => x.Item).ToList();
    }

    /// <summary>My List: saved films and shows the playlist still has (hidden categories left out), newest first.</summary>
    public static List<HomePosterCard> MyList(CatalogData data, HashSet<string> hiddenVod, HashSet<string> hiddenSeries)
    {
        var saved = HistoryStore.MyList;
        if (saved.Count == 0) return new List<HomePosterCard>();
        var films = saved.Where(x => x.Kind == "movie").Select(x => x.Id).ToHashSet();
        var shows = saved.Where(x => x.Kind == "series").Select(x => x.Id).ToHashSet();
        var found = new Dictionary<string, HomePosterCard>();
        if (films.Count > 0)
            foreach (var m in data.Movies)
                if (films.Contains(m.Id) && !hiddenVod.Contains(m.CategoryId))
                    found.TryAdd("movie:" + m.Id, new HomePosterCard { Id = m.Id, IsMovie = true, Title = m.Name, Image = m.Poster, Subtitle = m.Year ?? "Film", IsNew = false });
        if (shows.Count > 0)
            foreach (var x in data.Series)
                if (shows.Contains(x.Id) && !hiddenSeries.Contains(x.CategoryId))
                    found.TryAdd("series:" + x.Id, new HomePosterCard { Id = x.Id, IsMovie = false, Title = x.Name, Image = x.Cover, Subtitle = x.Year ?? "TV show", IsNew = false });
        return saved.Select(x => found.GetValueOrDefault(x.Kind + ":" + x.Id)).Where(c => c != null).Take(20).ToList();
    }
}
