using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>One search box for everything: channels, programmes in the guide, films and TV shows.</summary>
public partial class SearchView : UserControl, IPage
{
    public string Title => "Search";
    public bool SupportsSearch => true;
    public string SearchHint => "Search channels, programmes, films and shows";
    public GuideView Guide { get; set; }

    string _query = "";

    public SearchView()
    {
        InitializeComponent();
        ReminderService.Changed += () => { if (IsVisible && _query.Length > 0) Run(); };
    }

    public void ApplySearch(string query)
    {
        _query = (query ?? "").Trim();
        Run();
    }

    public void OnShown() => Run();

    /// <summary>Results can hold lots of posters – let them go when you leave.</summary>
    public void OnHidden()
    {
        ChannelResults.ItemsSource = ProgResults.ItemsSource = FilmResults.ItemsSource = ShowResults.ItemsSource = null;
        NowResults.ItemsSource = PopularResults.ItemsSource = null;
    }

    string _filter = "all";

    void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true, Tag: string f } || this.GetVisualRoot() == null) return;
        _filter = f;
        Run();
    }

    internal void SelfTestFilter(string f) => SeeAll_Click(new Button { Tag = f }, null);

    void SeeAll_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string f) return;
        foreach (var rb in Filters.Children.OfType<RadioButton>()) if ((rb.Tag as string) == f) rb.IsChecked = true;
    }

    void Run()
    {
        var q = _query;
        Discover.IsVisible = false;
        if (q.Length < 2 || Guide == null)
        {
            foreach (var s in new[] { ChannelSection, ProgSection, FilmSection, ShowSection }) s.IsVisible = false;
            Filters.IsVisible = false;
            if (Guide != null && ShowDiscover()) { EmptyState.IsVisible = false; return; }
            EmptyTitle.Text = "Search everything";
            EmptyBody.Text = "Channels, programmes in the TV guide, films and TV shows – all at once. Type at least two letters in the search bar at the top (" + (Os.Mac ? "⌘F" : "Ctrl+F") + " from anywhere).";
            EmptyState.IsVisible = true;
            return;
        }

        var ds = DataService.Instance;
        var epg = ds.Epg;
        var now = DateTime.Now;
        var rows = Guide.RowsIn(GuideView.AllId);
        bool all = _filter == "all";

        // Everything that matches (counted for the filter chips), best matches first.
        var channelRows = rows
            .Where(r => r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.DisplayName.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        const int ProgCap = 1000;
        var progs = new List<NowItem>();
        foreach (var r in rows)
        {
            var l = epg.Get(Guide.EpgIdOf(r));
            if (l == null) continue;
            for (int i = EpgData.FirstEndingAfter(l, now); i < l.Count; i++)
            {
                var p = l[i];
                if (!p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                bool live = p.Start <= now;
                progs.Add(new NowItem
                {
                    Row = r, Programme = p, IsLive = live,
                    TimeText = live ? "On now" : p.Start.ToString("HH:mm"),
                    SubTime = live ? $"until {p.End:HH:mm}" : Day(p.Start),
                    HasReminder = !live && ReminderService.Has(r.Channel.Id, p.Start),
                });
            }
            if (progs.Count >= ProgCap) break;
        }
        progs = progs.OrderBy(x => x.IsLive ? DateTime.MinValue : x.Programme.Start).ToList();

        var hidV = ds.HiddenSet("vod");
        var films = ds.Catalog.Movies.Where(m => !hidV.Contains(m.CategoryId) && m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenByDescending(m => m.Added).ToList();
        var hidS = ds.HiddenSet("series");
        var shows = ds.Catalog.Series.Where(s => !hidS.Contains(s.CategoryId) && s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenByDescending(s => s.Added).ToList();

        // Filter chips with how many of each.
        Filters.IsVisible = true;
        int total = channelRows.Count + progs.Count + films.Count + shows.Count;
        FAll.Content = $"All  {Count(total, total >= ProgCap)}";
        FChannels.Content = $"Channels  {Count(channelRows.Count)}";
        FGuide.Content = $"TV guide  {Count(progs.Count, progs.Count >= ProgCap)}";
        FFilms.Content = $"Films  {Count(films.Count)}";
        FShows.Content = $"TV shows  {Count(shows.Count)}";

        // "All" shows the top few of each; a filter shows (lots) more of one kind.
        bool any = false;
        var channels = channelRows.Take(all ? 8 : _filter == "channels" ? 200 : 0).Select(r => new HomeChannelCard
        {
            Row = r,
            ListId = GuideView.AllId,
            Title = epg.At(Guide.EpgIdOf(r), now) is { } p ? "Now: " + p.Title : "",
        }).ToList();
        any |= Show(ChannelSection, ChannelHeader, ChannelMore, ChannelResults, channels, "CHANNELS", channelRows.Count, all);

        var shownProgs = progs.Take(all ? 12 : _filter == "guide" ? 300 : 0).ToList();
        any |= Show(ProgSection, ProgHeader, ProgMore, ProgResults, shownProgs, "IN THE TV GUIDE", progs.Count, all, progs.Count >= ProgCap);

        var filmCards = films.Take(all ? 14 : _filter == "films" ? 150 : 0)
            .Select(m => new HomePosterCard { Id = m.Id, IsMovie = true, Title = m.Name, Image = m.Poster, Subtitle = m.Year }).ToList();
        any |= Show(FilmSection, FilmHeader, FilmMore, FilmResults, filmCards, "FILMS", films.Count, all);

        var showCards = shows.Take(all ? 14 : _filter == "shows" ? 150 : 0)
            .Select(s => new HomePosterCard { Id = s.Id, IsMovie = false, Title = s.Name, Image = s.Cover, Subtitle = s.Year ?? "TV show" }).ToList();
        any |= Show(ShowSection, ShowHeader, ShowMore, ShowResults, showCards, "TV SHOWS", shows.Count, all);

        EmptyState.IsVisible = !(any);
        if (!any)
        {
            EmptyTitle.Text = "Nothing found";
            EmptyBody.Text = all ? $"No channels, programmes, films or shows called “{q}”." : $"Nothing called “{q}” here – try another filter.";
        }
        Scroller.Offset = default;
    }

    static string Count(int n, bool capped = false) => capped ? $"{n:N0}+" : n.ToString("N0");

    static bool Show<T>(Control section, TextBlock header, Button more, ItemsControl list, List<T> items, string title,
                        int total, bool allMode, bool capped = false)
    {
        list.ItemsSource = items;
        section.IsVisible = items.Count > 0;
        header.Text = $"{title}  ·  {Count(total, capped)}";
        more.IsVisible = allMode && total > items.Count;
        more.Content = $"See all {Count(total, capped)}";
        return items.Count > 0;
    }

    static string Day(DateTime t)
    {
        var d = t.Date;
        if (d == DateTime.Today) return "Today";
        if (d == DateTime.Today.AddDays(1)) return "Tomorrow";
        return t.ToString("ddd d MMM");
    }

    // ------------------------------------------------------------------ opening results

    int _discoverToken;

    /// <summary>Before you type: your recent searches, what's on now on your channels, and popular films and shows. False if there's nothing to show.</summary>
    bool ShowDiscover()
    {
        NowResults.ItemTemplate ??= ChannelResults.ItemTemplate;
        var recent = RecentSearches.All.ToList();
        RecentList.ItemsSource = recent;
        RecentSection.IsVisible = recent.Count > 0;

        var epg = DataService.Instance.Epg;
        var now = DateTime.Now;
        var rows = Guide.FavouriteRows().Concat(Guide.RecentlyWatchedRows()).Distinct().ToList();
        var onNow = rows.Select(r => (r, p: epg.At(Guide.EpgIdOf(r), now))).Where(x => x.p != null).Take(6)
            .Select(x => new HomeChannelCard { Row = x.r, ListId = GuideView.AllId, Title = $"{x.p.Title}  ·  until {x.p.End:HH:mm}" }).ToList();
        NowResults.ItemsSource = onNow;
        NowSection.IsVisible = onNow.Count > 0;

        if (PopularResults.ItemsSource is not List<WideCard> { Count: > 0 }) PopularSection.IsVisible = false;
        LoadPopular();
        bool any = recent.Count > 0 || onNow.Count > 0 || PopularSection.IsVisible;
        Discover.IsVisible = true;
        return any || Tmdb.Enabled;
    }

    async void LoadPopular()
    {
        int token = ++_discoverToken;
        try
        {
            var films = await TitleRows.ChartAsync(Tmdb.Trending, false, 6);
            var shows = await TitleRows.ChartAsync(Tmdb.Trending, true, 6);
            if (token != _discoverToken || _query.Length >= 2) return;
            var mixed = new List<WideCard>();
            for (int i = 0; i < Math.Max(films.Count, shows.Count); i++)
            {
                if (i < films.Count) mixed.Add(films[i]);
                if (i < shows.Count) mixed.Add(shows[i]);
            }
            PopularResults.ItemsSource = mixed;
            PopularSection.IsVisible = mixed.Count > 0;
            TitleRows.FillImages(mixed, a => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Background));
            if (mixed.Count == 0 && !RecentSection.IsVisible && !NowSection.IsVisible)
            {
                Discover.IsVisible = false;
                EmptyState.IsVisible = true;
            }
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string q) MainWindow.Instance?.SearchFor(q);
    }

    void RecentRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string q) return;
        RecentSearches.Remove(q);
        var recent = RecentSearches.All.ToList();
        RecentList.ItemsSource = recent;
        RecentSection.IsVisible = recent.Count > 0;
    }

    void Wide_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WideCard c) MainWindow.Instance?.OpenLibraryItem(c.IsMovie, c.Id);
    }

    void Channel_Click(object sender, RoutedEventArgs e)
    {
        RecentSearches.Add(_query);
        if ((sender as Control)?.DataContext is not HomeChannelCard c) return;
        Guide.ShowAndSelect(GuideView.AllId, c.Row);
        MainWindow.Instance?.PlayLive(c.Row.Channel, true);
    }

    void Prog_Click(object sender, RoutedEventArgs e)
    {
        RecentSearches.Add(_query);
        if ((sender as Control)?.DataContext is not NowItem it) return;
        if (it.IsLive)
        {
            Guide.ShowAndSelect(GuideView.AllId, it.Row);
            MainWindow.Instance?.PlayLive(it.Row.Channel, true);
        }
        else MainWindow.Instance?.ShowInGuide(it.Row, it.Programme);
    }

    void Prog_RightClick(object sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if ((sender as Control)?.DataContext is not NowItem it) return;
        e.Handled = true;
        var p = it.Programme;
        bool set = ReminderService.Has(it.Row.Channel.Id, p.Start);
        var menu = new ContextMenu();
        var remind = new MenuItem { Header = set ? "Cancel reminder" : $"Remind me ({p.Start:ddd HH:mm})" };
        remind.Click += (_, _) =>
        {
            bool on = ReminderService.Toggle(it.Row.Channel.Id, it.Row.DisplayName, p);
            MainWindow.Instance?.ShowToast(on ? $"Reminder set for {p.Title}" : "Reminder removed");
        };
        var guide = new MenuItem { Header = "Show in Live TV" };
        guide.Click += (_, _) => MainWindow.Instance?.ShowInGuide(it.Row, p);
        var recItem = new MenuItem { Header = RecordUi.IsRecording(it.Row, p) ? "Cancel recording" : "Record" };
        recItem.Click += (_, _) => RecordUi.ToggleProgramme(it.Row, p);
        menu.Items.Add(recItem);
        if (!it.IsLive) menu.Items.Add(remind);
        menu.Items.Add(guide);
        menu.Open(sender as Control);
    }

    void Poster_Click(object sender, RoutedEventArgs e)
    {
        RecentSearches.Add(_query);
        if ((sender as Control)?.DataContext is not HomePosterCard c) return;
        MainWindow.Instance?.OpenLibraryItem(c.IsMovie, c.Id);
    }
}
