using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus;

// Live TV timeline: pause, rewind through the buffer, go further back with the provider's catch-up,
// and scrub forward again up to live.
public partial class MainWindow
{
    bool _liveSeeking;
    DateTime _seekRangeStartUtc, _seekRangeEndUtc;

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

    DateTime EarliestUtc()
    {
        var c = ArchiveChannel;
        if (c != null) return ArchiveOldestUtc(c);
        return _svc?.CanTimeshift == true ? _svc.BufferOldestUtc : DateTime.UtcNow;
    }

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

    void PlayArchiveAt(Channel c, DateTime targetUtc)
    {
        var now = DateTime.UtcNow;
        var oldest = ArchiveOldestUtc(c);
        if (targetUtc < oldest) { targetUtc = oldest; ShowToast("That's as far back as catch-up goes on this channel."); }
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

    void GoLiveNow()
    {
        if (_svc == null || !_nowLive) return;
        if (_svc.Source == LiveSource.Archive) { PlayVariant(); return; }
        _svc.GoLive();
    }

    public void PlayFromArchive(Channel ch, DateTime startLocal)
    {
        var row = _guide.RowFor(ch.Id);
        var identity = row?.Channel ?? ch;
        if (_nowLive && _nowChannel?.Id == identity.Id && _currentUrl != null)
        {
            SeekLiveTo(startLocal.ToUniversalTime());
            if (!_playerMode) EnterPlayer();
            return;
        }
        _startArchiveAtUtc = startLocal.ToUniversalTime();
        PlayLive(ch, true);
    }

    DateTime? _startArchiveAtUtc;

    bool TryStartPendingArchive(Channel archive)
    {
        if (_startArchiveAtUtc is not DateTime at) return false;
        _startArchiveAtUtc = null;
        if (at >= DateTime.UtcNow.AddMinutes(-1)) return false;
        PlayArchiveAt(archive, at);
        return true;
    }

    void UpdateTimeshiftUi()
    {
        bool seekable = LiveSeekable;
        var behind = seekable ? _svc.BehindLive : TimeSpan.Zero;
        bool paused = _svc?.Player.State == LibVLCSharp.Shared.VLCState.Paused;
        bool atLive = behind < TimeSpan.FromSeconds(4) && !paused;
        BackSeekButton.IsVisible = FwdSeekButton.IsVisible = seekable;
        GoLiveButton.IsVisible = seekable && !atLive;
        LiveSeek.IsVisible = seekable;
        LiveProgress.IsVisible = !seekable;
        if (atLive)
        {
            LiveBadgeText.Text = "LIVE";
            LiveBadge.Background = Res("LiveBrush");
            var c = ArchiveChannel;
            ToolTip.SetTip(LiveBadge, c != null ? $"Live  ·  catch-up goes back {DataService.CatchupDays(c)} days – drag the bar or press ←"
                : seekable ? $"Live  ·  you can rewind up to {Math.Floor(_svc.Buffered.TotalMinutes):0} min" : "Live");
        }
        else
        {
            LiveBadgeText.Text = "−" + (behind.TotalHours >= 1 ? $"{(int)behind.TotalHours}:{behind:mm\\:ss}" : behind.ToString(@"m\:ss"));
            LiveBadge.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            ToolTip.SetTip(LiveBadge, _svc?.Source == LiveSource.Archive ? "Catch-up – press End or \"Go live\" to return to live" : "Behind live – press End or \"Go live\" to catch up");
        }
        if (seekable && !_liveSeeking) UpdateSeekBar();
    }

    bool _settingSeek;

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
        _settingSeek = true;
        LiveSeek.Maximum = (end - start).TotalSeconds;
        LiveSeek.Value = Math.Clamp((pos - start).TotalSeconds, 0, LiveSeek.Maximum);
        _settingSeek = false;
        LiveTimes.Text = p != null ? $"{pos.ToLocalTime():HH:mm}  ·  {p.Start:HH:mm} – {p.End:HH:mm}" : $"{pos.ToLocalTime():HH:mm}";
    }

    void LiveSeek_Down()
    {
        _liveSeeking = true;
        _hideTimer.Stop();
    }

    void LiveSeek_Up()
    {
        if (!_liveSeeking) return;
        _liveSeeking = false;
        var target = _seekRangeStartUtc.AddSeconds(LiveSeek.Value);
        if (_seekRangeEndUtc >= DateTime.UtcNow.AddSeconds(-5) && LiveSeek.Value >= LiveSeek.Maximum - 3) GoLiveNow();
        else SeekLiveTo(target);
        if (_playerMode) _hideTimer.Start();
    }

