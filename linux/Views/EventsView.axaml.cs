using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Events: find a game in your TV guide ("Arsenal v Chelsea"), and follow your teams to see their games and get
/// reminders – a grid of games; click one for every channel showing it. Games come from your own guide and channel
/// names (see <see cref="Events"/>); crests from ESPN's team lists (<see cref="TeamCrests"/>).
/// </summary>
public partial class EventsView : UserControl, IPage
{
    public string Title => "Events";
    public bool SupportsSearch => false;
    public string SearchHint => "";
    public GuideView Guide { get; set; }

    bool _shown, _loading, _hooked;
    int _gen;
    EventGame _open; // the game whose channels are showing
    readonly DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMinutes(1) };

    public EventsView()
    {
        InitializeComponent();
        _queryTimer.Tick += (_, _) => { _queryTimer.Stop(); Refresh(); };
        _tick.Tick += (_, _) => Refresh(); // kick-offs turn into LIVE
        DataService.Instance.EpgChanged += () => { if (_shown) Refresh(); };
        ReminderService.Changed += () => { if (_shown) { Refresh(); RefreshSources(); } };
        RecordingService.Changed += () => Dispatcher.UIThread.Post(() => { if (_shown) RefreshSources(); });
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && _open != null) { CloseSources(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            if (_hooked || Guide == null) return;
            _hooked = true;
            Guide.RowsRebuilt += () => { if (_shown) Refresh(); };
        };
    }

    // ------------------------------------------------------------------ IPage

    public void ApplySearch(string query) { }

    public async void OnShown()
    {
        _shown = true;
        _loading = true;
        RemindAllToggle.IsChecked = SettingsStore.Current.RemindTeamGames;
        _loading = false;
        _tick.Start();
        Refresh();
        // Crests arrive a moment later the first time (then they're on disk).
        bool had = TeamCrests.Count > 0;
        await TeamCrests.EnsureLoadedAsync();
        if (!had && TeamCrests.Count > 0 && _shown) Refresh();
    }

    public void OnHidden()
    {
        _shown = false;
        _tick.Stop();
        CloseSources();
        ResultList.ItemsSource = TeamGames.ItemsSource = ChannelResults.ItemsSource = null;
        Events.ReleaseMemory();
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
        TeamChips.ItemsSource = teams.Select(t => new TeamChip { Name = t, Crest = TeamCrests.Find(t)?.Logo }).ToList();

        var q = Events.Parse(Query);
        bool searching = Query.Length >= 2;
        ResultsPart.IsVisible = searching;
        TeamsPart.IsVisible = !searching;

        var rows = Guide.RowsIn(GuideView.AllId);
        var epg = DataService.Instance.Epg;
        Func<GuideRow, string> epgId = Guide.EpgIdOf;
        var now = DateTime.Now;
        List<EventItem> items;
        List<GuideRow> channels = null;
        try
        {
            items = await Task.Run(() => Events.Items(
                searching ? Events.Find(rows, epgId, epg, q, now) : Events.ForTeams(rows, epgId, epg, teams, now), now));
            if (searching) channels = Events.EventChannels(rows, q);
        }
        catch (Exception ex) { Log.Error(ex); return; }
        if (gen != _gen || !_shown) return;

        if (searching)
        {
            ResultList.ItemsSource = items.Take(150).ToList();
            ResultsHeader.Text = items.Count == 0 ? "IN YOUR TV GUIDE" : items.Count == 1 ? "1 GAME IN YOUR TV GUIDE" : $"{items.Count} GAMES IN YOUR TV GUIDE";
            ResultsEmpty.IsVisible = items.Count == 0;
            ResultsEmpty.Text = q.IsEmpty
                ? "Type a team, or two teams like “Arsenal v Chelsea”."
                : $"Nothing in your TV guide for “{Query}” in the next {Events.Days} days. Guides usually cover a few days ahead – try again nearer the time.";
            ChannelResults.ItemsSource = channels.Take(60).Select(r => new EventChannelItem
            {
                Row = r,
                Now = epg.At(epgId(r), now) is { } p ? "Now: " + p.Title : "",
            }).ToList();
            ChannelsPart.IsVisible = channels.Count > 0;
        }
        else
        {
            TeamGames.ItemsSource = items;
            TeamEmpty.IsVisible = items.Count == 0;
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
        if (e.Key == Key.Escape && (QueryBox.Text ?? "").Length > 0) { QueryBox.Text = ""; e.Handled = true; }
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
        if ((sender as Control)?.DataContext is not TeamChip t) return;
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

    // ------------------------------------------------------------------ a game: every channel showing it

    void Game_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EventItem it) OpenSources(it);
    }

    void OpenSources(EventItem it)
    {
        _open = it.Game;
        SourcesArt.Content = it;
        SourcesTitle.Text = it.Title;
        SourcesWhen.Text = string.IsNullOrWhiteSpace(it.Game.Subtitle) ? it.When : $"{it.When}  ·  {it.Game.Subtitle}";
        RefreshSources();
        SourcesPanel.Opacity = 0;
        SourcesPanel.IsVisible = true;
        Dispatcher.UIThread.Post(() => SourcesPanel.Opacity = 1, DispatcherPriority.Background);
    }

    void RefreshSources()
    {
        if (_open == null) return;
        var list = Events.Sources(_open, DateTime.Now);
        SourceList.ItemsSource = list;
        SourcesCount.Text = list.Count == 1 ? "ON 1 CHANNEL" : $"ON {list.Count} CHANNELS";
    }

    void CloseSources()
    {
        _open = null;
        SourcesPanel.IsVisible = false;
        SourceList.ItemsSource = null;
        SourcesArt.Content = null;
    }

    void SourcesClose_Click(object sender, RoutedEventArgs e) => CloseSources();
    void SourcesDim_PointerPressed(object sender, PointerPressedEventArgs e) => CloseSources();
    void SourcesCard_PointerPressed(object sender, PointerPressedEventArgs e) => e.Handled = true; // clicks on the card don't close it

    static EventSourceItem SourceOf(object sender) => (sender as Control)?.DataContext as EventSourceItem;

    void SourceWatch_Click(object sender, RoutedEventArgs e)
    {
        if (SourceOf(sender) is not { } s) return;
        CloseSources();
        MainWindow.Instance?.PlayLive(s.Row.Channel, true);
    }

    void SourceShow_Click(object sender, RoutedEventArgs e)
    {
        if (SourceOf(sender) is not { } s) return;
        CloseSources();
        MainWindow.Instance?.ShowInGuide(s.Row, s.Programme);
    }

    void SourceRemind_Click(object sender, RoutedEventArgs e)
    {
        if (SourceOf(sender) is not { } s) return;
        bool on = ReminderService.Toggle(s.Row.Channel.Id, s.Row.DisplayName, s.Programme);
        MainWindow.Instance?.ShowToast(on
            ? $"Reminder set: {s.Programme.Title} on {s.Row.DisplayName}, {Events.Day(s.Programme.Start).ToLowerInvariant()} at {s.Programme.Start:HH:mm}"
            : $"Reminder cleared: {s.Programme.Title}");
    }

    void SourceRecord_Click(object sender, RoutedEventArgs e)
    {
        if (SourceOf(sender) is not { } s) return;
        RecordUi.ToggleProgramme(s.Row, s.Programme);
        RefreshSources();
    }

    /// <summary>Right-click a game: the quick things, without opening its channels.</summary>
    void Game_PointerReleased(object sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || (sender as Control)?.DataContext is not EventItem it) return;
        var g = it.Game;
        bool live = g.IsLiveAt(DateTime.Now);
        var menu = new ContextMenu();
        var open = new MenuItem { Header = g.Showings.Count == 1 ? "See the channel" : $"See all {g.Showings.Count} channels" };
        open.Click += (_, _) => OpenSources(it);
        menu.Items.Add(open);
        if (live)
        {
            var watch = new MenuItem { Header = "Watch on " + g.Row.DisplayName };
            watch.Click += (_, _) => MainWindow.Instance?.PlayLive(g.Row.Channel, true);
            menu.Items.Add(watch);
        }
        else
        {
            var rem = new MenuItem { Header = it.HasReminder ? "Clear reminder" : "Remind me" };
            rem.Click += (_, _) =>
            {
                bool on = Events.ToggleReminder(g);
                MainWindow.Instance?.ShowToast(on
                    ? $"Reminder set: {g.Title}, {Events.Day(g.Start).ToLowerInvariant()} at {g.Start:HH:mm}"
                    : $"Reminder cleared: {g.Title}");
            };
            menu.Items.Add(rem);
        }
        var show = new MenuItem { Header = "Show in Live TV" };
        show.Click += (_, _) => MainWindow.Instance?.ShowInGuide(g.Row, g.Programme);
        menu.Items.Add(show);
        menu.Open(sender as Control);
        e.Handled = true;
    }

    void EventChannel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EventChannelItem c) MainWindow.Instance?.PlayLive(c.Row.Channel, true);
    }

    // ------------------------------------------------------------------ self-test

    internal void SelfTestQuery(string q) { QueryBox.Text = q; _queryTimer.Stop(); Refresh(); }
    internal int SelfTestResults => (ResultList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal int SelfTestTeamGames => (TeamGames.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal void SelfTestOpenFirst() { if ((ResultList.ItemsSource as List<EventItem>)?.FirstOrDefault() is { } it) OpenSources(it); }
    internal int SelfTestSources => (SourceList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal void SelfTestCloseSources() => CloseSources();
}
