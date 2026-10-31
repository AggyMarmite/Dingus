using System.Windows;
using System.Windows.Controls;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Stremio inside Films / TV Shows: your add-ons' catalogues under the playlist's categories, a Stremio title's page,
/// and "More sources" (your add-ons' sources) under the playlist's own films and episodes.
/// </summary>
public partial class LibraryView
{
    StremioCatalogItem _stCat;
    readonly List<PosterItem> _stItems = new();
    int _stSkip;
    bool _stLoading, _stMore, _stInit;
    CancellationTokenSource _stCts, _moreCts;
    StremioMeta _stShow; // the playlist show, as Stremio knows it (for episodes' sources)

    string StType => _mode == LibraryMode.Movies ? "movie" : "series";

    void InitStremio()
    {
        if (_stInit) return;
        _stInit = true;
        Stremio.Changed += () => Dispatcher.InvokeAsync(() => { if (_shown) BuildStremioRail(); else _dirty = true; });
        RealDebrid.Changed += () => Dispatcher.InvokeAsync(() => { if (_shown) BuildStremioRail(); else _dirty = true; });
        StDetail.Closed += CloseStremioDetail;
        PosterList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (_stCat != null && !_stCat.IsRealDebrid && _stMore && !_stLoading && e.VerticalOffset + e.ViewportHeight > e.ExtentHeight - 700)
                LoadStremio(reset: false);
        }));
    }

    /// <summary>The Stremio part of the list on the left (none when Stremio isn't set up).</summary>
    void BuildStremioRail()
    {
        var items = new List<StremioCatalogItem>();
        if (Stremio.IsSetUp)
        {
            foreach (var (a, c) in Stremio.CatalogsOf(StType))
                items.Add(new StremioCatalogItem { Addon = a, Catalog = c, Name = Stremio.CatalogName(c, a), From = a.Name });
            if (_mode == LibraryMode.Movies && RealDebrid.Connected)
                items.Add(new StremioCatalogItem { IsRealDebrid = true, Name = "Your Real-Debrid files", From = "Real-Debrid" });
        }
        StremioRail.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var keep = _stCat == null ? null : items.FirstOrDefault(i => i.IsRealDebrid == _stCat.IsRealDebrid && i.Catalog?.Id == _stCat.Catalog?.Id &&
                                                                     i.Addon?.TransportUrl == _stCat.Addon?.TransportUrl);
        _suppress = true;
        StremioCatList.ItemsSource = items;
        StremioCatList.SelectedItem = keep;
        _suppress = false;
        if (_stCat != null && keep == null) { _stCat = null; SelectPlaylistCategory(); }
        else _stCat = keep;
        // Only Stremio (no playlist titles): open on its first catalogue.
        if (_stCat == null && _all.Count == 0 && items.Count > 0 && _cat == AllId && _search.Length == 0)
            StremioCatList.SelectedItem = items[0];
        if (Stremio.Addons.Any(a => a.Enabled && a.Resources.Count == 0)) _ = RefreshManifestsAsync();
    }

    bool _manifestsAsked;

    async Task RefreshManifestsAsync()
    {
        if (_manifestsAsked) return;
        _manifestsAsked = true;
        await Stremio.EnsureManifestsAsync(); // raises Changed → the rail fills in
    }

    void SelectPlaylistCategory()
    {
        if (CategoryList.ItemsSource is not IEnumerable<CategoryItem> cats) return;
        _suppress = true;
        CategoryList.SelectedItem = cats.FirstOrDefault(c => c.Id == _cat) ?? cats.FirstOrDefault();
        _suppress = false;
        _cat = (CategoryList.SelectedItem as CategoryItem)?.Id ?? AllId;
        ApplyFilter();
    }

    void StremioCatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || StremioCatList.SelectedItem is not StremioCatalogItem c) return;
        _stCat = c;
        _suppress = true;
        CategoryList.SelectedItem = null;
        _suppress = false;
        CloseDetail();
        CloseStremioDetail();
        if (_search.Length > 0) MainWindow.Instance?.ClearSearchBoxOnly();
        _search = "";
        LoadStremio(reset: true);
    }

    /// <summary>A Stremio catalogue (or your Real-Debrid files), a page at a time – the next page as you scroll down.</summary>
    async void LoadStremio(bool reset)
    {
        var cat = _stCat;
        if (cat == null) return;
        _stCts?.Cancel();
        var cts = _stCts = new CancellationTokenSource();
        if (reset) { _stSkip = 0; _stItems.Clear(); _stMore = false; }
        _stLoading = true;
        SortPanel.Visibility = Visibility.Collapsed;
        HeaderText.Text = cat.Name;
        EmptyState.Visibility = Visibility.Collapsed;
        EmptyButton.Visibility = Visibility.Collapsed;
        PosterList.Visibility = cat.IsRealDebrid ? Visibility.Collapsed : Visibility.Visible;
        RdPane.Visibility = cat.IsRealDebrid ? Visibility.Visible : Visibility.Collapsed;
        if (reset) { _filtered = new List<PosterItem>(); Regroup(true); RdList.ItemsSource = null; }
        CountText.Text = "loading…";
        try
        {
            if (cat.IsRealDebrid)
            {
                var files = await RealDebrid.FilesAsync(cts.Token);
                if (cts.IsCancellationRequested) return;
                RdList.ItemsSource = files.Select(f => new RdFileItem
                {
                    File = f,
                    Detail = string.Join("  ·  ", new[] { Stremio.Size(f.Bytes), f.Added?.ToLocalTime().ToString("d MMM yyyy"), f.Source == "torrent" ? "Torrent" : "Download" }
                        .Where(x => !string.IsNullOrEmpty(x))),
                }).ToList();
                CountText.Text = files.Count == 1 ? "1 file" : $"{files.Count} files";
                if (files.Count == 0) ShowEmpty("No video files in your Real-Debrid cloud", "Files you add on real-debrid.com, and torrents played from your add-ons, show up here.");
                return;
            }
            var page = await Stremio.CatalogAsync(cat.Addon, cat.Catalog, null, _stSkip, cts.Token);
            if (cts.IsCancellationRequested) return;
            var have = _stItems.Select(i => i.Id).ToHashSet();
            foreach (var m in page) if (have.Add(m.Id)) _stItems.Add(StremioPoster(m));
            _stSkip += page.Count;
            _stMore = cat.Catalog.CanPage && page.Count > 0;
            _filtered = _stItems.ToList();
            CountText.Text = _filtered.Count == 1 ? "1 title" : $"{_filtered.Count:N0} titles";
            Regroup(reset);
            if (_filtered.Count == 0) ShowEmpty("This catalogue is empty", "Try another one on the left.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex);
            CountText.Text = "";
            if (_stItems.Count == 0) ShowEmpty("Couldn't load this", Http.Friendly(ex) + ".", manage: true);
        }
        finally { if (_stCts == cts) _stLoading = false; }
    }

    void ShowEmpty(string title, string body, bool manage = false)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyButton.Visibility = manage ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    static PosterItem StremioPoster(StremioMeta m) => new()
    {
        Id = "st:" + m.Id, Title = m.Name, Image = m.Poster, Source = m,
        Subtitle = string.Join("  ·  ", new[] { m.ReleaseInfo, string.IsNullOrEmpty(m.ImdbRating) ? null : "★ " + m.ImdbRating }.Where(x => !string.IsNullOrEmpty(x))),
    };

    /// <summary>Leaves the Stremio catalogue for the playlist's own list.</summary>
    void LeaveStremio()
    {
        _stCts?.Cancel();
        _stCat = null;
        _stLoading = false;
        _suppress = true;
        StremioCatList.SelectedItem = null;
        _suppress = false;
        SortPanel.Visibility = Visibility.Visible;
        PosterList.Visibility = Visibility.Visible;
        RdPane.Visibility = Visibility.Collapsed;
        EmptyButton.Visibility = Visibility.Collapsed;
    }

    void ManageStremio_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenSettingsSection("stremio");

    // ------------------------------------------------------------------ a Stremio title's page

    /// <summary>Opens a Stremio film or show (from a catalogue, Search, or Home).</summary>
    public void OpenStremio(StremioMeta m)
    {
        CloseDetail();
        StDetail.Visibility = Visibility.Visible;
        StDetail.OpenMeta(m, _stCat?.Name ?? (_mode == LibraryMode.Movies ? "Films" : "TV Shows"));
    }

    void CloseStremioDetail()
    {
        if (StDetail.Visibility != Visibility.Visible) return;
        StDetail.Close();
        StDetail.Visibility = Visibility.Collapsed;
    }

    async void RdFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RdFileItem it || it.Busy) return;
        it.Busy = true;
        try
        {
            var url = await RealDebrid.PlayableAsync(it.File);
            MainWindow.Instance?.PlayVod(new VodItem
            {
                Kind = "stremio", Title = it.File.Name, Subtitle = "Real-Debrid", Url = url, ResumeKey = "rd:" + it.File.Id, ItemId = it.File.Id,
            }, resume: true);
        }
        catch (Exception ex) { Log.Error(ex); MainWindow.Instance?.ShowToast(ex is InvalidOperationException ? ex.Message : "Couldn't play that: " + Http.Friendly(ex)); }
        finally { it.Busy = false; }
    }

    // ------------------------------------------------------------------ More sources, under the playlist's own titles

    bool HasSourceAddons => Stremio.Enabled.Any(a => a.Serves("stream", StType, null));

    /// <summary>A playlist film's page: finds it in Stremio and lists your add-ons' sources under it.</summary>
    async void FindMoreSources(PosterItem item, string year)
    {
        _moreCts?.Cancel();
        MoreSources.Clear();
        EpisodeSources.Clear();
        EpisodeSources.Visibility = Visibility.Collapsed;
        _stShow = null;
        bool movie = _mode == LibraryMode.Movies;
        MoreSources.Visibility = Visibility.Collapsed;
        if (!HasSourceAddons) return;
        var cts = _moreCts = new CancellationTokenSource();
        if (movie)
        {
            MoreSources.Visibility = Visibility.Visible;
            MoreSources.Message(null, "Looking for it in your Stremio add-ons…");
        }
        try
        {
            var meta = await Stremio.FindAsync(StType, item.Title, year, cts.Token);
            if (cts.IsCancellationRequested || _current != item) return;
            if (movie)
            {
                if (meta == null) { MoreSources.Clear(); MoreSources.Visibility = Visibility.Collapsed; } // not one Stremio knows – nothing to add
                else MoreSources.Show(meta, null);
            }
            else
            {
                _stShow = meta;
                foreach (var ep in _episodes) ep.HasMoreSources = meta != null;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (movie && _current == item) MoreSources.Message(null, "Couldn't look it up: " + Http.Friendly(ex));
        }
    }

    /// <summary>An episode's "More sources": your add-ons' sources for it, under the episode list.</summary>
    void EpisodeSources_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // not the row's own click (which plays it)
        if ((sender as FrameworkElement)?.DataContext is not EpisodeItem ep || _stShow == null) return;
        var v = new StremioVideo
        {
            Id = $"{_stShow.Id}:{ep.Episode.Season}:{ep.Episode.Number}", Season = ep.Episode.Season, Episode = ep.Episode.Number, Title = ep.Episode.Title,
        };
        EpisodeSources.Visibility = Visibility.Visible;
        EpisodeSources.Show(_stShow, v, $"S{ep.Episode.Season:00}E{ep.Episode.Number:00}");
        Dispatcher.InvokeAsync(() => EpisodeSources.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void ClearMoreSources()
    {
        _moreCts?.Cancel();
        _stShow = null;
        MoreSources.Clear();
        MoreSources.Visibility = Visibility.Collapsed;
        EpisodeSources.Clear();
        EpisodeSources.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ self-test
    internal int SelfTestStremioCatalogues => (StremioCatList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal void SelfTestStremioCatalogue(int i)
    {
        if ((StremioCatList.ItemsSource as List<StremioCatalogItem>) is { Count: > 0 } l) StremioCatList.SelectedItem = l[Math.Min(i, l.Count - 1)];
    }
    internal int SelfTestTitles => _filtered.Count;
    internal void SelfTestOpenFirst() { if (_filtered.FirstOrDefault()?.Source is StremioMeta m) OpenStremio(m); }
    internal StremioDetail SelfTestStremioDetail => StDetail;
    internal int SelfTestMoreSources => MoreSources.SelfTestCount;
    internal int SelfTestEpisodeSources => EpisodeSources.SelfTestCount;
    internal void SelfTestEpisodeMoreSources()
    {
        if ((EpisodeList.ItemsSource as List<EpisodeItem>)?.FirstOrDefault() is { } ep)
            EpisodeSources_Click(new Button { DataContext = ep }, new RoutedEventArgs());
    }
    internal void SelfTestCloseStremio() => CloseStremioDetail();
}
