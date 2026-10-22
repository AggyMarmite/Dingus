using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;
using Dingus.Views;
using VLCState = LibVLCSharp.Shared.VLCState;

namespace Dingus;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; }

    record WinState(Rect Bounds, WindowState State, WindowStyle Style, ResizeMode Resize, double MinW, double MinH);

    const double MiniWidth = 480, MiniHeight = 270;
    const int MaxReconnects = 3;

    PlayerService _svc;
    readonly MediaKeys _mediaKeys;
    readonly GuideView _guide;
    readonly HomeView _home;
    readonly LibraryView _films;
    readonly LibraryView _shows;
    readonly SettingsView _settings;
    readonly SearchView _search;
    readonly RecordingsView _recordings;
    IPage _page;

    // ----- what's playing
    bool _playerMode, _osFullscreen, _mini, _previewActive, _nowLive, _seeking, _muted, _menuOpen;
    Channel _nowChannel;
    string _prevChannelId;
    VodItem _vod;
    string _currentUrl;
    DateTime _liveStartedUtc;
    bool _recentRecorded;

    // ----- layout
    Rect _lastSlot = Rect.Empty;
    Rect _normalBounds = Rect.Empty;
    WindowState _prevState;
    WindowStyle _prevStyle;
    ResizeMode _prevResize;
    WinState _miniSaved;

    // ----- stream health / reconnect
    DateTime _lastProgressUtc, _lastReconnectUtc, _lastSaveUtc;
    int _lastShown, _lastLost, _reconnects;
    long _lastTime, _lastVodTime, _lastVodLen;
    readonly Queue<int> _lostWindow = new();
    bool _healthPinned, _reconnecting, _cpuWarned, _rateMatched, _trimmedThisStream;
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

    readonly DispatcherTimer _uiTimer, _hideTimer, _toastTimer, _searchTimer, _healthTimer, _numberTimer, _upNextTimer;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        RestorePlacement();
        SetupTaskbar();

        _guide = new GuideView();
        _guide.CanPreview = () => _svc != null && !LoadingOverlayShown && !SplashUp;
        _home = new HomeView { Guide = _guide };
        _home.CanPreview = () => _svc != null && !LoadingOverlayShown && !SplashUp;
        _films = new LibraryView(LibraryMode.Movies);
        _shows = new LibraryView(LibraryMode.Series);
        _settings = new SettingsView();
        _search = new SearchView { Guide = _guide };
        _recordings = new RecordingsView { Guide = _guide };
        _guide.PreviewRequested += row => PlayLive(row.Channel, false);
        _mediaKeys = new MediaKeys(this, HandleMediaKey);
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
        Loaded += (_, _) => UpdateLockButton();
        // Look for an update a little after start-up – never in the middle of full-screen viewing.
        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += async (_, _) =>
        {
            if (_playerMode || OwnedWindows.Count > 0) { updateTimer.Interval = TimeSpan.FromMinutes(10); return; }
            updateTimer.Stop();
            await Updater.CheckAsync(this, manual: false);
        };
        updateTimer.Start();
        Video.SizeChanged += (_, _) =>
        {
            UpdateFillRatio();
            if (SettingsStore.Current.Crop == "fill") _svc?.ApplyPicture();
        };
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

        var ds = DataService.Instance;
        ds.BusyChanged += OnBusyChanged;
        ds.Message += ShowToast;
        ds.CatalogChanged += () => { if (_nowLive && _nowChannel != null && !ds.Catalog.Channels.Any(c => c.Id == _nowChannel.Id)) StopPlayback(); };

        SourceInitialized += (_, _) =>
        {
            NativeWindow.ApplyDarkTitleBar(this);
            NativeWindow.DisableAnimations(this);
            NativeWindow.RoundCorners(this, true);
        };
        StateChanged += (_, _) => { UpdateWindowFrame(); DropTopmostSoon(); };
        PowerService.Changed += OnPowerChanged;
        // Full screen stays above the taskbar only while Dingus is in front, so Alt-Tab works normally.
        Activated += (_, _) => { if (_osFullscreen || _mini) Topmost = true; };
        // Switching to another app (from any Dingus window – the player controls are a separate little window
        // over the video): full screen stops sitting on top. Picture-in-picture stays on top, as it should.
        Application.Current.Deactivated += (_, _) =>
        {
            if (_osFullscreen && !_mini) Topmost = false;
            if (!_osFullscreen && !_mini) EnsureNotOnTop(); // switching to another app: never stay in front of it
        };
        Application.Current.Activated += (_, _) => { if (_osFullscreen || _mini) Topmost = true; };
        Deactivated += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!_osFullscreen || _mini) return;
            bool ours = Application.Current.Windows.OfType<Window>().Any(w => w.IsActive); // e.g. the controls over the video
            if (!ours) Topmost = false;
        }, DispatcherPriority.Background);
        Loaded += OnLoaded;
        Closing += (_, e) =>
        {
            if (RecordingService.ActiveCount > 0 && SelfTest.Dir == null &&
                MessageBox.Show(this, "A recording is in progress. If you close Dingus it stops (what's recorded so far is kept).\n\nClose anyway?",
                    "Dingus", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            RecordingService.StopAll();
            SaveVodProgress();
            SavePlacement();
            SettingsStore.Save();
            HistoryStore.SaveNow();
            // Stop VLC off the UI thread before the video surface is torn down – closing mid-stream
            // can otherwise freeze the app on exit.
            if (_svc != null) Task.Run(() => _svc.Player.Stop()).Wait(TimeSpan.FromSeconds(2));
            DisplayRate.Restore();
        };
        LocationChanged += (_, _) => TrackNormalBounds();
        SizeChanged += (_, _) => TrackNormalBounds();
        PreviewKeyDown += OnPreviewKeyDown;
        // In the player, keyboard focus can sit inside the video surface (a separate native window)
        // where WPF key events never fire – so read key presses for the whole app here instead.
        System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
        Closed += (_, _) => System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage -= OnThreadMessage;
        Root.LayoutUpdated += (_, _) => { if (_previewActive && !_playerMode) PositionPreview(); };

        VolumeSlider.Value = SettingsStore.Current.Volume;
        MiniVolumeSlider.Value = VolumeSlider.Value;
        (SettingsStore.Current.StartOnGuide ? NavGuide : NavHome).IsChecked = true;
        OnPlaylistChanged();

        _startupLoading = SettingsStore.Current.Playlists.Count > 0;
        UpdateLoadingOverlay(null);

        // The splash goes once your saved playlist is on Home (the fresh copy carries on downloading in the
        // background) – or after a few seconds whatever happens.
        DataService.Instance.CatalogChanged += () =>
        {
            UpdateLoadingOverlay(_busyStep);
            var c = DataService.Instance.Catalog;
            if (c.Channels.Count + c.Movies.Count + c.Series.Count > 0)
                Dispatcher.InvokeAsync(() => HideSplash(), DispatcherPriority.Background);
        };
        var splashLimit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        splashLimit.Tick += (_, _) => { splashLimit.Stop(); HideSplash(); };
        splashLimit.Start();
        UpdateWelcome();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            PowerService.ApplyGpuPreference(); // before the video engine starts, so it opens on the right graphics chip
            _svc = await Task.Run(() => new PlayerService());
            Video.MediaPlayer = _svc.Player;
            HookPlayerEvents();
            _guide.TryAutoPreview(); // start the last-watched channel if the guide is already loaded
            _home.TryStartPreview();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (SelfTest.Dir != null) { SelfTest.Error(ex); return; }
            MessageBox.Show(this, "The video engine failed to start:\n\n" + ex.Message, "Dingus", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // If a playlist was added while the video engine was starting, it's already loading –
        // starting a second load here used to throw the first one's results away.
        if (SettingsStore.Current.Playlists.Count > 0 && !DataService.Instance.HasStartedLoading)
        {
            await DataService.Instance.LoadActiveAsync(refreshNow: true);
            // Normally the loading screen closes when the playlist download finishes; this covers the
            // case where nothing needed downloading (auto refresh off).
            HideStartupLoadingIfIdle();
        }
    }

    void HideStartupLoadingIfIdle()
    {
        if (!_startupLoading) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (!_startupLoading || DataService.Instance.IsBusy) return;
            _startupLoading = false;
            UpdateLoadingOverlay(null);
        }, DispatcherPriority.Background);
    }

    /// <summary>Plugged in / unplugged: switch graphics chip and quality, and reopen what's playing so it takes effect.</summary>
    void OnPowerChanged()
    {
        if (!SettingsStore.Current.BatteryAware) return;
        PowerService.ApplyGpuPreference();
        bool battery = PowerService.OnBattery;
        if (_currentUrl == null)
        {
            ShowToast(battery ? "On battery – Dingus will use built-in graphics." : "Plugged in – Dingus will use your graphics card.");
            return;
        }
        ShowToast(battery
            ? "On battery – switching to built-in graphics (4K channels drop to 1080p where available)."
            : "Plugged in – switching to your graphics card.");
        if (_nowLive && _nowChannel != null)
        {
            var row = _guide.RowFor(_nowChannel.Id);
            if (row != null) _variants = ChannelMerger.Order(row.Variants, ChannelMerger.CurrentMaxRank());
            _variantIdx = 0;
            PlayVariant();
            ConfigureChrome();
        }
        else if (_vod != null && _vod.ResumeKey != null)
        {
            SaveVodProgress();
            PlayVod(_vod, resume: true);
        }
    }

    // =====================================================================================
    // Multi-view
    // =====================================================================================

    void MultiView_Click(object sender, RoutedEventArgs e) => OpenMultiView(null);

    /// <summary>Opens Multi-view, starting with <paramref name="first"/> (or what's playing) in the first screen.</summary>
    public void OpenMultiView(GuideRow first)
    {
        if (_svc == null) return;
        if (_nowLive && _nowChannel != null) first = _guide.RowFor(_nowChannel.Id) ?? first;
        StopPlayback(); // frees the connection the main player was using
        var mv = new MultiViewWindow(_svc.Vlc, _guide, first) { Owner = this };
        mv.Closed += async (_, _) =>
        {
            Activate();
            if (mv.OpenFullScreen && mv.FocusedRow != null)
            {
                await Task.Delay(800); // let the tiles hang up first, so we don't go over the connection limit
                PlayLive(mv.FocusedRow.Channel, true);
            }
        };
        mv.Show();
    }

    /// <summary>Opens the guide on a channel (and optionally shows one of its programmes).</summary>
    public void ShowInGuide(GuideRow row, Programme p)
    {
        NavGuide.IsChecked = true;
        if (p != null) _guide.ShowProgramme(row, p);
        else _guide.SelectChannel(row.Channel.Id);
    }

    /// <summary>Opens a film or show's details page.</summary>
    /// <summary>The Dingus logo at the top left goes Home.</summary>
    void HomeLogo_Click(object sender, RoutedEventArgs e)
    {
        NavHome.IsChecked = true;
    }

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

    // =====================================================================================
    // Taskbar controls (buttons under the taskbar thumbnail preview)
    // =====================================================================================

    System.Windows.Shell.ThumbButtonInfo _tbPrev, _tbPlay, _tbNext;
    System.Windows.Media.ImageSource _icoPrev, _icoNext, _icoPlay, _icoPause;

    /// <summary>Called when Dingus is opened again while it's already running.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        if (!Topmost) { Topmost = true; Topmost = false; } // nudge it above other windows
        Focus();
    }

    void SetupTaskbar()
    {
        _icoPrev = GlyphImage("\uE892");
        _icoNext = GlyphImage("\uE893");
        _icoPlay = GlyphImage("\uE768");
        _icoPause = GlyphImage("\uE769");
        _tbPrev = new System.Windows.Shell.ThumbButtonInfo { ImageSource = _icoPrev, Description = "Previous channel", Visibility = Visibility.Collapsed };
        _tbPlay = new System.Windows.Shell.ThumbButtonInfo { ImageSource = _icoPause, Description = "Pause", Visibility = Visibility.Collapsed };
        _tbNext = new System.Windows.Shell.ThumbButtonInfo { ImageSource = _icoNext, Description = "Next channel", Visibility = Visibility.Collapsed };
        _tbPrev.Click += (_, _) => TaskbarStep(-1);
        _tbNext.Click += (_, _) => TaskbarStep(1);
        _tbPlay.Click += (_, _) => { _svc?.TogglePause(); };
        var info = new System.Windows.Shell.TaskbarItemInfo();
        info.ThumbButtonInfos.Add(_tbPrev);
        info.ThumbButtonInfos.Add(_tbPlay);
        info.ThumbButtonInfos.Add(_tbNext);
        TaskbarItemInfo = info;
    }

    void TaskbarStep(int dir)
    {
        if (_currentUrl == null) return;
        if (_nowLive)
        {
            if (_playerMode) Zap(dir);
            else _guide.Step(dir); // the guide preview follows the selection
        }
        else _svc?.SeekBy(dir > 0 ? 30_000 : -10_000);
    }

    /// <summary>Shows/hides the thumbnail buttons and the taskbar progress bar to match what's playing.</summary>
    void UpdateTaskbar(StreamHealth h = null)
    {
        if (TaskbarItemInfo == null || _tbPlay == null) return;
        bool playing = _currentUrl != null && _svc != null;
        var vis = playing ? Visibility.Visible : Visibility.Collapsed;
        _tbPrev.Visibility = _tbPlay.Visibility = _tbNext.Visibility = vis;
        if (!playing)
        {
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
            TaskbarItemInfo.Description = "Dingus";
            return;
        }
        bool isPlaying = h != null ? h.State == VLCState.Playing : _svc.Player.IsPlaying;
        _tbPlay.ImageSource = isPlaying ? _icoPause : _icoPlay;
        _tbPlay.Description = isPlaying ? "Pause" : "Play";
        _tbPrev.Description = _nowLive ? "Previous channel" : "Back 10 seconds";
        _tbNext.Description = _nowLive ? "Next channel" : "Forward 30 seconds";
        TaskbarItemInfo.Description = _nowLive ? _nowChannel?.Name ?? "Dingus" : _vod?.Title ?? "Dingus";

        if (!_nowLive && h != null && h.LengthMs > 0)
        {
            TaskbarItemInfo.ProgressState = isPlaying ? System.Windows.Shell.TaskbarItemProgressState.Normal
                                                      : System.Windows.Shell.TaskbarItemProgressState.Paused;
            TaskbarItemInfo.ProgressValue = Math.Clamp((double)h.TimeMs / h.LengthMs, 0, 1);
        }
        else TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
    }

    /// <summary>Draws an icon-font glyph as a small white image for the taskbar buttons.</summary>
    static System.Windows.Media.ImageSource GlyphImage(string glyph)
    {
        var tf = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new System.Windows.Media.FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, tf, 16, System.Windows.Media.Brushes.White, 1.0);
        var geo = ft.BuildGeometry(new Point(0, 0));
        var drawing = new System.Windows.Media.DrawingGroup();
        // Fixed 16×16 box so every icon is centred the same way.
        drawing.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.Transparent, null,
            new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.White, null, geo));
        var img = new System.Windows.Media.DrawingImage(drawing);
        img.Freeze();
        return img;
    }

    // =====================================================================================
    // Window size & position
    // =====================================================================================

    /// <summary>
    /// Opens as a window (never maximised) where you left it, or centred at about 80% of the screen, always inside
    /// the work area so the title bar and edges are never hidden behind the top of the screen or the taskbar.
    /// </summary>
    void RestorePlacement()
    {
        var wa = SystemParameters.WorkArea;
        MinWidth = Math.Min(960, wa.Width - 40);
        MinHeight = Math.Min(560, wa.Height - 40);

        // Dingus opens as a window, not filling the screen: about 80% of it, centred – or the size you last left it,
        // if that's smaller. (Maximise it with the button or by double-clicking the title bar whenever you like.)
        double maxW = Math.Max(MinWidth, Math.Round(wa.Width * 0.82)), maxH = Math.Max(MinHeight, Math.Round(wa.Height * 0.82));
        double defW = Math.Max(MinWidth, Math.Min(1440, Math.Round(wa.Width * 0.8)));
        double defH = Math.Max(MinHeight, Math.Min(900, Math.Round(wa.Height * 0.8)));

        var s = SettingsStore.Current;
        if (s.WindowLeft is double l && s.WindowTop is double t && s.WindowWidth is double w && s.WindowHeight is double h
            && w <= maxW && h <= maxH)
        {
            var r = new Rect(l, t, Math.Max(MinWidth, w), Math.Max(MinHeight, h));
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            // Keep it only if the title bar is on a screen (e.g. a monitor wasn't unplugged).
            if (screen.Contains(new Point(r.Left + 60, r.Top + 10)) && screen.Contains(new Point(r.Right - 60, r.Top + 10)))
            {
                Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
                _normalBounds = r;
                return;
            }
        }

        Width = defW;
        Height = defH;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
        _normalBounds = new Rect(Left, Top, Width, Height);
    }

    void TrackNormalBounds()
    {
        if (_mini || _osFullscreen || WindowState != WindowState.Normal || WindowStyle == WindowStyle.None) return;
        if (ActualWidth < 100 || ActualHeight < 100) return;
        _normalBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
    }

    void SavePlacement()
    {
        var s = SettingsStore.Current;
        var b = _mini ? _miniSaved.Bounds : _normalBounds;
        if (!b.IsEmpty)
        {
            s.WindowLeft = b.Left; s.WindowTop = b.Top; s.WindowWidth = b.Width; s.WindowHeight = b.Height;
        }
        var state = _mini ? _miniSaved.State : _osFullscreen ? _prevState : WindowState;
        s.WindowMaximized = state == WindowState.Maximized;
        if (_mini) { s.MiniLeft = Left; s.MiniTop = Top; }
    }

    // =====================================================================================
    // Navigation & shell
    // =====================================================================================

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_guide == null) return; // during InitializeComponent
        IPage page = _guide;
        if (sender == NavHome) page = _home;
        else if (sender == NavFilms) page = _films;
        else if (sender == NavShows) page = _shows;
        else if (sender == NavSettings) page = _settings;
        else if (sender == NavSearch) page = _search;
        else if (sender == NavRecordings) page = _recordings;
        Navigate(page);
    }

    void Navigate(IPage page)
    {
        if (_page == page) return;
        bool previewPage(IPage pg) => pg == _guide;
        if (_previewActive && !_playerMode && previewPage(_page) && !previewPage(page)) StopPlayback();
        _lastSlot = Rect.Empty;

        _page?.OnHidden();
        _page = page;
        PageHost.Content = page;
        // A gentle fade as the new page appears (no slide – keeps big pages cheap to draw). It starts once the page
        // has built and laid itself out, so the whole fade is seen instead of being swallowed by that work.
        PageHost.BeginAnimation(OpacityProperty, null);
        PageHost.Opacity = 0;
        var fadeFor = page;
        Dispatcher.InvokeAsync(() =>
        {
            if (_page != fadeFor) return;
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }, DispatcherPriority.Loaded);
        PageTitle.Text = page.Title;
        // One search bar for the whole app: whatever page you're on, typing searches everything.
        SearchHost.Visibility = Visibility.Visible;
        SearchBox.Tag = GlobalSearchHint;
        _searchTimer.Stop();
        if (page != _search && SearchBox.Text.Length > 0) SearchBox.Text = ""; // leaving Search clears it
        _searchTimer.Stop();
        page.ApplySearch(page == _search ? SearchBox.Text.Trim() : "");
        page.OnShown();
        UpdateWelcome();
        // The loading screen covers every page except Settings – re-check it for the page you've just opened.
        bool overlay = UpdateLoadingOverlay(_busyStep);
        bool busy = DataService.Instance.IsBusy;
        BusyPanel.Visibility = busy && !overlay ? Visibility.Visible : Visibility.Collapsed;
        Spin(BusyRotate, busy && !overlay);
    }

    /// <summary>No playlist yet: the splash stays up with "Add playlist" (Settings can still be opened over it).</summary>
    void UpdateWelcome()
    {
        Welcome.Visibility = Visibility.Collapsed; // the splash does this job now
        if (Splash == null) return;
        bool none = SettingsStore.Current.Playlists.Count == 0;
        if (none && _page != _settings) ShowSplash(withAdd: true);
        else if (none || SplashAdd.Visibility == Visibility.Visible) HideSplash(now: true); // Settings opened, or a playlist was just added
    }

    // ------------------------------------------------------------------ splash

    readonly DateTime _splashShownAt = DateTime.UtcNow;
    bool _splashGone;
    /// <summary>The splash is up – the video (drawn on its own surface, above everything) has to stay hidden meanwhile.</summary>
    bool SplashUp => Splash != null && Splash.Visibility == Visibility.Visible && !_splashGone;

    void ShowSplash(bool withAdd)
    {
        SplashAdd.Visibility = withAdd ? Visibility.Visible : Visibility.Collapsed;
        if (Splash.Visibility == Visibility.Visible && !_splashGone) return;
        _splashGone = false;
        Splash.BeginAnimation(OpacityProperty, null);
        Splash.Opacity = 1;
        SplashScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        SplashScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        SplashScale.ScaleX = SplashScale.ScaleY = 1;
        Splash.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Fades the splash away (after it's been up for at least a moment, so it doesn't just flicker) – the logo grows
    /// a touch as it goes.
    /// </summary>
    void HideSplash(bool now = false)
    {
        if (_splashGone || Splash.Visibility != Visibility.Visible) return;
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
        SplashAdd.Visibility = Visibility.Collapsed;
        var ms = TimeSpan.FromMilliseconds(320);
        var fade = new DoubleAnimation(1, 0, ms) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (!_splashGone) return;
            Splash.Visibility = Visibility.Collapsed;
            // Now the last channel can start in Live TV's mini player (it waited for the splash).
            _guide.TryAutoPreview();
            _home?.TryStartPreview();
        };
        Splash.BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation(1, 1.05, ms) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        SplashScale.CenterX = SplashContent.ActualWidth / 2;
        SplashScale.CenterY = SplashContent.ActualHeight / 2;
        SplashScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        SplashScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
    }

    public void ClearSearch()
    {
        ClearSearchBoxOnly();
        _page?.ApplySearch("");
    }

    public void SetSearchHint(string hint) => SearchBox.Tag = hint;

    const string GlobalSearchHint = "Search channels, programmes, films and shows";
    IPage _beforeSearch;

    /// <summary>Ctrl+F: the search bar.</summary>
    void SearchShortcut() => FocusSearch();

    /// <summary>Typing in the search bar on any page opens the Search page with the results.</summary>
    void RunGlobalSearch()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length > 0 && _page != _search)
        {
            _beforeSearch = _page;
            NavSearch.IsChecked = true; // Navigate keeps the text for the Search page
            FocusSearchCaretEnd();
            return;
        }
        if (_page == _search) _search.ApplySearch(q);
    }

    void FocusSearchCaretEnd()
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>Esc in the search bar: clear it, and go back to where you were.</summary>
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
        new Views.ChannelEditorWindow(_guide, GuideView.AllId) { Owner = this }.ShowDialog();
        _guide.ApplyChannelEdits();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Empties the search box without re-filtering the current page.</summary>
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

    /// <summary>Called whenever the active playlist (or the list of playlists) changes.</summary>
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
        var menu = new ContextMenu { PlacementTarget = PlaylistButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var active = DataService.Instance.Active;
        foreach (var p in SettingsStore.Current.Playlists)
        {
            var item = new MenuItem { Header = p.Name, IsChecked = p == active };
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
        var manage = new MenuItem { Header = "Manage playlists…" };
        manage.Click += (_, _) => NavSettings.IsChecked = true;
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await DataService.Instance.RefreshAsync();

    async void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PlaylistDialog(null) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SettingsStore.Current.Playlists.Add(dlg.Result);
        SettingsStore.Current.ActivePlaylistId = dlg.Result.Id;
        SettingsStore.Save();
        OnPlaylistChanged();
        await DataService.Instance.LoadActiveAsync(refreshNow: true);
    }

    // Loading screen: shown from the moment the app opens (and on first sign-in) until the playlist
    // has loaded. Refreshes you start yourself show the "Refreshing playlist" pill instead.
    bool _startupLoading, _loadingDismissed;

    void OnBusyChanged(bool busy, string text)
    {
        var ds = DataService.Instance;
        if (!busy || ds.BusyIsGuide) _startupLoading = false; // playlist done (the guide carries on in the pill)
        if (busy && ds.IsFirstLoad && !ds.BusyIsGuide && !_startupLoading && !LoadingOverlayShown) _loadingDismissed = false;
        _busyStep = busy ? text : null;
        bool overlay = UpdateLoadingOverlay(text);

        BusyPanel.Visibility = busy && !overlay ? Visibility.Visible : Visibility.Collapsed;
        BusyTitle.Text = ds.BusyIsGuide ? "Updating TV guide" : ds.IsFirstLoad ? "Loading playlist" : "Refreshing playlist";
        BusyText.Text = text == null ? "" : StepText(text);
        RefreshButton.IsEnabled = !busy && ds.Active != null;
        Spin(BusyRotate, busy && !overlay);
    }

    bool LoadingOverlayShown => LoadingOverlay.Visibility == Visibility.Visible;
    string _busyStep;

    /// <summary>Settings stay usable while a playlist loads: the loading screen steps aside for it (the pill shows progress).</summary>
    void LoadingSettings_Click(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

    /// <returns>True if the loading screen is showing.</returns>
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
        if (show && playlistWork && Splash != null) Dispatcher.InvokeAsync(() => HideSplash(), DispatcherPriority.Background);
        bool was = LoadingOverlayShown;

        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            bool hasSaved = ds.Catalog.Channels.Count > 0 || ds.Catalog.Movies.Count > 0 || ds.Catalog.Series.Count > 0;
            LoadingStep.Text = step ?? (hasSaved ? "Opening your saved channels…" : "Starting…");
            LoadingHint.Text = hasSaved
                ? "Getting the latest channels, films and shows from your provider."
                : "Large playlists can take a minute the first time.";
            LoadingSkip.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;
            HideVideo(); // the video surface would otherwise sit on top of this screen
        }
        Spin(LoadingRotate, show);
        if (was && !show) Dispatcher.InvokeAsync(() => { _guide.TryAutoPreview(); _home?.TryStartPreview(); }, DispatcherPriority.Loaded);
        return show;
    }

    void LoadingSkip_Click(object sender, RoutedEventArgs e)
    {
        _loadingDismissed = true;
        _startupLoading = false;
        OnBusyChanged(DataService.Instance.IsBusy, DataService.Instance.IsBusy ? BusyText.Text.TrimStart('·', ' ') : null);
    }

    /// <summary>Short step shown after the pill's title, e.g. "Refreshing playlist · films".</summary>
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

    static void Spin(System.Windows.Media.RotateTransform rt, bool on)
    {
        if (on)
        {
            if (rt.HasAnimatedProperties) return;
            rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
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

    /// <summary>Plays a live channel – in the Guide's mini player, or full screen.</summary>
    public void PlayLive(Channel ch, bool fullscreen)
    {
        if (_svc == null || ch == null) return;
        if (!StartLive(ch)) return;

        if (_playerMode) { ConfigureChrome(); UpdateChromeText(); return; }
        if (fullscreen) { EnterPlayer(true); return; }

        _previewActive = true;
        _lastSlot = Rect.Empty;
        PositionPreview();
    }

    /// <summary>Plays a live channel in the floating, always-on-top mini player.</summary>
    public void PlayLiveMini(Channel ch)
    {
        if (_svc == null || ch == null) return;
        if (!StartLive(ch)) return;
        EnterMini();
    }

    /// <summary>Plays a past (or current, from the start) programme using the provider's catch-up.</summary>
    /// <summary>
    /// Plays a past (or current, from the start) programme with the provider's catch-up – on the channel's
    /// live timeline, so you can scrub through it and carry on (or jump) up to live.
    /// </summary>
    public void PlayCatchup(Channel ch, Programme p)
    {
        if (!DataService.Instance.CanCatchup(ch, p)) { ShowToast("Catch-up isn't available for this programme."); return; }
        PlayFromArchive(ch, p.Start);
    }

    // Merged channels: every version of the channel being watched, in the order to try them.
    List<Channel> _variants = new();
    int _variantIdx;

    bool StartLive(Channel ch)
    {
        // Work with the guide row, so all versions of a merged channel count as the same channel.
        var row = _guide.RowFor(ch.Id);
        var identity = row?.Channel ?? ch;
        bool same = _nowLive && _nowChannel?.Id == identity.Id && _currentUrl != null;
        if (!same)
        {
            var order = row != null ? ChannelMerger.Order(row.Variants, ChannelMerger.CurrentMaxRank()) : new List<Channel> { ch };
            if (row != null && !ReferenceEquals(ch, identity) && order.Contains(ch)) { order.Remove(ch); order.Insert(0, ch); } // a specific version was asked for
            SaveVodProgress();

            // "Last channel" = the previous channel you actually watched (not one you scrolled past).
            if (_nowLive && _nowChannel != null && _nowChannel.Id != identity.Id &&
                (DateTime.UtcNow - _liveStartedUtc).TotalSeconds >= 5)
                _prevChannelId = _nowChannel.Id;

            _variants = order;
            _variantIdx = 0;
            if (!PlayVariant()) return false;
            _liveStartedUtc = DateTime.UtcNow;
            _recentRecorded = false;
        }
        _nowLive = true;
        _nowChannel = identity;
        ch = identity;
        _vod = null;
        SettingsStore.Current.LastChannelId = ch.Id;
        _guide.SetPreviewPlaying(true);
        _home.SetPreviewPlaying(true);
        OnPlaybackStarted();
        return true;
    }

    /// <summary>Opens the current version (<see cref="_variantIdx"/>) of the live channel.</summary>
    bool PlayVariant()
    {
        if (_variantIdx < 0 || _variantIdx >= _variants.Count) return false;
        var url = DataService.Instance.StreamUrl(_variants[_variantIdx]);
        if (url == null) return false;
        if (_startArchiveAtUtc != null && _guide.RowFor(_variants[_variantIdx].Id)?.CatchupChannel is { } archive &&
            TryStartPendingArchive(archive))
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

    /// <summary>Switches to another version of the channel (quality menu or automatic fallback).</summary>
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
        var menu = new ContextMenu { PlacementTarget = QualityButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        for (int i = 0; i < _variants.Count; i++)
        {
            var v = _variants[i];
            var item = new MenuItem { Header = $"{ChannelMerger.Label(v)}  –  {v.Name}", IsChecked = i == _variantIdx };
            int idx = i;
            item.Click += (_, _) => SwitchVariant(idx, null);
            menu.Items.Add(item);
        }
        _hideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; if (_playerMode) _hideTimer.Start(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    /// <summary>Plays a film, episode or catch-up programme full screen.</summary>
    public void PlayVod(VodItem item, bool resume)
    {
        if (_svc == null || item?.Url == null) return;
        SaveVodProgress();

        long start = 0;
        var h = resume ? HistoryStore.Get(item.ResumeKey) : null;
        if (h != null && h.CanResume) start = Math.Max(0, h.PositionMs - 3000); // a few seconds of context

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

        if (_playerMode && !_mini) { ConfigureChrome(); UpdateChromeText(); ShowChrome(); }
        else if (_mini) UpdateChromeText();
        else EnterPlayer(true);

        if (start > 0) ShowToast($"Resuming from {Fmt(start)}  ·  press Home to start over");
    }

    void OnPlaybackStarted()
    {
        _mediaKeys.Enable(true);
        _healthTimer.Start();
        UpdateTaskbar();
    }

    void StopPlayback()
    {
        SaveVodProgress();
        _svc?.Stop();
        _previewActive = false;
        _nowLive = false;
        _nowChannel = null;
        _vod = null;
        _currentUrl = null;
        _mediaKeys?.Enable(false);
        _healthTimer?.Stop();
        KeepAwake.Set(false);
        UpdateTaskbar();
        _guide?.SetPreviewPlaying(false);
        _home?.SetPreviewPlaying(false);
        SetBuffering(null);
        HideUpNext();
        if (_playerMode) ExitPlayerLayout();
        HideVideo();
    }

    // =====================================================================================
    // Video layout: hidden / guide preview / full window / floating mini player
    // =====================================================================================

    void HideVideo()
    {
        _lastSlot = Rect.Empty;
        Video.HorizontalAlignment = HorizontalAlignment.Left;
        Video.VerticalAlignment = VerticalAlignment.Top;
        Video.Margin = new Thickness(-4, -4, 0, 0);
        Video.Width = 2;
        Video.Height = 2;
        PreviewHint.Visibility = Visibility.Collapsed;
        PlayerChrome.Visibility = Visibility.Collapsed;
        MiniChrome.Visibility = Visibility.Collapsed;
        HealthBadge.Visibility = Visibility.Collapsed;
    }

    void PositionPreview()
    {
        try
        {
            FrameworkElement slot = _page == _guide ? _guide.PreviewSlotElement : _page == _home ? _home.PreviewSlotElement : null;
            if (slot == null || !slot.IsVisible || Welcome.Visibility == Visibility.Visible || LoadingOverlayShown || SplashUp) { HideVideo(); return; }
            if (slot.ActualWidth < 10) return;
            var p = slot.TranslatePoint(new Point(0, 0), Root);
            var r = new Rect(p.X, p.Y, slot.ActualWidth, slot.ActualHeight);
            // The video floats above the app, so if the page scrolls it out of view, hide it rather than overlap the top bar.
            var hp = PageHost.TranslatePoint(new Point(0, 0), Root);
            var host = new Rect(hp.X, hp.Y, PageHost.ActualWidth, PageHost.ActualHeight);
            if (!host.Contains(r)) { if (!_lastSlot.IsEmpty) HideVideo(); return; }
            if (r == _lastSlot) return;
            _lastSlot = r;
            Video.HorizontalAlignment = HorizontalAlignment.Left;
            Video.VerticalAlignment = VerticalAlignment.Top;
            Video.Margin = new Thickness(r.X, r.Y, 0, 0);
            Video.Width = r.Width;
            Video.Height = r.Height;
            PreviewHint.Visibility = Visibility.Visible;
            PlayerChrome.Visibility = Visibility.Collapsed;
        }
        catch (InvalidOperationException) { }
    }

    void FillWindowWithVideo()
    {
        _lastSlot = Rect.Empty;
        Toast.Visibility = Visibility.Collapsed;
        Video.HorizontalAlignment = HorizontalAlignment.Stretch;
        Video.VerticalAlignment = VerticalAlignment.Stretch;
        Video.Margin = new Thickness(0);
        Video.Width = double.NaN;
        Video.Height = double.NaN;
        Shell.Visibility = Visibility.Hidden;
        PreviewHint.Visibility = Visibility.Collapsed;
    }

    void EnterPlayer(bool osFullscreen)
    {
        _playerMode = true;
        FillWindowWithVideo();
        MiniChrome.Visibility = Visibility.Collapsed;
        SeekSlider.Value = 0;
        PosText.Text = LenText.Text = "0:00";
        ConfigureChrome();
        if (osFullscreen) SetOsFullscreen(true);
        UpdateChromeText();
        ShowChrome();
        _uiTimer.Start();
        Activate();
        Focus();
    }

    /// <summary>Shows the controls that make sense for what's playing (live vs film/episode).</summary>
    void ConfigureChrome()
    {
        bool live = _nowLive;
        SeekRow.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        LiveRow.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        ChUpButton.Visibility = ChDownButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        MultiViewButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LastChButton.Visibility = live && _prevChannelId != null ? Visibility.Visible : Visibility.Collapsed;
        BackSeekButton.Visibility = FwdSeekButton.Visibility = !live || LiveSeekable ? Visibility.Visible : Visibility.Collapsed;
        BackSeekButton.ToolTip = "Back 10 seconds (←)";
        FwdSeekButton.ToolTip = live ? "Forward 10 seconds (→)" : "Forward 30 seconds (→)";
        NextEpisodeButton.Visibility = !live && _vod?.Next != null ? Visibility.Visible : Visibility.Collapsed;
        QualityButton.Visibility = live && _variants.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (live && _variants.Count > 1 && _variantIdx < _variants.Count)
            QualityButton.ToolTip = $"Version: {ChannelMerger.Label(_variants[_variantIdx])} ({_variantIdx + 1} of {_variants.Count})";
        NextEpisodeButton.ToolTip = _vod?.NextLabel;
    }

    void ExitPlayer()
    {
        if (!_playerMode) return;
        CloseShortcutSheet();
        SaveVodProgress();
        ExitPlayerLayout();
        if (_nowLive && _page == _guide)
        {
            // back to the Guide – the channel keeps playing in the guide's preview
            _previewActive = true;
            Dispatcher.InvokeAsync(PositionPreview, DispatcherPriority.Loaded);
        }
        else StopPlayback();
    }

    void ExitPlayerLayout()
    {
        ClosePanel();
        RestoreDisplayRate();
        if (_mini) LeaveMiniWindow();
        SetOsFullscreen(false);
        _playerMode = false;
        _uiTimer.Stop();
        _hideTimer.Stop();
        Overlay.Cursor = null;
        Motion.HideNow(OverlayToast);
        Motion.HideNow(PlayerChrome);
        Motion.HideNow(InfoBar);
        _browseRow = null;
        MiniChrome.Visibility = Visibility.Collapsed;
        HealthBadge.Visibility = Visibility.Collapsed;
        HideUpNext();
        ClearNumber();
        Shell.Visibility = Visibility.Visible;
        HideVideo();
    }

    void SetOsFullscreen(bool on, bool restoreMaximized = true)
    {
        if (on == _osFullscreen) return;
        if (on)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            // Cover the whole monitor exactly (not "maximise", which can stop at the taskbar), and sit
            // above the taskbar while the player is in front.
            var m = MonitorBoundsDip();
            // While maximised, these set where the window will restore to – so un-maximising goes straight to
            // full screen in one step, instead of flashing the window's normal size on the way.
            Left = m.Left; Top = m.Top; Width = m.Width; Height = m.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Left = m.Left; Top = m.Top; Width = m.Width; Height = m.Height;
            Topmost = true;
            _streamingFullscreen = true;
        }
        else
        {
            // No longer full screen from here on: changing the window back below re-activates it, and the activation
            // handlers would otherwise put it back on top of everything.
            _osFullscreen = false;
            if (!_mini) Topmost = false;
            _streamingFullscreen = false;
            bool toMaximized = restoreMaximized && _prevState == WindowState.Maximized && !_mini;
            WindowStyle = _prevStyle == WindowStyle.None ? WindowStyle.SingleBorderWindow : _prevStyle;
            ResizeMode = _prevResize == ResizeMode.NoResize ? ResizeMode.CanResize : _prevResize;
            if (toMaximized)
            {
                // Straight back to maximised (one step), then quietly set the size it restores to later.
                WindowState = WindowState.Maximized;
                if (!_normalBounds.IsEmpty)
                {
                    Left = _normalBounds.Left; Top = _normalBounds.Top;
                    Width = _normalBounds.Width; Height = _normalBounds.Height;
                }
            }
            else if (!_normalBounds.IsEmpty)
            {
                Left = _normalBounds.Left; Top = _normalBounds.Top;
                Width = _normalBounds.Width; Height = _normalBounds.Height;
            }
            NativeWindow.ApplyDarkTitleBar(this);
        }
        _osFullscreen = on;
        if (!on)
        {
            DropTopmostSoon();
            // …and again once the video and its controls have settled at the new size.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (_, _) => { t.Stop(); EnsureNotOnTop(); };
            t.Start();
        }
        NativeWindow.RoundCorners(this, !on);
        UpdateWindowFrame();
        FullscreenButton.Content = "\uE73F"; // "exit full screen" – the player is always full screen
    }

    bool _streamingFullscreen;

    /// <summary>
    /// Safety net: only full screen (while Dingus is in front) and picture-in-picture sit above other apps. Once the
    /// window has settled after a change, anything else that left it on top is undone.
    /// </summary>
    void DropTopmostSoon() => Dispatcher.InvokeAsync(EnsureNotOnTop, DispatcherPriority.ApplicationIdle);

    /// <summary>
    /// Unless it's full screen or picture-in-picture, no Dingus window may sit above other apps: the main window and
    /// everything it owns (the player controls over the video, dialogs) – checked against what Windows itself has,
    /// not just WPF's Topmost. Windows meant to be on top (the reminder pop-up) are left alone.
    /// </summary>
    void EnsureNotOnTop()
    {
        if (_osFullscreen || _mini) return;
        if (Topmost) Topmost = false;
        int fixedCount = 0;
        if (NativeWindow.ClearTopmost(this)) fixedCount++;
        foreach (Window w in OwnedWindows)
            if (!w.Topmost && NativeWindow.ClearTopmost(w)) fixedCount++;
        foreach (var w in Application.Current.Windows.OfType<Window>())
            if (w != this && w.Owner == null && !w.Topmost && w.IsVisible && NativeWindow.ClearTopmost(w)) fixedCount++;
        if (fixedCount > 0) Log.Info($"Took {fixedCount} window(s) off 'always on top'");
        LastTopmostFixes += fixedCount;
    }

    /// <summary>For the self-test: how many windows had to be taken off "always on top".</summary>
    internal int LastTopmostFixes;

    /// <summary>The monitor the window is on, in WPF units.</summary>
    Rect MonitorBoundsDip()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return new Rect(b.Left / dpi.DpiScaleX, b.Top / dpi.DpiScaleY, b.Width / dpi.DpiScaleX, b.Height / dpi.DpiScaleY);
    }

    // ----- floating mini player -----

    void EnterMini()
    {
        if (_mini || _svc == null || (!_nowLive && _vod == null)) return;
        ClosePanel();
        RestoreDisplayRate();

        var savedState = _osFullscreen ? _prevState : WindowState;
        if (_osFullscreen) SetOsFullscreen(false, restoreMaximized: false);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!_normalBounds.IsEmpty) bounds = _normalBounds;
        _miniSaved = new WinState(bounds, savedState, WindowStyle, ResizeMode, MinWidth, MinHeight);

        _mini = true;
        _playerMode = true;
        _previewActive = false;
        FillWindowWithVideo();
        PlayerChrome.Visibility = Visibility.Collapsed;
        HealthBadge.Visibility = Visibility.Collapsed;
        _hideTimer.Stop();
        Overlay.Cursor = null;

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 0;
        MinHeight = 0;
        var s = SettingsStore.Current;
        double w = Math.Clamp(s.MiniWidth ?? MiniWidth, 320, 1280), h = w * 9 / 16;
        Width = w;
        Height = h;
        Topmost = true;

        var wa = SystemParameters.WorkArea;
        double left = wa.Right - w - 16, top = wa.Bottom - h - 16;
        if (s.MiniLeft is double ml && s.MiniTop is double mt &&
            ml >= wa.Left - 20 && mt >= wa.Top - 20 && ml + w <= wa.Right + 20 && mt + h <= wa.Bottom + 20)
        { left = ml; top = mt; }
        Left = left;
        Top = top;

        UpdateChromeText();
        _uiTimer.Start();
    }

    void LeaveMiniWindow()
    {
        if (!_mini) return;
        SettingsStore.Current.MiniLeft = Left;
        SettingsStore.Current.MiniTop = Top;
        SettingsStore.Current.MiniWidth = Width;
        _mini = false;
        MiniChrome.Visibility = Visibility.Collapsed;
        Topmost = false;
        DropTopmostSoon();
        var m = _miniSaved;
        WindowStyle = m.Style == WindowStyle.None ? WindowStyle.SingleBorderWindow : m.Style;
        ResizeMode = m.Resize == ResizeMode.NoResize ? ResizeMode.CanResize : m.Resize;
        MinWidth = m.MinW;
        MinHeight = m.MinH;
        Left = m.Bounds.Left;
        Top = m.Bounds.Top;
        Width = m.Bounds.Width;
        Height = m.Bounds.Height;
        if (m.State == WindowState.Maximized) WindowState = WindowState.Maximized;
        NativeWindow.ApplyDarkTitleBar(this);
    }

    /// <param name="toPlayer">True = open the full player; false = go back to the app.</param>
    void ExitMini(bool toPlayer)
    {
        if (!_mini) return;
        LeaveMiniWindow();
        if (toPlayer) EnterPlayer(true);
        else ExitPlayer();
    }

    void Mini_Click(object sender, RoutedEventArgs e) => EnterMini();
    void PreviewPip_Click(object sender, RoutedEventArgs e) => EnterMini();

    /// <summary>Full-screen button on the Guide / Home preview.</summary>
    void PreviewFull_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_currentUrl == null || _playerMode || !_nowLive) return;
        EnterPlayer(true); // same as double-clicking the preview
    }

    /// <summary>Picture-in-picture: drag the corner grip to resize (always 16:9).</summary>
    void PipGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_mini) return;
        var wa = SystemParameters.WorkArea;
        double w = Math.Clamp(Width + Math.Max(e.HorizontalChange, e.VerticalChange * 16 / 9), 320, Math.Min(1280, wa.Width - 32));
        Width = w;
        Height = w * 9 / 16;
    }

    void PipGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SettingsStore.Current.MiniWidth = Width;
        SnapPip();
    }

    /// <summary>After moving or resizing: keep it on screen, and snap into a corner when it's dropped near one.</summary>
    void SnapPip()
    {
        if (!_mini) return;
        var wa = SystemParameters.WorkArea;
        const double margin = 16, snap = 80;
        double l = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width)), t = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        double toLeft = l - wa.Left, toRight = wa.Right - (l + Width), toTop = t - wa.Top, toBottom = wa.Bottom - (t + Height);
        if (Math.Min(toLeft, toRight) < snap && Math.Min(toTop, toBottom) < snap)
        {
            l = toLeft < toRight ? wa.Left + margin : wa.Right - Width - margin;
            t = toTop < toBottom ? wa.Top + margin : wa.Bottom - Height - margin;
        }
        Left = l;
        Top = t;
        SettingsStore.Current.MiniLeft = l;
        SettingsStore.Current.MiniTop = t;
    }
    void MiniExpand_Click(object sender, RoutedEventArgs e) => ExitMini(true);
    void MiniClose_Click(object sender, RoutedEventArgs e) => ExitMini(false);

    // =====================================================================================
    // Player chrome
    // =====================================================================================

    void UpdateChromeText() => UpdateChromeText(_nowLive ? _nowChannel : null);

    /// <summary>Fills the top bar. For live TV this is the zap banner: number, logo, now and next.</summary>
    void UpdateChromeText(Channel ch)
    {
        if (ch != null)
        {
            var row = _guide.RowFor(ch.Id);
            ChromeTitle.Text = row?.DisplayName ?? ch.Name;
            MiniTitle.Text = ch.Name;
            ImageLoader.SetUrl(ChromeLogo, row?.Logo);
            ChromeLogoBox.Visibility = string.IsNullOrEmpty(row?.Logo) ? Visibility.Collapsed : Visibility.Visible;
            ChromeLeft.Text = ""; // live: the time left is next to the programme's times
            UpdateLiveInfo(ch);
            UpdateMiniInfo();
            TidyChromeLines();
        }
        else
        {
            ChromeTitle.Text = _vod?.Title ?? "";
            ChromeSubtitle.Text = _vod?.Subtitle ?? "";
            ChromeNext.Text = _vod?.NextLabel != null ? "Next   " + _vod.NextLabel : "";
            MiniTitle.Text = _vod?.Title ?? "";
            ChromeLogoBox.Visibility = Visibility.Collapsed;
            UpdateMiniInfo();
            TidyChromeLines();
        }
    }

    void UpdateLiveInfo(Channel ch)
    {
        var at = DateTime.Now - (_svc?.BehindLive ?? TimeSpan.Zero); // where you are, if paused/rewound/catch-up
        var row = ch == null ? null : _guide.RowFor(ch.Id);
        var p = row != null ? DataService.Instance.Epg.At(_guide.EpgIdOf(row), at) : _guide.NowOn(ch);
        if (p == null)
        {
            ChromeSubtitle.Text = "No guide information";
            ChromeNext.Text = "";
            LiveProgress.Value = 0;
            LiveTimes.Text = "";
            return;
        }
        var now = at;
        ChromeSubtitle.Text = p.Title;
        var next = row != null ? DataService.Instance.Epg.StartingFrom(_guide.EpgIdOf(row), p.End) : _guide.NextOn(ch);
        ChromeNext.Text = next != null ? $"Next   {next.Start:HH:mm}   {next.Title}" : "";
        LiveProgress.Value = (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds);
        LiveTimes.Text = $"{p.Start:HH:mm} – {p.End:HH:mm}  ·  {LeftText(p.End - now)}";
    }

    /// <summary>Empty lines in the player's top bar take no space (a film has no "Next", a channel no "time left" line).</summary>
    void TidyChromeLines()
    {
        foreach (var t in new[] { ChromeSubtitle, ChromeNext, ChromeLeft })
            t.Visibility = string.IsNullOrEmpty(t.Text) ? Visibility.Collapsed : Visibility.Visible;
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
            var at = DateTime.Now - (_svc.BehindLive);
            var row = _guide.RowFor(_nowChannel.Id);
            MiniTitle.Text = row?.DisplayName ?? _nowChannel.Name;
            var p = row != null ? DataService.Instance.Epg.At(_guide.EpgIdOf(row), at) : _guide.NowOn(_nowChannel);
            if (p == null) { MiniInfo.Text = "No guide information"; MiniProgress.Visibility = Visibility.Collapsed; return; }
            MiniInfo.Text = $"{p.Title}  ·  {LeftText(p.End - at)}";
            MiniProgress.Value = (at - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds);
            MiniProgress.Visibility = Visibility.Visible;
            return;
        }
        var mp = _svc.Player;
        long len = mp.Length, time = mp.Time;
        MiniTitle.Text = _vod?.Title ?? "";
        var sub = _vod?.Subtitle;
        var left = len > 0 ? LeftText(TimeSpan.FromMilliseconds(Math.Max(0, len - time))) : null;
        MiniInfo.Text = string.Join("  ·  ", new[] { sub, left }.Where(x => !string.IsNullOrEmpty(x)));
        MiniProgress.Visibility = len > 0 ? Visibility.Visible : Visibility.Collapsed;
        MiniProgress.Value = len > 0 ? Math.Clamp((double)time / len, 0, 1) : 0;
    }

    void ShowChrome()
    {
        if (!_playerMode) return;
        if (_panelOpen) { BumpPanelTimer(); return; } // the channel list replaces the controls while it's open
        if (_mini)
        {
            MiniChrome.Visibility = Visibility.Visible;
            _hideTimer.Stop();
            _hideTimer.Start();
            return;
        }
        Motion.FadeIn(PlayerChrome, ms: 160);
        Overlay.Cursor = null;
        _hideTimer.Stop();
        _hideTimer.Start();
        UpdatePlayerUi();
        UpdateHealthVisibility();
    }

    void HideChrome()
    {
        _hideTimer.Stop();
        if (!_playerMode || _menuOpen || SeekSlider.IsMouseCaptureWithin || VolumeSlider.IsMouseCaptureWithin) return;
        if (_mini) { if (!Overlay.IsMouseOver) MiniChrome.Visibility = Visibility.Collapsed; return; }
        if (UpNextCard.Visibility == Visibility.Visible) return;
        Motion.FadeOut(PlayerChrome, Visibility.Hidden, ms: 260);
        Overlay.Cursor = Cursors.None;
        UpdateHealthVisibility();
    }

    void UpdatePlayerUi()
    {
        if (!_playerMode || _svc == null) return;
        var mp = _svc.Player;
        var glyph = mp.IsPlaying ? "" : "";
        MiniPlayPause.Content = glyph;
        PreviewPlayPause.Content = glyph;
        UpdateVolumeGlyphs();
        UpdateMiniInfo();
        if (PlayerChrome.Visibility != Visibility.Visible) return;
        PlayPauseButton.Content = glyph;

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

    static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    void SetPreviewPauseGlyph(bool playing)
    {
        PreviewPlayPause.Content = playing ? "\uE769" : "\uE768";
        PreviewPlayPause.ToolTip = playing ? "Pause" : "Play";
        MiniPlayPause.Content = PreviewPlayPause.Content;
    }

    void SetBuffering(string text)
    {
        BufferText.Text = text ?? "";
        BufferText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void HookPlayerEvents()
    {
        var mp = _svc.Player;
        mp.Paused += (_, _) => Dispatcher.InvokeAsync(() => SetPreviewPauseGlyph(false));
        mp.Playing += (_, _) => Dispatcher.InvokeAsync(() =>
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
            Dispatcher.InvokeAsync(() =>
            {
                if (_reconnecting) return;
                if (pct >= 100) SetBuffering(null);
                else if (_previewActive || _playerMode) SetBuffering($"Buffering {pct:0}%");
            }, DispatcherPriority.Background);
        };
        mp.EncounteredError += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (_currentUrl != null) Reconnect();
        });
        mp.EndReached += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            SetBuffering(null);
            if (_currentUrl == null) return;
            if (_nowLive && _svc.Source == LiveSource.Archive) { GoLiveNow(); return; } // caught up with live
            if (_nowLive) Reconnect(); // live streams shouldn't "end" – it's a dropped connection
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
        _rateMatched = false;
        _trimmedThisStream = false;
        _streamStartedUtc = DateTime.UtcNow;
        _reconnects = 0;
        HealthText.Text = "Connecting…";
        HealthDot.Fill = Amber;
    }

    static readonly System.Windows.Media.SolidColorBrush Green = Frozen(0x4A, 0xDE, 0x80);
    static readonly System.Windows.Media.SolidColorBrush Amber = Frozen(0xFB, 0xBF, 0x24);
    static readonly System.Windows.Media.SolidColorBrush Red = Frozen(0xF8, 0x71, 0x71);
    static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    async Task HealthTick()
    {
        if (_svc == null || _currentUrl == null) { _healthTimer.Stop(); return; }
        var url = _currentUrl;
        StreamHealth h;
        try { h = await _svc.GetHealthAsync(); }
        catch { return; }
        if (url != _currentUrl) return; // stream changed while we were reading

        UpdateTaskbar(h);
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

        // Stalled: meant to be playing, but no new frames and the clock isn't moving.
        bool active = h.State is VLCState.Playing or VLCState.Buffering or VLCState.Opening;
        var limit = h.State == VLCState.Opening ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(12);
        if (active && !_reconnecting && now - _lastProgressUtc > limit) { Reconnect(); return; }

        // Once a stream has settled (15 s of smooth playback), hand unused memory back to Windows.
        if (!_trimmedThisStream && progressed && h.State == VLCState.Playing && now - _streamStartedUtc > TimeSpan.FromSeconds(15))
        {
            _trimmedThisStream = true;
            Memory.CompactSoon();
        }

        // Remember this channel in "Recently watched" once it's been on for a bit.
        if (_nowLive && !_recentRecorded && now - _liveStartedUtc > TimeSpan.FromSeconds(10) && progressed)
        {
            _recentRecorded = true;
            HistoryStore.AddRecentChannel(_nowChannel?.Id);
        }

        // Remember where you are in films/episodes.
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

        // Badge text and colour.
        var parts = new List<string>();
        if (h.Height > 0) parts.Add(ResLabel(h.Width, h.Height));
        if (h.Fps > 1) parts.Add($"{h.Fps:0} fps");
        if (h.Mbps > 0.05) parts.Add($"{h.Mbps:0.0} Mbps");
        parts.Add(dropped == 0 ? "no dropped frames" : $"{dropped} dropped");
        if (h.GpuDecoding == true) parts.Add("GPU decoding");
        else if (h.GpuDecoding == false) parts.Add("CPU decoding");
        long ws = Memory.WorkingSetMB(), ours = GC.GetTotalMemory(false) / (1024 * 1024);
        parts.Add($"RAM {ws:N0} MB (app data {ours:N0} MB)");

        // Warn once per stream if a 4K picture is being decoded on the processor (slow and memory-hungry).
        if (h.GpuDecoding == false && h.Height >= 2000 && !_cpuWarned)
        {
            _cpuWarned = true;
            ShowToast(SettingsStore.Current.HardwareDecoding
                ? "Your graphics card can't decode this 4K stream, so the processor is doing it – that uses a lot of memory and CPU."
                : "Hardware decoding is off in Settings, so 4K is being decoded by the processor. Turning it on uses far less memory.");
        }
        // Adaptive frame rate: once the frame rate is known, match the display to it (full screen only).
        if (!_rateMatched && _playerMode && !_mini && _osFullscreen && SettingsStore.Current.AdaptiveFrameRate &&
            h.State == VLCState.Playing && h.Fps > 10 && h.DisplayedPictures > 50)
        {
            _rateMatched = true;
            var fps = h.Fps;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var hz = await Task.Run(() => DisplayRate.Match(fps, hwnd));
            if (hz != null && _playerMode) ShowToast($"Display set to {hz} Hz to match the video ({fps:0.###} fps)");
        }

        HealthText.Text = _reconnecting ? $"Reconnecting ({_reconnects}/{MaxReconnects})" : string.Join("  ·  ", parts);
        HealthDot.Fill = _reconnecting || (active && now - _lastProgressUtc > TimeSpan.FromSeconds(4)) ? Red
                       : dropped > 5 || h.State == VLCState.Buffering ? Amber
                       : Green;
        UpdateHealthVisibility();
    }

    void RestoreDisplayRate()
    {
        _rateMatched = false;
        if (DisplayRate.IsChanged) Task.Run(DisplayRate.Restore);
    }

    // =====================================================================================
    // In-player channel list
    // =====================================================================================

    void Channels_Click(object sender, RoutedEventArgs e) => OpenPanel();

    void OpenPanel()
    {
        if (!_playerMode || _mini || !_nowLive) return;
        _panelCats = _guide.Categories.Where(c => c.Count > 0).ToList();
        if (_panelCats.Count == 0) return;
        _panelCatIdx = Math.Max(0, _panelCats.FindIndex(c => c.Id == _guide.CurrentCategoryId));
        _panelOpen = true;
        _hideTimer.Stop();
        Motion.HideNow(PlayerChrome, Visibility.Hidden);
        Overlay.Cursor = null;
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
                Now = p == null ? "No guide information" : $"{p.Title}",
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
        if (_playerMode && !_mini) ShowChrome();
    }

    void BumpPanelTimer()
    {
        _panelTimer.Stop();
        _panelTimer.Start();
    }

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
        return false; // other keys (numbers, volume, pause) still work while the list is open
    }

    void PanelMove(int delta)
    {
        int n = PanelList.Items.Count;
        if (n == 0) return;
        int i = PanelList.SelectedIndex < 0 ? 0 : Math.Clamp(PanelList.SelectedIndex + delta, 0, n - 1);
        PanelList.SelectedIndex = i;
        PanelList.ScrollIntoView(PanelList.SelectedItem);
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
        _guide.ShowAndSelect(cat.Id, z.Row); // channel up/down now moves within this category
        PlayLive(z.Row.Channel, false);
        UpdateChromeText(z.Row.Channel);
    }

    void PanelList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(PanelList, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        PanelList.SelectedItem = item.DataContext;
        TunePanelSelection();
        e.Handled = true;
    }

    void ChannelPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // don't treat as a click on the video

    bool IsInChannelPanel(DependencyObject d)
    {
        while (d != null)
        {
            if (ReferenceEquals(d, ChannelPanel)) return true;
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
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

    void UpdateHealthVisibility()
    {
        bool show = _playerMode && !_mini && _currentUrl != null &&
                    (_healthPinned || PlayerChrome.Visibility == Visibility.Visible);
        HealthBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    void Info_Click(object sender, RoutedEventArgs e) => ToggleHealthPinned();

    void ToggleHealthPinned()
    {
        _healthPinned = !_healthPinned;
        SettingsStore.Current.ShowStreamInfo = _healthPinned;
        SettingsStore.Save();
        UpdateHealthVisibility();
        ShowToast(_healthPinned ? "Stream info pinned (press I to hide)" : "Stream info shows with the controls");
    }

    /// <summary>Re-opens the current stream after an error or stall, up to three times, with a short back-off.</summary>
    async void Reconnect()
    {
        var url = _currentUrl;
        if (url == null || _svc == null) return;

        // Merged channel: after one failed retry, move on to the next version instead of hammering this one.
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
        if (_currentUrl != url) return; // you changed channel meanwhile

        long resumeAt = _nowLive ? 0 : Math.Max(0, _lastVodTime - 2000);
        if (_nowLive && _svc.Source == LiveSource.Archive) { SeekLiveTo(LivePositionUtc); _lastProgressUtc = DateTime.UtcNow; return; }
        try { _svc.Play(url, _nowLive, resumeAt, _nowLive ? PictureFor(_variants.Count > _variantIdx ? _variants[_variantIdx] : _nowChannel) : null); }
        catch (Exception ex) { Log.Error(ex); }
        _lastProgressUtc = DateTime.UtcNow;
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
        if (_mini) UpNextCard.Visibility = Visibility.Collapsed; else Motion.FadeIn(UpNextCard, fromX: 30, ms: 220);
        if (!_mini) ShowChrome();
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
        if (UpNextCard != null) UpNextCard.Visibility = Visibility.Collapsed;
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
    // Keyboard, mouse & media keys
    // =====================================================================================

    void Overlay_Loaded(object sender, RoutedEventArgs e)
    {
        // The controls over the video live in their own small window. Don't steal activation back
        // from it mid-click (that cancels the click). Keys are read app-wide in OnThreadMessage.
    }

    void OnThreadMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        if (handled || !_playerMode || _menuOpen) return;
        if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) return;
        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        if (HandleKey(key)) handled = true;
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_playerMode) return; // handled app-wide in OnThreadMessage
        if (SplashUp) return;     // no shortcuts into the pages hidden behind the splash
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        bool typing = Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox;
        if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchShortcut(); e.Handled = true; return; }
        if (IsSheetKey(key) && (!typing || key == Key.F1)) { ToggleShortcutSheet(); e.Handled = true; return; }
        if (SheetOpen && key == Key.Escape) { CloseShortcutSheet(); e.Handled = true; return; }
        if (key == Key.F11 && _nowLive) { EnterPlayer(true); e.Handled = true; return; }
        if (key == Key.P && _nowLive && !(Keyboard.FocusedElement is TextBox)) { EnterMini(); e.Handled = true; return; }
        if (key == Key.V && _page == _guide && !(Keyboard.FocusedElement is TextBox)) { OpenMultiView(null); e.Handled = true; return; }

        // Type a channel number anywhere in the Guide (unless you're typing in a text box).
        if (_page == _guide && !(Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox))
        {
            if (DigitOf(key) is int d) { TypeDigit(d); e.Handled = true; return; }
            if (key == Key.Enter && _number.Length > 0) { CommitNumber(); e.Handled = true; return; }
            if (key == Key.Escape && _number.Length > 0) { ClearNumber(); e.Handled = true; }
        }
    }

    bool HandleKey(Key key)
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
                if (_mini) ExitMini(false); else ExitPlayer();
                return true;
            case Key.Enter:
                if (_number.Length > 0) { CommitNumber(); return true; }
                if (_nowLive && !_mini) { OpenPanel(); return true; }
                break;
            case Key.L:
                if (_nowLive && !_mini) { OpenPanel(); return true; }
                break;
            case Key.F:
            case Key.F11:
                // The player is always full screen: leaving full screen means going back to the app.
                if (_mini) ExitMini(true); else ExitPlayer();
                return true;
            case Key.P:
                if (_mini) ExitMini(true); else EnterMini();
                return true;
            case Key.I:
                ToggleHealthPinned();
                return true;
            case Key.S:
                if (!_mini) { ShowSleepMenu(); return true; }
                break;
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
                if (LiveSeekable) { SeekLiveBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? -300 : -10); return true; }
                break;
            case Key.Right:
                if (!_nowLive) { _svc?.SeekBy(30_000); return true; }
                if (LiveSeekable && _svc.BehindLive > TimeSpan.FromSeconds(4)) { SeekLiveBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 300 : 10); return true; }
                break;
            case Key.End:
                if (LiveSeekable) { GoLiveNow(); ShowToast("Back to live"); return true; }
                break;
        }
        return false;
    }

    /// <summary>Media keys arrive here whether or not the app is focused (only while something plays).</summary>
    void HandleMediaKey(Key key)
    {
        if (_currentUrl == null) return;
        if (_playerMode) { HandleKey(key); return; }

        // Guide preview: play/pause, stop, and zap through the list.
        switch (key)
        {
            case Key.MediaPlayPause: _svc?.TogglePause(); break;
            case Key.MediaStop: StopPlayback(); break;
            case Key.MediaNextTrack: if (_nowLive) _guide.Step(1); break;
            case Key.MediaPreviousTrack: if (_nowLive) _guide.Step(-1); break;
        }
    }

    static int? DigitOf(Key k)
    {
        if (k >= Key.D0 && k <= Key.D9) return k - Key.D0;
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return k - Key.NumPad0;
        return null;
    }

    // ----- channel numbers & zapping -----

    void TypeDigit(int d)
    {
        if (_number.Length == 0 && d == 0) { GoToPreviousChannel(); return; }
        if (_number.Length >= 4) _number = "";
        _number += d.ToString();
        NumberText.Text = OverlayNumberText.Text = _number;
        NumberBox.Visibility = !_playerMode ? Visibility.Visible : Visibility.Collapsed;
        OverlayNumberBox.Visibility = _playerMode ? Visibility.Visible : Visibility.Collapsed;
        _numberTimer.Stop();
        _numberTimer.Start();
    }

    void CommitNumber()
    {
        var n = _number;
        ClearNumber();
        if (n.Length == 0) return;
        var row = _guide.SelectByNumber(n); // the guide's selection debounce then tunes the channel
        if (row == null) ShowToast($"There's no channel {n}.");
        else if (_playerMode) UpdateChromeText(row.Channel);
    }

    void ClearNumber()
    {
        _number = "";
        _numberTimer?.Stop();
        if (NumberBox != null) NumberBox.Visibility = Visibility.Collapsed;
        if (OverlayNumberBox != null) OverlayNumberBox.Visibility = Visibility.Collapsed;
    }

    void GoToPreviousChannel()
    {
        if (_prevChannelId == null) { ShowToast("There's no previous channel yet."); return; }
        var row = _guide.SelectChannel(_prevChannelId);
        if (row == null) { ShowToast("The previous channel isn't in this playlist any more."); return; }
        if (_playerMode) UpdateChromeText(row.Channel);
    }

    /// <summary>
    /// Channel up/down. The banner updates instantly; the stream itself is only opened once you stop
    /// pressing (the guide's selection debounce), so flicking through channels doesn't open – and
    /// get blocked on – a connection for every channel you pass.
    /// </summary>
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

    /// <summary>The speaker icon on the player's and the mini player's mute buttons (crossed out when muted or at 0).</summary>
    void UpdateVolumeGlyphs()
    {
        var g = _muted || VolumeSlider.Value <= 0 ? "\uE74F" : "\uE767";
        if (MuteButton != null) MuteButton.Content = g;
        if (MiniMute != null) MiniMute.Content = g;
    }

    // Mini player: the volume slider slides out while the mouse is over the mute button. It and the player's own
    // slider are the same volume, kept in step both ways.
    void MiniVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeSlider != null && Math.Abs(VolumeSlider.Value - e.NewValue) > 0.5) VolumeSlider.Value = e.NewValue;
    }

    void MiniVolume_MouseEnter(object sender, MouseEventArgs e) => SlideMiniVolume(open: true);

    void MiniVolume_MouseLeave(object sender, MouseEventArgs e)
    {
        if (MiniVolumeSlider.IsMouseCaptureWithin) return; // still dragging
        SlideMiniVolume(open: false);
    }

    void SlideMiniVolume(bool open)
    {
        var a = new System.Windows.Media.Animation.DoubleAnimation(open ? 96 : 0, TimeSpan.FromMilliseconds(open ? 200 : 160))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = open ? System.Windows.Media.Animation.EasingMode.EaseOut : System.Windows.Media.Animation.EasingMode.EaseIn,
            },
        };
        MiniVolumeHost.BeginAnimation(WidthProperty, a);
    }

    // ----- mouse over the video -----

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_playerMode) return;
        if (_mini || PlayerChrome.Visibility != Visibility.Visible) ShowChrome();
        else { _hideTimer.Stop(); _hideTimer.Start(); }
    }

    void Overlay_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_mini) MiniChrome.Visibility = Visibility.Collapsed;
    }

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mini)
        {
            if (e.ClickCount == 2) ExitMini(true);
            else
            {
                NativeWindow.BeginDrag(this); // returns when you let go
                SnapPip();
            }
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            if (!_playerMode) { if (_nowLive) EnterPlayer(true); }
            else if (!(e.OriginalSource is DependencyObject d && IsInChannelPanel(d))) ExitPlayer(); // double-click the video = back to the guide
            e.Handled = true;
        }
        else if (_playerMode) ShowChrome();
    }

    void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mini && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl + mouse wheel resizes picture-in-picture, keeping its outer corner in place.
            var wa = SystemParameters.WorkArea;
            double w = Math.Clamp(Width * (e.Delta > 0 ? 1.1 : 1 / 1.1), 320, Math.Min(1280, wa.Width - 32));
            double right = Left + Width, bottom = Top + Height;
            Width = w; Height = w * 9 / 16;
            if (right > wa.Left + wa.Width / 2) Left = right - Width;
            if (bottom > wa.Top + wa.Height / 2) Top = bottom - Height;
            SettingsStore.Current.MiniWidth = w;
            SnapPip();
            return;
        }
        if (!_playerMode) return;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 5 : -5), 0, 100);
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

    void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _svc?.SeekTo((long)SeekSlider.Value);
        _seeking = false;
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = (int)e.NewValue;
        SettingsStore.Current.Volume = v;
        if (v > 0 && _muted) { _muted = false; _svc?.SetMute(false); }
        _svc?.SetVolume(v);
        UpdateVolumeGlyphs();
        if (MiniVolumeSlider != null && Math.Abs(MiniVolumeSlider.Value - v) > 0.5) MiniVolumeSlider.Value = v;
    }

    void Audio_Click(object sender, RoutedEventArgs e) =>
        ShowTrackMenu(AudioButton, _svc?.AudioTracks(), id => _svc.SetAudioTrack(id), "No other audio tracks");

    void Subs_Click(object sender, RoutedEventArgs e) =>
        ShowTrackMenu(SubsButton, _svc?.SubtitleTracks(), id => _svc.SetSubtitle(id), "No subtitles in this stream");

    void ShowTrackMenu(Button anchor, List<TrackOption> tracks, Action<int> select, string emptyText)
    {
        if (tracks == null) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        if (tracks.Count == 0) menu.Items.Add(new MenuItem { Header = emptyText, IsEnabled = false });
        foreach (var t in tracks)
        {
            var item = new MenuItem { Header = t.Name, IsChecked = t.Current };
            var id = t.Id;
            item.Click += (_, _) => select(id);
            menu.Items.Add(item);
        }
        _hideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; if (_playerMode) _hideTimer.Start(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }
}
