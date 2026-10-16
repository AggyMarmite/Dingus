using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
// WPF also has a MediaPlayer/Media – these tiles use VLC's.
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Media = LibVLCSharp.Shared.Media;
using LibVLCSharp.WPF;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Multi-view: 4, 6 or 8 live channels at once, full screen. Each tile is its own stream (one connection
/// each); only the selected tile plays sound. Warns when the layout needs more streams than your plan allows.
/// </summary>
public partial class MultiViewWindow : Window
{
    sealed class Tile
    {
        public int Index;
        public Border Frame;
        public VideoView View;
        public MediaPlayer Player;
        public GuideRow Row;
        public List<Channel> Variants = new();
        public int VariantIdx;
        public TextBlock Name, Now, Status, Number;
        public FrameworkElement Info, AddPrompt;
        public TextBlock SoundIcon;
        public bool Failed;
    }

    readonly LibVLC _vlc;
    readonly GuideView _guide;
    readonly List<Tile> _tiles = new();
    readonly List<GuideRow> _keep = new(); // channels carried across layout changes
    int _layout;
    Tile _focused;
    bool _closing;

    /// <summary>The tile you were listening to when Multi-view closed (to carry on in the main player), or null.</summary>
    public GuideRow FocusedRow { get; private set; }
    /// <summary>True when you closed Multi-view by asking to watch one tile full screen.</summary>
    public bool OpenFullScreen { get; private set; }

    static readonly SolidColorBrush FocusBrush = Frozen(0x3B, 0x82, 0xF6);
    static readonly SolidColorBrush IdleBrush = Frozen(0x1A, 0x1F, 0x2B);
    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public MultiViewWindow(LibVLC vlc, GuideView guide, GuideRow first)
    {
        _vlc = vlc;
        _guide = guide;
        InitializeComponent();
        if (first != null) _keep.Add(first);

        SourceInitialized += (_, _) =>
        {
            // Full screen on the same monitor as the main window, above the taskbar.
            var owner = Owner ?? Application.Current.MainWindow;
            var hwnd = new WindowInteropHelper(owner).Handle;
            var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            var dpi = VisualTreeHelper.GetDpi(owner);
            Left = b.Left / dpi.DpiScaleX; Top = b.Top / dpi.DpiScaleY;
            Width = b.Width / dpi.DpiScaleX; Height = b.Height / dpi.DpiScaleY;
            Topmost = true;
        };
        Activated += (_, _) => Topmost = true;
        Deactivated += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!Application.Current.Windows.OfType<Window>().Any(w => w.IsActive && (w == this || w.Owner == this || w is ChannelPickerWindow)))
                Topmost = false;
        });

        ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= OnThreadMessage;
        Closing += (_, _) => Shutdown();

        int layout = SettingsStore.Current.MultiViewLayout;
        (layout == 8 ? L8 : layout == 6 ? L6 : L4).IsChecked = true; // builds the tiles
    }

    // ------------------------------------------------------------------ layout

    void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string t || !int.TryParse(t, out var n) || Tiles == null) return;
        SettingsStore.Current.MultiViewLayout = n;
        SettingsStore.Save();
        BuildTiles(n);
    }

    void BuildTiles(int count)
    {
        // Remember what's showing, release the old tiles, then lay out the new grid.
        if (_tiles.Count > 0)
        {
            _keep.Clear();
            _keep.AddRange(_tiles.Where(t => t.Row != null).Select(t => t.Row));
        }
        ReleaseTiles();
        _layout = count;
        Tiles.Columns = count == 4 ? 2 : count == 6 ? 3 : 4;
        Tiles.Rows = 2;

        for (int i = 0; i < count; i++)
        {
            var tile = CreateTile(i);
            _tiles.Add(tile);
            Tiles.Children.Add(tile.Frame);
        }
        for (int i = 0; i < Math.Min(_keep.Count, count); i++) Assign(_tiles[i], _keep[i]);
        SelectTile(_tiles[0]);
        UpdateWarning();
    }

    Tile CreateTile(int index)
    {
        var s = SettingsStore.Current;
        var t = new Tile { Index = index };
        t.Player = new MediaPlayer(_vlc)
        {
            EnableHardwareDecoding = s.HardwareDecoding,
            EnableKeyInput = false,
            EnableMouseInput = false,
            Mute = true,
        };

        // What sits over the video (lives in the video's own overlay window).
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.Hand };
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && t.Row != null) { WatchFullScreen(t); e.Handled = true; return; }
            SelectTile(t);
            if (t.Row == null || t.Failed) PickChannel(t);
        };

        t.Status = new TextBlock { Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
                                   VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                                   Margin = new Thickness(20), Visibility = Visibility.Collapsed };
        overlay.Children.Add(t.Status);

        var add = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        add.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 30,
                                         Foreground = (Brush)FindResource("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        add.Children.Add(new TextBlock { Text = $"Add channel  ({index + 1})", Foreground = (Brush)FindResource("MutedBrush"),
                                         FontSize = 13, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        t.AddPrompt = add;
        overlay.Children.Add(add);

        // Bottom: number, channel and what's on.
        var info = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(12, 18, 12, 10), Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 0, 0, 0), 90),
        };
        var infoStack = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        t.Number = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD)), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
        t.Name = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        line.Children.Add(t.Number);
        line.Children.Add(t.Name);
        t.Now = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCF, 0xDB)), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        infoStack.Children.Add(line);
        infoStack.Children.Add(t.Now);
        info.Child = infoStack;
        t.Info = info;
        overlay.Children.Add(info);

        // Top right: sound indicator + change + remove.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                                     VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
        t.SoundIcon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Foreground = Brushes.White,
                                      VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed,
                                      ToolTip = "Playing this tile's sound" };
        tools.Children.Add(t.SoundIcon);
        tools.Children.Add(ToolButton("", "Change channel (Enter)", () => { SelectTile(t); PickChannel(t); }));
        tools.Children.Add(ToolButton("", "Watch full screen (F)", () => { if (t.Row != null) WatchFullScreen(t); }));
        tools.Children.Add(ToolButton("", "Remove (Del)", () => Clear(t)));
        overlay.Children.Add(tools);

        t.View = new VideoView { Background = Brushes.Black, Content = overlay };
        t.View.Loaded += (_, _) => { if (t.View.MediaPlayer == null) t.View.MediaPlayer = t.Player; };

        t.Frame = new Border { BorderThickness = new Thickness(2), BorderBrush = IdleBrush, Background = Brushes.Black, Child = t.View };

        // Playback events → tile status, and fall back to another version of a merged channel on failure.
        t.Player.Playing += (_, _) => Dispatcher.InvokeAsync(() => t.Status.Visibility = Visibility.Collapsed);
        t.Player.EncounteredError += (_, _) => Dispatcher.InvokeAsync(() => OnTileFailed(t));
        t.Player.EndReached += (_, _) => Dispatcher.InvokeAsync(() => OnTileFailed(t));
        return t;
    }

    Button ToolButton(string glyph, string tip, Action click)
    {
        var b = new Button { Style = (Style)FindResource("OverlayIconButton"), Content = glyph, ToolTip = tip, Width = 32, Height = 32, FontSize = 13,
                             Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Margin = new Thickness(2, 0, 0, 0) };
        b.Click += (_, e) => { click(); e.Handled = true; };
        return b;
    }

    // ------------------------------------------------------------------ tiles

    void Assign(Tile t, GuideRow row)
    {
        t.Row = row;
        t.Failed = false;
        // Smaller tiles don't need 4K: cap quality to save connections' bandwidth, memory and battery.
        int cap = Math.Min(ChannelMerger.CurrentMaxRank(), _layout >= 6 ? 2 : 3);
        t.Variants = ChannelMerger.Order(row.Variants, cap);
        t.VariantIdx = 0;
        UpdateTileInfo(t);
        PlayTile(t);
        UpdateWarning();
    }

    void PlayTile(Tile t)
    {
        if (t.VariantIdx >= t.Variants.Count) return;
        var url = DataService.Instance.StreamUrl(t.Variants[t.VariantIdx]);
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) { ShowStatus(t, "This channel can't be played."); return; }
        ShowStatus(t, "Tuning…");
        var cache = Math.Clamp(SettingsStore.Current.NetworkCachingMs, 300, 10000);
        var player = t.Player;
        bool mute = t != _focused;
        int vol = SettingsStore.Current.Volume;
        Task.Run(() =>
        {
            try
            {
                using var media = new Media(_vlc, uri, $":network-caching={cache}", $":live-caching={cache}", ":adaptive-maxbuffer=8000");
                player.Play(media);
                player.Mute = mute;
                player.Volume = vol;
            }
            catch (Exception ex) { Log.Error(ex); }
        });
    }

    void OnTileFailed(Tile t)
    {
        if (_closing || t.Row == null) return;
        if (t.VariantIdx + 1 < t.Variants.Count)
        {
            t.VariantIdx++;
            ShowStatus(t, "Trying another version…");
            PlayTile(t);
            return;
        }
        var acc = DataService.Instance.Catalog.Account;
        int active = _tiles.Count(x => x.Row != null);
        ShowStatus(t, acc != null && acc.MaxConnections > 0 && active > acc.MaxConnections
            ? $"Couldn't play – your plan allows {acc.MaxConnections} stream{(acc.MaxConnections == 1 ? "" : "s")} at once.\nRemove a tile, then click here to retry."
            : "Couldn't play this channel.\nClick to choose another.");
        t.Failed = true; // a click on the tile now opens the channel picker
    }

    void ShowStatus(Tile t, string text)
    {
        t.Status.Text = text;
        t.Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateTileInfo(Tile t)
    {
        bool has = t.Row != null;
        t.AddPrompt.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        t.Info.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) { ShowStatus(t, null); return; }
        t.Number.Text = t.Row.Number;
        t.Name.Text = t.Row.DisplayName;
        var p = DataService.Instance.Epg.At(_guide.EpgIdOf(t.Row), DateTime.Now);
        t.Now.Text = p == null ? "" : $"{p.Title}  ·  until {p.End:HH:mm}";
    }

    void Clear(Tile t)
    {
        t.Row = null;
        t.Variants.Clear();
        var player = t.Player;
        Task.Run(() => { try { player.Stop(); } catch { } });
        UpdateTileInfo(t);
        UpdateWarning();
    }

    void SelectTile(Tile t)
    {
        if (t == null) return;
        _focused = t;
        int vol = SettingsStore.Current.Volume;
        foreach (var x in _tiles)
        {
            bool on = x == t;
            x.Frame.BorderBrush = on ? FocusBrush : IdleBrush;
            x.SoundIcon.Visibility = on && x.Row != null ? Visibility.Visible : Visibility.Collapsed;
            var pl = x.Player;
            Task.Run(() => { try { pl.Mute = !on; if (on) pl.Volume = vol; } catch { } });
        }
    }

    void PickChannel(Tile t)
    {
        var dlg = new ChannelPickerWindow(_guide, $"Choose a channel for screen {t.Index + 1}") { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            Assign(t, dlg.Result);
            SelectTile(t);
        }
    }

    void WatchFullScreen(Tile t)
    {
        FocusedRow = t.Row;
        OpenFullScreen = true;
        Close();
    }

    // ------------------------------------------------------------------ connections warning

    void UpdateWarning()
    {
        var acc = DataService.Instance.Catalog.Account;
        int max = acc?.MaxConnections ?? 0;
        int active = _tiles.Count(t => t.Row != null);
        string msg = null;
        bool severe = false;

        if (max > 0 && active > max)
        {
            msg = $"{active} channels are playing but your plan allows {max} stream{(max == 1 ? "" : "s")} at once – some tiles won't play.";
            severe = true;
        }
        else if (max > 0 && _layout > max)
            msg = $"Your plan allows {max} stream{(max == 1 ? "" : "s")} at once, so only {max} of these {_layout} screens can play together.";
        else if (max == 0)
            msg = "Each screen is a separate stream. Check how many your provider allows at once.";

        if (SettingsStore.Current.BatteryAware && PowerService.OnBattery)
            msg = (msg == null ? "" : msg + "  ") + "On battery – multi-view uses a lot of power.";

        WarnPill.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
        WarnText.Text = msg ?? "";
        WarnPill.Background = new SolidColorBrush(severe ? Color.FromRgb(0x4A, 0x16, 0x16) : Color.FromRgb(0x3A, 0x2A, 0x0A));
    }

    // ------------------------------------------------------------------ keys & closing

    void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        if (handled || msg.message != WM_KEYDOWN) return;
        // Only while Multi-view (or one of its tiles' overlay windows) is the active window.
        if (!Application.Current.Windows.OfType<Window>().Any(w => w.IsActive && (w == this || w.Owner == this))) return;
        if (Application.Current.Windows.OfType<ChannelPickerWindow>().Any()) return; // the picker handles its own keys
        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        int digit = key >= Key.D1 && key <= Key.D8 ? key - Key.D1 : key >= Key.NumPad1 && key <= Key.NumPad8 ? key - Key.NumPad1 : -1;
        if (digit >= 0 && digit < _tiles.Count) { SelectTile(_tiles[digit]); handled = true; return; }
        switch (key)
        {
            case Key.Escape: Close(); handled = true; break;
            case Key.Enter: if (_focused != null) PickChannel(_focused); handled = true; break;
            case Key.F: if (_focused?.Row != null) WatchFullScreen(_focused); handled = true; break;
            case Key.Delete: if (_focused != null) Clear(_focused); handled = true; break;
            case Key.Right: MoveFocus(1); handled = true; break;
            case Key.Left: MoveFocus(-1); handled = true; break;
        }
    }

    void MoveFocus(int d)
    {
        if (_tiles.Count == 0) return;
        int i = _focused == null ? 0 : (_focused.Index + d + _tiles.Count) % _tiles.Count;
        SelectTile(_tiles[i]);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        FocusedRow ??= _focused?.Row;
        ReleaseTiles();
    }

    /// <summary>Stops and frees every tile's player (off the UI thread so closing never stalls).</summary>
    void ReleaseTiles()
    {
        var players = _tiles.Select(t => t.Player).ToList();
        foreach (var t in _tiles)
        {
            try { t.View.MediaPlayer = null; t.View.Dispose(); } catch { }
        }
        _tiles.Clear();
        Tiles.Children.Clear();
        _focused = null;
        Task.Run(() =>
        {
            foreach (var p in players)
            {
                try { p.Stop(); p.Dispose(); } catch (Exception ex) { Log.Error(ex); }
            }
        });
    }
}