    void LiveSeek_ValueChanged()
    {
        if (!_liveSeeking || _settingSeek) return;
        var t = _seekRangeStartUtc.AddSeconds(LiveSeek.Value);
        bool live = _seekRangeEndUtc >= DateTime.UtcNow.AddSeconds(-5) && LiveSeek.Value >= LiveSeek.Maximum - 3;
        LiveTimes.Text = live ? "Go to LIVE" : $"Go to {t.ToLocalTime():HH:mm:ss}";
    }

    void GoLive_Click(object sender, RoutedEventArgs e) => GoLiveNow();

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
        var s = SettingsStore.Current;
        var menu = NewMenu();
        MenuItem Sub(string header) { var m = new MenuItem { Header = header }; menu.Items.Add(m); return m; }

        var aspect = Sub("Aspect ratio");
        foreach (var (label, value) in new[] { ("As broadcast", (string)null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9 (ultrawide)", "21:9") })
            Add(aspect, label, () => { s.AspectRatio = value; SavePicture(value == null ? "Aspect ratio: as broadcast" : $"Aspect ratio: {label}"); }, s.AspectRatio == value);

        var crop = Sub("Zoom & crop");
        foreach (var (label, value) in new[]
                 {
                     ("Off", (string)null), ("Fill the screen (crops the edges)", "fill"),
                     ("Remove black bars – 16:9", "16:9"), ("Remove black bars – cinema (2.35:1)", "235:100"),
                 })
            Add(crop, label, () => { s.Crop = value; SavePicture(value == null ? "Crop off" : label); }, s.Crop == value);

        menu.Items.Add(new Separator());
        long ad = _svc.AudioDelayMs;
        var audio = Sub($"Audio sync ({Ms(ad)})");
        foreach (var ms in new long[] { -500, -300, -200, -100, 0, 100, 200, 300, 500 })
        {
            var m = ms;
            Add(audio, ms == 0 ? "In sync (0 ms)" : ms < 0 ? $"Sound earlier by {-ms} ms" : $"Sound later by {ms} ms",
                () => { _svc.SetAudioDelay(m); ShowToast($"Audio sync: {Ms(m)}"); }, ad == ms);
        }
        long sd = _svc.SubtitleDelayMs;
        var subs = Sub($"Subtitle timing ({Ms(sd)})");
        foreach (var ms in new long[] { -2000, -1000, -500, 0, 500, 1000, 2000 })
        {
            var m = ms;
            Add(subs, ms == 0 ? "No change" : ms < 0 ? $"Show {-ms / 1000.0:0.#} s earlier" : $"Show {ms / 1000.0:0.#} s later",
                () => { _svc.SetSubtitleDelay(m); ShowToast($"Subtitle timing: {Ms(m)}"); }, sd == ms);
        }
        menu.Items.Add(new Separator());
        var night = new MenuItem { Header = "Night mode – even out loud and quiet sounds", ToggleType = MenuItemToggleType.CheckBox, IsChecked = s.NightMode };
        night.Click += (_, _) => SetNightMode(!s.NightMode);
        menu.Items.Add(night);
        AddPictureFixMenu(menu);
        OpenMenu(menu, PictureButton);
    }

    static string Ms(long ms) => ms == 0 ? "0 ms" : (ms > 0 ? "+" : "−") + Math.Abs(ms) + " ms";

    void SavePicture(string toast)
    {
        UpdateFillRatio();
        SettingsStore.Save();
        _svc?.ApplyPicture();
        ShowToast(toast);
    }

    void UpdateFillRatio()
    {
        if (_svc == null) return;
        double w = Video.Bounds.Width, h = Video.Bounds.Height;
        if (w < 50 || h < 50) return;
        _svc.FillRatio = $"{Math.Round(w)}:{Math.Round(h)}";
    }

    void SetNightMode(bool on)
    {
        var s = SettingsStore.Current;
        s.NightMode = on;
        SettingsStore.Save();
        ShowToast(on ? "Night mode on – loud and quiet sounds evened out" : "Night mode off");
        if (_nowLive) PlayVariant();
        else if (_vod != null) { var v = _vod; PlayVod(v, resume: true); }
    }

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
        if (_nowLive) PlayVariant();
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
            ToggleType = MenuItemToggleType.CheckBox, IsChecked = cur.Deinterlace,
        };
        deint.Click += (_, _) => SetPictureFor(ch, cur with { Deinterlace = !cur.Deinterlace },
            !cur.Deinterlace ? "Interlacing lines removed on this channel" : "Interlacing back to automatic on this channel");
        fix.Items.Add(deint);
        fix.Items.Add(new Separator());
        Add(fix, "Graphics card – normal", () => SetPictureFor(ch, cur with { Decoder = null }, "Normal decoding on this channel"), cur.Decoder == null);
        Add(fix, "Software decoding (most compatible, uses more CPU)", () => SetPictureFor(ch, cur with { Decoder = "software" }, "Software decoding on this channel"), cur.Decoder == "software");
        fix.Items.Add(new Separator());
        fix.Items.Add(new MenuItem { Header = "Dingus remembers this for the channel.", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(fix);
    }
}

// Parental lock button in the top bar.
public partial class MainWindow
{
    void UpdateLockButton()
    {
        LockButton.IsVisible = Parental.HasPin;
        LockButton.Content = Parental.Unlocked ? Icons.Unlock : Icons.Lock;
        ToolTip.SetTip(LockButton, Parental.Unlocked
            ? "Locked categories are showing – click to lock them again"
            : "Locked categories are hidden – click and enter your PIN to show them");
    }

    async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (Parental.Unlocked) { Parental.Lock(); ShowToast("Locked."); }
        else if (await Parental.TryUnlockAsync(this)) ShowToast("Unlocked until you close Dingus.");
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
        InfoWatching.IsVisible = watching;
        InfoNow.Text = p?.Title ?? "No guide information";
        InfoNowTime.Text = p != null ? $"{p.Start:HH:mm} – {p.End:HH:mm}  ·  {Math.Max(1, (int)Math.Ceiling((p.End - now).TotalMinutes))} min left" : "";
        InfoProgress.Value = p != null ? (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds) : 0;
        InfoProgress.Opacity = p != null ? 1 : 0;
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
        if (_nowChannel != null && _guide.RowFor(_nowChannel.Id) == row) return;
        _guide.SelectChannel(row.Channel.Id);
        PlayLive(row.Channel, false);
        UpdateChromeText(row.Channel);
    }

    void InfoBar_PointerReleased(object sender, PointerReleasedEventArgs e) { TuneBrowse(); e.Handled = true; }

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
        CloseBrowse();
        return false;
    }
}

