using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Home: carry on watching (live preview of your last channel), account status, coming up next,
/// live sport, tonight's picks, continue watching, recently watched, favourites and new titles.
/// Built only while it's on screen, and refreshed every minute so times stay current.
/// </summary>
public partial class HomeView : UserControl, IPage
{
    public string Title => "Home";
    public bool SupportsSearch => false;
    public string SearchHint => "";

    /// <summary>The guide's rows (merged channels, favourites, guide data) are shared with Home.</summary>
    public GuideView Guide { get; set; }
    /// <summary>Set by the main window: true once the video engine is ready to preview.</summary>
    public Func<bool> CanPreview { get; set; } = () => false;
    /// <summary>Where the main window puts the live preview video.</summary>
    public FrameworkElement PreviewSlotElement => BannerSlot;

    bool _shown, _previewStarted;
    GuideRow _bannerRow;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(1) };

    public HomeView()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => Build();
        DataService.Instance.CatalogChanged += () => { if (_shown) Dispatcher.InvokeAsync(Build, DispatcherPriority.Background); };
        DataService.Instance.EpgChanged += () => { if (_shown) Build(); };
        HistoryStore.Changed += () => { if (_shown) Build(); };
        ReminderService.Changed += () => { if (_shown) Build(); };
    }

    public void ApplySearch(string query) { }

    public void OnShown()
    {
        _shown = true;
        _previewStarted = false;
        _playRequested = false; // each visit starts with the logo and a Play button
        Build();
        _refresh.Start();
        RefreshAccount();
    }

    public void OnHidden()
    {
        _shown = false;
        _refresh.Stop();
        foreach (var list in new ItemsControl[] { NextList, SportList, RemindList, PicksList, ResumeList, RecentList, FavList, NewFilmsList, NewShowsList })
            list.ItemsSource = null;
    }

    public void SetPreviewPlaying(bool playing) =>
        BannerPlaceholder.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Starts the live preview of your last channel in the banner (once per visit).</summary>
    public void TryStartPreview()
    {
        // Only when you press Play – starting a stream as Home opens made the page stutter.
        if (!_playRequested || !_shown || _previewStarted || _bannerRow == null || !CanPreview()) return;
        _previewStarted = true;
        MainWindow.Instance?.PlayLive(_bannerRow.Channel, false);
    }

    async void RefreshAccount()
    {
        if (await DataService.Instance.RefreshAccountAsync() && _shown) UpdateAccount();
    }

    // ------------------------------------------------------------------ building

    void Build()
    {
        if (!_shown || Guide == null) return;
        var ds = DataService.Instance;
        var epg = ds.Epg;
        var now = DateTime.Now;

        Greeting.Text = now.Hour < 12 ? "Good morning" : now.Hour < 18 ? "Good afternoon" : "Good evening";
        GreetingSub.Text = ds.Active == null ? "" : $"{ds.Active.Name}  ·  {now:dddd d MMMM}";
        UpdateAccount();

        var favs = Guide.FavouriteRows();
        var recent = Guide.RecentlyWatchedRows().Take(20).ToList();
        var all = Guide.RowsIn(GuideView.AllId);

        BuildBanner(epg, now);

        // Favourites and recently watched: what's on now.
        FavList.ItemsSource = favs.Select(r => NowCard(r, GuideView.FavId, epg, now)).ToList();
        FavEmpty.Visibility = favs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavScroller.Visibility = favs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentList.ItemsSource = recent.Select(r => NowCard(r, GuideView.RecentId, epg, now)).ToList();
        RecentSection.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Coming up next: the next programme on your favourites (or recent channels) in the next 3 hours.
        var source = favs.Count > 0 ? favs : recent;
        var listId = favs.Count > 0 ? GuideView.FavId : GuideView.RecentId;
        var next = new List<(DateTime Start, HomeChannelCard Card)>();
        foreach (var r in source)
        {
            var id = Guide.EpgIdOf(r);
            var cur = epg.At(id, now);
            var p = cur != null ? epg.StartingFrom(id, cur.End) : epg.StartingFrom(id, now);
            if (p == null || p.Start - now > TimeSpan.FromHours(3)) continue;
            int mins = (int)Math.Ceiling((p.Start - now).TotalMinutes);
            next.Add((p.Start, new HomeChannelCard
            {
                Row = r, ListId = listId, Title = p.Title,
                When = mins <= 0 ? "Starting now" : mins < 60 ? $"In {mins} min" : $"At {p.Start:HH:mm}",
                Soon = mins <= 15,
                GenreBrush = Genres.Brush(p.Genre),
            }));
        }
        NextList.ItemsSource = next.OrderBy(x => x.Start).Take(20).Select(x => x.Card).ToList();
        NextSection.Visibility = next.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Live sport now, across every channel (favourites first).
        var favSet = new HashSet<GuideRow>(favs);
        var sport = new List<(bool Fav, DateTime End, HomeChannelCard Card)>();
        foreach (var r in all)
        {
            var p = epg.At(Guide.EpgIdOf(r), now);
            if (!Genres.IsSport(p, r.Name)) continue;
            sport.Add((favSet.Contains(r), p.End, NowCard(r, GuideView.AllId, epg, now)));
        }
        SportList.ItemsSource = sport.OrderByDescending(x => x.Fav).ThenBy(x => x.End).Take(30).Select(x => x.Card).ToList();
        SportSection.Visibility = sport.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Your reminders.
        var reminders = new List<HomeChannelCard>();
        foreach (var rem in ReminderService.Upcoming)
        {
            var r = Guide.RowFor(rem.ChannelId);
            if (r == null) continue;
            bool on = rem.Start <= now;
            reminders.Add(new HomeChannelCard
            {
                Row = r, ListId = GuideView.AllId, Title = rem.Title,
                Upcoming = epg.At(Guide.EpgIdOf(r), rem.Start.AddSeconds(1)) ?? new Programme { Title = rem.Title, Start = rem.Start, End = rem.End },
                When = on ? "On now" : $"{(rem.Start.Date == DateTime.Today ? "Today" : rem.Start.ToString("ddd d MMM"))}  {rem.Start:HH:mm}",
                Soon = !on && rem.Start - now < TimeSpan.FromMinutes(30),
            });
        }
        RemindList.ItemsSource = reminders;
        RemindSection.Visibility = reminders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Tonight's picks: films and sport starting this evening (6pm–midnight, or from now if later).
        var start = DateTime.Today.AddHours(18);
        var end = DateTime.Today.AddDays(1);
        PicksHeader.Text = "TONIGHT'S PICKS";
        if (now >= end.AddHours(-1)) { start = DateTime.Today.AddDays(1).AddHours(18); end = start.AddHours(6); PicksHeader.Text = "TOMORROW NIGHT'S PICKS"; }
        else if (now > start) start = now;
        var picks = new List<(bool Fav, DateTime Start, HomeChannelCard Card)>();
        foreach (var r in all)
        {
            var l = epg.Get(Guide.EpgIdOf(r));
            if (l == null) continue;
            for (int i = EpgData.FirstEndingAfter(l, start); i < l.Count && l[i].Start < end; i++)
            {
                var p = l[i];
                if (p.Start < start) continue;
                if (!Genres.IsFilm(p) && !Genres.IsSport(p, r.Name)) continue;
                picks.Add((favSet.Contains(r), p.Start, new HomeChannelCard
                {
                    Row = r, ListId = GuideView.AllId, Title = p.Title, Upcoming = p,
                    When = $"{p.Start:HH:mm}  ·  {(Genres.IsFilm(p) ? "Film" : "Sport")}",
                    GenreBrush = Genres.Brush(p.Genre) ?? Genres.Brush("sport"),
                }));
            }
        }
        PicksList.ItemsSource = picks.OrderByDescending(x => x.Fav).ThenBy(x => x.Start).Take(30).Select(x => x.Card).ToList();
        PicksSection.Visibility = picks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Continue watching: films and episodes you stopped part-way through.
        var resume = HistoryStore.Recent("movie").Concat(HistoryStore.Recent("episode"))
            .Where(e => e.CanResume && !DataService.Instance.IsHiddenEntry(e))
            .OrderByDescending(e => e.UpdatedUtc)
            .Take(20)
            .Select(e => new HomeResumeCard
            {
                Entry = e,
                Subtitle = e.Kind == "episode" && !string.IsNullOrEmpty(e.Subtitle)
                    ? e.Subtitle
                    : e.DurationMs > 0 ? $"{Left(e.DurationMs - e.PositionMs)} left" : "Film",
            })
            .ToList();
        ResumeList.ItemsSource = resume;
        ResumeSection.Visibility = resume.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // New films and shows from your provider (hidden categories left out).
        var data = ds.Catalog;
        var hiddenVod = ds.HiddenSet("vod");
        var hiddenSeries = ds.HiddenSet("series");
        var newFilms = data.Movies.Where(m => !hiddenVod.Contains(m.CategoryId)).OrderByDescending(m => m.Added).Take(20)
            .Select(m => new HomePosterCard { Id = m.Id, IsMovie = true, Title = m.Name, Image = m.Poster, Subtitle = m.Year ?? "Film" }).ToList();
        var newShows = data.Series.Where(x => !hiddenSeries.Contains(x.CategoryId)).OrderByDescending(x => x.Added).Take(20)
            .Select(x => new HomePosterCard { Id = x.Id, IsMovie = false, Title = x.Name, Image = x.Cover, Subtitle = x.Year ?? "TV show" }).ToList();
        NewFilmsList.ItemsSource = newFilms;
        NewFilmsSection.Visibility = newFilms.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NewShowsList.ItemsSource = newShows;
        NewShowsSection.Visibility = newShows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        TryStartPreview();
    }

    void BuildBanner(EpgData epg, DateTime now)
    {
        var last = SettingsStore.Current.LastChannelId;
        _bannerRow = last == null ? null : Guide.RowFor(last);
        if (_bannerRow == null) { Banner.Visibility = Visibility.Collapsed; return; }

        Banner.Visibility = Visibility.Visible;
        BannerChannel.Text = _bannerRow.DisplayName;
        ImageLoader.SetUrl(BannerLogo, _bannerRow.Logo);
        ImageLoader.SetUrl(BannerBigLogo, _bannerRow.Logo);
        BannerBigInitials.Text = _bannerRow.Logo == null ? _bannerRow.Initials : "";
        var id = Guide.EpgIdOf(_bannerRow);
        var p = epg.At(id, now);
        if (p == null)
        {
            BannerTitle.Text = _bannerRow.DisplayName;
            BannerTime.Text = "No guide information";
            BannerProgress.Visibility = Visibility.Collapsed;
            BannerNext.Text = "";
            return;
        }
        BannerTitle.Text = p.Title;
        var left = Math.Max(1, (int)Math.Ceiling((p.End - now).TotalMinutes));
        BannerTime.Text = $"{p.Start:HH:mm} – {p.End:HH:mm}  ·  {left} min left";
        BannerProgress.Visibility = Visibility.Visible;
        BannerProgress.Value = (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds);
        var n = epg.StartingFrom(id, p.End);
        BannerNext.Text = n != null ? $"Next   {n.Start:HH:mm}   {n.Title}" : "";
    }

    void UpdateAccount()
    {
        var acc = DataService.Instance.Catalog.Account;
        if (acc == null) { AccountStrip.Visibility = Visibility.Collapsed; return; }
        AccountStrip.Visibility = Visibility.Visible;

        var parts = new List<string>();
        var status = string.IsNullOrEmpty(acc.Status) ? "Unknown" : char.ToUpper(acc.Status[0]) + acc.Status.Substring(1);
        parts.Add((acc.Trial ? "Trial · " : "Subscription ") + status.ToLowerInvariant());
        int? days = null;
        if (acc.ExpiresUtc is DateTime exp)
        {
            days = (int)Math.Floor((exp - DateTime.UtcNow).TotalDays);
            parts.Add(days < 0 ? $"expired {exp.ToLocalTime():d MMM yyyy}"
                    : days == 0 ? "expires today"
                    : $"expires {exp.ToLocalTime():d MMM yyyy} ({days} day{(days == 1 ? "" : "s")})");
        }
        else parts.Add("no expiry date");
        if (acc.MaxConnections > 0) parts.Add($"{acc.ActiveConnections} of {acc.MaxConnections} connection{(acc.MaxConnections == 1 ? "" : "s")} in use");
        AccountText.Text = string.Join("  ·  ", parts);

        bool bad = days < 0 || !string.Equals(acc.Status, "active", StringComparison.OrdinalIgnoreCase);
        bool warn = days <= 14 || (acc.MaxConnections > 0 && acc.ActiveConnections >= acc.MaxConnections);
        AccountDot.Fill = new System.Windows.Media.SolidColorBrush(bad ? System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71)
                                                                : warn ? System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24)
                                                                : System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80));
    }

    HomeChannelCard NowCard(GuideRow r, string listId, EpgData epg, DateTime now)
    {
        var p = epg.At(Guide.EpgIdOf(r), now);
        return new HomeChannelCard
        {
            Row = r,
            ListId = listId,
            Title = p?.Title ?? "No guide information",
            When = p != null ? $"{p.Start:HH:mm} – {p.End:HH:mm}" : "",
            Progress = p == null ? 0 : (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds),
            GenreBrush = Genres.Brush(p?.Genre),
        };
    }

    static string Left(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";
    }

    // ------------------------------------------------------------------ interaction

    void Channel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeChannelCard c) return;
        if (c.Upcoming != null && c.Upcoming.Start > DateTime.Now)
        {
            // Not on yet: open the guide on that programme.
            MainWindow.Instance?.ShowInGuide(c.Row, c.Upcoming);
            return;
        }
        // Channel up/down in the player then moves through this row's list (favourites, recent, all).
        Guide.ShowAndSelect(c.ListId, c.Row);
        MainWindow.Instance?.PlayLive(c.Row.Channel, true);
    }

    /// <summary>Right-click a programme that hasn't started yet: set or cancel a reminder.</summary>
    void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeChannelCard c || c.Upcoming == null || c.Upcoming.Start <= DateTime.Now) return;
        e.Handled = true;
        bool set = ReminderService.Has(c.Row.Channel.Id, c.Upcoming.Start);
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        var item = new MenuItem { Header = set ? "Cancel reminder" : $"Remind me ({c.Upcoming.Start:ddd HH:mm})" };
        item.Click += (_, _) =>
        {
            bool on = ReminderService.Toggle(c.Row.Channel.Id, c.Row.DisplayName, c.Upcoming);
            MainWindow.Instance?.ShowToast(on ? $"Reminder set for {c.Upcoming.Title}" : "Reminder removed");
        };
        var guide = new MenuItem { Header = "Show in Live TV" };
        guide.Click += (_, _) => MainWindow.Instance?.ShowInGuide(c.Row, c.Upcoming);
        menu.Items.Add(item);
        menu.Items.Add(guide);
        menu.IsOpen = true;
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeResumeCard c) return;
        var item = DataService.Instance.ResumeItem(c.Entry);
        if (item == null) { MainWindow.Instance?.ShowToast("That title isn't in your playlist any more."); return; }
        MainWindow.Instance?.PlayVod(item, resume: true);
    }

    void Poster_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HomePosterCard c) MainWindow.Instance?.OpenLibraryItem(c.IsMovie, c.Id);
    }

    bool _playRequested;

    /// <summary>Play button on the banner: starts the channel in the banner's picture.</summary>
    void BannerPlay_Click(object sender, RoutedEventArgs e)
    {
        _playRequested = true;
        _previewStarted = false;
        TryStartPreview();
    }

    void BannerWatch_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerRow == null) return;
        MainWindow.Instance?.PlayLive(_bannerRow.Channel, true);
    }

    void BannerGuide_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerRow != null) MainWindow.Instance?.ShowInGuide(_bannerRow, null);
    }

    /// <summary>Mouse wheel scrolls the page (not the row it's over); Shift + wheel scrolls the row sideways.</summary>
    void Row_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer row) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) row.ScrollToHorizontalOffset(row.HorizontalOffset - e.Delta);
        else Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
    }
}
