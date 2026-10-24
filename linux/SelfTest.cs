using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dingus.Services;
using Dingus.Views;

namespace Dingus;

/// <summary>
/// "dingus --self-test &lt;folder&gt;": opens every page, saves a screenshot of each and writes result.txt.
/// Run by the GitHub build (on a virtual screen) so layout or start-up problems show up before a release.
/// </summary>
public static class SelfTest
{
    public static string Dir { get; set; }

    /// <summary>Notes what the video engine finds (plugins, a bare start-up and its log) – for diagnosing a new platform.</summary>
    public static void VlcCheck()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine("lib: " + VlcPaths.LibDir);
            var pp = Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH");
            sb.AppendLine("VLC_PLUGIN_PATH: " + pp);
            if (pp != null && Directory.Exists(pp))
            {
                var files = Directory.GetFiles(pp).Select(Path.GetFileName).OrderBy(n => n).ToList();
                sb.AppendLine($"plugins: {files.Count}");
                sb.AppendLine(string.Join(" ", files.Where(n => n.Contains("http") || n.Contains("normvol") || n.Contains("compressor") || n.Contains("avcodec") || n.Contains("vout") || n.Contains("auhal") || n.Contains("filesystem"))));
            }
            using var vlc = new LibVLCSharp.Shared.LibVLC(false, "--verbose=2");
            var log = new List<string>();
            vlc.Log += (_, e) => { lock (log) if (log.Count < 300) log.Add($"{e.Level} {e.Module}: {e.Message}"); };
            sb.AppendLine("version: " + vlc.Version);
            sb.AppendLine("audio outputs: " + string.Join(", ", vlc.AudioOutputs.Select(a => a.Name)));
            sb.AppendLine("video filters: " + vlc.VideoFilters.Length);
            try { using var t = new LibVLCSharp.Shared.LibVLC(false, "--http-reconnect"); sb.AppendLine("--http-reconnect: ok"); }
            catch (Exception ex) { sb.AppendLine("--http-reconnect: " + ex.Message); }
            lock (log) sb.AppendLine(string.Join("\n", log.Take(120)));
        }
        catch (Exception ex) { sb.AppendLine("ERROR " + ex); }
        try { File.WriteAllText(Path.Combine(Dir, "vlc-check.txt"), sb.ToString()); } catch { }
    }
    public static readonly List<string> Errors = new();
    static readonly List<string> Notes = new();

    public static void Error(Exception ex) { lock (Errors) Errors.Add(ex.ToString()); }

    static async Task Idle(int ms = 600)
    {
        await Task.Delay(ms);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    public static async Task RunAsync(MainWindow main)
    {
        Directory.CreateDirectory(Dir);
        try
        {
            main.WindowState = WindowState.Maximized;
            await Idle(500);
            if (main.Screens.Primary is { } scr && main.Bounds.Width < scr.WorkingArea.Width / scr.Scaling - 40)
            {
                // No window manager (the build's virtual screen): size the window to the screen by hand.
                main.WindowState = WindowState.Normal;
                main.Position = scr.WorkingArea.Position;
                main.Width = scr.WorkingArea.Width / scr.Scaling;
                main.Height = scr.WorkingArea.Height / scr.Scaling;
            }
            if (main.SelfTestSplashShowing) Snap(main.SelfTestRoot, "00-splash");
            for (int i = 0; i < 60 && (DataService.Instance.IsBusy || DataService.Instance.Catalog.Channels.Count == 0); i++) await Task.Delay(500);
            for (int i = 0; i < 20 && main.SelfTestSplashShowing; i++) await Task.Delay(250);
            if (main.SelfTestSplashShowing) Error(new Exception("The splash screen was still up after the playlist loaded."));
            main.SelfTestSplash(true);
            await Idle(500);
            Snap(main.SelfTestRoot, "00-splash-add-playlist");
            main.SelfTestSplash(false);
            await Idle(400);
            Note($"channels: {DataService.Instance.Catalog.Channels.Count}, guide-matched: {DataService.Instance.Epg.MatchedChannels}");
            await Idle(1500);

            await Page(main, main.NavHome, "01-home");
            // Eased wheel scrolling: it glides to where it's asked to.
            var homeScroll = main.SelfTestRoot.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(SmoothScroll.GetEnabled);
            if (homeScroll == null) Error(new Exception("Home has no smooth-scrolling ScrollViewer."));
            else if (homeScroll.Extent.Height - homeScroll.Viewport.Height > 300)
            {
                SmoothScroll.ScrollBy(homeScroll, 300);
                await Task.Delay(50);
                double mid = homeScroll.Offset.Y;
                await Idle(900);
                Note($"smooth scroll: {mid:0} px after 50 ms, {homeScroll.Offset.Y:0} px at the end");
                if (Math.Abs(homeScroll.Offset.Y - 300) > 2) Error(new Exception($"Smooth scrolling ended at {homeScroll.Offset.Y:0} px, not 300."));
                homeScroll.Offset = homeScroll.Offset.WithY(0);
                await Idle(300);
            }
            await Page(main, main.NavGuide, "02-guide");
            await GuideMode(main, "now", "03-guide-on-now");
            await GuideMode(main, "tonight", "04-guide-tonight");
            await GuideMode(main, "grid", null);
            await Page(main, main.NavFilms, "06-films");
            await Page(main, main.NavShows, "07-shows");
            // Trailers: whatever the provider sends (a YouTube id, a link, junk) becomes a YouTube link or nothing.
            foreach (var (raw, want) in new (string, string)[]
            {
                ("dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
                ("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=3", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
                ("https://youtu.be/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
                ("", null), ("0", null), ("n/a", null),
            })
                if (Trailer.Url(raw) != want) Error(new Exception($"Trailer link for “{raw}” came out as {Trailer.Url(raw) ?? "nothing"}, not {want ?? "nothing"}."));
            Note("trailer links: checked");

            // My List: save two films and a show, then see them on Home, in Films and on a film's page.
            var cat = DataService.Instance.Catalog;
            foreach (var m in cat.Movies.Take(2)) if (!HistoryStore.IsSaved("movie", m.Id)) HistoryStore.ToggleSaved("movie", m.Id, m.Name, m.Poster);
            foreach (var x in cat.Series.Take(1)) if (!HistoryStore.IsSaved("series", x.Id)) HistoryStore.ToggleSaved("series", x.Id, x.Name, x.Cover);
            Note($"my list: {HistoryStore.MyList.Count} saved");
            await Page(main, main.NavHome, "01b-home-mylist");
            Note($"home my list row: {main.SelfTestHome.SelfTestMyListShown} titles");
            if (main.SelfTestHome.SelfTestMyListShown != 3) Error(new Exception("Home's My List row doesn't show the 3 saved titles."));
            main.OpenLibraryCategory(true, LibraryView.MyListId);
            await Idle(1500);
            Snap(main.SelfTestRoot, "06b-films-mylist");
            if (cat.Movies.Count > 0)
            {
                main.OpenLibraryItem(true, cat.Movies[0].Id);
                await Idle(1500);
                Snap(main.SelfTestRoot, "06c-film-details");
            }
            await Page(main, main.NavSettings, "09-settings");
            var row0 = main.SelfTestGuide.SelfTestFirstRow;
            if (row0 != null)
            {
                var t = DateTime.Today.AddDays(1).AddHours(21);
                RecordingService.Schedule(row0.Channel.Id, row0.DisplayName, "Football Highlights", t, t.AddMinutes(80));
                ReminderService.Toggle(row0.Channel.Id, row0.DisplayName, new Models.Programme { Title = "Dance Off", Start = t.AddHours(-2), End = t.AddHours(-1) });
            }
            await Page(main, main.NavRecordings, "16-recordings");
            // Events: follow a team (its games in the guide, reminders set by themselves), then search for one game.
            SettingsStore.Current.FollowedTeams = new List<string>();
            Events.Follow("Rovers");
            SettingsStore.Current.RemindTeamGames = true;
            await Page(main, main.NavEvents, "20-events");
            var teamGames = main.SelfTestEvents.SelfTestTeamGames;
            Note($"events: following Rovers – {teamGames} games in the guide");
            if (teamGames == 0) Error(new Exception("Events found no games for a followed team (Rovers)."));
            main.SyncTeamReminders();
            await Idle(3500);
            int teamReminders = ReminderService.Upcoming.Count(r => r.Title.Contains("Rovers"));
            Note($"events: {teamReminders} reminders set for Rovers' games");
            if (teamReminders == 0) Error(new Exception("“Remind me before every game” didn't set any reminders."));
            main.SelfTestEvents.SelfTestQuery("rovers v utd");
            await Idle(1500);
            Note($"events: search “rovers v utd” – {main.SelfTestEvents.SelfTestResults} games");
            Snap(main.SelfTestRoot, "20b-events-search");
            if (main.SelfTestEvents.SelfTestResults == 0) Error(new Exception("Searching Events for “rovers v utd” found nothing."));
            main.SelfTestEvents.SelfTestQuery("");
            main.SelfTestSearch("news");
            await Idle(1500);
            Snap(main.SelfTestRoot, "14-search");
            main.SelfTestSearchFilter("films");
            await Idle(800);
            Snap(main.SelfTestRoot, "15-search-films");
            main.SelfTestSearch("");
            await Page(main, main.NavGuide, null);

            main.SelfTestShortcuts(true);
            await Idle();
            Snap(main.SelfTestRoot, "10-shortcuts");
            main.SelfTestShortcuts(false);

            var editor = new ChannelEditorWindow(main.SelfTestGuide, GuideView.AllId);
            editor.Show(main);
            await Idle(1200);
            if (editor.Content is Control ec) Snap(ec, "11-channel-editor");
            editor.Close();

            var first = main.SelfTestGuide.SelfTestFirstRow;
            if (first != null)
            {
                var match = new GuideMatchWindow(first);
                match.Show(main);
                await Idle(1000);
                if (match.Content is Control mc) Snap(mc, "13-guide-match");
                match.Close();
            }

            // Real playback (the build makes a short test video): the video engine decodes it and draws the player.
            var video = Environment.GetEnvironmentVariable("DINGUS_TEST_VIDEO");
            if (!string.IsNullOrEmpty(video) && File.Exists(video) && main.SelfTestPlayer != null)
            {
                main.SelfTestPlay(new Uri(video).AbsoluteUri);
                for (int i = 0; i < 40 && !main.SelfTestPlayer.Player.IsPlaying; i++) await Task.Delay(250);
                await Task.Delay(2500);
                var mp = main.SelfTestPlayer.Player;
                Note($"player: playing={mp.IsPlaying} time={mp.Time} ms length={mp.Length} ms");
                var snap = Path.Combine(Dir, "17-player-frame.png");
                mp.TakeSnapshot(0, snap, 960, 0);
                await Task.Delay(800);
                Note(File.Exists(snap) ? "player: frame captured" : "player: no frame captured");
                if (!mp.IsPlaying) Error(new Exception("The test video didn't play."));
                // Volume levelling (on by default): VLC loads its sound filter for the stream.
                if (main.SelfTestPlayer is { } psvc)
                {
                    string loaded;
                    lock (psvc.LoadedSoundFilters) loaded = string.Join(", ", psvc.LoadedSoundFilters);
                    Note($"sound filters: {(loaded.Length > 0 ? loaded : "none reported")}{(psvc.SoundFilterProblem != null ? " · problem: " + psvc.SoundFilterProblem : "")}{(VlcSound.Problem != null ? " · " + VlcSound.Problem : "")}");
                    if (psvc.SoundFilterProblem != null) Error(new Exception("A sound filter didn't load: " + psvc.SoundFilterProblem));
                    else if (SettingsStore.Current.EvenVolume && !loaded.Contains("normvol")) {
                        lock (psvc.SoundLog) foreach (var l in psvc.SoundLog.Take(40)) Note("  vlc: " + l);
                        Error(new Exception("Volume levelling is on but VLC didn't load its filter."));
                    }
                }
                // Night mode on, mid-stream: the compressor joins the levelling straight away and the video carries on.
                {
                    var nsvc = main.SelfTestPlayer;
                    SettingsStore.Current.NightMode = true;
                    main.ApplySoundSettings();
                    await Task.Delay(2500);
                    string nl = "";
                    if (nsvc != null) lock (nsvc.LoadedSoundFilters) nl = string.Join(", ", nsvc.LoadedSoundFilters);
                    Note($"night mode on: playing={nsvc?.Player.IsPlaying} · sound filters: {(nl.Length > 0 ? nl : "none reported")}");
                    if (!nl.Contains("compressor")) Error(new Exception("Night mode is on but VLC didn't load its filter."));
                    if (nsvc?.Player.IsPlaying != true) Error(new Exception("The video stopped when night mode was switched on."));
                    SettingsStore.Current.NightMode = false;
                    main.ApplySoundSettings();
                    await Task.Delay(800);
                }
                if (main.SelfTestOverlay is { } ov) Snap(ov, "18-player-controls");
                File.WriteAllText(Path.Combine(Dir, "player-ready.txt"), "ok"); // the build captures the screen now
                await Task.Delay(2500);
                // Picture-in-picture: info at the top, mute + volume bottom-left (the same volume as the player's).
                main.SelfTestMini(true);
                await Idle(800);
                main.SelfTestMiniControls();
                main.SelfTestSetVolume(40);
                await Idle(600);
                if (main.SelfTestOverlay is { } mov) Snap(mov, "19-mini-player");
                var vols = main.SelfTestVolumes;
                Note($"mini volume: player={vols.Player} mini={vols.Mini}");
                if (Math.Abs(vols.Player - vols.Mini) > 0.5) Error(new Exception("The mini player's volume doesn't match the player's."));
                main.SelfTestMini(false);
                await Idle(800);
                main.SelfTestExitPlayer();
                await Idle(800);
            }

            var card = ReminderService.SelfTestCard();
            await Idle(800);
            if (card.Content is Control c) Snap(c, "12-reminder");
            card.Close();
        }
        catch (Exception ex) { Error(ex); }

        var result = Errors.Count == 0 ? "PASSED" : "FAILED";
        var lines = new List<string>(Notes);
        lock (Errors) lines.AddRange(Errors.Select(e => "ERROR: " + e));
        lines.Add(result);
        File.WriteAllLines(Path.Combine(Dir, "result.txt"), lines);
        main.Close();
    }

    static void Note(string s) { lock (Notes) Notes.Add(s); }

    static async Task Page(MainWindow main, RadioButton tab, string name)
    {
        tab.IsChecked = true;
        await Idle(1500);
        if (name != null) Snap(main.SelfTestRoot, name);
    }

    static async Task GuideMode(MainWindow main, string mode, string name)
    {
        main.SelfTestGuide.SelfTestMode(mode);
        await Idle(1000);
        if (name != null) Snap(main.SelfTestRoot, name);
    }

    static void Snap(Control c, string name)
    {
        try
        {
            var size = c.Bounds.Size;
            if (size.Width < 10 || size.Height < 10) { Note($"{name}: nothing to capture"); return; }
            var px = new PixelSize((int)size.Width, (int)size.Height);
            using var bmp = new RenderTargetBitmap(px, new Vector(96, 96));
            bmp.Render(c);
            bmp.Save(Path.Combine(Dir, name + ".png"));
            Note($"{name}: {px.Width}×{px.Height}");
        }
        catch (Exception ex) { Error(ex); }
    }
}