// Recording from the player.
public partial class MainWindow
{
    public string EpgIdForRow(Views.GuideRow row) => row == null ? null : _guide.EpgIdOf(row);

    void Record_Click(object sender, RoutedEventArgs e) => RecordCurrent();

    void RecordCurrent()
    {
        if (!_nowLive || _nowChannel == null) return;
        Views.RecordUi.RecordNow(_guide.RowFor(_nowChannel.Id));
        UpdateRecordButton();
    }

    void UpdateRecordButton()
    {
        if (RecordButton == null) return;
        RecordButton.IsVisible = _nowLive;
        var row = _nowChannel == null ? null : _guide.RowFor(_nowChannel.Id);
        var p = row == null ? null : DataService.Instance.Epg.At(_guide.EpgIdOf(row), DateTime.Now);
        bool on = row != null && p != null && RecordingService.IsScheduled(row.Channel.Id, p.Start);
        RecordButton.Foreground = on ? Res("LiveBrush") : Brushes.White;
        ToolTip.SetTip(RecordButton, on ? "Recording this programme – click to stop (R)" : "Record this programme (R)");
    }
}

// Hooks for the self-test (dingus --self-test <folder>).
public partial class MainWindow
{
    internal Control SelfTestRoot => Root;
    internal object SelfTestPage => _page;
    internal Views.GuideView SelfTestGuide => _guide;
    internal void SelfTestShortcuts(bool open) { if (open != SheetOpen) ToggleShortcutSheet(); }
    internal void SelfTestSearch(string text) => SearchBox.Text = text;
    internal void SelfTestSearchFilter(string f) => _search.SelfTestFilter(f);
    internal PlayerService SelfTestPlayer => _svc;
    internal Control SelfTestOverlay => Overlay;
    internal bool SelfTestSplashShowing => SplashUp;
    internal void SelfTestMini(bool on) { if (on) EnterMini(); else ExitMini(true); }
    internal void SelfTestMiniControls() { MiniChrome.IsVisible = true; _hideTimer.Stop(); SlideMiniVolume(open: true); UpdateMiniInfo(); }
    internal (double Player, double Mini) SelfTestVolumes => (VolumeSlider.Value, MiniVolumeSlider.Value);
    internal void SelfTestSetVolume(double v) => VolumeSlider.Value = v;
    internal void SelfTestSplash(bool show)
    {
        if (show) { ShowSplash(withAdd: true); return; }
        _splashGone = true; _splashOff?.Stop(); Splash.IsVisible = false; SplashAdd.IsVisible = false;
    }
    internal void SelfTestPlay(string url) => PlayVod(new VodItem { Kind = "movie", Title = "Test video", Subtitle = "Dingus self-test", Url = url }, resume: false);
    internal void SelfTestExitPlayer() => ExitPlayer();
}

public partial class MainWindow
{
    void MultiView_Click(object sender, RoutedEventArgs e) => OpenMultiView(null);
}
