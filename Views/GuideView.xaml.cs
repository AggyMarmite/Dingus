using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;
using Shapes = System.Windows.Shapes;

namespace Dingus.Views;

public partial class GuideView : UserControl, IPage
{
    const double ChannelCol = 300;
    public const string AllId = "__all", FavId = "__fav", RecentId = "__recent";

    public string Title => "TV Guide";
    public bool SupportsSearch => true;
    public string SearchHint => _mode == "find" ? "Find a programme on any channel…" : "Search all channels";

    /// <summary>Raised (debounced) when the selection settles on a channel – starts the mini player.</summary>
    public event Action<GuideRow> PreviewRequested;
    /// <summary>Set by the main window: true once the video engine is ready.</summary>
    public Func<bool> CanPreview { get; set; } = () => false;
    public FrameworkElement PreviewSlotElement => PreviewSlot;

    List<GuideRow> _all = new();
    /// <summary>Every row, including channels hidden in the channel editor.</summary>
    List<GuideRow> _everything = new();
    List<GuideRow> _shown = new();
    /// <summary>Any channel id (every merged version) → its guide row.</summary>
    Dictionary<string, GuideRow> _byId = new();
    string _mode = "grid"; // grid | now | tonight
    CategoryItem _favCat, _recentCat;
    string _cat = AllId, _search = "", _selectedId;
    bool _suppressCat, _suppressSel, _initialSelectDone, _autoPreviewDone;

    DateTime _winStart;
    bool _follow = true;
    int _winMinutes = 180;
    double _ppm = 4;
    double _lastWidth;

    GuideRow _heroRow;
    Programme _heroProg;

    readonly DispatcherTimer _clock;
    readonly DispatcherTimer _debounce;

    public GuideView()
    {
        InitializeComponent();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => OnClock();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (ChannelList.SelectedItem is GuideRow r) PreviewRequested?.Invoke(r);
        };

        DataService.Instance.CatalogChanged += Rebuild;
        DataService.Instance.EpgChanged += OnEpgChanged;
        HistoryStore.Changed += OnHistoryChanged;
        ReminderService.Changed += OnRemindersChanged;

