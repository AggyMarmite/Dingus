using System.Windows;
using System.Windows.Controls;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

public enum LibraryMode { Movies, Series }

/// <summary>Films and TV Shows share this page: category rail, poster grid and a details view.</summary>
public partial class LibraryView : UserControl, IPage
{
    const string AllId = "__all", ContinueId = "__continue";
    const double CardWidth = 172; // 156 + margins

    readonly LibraryMode _mode;
    List<PosterItem> _all = new();
    Dictionary<string, PosterItem> _byId = new();
    List<PosterItem> _filtered = new();
    CategoryItem _continueCat;
    string _cat = AllId, _search = "", _sort;
    int _cols;
    bool _suppress, _dirty = true, _shown;

    PosterItem _current;
    List<EpisodeItem> _episodes = new(); // whole show, in watch order
    CancellationTokenSource _detailCts;

    public string Title => _mode == LibraryMode.Movies ? "Films" : "TV Shows";
    public bool SupportsSearch => true;
    public string SearchHint => _mode == LibraryMode.Movies ? "Search all films" : "Search all shows";

    string Kind => _mode == LibraryMode.Movies ? "movie" : "episode";

    public LibraryView(LibraryMode mode)
    {
        _mode = mode;
        InitializeComponent();
        _sort = mode == LibraryMode.Movies ? "added" : "name";
        _suppress = true;
        (mode == LibraryMode.Movies ? SortAdded : SortName).IsChecked = true;
        _suppress = false;

        // Built only while the page is open: Films and TV Shows can be tens of thousands of titles,
        // so they don't sit in memory while you're in the guide.
        DataService.Instance.CatalogChanged += () => { if (_shown) Rebuild(); else _dirty = true; };
        HistoryStore.Changed += OnHistoryChanged;
        PosterList.SizeChanged += (_, _) => Regroup(false);
    }

    // ------------------------------------------------------------------ IPage

    public void ApplySearch(string query)
    {
        _search = query ?? "";
        if (_search.Length > 0) CloseDetail();
        ApplyFilter();
    }

    public void OnShown()
    {
        _shown = true;
        if (_dirty) Rebuild();
    }

    public void OnHidden()
    {
        _shown = false;
        CloseDetail();
        _all = new List<PosterItem>();
        _byId = new Dictionary<string, PosterItem>();
        _filtered = new List<PosterItem>();
        PosterList.ItemsSource = null;
        CategoryList.ItemsSource = null;
        _continueCat = null;
        _dirty = true;
        Memory.CompactSoon();
    }

    // ------------------------------------------------------------------ data

    void Rebuild()
    {
        _dirty = false;
        var ds = DataService.Instance;
        var data = ds.Catalog;
        List<Category> cats;

        if (_mode == LibraryMode.Movies)
        {
            var hidden = ds.HiddenSet("vod");
            cats = data.VodCategories.Where(c => !hidden.Contains(c.Id)).ToList();
            _all = data.Movies.Where(m => !hidden.Contains(m.CategoryId)).Select(m => new PosterItem
            {
                Id = m.Id, Title = m.Name, Image = m.Poster, CategoryId = m.CategoryId, Added = m.Added,
                Rating = m.Rating, Subtitle = Meta(m.Year, m.Rating), Source = m,
            }).ToList();
        }
        else
        {
            var hidden = ds.HiddenSet("series");
            cats = data.SeriesCategories.Where(c => !hidden.Contains(c.Id)).ToList();
            _all = data.Series.Where(s => !hidden.Contains(s.CategoryId)).Select(s => new PosterItem
            {
                Id = s.Id, Title = s.Name, Image = s.Cover, CategoryId = s.CategoryId, Added = s.Added,
                Rating = s.Rating, Subtitle = Meta(s.Year, s.Rating), Source = s,
            }).ToList();
        }
        _byId = new Dictionary<string, PosterItem>();
        foreach (var p in _all) _byId.TryAdd(p.Id, p);
        ApplyProgress();

        var counts = _all.GroupBy(p => p.CategoryId).ToDictionary(g => g.Key, g => g.Count());
        _continueCat = new CategoryItem { Id = ContinueId, Name = "Continue watching", Count = ContinueItems().Count };
        var items = new List<CategoryItem>
        {
            new() { Id = AllId, Name = _mode == LibraryMode.Movies ? "All films" : "All shows", Count = _all.Count },
            _continueCat,
        };
        foreach (var c in cats)
            if (counts.TryGetValue(c.Id, out var n)) items.Add(new CategoryItem { Id = c.Id, Name = c.Name, Count = n });

        _suppress = true;
        CategoryList.ItemsSource = items;
        var sel = items.FirstOrDefault(i => i.Id == _cat) ?? items[0];
        _cat = sel.Id;
        CategoryList.SelectedItem = sel;
        _suppress = false;

        ApplyFilter();
    }

