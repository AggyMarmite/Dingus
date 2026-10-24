using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Events: find a game in your TV guide ("Arsenal v Chelsea"), and follow your teams to see their games and get
/// reminders. Everything comes from your own guide and channel names (see <see cref="Events"/>).
/// </summary>
public partial class EventsView : UserControl, IPage
{
    public string Title => "Events";
    public bool SupportsSearch => false;
    public string SearchHint => "";
    public GuideView Guide { get; set; }

    bool _shown, _loading, _hooked;
    int _gen;
    readonly DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMinutes(1) };

    public EventsView()
    {
        InitializeComponent();
        _queryTimer.Tick += (_, _) => { _queryTimer.Stop(); Refresh(); };
        _tick.Tick += (_, _) => Refresh(); // kick-offs turn into LIVE
        DataService.Instance.EpgChanged += () => { if (_shown) Refresh(); };
        ReminderService.Changed += () => { if (_shown) Refresh(); };
        Loaded += (_, _) =>
        {
            if (_hooked || Guide == null) return;
            _hooked = true;
            Guide.RowsRebuilt += () => { if (_shown) Refresh(); };
        };
    }

    // ------------------------------------------------------------------ IPage

    public void ApplySearch(string query) { }

    public void OnShown()
    {
        _shown = true;
        _loading = true;
        RemindAllToggle.IsChecked = SettingsStore.Current.RemindTeamGames;
        _loading = false;
        _tick.Start();
        Refresh();
    }

    public void OnHidden()
    {
        _shown = false;
        _tick.Stop();
        ResultList.ItemsSource = TeamGames.ItemsSource = ChannelResults.ItemsSource = null;
    }

    // ------------------------------------------------------------------ building

    string Query => (QueryBox.Text ?? "").Trim();

    /// <summary>Works out the games off the window's thread (a big guide takes a moment), then shows them.</summary>
    async void Refresh()
    {
        if (!_shown || Guide == null) return;
        int gen = ++_gen;
        var s = SettingsStore.Current;
        var teams = (s.FollowedTeams ?? new()).ToList();
        TeamChips.ItemsSource = teams.Select(t => new TeamChip { Name = t }).ToList();

        var q = Events.Parse(Query);
        bool searching = Query.Length >= 2;
        ResultsPart.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        TeamsPart.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;

        var rows = Guide.RowsIn(GuideView.AllId);
        var epg = DataService.Instance.Epg;
        Func<GuideRow, string> epgId = Guide.EpgIdOf;
        var now = DateTime.Now;
        List<EventGame> games;
        List<GuideRow> channels = null;
        try
        {
            if (searching)
            {
                games = await Task.Run(() => Events.Find(rows, epgId, epg, q, now));
                channels = Events.EventChannels(rows, q);
            }
            else games = await Task.Run(() => Events.ForTeams(rows, epgId, epg, teams, now));
        }
        catch (Exception ex) { Log.Error(ex); return; }
        if (gen != _gen || !_shown) return;

        var items = Events.Items(games, now);
        if (searching)
        {
            ResultList.ItemsSource = items;
            ResultsHeader.Text = items.Count == 0 ? "IN YOUR TV GUIDE" : items.Count == 1 ? "1 GAME IN YOUR TV GUIDE" : $"{items.Count} GAMES IN YOUR TV GUIDE";
            ResultsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultsEmpty.Text = q.IsEmpty
                ? "Type a team, or two teams like “Arsenal v Chelsea”."
                : $"Nothing in your TV guide for “{Query}” in the next {Events.Days} days. Guides usually cover a few days ahead – try again nearer the time.";
            ChannelResults.ItemsSource = channels.Take(60).Select(r => new EventChannelItem
            {
                Row = r,
                Now = epg.At(epgId(r), now) is { } p ? "Now: " + p.Title : "",
            }).ToList();
            ChannelsPart.Visibility = channels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            TeamGames.ItemsSource = items;
            TeamEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TeamEmpty.Text = teams.Count == 0
                ? "Follow a team above and its games in your TV guide will show here – with a reminder if you'd like one."
                : DataService.Instance.Epg.MatchedChannels == 0
                    ? "Your playlist doesn't have a TV guide yet, so there are no games to show."
                    : "No games for your teams in the TV guide yet. Guides usually cover the next few days – they'll appear here when they're listed.";
        }
    }

    // ------------------------------------------------------------------ search

    void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        _queryTimer.Stop();
        _queryTimer.Start();
    }

    void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && QueryBox.Text.Length > 0) { QueryBox.Text = ""; e.Handled = true; }
        else if (e.Key == Key.Enter) { _queryTimer.Stop(); Refresh(); e.Handled = true; }
    }

    // ------------------------------------------------------------------ teams

    void Follow_Click(object sender, RoutedEventArgs e) => FollowTyped();

    void FollowBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FollowTyped(); e.Handled = true; }
    }

    void FollowTyped()
    {
        var name = (FollowBox.Text ?? "").Trim();
        if (name.Length == 0) return;
        if (Events.Follow(name))
        {
            MainWindow.Instance?.ShowToast($"Following {name}");
            FollowBox.Text = "";
            MainWindow.Instance?.SyncTeamReminders();
        }
        else MainWindow.Instance?.ShowToast(Events.Norm(name).Trim().Length < 2 ? "Type a team's name first." : $"You already follow {name}.");
        Refresh();
    }

    void Unfollow_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not TeamChip t) return;
        Events.Unfollow(t.Name);
        MainWindow.Instance?.ShowToast($"Stopped following {t.Name}");
        Refresh();
    }

    void RemindAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Current.RemindTeamGames = RemindAllToggle.IsChecked == true;
        SettingsStore.Save();
        if (SettingsStore.Current.RemindTeamGames) MainWindow.Instance?.SyncTeamReminders(announce: true);
    }

    // ------------------------------------------------------------------ games

    static EventItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as EventItem;

    void Game_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } it) return;
        if (it.Game.IsLiveAt(DateTime.Now)) MainWindow.Instance?.PlayLive(it.Game.Row.Channel, true);
        else MainWindow.Instance?.ShowInGuide(it.Game.Row, it.Game.Programme);
    }

    void Watch_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // not the row's own click as well
        if (ItemOf(sender) is { } it) MainWindow.Instance?.PlayLive(it.Game.Row.Channel, true);
    }

    void Remind_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is not { } it) return;
        bool on = Events.ToggleReminder(it.Game);
        MainWindow.Instance?.ShowToast(on
            ? $"Reminder set: {it.Game.Title}, {Events.Day(it.Game.Start).ToLowerInvariant()} at {it.Game.Start:HH:mm}"
            : $"Reminder cleared: {it.Game.Title}");
    }

    /// <summary>Right-click a game: watch or find it on any of its channels, record it.</summary>
    void Game_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } it) return;
        var g = it.Game;
        bool live = g.IsLiveAt(DateTime.Now);
        var menu = new ContextMenu();
        foreach (var (row, p) in g.Showings.Take(12))
        {
            var mi = new MenuItem { Header = (live ? "Watch on " : "Show on ") + row.DisplayName + (live ? "" : " in Live TV") };
            mi.Click += (_, _) =>
            {
                if (live) MainWindow.Instance?.PlayLive(row.Channel, true);
                else MainWindow.Instance?.ShowInGuide(row, p);
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        if (!live)
        {
            var rem = new MenuItem { Header = it.HasReminder ? "Clear reminder" : "Remind me" };
            rem.Click += (_, _) => Remind_Click(sender, new RoutedEventArgs());
            menu.Items.Add(rem);
        }
        var rec = new MenuItem { Header = RecordUi.IsRecording(g.Row, g.Programme) ? (live ? "Stop recording" : "Cancel recording") : "Record" };
        rec.Click += (_, _) => RecordUi.ToggleProgramme(g.Row, g.Programme);
        menu.Items.Add(rec);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
        e.Handled = true;
    }

    void EventChannel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EventChannelItem c) MainWindow.Instance?.PlayLive(c.Row.Channel, true);
    }

    // ------------------------------------------------------------------ self-test

    internal void SelfTestQuery(string q) { QueryBox.Text = q; _queryTimer.Stop(); Refresh(); }
    internal int SelfTestResults => (ResultList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal int SelfTestTeamGames => (TeamGames.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
}
