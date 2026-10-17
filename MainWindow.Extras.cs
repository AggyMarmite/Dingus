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
        ("↑  ↓", "Live TV: what's on other channels (Enter to watch) · films: volume"),
        ("PgUp / PgDn", "Channel up / down"),
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
        ("Ctrl + F", "Search everything"),
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
        Motion.FadeIn(sheet, fromY: 12, ms: 200);
    }

    void CloseShortcutSheet()
    {
        Motion.FadeOut(ShortcutSheet, toY: 8);
        Motion.FadeOut(OverlaySheet, toY: 8);
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
        var row = _nowChannel == null ? null : _guide.RowFor(_nowChannel.Id);
        var p = row == null ? null : DataService.Instance.Epg.At(_guide.EpgIdOf(row), pos.ToLocalTime());
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
        var row = _guide.RowFor(_nowChannel.Id);
        var p = row == null ? null : DataService.Instance.Epg.At(_guide.EpgIdOf(row), LivePositionUtc.ToLocalTime());
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
        AddPictureFixMenu(menu);

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
    internal void SelfTestSearchFilter(string f) => _search.SelfTestFilter(f);
}

// Per-channel picture fixes (picture & sound menu → "Picture fix for this channel").
public partial class MainWindow
{
    /// <summary>The key a channel's picture fix is saved under: the guide row, so every version shares it.</summary>
    string PictureKey(Channel c) => c == null ? null : (_guide.RowFor(c.Id)?.Channel.Id ?? c.Id);

    ChannelPicture PictureFor(Channel c)
    {
        var p = DataService.Instance.Active;
        var key = PictureKey(c);
        if (p == null || key == null || !p.PictureFixes.TryGetValue(key, out var v)) return null;
        var parts = v.Split('|');
        var dec = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null;
        bool deint = parts.Length > 1 && parts[1] == "1";
        return new ChannelPicture(dec, deint);
    }

    void SetPictureFor(Channel c, ChannelPicture pic, string toast)
    {
        var p = DataService.Instance.Active;
        var key = PictureKey(c);
        if (p == null || key == null) return;
        if (pic == null || pic.IsDefault) p.PictureFixes.Remove(key);
        else p.PictureFixes[key] = $"{pic.Decoder}|{(pic.Deinterlace ? "1" : "0")}";
        SettingsStore.Save();
        ShowToast(toast);
        if (_nowLive) PlayVariant(); // reopen the channel so the change takes effect
    }

    void AddPictureFixMenu(ContextMenu menu)
    {
        if (!_nowLive || _nowChannel == null) return;
        var ch = _nowChannel;
        var cur = PictureFor(ch) ?? new ChannelPicture(null, false);
        var fix = new MenuItem { Header = "Picture glitches on this channel?" };

        var deint = new MenuItem
        {
            Header = "Always remove interlacing lines (for comb-like lines on movement)",
            IsCheckable = true, IsChecked = cur.Deinterlace,
        };
        deint.Click += (_, _) => SetPictureFor(ch, cur with { Deinterlace = !cur.Deinterlace },
            !cur.Deinterlace ? "Interlacing lines removed on this channel" : "Interlacing back to automatic on this channel");
        fix.Items.Add(deint);
        fix.Items.Add(new Separator());

        void Dec(string label, string value, string toast)
        {
            var mi = new MenuItem { Header = label, IsChecked = cur.Decoder == value };
            mi.Click += (_, _) => SetPictureFor(ch, cur with { Decoder = value }, toast);
            fix.Items.Add(mi);
        }
        Dec("Graphics card – normal", null, "Normal decoding on this channel");
        Dec("Graphics card – alternative decoder (for blocky or green glitches)", "dxva2", "Trying the alternative graphics decoder on this channel");
        Dec("Software decoding (most compatible, uses more CPU)", "software", "Software decoding on this channel");
        fix.Items.Add(new Separator());
        fix.Items.Add(new MenuItem { Header = "Dingus remembers this for the channel.", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(fix);
    }
}

// Info bar: ↑ ↓ browse what's on other channels while you keep watching; Enter switches.
public partial class MainWindow
{
    Views.GuideRow _browseRow;
    DispatcherTimer _browseTimer;
    bool Browsing => _browseRow != null;

    void Browse(int delta)
    {
        var current = _nowChannel == null ? null : _guide.RowFor(_nowChannel.Id);
        var row = _guide.Peek(_browseRow ?? current, delta);
        if (row == null) return;
        _browseRow = row;
        HideChrome();
        ShowInfoBar(row, row == current);
        if (_browseTimer == null)
        {
            _browseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _browseTimer.Tick += (_, _) => CloseBrowse();
        }
        _browseTimer.Stop();
        _browseTimer.Start();
    }

    void ShowInfoBar(Views.GuideRow row, bool watching)
    {
        var epg = DataService.Instance.Epg;
        var id = _guide.EpgIdOf(row);
        var now = DateTime.Now;
        var p = epg.At(id, now);
        var n = p != null ? epg.StartingFrom(id, p.End) : epg.StartingFrom(id, now);
        InfoName.Text = row.DisplayName;
        InfoInitials.Text = row.Initials;
        ImageLoader.SetUrl(InfoLogo, row.Logo);
        InfoWatching.Visibility = watching ? Visibility.Visible : Visibility.Collapsed;
        InfoNow.Text = p?.Title ?? "No guide information";
        InfoNowTime.Text = p != null ? $"{p.Start:HH:mm} – {p.End:HH:mm}  ·  {Math.Max(1, (int)Math.Ceiling((p.End - now).TotalMinutes))} min left" : "";
        InfoProgress.Value = p != null ? (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds) : 0;
        InfoProgress.Visibility = p != null ? Visibility.Visible : Visibility.Hidden;
        InfoNext.Text = n?.Title ?? "";
        InfoNextTime.Text = n != null ? $"{n.Start:HH:mm}" : "";
        Motion.FadeIn(InfoBar, fromY: 16, ms: 200);
    }

    void CloseBrowse()
    {
        _browseTimer?.Stop();
        _browseRow = null;
        Motion.FadeOut(InfoBar, toY: 12, ms: 160);
    }

    void TuneBrowse()
    {
        var row = _browseRow;
        CloseBrowse();
        if (row == null) return;
        if (_nowChannel != null && _guide.RowFor(_nowChannel.Id) == row) return; // already watching it
        _guide.SelectChannel(row.Channel.Id); // channel up/down then carries on from here
        PlayLive(row.Channel, false);
        UpdateChromeText(row.Channel);
    }

    void InfoBar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { TuneBrowse(); e.Handled = true; }

    /// <summary>Keys while the info bar is open. Returns true if the key was used.</summary>
    bool HandleBrowseKey(Key key)
    {
        switch (key)
        {
            case Key.Up: Browse(-1); return true;
            case Key.Down: Browse(1); return true;
            case Key.Enter: TuneBrowse(); return true;
            case Key.Escape:
            case Key.Back: CloseBrowse(); return true;
        }
        CloseBrowse(); // anything else closes it and does its usual job
        return false;
    }
}
