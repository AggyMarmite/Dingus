using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;

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
        ("←  →", "Back / forward (live TV 10 s, films 10 s / 30 s)"),
        ("End  /  Home", "Live TV: back to live / start of the programme"),
        ("Ctrl + ← →", "Live TV: back / forward 5 minutes"),
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
        ("Home", "Films: start from the beginning"),
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

// Live TV timeline: pause, rewind through the buffer, go further back with the provider's catch-up,
// and scrub forward again up to live.
public partial class MainWindow
{
    bool _liveSeeking;
    DateTime _seekRangeStartUtc, _seekRangeEndUtc;

    /// <summary>The version of the channel that has catch-up (if any).</summary>
    Channel ArchiveChannel
    {
        get
        {
            if (!_nowLive || _nowChannel == null) return null;
            var c = _guide.RowFor(_nowChannel.Id)?.CatchupChannel;
            return c != null && DataService.Instance.Xtream != null && c.Url == null ? c : null;
        }
    }

    DateTime ArchiveOldestUtc(Channel c) => DateTime.UtcNow.AddDays(-DataService.CatchupDays(c)).AddMinutes(5);

    bool LiveSeekable => _nowLive && _svc != null && (_svc.CanTimeshift || _svc.Source == LiveSource.Archive || ArchiveChannel != null);

    DateTime LivePositionUtc => _svc?.PositionUtc ?? DateTime.UtcNow;

    /// <summary>The earliest moment you can go back to on this channel.</summary>
    DateTime EarliestUtc()
    {
        var c = ArchiveChannel;
        if (c != null) return ArchiveOldestUtc(c);
        return _svc?.CanTimeshift == true ? _svc.BufferOldestUtc : DateTime.UtcNow;
    }

    /// <summary>Goes to a moment on the live channel: the rewind buffer if it's there, otherwise catch-up, or live.</summary>
    void SeekLiveTo(DateTime targetUtc)
    {
        if (!_nowLive || _svc == null) return;
        var now = DateTime.UtcNow;
        if (targetUtc >= now.AddSeconds(-4)) { GoLiveNow(); return; }
        if (_svc.CanTimeshift && targetUtc >= _svc.BufferOldestUtc) { _svc.SeekBuffer(targetUtc); return; }

        var c = ArchiveChannel;
        if (c == null)
        {
            if (_svc.CanTimeshift) { _svc.SeekBuffer(_svc.BufferOldestUtc); ShowToast("That's as far back as this channel goes."); }
            return;
        }
        PlayArchiveAt(c, targetUtc);
    }

    /// <summary>Plays the provider's catch-up for a channel from a moment, running on up to live.</summary>
    void PlayArchiveAt(Channel c, DateTime targetUtc)
    {
        var now = DateTime.UtcNow;
        var oldest = ArchiveOldestUtc(c);
        if (targetUtc < oldest) { targetUtc = oldest; ShowToast("That's as far back as catch-up goes on this channel."); }
        // Catch-up starts on a whole minute; it runs up to now so you can carry on watching (or scrub) to live.
        var startUtc = new DateTime(targetUtc.Year, targetUtc.Month, targetUtc.Day, targetUtc.Hour, targetUtc.Minute, 0, DateTimeKind.Utc);
        int minutes = (int)Math.Ceiling((now - startUtc).TotalMinutes) + 5;
        var url = DataService.Instance.CatchupUrlAt(c, startUtc.ToLocalTime(), minutes);
        if (url == null) return;
        _svc.PlayArchive(url, startUtc);
        _lastProgressUtc = DateTime.UtcNow;
        SetBuffering("Loading catch-up…");
    }

    void SeekLiveBy(double seconds)
    {
        if (!LiveSeekable) return;
        SeekLiveTo(LivePositionUtc.AddSeconds(seconds));
    }

    /// <summary>Back to the live picture (re-opens the channel if you were watching catch-up).</summary>
    void GoLiveNow()
    {
        if (_svc == null || !_nowLive) return;
        if (_svc.Source == LiveSource.Archive) { PlayVariant(); return; }
        _svc.GoLive();
    }

    /// <summary>Catch-up from the guide: plays the channel from that moment, on the same timeline as live.</summary>
    public void PlayFromArchive(Channel ch, DateTime startLocal)
    {
        var row = _guide.RowFor(ch.Id);
        var identity = row?.Channel ?? ch;
        if (_nowLive && _nowChannel?.Id == identity.Id && _currentUrl != null)
        {
            // Already on this channel: just move along its timeline (uses the rewind buffer if it reaches).
            SeekLiveTo(startLocal.ToUniversalTime());
            if (!_playerMode) EnterPlayer(true);
            return;
        }
        _startArchiveAtUtc = startLocal.ToUniversalTime();
        PlayLive(ch, true);
    }

    DateTime? _startArchiveAtUtc;

    /// <summary>Called by PlayVariant: if a catch-up start is waiting, start there instead of live.</summary>
    bool TryStartPendingArchive(Channel archive)
    {
        if (_startArchiveAtUtc is not DateTime at) return false;
        _startArchiveAtUtc = null;
        if (at >= DateTime.UtcNow.AddMinutes(-1)) return false; // that's live – just tune in
        PlayArchiveAt(archive, at);
        return true;
    }

