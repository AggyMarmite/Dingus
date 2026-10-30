using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Stremio: your add-ons' catalogues and search, a title's details (a show's episodes) and every add-on's sources for
/// it, plus the files in your Real-Debrid cloud. Torrent sources play through Real-Debrid. The add-on side is in
/// <see cref="Stremio"/>, Real-Debrid in <see cref="RealDebrid"/>.
/// </summary>
public partial class StremioView : UserControl, IPage
{
    public string Title => "Stremio";
    public bool SupportsSearch => false;
    public string SearchHint => "";

    bool _shown, _suppress, _dirty = true;
    StremioCatalogItem _cat;
    int _skip;
    readonly List<PosterItem> _items = new();
    CancellationTokenSource _browseCts, _detailCts;
    StremioMeta _meta;
    StremioVideo _episode;
    readonly DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    public StremioView()
    {
        InitializeComponent();
        _queryTimer.Tick += (_, _) => { _queryTimer.Stop(); Browse(reset: true); };
        Stremio.Changed += () => Dispatcher.InvokeAsync(() => { _dirty = true; if (_shown) Rebuild(); });
        RealDebrid.Changed += () => Dispatcher.InvokeAsync(() => { _dirty = true; if (_shown) Rebuild(); });
    }

    // ------------------------------------------------------------------ IPage

    public void ApplySearch(string query) { }

    public async void OnShown()
    {
        _shown = true;
        if (_dirty) Rebuild();
        if (Stremio.Addons.Any(a => a.Resources.Count == 0))
        {
            await Stremio.EnsureManifestsAsync(); // the default catalogue, the first time
        }
    }

    public void OnHidden()
    {
        _shown = false;
        _browseCts?.Cancel();
    }

    // ------------------------------------------------------------------ catalogues

    void Rebuild()
    {
        _dirty = false;
        var items = new List<StremioCatalogItem>();
        foreach (var a in Stremio.Enabled)
            foreach (var c in a.Catalogs.Where(c => !c.NeedsInput && c.Type is "movie" or "series" or "tv" or "channel"))
                items.Add(new StremioCatalogItem { Addon = a, Catalog = c, Name = CatalogName(c), From = a.Name });
        if (RealDebrid.Connected) items.Add(new StremioCatalogItem { IsRealDebrid = true, Name = "Your files", From = "Real-Debrid" });
        _suppress = true;
        CatalogList.ItemsSource = items;
        var keep = items.FirstOrDefault(i => _cat != null && i.IsRealDebrid == _cat.IsRealDebrid && i.Catalog?.Id == _cat.Catalog?.Id &&
                                             i.Catalog?.Type == _cat.Catalog?.Type && i.Addon?.TransportUrl == _cat.Addon?.TransportUrl) ?? items.FirstOrDefault();
        CatalogList.SelectedItem = keep;
        _suppress = false;
        _cat = keep;
        Browse(reset: true);
    }

    static string CatalogName(StremioCatalog c)
    {
        var kind = c.Type switch { "movie" => "films", "series" => "shows", "tv" or "channel" => "channels", _ => c.Type };
        if (string.IsNullOrWhiteSpace(c.Name)) return char.ToUpper(kind[0]) + kind[1..];
        // "Popular" → "Popular films"; names that already say what they are stay as they are.
        return c.Name.Contains("movie", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("film", StringComparison.OrdinalIgnoreCase) ||
               c.Name.Contains("series", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("show", StringComparison.OrdinalIgnoreCase)
            ? c.Name : $"{c.Name} {kind}";
    }

    void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CatalogList.SelectedItem is not StremioCatalogItem c) return;
        _cat = c;
        if (QueryBox.Text.Length > 0) { _queryTimer.Stop(); QueryBox.Text = ""; }
        CloseDetail();
        Browse(reset: true);
    }

    string Query => (QueryBox.Text ?? "").Trim();

    /// <summary>Shows a catalogue (or search results, or your Real-Debrid files) – a page at a time.</summary>
    async void Browse(bool reset)
    {
        _browseCts?.Cancel();
        var cts = _browseCts = new CancellationTokenSource();
        if (reset) { _skip = 0; _items.Clear(); MetaGrid.ItemsSource = null; RdList.ItemsSource = null; BrowseScroll.ScrollToTop(); }
        MoreButton.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        var q = Query;
        try
        {
            if (q.Length >= 2)
            {
                HeaderText.Text = $"Results for “{q}”";
                CountText.Text = "searching…";
                var found = await Stremio.SearchAsync(q, cts.Token);
                if (cts.IsCancellationRequested) return;
                var seen = new HashSet<string>();
                foreach (var (a, c, list) in found.OrderBy(r => r.Catalog.Type == "movie" ? 0 : 1))
                    foreach (var m in list)
                        if (seen.Add(m.Type + ":" + m.Id)) _items.Add(Poster(m));
                MetaGrid.ItemsSource = _items.ToList();
                CountText.Text = Count(_items.Count);
                if (_items.Count == 0) Empty($"Nothing found for “{q}”", Stremio.Enabled.Any(a => a.Catalogs.Any(c => c.CanSearch))
                    ? "Try a different spelling, or add an add-on that has it." : "None of your add-ons can search. Add one in Settings.", manage: true);
                return;
            }
            if (_cat == null)
            {
                HeaderText.Text = "Stremio";
                CountText.Text = "";
                Empty(Stremio.Addons.Count == 0 ? "No add-ons yet" : "Getting your add-ons…",
                    "Sign in with your Stremio account to bring in your add-ons, or add one by its address.", manage: true);
                return;
            }
            HeaderText.Text = _cat.IsRealDebrid ? "Your Real-Debrid files" : $"{_cat.Name}";
            if (_cat.IsRealDebrid)
            {
                CountText.Text = "loading…";
                var files = await RealDebrid.FilesAsync(cts.Token);
                if (cts.IsCancellationRequested) return;
                RdList.ItemsSource = files.Select(f => new RdFileItem
                {
                    File = f,
                    Detail = string.Join("  ·  ", new[] { Stremio.Size(f.Bytes), f.Added?.ToLocalTime().ToString("d MMM yyyy"), f.Source == "torrent" ? "Torrent" : "Download" }
                        .Where(x => !string.IsNullOrEmpty(x))),
                }).ToList();
                CountText.Text = files.Count == 1 ? "1 file" : $"{files.Count} files";
                if (files.Count == 0) Empty("No video files in your Real-Debrid cloud", "Files you add on real-debrid.com, and torrents played from add-ons, show up here.");
                return;
            }
            CountText.Text = _skip == 0 ? "loading…" : CountText.Text;
            var page = await Stremio.CatalogAsync(_cat.Addon, _cat.Catalog, null, _skip, cts.Token);
            if (cts.IsCancellationRequested) return;
            var have = _items.Select(i => i.Id).ToHashSet();
            foreach (var m in page) if (have.Add(m.Id)) _items.Add(Poster(m));
            _skip += page.Count;
            MetaGrid.ItemsSource = _items.ToList();
            CountText.Text = Count(_items.Count);
            MoreButton.Visibility = _cat.Catalog.CanPage && page.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_items.Count == 0) Empty("This catalogue is empty", "Try another one on the left.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex);
            CountText.Text = "";
            Empty("Couldn't load this", Http.Friendly(ex) + ".");
        }
    }

    static string Count(int n) => n == 1 ? "1 title" : $"{n:N0} titles";

    void Empty(string title, string body, bool manage = false)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyButton.Visibility = manage ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    static PosterItem Poster(StremioMeta m) => new()
    {
        Id = m.Id, Title = m.Name, Image = m.Poster, Source = m,
        Subtitle = string.Join("  ·  ", new[] { m.ReleaseInfo, m.Type == "series" ? "Show" : m.Type == "movie" ? null : m.Type }.Where(x => !string.IsNullOrEmpty(x))),
    };

    void More_Click(object sender, RoutedEventArgs e) => Browse(reset: false);

    void Query_TextChanged(object sender, TextChangedEventArgs e) { _queryTimer.Stop(); _queryTimer.Start(); }

    void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && QueryBox.Text.Length > 0) { QueryBox.Text = ""; e.Handled = true; }
        else if (e.Key == Key.Enter) { _queryTimer.Stop(); Browse(reset: true); e.Handled = true; }
    }

    void Manage_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.OpenSettingsSection("stremio");

    // ------------------------------------------------------------------ details

    void Meta_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PosterItem { Source: StremioMeta m }) OpenMeta(m);
    }

    /// <summary>Opens a title's page (from a catalogue, or "More sources" on a playlist film or show).</summary>
    public async void OpenMeta(StremioMeta m)
    {
        _detailCts?.Cancel();
        var cts = _detailCts = new CancellationTokenSource();
        _meta = m;
        _episode = null;
        DetailPane.Visibility = Visibility.Visible;
        DetailScroll.ScrollToTop();
        BackText.Text = HeaderText.Text.Length > 0 && Query.Length == 0 ? (_cat?.Name ?? "Back") : "Back";
        ShowMeta(m);
        DetailStatus.Text = "Loading details…";
        SourceList.ItemsSource = null;
        SourcesStatus.Text = "";
        SeriesPart.Visibility = m.Type == "series" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPart.Visibility = m.Type == "series" ? Visibility.Collapsed : Visibility.Visible;
        SeasonList.ItemsSource = null;
        EpisodeList.ItemsSource = null;
        if (m.Type != "series") FindSources(m.Type, m.Id, cts.Token);
        try
        {
            var full = await Stremio.MetaAsync(m.Type, m.Id, m.From, cts.Token);
            if (cts.IsCancellationRequested) return;
            DetailStatus.Text = "";
            if (full != null) { full.From ??= m.From; _meta = full; ShowMeta(full); }
            if (_meta.Type == "series") ShowSeasons();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { DetailStatus.Text = "Couldn't load the details: " + Http.Friendly(ex); }
    }

    void ShowMeta(StremioMeta m)
    {
        DetailTitle.Text = m.Name;
        DetailMeta.Text = string.Join("  ·  ", new[] { m.ReleaseInfo, m.Runtime, string.IsNullOrEmpty(m.ImdbRating) ? null : "★ " + m.ImdbRating,
            m.Genres.Count > 0 ? string.Join(", ", m.Genres.Take(3)) : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        DetailPlot.Text = m.Description ?? "";
        var credits = new List<string>();
        if (m.Cast.Count > 0) credits.Add("Starring  " + string.Join(", ", m.Cast.Take(5)));
        if (m.Director.Count > 0) credits.Add("Director  " + string.Join(", ", m.Director.Take(2)));
        DetailCredits.Text = string.Join("\n", credits);
        ImageLoader.SetUrl(DetailPoster, m.Poster);
        ImageLoader.SetUrl(DetailBackdrop, m.Background);
    }

    List<int> _seasons = new();

    void ShowSeasons()
    {
        _seasons = _meta.Videos.Select(v => v.Season).Distinct().OrderBy(s => s == 0 ? int.MaxValue : s).ToList();
        if (_seasons.Count == 0) { DetailStatus.Text = "No episodes listed."; return; }
        _suppress = true;
        SeasonList.ItemsSource = _seasons.Select(s => s == 0 ? "Specials" : $"Season {s}").ToList();
        SeasonList.SelectedIndex = 0;
        _suppress = false;
        ShowEpisodes(_seasons[0]);
    }

    void SeasonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || SeasonList.SelectedIndex < 0 || SeasonList.SelectedIndex >= _seasons.Count) return;
        ShowEpisodes(_seasons[SeasonList.SelectedIndex]);
    }

    void ShowEpisodes(int season)
    {
        EpisodeList.ItemsSource = _meta.Videos.Where(v => v.Season == season).OrderBy(v => v.Episode).Select(v => new StremioEpisodeItem
        {
            Video = v,
            Code = season == 0 ? $"Special {v.Episode}" : $"S{v.Season:00} E{v.Episode:00}",
            Title = string.IsNullOrWhiteSpace(v.Title) ? $"Episode {v.Episode}" : v.Title,
            Aired = v.Released?.ToLocalTime().ToString("d MMM yyyy") ?? "",
            Selected = _episode?.Id == v.Id,
        }).ToList();
    }

    void Episode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StremioEpisodeItem ep) return;
        _episode = ep.Video;
        ShowEpisodes(ep.Video.Season);
        SourcesPart.Visibility = Visibility.Visible;
        FindSources("series", ep.Video.Id, (_detailCts ??= new()).Token, ep.Code + "  ·  " + ep.Title);
        Dispatcher.InvokeAsync(() => SourcesPart.BringIntoView(), DispatcherPriority.Loaded);
    }

    void Back_Click(object sender, RoutedEventArgs e) => CloseDetail();

    void CloseDetail()
    {
        _detailCts?.Cancel();
        _meta = null;
        DetailPane.Visibility = Visibility.Collapsed;
        ImageLoader.SetUrl(DetailBackdrop, null);
        SourceList.ItemsSource = null;
    }

    // ------------------------------------------------------------------ sources

    readonly List<StremioStream> _streams = new();
    int _asked, _answered;

    /// <summary>Asks every add-on for sources; they appear as each one answers.</summary>
    async void FindSources(string type, string id, CancellationToken ct, string what = null)
    {
        _streams.Clear();
        SourceList.ItemsSource = null;
        SourcesHeader.Text = what == null ? "SOURCES" : "SOURCES  ·  " + what.ToUpperInvariant();
        _asked = Stremio.StreamAddonCount(type, id);
        _answered = 0;
        if (_asked == 0)
        {
            SourcesStatus.Text = "None of your add-ons has sources for this. Sign in to Stremio or add a source add-on in Settings.";
            return;
        }
        SourcesStatus.Text = _asked == 1 ? "Asking 1 add-on…" : $"Asking {_asked} add-ons…";
        var errors = new List<string>();
        await Stremio.StreamsAsync(type, id, (a, list, err) => Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            _answered++;
            if (err != null) errors.Add($"{a.Name}: {err}");
            _streams.AddRange(list);
            SourceList.ItemsSource = Stremio.Items(_streams, RealDebrid.Connected);
            UpdateSourcesStatus(errors);
        }), ct);
    }

    void UpdateSourcesStatus(List<string> errors)
    {
        int n = _streams.Count;
        var parts = new List<string>();
        parts.Add(n == 0 ? (_answered < _asked ? "Nothing yet" : "No sources found") : n == 1 ? "1 source" : $"{n} sources");
        if (_answered < _asked) parts.Add($"still asking {_asked - _answered} add-on{(_asked - _answered == 1 ? "" : "s")}…");
        if (errors.Count > 0 && _answered >= _asked) parts.Add("didn't answer: " + string.Join(", ", errors.Select(e => e.Split(':')[0])));
        if (n > 0 && _streams.Any(s => s.Kind == StreamKind.Torrent) && !RealDebrid.Connected) parts.Add("torrent sources need Real-Debrid (Settings)");
        SourcesStatus.Text = string.Join("  ·  ", parts);
    }

    async void Source_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StremioSourceItem it || it.Busy || _meta == null) return;
        var s = it.Stream;
        if (s.Kind == StreamKind.External)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s.ExternalUrl) { UseShellExecute = true }); } catch { }
            return;
        }
        it.Busy = true;
        try
        {
            string url = s.Url;
            if (s.Kind == StreamKind.Torrent)
            {
                var progress = new Progress<string>(msg => SourcesStatus.Text = msg);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                url = await RealDebrid.ResolveTorrentAsync(s.InfoHash, s.FileIdx, s.Filename, progress, cts.Token);
            }
            Play(url);
        }
        catch (RdNotReadyException ex) { SourcesStatus.Text = ex.Message; MainWindow.Instance?.ShowToast(ex.Message); }
        catch (Exception ex)
        {
            Log.Error(ex);
            var msg = ex is InvalidOperationException ? ex.Message : "Couldn't play that: " + Http.Friendly(ex);
            SourcesStatus.Text = msg;
            MainWindow.Instance?.ShowToast(msg);
        }
        finally { it.Busy = false; }
    }

    void Play(string url)
    {
        if (_meta == null || url == null) return;
        MainWindow.Instance?.PlayVod(StremioItem(url), resume: true);
    }

    VodItem StremioItem(string url, string fileName = null)
    {
        var ep = _episode;
        return new VodItem
        {
            Kind = "stremio",
            Title = _meta.Name,
            Subtitle = ep == null ? _meta.ReleaseInfo : $"S{ep.Season:00}E{ep.Episode:00}  ·  {ep.Title}",
            Url = url,
            Image = _meta.Poster,
            ResumeKey = "st:" + (ep?.Id ?? _meta.Id),
            ItemId = ep?.Id ?? _meta.Id,
            Ext = string.IsNullOrEmpty(fileName) ? null : Path.GetExtension(fileName),
        };
    }

    /// <summary>Downloads a source (a torrent goes through Real-Debrid first).</summary>
    async void SourceDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StremioSourceItem it || it.Saving || _meta == null) return;
        var key = "st:" + (_episode?.Id ?? _meta.Id);
        if (DownloadService.Find(key) is { Status: not DownloadStatus.Failed }) { MainWindow.Instance?.OpenDownloads(); return; }
        var s = it.Stream;
        var ep = _episode;
        it.Saving = true;
        try
        {
            string url = s.Url;
            if (s.Kind == StreamKind.Torrent)
            {
                var progress = new Progress<string>(msg => SourcesStatus.Text = msg);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                url = await RealDebrid.ResolveTorrentAsync(s.InfoHash, s.FileIdx, s.Filename, progress, cts.Token);
            }
            var (_, msg) = DownloadService.Start(StremioItem(url, s.Filename ?? ExtOf(url)), ep?.Season ?? 0, ep?.Episode ?? 0);
            SourcesStatus.Text = msg;
            MainWindow.Instance?.ShowToast(msg);
        }
        catch (RdNotReadyException ex) { SourcesStatus.Text = ex.Message; MainWindow.Instance?.ShowToast(ex.Message); }
        catch (Exception ex)
        {
            Log.Error(ex);
            var msg = ex is InvalidOperationException ? ex.Message : "Couldn't download that: " + Http.Friendly(ex);
            SourcesStatus.Text = msg;
            MainWindow.Instance?.ShowToast(msg);
        }
        finally { it.Saving = false; }
    }

    static string ExtOf(string url) { try { return Path.GetFileName(new Uri(url).AbsolutePath); } catch { return null; } }

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

    // ------------------------------------------------------------------ self-test

    internal int SelfTestTitles => _items.Count;
    internal int SelfTestSources => (SourceList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal int SelfTestEpisodes => (EpisodeList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal void SelfTestSearch(string q) { QueryBox.Text = q; _queryTimer.Stop(); Browse(reset: true); }
    internal void SelfTestOpenFirst() { if (_items.FirstOrDefault()?.Source is StremioMeta m) OpenMeta(m); }
    internal void SelfTestOpenEpisode()
    {
        if ((EpisodeList.ItemsSource as List<StremioEpisodeItem>)?.FirstOrDefault() is { } ep)
            Episode_Click(new Button { DataContext = ep }, null);
    }
    internal void SelfTestPlayFirst()
    {
        if ((SourceList.ItemsSource as List<StremioSourceItem>)?.FirstOrDefault(s => s.Stream.Kind == StreamKind.Direct) is { } it)
            Source_Click(new Button { DataContext = it }, null);
    }
    internal void SelfTestClose() { CloseDetail(); QueryBox.Text = ""; }
}