    static string Meta(string year, double rating)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(year)) parts.Add(year);
        if (rating > 0) parts.Add($"★ {rating:0.#}");
        return string.Join("  ·  ", parts);
    }

    /// <summary>Films: in-progress films. Shows: shows with a recently watched episode. Newest first.</summary>
    List<PosterItem> ContinueItems()
    {
        var res = new List<PosterItem>();
        var seen = new HashSet<string>();
        foreach (var e in HistoryStore.Recent(Kind))
        {
            if (_mode == LibraryMode.Movies && !e.CanResume) continue;
            var id = _mode == LibraryMode.Movies ? e.ItemId : e.SeriesId;
            if (id == null || !seen.Add(id) || !_byId.TryGetValue(id, out var p)) continue;
            res.Add(p);
            if (res.Count >= 40) break;
        }
        return res;
    }

    void ApplyProgress()
    {
        if (_mode != LibraryMode.Movies) return; // show posters don't carry a bar – episodes do
        foreach (var p in _all)
        {
            var e = HistoryStore.Get("m:" + p.Id);
            p.Progress = e != null && e.CanResume ? e.Fraction : 0;
        }
    }

    void OnHistoryChanged()
    {
        ApplyProgress();
        if (_continueCat != null) _continueCat.Count = ContinueItems().Count;
        if (_cat == ContinueId && _search.Length == 0) ApplyFilter();
        UpdateEpisodeProgress();
        if (_current?.Source is Movie) UpdateMovieButtons();
    }

    void ApplyFilter()
    {
        IEnumerable<PosterItem> q;
        bool keepOrder = false;
        if (_search.Length > 0) q = _all.Where(p => p.Title.Contains(_search, StringComparison.OrdinalIgnoreCase));
        else if (_cat == ContinueId) { q = ContinueItems(); keepOrder = true; }
        else if (_cat != AllId) q = _all.Where(p => p.CategoryId == _cat);
        else q = _all;

        if (!keepOrder)
            q = _sort switch
            {
                "name" => q.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase),
                "rating" => q.OrderByDescending(p => p.Rating).ThenBy(p => p.Title),
                _ => q.OrderByDescending(p => p.Added),
            };
        _filtered = q.ToList();

        HeaderText.Text = _search.Length > 0
            ? $"Results for “{_search}”"
            : (CategoryList.SelectedItem as CategoryItem)?.Name ?? Title;
        CountText.Text = _filtered.Count == 1 ? "1 title" : $"{_filtered.Count:N0} titles";

        bool empty = _filtered.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            if (_all.Count == 0)
            {
                EmptyTitle.Text = _mode == LibraryMode.Movies ? "No films yet" : "No TV shows yet";
                EmptyBody.Text = "Your playlist doesn't include any, or it hasn't been loaded yet.";
            }
            else if (_cat == ContinueId && _search.Length == 0)
            {
                EmptyTitle.Text = "Nothing in progress";
                EmptyBody.Text = _mode == LibraryMode.Movies
                    ? "Films you start watching will appear here."
                    : "Shows you start watching will appear here.";
            }
            else { EmptyTitle.Text = "No matches"; EmptyBody.Text = "Try a different search or category."; }
        }
        Regroup(true);
    }

    /// <summary>
    /// Posters are grouped into rows so the list can virtualise – only the rows on screen exist,
    /// which keeps memory flat even with tens of thousands of titles.
    /// </summary>
    void Regroup(bool force)
    {
        var width = PosterList.ActualWidth - 40;
        if (width <= 0) width = 1200;
        int cols = Math.Max(1, (int)(width / CardWidth));
        if (!force && cols == _cols) return;
        _cols = cols;
        var rows = _filtered.Chunk(cols).Select(c => new PosterRow { Items = c }).ToList();
        PosterList.ItemsSource = rows;
        if (force && rows.Count > 0) PosterList.ScrollIntoView(rows[0]);
    }

    // ------------------------------------------------------------------ interaction

    void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CategoryList.SelectedItem is not CategoryItem c) return;
        _cat = c.Id;
        CloseDetail();
        if (_search.Length > 0) MainWindow.Instance?.ClearSearch();
        else ApplyFilter();
    }

    void Sort_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || (sender as FrameworkElement)?.Tag is not string tag) return;
        _sort = tag;
        ApplyFilter();
    }

    void Poster_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PosterItem item) OpenDetail(item);
    }

    /// <summary>Opens a film/show's details by id (from Home). Call after the page is shown.</summary>
    public void OpenItem(string id)
    {
        if (_dirty) Rebuild();
        if (id != null && _byId.TryGetValue(id, out var item)) OpenDetail(item);
    }

    async void OpenDetail(PosterItem item)
    {
        _current = item;
        bool movie = _mode == LibraryMode.Movies;
        DetailPane.Visibility = Visibility.Visible;
        BackText.Text = movie ? "Films" : "TV Shows";
        DetailTitle.Text = item.Title;
        DetailMeta.Text = item.Subtitle;
        DetailPlot.Text = "";
        DetailCredits.Text = "";
        DetailStatus.Text = "Loading details…";
        ImageLoader.SetUrl(DetailPoster, item.Image);
        ImageLoader.SetUrl(DetailBackdrop, null);
        PlayButton.Visibility = movie ? Visibility.Visible : Visibility.Collapsed;
        StartOverButton.Visibility = Visibility.Collapsed;
        DetailProgress.Visibility = Visibility.Collapsed;
        SeriesPart.Visibility = movie ? Visibility.Collapsed : Visibility.Visible;
        SeasonList.ItemsSource = null;
        EpisodeList.ItemsSource = null;
        _episodes = new List<EpisodeItem>();
        DetailScroll.ScrollToTop();
        if (movie) UpdateMovieButtons();

        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;

        try
        {
            var d = movie
                ? await DataService.Instance.GetMovieDetailsAsync((Movie)item.Source, ct)
                : await DataService.Instance.GetSeriesDetailsAsync((Series)item.Source, ct);
            if (ct.IsCancellationRequested || _current != item) return;

            DetailStatus.Text = "";
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(d.Year)) meta.Add(d.Year);
            if (!string.IsNullOrEmpty(d.Duration)) meta.Add(d.Duration);
            if (!string.IsNullOrEmpty(d.Rating) && d.Rating != "0") meta.Add("★ " + d.Rating);
            else if (item.Rating > 0) meta.Add($"★ {item.Rating:0.#}");
            if (!string.IsNullOrEmpty(d.Genre)) meta.Add(d.Genre);
            if (meta.Count > 0) DetailMeta.Text = string.Join("  ·  ", meta);

            DetailPlot.Text = d.Plot ?? "";
            var credits = new List<string>();
            if (!string.IsNullOrEmpty(d.Cast)) credits.Add("Starring  " + d.Cast);
            if (!string.IsNullOrEmpty(d.Director)) credits.Add("Director  " + d.Director);
            DetailCredits.Text = string.Join("\n", credits);

            if (!string.IsNullOrEmpty(d.Backdrop)) ImageLoader.SetUrl(DetailBackdrop, d.Backdrop);
            if (string.IsNullOrEmpty(item.Image) && !string.IsNullOrEmpty(d.Poster)) ImageLoader.SetUrl(DetailPoster, d.Poster);

            if (!movie) ShowEpisodes(d.Episodes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (_current == item) DetailStatus.Text = "Couldn't load details: " + Http.Friendly(ex);
        }
    }

    void ShowEpisodes(List<Episode> eps)
    {
        // Watch order: seasons ascending, specials (season 0) last, episodes ascending.
        var ordered = eps.OrderBy(e => e.Season == 0 ? int.MaxValue : e.Season).ThenBy(e => e.Number).ToList();
        _episodes = ordered.Select((e, i) => new EpisodeItem(e, i)).ToList();
        UpdateEpisodeProgress();

        var seasons = _episodes
            .GroupBy(ep => ep.Episode.Season)
            .Select(g => new SeasonItem
            {
                Number = g.Key,
                Label = g.Key == 0 ? "Specials" : $"Season {g.Key}",
                Episodes = g.ToList(),
            }).ToList();
        SeasonList.ItemsSource = seasons;
        if (seasons.Count == 0) { DetailStatus.Text = "No episodes available."; return; }

        // Open on the season you were last watching.
        var last = _episodes
            .Select(ep => (ep, h: HistoryStore.Get("e:" + ep.Episode.Id)))
            .Where(x => x.h != null)
            .OrderByDescending(x => x.h.UpdatedUtc)
            .Select(x => x.ep)
            .FirstOrDefault();
        var season = last == null ? seasons[0] : seasons.FirstOrDefault(s => s.Number == last.Episode.Season) ?? seasons[0];
        SeasonList.SelectedItem = season;
    }

    void UpdateEpisodeProgress()
    {
        foreach (var ep in _episodes)
        {
            var h = HistoryStore.Get("e:" + ep.Episode.Id);
            ep.Watched = h?.Finished == true;
            ep.Progress = h != null && !h.Finished ? h.Fraction : 0;
        }
    }

    void UpdateMovieButtons()
    {
        if (_current?.Source is not Movie m) return;
        var h = HistoryStore.Get("m:" + m.Id);
        bool resume = h != null && h.CanResume;
        PlayText.Text = resume ? "Resume from " + Fmt(h.PositionMs) : "Play";
        StartOverButton.Visibility = resume ? Visibility.Visible : Visibility.Collapsed;
        DetailProgress.Visibility = resume ? Visibility.Visible : Visibility.Collapsed;
        DetailProgress.Value = resume ? h.Fraction : 0;
    }

    static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    void SeasonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EpisodeList.ItemsSource = (SeasonList.SelectedItem as SeasonItem)?.Episodes;
    }

    void Back_Click(object sender, RoutedEventArgs e) => CloseDetail();

    void CloseDetail()
    {
        _detailCts?.Cancel();
        _current = null;
        _episodes = new List<EpisodeItem>();
        DetailPane.Visibility = Visibility.Collapsed;
        ImageLoader.SetUrl(DetailBackdrop, null);
    }

    void PlayMovie_Click(object sender, RoutedEventArgs e) => PlayMovie(resume: true);
    void StartOver_Click(object sender, RoutedEventArgs e) => PlayMovie(resume: false);

    void PlayMovie(bool resume)
    {
        if (_current?.Source is not Movie m) return;
        var url = DataService.Instance.MovieUrl(m);
        if (url == null) return;
        MainWindow.Instance?.PlayVod(new VodItem
        {
            Kind = "movie",
            Ext = m.Ext,
            Title = m.Name,
            Subtitle = DetailMeta.Text,
            Url = url,
            Image = m.Poster,
            ResumeKey = "m:" + m.Id,
            ItemId = m.Id,
        }, resume);
    }

    void Episode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EpisodeItem ep) return;
        var item = BuildEpisode(ep.Index);
        if (item != null) MainWindow.Instance?.PlayVod(item, resume: true);
    }

    /// <summary>
    /// Builds the player item for an episode, with a link to the one after it (for "Up next").
    /// It captures this show's episode list, so it keeps working after you leave the page.
    /// </summary>
    VodItem BuildEpisode(int index) => BuildEpisode(_episodes, _current?.Source as Series, index);

    static VodItem BuildEpisode(List<EpisodeItem> list, Series s, int index)
    {
        if (s == null || index < 0 || index >= list.Count) return null;
        var ep = list[index].Episode;
        var url = DataService.Instance.EpisodeUrl(ep);
        if (url == null) return null;
        var next = index + 1 < list.Count ? list[index + 1].Episode : null;
        return new VodItem
        {
            Kind = "episode",
            Ext = ep.Ext,
            Title = s.Name,
            Subtitle = $"{Code(ep)}  ·  {ep.Title}",
            Url = url,
            Image = ep.Image ?? s.Cover,
            ResumeKey = "e:" + ep.Id,
            ItemId = ep.Id,
            SeriesId = s.Id,
            Next = next == null ? null : () => BuildEpisode(list, s, index + 1),
            NextLabel = next == null ? null : $"{Code(next)}  ·  {next.Title}",
        };
    }

    static string Code(Episode e) => $"S{e.Season:00}E{e.Number:00}";
}