    void UpdateTimeshiftUi()
    {
        bool seekable = LiveSeekable;
        var behind = seekable ? _svc.BehindLive : TimeSpan.Zero;
        bool paused = _svc?.Player.State == LibVLCSharp.Shared.VLCState.Paused;
        bool atLive = behind < TimeSpan.FromSeconds(4) && !paused;

        var seekVis = seekable ? Visibility.Visible : Visibility.Collapsed;
        if (BackSeekButton.Visibility != seekVis)
        {
            BackSeekButton.Visibility = FwdSeekButton.Visibility = seekVis;
            BackSeekButton.ToolTip = "Back 10 seconds (←)";
            FwdSeekButton.ToolTip = "Forward 10 seconds (→)";
        }
        GoLiveButton.Visibility = seekable && !atLive ? Visibility.Visible : Visibility.Collapsed;
        LiveSeek.Visibility = seekable ? Visibility.Visible : Visibility.Collapsed;
        LiveProgress.Visibility = seekable ? Visibility.Collapsed : Visibility.Visible;

        if (atLive)
        {
            LiveBadgeText.Text = "LIVE";
            LiveBadge.Background = (System.Windows.Media.Brush)FindResource("LiveBrush");
            var c = ArchiveChannel;
            LiveBadge.ToolTip = c != null ? $"Live  ·  catch-up goes back {DataService.CatchupDays(c)} days – drag the bar or press ←"
                : seekable ? $"Live  ·  you can rewind up to {Math.Floor(_svc.Buffered.TotalMinutes):0} min" : "Live";
        }
        else
        {
            LiveBadgeText.Text = "−" + (behind.TotalHours >= 1 ? $"{(int)behind.TotalHours}:{behind:mm\\:ss}" : behind.ToString(@"m\:ss"));
            LiveBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            LiveBadge.ToolTip = _svc?.Source == LiveSource.Archive ? "Catch-up – press End or \"Go live\" to return to live" : "Behind live – press End or \"Go live\" to catch up";
        }
        if (seekable && !_liveSeeking) UpdateSeekBar();
    }

    /// <summary>The scrub bar covers the programme you're watching, from its start to now (or its end).</summary>
    void UpdateSeekBar()
    {
        var now = DateTime.UtcNow;
        var pos = LivePositionUtc;
        var p = _nowChannel == null ? null : DataService.Instance.Epg.At(_guide.EpgIdOf(_guide.RowFor(_nowChannel.Id)), pos.ToLocalTime());
        DateTime start = p?.Start.ToUniversalTime() ?? pos.AddMinutes(-30);
        DateTime end = p?.End.ToUniversalTime() ?? now;
        if (end > now) end = now;
        var earliest = EarliestUtc();
        if (start < earliest) start = earliest;
        if (end <= start.AddSeconds(30)) end = start.AddSeconds(30);
        _seekRangeStartUtc = start;
        _seekRangeEndUtc = end;
        LiveSeek.Maximum = (end - start).TotalSeconds;
        LiveSeek.Value = Math.Clamp((pos - start).TotalSeconds, 0, LiveSeek.Maximum);
        LiveTimes.Text = p != null ? $"{pos.ToLocalTime():HH:mm}  ·  {p.Start:HH:mm} – {p.End:HH:mm}" : $"{pos.ToLocalTime():HH:mm}";
    }

    void LiveSeek_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _liveSeeking = true;
        _hideTimer.Stop();
    }

    void LiveSeek_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_liveSeeking) return;
        _liveSeeking = false;
        var target = _seekRangeStartUtc.AddSeconds(LiveSeek.Value);
        // Dragged right to the end of a programme that's still on = live.
        if (_seekRangeEndUtc >= DateTime.UtcNow.AddSeconds(-5) && LiveSeek.Value >= LiveSeek.Maximum - 3) GoLiveNow();
        else SeekLiveTo(target);
        if (_playerMode) _hideTimer.Start();
    }

    void LiveSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_liveSeeking) return;
        var t = _seekRangeStartUtc.AddSeconds(LiveSeek.Value);
        bool live = _seekRangeEndUtc >= DateTime.UtcNow.AddSeconds(-5) && LiveSeek.Value >= LiveSeek.Maximum - 3;
        LiveTimes.Text = live ? "Go to LIVE" : $"Go to {t.ToLocalTime():HH:mm:ss}";
    }

    void GoLive_Click(object sender, RoutedEventArgs e) => GoLiveNow();

    /// <summary>Home on live TV: back to the start of the programme you're watching.</summary>
    void StartOfProgramme()
    {
        if (!LiveSeekable || _nowChannel == null) return;
        var p = DataService.Instance.Epg.At(_guide.EpgIdOf(_guide.RowFor(_nowChannel.Id)), LivePositionUtc.ToLocalTime());
        if (p == null) return;
        SeekLiveTo(p.Start.ToUniversalTime());
        ShowToast($"From the start: {p.Title}");
    }
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

// Hooks for the self-test (Dingus.exe --self-test <folder>).
public partial class MainWindow
{
    internal FrameworkElement SelfTestRoot => Root;
    internal object SelfTestPage => _page;
    internal Views.GuideView SelfTestGuide => _guide;
    internal void SelfTestShortcuts(bool open) { if (open != SheetOpen) ToggleShortcutSheet(); }
    internal void SelfTestSearch(string text) => SearchBox.Text = text;
}
