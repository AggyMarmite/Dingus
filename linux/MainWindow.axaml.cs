using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dingus.Models;
using Dingus.Services;
using Dingus.Views;
using VLCState = LibVLCSharp.Shared.VLCState;

namespace Dingus;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; }

    const int MaxReconnects = 3;

    PlayerService _svc;
    readonly GuideView _guide;
    readonly HomeView _home;
    readonly LibraryView _films;
    readonly LibraryView _shows;
    readonly SettingsView _settings;
    readonly SearchView _search;
    readonly RecordingsView _recordings;
    IPage _page;

    // ----- what's playing
    bool _playerMode, _previewActive, _nowLive, _seeking, _muted, _menuOpen;
    Channel _nowChannel;
    string _prevChannelId;
    VodItem _vod;
    string _currentUrl;
    DateTime _liveStartedUtc;
    bool _recentRecorded;
    WindowState _stateBeforePlayer = WindowState.Normal;
    Rect _lastSlot;

    // ----- stream health / reconnect
    DateTime _lastProgressUtc, _lastReconnectUtc, _lastSaveUtc;
    int _lastShown, _lastLost, _reconnects;
    long _lastTime, _lastVodTime, _lastVodLen;
    readonly Queue<int> _lostWindow = new();
    bool _healthPinned, _reconnecting, _cpuWarned, _trimmedThisStream;
    DateTime _streamStartedUtc;

    // ----- in-player channel list
    bool _panelOpen;
    List<CategoryItem> _panelCats = new();
    int _panelCatIdx;
    readonly DispatcherTimer _panelTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    // ----- number entry / up next
    string _number = "";
    VodItem _upNext;
    int _upNextSeconds;

    readonly DispatcherTimer _uiTimer, _hideTimer, _toastTimer, _searchTimer, _healthTimer, _numberTimer, _upNextTimer, _spinTimer;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        RestorePlacement();

        _guide = new GuideView();
        _guide.CanPreview = () => _svc != null && !LoadingOverlayShown && !SplashUp;
        _home = new HomeView { Guide = _guide };
        _films = new LibraryView(LibraryMode.Movies);
        _shows = new LibraryView(LibraryMode.Series);
        _settings = new SettingsView();
        _search = new SearchView { Guide = _guide };
        _recordings = new RecordingsView { Guide = _guide };
        _guide.PreviewRequested += row => PlayLive(row.Channel, false);
        ReminderService.Watch = id =>
        {
            BringToFront();
            var row = _guide.RowFor(id);
            if (row == null) { ShowToast("That channel isn't in your playlist any more."); return; }
            NavGuide.IsChecked = true;
            _guide.SelectChannel(row.Channel.Id);
            PlayLive(row.Channel, true);
        };
        ReminderService.Start();
        RecordingService.Start();
        Parental.Changed += UpdateLockButton;
        Opened += (_, _) => UpdateLockButton();

        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += async (_, _) =>
        {
            if (_playerMode || SelfTest.Dir != null) { updateTimer.Interval = TimeSpan.FromMinutes(10); return; }
            updateTimer.Stop();
            await Updater.CheckAsync(this, manual: false);
        };
        updateTimer.Start();
        _healthPinned = SettingsStore.Current.ShowStreamInfo;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _uiTimer.Tick += (_, _) => UpdatePlayerUi();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hideTimer.Tick += (_, _) => HideChrome();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Motion.FadeOut(Toast, toY: 8); Motion.FadeOut(OverlayToast, toY: -8); };
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunGlobalSearch(); };
        _healthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _healthTimer.Tick += async (_, _) => await HealthTick();
        _numberTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _numberTimer.Tick += (_, _) => CommitNumber();
        _upNextTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _upNextTimer.Tick += (_, _) => UpNextTick();
        _panelTimer.Tick += (_, _) => ClosePanel();
        _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _spinTimer.Tick += (_, _) => SpinTick();

        var ds = DataService.Instance;
        ds.BusyChanged += OnBusyChanged;
        ds.Message += ShowToast;
        ds.CatalogChanged += () => { if (_nowLive && _nowChannel != null && !ds.Catalog.Channels.Any(c => c.Id == _nowChannel.Id)) StopPlayback(); };

        Opened += OnOpened;
        Closing += OnClosing;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateWindowFrame();
        };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Root.LayoutUpdated += (_, _) => { if (_previewActive && !_playerMode) PositionPreview(); };
        Video.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                UpdateFillRatio();
                if (SettingsStore.Current.Crop == "fill") _svc?.ApplyPicture();
            }
        };

        if (Os.Mac) UseMacWindowButtons();

        // Title bar: drag to move, double-click to maximise.
        DragArea.PointerPressed += TitleBar_PointerPressed;
        TitleBar.PointerPressed += TitleBar_PointerPressed;
        TitleBar.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) TitleBar_SizeChanged(TitleBar.Bounds.Width); };

        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => _seeking = true, RoutingStrategies.Tunnel, true);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) => { _svc?.SeekTo((long)SeekSlider.Value); _seeking = false; }, RoutingStrategies.Tunnel, true);
        LiveSeek.AddHandler(PointerPressedEvent, (_, _) => LiveSeek_Down(), RoutingStrategies.Tunnel, true);
        LiveSeek.AddHandler(PointerReleasedEvent, (_, _) => LiveSeek_Up(), RoutingStrategies.Tunnel, true);
        LiveSeek.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) LiveSeek_ValueChanged(); };
        VolumeSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Volume_Changed((double)e.NewValue!); };

        VolumeSlider.Value = SettingsStore.Current.Volume;
        // Mini player: the volume slider slides out while the pointer is over the mute button. It and the player's own
        // slider are the same volume, kept in step both ways.
        MiniVolumeSlider.Value = VolumeSlider.Value;
        MiniVolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && Math.Abs(VolumeSlider.Value - (double)e.NewValue!) > 0.5) VolumeSlider.Value = (double)e.NewValue!;
        };
        MiniVolume.PointerEntered += (_, _) => SlideMiniVolume(open: true);
        MiniVolume.PointerExited += (_, _) => { if (!MiniVolumeSlider.IsPointerOver) SlideMiniVolume(open: false); };
        (SettingsStore.Current.StartOnGuide ? NavGuide : NavHome).IsChecked = true;
        OnPlaylistChanged();

        _startupLoading = SettingsStore.Current.Playlists.Count > 0;
        UpdateLoadingOverlay(null);

        // The splash goes once your saved playlist is on Home (the fresh copy carries on downloading in the
        // background) – or after a few seconds whatever happens.
        SplashDrag.PointerPressed += TitleBar_PointerPressed;
        DataService.Instance.CatalogChanged += () =>
        {
            UpdateLoadingOverlay(_busyStep);
            var c = DataService.Instance.Catalog;
            if (c.Channels.Count + c.Movies.Count + c.Series.Count > 0)
                Dispatcher.UIThread.Post(() => HideSplash(), DispatcherPriority.Background);
        };
        var splashLimit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        splashLimit.Tick += (_, _) => { splashLimit.Stop(); HideSplash(); };
        splashLimit.Start();
        UpdateWelcome();
    }

    async void OnOpened(object sender, EventArgs e)
    {
        UpdateWindowFrame();
        try
        {
            if (VlcPaths.Disabled) throw new OperationCanceledException("video engine turned off (DINGUS_NO_VLC)");
            if (VlcPaths.StartError != null) throw VlcPaths.StartError;
            _svc = await Task.Run(() => new PlayerService());
            Video.MediaPlayer = _svc.Player;
            HookPlayerEvents();
            _guide.TryAutoPreview();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (VlcPaths.Disabled) { }
            else if (SelfTest.Dir != null) SelfTest.Error(ex);
            else await Dialogs.Message(this, "The video engine didn't start",
                "Dingus uses VLC to play video. " + ex.Message + (Os.Mac ? "\n\nTry downloading Dingus again from the releases page." : "\n\nIf you're not using the AppImage, install VLC (for example: sudo apt install vlc)."));
        }

        if (SettingsStore.Current.Playlists.Count > 0 && !DataService.Instance.HasStartedLoading)
        {
            await DataService.Instance.LoadActiveAsync(refreshNow: true);
            HideStartupLoadingIfIdle();
        }
    }

    bool _closeConfirmed;

    async void OnClosing(object sender, WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && RecordingService.ActiveCount > 0 && SelfTest.Dir == null)
        {
            e.Cancel = true;
            if (!await Dialogs.Confirm(this, "A recording is in progress",
                    "If you close Dingus it stops (what's recorded so far is kept). Close anyway?", "Close", "Keep recording", danger: true)) return;
            _closeConfirmed = true;
            Close();
            return;
        }
        RecordingService.StopAll();
        SaveVodProgress();
        SavePlacement();
        SettingsStore.Save();
        HistoryStore.SaveNow();
        if (_svc != null) Task.Run(() => _svc.Player.Stop()).Wait(TimeSpan.FromSeconds(2));
        KeepAwake.Set(false);
    }

    void HideStartupLoadingIfIdle()
    {
        if (!_startupLoading) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_startupLoading || DataService.Instance.IsBusy) return;
            _startupLoading = false;
            UpdateLoadingOverlay(null);
        }, DispatcherPriority.Background);
    }

    /// <summary>Called when Dingus is opened again while it's already running.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>Opens Live TV on a channel (and optionally shows one of its programmes).</summary>
    public void ShowInGuide(GuideRow row, Programme p)
    {
        NavGuide.IsChecked = true;
        if (p != null) _guide.ShowProgramme(row, p);
        else _guide.SelectChannel(row.Channel.Id);
    }

    /// <summary>Opens a film or show's details page.</summary>
    public void OpenLibraryItem(bool movie, string id)
    {
        (movie ? NavFilms : NavShows).IsChecked = true;
        (movie ? _films : _shows).OpenItem(id);
    }

    /// <summary>Opens Films or TV Shows on a category (e.g. My List).</summary>
    public void OpenLibraryCategory(bool movie, string categoryId)
    {
        (movie ? NavFilms : NavShows).IsChecked = true;
        (movie ? _films : _shows).ShowCategory(categoryId);
    }

    /// <summary>Opens Multi-view, starting with <paramref name="first"/> (or what's playing) in the first screen.</summary>
    public void OpenMultiView(GuideRow first)
    {
        if (_svc == null) return;
        if (_nowLive && _nowChannel != null) first = _guide.RowFor(_nowChannel.Id) ?? first;
        if (_mini) LeaveMiniWindow();
        if (_playerMode) ExitPlayerLayout();
        StopPlayback(); // frees the connection the main player was using
        var mv = new MultiViewWindow(_svc.Vlc, _guide, first);
        mv.Closed += async (_, _) =>
        {
            Activate();
            if (mv.OpenFullScreen && mv.FocusedRow != null)
            {
                await Task.Delay(800); // let the tiles hang up first, so we don't go over the connection limit
                PlayLive(mv.FocusedRow.Channel, true);
            }
        };
        mv.Show(this);
    }

    // =====================================================================================
    // Window size & position
    // =====================================================================================

    /// <summary>
    /// Opens as a window (never maximised): the size you last left it if that's smaller than most of the screen,
    /// otherwise centred at about 80% of the screen.
    /// </summary>
    void RestorePlacement()
    {
        var s = SettingsStore.Current;
        var screen = Screens?.Primary;
        double sw = 1600, sh = 900;
        if (screen != null) { sw = screen.WorkingArea.Width / screen.Scaling; sh = screen.WorkingArea.Height / screen.Scaling; }
        double maxW = Math.Max(MinWidth, Math.Round(sw * 0.82)), maxH = Math.Max(MinHeight, Math.Round(sh * 0.82));
        if (s.WindowWidth is double w && s.WindowHeight is double h && w > 400 && h > 300 && w <= maxW && h <= maxH)
        {
            Width = Math.Max(MinWidth, w);
            Height = Math.Max(MinHeight, h);
            if (s.WindowLeft is double l && s.WindowTop is double t)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint((int)l, (int)t);
            }
            return;
        }
        Width = Math.Max(MinWidth, Math.Min(1440, Math.Round(sw * 0.8)));
        Height = Math.Max(MinHeight, Math.Min(900, Math.Round(sh * 0.8)));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    void SavePlacement()
    {
        var s = SettingsStore.Current;
        var state = _playerMode ? _stateBeforePlayer : WindowState;
        s.WindowMaximized = state == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowLeft = Position.X; s.WindowTop = Position.Y;
            s.WindowWidth = Bounds.Width; s.WindowHeight = Bounds.Height;
        }
    }

    void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v != TitleBar && v != DragArea && v.FindAncestorOfType<Button>() != null) return;
        if (e.Source is TextBox || (e.Source as Visual)?.FindAncestorOfType<TextBox>() != null) return;
        if (e.Source is Visual rv && rv.FindAncestorOfType<RadioButton>() != null) return;
        if (e.ClickCount == 2) { Maximise_Click(null, null); return; }
        BeginMoveDrag(e);
    }

    void Grip_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || sender is not Control c) return;
        var edge = (c.Tag as string) switch
        {
            "W" => WindowEdge.West, "E" => WindowEdge.East, "N" => WindowEdge.North, "S" => WindowEdge.South,
            "NW" => WindowEdge.NorthWest, "NE" => WindowEdge.NorthEast, "SW" => WindowEdge.SouthWest, _ => WindowEdge.SouthEast,
        };
        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    /// <summary>
    /// Mac: the usual red/yellow/green buttons (and native resizing) at the left of Dingus's own title bar,
    /// instead of the Windows-style buttons on the right.
    /// </summary>
    void UseMacWindowButtons()
    {
        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = 46;
        MinButton.IsVisible = MaxButton.IsVisible = CloseButton.IsVisible = false;
        ResizeGrips.IsVisible = false;
        TitleLeft.Margin = new Thickness(80, 0, 0, 0);
        PropertyChanged += (_, e) =>
        {
            // Full screen hides the window buttons, so the title bar doesn't need to leave room for them.
            if (e.Property == WindowStateProperty)
                TitleLeft.Margin = new Thickness(WindowState == WindowState.FullScreen ? 12 : 80, 0, 0, 0);
        };
    }

    /// <summary>Mac menu bar: Dingus → Settings / About.</summary>
    public void OpenSettingsPage()
    {
        if (_mini) ExitMini(false);
        ExitPlayer();
        BringToFront();
        NavSettings.IsChecked = true;
    }

    /// <summary>The Dingus logo at the top left goes Home.</summary>
    void HomeLogo_Click(object sender, RoutedEventArgs e) => NavHome.IsChecked = true;

    void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void UpdateWindowFrame()
    {
        bool max = WindowState is WindowState.Maximized or WindowState.FullScreen;
        ResizeGrips.IsVisible = !max && !_mini && !Os.Mac;
        MaxButton.Content = WindowState == WindowState.Maximized ? Icons.Restore : Icons.Maximise;
        ToolTip.SetTip(MaxButton, WindowState == WindowState.Maximized ? "Restore down" : "Maximise");
    }

    int _barLevel = -1;
    readonly Dictionary<RadioButton, object> _tabLabels = new();

    /// <summary>Narrow windows: drop the word mark, then the tab labels, so search always has room.</summary>
    void TitleBar_SizeChanged(double w)
    {
        int level = w >= 1280 ? 0 : w >= 1120 ? 1 : 2;
        if (level == _barLevel) return;
        _barLevel = level;
        WordMark.IsVisible = level == 0;
        PlaylistName.MaxWidth = level == 0 ? 130 : 80;
        foreach (var tab in new[] { NavHome, NavGuide, NavFilms, NavShows, NavRecordings })
        {
            if (!_tabLabels.ContainsKey(tab)) _tabLabels[tab] = tab.Content;
            bool iconOnly = level == 2;
            tab.Classes.Set("compact", iconOnly);
            ToolTip.SetTip(tab, iconOnly ? _tabLabels[tab] : (tab == NavRecordings ? "Recordings & reminders" : null));
        }
    }

    // =====================================================================================
    // Navigation & shell
    // =====================================================================================

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_guide == null || sender is not RadioButton { IsChecked: true } rb) return;
        HomeLogo.Classes.Set("home", rb == NavHome);
        IPage page = _guide;
        if (rb == NavHome) page = _home;
        else if (rb == NavFilms) page = _films;
        else if (rb == NavShows) page = _shows;
        else if (rb == NavSettings) page = _settings;
        else if (rb == NavSearch) page = _search;
        else if (rb == NavRecordings) page = _recordings;
        // Settings sits apart from the tabs: un-check the others by hand.
        foreach (var other in new[] { NavHome, NavGuide, NavFilms, NavShows, NavRecordings, NavSearch, NavSettings })
            if (other != rb && other.IsChecked == true) other.IsChecked = false;
        Navigate(page);
    }

    void Navigate(IPage page)
    {
        if (_page == page) return;
        if (_previewActive && !_playerMode && _page == _guide && page != _guide) StopPlayback();
        _lastSlot = default;

        _page?.OnHidden();
        _page = page;
        PageHost.Opacity = 0;
        PageHost.Content = page;
        Dispatcher.UIThread.Post(() => PageHost.Opacity = 1, DispatcherPriority.Background);
        _searchTimer.Stop();
        if (page != _search && (SearchBox.Text ?? "").Length > 0) SearchBox.Text = "";
        _searchTimer.Stop();
        page.ApplySearch(page == _search ? (SearchBox.Text ?? "").Trim() : "");
        page.OnShown();
        UpdateWelcome();
        bool overlay = UpdateLoadingOverlay(_busyStep);
        bool busy = DataService.Instance.IsBusy;
        BusyPanel.IsVisible = busy && !overlay;
        Spin();
    }

    /// <summary>No playlist yet: the splash stays up with "Add playlist" (Settings can still be opened over it).</summary>
    void UpdateWelcome()
    {
        Welcome.IsVisible = false; // the splash does this job now
        if (Splash == null) return;
        bool none = SettingsStore.Current.Playlists.Count == 0;
        if (none && _page != _settings) ShowSplash(withAdd: true);
        else if (none || SplashAdd.IsVisible) HideSplash(now: true); // Settings opened, or a playlist was just added
    }

    // ------------------------------------------------------------------ splash

    readonly DateTime _splashShownAt = DateTime.UtcNow;
    bool _splashGone;
    DispatcherTimer _splashOff;
    /// <summary>The splash is up – the video (drawn on its own surface, above everything) has to stay hidden meanwhile.</summary>
    bool SplashUp => Splash != null && Splash.IsVisible && !_splashGone;

    void ShowSplash(bool withAdd)
    {
        SplashAdd.IsVisible = withAdd;
        if (SplashUp) return;
        _splashOff?.Stop();
        _splashGone = false;
        Splash.Opacity = 1;
        SplashContent.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
        Splash.IsVisible = true;
        HideVideo();
    }

    /// <summary>
    /// Fades the splash away (after it's been up for at least a moment, so it doesn't just flicker) – the logo grows
    /// a touch as it goes.
    /// </summary>
    void HideSplash(bool now = false)
    {
        if (!SplashUp) return;
        if (SettingsStore.Current.Playlists.Count == 0 && _page != _settings) return; // waiting for a playlist
        var wait = TimeSpan.FromMilliseconds(800) - (DateTime.UtcNow - _splashShownAt);
        if (!now && wait > TimeSpan.Zero)
        {
            var t = new DispatcherTimer { Interval = wait };
            t.Tick += (_, _) => { t.Stop(); HideSplash(now: true); };
            t.Start();
            return;
        }
        _splashGone = true;
        SplashAdd.IsVisible = false;
        Splash.Opacity = 0;
        SplashContent.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.05)");
        _splashOff = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(340) };
        _splashOff.Tick += (_, _) =>
        {
            _splashOff.Stop();
            if (!_splashGone) return;
            Splash.IsVisible = false;
            // Now the last channel can start in Live TV's mini player (it waited for the splash).
            _guide.TryAutoPreview();
        };
        _splashOff.Start();
    }

    public void ClearSearch()
    {
        ClearSearchBoxOnly();
        _page?.ApplySearch("");
    }

    public void SetSearchHint(string hint) { }

    IPage _beforeSearch;

    void RunGlobalSearch()
    {
        var q = (SearchBox.Text ?? "").Trim();
        if (q.Length > 0 && _page != _search)
        {
            _beforeSearch = _page;
            NavSearch.IsChecked = true;
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            return;
        }
        if (_page == _search) _search.ApplySearch(q);
    }

    void LeaveSearch()
    {
        ClearSearchBoxOnly();
        if (_page != _search) return;
        var back = _beforeSearch ?? _home;
        (back == _guide ? NavGuide : back == _films ? NavFilms : back == _shows ? NavShows : back == _settings ? NavSettings
            : back == _recordings ? NavRecordings : NavHome).IsChecked = true;
    }

    public void OpenChannelEditor()
    {
        if (DataService.Instance.Active == null) { ShowToast("Add a playlist first."); return; }
        _ = OpenChannelEditorAsync();
    }

    async Task OpenChannelEditorAsync()
    {
        await new ChannelEditorWindow(_guide, GuideView.AllId).ShowDialog(this);
        _guide.ApplyChannelEdits();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ClearSearchBoxOnly()
    {
        _searchTimer.Stop();
        SearchBox.Text = "";
        _searchTimer.Stop();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { LeaveSearch(); e.Handled = true; }
        else if (e.Key == Key.Enter) { _searchTimer.Stop(); RunGlobalSearch(); e.Handled = true; }
    }

    public void OnPlaylistChanged()
    {
        var active = DataService.Instance.Active;
        PlaylistName.Text = active?.Name ?? "No playlist";
        UpdateWelcome();
        RefreshButton.IsEnabled = active != null;
        _prevChannelId = null;
        StopPlayback();
    }

    void PlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight };
        var active = DataService.Instance.Active;
        foreach (var p in SettingsStore.Current.Playlists)
        {
            var item = new MenuItem { Header = p.Name, ToggleType = MenuItemToggleType.Radio, IsChecked = p == active };
            var target = p;
            item.Click += async (_, _) =>
            {
                if (target == DataService.Instance.Active) return;
                SettingsStore.Current.ActivePlaylistId = target.Id;
                SettingsStore.Save();
                OnPlaylistChanged();
                await DataService.Instance.LoadActiveAsync(refreshNow: true);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add playlist…" };
        add.Click += (_, _) => AddPlaylist_Click(null, null);
        menu.Items.Add(add);
        var manage = new MenuItem { Header = "Manage playlists…" };
        manage.Click += (_, _) => NavSettings.IsChecked = true;
        menu.Items.Add(manage);
        menu.Open(PlaylistButton);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await DataService.Instance.RefreshAsync();

    async void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PlaylistDialog(null);
        await dlg.ShowDialog(this);
        if (dlg.Result == null) return;
        SettingsStore.Current.Playlists.Add(dlg.Result);
        SettingsStore.Current.ActivePlaylistId = dlg.Result.Id;
        SettingsStore.Save();
        OnPlaylistChanged();
        await DataService.Instance.LoadActiveAsync(refreshNow: true);
    }

    // Loading screen: from the moment the app opens (and on first sign-in) until the playlist has loaded.
    bool _startupLoading, _loadingDismissed;
    string _busyStep;

    void OnBusyChanged(bool busy, string text)
    {
        var ds = DataService.Instance;
        if (!busy || ds.BusyIsGuide) _startupLoading = false;
        if (busy && ds.IsFirstLoad && !ds.BusyIsGuide && !_startupLoading && !LoadingOverlayShown) _loadingDismissed = false;
        _busyStep = busy ? text : null;
        bool overlay = UpdateLoadingOverlay(text);
        BusyPanel.IsVisible = busy && !overlay;
        BusyTitle.Text = ds.BusyIsGuide ? "Updating TV guide" : ds.IsFirstLoad ? "Loading playlist" : "Refreshing playlist";
        BusyText.Text = text == null ? "" : StepText(text);
        RefreshButton.IsEnabled = !busy && ds.Active != null;
        Spin();
    }

    bool LoadingOverlayShown => LoadingOverlay.IsVisible;

    void LoadingSettings_Click(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

    bool UpdateLoadingOverlay(string step)
    {
        var ds = DataService.Instance;
        bool playlistWork = ds.IsBusy && !ds.BusyIsGuide && ds.IsFirstLoad;
        bool hasSavedNow = ds.Catalog.Channels.Count > 0 || ds.Catalog.Movies.Count > 0 || ds.Catalog.Series.Count > 0;
        // With a saved playlist Dingus opens straight on it and refreshes in the background (the pill in the title bar
        // shows progress); the full loading screen is only for when there's nothing to show yet.
        bool show = !_loadingDismissed && (playlistWork || _startupLoading) && SettingsStore.Current.Playlists.Count > 0 &&
                    _page != _settings && !hasSavedNow;
        // Nothing saved: the loading screen takes over from the splash.
        if (show && playlistWork && Splash != null) Dispatcher.UIThread.Post(() => HideSplash(), DispatcherPriority.Background);
        bool was = LoadingOverlayShown;
        LoadingOverlay.IsVisible = show;
        if (show)
        {
            bool hasSaved = ds.Catalog.Channels.Count > 0 || ds.Catalog.Movies.Count > 0 || ds.Catalog.Series.Count > 0;
            LoadingStep.Text = step ?? (hasSaved ? "Opening your saved channels…" : "Starting…");
            LoadingHint.Text = hasSaved ? "Getting the latest channels, films and shows from your provider." : "Large playlists can take a minute the first time.";
            LoadingSkip.IsVisible = hasSaved;
            HideVideo();
        }
        Spin();
        if (was && !show) Dispatcher.UIThread.Post(() => _guide.TryAutoPreview(), DispatcherPriority.Loaded);
        return show;
    }

    void LoadingSkip_Click(object sender, RoutedEventArgs e)
    {
        _loadingDismissed = true;
        _startupLoading = false;
        OnBusyChanged(DataService.Instance.IsBusy, DataService.Instance.IsBusy ? (BusyText.Text ?? "").TrimStart('·', ' ') : null);
    }

    static string StepText(string t) => "·  " + (t switch
    {
        "Connecting…" => "connecting",
        "Signing in…" => "signing in",
        "Loading channels…" => "channels",
        "Loading films…" => "films",
        "Loading TV shows…" => "TV shows",
        "Downloading playlist…" => "downloading",
        "Reading playlist…" => "reading",
        "Downloading TV guide…" => "downloading",
        "Matching TV guide…" => "matching channels",
        _ => t.TrimEnd('…'),
    });

    double _spinAngle;

    void Spin()
    {
        bool on = BusyPanel.IsVisible || LoadingOverlay.IsVisible;
        if (on && !_spinTimer.IsEnabled) _spinTimer.Start();
        else if (!on) _spinTimer.Stop();
    }

    void SpinTick()
    {
        _spinAngle = (_spinAngle + 360.0 / 33) % 360; // about one turn a second
        var rt = new RotateTransform(_spinAngle);
        if (BusyPanel.IsVisible) BusyIcon.RenderTransform = rt;
        if (LoadingOverlay.IsVisible) LoadingArc.RenderTransform = rt;
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (_playerMode)
        {
            OverlayToastText.Text = message;
            Motion.FadeIn(OverlayToast, fromY: -10);
        }
        else
        {
            ToastText.Text = message;
            Motion.FadeIn(Toast, fromY: 10);
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // =====================================================================================
    // Playback entry points
    // =====================================================================================

    /// <summary>Plays a live channel – in Live TV's mini player, or full screen.</summary>
    public void PlayLive(Channel ch, bool fullscreen)
    {
        if (_svc == null || ch == null) return;
        if (!StartLive(ch)) return;
        if (_playerMode) { ConfigureChrome(); UpdateChromeText(); return; }
        if (fullscreen) { EnterPlayer(); return; }
        _previewActive = true;
        _lastSlot = default;
        PositionPreview();
    }

    public void PlayLiveMini(Channel ch)
    {
        if (_svc == null || ch == null) return;
        if (!StartLive(ch)) return;
        EnterMini();
    }

    public void PlayCatchup(Channel ch, Programme p)
    {
        if (!DataService.Instance.CanCatchup(ch, p)) { ShowToast("Catch-up isn't available for this programme."); return; }
        PlayFromArchive(ch, p.Start);
    }

    List<Channel> _variants = new();
    int _variantIdx;

    bool StartLive(Channel ch)
    {
        var row = _guide.RowFor(ch.Id);
        var identity = row?.Channel ?? ch;
        bool same = _nowLive && _nowChannel?.Id == identity.Id && _currentUrl != null;
        if (!same)
        {
            var order = row != null ? ChannelMerger.Order(row.Variants, ChannelMerger.CurrentMaxRank()) : new List<Channel> { ch };
            if (row != null && !ReferenceEquals(ch, identity) && order.Contains(ch)) { order.Remove(ch); order.Insert(0, ch); }
            SaveVodProgress();
            if (_nowLive && _nowChannel != null && _nowChannel.Id != identity.Id && (DateTime.UtcNow - _liveStartedUtc).TotalSeconds >= 5)
                _prevChannelId = _nowChannel.Id;
            _variants = order;
            _variantIdx = 0;
            if (!PlayVariant()) return false;
            _liveStartedUtc = DateTime.UtcNow;
            _recentRecorded = false;
        }
        _nowLive = true;
        _nowChannel = identity;
        _vod = null;
        SettingsStore.Current.LastChannelId = identity.Id;
        _guide.SetPreviewPlaying(true);
        OnPlaybackStarted();
        return true;
    }

    bool PlayVariant()
    {
        if (_variantIdx < 0 || _variantIdx >= _variants.Count) return false;
        var url = DataService.Instance.StreamUrl(_variants[_variantIdx]);
        if (url == null) return false;
        if (_startArchiveAtUtc != null && _guide.RowFor(_variants[_variantIdx].Id)?.CatchupChannel is { } archive && TryStartPendingArchive(archive))
        {
            _currentUrl = url;
            ResetHealth();
            return true;
        }
        _startArchiveAtUtc = null;
        try { _svc.Play(url, true, 0, PictureFor(_variants[_variantIdx])); }
        catch (Exception ex) { ShowToast(ex.Message); return false; }
        _currentUrl = url;
        ResetHealth();
        SetBuffering("Tuning…");
        return true;
    }

    void SwitchVariant(int index, string why)
    {
        if (index < 0 || index >= _variants.Count || index == _variantIdx && why == null) return;
        _variantIdx = index;
        if (!PlayVariant()) return;
        if (why != null) ShowToast(why);
        ConfigureChrome();
    }

    void Quality_Click(object sender, RoutedEventArgs e)
    {
        if (_variants.Count < 2) return;
        var menu = NewMenu();
        for (int i = 0; i < _variants.Count; i++)
        {
            var v = _variants[i];
            int idx = i;
            Add(menu, $"{ChannelMerger.Label(v)}  –  {v.Name}", () => SwitchVariant(idx, null), i == _variantIdx);
        }
        OpenMenu(menu, QualityButton);
    }

    /// <summary>Plays a film, episode or catch-up programme full screen.</summary>
    public void PlayVod(VodItem item, bool resume)
    {
        if (_svc == null || item?.Url == null) return;
        SaveVodProgress();
        long start = 0;
        var h = resume ? HistoryStore.Get(item.ResumeKey) : null;
        if (h != null && h.CanResume) start = Math.Max(0, h.PositionMs - 3000);
        try { _svc.Play(item.Url, false, start); }
        catch (Exception ex) { ShowToast(ex.Message); return; }
        _previewActive = false;
        _nowLive = false;
        _nowChannel = null;
        _vod = item;
        _currentUrl = item.Url;
        _lastVodTime = start;
        _lastVodLen = h?.DurationMs ?? 0;
        _guide.SetPreviewPlaying(false);
        HideUpNext();
        ResetHealth();
        SetBuffering("Loading…");
        OnPlaybackStarted();
        if (_playerMode) { ConfigureChrome(); UpdateChromeText(); ShowChrome(); }
        else EnterPlayer();
        if (start > 0) ShowToast($"Resuming from {Fmt(start)}  ·  press Home to start over");
    }

    void OnPlaybackStarted() => _healthTimer.Start();

    void StopPlayback()
    {
        SaveVodProgress();
        _svc?.Stop();
        _previewActive = false;
        _nowLive = false;
        _nowChannel = null;
        _vod = null;
        _currentUrl = null;
        _healthTimer?.Stop();
        KeepAwake.Set(false);
        _guide?.SetPreviewPlaying(false);
        SetBuffering(null);
        HideUpNext();
        if (_playerMode) ExitPlayerLayout();
        HideVideo();
    }

    // =====================================================================================
    // Video layout: hidden / Live TV preview / the whole window
    // =====================================================================================

    void HideVideo()
    {
        _lastSlot = default;
        Video.IsVisible = false;
        PreviewHint.IsVisible = false;
        PlayerChrome.IsVisible = false;
        HealthBadge.IsVisible = false;
    }

    void PositionPreview()
    {
        var slot = _page == _guide ? _guide.PreviewSlotElement : null;
        if (slot == null || !slot.IsEffectivelyVisible || Welcome.IsVisible || LoadingOverlayShown || SplashUp) { HideVideo(); return; }
        if (slot.Bounds.Width < 10) return;
        var p = slot.TranslatePoint(new Point(0, 0), Root);
        var hp = PageHost.TranslatePoint(new Point(0, 0), Root);
        if (p == null || hp == null) return;
        var r = new Rect(p.Value.X, p.Value.Y, slot.Bounds.Width, slot.Bounds.Height);
        var host = new Rect(hp.Value.X, hp.Value.Y, PageHost.Bounds.Width, PageHost.Bounds.Height);
        if (!host.Contains(r)) { if (Video.IsVisible) HideVideo(); return; }
        if (r == _lastSlot && Video.IsVisible) return;
        _lastSlot = r;
        Video.HorizontalAlignment = HorizontalAlignment.Left;
        Video.VerticalAlignment = VerticalAlignment.Top;
        Video.Margin = new Thickness(r.X, r.Y, 0, 0);
        Video.Width = r.Width;
        Video.Height = r.Height;
        Video.IsVisible = true;
        PreviewHint.IsVisible = true;
        PlayerChrome.IsVisible = false;
    }

    void FillWindowWithVideo()
    {
        _lastSlot = default;
        Toast.IsVisible = false;
        Video.HorizontalAlignment = HorizontalAlignment.Stretch;
        Video.VerticalAlignment = VerticalAlignment.Stretch;
        Video.Margin = new Thickness(0);
        Video.Width = double.NaN;
        Video.Height = double.NaN;
        Video.IsVisible = true;
        Shell.IsVisible = false;
        PreviewHint.IsVisible = false;
    }

    void EnterPlayer()
    {
        if (_mini) LeaveMiniWindow();
        MiniChrome.IsVisible = false;
        _playerMode = true;
        _stateBeforePlayer = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
        FillWindowWithVideo();
        SeekSlider.Value = 0;
        PosText.Text = LenText.Text = "0:00";
        ConfigureChrome();
        WindowState = WindowState.FullScreen;
        UpdateChromeText();
        ShowChrome();
        _uiTimer.Start();
        Activate();
        Overlay.Focus();
    }

    void ConfigureChrome()
    {
        bool live = _nowLive;
        SeekRow.IsVisible = !live;
        LiveRow.IsVisible = live;
        ChUpButton.IsVisible = ChDownButton.IsVisible = live;
        ChannelsButton.IsVisible = live;
        MultiViewButton.IsVisible = live;
        LastChButton.IsVisible = live && _prevChannelId != null;
        BackSeekButton.IsVisible = FwdSeekButton.IsVisible = !live || LiveSeekable;
        FwdSeekButton.Content = live ? Icons.Fwd10 : Icons.Fwd30;
        ToolTip.SetTip(FwdSeekButton, live ? "Forward 10 seconds (→)" : "Forward 30 seconds (→)");
        NextEpisodeButton.IsVisible = !live && _vod?.Next != null;
        QualityButton.IsVisible = live && _variants.Count > 1;
        RecordButton.IsVisible = live;
        if (live && _variants.Count > 1 && _variantIdx < _variants.Count)
            ToolTip.SetTip(QualityButton, $"Version: {ChannelMerger.Label(_variants[_variantIdx])} ({_variantIdx + 1} of {_variants.Count})");
        ToolTip.SetTip(NextEpisodeButton, _vod?.NextLabel);
    }

    void ExitPlayer()
    {
        if (!_playerMode) return;
        CloseShortcutSheet();
        SaveVodProgress();
        ExitPlayerLayout();
        if (_nowLive && _page == _guide)
        {
            _previewActive = true;
            Dispatcher.UIThread.Post(PositionPreview, DispatcherPriority.Loaded);
        }
        else StopPlayback();
    }

    void ExitPlayerLayout()
    {
        if (_mini) LeaveMiniWindow();
        ClosePanel();
        _playerMode = false;
        _uiTimer.Stop();
        _hideTimer.Stop();
        Overlay.Cursor = Cursor.Default;
        Motion.HideNow(OverlayToast);
        Motion.HideNow(PlayerChrome);
        Motion.HideNow(InfoBar);
        _browseRow = null;
        HealthBadge.IsVisible = false;
        HideUpNext();
        ClearNumber();
        if (WindowState == WindowState.FullScreen) WindowState = _stateBeforePlayer;
        Shell.IsVisible = true;
        HideVideo();
    }

    // ----- picture-in-picture: the window becomes a small player that stays on top -----

    bool _mini;
    PixelPoint _miniSavedPos;
    double _miniSavedW, _miniSavedH;
    WindowState _miniSavedState;

    void Mini_Click(object sender, RoutedEventArgs e) => EnterMini();
    void MiniExpand_Click(object sender, RoutedEventArgs e) => ExitMini(true);
    void MiniClose_Click(object sender, RoutedEventArgs e) => ExitMini(false);

    public void EnterMini()
    {
        if (_mini || _svc == null || (!_nowLive && _vod == null)) return;
        ClosePanel();
        _miniSavedState = _playerMode ? _stateBeforePlayer : WindowState;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        _miniSavedPos = Position;
        _miniSavedW = Width; _miniSavedH = Height;

        _mini = true;
        _playerMode = true;
        _previewActive = false;
        FillWindowWithVideo();
        Motion.HideNow(PlayerChrome);
        HealthBadge.IsVisible = false;
        _hideTimer.Stop();
        ResizeGrips.IsVisible = false;
        if (Os.Mac) SystemDecorations = SystemDecorations.None; // no window buttons over the little player
        MinWidth = 0; MinHeight = 0;
        var s = SettingsStore.Current;
        double w = Math.Clamp(s.MiniWidth ?? 480, 320, 1280), h = w * 9 / 16;
        Width = w; Height = h;
        Topmost = true;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var wa = screen.WorkingArea;
            double k = screen.Scaling;
            int left = (int)(wa.Right - (w + 16) * k), top = (int)(wa.Bottom - (h + 16) * k);
            if (s.MiniLeft is double ml && s.MiniTop is double mt && ml >= wa.X - 20 && mt >= wa.Y - 20 && ml + w * k <= wa.Right + 20 && mt + h * k <= wa.Bottom + 20)
            { left = (int)ml; top = (int)mt; }
            Position = new PixelPoint(left, top);
        }
        UpdateChromeText();
        _uiTimer.Start();
    }

    void LeaveMiniWindow()
    {
        if (!_mini) return;
        var s = SettingsStore.Current;
        s.MiniLeft = Position.X; s.MiniTop = Position.Y; s.MiniWidth = Width;
        _mini = false;
        MiniChrome.IsVisible = false;
        Topmost = false;
        if (Os.Mac) SystemDecorations = SystemDecorations.Full;
        MinWidth = 900; MinHeight = 540;
        Width = _miniSavedW; Height = _miniSavedH;
        Position = _miniSavedPos;
        if (_miniSavedState == WindowState.Maximized) WindowState = WindowState.Maximized;
        _stateBeforePlayer = _miniSavedState;
        UpdateWindowFrame();
    }

    void ExitMini(bool toPlayer)
    {
        if (!_mini) return;
        LeaveMiniWindow();
        if (toPlayer) EnterPlayer();
        else ExitPlayer();
    }

    void PipGrip_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (!_mini) return;
        BeginResizeDrag(WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    // =====================================================================================
    // Player chrome
    // =====================================================================================

    void UpdateChromeText() => UpdateChromeText(_nowLive ? _nowChannel : null);

    void UpdateChromeText(Channel ch)
    {
        if (ch != null)
        {
            var row = _guide.RowFor(ch.Id);
            ChromeTitle.Text = row?.DisplayName ?? ch.Name;
            MiniTitle.Text = row?.DisplayName ?? ch.Name;
            ImageLoader.SetUrl(ChromeLogo, row?.Logo);
            ChromeLogoBox.IsVisible = !string.IsNullOrEmpty(row?.Logo);
            UpdateLiveInfo(ch);
        }
        else
        {
            ChromeTitle.Text = _vod?.Title ?? "";
            MiniTitle.Text = _vod?.Title ?? "";
            ChromeSubtitle.Text = _vod?.Subtitle ?? "";
            ChromeNext.Text = _vod?.NextLabel != null ? "Next   " + _vod.NextLabel : "";
            ChromeLogoBox.IsVisible = false;
            TidyChromeLines();
            UpdateMiniInfo();
        }
    }

    void UpdateLiveInfo(Channel ch)
    {
        var at = DateTime.Now - (_svc?.BehindLive ?? TimeSpan.Zero);
        var row = ch == null ? null : _guide.RowFor(ch.Id);
        var p = row != null ? DataService.Instance.Epg.At(_guide.EpgIdOf(row), at) : _guide.NowOn(ch);
        if (p == null)
        {
            ChromeSubtitle.Text = "No guide information";
            ChromeNext.Text = "";
            ChromeLeft.Text = "";
            LiveProgress.Value = 0;
            LiveTimes.Text = "";
            TidyChromeLines();
            UpdateMiniInfo();
            return;
        }
        ChromeSubtitle.Text = p.Title;
        var next = row != null ? DataService.Instance.Epg.StartingFrom(_guide.EpgIdOf(row), p.End) : _guide.NextOn(ch);
        ChromeNext.Text = next != null ? $"Next   {next.Start:HH:mm}   {next.Title}" : "";
        LiveProgress.Value = (at - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds);
        LiveTimes.Text = $"{p.Start:HH:mm} – {p.End:HH:mm}  ·  {LeftText(p.End - at)}";
        ChromeLeft.Text = ""; // live: the time left is next to the programme's times
        TidyChromeLines();
        UpdateMiniInfo();
    }

    void ShowChrome()
    {
        if (!_playerMode) return;
        if (_panelOpen) { BumpPanelTimer(); return; }
        if (_mini)
        {
            MiniChrome.IsVisible = true;
            UpdateMiniInfo();
            _hideTimer.Stop();
            _hideTimer.Start();
            return;
        }
        Motion.FadeIn(PlayerChrome, ms: 160);
        Overlay.Cursor = Cursor.Default;
        _hideTimer.Stop();
        _hideTimer.Start();
        UpdatePlayerUi();
        UpdateHealthVisibility();
    }

    void HideChrome()
    {
        _hideTimer.Stop();
        if (!_playerMode || _menuOpen || _seeking || _liveSeeking) return;
        if (_mini) { if (!Overlay.IsPointerOver) { MiniChrome.IsVisible = false; SlideMiniVolume(open: false); } return; }
        if (UpNextCard.IsVisible) return;
        Motion.FadeOut(PlayerChrome, ms: 260);
        Overlay.Cursor = new Cursor(StandardCursorType.None);
        UpdateHealthVisibility();
    }

    void UpdatePlayerUi()
    {
        if (!_playerMode || _svc == null) return;
        var mp = _svc.Player;
        var glyph = mp.IsPlaying ? Icons.Pause : Icons.Play;
        PreviewPlayPause.Content = glyph;
        MiniPlayPause.Content = glyph;
        if (_mini && MiniChrome.IsVisible) UpdateMiniInfo();
        if (!PlayerChrome.IsVisible) return;
        PlayPauseButton.Content = glyph;
        UpdateVolumeGlyphs();
        if (_nowLive)
        {
            UpdateLiveInfo(_nowChannel);
            UpdateTimeshiftUi();
            UpdateRecordButton();
            return;
        }
        long len = mp.Length, time = mp.Time;
        if (len > 0 && !_seeking)
        {
            SeekSlider.Maximum = len;
            SeekSlider.Value = Math.Clamp(time, 0, len);
        }
        PosText.Text = Fmt(time);
        LenText.Text = len > 0 ? Fmt(len) : "--:--";
        ChromeLeft.Text = len > 0 ? LeftText(TimeSpan.FromMilliseconds(Math.Max(0, len - time))) : "";
        TidyChromeLines();
    }

    /// <summary>Empty lines in the player's top bar take no space (a film has no "Next", a channel no "time left" line).</summary>
    void TidyChromeLines()
    {
        foreach (var t in new[] { ChromeSubtitle, ChromeNext, ChromeLeft })
            t.IsVisible = !string.IsNullOrEmpty(t.Text);
    }

    /// <summary>"23 min left", "1 h 5 min left", "Ending" – for live programmes, films and episodes.</summary>
    static string LeftText(TimeSpan left)
    {
        if (left.TotalSeconds < 60) return "Ending";
        int mins = (int)Math.Ceiling(left.TotalMinutes);
        return mins >= 60 ? $"{mins / 60} h {mins % 60} min left" : $"{mins} min left";
    }

    /// <summary>The mini player's info: the channel (or title), what's on and how long is left, with a thin progress line.</summary>
    void UpdateMiniInfo()
    {
        if (!_mini || _svc == null) return;
        if (_nowLive && _nowChannel != null)
        {
            var at = DateTime.Now - _svc.BehindLive;
            var row = _guide.RowFor(_nowChannel.Id);
            MiniTitle.Text = row?.DisplayName ?? _nowChannel.Name;
            var p = row != null ? DataService.Instance.Epg.At(_guide.EpgIdOf(row), at) : _guide.NowOn(_nowChannel);
            if (p == null) { MiniInfo.Text = "No guide information"; MiniProgress.IsVisible = false; return; }
            MiniInfo.Text = $"{p.Title}  ·  {LeftText(p.End - at)}";
            MiniProgress.Value = (at - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds);
            MiniProgress.IsVisible = true;
            return;
        }
        var mp = _svc.Player;
        long len = mp.Length, time = mp.Time;
        MiniTitle.Text = _vod?.Title ?? "";
        var sub = _vod?.Subtitle;
        var left = len > 0 ? LeftText(TimeSpan.FromMilliseconds(Math.Max(0, len - time))) : null;
        MiniInfo.Text = string.Join("  ·  ", new[] { sub, left }.Where(x => !string.IsNullOrEmpty(x)));
        MiniProgress.IsVisible = len > 0;
        MiniProgress.Value = len > 0 ? Math.Clamp((double)time / len, 0, 1) : 0;
    }

    static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    void SetPreviewPauseGlyph(bool playing)
    {
        PreviewPlayPause.Content = playing ? Icons.Pause : Icons.Play;
        ToolTip.SetTip(PreviewPlayPause, playing ? "Pause" : "Play");
    }

    void SetBuffering(string text)
    {
        BufferText.Text = text ?? "";
        BufferText.IsVisible = !string.IsNullOrEmpty(text);
    }

    void HookPlayerEvents()
    {
        var mp = _svc.Player;
        mp.Paused += (_, _) => Dispatcher.UIThread.Post(() => SetPreviewPauseGlyph(false));
        mp.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            SetPreviewPauseGlyph(true);
            if (!_reconnecting) SetBuffering(null);
            _lastProgressUtc = DateTime.UtcNow;
            _svc.SetVolume((int)VolumeSlider.Value);
            _svc.SetMute(_muted);
        });
        mp.Buffering += (_, e) =>
        {
            var pct = e.Cache;
            Dispatcher.UIThread.Post(() =>
            {
                if (_reconnecting) return;
                if (pct >= 100) SetBuffering(null);
                else if (_previewActive || _playerMode) SetBuffering($"Buffering {pct:0}%");
            }, DispatcherPriority.Background);
        };
        mp.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => { if (_currentUrl != null) Reconnect(); });
        mp.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            SetBuffering(null);
            if (_currentUrl == null) return;
            if (_nowLive && _svc.Source == LiveSource.Archive) { GoLiveNow(); return; }
            if (_nowLive) Reconnect();
            else OnVodEnded();
        });
    }

    // =====================================================================================
    // Stream health & automatic reconnect
    // =====================================================================================

    void ResetHealth()
    {
        _lastProgressUtc = DateTime.UtcNow;
        _lastShown = 0;
        _lastLost = 0;
        _lastTime = 0;
        _lostWindow.Clear();
        _reconnecting = false;
        _cpuWarned = false;
        _trimmedThisStream = false;
        _streamStartedUtc = DateTime.UtcNow;
        _reconnects = 0;
        HealthText.Text = "Connecting…";
        HealthDot.Fill = Amber;
    }

    static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    async Task HealthTick()
    {
        if (_svc == null || _currentUrl == null) { _healthTimer.Stop(); return; }
        var url = _currentUrl;
        StreamHealth h;
        try { h = await _svc.GetHealthAsync(); }
        catch { return; }
        if (url != _currentUrl) return;

        KeepAwake.Set(h.State is VLCState.Playing or VLCState.Buffering or VLCState.Opening);
        var now = DateTime.UtcNow;
        bool progressed = h.DisplayedPictures != _lastShown || Math.Abs(h.TimeMs - _lastTime) > 200;
        int lostDelta = Math.Max(0, h.LostPictures - _lastLost);
        _lastShown = h.DisplayedPictures;
        _lastLost = h.LostPictures;
        _lastTime = h.TimeMs;
        _lostWindow.Enqueue(lostDelta);
        while (_lostWindow.Count > 10) _lostWindow.Dequeue();
        int dropped = _lostWindow.Sum();

        if (h.State == VLCState.Paused) _lastProgressUtc = now;
        if (progressed)
        {
            _lastProgressUtc = now;
            if (_reconnecting) { _reconnecting = false; SetBuffering(null); }
            if (_reconnects > 0 && now - _lastReconnectUtc > TimeSpan.FromSeconds(30)) _reconnects = 0;
        }

        bool active = h.State is VLCState.Playing or VLCState.Buffering or VLCState.Opening;
        var limit = h.State == VLCState.Opening ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(12);
        if (active && !_reconnecting && now - _lastProgressUtc > limit) { Reconnect(); return; }

        if (!_trimmedThisStream && progressed && h.State == VLCState.Playing && now - _streamStartedUtc > TimeSpan.FromSeconds(15))
        {
            _trimmedThisStream = true;
            Memory.CompactSoon();
        }
        if (_nowLive && !_recentRecorded && now - _liveStartedUtc > TimeSpan.FromSeconds(10) && progressed)
        {
            _recentRecorded = true;
            HistoryStore.AddRecentChannel(_nowChannel?.Id);
        }
        if (_vod != null && h.LengthMs > 0 && h.State == VLCState.Playing)
        {
            _lastVodTime = h.TimeMs;
            _lastVodLen = h.LengthMs;
            if (now - _lastSaveUtc > TimeSpan.FromSeconds(5))
            {
                _lastSaveUtc = now;
                HistoryStore.UpdateProgress(_vod, h.TimeMs, h.LengthMs);
            }
        }

        var parts = new List<string>();
        if (h.Height > 0) parts.Add(ResLabel(h.Width, h.Height));
        if (h.Fps > 1) parts.Add($"{h.Fps:0} fps");
        if (h.Mbps > 0.05) parts.Add($"{h.Mbps:0.0} Mbps");
        parts.Add(dropped == 0 ? "no dropped frames" : $"{dropped} dropped");
        if (h.GpuDecoding == true) parts.Add("GPU decoding");
        else if (h.GpuDecoding == false) parts.Add("CPU decoding");
        parts.Add($"RAM {Memory.WorkingSetMB():N0} MB");
        if (h.GpuDecoding == false && h.Height >= 2000 && !_cpuWarned)
        {
            _cpuWarned = true;
            ShowToast(SettingsStore.Current.HardwareDecoding
                ? "Your graphics card can't decode this 4K stream, so the processor is doing it – that uses a lot of CPU."
                : "Hardware decoding is off in Settings, so 4K is being decoded by the processor.");
        }
        HealthText.Text = _reconnecting ? $"Reconnecting ({_reconnects}/{MaxReconnects})" : string.Join("  ·  ", parts);
        HealthDot.Fill = _reconnecting || (active && now - _lastProgressUtc > TimeSpan.FromSeconds(4)) ? Red
                       : dropped > 5 || h.State == VLCState.Buffering ? Amber : Green;
        UpdateHealthVisibility();
    }

    static string ResLabel(uint w, uint h)
    {
        if (h >= 2000 || w >= 3600) return "4K";
        if (h >= 1400) return "1440p";
        if (h >= 1000 || w >= 1900) return "1080p";
        if (h >= 700 || w >= 1260) return "720p";
        if (h >= 540) return "576p";
        return $"{h}p";
    }

    void UpdateHealthVisibility() =>
        HealthBadge.IsVisible = _playerMode && _currentUrl != null && (_healthPinned || PlayerChrome.IsVisible);

    void Info_Click(object sender, RoutedEventArgs e) => ToggleHealthPinned();

    void ToggleHealthPinned()
    {
        _healthPinned = !_healthPinned;
        SettingsStore.Current.ShowStreamInfo = _healthPinned;
        SettingsStore.Save();
        UpdateHealthVisibility();
        ShowToast(_healthPinned ? "Stream info pinned (press I to hide)" : "Stream info shows with the controls");
    }

    async void Reconnect()
    {
        var url = _currentUrl;
        if (url == null || _svc == null) return;
        if (_nowLive && _reconnects >= 1 && _variantIdx + 1 < _variants.Count)
        {
            var name = _nowChannel?.Name ?? "This channel";
            var next = _variants[_variantIdx + 1];
            _reconnecting = false;
            SwitchVariant(_variantIdx + 1, $"{name}: that version stopped working – switched to {ChannelMerger.Label(next)}.");
            return;
        }
        if (_reconnects >= MaxReconnects)
        {
            _reconnecting = false;
            SetBuffering(null);
            ShowToast(_nowLive
                ? "This channel isn't responding. It may be offline, or your connection limit may be reached."
                : "This stream couldn't be played. Check your connection and try again.");
            HealthDot.Fill = Red;
            HealthText.Text = "Stream lost";
            if (!_nowLive && _playerMode) ExitPlayer();
            else { _svc.Stop(); _currentUrl = null; _healthTimer.Stop(); _reconnects = 0; }
            return;
        }
        _reconnects++;
        _reconnecting = true;
        _lastReconnectUtc = DateTime.UtcNow;
        SetBuffering(_reconnects == 1 ? "Reconnecting…" : $"Reconnecting… (attempt {_reconnects} of {MaxReconnects})");
        int delay = (_reconnects - 1) * 2000;
        _lastProgressUtc = DateTime.UtcNow.AddMilliseconds(delay);
        if (delay > 0) await Task.Delay(delay);
        if (_currentUrl != url) return;
        long resumeAt = _nowLive ? 0 : Math.Max(0, _lastVodTime - 2000);
        if (_nowLive && _svc.Source == LiveSource.Archive) { SeekLiveTo(LivePositionUtc); _lastProgressUtc = DateTime.UtcNow; return; }
        try { _svc.Play(url, _nowLive, resumeAt, _nowLive ? PictureFor(_variants.Count > _variantIdx ? _variants[_variantIdx] : _nowChannel) : null); }
        catch (Exception ex) { Log.Error(ex); }
        _lastProgressUtc = DateTime.UtcNow;
    }

    // =====================================================================================
    // In-player channel list
    // =====================================================================================

    void Channels_Click(object sender, RoutedEventArgs e) => OpenPanel();

    void OpenPanel()
    {
        if (!_playerMode || !_nowLive) return;
        _panelCats = _guide.Categories.Where(c => c.Count > 0).ToList();
        if (_panelCats.Count == 0) return;
        _panelCatIdx = Math.Max(0, _panelCats.FindIndex(c => c.Id == _guide.CurrentCategoryId));
        _panelOpen = true;
        _hideTimer.Stop();
        Motion.HideNow(PlayerChrome);
        Overlay.Cursor = Cursor.Default;
        Motion.FadeIn(ChannelPanel, fromX: -40, ms: 200);
        FillPanel();
        BumpPanelTimer();
    }

    void FillPanel()
    {
        var cat = _panelCats[_panelCatIdx];
        PanelCategory.Text = cat.Name;
        var rows = _guide.RowsIn(cat.Id);
        var epg = DataService.Instance.Epg;
        var now = DateTime.Now;
        var items = new List<ZapItem>(rows.Count);
        foreach (var r in rows)
        {
            var p = epg.At(r.EpgId, now);
            items.Add(new ZapItem
            {
                Row = r,
                Now = p == null ? "No guide information" : p.Title,
                Progress = p == null ? 0 : (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds),
                IsCurrent = r.Channel.Id == _nowChannel?.Id,
            });
        }
        PanelCount.Text = items.Count == 1 ? "1 channel" : $"{items.Count:N0} channels";
        PanelList.ItemsSource = items;
        var sel = items.FirstOrDefault(i => i.IsCurrent) ?? items.FirstOrDefault();
        PanelList.SelectedItem = sel;
        if (sel != null) PanelList.ScrollIntoView(sel);
    }

    void ClosePanel()
    {
        if (!_panelOpen) return;
        _panelOpen = false;
        _panelTimer.Stop();
        Motion.FadeOut(ChannelPanel, toX: -30, ms: 150);
        PanelList.ItemsSource = null;
        if (_playerMode) ShowChrome();
    }

    void BumpPanelTimer() { _panelTimer.Stop(); _panelTimer.Start(); }

    bool HandlePanelKey(Key key)
    {
        BumpPanelTimer();
        switch (key)
        {
            case Key.Up: PanelMove(-1); return true;
            case Key.Down: PanelMove(1); return true;
            case Key.PageUp: PanelMove(-8); return true;
            case Key.PageDown: PanelMove(8); return true;
            case Key.Home: PanelMove(-100000); return true;
            case Key.End: PanelMove(100000); return true;
            case Key.Left: PanelSwitchCategory(-1); return true;
            case Key.Right: PanelSwitchCategory(1); return true;
            case Key.Enter: TunePanelSelection(); return true;
            case Key.Escape:
            case Key.Back:
            case Key.L:
                ClosePanel(); return true;
        }
        return false;
    }

    void PanelMove(int delta)
    {
        int n = PanelList.ItemCount;
        if (n == 0) return;
        int i = PanelList.SelectedIndex < 0 ? 0 : Math.Clamp(PanelList.SelectedIndex + delta, 0, n - 1);
        PanelList.SelectedIndex = i;
        PanelList.ScrollIntoView(i);
    }

    void PanelSwitchCategory(int delta)
    {
        if (_panelCats.Count == 0) return;
        _panelCatIdx = (_panelCatIdx + delta + _panelCats.Count) % _panelCats.Count;
        FillPanel();
    }

    void PanelPrevCat_Click(object sender, RoutedEventArgs e) { PanelSwitchCategory(-1); BumpPanelTimer(); }
    void PanelNextCat_Click(object sender, RoutedEventArgs e) { PanelSwitchCategory(1); BumpPanelTimer(); }

    void TunePanelSelection()
    {
        if (PanelList.SelectedItem is not ZapItem z) return;
        var cat = _panelCats[_panelCatIdx];
        ClosePanel();
        _guide.ShowAndSelect(cat.Id, z.Row);
        PlayLive(z.Row.Channel, false);
        UpdateChromeText(z.Row.Channel);
    }

    void PanelList_Tapped(object sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not ZapItem z) return;
        PanelList.SelectedItem = z;
        TunePanelSelection();
        e.Handled = true;
    }

    // =====================================================================================
    // Films & episodes: progress, up next
    // =====================================================================================

    void SaveVodProgress()
    {
        if (_vod?.ResumeKey == null || _svc == null) return;
        long t = _lastVodTime, len = _lastVodLen;
        try
        {
            var mp = _svc.Player;
            if (mp.Length > 0) { t = mp.Time; len = mp.Length; }
        }
        catch { }
        if (len > 0) HistoryStore.UpdateProgress(_vod, t, len);
    }

    void OnVodEnded()
    {
        if (_vod == null) { ExitPlayer(); return; }
        HistoryStore.UpdateProgress(_vod, _lastVodLen, _lastVodLen, ended: true);
        var next = _vod.Next?.Invoke();
        if (next == null) { _vod = null; ExitPlayer(); return; }
        ShowUpNext(next);
    }

    void ShowUpNext(VodItem next)
    {
        _upNext = next;
        _upNextSeconds = 10;
        UpNextTitle.Text = next.Subtitle;
        UpNextCountdown.Text = $"Up next in {_upNextSeconds}s";
        Motion.FadeIn(UpNextCard, fromX: 30, ms: 220);
        ShowChrome();
        _upNextTimer.Start();
    }

    void UpNextTick()
    {
        if (_upNext == null) { _upNextTimer.Stop(); return; }
        _upNextSeconds--;
        if (_upNextSeconds <= 0) { PlayUpNext(); return; }
        UpNextCountdown.Text = $"Up next in {_upNextSeconds}s";
    }

    void PlayUpNext()
    {
        var next = _upNext;
        HideUpNext();
        if (next != null) PlayVod(next, resume: true);
    }

    void HideUpNext()
    {
        _upNext = null;
        _upNextTimer?.Stop();
        if (UpNextCard != null) UpNextCard.IsVisible = false;
    }

    void UpNextPlay_Click(object sender, RoutedEventArgs e) => PlayUpNext();

    void UpNextCancel_Click(object sender, RoutedEventArgs e)
    {
        HideUpNext();
        _vod = null;
        ExitPlayer();
    }

    void NextEpisode_Click(object sender, RoutedEventArgs e) => PlayNextEpisodeNow();

    void PlayNextEpisodeNow()
    {
        var next = _vod?.Next?.Invoke();
        if (next == null) return;
        SaveVodProgress();
        PlayVod(next, resume: true);
    }

    // =====================================================================================
    // Keyboard & mouse
    // =====================================================================================

    bool Typing
    {
        get
        {
            var f = FocusManager?.GetFocusedElement();
            return f is TextBox;
        }
    }

    /// <summary>Keys in the player arrive at the controls over the video (their own little window).</summary>
    void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_playerMode || _menuOpen) return;
        if (HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key;
        if (_playerMode)
        {
            if (!_menuOpen && HandleKey(key, e.KeyModifiers)) e.Handled = true;
            return;
        }
        if (SplashUp) return; // no shortcuts into the pages hidden behind the splash
        bool typing = Typing;
        if (key == Key.F && e.KeyModifiers == Os.Command) { FocusSearch(); e.Handled = true; return; }
        if (IsSheetKey(key, e) && (!typing || key == Key.F1)) { ToggleShortcutSheet(); e.Handled = true; return; }
        if (SheetOpen && key == Key.Escape) { CloseShortcutSheet(); e.Handled = true; return; }
        if (key == Key.F11 && _nowLive) { EnterPlayer(); e.Handled = true; return; }
        if (key == Key.P && _nowLive && !typing) { EnterMini(); e.Handled = true; return; }
        if (key == Key.V && _page == _guide && !typing) { OpenMultiView(null); e.Handled = true; return; }

        if (_page == _guide && !typing)
        {
            if (DigitOf(key) is int d) { TypeDigit(d); e.Handled = true; return; }
            if (key == Key.Enter && _number.Length > 0) { CommitNumber(); e.Handled = true; return; }
            if (key == Key.Escape && _number.Length > 0) { ClearNumber(); e.Handled = true; }
        }
    }

    static bool IsSheetKey(Key k, KeyEventArgs e = null) => k == Key.F1 || k == Key.OemQuestion;

    bool HandleKey(Key key, KeyModifiers mods)
    {
        if (!_playerMode) return false;
        if (IsSheetKey(key)) { ToggleShortcutSheet(); return true; }
        if (SheetOpen) { CloseShortcutSheet(); if (key == Key.Escape) return true; }
        if (Browsing && HandleBrowseKey(key)) return true;
        if (_panelOpen && HandlePanelKey(key)) return true;
        if (_nowLive && !_mini && (key == Key.Up || key == Key.Down)) { Browse(key == Key.Up ? -1 : 1); return true; }
        ShowChrome();
        if (_nowLive && DigitOf(key) is int d) { TypeDigit(d); return true; }

        switch (key)
        {
            case Key.Escape:
                if (_number.Length > 0) { ClearNumber(); return true; }
                if (_mini) ExitMini(false); else ExitPlayer();
                return true;
            case Key.Back:
                if (_nowLive) { GoToPreviousChannel(); return true; }
                ExitPlayer();
                return true;
            case Key.Enter:
                if (_number.Length > 0) { CommitNumber(); return true; }
                if (_nowLive) { OpenPanel(); return true; }
                break;
            case Key.L:
                if (_nowLive) { OpenPanel(); return true; }
                break;
            case Key.F:
            case Key.F11:
                if (_mini) ExitMini(true); else ExitPlayer();
                return true;
            case Key.P:
                if (_mini) ExitMini(true); else EnterMini();
                return true;
            case Key.I:
                ToggleHealthPinned();
                return true;
            case Key.S:
                ShowSleepMenu();
                return true;
            case Key.R:
                if (_nowLive) { RecordCurrent(); return true; }
                break;
            case Key.V:
                if (_nowLive && !_mini) { OpenMultiView(null); return true; }
                break;
            case Key.N:
                if (!_nowLive) { PlayNextEpisodeNow(); return true; }
                break;
            case Key.Home:
                if (!_nowLive) { _svc?.SeekTo(0); return true; }
                if (LiveSeekable) { StartOfProgramme(); return true; }
                break;
            case Key.Space:
            case Key.MediaPlayPause:
                _svc?.TogglePause(); return true;
            case Key.M:
            case Key.VolumeMute:
                ToggleMute(); return true;
            case Key.Up:
            case Key.PageUp:
            case Key.MediaPreviousTrack:
                if (_nowLive) { Zap(-1); return true; }
                if (key == Key.Up) { VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); return true; }
                if (key == Key.MediaPreviousTrack) { _svc?.SeekBy(-10_000); return true; }
                break;
            case Key.Down:
            case Key.PageDown:
            case Key.MediaNextTrack:
                if (_nowLive) { Zap(1); return true; }
                if (key == Key.Down) { VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); return true; }
                if (key == Key.MediaNextTrack) { if (_vod?.Next != null) PlayNextEpisodeNow(); else _svc?.SeekBy(30_000); return true; }
                break;
            case Key.Left:
                if (!_nowLive) { _svc?.SeekBy(-10_000); return true; }
                if (LiveSeekable) { SeekLiveBy(Os.HasCommand(mods) ? -300 : -10); return true; }
                break;
            case Key.Right:
                if (!_nowLive) { _svc?.SeekBy(30_000); return true; }
                if (LiveSeekable && _svc.BehindLive > TimeSpan.FromSeconds(4)) { SeekLiveBy(Os.HasCommand(mods) ? 300 : 10); return true; }
                break;
            case Key.End:
                if (LiveSeekable) { GoLiveNow(); ShowToast("Back to live"); return true; }
                break;
        }
        return false;
    }

    static int? DigitOf(Key k)
    {
        if (k >= Key.D0 && k <= Key.D9) return k - Key.D0;
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return k - Key.NumPad0;
        return null;
    }

    void TypeDigit(int d)
    {
        if (_number.Length == 0 && d == 0) { GoToPreviousChannel(); return; }
        if (_number.Length >= 4) _number = "";
        _number += d.ToString();
        NumberText.Text = OverlayNumberText.Text = _number;
        NumberBox.IsVisible = !_playerMode;
        OverlayNumberBox.IsVisible = _playerMode;
        _numberTimer.Stop();
        _numberTimer.Start();
    }

    void CommitNumber()
    {
        var n = _number;
        ClearNumber();
        if (n.Length == 0) return;
        var row = _guide.SelectByNumber(n);
        if (row == null) ShowToast($"There's no channel {n}.");
        else if (_playerMode) UpdateChromeText(row.Channel);
    }

    void ClearNumber()
    {
        _number = "";
        _numberTimer?.Stop();
        if (NumberBox != null) NumberBox.IsVisible = false;
        if (OverlayNumberBox != null) OverlayNumberBox.IsVisible = false;
    }

    void GoToPreviousChannel()
    {
        if (_prevChannelId == null) { ShowToast("There's no previous channel yet."); return; }
        var row = _guide.SelectChannel(_prevChannelId);
        if (row == null) { ShowToast("The previous channel isn't in this playlist any more."); return; }
        if (_playerMode) UpdateChromeText(row.Channel);
    }

    void Zap(int delta)
    {
        var row = _guide.Step(delta);
        if (row == null) return;
        UpdateChromeText(row.Channel);
    }

    void ToggleMute()
    {
        _muted = !_muted;
        _svc?.SetMute(_muted);
        UpdateVolumeGlyphs();
        UpdatePlayerUi();
    }

    void Overlay_PointerMoved(object sender, PointerEventArgs e)
    {
        if (!_playerMode) return;
        if (!PlayerChrome.IsVisible) ShowChrome();
        else { _hideTimer.Stop(); _hideTimer.Start(); }
        Overlay.Cursor = Cursor.Default;
    }

    void Overlay_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Overlay).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && (v.FindAncestorOfType<Button>() != null || v.FindAncestorOfType<Slider>() != null || v.FindAncestorOfType<ListBox>() != null)) return;
        if (_mini)
        {
            if (e.ClickCount == 2) ExitMini(true);
            else BeginMoveDrag(e);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            if (!_playerMode) { if (_nowLive) EnterPlayer(); }
            else if (!(e.Source is Visual sv && sv.FindAncestorOfType<Border>() is { } b && (b == ChannelPanel || ChannelPanel.IsVisualAncestorOf(sv)))) ExitPlayer();
            e.Handled = true;
        }
        else if (_playerMode) ShowChrome();
        Overlay.Focus();
    }

    void Overlay_PointerWheelChanged(object sender, PointerWheelEventArgs e)
    {
        if (_mini && Os.HasCommand(e.KeyModifiers))
        {
            double w = Math.Clamp(Width * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), 320, 1280);
            Width = w; Height = w * 9 / 16;
            SettingsStore.Current.MiniWidth = w;
            return;
        }
        if (!_playerMode) return;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta.Y > 0 ? 5 : -5), 0, 100);
        ShowChrome();
    }

    // ----- chrome buttons -----

    void Back_Click(object sender, RoutedEventArgs e) => ExitPlayer();
    void PlayPause_Click(object sender, RoutedEventArgs e) => _svc?.TogglePause();
    void SeekBack_Click(object sender, RoutedEventArgs e) { if (_nowLive) SeekLiveBy(-10); else _svc?.SeekBy(-10_000); }
    void SeekFwd_Click(object sender, RoutedEventArgs e) { if (_nowLive) { if (_svc.BehindLive > TimeSpan.FromSeconds(4)) SeekLiveBy(10); } else _svc?.SeekBy(30_000); }
    void ChUp_Click(object sender, RoutedEventArgs e) => Zap(-1);
    void ChDown_Click(object sender, RoutedEventArgs e) => Zap(1);
    void LastCh_Click(object sender, RoutedEventArgs e) => GoToPreviousChannel();
    void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    void Fullscreen_Click(object sender, RoutedEventArgs e) => ExitPlayer();

    void PreviewFull_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_currentUrl == null || _playerMode || !_nowLive) return;
        EnterPlayer();
    }

    void Volume_Changed(double value)
    {
        var v = (int)value;
        SettingsStore.Current.Volume = v;
        if (v > 0 && _muted) { _muted = false; _svc?.SetMute(false); }
        _svc?.SetVolume(v);
        if (MiniVolumeSlider != null && Math.Abs(MiniVolumeSlider.Value - v) > 0.5) MiniVolumeSlider.Value = v;
        UpdateVolumeGlyphs();
    }

    /// <summary>The speaker icon on the player's and the mini player's mute buttons (crossed out when muted or at 0).</summary>
    void UpdateVolumeGlyphs()
    {
        var g = _muted || VolumeSlider.Value <= 0 ? Icons.VolumeOff : Icons.Volume;
        if (MuteButton != null) MuteButton.Content = g;
        if (MiniMute != null) MiniMute.Content = g;
    }

    void SlideMiniVolume(bool open) => MiniVolumeHost.Width = open ? 96 : 0;

    void Audio_Click(object sender, RoutedEventArgs e) =>
        ShowTrackMenu(AudioButton, _svc?.AudioTracks(), id => _svc.SetAudioTrack(id), "No other audio tracks");

    void Subs_Click(object sender, RoutedEventArgs e) =>
        ShowTrackMenu(SubsButton, _svc?.SubtitleTracks(), id => _svc.SetSubtitle(id), "No subtitles in this stream");

    void ShowTrackMenu(Button anchor, List<TrackOption> tracks, Action<int> select, string emptyText)
    {
        if (tracks == null) return;
        var menu = NewMenu();
        if (tracks.Count == 0) menu.Items.Add(new MenuItem { Header = emptyText, IsEnabled = false });
        foreach (var t in tracks)
        {
            var id = t.Id;
            Add(menu, t.Name, () => select(id), t.Current);
        }
        OpenMenu(menu, anchor);
    }

    // ----- menus in the player -----

    static ContextMenu NewMenu() => new() { Placement = PlacementMode.TopEdgeAlignedRight };

    static MenuItem Add(ItemsControl parent, string text, Action click, bool? isChecked = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = text, IsEnabled = enabled };
        if (isChecked != null) { mi.ToggleType = MenuItemToggleType.Radio; mi.IsChecked = isChecked.Value; }
        if (click != null) mi.Click += (_, _) => click();
        parent.Items.Add(mi);
        return mi;
    }

    void OpenMenu(ContextMenu menu, Control anchor)
    {
        _hideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; if (_playerMode) _hideTimer.Start(); };
        _menuOpen = true;
        menu.Open(anchor);
    }

    // =====================================================================================
    // Keyboard shortcut sheet
    // =====================================================================================

    static readonly (string Keys, string What)[] Shortcuts =
    {
        ("0 – 9", "Type a channel number"),
        ("0 / Backspace", "Last channel you watched"),
        ("↑  ↓", "Live TV: what's on other channels (Enter to watch) · films: volume"),
        ("PgUp / PgDn", "Channel up / down"),
        ("←  →", "Back / forward (live TV 10 s, films 10 s / 30 s)"),
        ("End  /  Home", "Live TV: back to live / start of the programme"),
        ("Ctrl + ← →", "Live TV: back / forward 5 minutes"),
        ("Enter / L", "Channel list – what's on now"),
        ("Space", "Play / pause"),
        ("M", "Mute  ·  mouse wheel = volume"),
        ("F / F11", "Full screen on / off"),
        ("Esc", "Back to Live TV"),
        ("P", "Picture-in-picture"),
        ("V", "Multi-view (4 / 6 / 8 channels)"),
        ("I", "Stream info"),
        ("S", "Sleep timer"),
        ("R", "Record this programme"),
        ("N", "Next episode"),
        ("Home", "Films: start from the beginning"),
        ("Ctrl + F", "Search everything"),
        ("? / F1", "This list"),
    };

    bool SheetOpen => ShortcutSheet.IsVisible || OverlaySheet.IsVisible;

    void ToggleShortcutSheet()
    {
        if (SheetOpen) { CloseShortcutSheet(); return; }
        var (sheet, grid) = _playerMode ? (OverlaySheet, OverlaySheetGrid) : (ShortcutSheet, ShortcutSheetGrid);
        if (grid.Children.Count == 0)
            foreach (var (k, what) in Shortcuts)
            {
                var keys = Os.Mac ? k.Replace("Ctrl", "⌘") : k;
                var row = new DockPanel { Margin = new Thickness(0, 5, 28, 5) };
                var key = new Border
                {
                    MinWidth = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 2, 8, 3),
                        Child = new TextBlock { Text = keys, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeight.SemiBold },
                    },
                };
                DockPanel.SetDock(key, Dock.Left);
                row.Children.Add(key);
                row.Children.Add(new TextBlock
                {
                    Text = what, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                });
                grid.Children.Add(row);
            }
        Motion.FadeIn(sheet, fromY: 12, ms: 200);
    }

    void CloseShortcutSheet()
    {
        Motion.FadeOut(ShortcutSheet, toY: 8);
        Motion.FadeOut(OverlaySheet, toY: 8);
    }

    void Sheet_PointerPressed(object sender, PointerPressedEventArgs e) { CloseShortcutSheet(); e.Handled = true; }

    // =====================================================================================
    // Sleep timer
    // =====================================================================================

    DispatcherTimer _sleepTimer;
    DateTime? _sleepAt;
    bool _sleepWarned;

    void Sleep_Click(object sender, RoutedEventArgs e) => ShowSleepMenu();

    void ShowSleepMenu()
    {
        var menu = NewMenu();
        if (_sleepAt is DateTime at) Add(menu, $"Stops at {at:HH:mm} ({Remaining(at)})", null, enabled: false);
        Add(menu, "Off", () => SetSleep(null), _sleepAt == null);
        foreach (var m in new[] { 15, 30, 60, 90 }) { var mm = m; Add(menu, $"{m} minutes", () => SetSleep(DateTime.Now.AddMinutes(mm)), false); }
        if (_nowLive && _nowChannel != null && _guide.NowOn(_nowChannel) is { } p && p.End > DateTime.Now)
            Add(menu, $"End of this programme ({p.End:HH:mm})", () => SetSleep(p.End.AddMinutes(1)), false);
        else if (!_nowLive && _svc?.Player is { Length: > 0 } mp)
        {
            var left = mp.Length - mp.Time;
            if (left > 60_000) Add(menu, "End of this film / episode", () => SetSleep(DateTime.Now.AddMilliseconds(left + 15_000)), false);
        }
        OpenMenu(menu, SleepButton);
    }

    void SetSleep(DateTime? at)
    {
        _sleepAt = at;
        _sleepWarned = false;
        if (at == null) { _sleepTimer?.Stop(); ShowToast("Sleep timer off"); }
        else
        {
            if (_sleepTimer == null)
            {
                _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _sleepTimer.Tick += (_, _) => SleepTick();
            }
            _sleepTimer.Start();
            ShowToast($"Sleep timer: stopping at {at:HH:mm} ({Remaining(at.Value)})");
        }
        UpdateSleepButton();
    }

    void SleepTick()
    {
        if (_sleepAt is not DateTime at) { _sleepTimer.Stop(); return; }
        if (_currentUrl == null) { SetSleepOffQuietly(); return; }
        var left = at - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            SetSleepOffQuietly();
            if (_playerMode) ExitPlayer();
            StopPlayback();
            ShowToast("Sleep timer – playback stopped. Goodnight!");
            return;
        }
        if (!_sleepWarned && left <= TimeSpan.FromMinutes(1))
        {
            _sleepWarned = true;
            ShowToast("Sleep timer: stopping in 1 minute  ·  press S to change it");
        }
        UpdateSleepButton();
    }

    void SetSleepOffQuietly()
    {
        _sleepAt = null;
        _sleepTimer?.Stop();
        UpdateSleepButton();
    }

    void UpdateSleepButton()
    {
        bool on = _sleepAt != null;
        SleepButton.Foreground = on ? Res("AccentBrush") : Brushes.White;
        ToolTip.SetTip(SleepButton, on ? $"Sleep timer – stops at {_sleepAt:HH:mm} ({Remaining(_sleepAt.Value)}) (S)" : "Sleep timer (S)");
    }

    static string Remaining(DateTime at)
    {
        var m = (int)Math.Ceiling((at - DateTime.Now).TotalMinutes);
        return m <= 1 ? "1 min left" : m < 60 ? $"{m} min left" : $"{m / 60} h {m % 60:00} min left";
    }

    public static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;
}
