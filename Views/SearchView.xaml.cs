using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public void OnShown() { if (_query.Length > 0) Run(); }

    /// <summary>Results can hold lots of posters – let them go when you leave.</summary>
    public void OnHidden()
    {
        ChannelResults.ItemsSource = ProgResults.ItemsSource = FilmResults.ItemsSource = ShowResults.ItemsSource = null;
    }

    void Run()
    {
        var q = _query;
        bool any = false;
        if (q.Length < 2 || Guide == null)
        {
            foreach (var s in new[] { ChannelSection, ProgSection, FilmSection, ShowSection }) s.Visibility = Visibility.Collapsed;
            EmptyTitle.Text = "Search everything";
            EmptyBody.Text = "Channels, programmes in the TV guide, films and TV shows – all at once. Type at least two letters in the search box above (Ctrl+F from anywhere).";
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        var ds = DataService.Instance;
        var epg = ds.Epg;
        var now = DateTime.Now;
        var rows = Guide.RowsIn(GuideView.AllId);

        // Channels whose name matches (best matches first).
        var channels = rows
            .Where(r => r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.DisplayName.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(24)
            .Select(r => new HomeChannelCard
            {
                Row = r,
                ListId = GuideView.AllId,
                Title = epg.At(Guide.EpgIdOf(r), now) is { } p ? "Now: " + p.Title : "",
            }).ToList();
        any |= Show(ChannelSection, ChannelHeader, ChannelResults, channels, "CHANNELS");

        // Programmes from now on, in every channel's guide.
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
            if (progs.Count > 600) break;
        }
        int totalProgs = progs.Count;
        var shownProgs = progs.OrderBy(x => x.IsLive ? DateTime.MinValue : x.Programme.Start).Take(40).ToList();
        any |= Show(ProgSection, ProgHeader, ProgResults, shownProgs, "IN THE TV GUIDE", totalProgs);
        MoreProgs.Visibility = totalProgs > shownProgs.Count ? Visibility.Visible : Visibility.Collapsed;

        // Films and shows (not in hidden or locked categories).
        var hidV = ds.HiddenSet("vod");
        var films = ds.Catalog.Movies.Where(m => !hidV.Contains(m.CategoryId) && m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenByDescending(m => m.Added)
            .Take(30).Select(m => new HomePosterCard { Id = m.Id, IsMovie = true, Title = m.Name, Image = m.Poster, Subtitle = m.Year }).ToList();
        any |= Show(FilmSection, FilmHeader, FilmResults, films, "FILMS");

        var hidS = ds.HiddenSet("series");
        var shows = ds.Catalog.Series.Where(s => !hidS.Contains(s.CategoryId) && s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenByDescending(s => s.Added)
            .Take(30).Select(s => new HomePosterCard { Id = s.Id, IsMovie = false, Title = s.Name, Image = s.Cover, Subtitle = s.Year ?? "TV show" }).ToList();
        any |= Show(ShowSection, ShowHeader, ShowResults, shows, "TV SHOWS");

        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
        {
            EmptyTitle.Text = "Nothing found";
            EmptyBody.Text = $"No channels, programmes, films or shows called “{q}”.";
        }
        Scroller.ScrollToTop();
    }

    static bool Show<T>(FrameworkElement section, TextBlock header, ItemsControl list, List<T> items, string title, int total = -1)
    {
        list.ItemsSource = items;
        section.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        int n = total >= 0 ? total : items.Count;
        header.Text = $"{title}  ·  {(n >= 600 ? "600+" : n.ToString("N0"))}";
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

    void Channel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeChannelCard c) return;
        Guide.ShowAndSelect(GuideView.AllId, c.Row);
        MainWindow.Instance?.PlayLive(c.Row.Channel, true);
    }

    void Prog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NowItem it) return;
        if (it.IsLive)
        {
            Guide.ShowAndSelect(GuideView.AllId, it.Row);
            MainWindow.Instance?.PlayLive(it.Row.Channel, true);
        }
        else MainWindow.Instance?.ShowInGuide(it.Row, it.Programme);
    }

    void Prog_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NowItem it || it.IsLive) return;
        e.Handled = true;
        var p = it.Programme;
        bool set = ReminderService.Has(it.Row.Channel.Id, p.Start);
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        var remind = new MenuItem { Header = set ? "Cancel reminder" : $"Remind me ({p.Start:ddd HH:mm})" };
        remind.Click += (_, _) =>
        {
            bool on = ReminderService.Toggle(it.Row.Channel.Id, it.Row.DisplayName, p);
            MainWindow.Instance?.ShowToast(on ? $"Reminder set for {p.Title}" : "Reminder removed");
        };
        var guide = new MenuItem { Header = "Show in the TV Guide" };
        guide.Click += (_, _) => MainWindow.Instance?.ShowInGuide(it.Row, p);
        menu.Items.Add(remind);
        menu.Items.Add(guide);
        menu.IsOpen = true;
    }

    void MoreProgs_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.FindProgrammeInGuide(_query);

    void Poster_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HomePosterCard c) MainWindow.Instance?.OpenLibraryItem(c.IsMovie, c.Id);
    }
}
