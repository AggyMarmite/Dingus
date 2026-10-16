using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dingus;

// Keyboard shortcut sheet (? / F1) and the sleep timer.
public partial class MainWindow
{
    // =====================================================================================
    // Keyboard shortcut sheet
    // =====================================================================================

    static readonly (string Keys, string What)[] Shortcuts =
    {
        ("0 – 9", "Type a channel number"),
        ("0 / Backspace", "Last channel you watched"),
        ("↑  ↓", "Channel up / down (films: volume)"),
        ("←  →", "Films: back 10 s / forward 30 s"),
        ("Enter / L", "Channel list – what's on now"),
        ("Space", "Play / pause"),
        ("M", "Mute  ·  mouse wheel = volume"),
        ("F / F11", "Full screen on / off"),
        ("Esc", "Back to the guide"),
        ("P", "Picture-in-picture"),
        ("V", "Multi-view (4 / 6 / 8 channels)"),
        ("I", "Stream info"),
        ("S", "Sleep timer"),
        ("N", "Next episode"),
        ("Home", "Start a film from the beginning"),
        ("? / F1", "This list"),
    };

    bool SheetOpen => ShortcutSheet.Visibility == Visibility.Visible || OverlaySheet.Visibility == Visibility.Visible;

    void ToggleShortcutSheet()
    {
        if (SheetOpen) { CloseShortcutSheet(); return; }
        var (sheet, grid) = _playerMode ? (OverlaySheet, OverlaySheetGrid) : (ShortcutSheet, ShortcutSheetGrid);
        if (grid.Children.Count == 0)
            foreach (var (keys, what) in Shortcuts)
            {
                var row = new DockPanel { Margin = new Thickness(0, 5, 28, 5) };
                var key = new Border
                {
                    MinWidth = 120,
                    Child = new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 2, 8, 3),
                        Child = new TextBlock { Text = keys, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold },
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
        sheet.Visibility = Visibility.Visible;
    }

    void CloseShortcutSheet()
    {
        ShortcutSheet.Visibility = Visibility.Collapsed;
        OverlaySheet.Visibility = Visibility.Collapsed;
    }

    void Sheet_MouseDown(object sender, MouseButtonEventArgs e) { CloseShortcutSheet(); e.Handled = true; }

    static bool IsSheetKey(Key k) => k == Key.F1 || k == Key.OemQuestion;

    // =====================================================================================
    // Sleep timer
    // =====================================================================================

    DispatcherTimer _sleepTimer;
    DateTime? _sleepAt;
    bool _sleepWarned;

    void Sleep_Click(object sender, RoutedEventArgs e) => ShowSleepMenu();

    void ShowSleepMenu()
    {
        var menu = new ContextMenu { PlacementTarget = SleepButton, Placement = PlacementMode.Top };
        if (_sleepAt is DateTime at)
            menu.Items.Add(new MenuItem { Header = $"Stops at {at:HH:mm} ({Remaining(at)})", IsEnabled = false });

        void Add(string text, Func<DateTime?> when, bool isChecked = false)
        {
            var item = new MenuItem { Header = text, IsChecked = isChecked };
            item.Click += (_, _) => SetSleep(when());
            menu.Items.Add(item);
        }

        Add("Off", () => null, _sleepAt == null);
        foreach (var m in new[] { 15, 30, 60, 90 })
            Add($"{m} minutes", () => DateTime.Now.AddMinutes(m));
        if (_nowLive && _nowChannel != null && _guide.NowOn(_nowChannel) is { } p && p.End > DateTime.Now)
            Add($"End of this programme ({p.End:HH:mm})", () => p.End.AddMinutes(1));
        else if (!_nowLive && _svc?.Player is { Length: > 0 } mp)
        {
            var left = mp.Length - mp.Time;
            if (left > 60_000) Add("End of this film / episode", () => DateTime.Now.AddMilliseconds(left + 15_000));
        }

        _hideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; if (_playerMode) _hideTimer.Start(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    void SetSleep(DateTime? at)
    {
        _sleepAt = at;
        _sleepWarned = false;
        if (at == null)
        {
            _sleepTimer?.Stop();
            ShowToast("Sleep timer off");
        }
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
        if (_currentUrl == null) { SetSleepOffQuietly(); return; } // nothing playing any more
        var left = at - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            SetSleepOffQuietly();
            if (_mini) ExitMini(false);
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
        SleepButton.Foreground = on ? (Brush)FindResource("AccentBrush") : Brushes.White;
        SleepButton.ToolTip = on ? $"Sleep timer – stops at {_sleepAt:HH:mm} ({Remaining(_sleepAt.Value)}) (S)" : "Sleep timer (S)";
    }

    static string Remaining(DateTime at)
    {
        var m = (int)Math.Ceiling((at - DateTime.Now).TotalMinutes);
        return m <= 1 ? "1 min left" : m < 60 ? $"{m} min left" : $"{m / 60} h {m % 60:00} min left";
    }
}

// Live pause & rewind controls.
public partial class MainWindow
{
    void UpdateTimeshiftUi()
    {
        bool ts = _svc?.CanTimeshift == true;
        var behind = ts ? _svc.BehindLive : TimeSpan.Zero;
        bool atLive = behind < TimeSpan.FromSeconds(4) && _svc?.Player.State != LibVLCSharp.Shared.VLCState.Paused;

        var seekVis = ts ? Visibility.Visible : Visibility.Collapsed;
        if (BackSeekButton.Visibility != seekVis)
        {
            BackSeekButton.Visibility = FwdSeekButton.Visibility = seekVis;
            BackSeekButton.ToolTip = "Back 10 seconds (←)";
            FwdSeekButton.ToolTip = "Forward 10 seconds (→)";
        }
        GoLiveButton.Visibility = ts && !atLive ? Visibility.Visible : Visibility.Collapsed;
        if (atLive)
        {
            LiveBadgeText.Text = "LIVE";
            LiveBadge.Background = (System.Windows.Media.Brush)FindResource("LiveBrush");
            LiveBadge.ToolTip = ts ? $"Live  ·  you can rewind up to {Math.Floor(_svc.Buffered.TotalMinutes):0} min" : "Live";
        }
        else
        {
            LiveBadgeText.Text = "−" + (behind.TotalHours >= 1 ? behind.ToString(@"h\:mm\:ss") : behind.ToString(@"m\:ss"));
            LiveBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            LiveBadge.ToolTip = "Behind live – press End or \"Go live\" to catch up";
        }
    }

    void GoLive_Click(object sender, RoutedEventArgs e) => _svc?.GoLive();
}

// Picture & sound menu in the player.
public partial class MainWindow
{
    void Picture_Click(object sender, RoutedEventArgs e) => ShowPictureMenu();

    void ShowPictureMenu()
    {
        if (_svc == null) return;
        var s = Services.SettingsStore.Current;
        var menu = new ContextMenu { PlacementTarget = PictureButton, Placement = PlacementMode.Top };

        MenuItem Sub(string header) { var m = new MenuItem { Header = header }; menu.Items.Add(m); return m; }
        void Opt(MenuItem parent, string text, bool isChecked, Action click)
        {
            var mi = new MenuItem { Header = text, IsChecked = isChecked };
            mi.Click += (_, _) => click();
            parent.Items.Add(mi);
        }

        var aspect = Sub("Aspect ratio");
        foreach (var (label, value) in new[] { ("As broadcast", (string)null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9 (ultrawide)", "21:9") })
            Opt(aspect, label, s.AspectRatio == value, () => { s.AspectRatio = value; SavePicture(label == "As broadcast" ? "Aspect ratio: as broadcast" : $"Aspect ratio: {label}"); });

        var crop = Sub("Zoom & crop");
        foreach (var (label, value) in new[]
                 {
                     ("Off", (string)null), ("Fill the screen (crops the edges)", "fill"),
                     ("Remove black bars – 16:9", "16:9"), ("Remove black bars – cinema (2.35:1)", "235:100"),
                 })
            Opt(crop, label, s.Crop == value, () => { s.Crop = value; SavePicture(value == null ? "Crop off" : label); });

        menu.Items.Add(new Separator());

        long ad = _svc.AudioDelayMs;
        var audio = Sub($"Audio sync ({Ms(ad)})");
        foreach (var ms in new long[] { -500, -300, -200, -100, 0, 100, 200, 300, 500 })
            Opt(audio, ms == 0 ? "In sync (0 ms)" : ms < 0 ? $"Sound earlier by {-ms} ms" : $"Sound later by {ms} ms", ad == ms,
                () => { _svc.SetAudioDelay(ms); ShowToast($"Audio sync: {Ms(ms)}"); });

        long sd = _svc.SubtitleDelayMs;
        var subs = Sub($"Subtitle timing ({Ms(sd)})");
        foreach (var ms in new long[] { -2000, -1000, -500, 0, 500, 1000, 2000 })
            Opt(subs, ms == 0 ? "No change" : ms < 0 ? $"Show {-ms / 1000.0:0.#} s earlier" : $"Show {ms / 1000.0:0.#} s later", sd == ms,
                () => { _svc.SetSubtitleDelay(ms); ShowToast($"Subtitle timing: {Ms(ms)}"); });

        menu.Items.Add(new Separator());
        var night = new MenuItem { Header = "Night mode – even out loud and quiet sounds", IsCheckable = true, IsChecked = s.NightMode };
        night.Click += (_, _) => SetNightMode(!s.NightMode);
        menu.Items.Add(night);

        _hideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; if (_playerMode) _hideTimer.Start(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    static string Ms(long ms) => ms == 0 ? "0 ms" : (ms > 0 ? "+" : "−") + Math.Abs(ms) + " ms";

    void SavePicture(string toast)
    {
        UpdateFillRatio();
        Services.SettingsStore.Save();
        _svc?.ApplyPicture();
        ShowToast(toast);
    }

    /// <summary>"Fill the screen" crops the video to the shape of the player.</summary>
    void UpdateFillRatio()
    {
        if (_svc == null) return;
        double w = Video.ActualWidth, h = Video.ActualHeight;
        if (w < 50 || h < 50) return;
        _svc.FillRatio = $"{Math.Round(w)}:{Math.Round(h)}";
    }

    void SetNightMode(bool on)
    {
        var s = Services.SettingsStore.Current;
        s.NightMode = on;
        Services.SettingsStore.Save();
        ShowToast(on ? "Night mode on – loud and quiet sounds evened out" : "Night mode off");
        // The sound filter is set when a stream opens, so re-open what's playing.
        if (_nowLive) PlayVariant();
        else if (_vod != null) { var v = _vod; PlayVod(v, resume: true); }
    }
}

// Parental lock button in the top bar.
public partial class MainWindow
{
    void UpdateLockButton()
    {
        bool pin = Services.Parental.HasPin;
        LockButton.Visibility = pin ? Visibility.Visible : Visibility.Collapsed;
        LockButton.Content = Services.Parental.Unlocked ? "\uE785" : "\uE72E";
        LockButton.ToolTip = Services.Parental.Unlocked
            ? "Locked categories are showing – click to lock them again"
            : "Locked categories are hidden – click and enter your PIN to show them";
    }

    void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (Services.Parental.Unlocked) { Services.Parental.Lock(); ShowToast("Locked."); }
        else if (Services.Parental.TryUnlock(this)) ShowToast("Unlocked until you close Dingus.");
    }
}