        ChannelList.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _lastWidth) < 1) return;
            _lastWidth = e.NewSize.Width;
            RecomputeBlocks();
        };
        NowLayer.SizeChanged += (_, _) => UpdateNowLine();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _clock.Start(); OnClock(); TryAutoPreview(); }
            else _clock.Stop();
        };

        _selectedId = SettingsStore.Current.LastChannelId;
        Rebuild();
    }

    // ------------------------------------------------------------------ IPage

    public void ApplySearch(string query)
    {
        _search = query ?? "";
        if (_mode == "find") { BuildList(); return; } // programme search: the channel list stays as it is
        ApplyFilter();
    }

    public void OnShown() => RecomputeBlocks();
    public void OnHidden() { }

    public void SetPreviewPlaying(bool playing) =>
        PreviewPlaceholder.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>On launch, start the last-watched channel in the mini player once everything is ready.</summary>
    public void TryAutoPreview()
    {
        if (_autoPreviewDone || !IsVisible || !CanPreview()) return;
        if (ChannelList.SelectedItem is not GuideRow r) return;
        _autoPreviewDone = true;
        PreviewRequested?.Invoke(r);
    }

    // ------------------------------------------------------------------ data

    void Rebuild()
    {
        var ds = DataService.Instance;
        var data = ds.Catalog;
        var hidden = ds.HiddenSet("live");
        var favs = ds.FavouriteSet();
        var epg = ds.Epg;
        var hiddenCh = new HashSet<string>(ds.Active?.HiddenChannels ?? new List<string>());
        var names = ds.Active?.ChannelNames ?? new Dictionary<string, string>();

        _all = new List<GuideRow>(data.Channels.Count);
        _byId = new Dictionary<string, GuideRow>(data.Channels.Count);
        bool merge = SettingsStore.Current.MergeDuplicates;
        var groups = new Dictionary<string, GuideRow>();
        int i = 1;
        foreach (var c in data.Channels)
        {
            if (hidden.Contains(c.CategoryId)) continue;
            var key = merge ? ChannelMerger.Key(c) : "";
            if (key.Length == 0 || !groups.TryGetValue(key, out var row))
            {
                row = new GuideRow(c, i++) { BlockBuilder = BuildRowBlocks };
                _all.Add(row);
                if (key.Length > 0) groups[key] = row;
            }
            row.AddVariant(c);
            _byId.TryAdd(c.Id, row);
        }
        foreach (var r in _all)
        {
            r.IsFavourite = r.Variants.Any(v => favs.Contains(v.Id));
            r.Logo = r.Variants.Select(v => v.Logo).FirstOrDefault(l => l != null) ?? r.Variants.Select(v => epg.Icon(v.Id)).FirstOrDefault(l => l != null);
            r.EpgId = PickEpgId(r, epg);
            r.CustomName = r.Variants.Select(v => names.TryGetValue(v.Id, out var n) ? n : null).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            r.IsHidden = hiddenCh.Count > 0 && r.Variants.Any(v => hiddenCh.Contains(v.Id));
        }
        _everything = _all;
        if (hiddenCh.Count > 0) _all = _all.Where(r => !r.IsHidden).ToList();

        var counts = new Dictionary<string, int>();
        foreach (var r in _all)
            foreach (var cid in r.CategoryIds) counts[cid] = counts.TryGetValue(cid, out var n0) ? n0 + 1 : 1;
        _favCat = new CategoryItem { Id = FavId, Name = "Favourites", Count = _all.Count(r => r.IsFavourite) };
        _recentCat = new CategoryItem { Id = RecentId, Name = "Recently watched", Count = RecentRows().Count };
        var cats = new List<CategoryItem>
        {
            new() { Id = AllId, Name = "All channels", Count = _all.Count },
            _favCat,
            _recentCat,
        };
        foreach (var c in data.LiveCategories)
            if (!hidden.Contains(c.Id) && counts.TryGetValue(c.Id, out var n))
                cats.Add(new CategoryItem { Id = c.Id, Name = c.Name, Count = n });

        _suppressCat = true;
        CategoryList.ItemsSource = cats;
        var sel = cats.FirstOrDefault(c => c.Id == _cat) ?? cats[0];
        _cat = sel.Id;
        CategoryList.SelectedItem = sel;
        _suppressCat = false;

        ApplyFilter();
    }

    static string PickEpgId(GuideRow r, EpgData epg) =>
        r.Variants.Select(v => v.Id).FirstOrDefault(id => epg.Get(id) != null) ?? r.Channel.Id;

    List<GuideRow> RecentRows()
    {
        var res = new List<GuideRow>();
        var seen = new HashSet<GuideRow>();
        foreach (var id in HistoryStore.RecentChannels)
            if (_byId.TryGetValue(id, out var r) && seen.Add(r)) res.Add(r);
        return res;
    }

    void OnHistoryChanged()
    {
        // Only the count updates live; the list itself re-orders next time the category is opened,
        // so it never reshuffles under you while you're zapping through it.
        if (_recentCat != null) _recentCat.Count = RecentRows().Count;
    }

    void OnEpgChanged()
    {
        var epg = DataService.Instance.Epg;
        foreach (var r in _all)
        {
            r.EpgId = PickEpgId(r, epg);
            if (r.Logo == null) r.Logo = r.Variants.Select(v => epg.Icon(v.Id)).FirstOrDefault(l => l != null);
        }
        RecomputeBlocks();
        if (_heroRow != null) ShowHero(_heroRow, null);
        if (_mode != "grid") BuildList();
    }

    void ApplyFilter()
    {
        IEnumerable<GuideRow> q;
        if (_search.Length > 0) q = _all.Where(r => r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase));
        else if (_cat == FavId) q = _all.Where(r => r.IsFavourite);
        else q = RowsIn(_cat);
        _shown = q.ToList();

        RecomputeBlocks();

        _suppressSel = true;
        ChannelList.ItemsSource = _shown;
        var sel = _selectedId == null ? null : _shown.FirstOrDefault(r => r.Has(_selectedId));
        ChannelList.SelectedItem = sel;
        _suppressSel = false;

        if (sel != null)
        {
            ChannelList.ScrollIntoView(sel);
            if (!_initialSelectDone || _heroRow?.Channel.Id != sel.Channel.Id) ShowHero(sel, null);
        }
        else if (_shown.Count > 0) ChannelList.ScrollIntoView(_shown[0]);
        _initialSelectDone |= sel != null;

        bool empty = _shown.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            if (DataService.Instance.Catalog.Channels.Count == 0)
            { EmptyTitle.Text = "No channels yet"; EmptyBody.Text = "Add or refresh a playlist to see the guide."; }
            else if (_search.Length > 0)
            { EmptyTitle.Text = "No matches"; EmptyBody.Text = $"Nothing called “{_search}”."; }
            else if (_cat == FavId)
            { EmptyTitle.Text = "No favourites yet"; EmptyBody.Text = "Right-click a channel to add it to your favourites."; }
            else if (_cat == RecentId)
            { EmptyTitle.Text = "Nothing watched yet"; EmptyBody.Text = "Channels you watch will appear here."; }
            else
            { EmptyTitle.Text = "Nothing here"; EmptyBody.Text = "This category is empty."; }
        }

        if (_mode != "grid") BuildList();
        TryAutoPreview();
    }

    // ------------------------------------------------------------------ timeline

    double TimelineWidth() => ChannelList.ActualWidth - ChannelCol - 14;

    static DateTime Floor30(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute >= 30 ? 30 : 0, 0, t.Kind);

    void RecomputeBlocks()
    {
        var tw = TimelineWidth();
        if (tw < 60) return;

        _winMinutes = Math.Clamp((int)(tw / 5.0 / 30) * 30, 90, 240);
        if (_follow) _winStart = Floor30(DateTime.Now);
        _ppm = tw / _winMinutes;

        DrawHeader();
        _blockNow = DateTime.Now;
        _blockWidth = tw;
        GuideRow.Version++;
        foreach (var r in _shown) r.InvalidateBlocks();
        UpdateNowLine();
        DayText.Text = DayLabel(_winStart);
    }

    DateTime _blockNow = DateTime.Now;
    double _blockWidth;

    IReadOnlyList<ProgBlock> BuildRowBlocks(GuideRow r) =>
        _blockWidth < 60 ? Array.Empty<ProgBlock>() : BuildBlocks(r, DataService.Instance.Epg, _blockNow, _blockWidth);

    List<ProgBlock> BuildBlocks(GuideRow r, EpgData epg, DateTime now, double tw)
    {
        var res = new List<ProgBlock>(4);
        var list = epg.Get(r.EpgId);
        var ws = _winStart;
        var we = ws.AddMinutes(_winMinutes);
        var ds = DataService.Instance;

        if (list != null)
        {
            for (int i = EpgData.FirstEndingAfter(list, ws); i < list.Count && list[i].Start < we; i++)
            {
                var p = list[i];
                double l = Math.Max(0, (p.Start - ws).TotalMinutes * _ppm);
                double rr = Math.Min(tw, (p.End - ws).TotalMinutes * _ppm);
                double w = rr - l - 3;
                if (w < 6) continue;
                res.Add(new ProgBlock
                {
                    Title = p.Title,
                    TimeText = w > 90 ? $"{p.Start:HH:mm} – {p.End:HH:mm}" : "",
                    Left = l,
                    Width = w,
                    IsNow = p.Start <= now && p.End > now,
                    IsPast = p.End <= now,
                    CanCatchup = r.CatchupChannel != null && ds.CanCatchup(r.CatchupChannel, p),
                    HasReminder = p.Start > now && ReminderService.Has(r.Channel.Id, p.Start),
                    Programme = p,
                    Row = r,
                });
            }
        }

        if (res.Count == 0)
            res.Add(new ProgBlock
            {
                Title = list == null ? r.Name : "No information",
                TimeText = list == null ? "No guide data for this channel" : "",
                Left = 0,
                Width = Math.Max(10, tw - 3),
                Row = r,
            });
        return res;
    }

    void DrawHeader()
    {
        TimeHeader.Children.Clear();
        for (int m = 0; m < _winMinutes; m += 30)
        {
            double x = m * _ppm;
            var t = _winStart.AddMinutes(m);
            var tick = new Shapes.Rectangle { Width = 1, Height = 12, Fill = (System.Windows.Media.Brush)FindResource("LineBrush") };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 14);
            var label = new TextBlock
            {
                Text = t.ToString("HH:mm"),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            };
            Canvas.SetLeft(label, x + 7);
            Canvas.SetTop(label, 11);
            TimeHeader.Children.Add(tick);
            TimeHeader.Children.Add(label);
        }
    }

    void UpdateNowLine()
    {
        var tw = TimelineWidth();
        var x = (DateTime.Now - _winStart).TotalMinutes * _ppm;
        if (tw < 60 || x < 0 || x > tw) { NowLine.Visibility = Visibility.Collapsed; return; }
        NowLine.Visibility = Visibility.Visible;
        NowLine.Height = NowLayer.ActualHeight;
        Canvas.SetLeft(NowLine, ChannelCol + x - 1);
    }

    void OnClock()
    {
        RecomputeBlocks();
        if (_heroRow != null && (IsLiveHero || _heroProg.End <= DateTime.Now))
            ShowHero(_heroRow, null);
    }

    bool IsLiveHero => _heroProg == null || (_heroProg.Start <= DateTime.Now && _heroProg.End > DateTime.Now);

    void Earlier_Click(object sender, RoutedEventArgs e) => ShiftWindow(-60);
    void Later_Click(object sender, RoutedEventArgs e) => ShiftWindow(60);

    void Now_Click(object sender, RoutedEventArgs e)
    {
        _follow = true;
        RecomputeBlocks();
    }

    void ShiftWindow(int minutes)
    {
        var floor = Floor30(DateTime.Now);
        var next = _winStart.AddMinutes(minutes);
        // Allow scrolling back far enough to reach catch-up programmes.
        if (next < floor.AddDays(-EpgService.PastDays)) next = floor.AddDays(-EpgService.PastDays);
        if (next > floor.AddHours(48)) next = floor.AddHours(48);
        _winStart = next;
        _follow = _winStart == floor;
        RecomputeBlocks();
    }

    static string DayLabel(DateTime t)
    {
        var d = t.Date;
        var today = DateTime.Today;
        if (d == today) return "Today";
        if (d == today.AddDays(1)) return "Tomorrow";
        if (d == today.AddDays(-1)) return "Yesterday";
        return t.ToString("ddd d MMM");
    }

    // ------------------------------------------------------------------ hero

    void ShowHero(GuideRow r, Programme p)
    {
        _heroRow = r;
        var ds = DataService.Instance;
        var epg = ds.Epg;
        var now = DateTime.Now;
        var current = epg.At(r.EpgId, now);
        p ??= current;
        _heroProg = p;

        HeroPanel.Visibility = Visibility.Visible;
        HeroChannel.Text = $"{r.Number}   {r.DisplayName}";
        ImageLoader.SetUrl(HeroLogo, r.Logo);

        if (p == null)
        {
            HeroTitle.Text = r.Name;
            HeroTime.Text = epg.Get(r.EpgId) == null ? "No guide information" : "No information for this time";
            HeroDesc.Text = "";
            HeroProgress.Visibility = Visibility.Collapsed;
            HeroLive.Visibility = Visibility.Collapsed;
            HeroNext.Text = "";
        }
        else
        {
            bool live = p.Start <= now && p.End > now;
            HeroTitle.Text = p.Title;
            HeroLive.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            HeroProgress.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            if (live)
            {
                var left = Math.Max(1, (int)Math.Ceiling((p.End - now).TotalMinutes));
                HeroTime.Text = $"{p.Start:HH:mm} – {p.End:HH:mm}  ·  {left} min left";
                HeroProgress.Value = (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds);
            }
            else
            {
                HeroTime.Text = $"{DayLabel(p.Start)}  {p.Start:HH:mm} – {p.End:HH:mm}";
            }
            HeroDesc.Text = p.Desc ?? "";
            var next = epg.StartingFrom(r.EpgId, p.End);
            HeroNext.Text = next != null ? $"Next   {next.Start:HH:mm}   {next.Title}" : "";
        }

        HeroVersions.Text = r.VersionsText;
        bool cu = p != null && r.CatchupChannel is { } cch && ds.CanCatchup(cch, p);
        CatchupButton.Visibility = cu ? Visibility.Visible : Visibility.Collapsed;
        CatchupText.Text = p != null && p.End > now ? "Start over" : "Watch catch-up";
        UpdateFavButton();
        UpdateRemindButton();
    }

    void UpdateFavButton()
    {
        bool fav = _heroRow?.IsFavourite == true;
        FavGlyph.Text = fav ? "" : "";
        FavText.Text = fav ? "Favourite" : "Add to favourites";
    }

    // ------------------------------------------------------------------ interaction

    void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCat || CategoryList.SelectedItem is not CategoryItem c) return;
        _cat = c.Id;
        if (_mode == "find") { ApplyFilter(); return; } // keep the programme search, now within this category
        if (_search.Length > 0) MainWindow.Instance?.ClearSearch(); // ClearSearch re-applies the filter
        else ApplyFilter();
    }

    void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSel || ChannelList.SelectedItem is not GuideRow r) return;
        _selectedId = r.Channel.Id;
        ShowHero(r, null);
        _debounce.Stop();
        _debounce.Start();
    }

    void Block_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProgBlock b) return;
        if (!ReferenceEquals(ChannelList.SelectedItem, b.Row)) ChannelList.SelectedItem = b.Row;
        ShowHero(b.Row, b.Programme);
    }

    void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ChannelList, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
        // Double-clicking a past programme that has catch-up plays it; anything else watches live.
        var block = FindBlock(e.OriginalSource as DependencyObject);
        if (block != null && block.IsPast && block.CanCatchup)
        {
            _debounce.Stop();
            MainWindow.Instance?.PlayCatchup(block.Row.CatchupChannel ?? block.Row.Channel, block.Programme);
            return;
        }
        WatchFull();
    }

    static ProgBlock FindBlock(DependencyObject d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.DataContext is ProgBlock b) return b;
            if (d is ListBoxItem) return null;
            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    void ChannelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { WatchFull(); e.Handled = true; }
    }

    void WatchFull_Click(object sender, RoutedEventArgs e) => WatchFull();

    void WatchFull()
    {
        if (ChannelList.SelectedItem is not GuideRow r) r = _heroRow;
        if (r == null) return;
        _debounce.Stop();
        MainWindow.Instance?.PlayLive(r.Channel, true);
    }

    void Mini_Click(object sender, RoutedEventArgs e)
    {
        var r = ChannelList.SelectedItem as GuideRow ?? _heroRow;
        if (r == null) return;
        _debounce.Stop();
        MainWindow.Instance?.PlayLiveMini(r.Channel);
    }

    void MultiView_Click(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        MainWindow.Instance?.OpenMultiView(ChannelList.SelectedItem as GuideRow ?? _heroRow);
    }

    void Catchup_Click(object sender, RoutedEventArgs e)
    {
        if (_heroRow == null || _heroProg == null) return;
        _debounce.Stop();
        MainWindow.Instance?.PlayCatchup(_heroRow.CatchupChannel ?? _heroRow.Channel, _heroProg);
    }

    void Fav_Click(object sender, RoutedEventArgs e)
    {
        var r = ChannelList.SelectedItem as GuideRow ?? _heroRow;
        if (r == null) return;
        r.IsFavourite = DataService.Instance.SetFavourite(r.Variants.Select(v => v.Id), r.Channel.Id, !r.IsFavourite);
        if (_favCat != null) _favCat.Count = _all.Count(x => x.IsFavourite);
        UpdateFavButton();
        if (_cat == FavId && _search.Length == 0) ApplyFilter();
    }

    // ------------------------------------------------------------------ used by the player

    /// <summary>Channel up/down: moves the selection (the preview follows via the debounce).</summary>
    public GuideRow Step(int delta)
    {
        if (_shown.Count == 0) return null;
        int idx = ChannelList.SelectedItem is GuideRow cur ? _shown.IndexOf(cur) : -1;
        idx = idx < 0 ? 0 : (idx + delta + _shown.Count) % _shown.Count;
        var row = _shown[idx];
        ChannelList.SelectedItem = row;
        ChannelList.ScrollIntoView(row);
        return row;
    }

    /// <summary>Selects a channel by id (e.g. "previous channel"); switches to All channels if it isn't in the current list.</summary>
    public GuideRow SelectChannel(string channelId) => SelectRow(RowFor(channelId));

    /// <summary>Selects a channel by its number, as typed on the keyboard.</summary>
    public GuideRow SelectByNumber(string number)
    {
        number = number.TrimStart('0');
        return SelectRow(_all.FirstOrDefault(r => r.Number == number) ??
                         _all.FirstOrDefault(r => r.Variants.Any(v => v.Num > 0 && v.Num.ToString() == number)));
    }

    GuideRow SelectRow(GuideRow row)
    {
        if (row == null) return null;
        if (!_shown.Contains(row))
        {
            _cat = AllId;
            _suppressCat = true;
            CategoryList.SelectedItem = (CategoryList.ItemsSource as IEnumerable<CategoryItem>)?.FirstOrDefault(c => c.Id == AllId);
            _suppressCat = false;
            if (_search.Length > 0) { _search = ""; MainWindow.Instance?.ClearSearchBoxOnly(); }
            ApplyFilter();
        }
        ChannelList.SelectedItem = row;
        ChannelList.ScrollIntoView(row);
        return row;
    }

    /// <summary>Selects a channel and shows a (future) programme's details – used by Home's "Tonight's picks".</summary>
    public void ShowProgramme(GuideRow row, Programme p)
    {
        ShowAndSelect(AllId, row);
        ShowHero(row, p);
    }

    public List<GuideRow> FavouriteRows() => _all.Where(r => r.IsFavourite).ToList();
    public List<GuideRow> RecentlyWatchedRows() => RecentRows();
    public string EpgIdOf(GuideRow r) => r.EpgId ?? r.Channel.Id;

    /// <summary>Categories as shown in the guide (with channel counts).</summary>
    public IReadOnlyList<CategoryItem> Categories => CategoryList.ItemsSource as IReadOnlyList<CategoryItem> ?? Array.Empty<CategoryItem>();
    public string CurrentCategoryId => _search.Length > 0 ? AllId : _cat;

    /// <summary>The channels in a category, in guide order.</summary>
    public List<GuideRow> RowsIn(string catId) => RowsIn(catId, _all);

    List<GuideRow> RowsIn(string catId, List<GuideRow> from)
    {
        if (catId == RecentId) return RecentRows();
        IEnumerable<GuideRow> q = catId == FavId ? from.Where(r => r.IsFavourite)
            : catId == AllId ? from
            : from.Where(r => r.CategoryIds.Contains(catId));
        return Ordered(catId, q);
    }

    /// <summary>Applies your own order from the channel editor (channels you haven't moved keep their place after).</summary>
    List<GuideRow> Ordered(string catId, IEnumerable<GuideRow> rows)
    {
        var list = rows as List<GuideRow> ?? rows.ToList();
        var order = DataService.Instance.Active?.ChannelOrder;
        if (order == null || !order.TryGetValue(catId, out var ids) || ids.Count == 0) return list;
        var rank = new Dictionary<GuideRow, int>();
        for (int i = 0; i < ids.Count; i++)
            if (_byId.TryGetValue(ids[i], out var r)) rank.TryAdd(r, i);
        return list.Select((r, i) => (r, k: rank.TryGetValue(r, out var k) ? k : ids.Count + i)).OrderBy(x => x.k).Select(x => x.r).ToList();
    }

    // ------------------------------------------------------------------ channel editor

    /// <summary>The channels in a category for the channel editor, including hidden ones, in your order.</summary>
    public List<GuideRow> EditableRows(string catId) => RowsIn(catId, _everything);

    /// <summary>Categories for the channel editor (no "Recently watched" – it orders itself).</summary>
    public List<CategoryItem> EditableCategories() => Categories.Where(c => c.Id != RecentId).ToList();

    /// <summary>Re-reads names, hidden channels and order after the channel editor changed them.</summary>
    public void ApplyChannelEdits() => Rebuild();

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        var r = _ctxRow ?? ChannelList.SelectedItem as GuideRow;
        if (r == null) return;
        var name = ChannelEditorWindow.Ask(Window.GetWindow(this), "Rename channel", $"New name for {r.DisplayName}:", r.DisplayName);
        if (name == null) return;
        ChannelEditorWindow.SetName(r, name);
        SettingsStore.Save();
        Rebuild();
    }

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        var r = _ctxRow ?? ChannelList.SelectedItem as GuideRow;
        if (r == null) return;
        ChannelEditorWindow.SetHidden(r, true);
        SettingsStore.Save();
        Rebuild();
        MainWindow.Instance?.ShowToast($"{r.DisplayName} hidden – right-click a channel → Edit channels to show it again.");
    }

    void EditChannels_Click(object sender, RoutedEventArgs e)
    {
        var w = new ChannelEditorWindow(this, _cat == RecentId ? AllId : _cat) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
        Rebuild();
    }

    // ------------------------------------------------------------------ reminders & right-click

    GuideRow _ctxRow;
    Programme _ctxProg;

    void List_RightDown(object sender, MouseButtonEventArgs e)
    {
        _ctxRow = null;
        _ctxProg = null;
        var d = e.OriginalSource as DependencyObject;
        if (sender == ChannelList)
        {
            if (ItemsControl.ContainerFromElement(ChannelList, d) is ListBoxItem { DataContext: GuideRow r }) _ctxRow = r;
            if (FindBlock(d) is { } b) { _ctxRow = b.Row; _ctxProg = b.Programme; }
            if (_ctxRow != null)
            {
                if (!ReferenceEquals(ChannelList.SelectedItem, _ctxRow)) ChannelList.SelectedItem = _ctxRow;
                ShowHero(_ctxRow, _ctxProg);
            }
        }
        else if (ItemsControl.ContainerFromElement(NowList, d) is ListBoxItem { DataContext: NowItem it })
        {
            NowList.SelectedItem = it;
            _ctxRow = it.Row;
            _ctxProg = it.Programme;
        }
    }

    void Ctx_Opened(object sender, RoutedEventArgs e)
    {
        bool future = _ctxProg != null && _ctxProg.Start > DateTime.Now && _ctxRow != null;
        bool set = future && ReminderService.Has(_ctxRow.Channel.Id, _ctxProg.Start);
        foreach (var mi in new[] { CtxRemind, CtxRemind2 })
        {
            mi.Visibility = future ? Visibility.Visible : Visibility.Collapsed;
            mi.Header = set ? "Cancel reminder" : $"Remind me ({_ctxProg?.Start:ddd HH:mm})";
        }
        CtxRemindSep.Visibility = CtxRemindSep2.Visibility = future ? Visibility.Visible : Visibility.Collapsed;
    }

    void CtxRemind_Click(object sender, RoutedEventArgs e) => ToggleReminder(_ctxRow, _ctxProg);
    void Remind_Click(object sender, RoutedEventArgs e) => ToggleReminder(_heroRow, _heroProg);

    void ToggleReminder(GuideRow r, Programme p)
    {
        if (r == null || p == null || p.Start <= DateTime.Now) return;
        bool on = ReminderService.Toggle(r.Channel.Id, r.DisplayName, p);
        MainWindow.Instance?.ShowToast(on
            ? $"Reminder set – you'll get a pop-up at {p.Start.AddMinutes(-1):HH:mm} for {p.Title}"
            : "Reminder removed");
    }

    void OnRemindersChanged()
    {
        RecomputeBlocks();
        if (_mode != "grid") BuildList();
        UpdateRemindButton();
    }

    void UpdateRemindButton()
    {
        bool future = _heroRow != null && _heroProg != null && _heroProg.Start > DateTime.Now;
        RemindButton.Visibility = future ? Visibility.Visible : Visibility.Collapsed;
        RemindText.Text = future && ReminderService.Has(_heroRow.Channel.Id, _heroProg.Start) ? "Reminder set" : "Remind me";
    }

    /// <summary>Switches the guide to a category (so channel up/down stays inside it) and selects a channel.</summary>
    public void ShowAndSelect(string catId, GuideRow row)
    {
        if (_search.Length > 0) { _search = ""; MainWindow.Instance?.ClearSearchBoxOnly(); }
        if (_cat != catId)
        {
            _cat = catId;
            _suppressCat = true;
            CategoryList.SelectedItem = Categories.FirstOrDefault(c => c.Id == catId);
            _suppressCat = false;
            ApplyFilter();
        }
        if (row == null) return;
        ChannelList.SelectedItem = row;
        ChannelList.ScrollIntoView(row);
    }

    string EpgIdFor(Channel c) => c == null ? null : (RowFor(c.Id)?.EpgId ?? c.Id);
    public Programme NowOn(Channel c) => DataService.Instance.Epg.At(EpgIdFor(c), DateTime.Now);
    public Programme NextOn(Channel c)
    {
        var now = NowOn(c);
        return now == null ? null : DataService.Instance.Epg.StartingFrom(EpgIdFor(c), now.End);
    }
    /// <summary>The guide row for any version of a channel.</summary>
    public GuideRow RowFor(string channelId) => channelId != null && _byId.TryGetValue(channelId, out var r) ? r : null;

    // ------------------------------------------------------------------ "On now" / "Tonight"

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string mode || ListMode == null) return;
        bool wasFind = _mode == "find";
        _mode = mode;
        bool list = mode != "grid";
        MainWindow.Instance?.SetSearchHint(SearchHint);
        if (mode == "find") MainWindow.Instance?.FocusSearch();
        if (wasFind && mode != "find" && _search.Length > 0) ApplyFilter(); // the search box filters channels again
        ListMode.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        TimelineHeader.Visibility = list ? Visibility.Hidden : Visibility.Visible;
        NowLayer.Visibility = list ? Visibility.Hidden : Visibility.Visible;
        if (list) BuildList();
        else { NowList.ItemsSource = null; RecomputeBlocks(); }
    }

    void BuildList()
    {
        if (_mode == "grid" || NowList == null) return;
        var epg = DataService.Instance.Epg;
        var ds = DataService.Instance;
        var now = DateTime.Now;
        var items = new List<NowItem>();
        var catName = _search.Length > 0 && _mode != "find" ? $"“{_search}”" : (CategoryList.SelectedItem as CategoryItem)?.Name ?? "All channels";
        string emptyTitle = null, emptyBody = null;

        if (_mode == "find")
        {
            ListTitle.Text = "Find a programme";
            if (_search.Length < 2)
            {
                ListSub.Text = catName;
                emptyTitle = "Search the whole TV guide";
                emptyBody = "Type a programme name in the search box at the top – e.g. “match of the day” or “news”.";
            }
            else
            {
                const int Max = 400;
                foreach (var r in RowsIn(_cat))
                {
                    var l = epg.Get(r.EpgId);
                    if (l == null) continue;
                    for (int i = EpgData.FirstEndingAfter(l, now); i < l.Count; i++)
                    {
                        var p = l[i];
                        if (!p.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
                        bool live = p.Start <= now && p.End > now;
                        items.Add(new NowItem
                        {
                            Row = r, Programme = p, IsLive = live,
                            TimeText = live ? "On now" : p.Start.ToString("HH:mm"),
                            SubTime = live ? $"until {p.End:HH:mm}" : DayLabel(p.Start),
                            Progress = live ? (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds) : 0,
                            CanCatchup = live && r.CatchupChannel != null && ds.CanCatchup(r.CatchupChannel, p),
                            HasReminder = !live && ReminderService.Has(r.Channel.Id, p.Start),
                        });
                    }
                    if (items.Count > Max * 3) break;
                }
                items = items.OrderBy(x => x.IsLive ? DateTime.MinValue : x.Programme.Start).Take(Max).ToList();
                ListSub.Text = $"“{_search}”  ·  {catName}  ·  {(items.Count >= Max ? $"first {Max}" : items.Count.ToString("N0"))} programmes" +
                               "  ·  right-click one to set a reminder";
                emptyTitle = "No programmes found";
                emptyBody = $"Nothing called “{_search}” in the guide from now on.";
            }
        }
        else if (_mode == "now")
        {
            foreach (var r in _shown)
            {
                var p = epg.At(r.EpgId, now);
                if (p == null) continue;
                var left = Math.Max(1, (int)Math.Ceiling((p.End - now).TotalMinutes));
                items.Add(new NowItem
                {
                    Row = r, Programme = p, IsLive = true,
                    TimeText = $"{left} min left",
                    SubTime = $"{p.Start:HH:mm} – {p.End:HH:mm}",
                    Progress = (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds),
                    CanCatchup = r.CatchupChannel != null && ds.CanCatchup(r.CatchupChannel, p),
                });
            }
            ListTitle.Text = "On now";
            ListSub.Text = $"{catName}  ·  {items.Count:N0} programmes";
        }
        else
        {
            // Tonight: programmes starting from 6pm (or from now, if it's later) until midnight.
            var start = DateTime.Today.AddHours(18);
            var end = DateTime.Today.AddDays(1);
            string label = "Tonight";
            if (now >= end.AddHours(-1)) { start = DateTime.Today.AddDays(1).AddHours(18); end = start.AddHours(6); label = "Tomorrow night"; }
            else if (now > start) start = now;

            foreach (var r in _shown)
            {
                var l = epg.Get(r.EpgId);
                if (l == null) continue;
                for (int i = EpgData.FirstEndingAfter(l, start); i < l.Count && l[i].Start < end; i++)
                {
                    var p = l[i];
                    bool live = p.Start <= now && p.End > now;
                    if (p.Start < start && !live) continue;
                    items.Add(new NowItem
                    {
                        Row = r, Programme = p, IsLive = live,
                        TimeText = live ? "On now" : p.Start.ToString("HH:mm"),
                        SubTime = live ? $"until {p.End:HH:mm}" : $"until {p.End:HH:mm}",
                        Progress = live ? (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds) : 0,
                        HasReminder = !live && ReminderService.Has(r.Channel.Id, p.Start),
                    });
                }
            }
            items = items.OrderBy(x => x.IsLive ? DateTime.MinValue : x.Programme.Start).ToList();
            ListTitle.Text = label;
            ListSub.Text = $"{catName}  ·  {start:HH:mm} – {end:HH:mm}  ·  {items.Count:N0} programmes";
        }

        NowList.ItemsSource = items;
        bool empty = items.Count == 0;
        ListEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty && epg.MatchedChannels > 0 && emptyTitle != null)
        {
            ListEmptyTitle.Text = emptyTitle;
            ListEmptyBody.Text = emptyBody;
        }
        else if (empty)
        {
            ListEmptyTitle.Text = epg.MatchedChannels == 0 ? "No TV guide yet" : "Nothing to show";
            ListEmptyBody.Text = epg.MatchedChannels == 0
                ? "The guide appears once it has downloaded."
                : "None of the channels in this category have guide information for this time.";
        }
    }

    void NowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NowList.SelectedItem is not NowItem it) return;
        // Selecting a line previews that channel (via the normal selection) and shows the programme.
        if (!ReferenceEquals(ChannelList.SelectedItem, it.Row)) ChannelList.SelectedItem = it.Row;
        ShowHero(it.Row, it.Programme);
    }

    void NowList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(NowList, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
        WatchFull();
    }

    void NowList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { WatchFull(); e.Handled = true; }
    }
}
