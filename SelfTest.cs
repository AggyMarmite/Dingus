using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dingus.Services;
using Dingus.Views;

namespace Dingus;

/// <summary>
/// "Dingus.exe --self-test &lt;folder&gt;": opens every page and dialog, saves a screenshot of each and writes
/// result.txt. Run by the GitHub build so layout or start-up problems show up before a release.
/// </summary>
public static class SelfTest
{
    public static string Dir { get; set; }
    public static readonly List<string> Errors = new();
    static readonly List<string> Log = new();

    public static void Error(Exception ex) { lock (Errors) Errors.Add(ex.ToString()); }

    static Task Idle(int ms = 600) =>
        Task.Delay(ms).ContinueWith(_ => { }, TaskScheduler.Default)
            .ContinueWith(_ => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task, TaskScheduler.Default).Unwrap();

    public static async Task RunAsync(MainWindow main)
    {
        Directory.CreateDirectory(Dir);
        try
        {
            var wa = SystemParameters.WorkArea;
            Note($"window opened at {main.ActualWidth:0}×{main.ActualHeight:0} ({main.WindowState}) on a {wa.Width:0}×{wa.Height:0} screen");
            if (main.WindowState != WindowState.Normal || main.ActualWidth > wa.Width * 0.85 || main.ActualHeight > wa.Height * 0.85)
                Error(new Exception("Dingus should open as a window, at about 80% of the screen"));
            Note($"splash showing as the window opened: {main.SelfTestSplashShowing}");
            Snap(main.SelfTestRoot, "00-splash");
            main.WindowState = WindowState.Maximized; // screenshots at the full screen size
            // Wait for the sample playlist and guide to load.
            for (int i = 0; i < 60 && (DataService.Instance.IsBusy || DataService.Instance.Catalog.Channels.Count == 0); i++) await Task.Delay(500);
            Note($"channels: {DataService.Instance.Catalog.Channels.Count}, guide-matched: {DataService.Instance.Epg.MatchedChannels}");
            await Idle(1500);

            // By now the saved playlist is on Home: the splash must have gone (it waits for nothing else).
            if (main.SelfTestSplashShowing) Error(new Exception("The splash screen was still showing after the playlist loaded"));
            // The splash for someone with no playlist yet: "Add playlist" under the slogan.
            main.SelfTestSplash(true);
            await Idle(600);
            Snap(main.SelfTestRoot, "00-splash-add-playlist");
            main.SelfTestSplash(false);
            CheckTitleBarClickable(main);
            await Page(main, main.NavHome, "01-home", fullScroll: true);
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
            await Page(main, main.NavHome, "01b-home-mylist", fullScroll: true);
            main.OpenLibraryCategory(true, LibraryView.MyListId);
            await Idle(1500);
            Snap(main.SelfTestRoot, "06b-films-mylist");
            if (cat.Movies.Count > 0)
            {
                main.OpenLibraryItem(true, cat.Movies[0].Id);
                await Idle(1500);
                Snap(main.SelfTestRoot, "06c-film-details");
            }
            await Page(main, main.NavSettings, "09-settings", fullScroll: true);
            var row0 = main.SelfTestGuide.SelfTestFirstRow;
            if (row0 != null)
            {
                var t = DateTime.Today.AddDays(1).AddHours(21);
                RecordingService.Schedule(row0.Channel.Id, row0.DisplayName, "Football Highlights", t, t.AddMinutes(80));
                ReminderService.Toggle(row0.Channel.Id, row0.DisplayName, new Models.Programme { Title = "Dance Off", Start = t.AddHours(-2), End = t.AddHours(-1) });
            }
            await Page(main, main.NavRecordings, "16-recordings", fullScroll: true);
            main.SelfTestSearch("news"); // typing anywhere opens Search
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

            var editor = new ChannelEditorWindow(main.SelfTestGuide, GuideView.AllId) { Owner = main };
            editor.Show();
            await Idle(1200);
            Snap((FrameworkElement)editor.Content, "11-channel-editor");
            editor.Close();

            var first = main.SelfTestGuide.SelfTestFirstRow;
            if (first != null)
            {
                var match = new GuideMatchWindow(first) { Owner = main };
                match.Show();
                await Idle(1000);
                Snap((FrameworkElement)match.Content, "13-guide-match");
                match.Close();
            }

            var card = ReminderService.SelfTestCard();
            await Idle(800);
            if (card != null) { Snap((FrameworkElement)card.Content, "12-reminder"); card.Close(); }

            await VideoMemory(main);
            Note($"memory: {Memory.WorkingSetMB()} MB");
        }
        catch (Exception ex) { Error(ex); }

        lock (Errors)
        {
            File.WriteAllText(Path.Combine(Dir, "result.txt"),
                string.Join("\n", Log) + "\n\n" + (Errors.Count == 0 ? "PASSED" : $"FAILED – {Errors.Count} error(s):\n\n" + string.Join("\n\n", Errors)));
            Environment.ExitCode = Errors.Count == 0 ? 0 : 1;
        }
        App.ReleaseSingleInstance();
        Application.Current.Shutdown(Environment.ExitCode);
    }

    static void Note(string s) => Log.Add(s);

    /// <summary>
    /// Plays a short test video (DINGUS_TEST_VIDEO) in the full player, then picture-in-picture, then the full player
    /// again, noting memory at each stage – picture-in-picture shouldn't need more than the full player.
    /// </summary>
    static async Task VideoMemory(MainWindow main)
    {
        var video = Environment.GetEnvironmentVariable("DINGUS_TEST_VIDEO");
        if (string.IsNullOrEmpty(video) || !File.Exists(video)) return;
        string Mem()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return $"working set {p.WorkingSet64 >> 20} MB · private {p.PrivateMemorySize64 >> 20} MB · .NET heap {GC.GetTotalMemory(false) >> 20} MB";
        }
        // Note every change to "always on top", and what changed it.
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Window.TopmostProperty, typeof(Window)).AddValueChanged(main, (_, _) =>
        {
            var who = string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Skip(1)
                .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.Namespace?.StartsWith("Dingus") == true)
                .Take(4).Select(m => m.DeclaringType.Name + "." + m.Name));
            Note($"  on top → {main.Topmost} (fullscreen {main.SelfTestFullscreen}) from {who}");
        });
        main.PlayVod(new Models.VodItem { Kind = "movie", Title = "Test video", Url = new Uri(video).AbsoluteUri }, resume: false);
        for (int i = 0; i < 40 && !main.SelfTestPlaying; i++) await Task.Delay(250);
        await Task.Delay(3500);
        main.SelfTestShowChrome();
        await Idle(500);
        Note($"video, full player: playing={main.SelfTestPlaying} · {Mem()}");
        // Volume levelling (on by default): VLC loads its sound filter for the stream.
        if (main.SelfTestPlayer is { } psvc)
        {
            string loaded;
            lock (psvc.LoadedSoundFilters) loaded = string.Join(", ", psvc.LoadedSoundFilters);
            Note($"sound filters: {(loaded.Length > 0 ? loaded : "none reported")}{(psvc.SoundFilterProblem != null ? " · problem: " + psvc.SoundFilterProblem : "")}{(VlcSound.Problem != null ? " · " + VlcSound.Problem : "")}");
            if (psvc.SoundFilterProblem != null) Error(new Exception("A sound filter didn't load: " + psvc.SoundFilterProblem));
            else if (Services.SettingsStore.Current.EvenVolume && !loaded.Contains("normvol")) {
                lock (psvc.SoundLog) foreach (var l in psvc.SoundLog.Take(40)) Note("  vlc: " + l);
                Error(new Exception("Volume levelling is on but VLC didn't load its filter."));
            }
        }
        Snap(main.SelfTestOverlay, "17-player-controls");
        // Night mode on, mid-stream: the compressor joins the levelling straight away and the video carries on.
        {
            var nsvc = main.SelfTestPlayer;
            Services.SettingsStore.Current.NightMode = true;
            main.ApplySoundSettings();
            await Task.Delay(2500);
            string nl = "";
            if (nsvc != null) lock (nsvc.LoadedSoundFilters) nl = string.Join(", ", nsvc.LoadedSoundFilters);
            Note($"night mode on: playing={nsvc?.Player.IsPlaying} · sound filters: {(nl.Length > 0 ? nl : "none reported")}");
            if (!nl.Contains("compressor")) Error(new Exception("Night mode is on but VLC didn't load its filter."));
            if (nsvc?.Player.IsPlaying != true) Error(new Exception("The video stopped when night mode was switched on."));
            Services.SettingsStore.Current.NightMode = false;
            main.ApplySoundSettings();
            await Task.Delay(800);
        }
        main.SelfTestMini(true);
        await Task.Delay(3500);
        Note($"video, picture-in-picture: playing={main.SelfTestPlaying} · {Mem()}");
        main.SelfTestMiniControls();
        await Idle(700);
        Snap(main.SelfTestOverlay, "18-pip-controls"); // the controls sit in their own layer over the video
        var vol = main.SelfTestVolumes;
        Note($"mini player volume {vol.Mini:0} (player {vol.Player:0})");
        if (Math.Abs(vol.Mini - vol.Player) > 0.5) Error(new Exception("The mini player's volume slider doesn't match the player's volume"));
        main.SelfTestMini(false);
        await Task.Delay(3500);
        Note($"video, full player again: playing={main.SelfTestPlaying} · {Mem()}");
        main.SelfTestGoFullscreen();
        Note($"just after going full screen: {main.SelfTestFullscreen} · {main.WindowState} · {main.WindowStyle} · {main.ActualWidth:0}×{main.ActualHeight:0}");
        await Idle(1500);
        Note($"full screen: {main.SelfTestFullscreen} · on top: {main.Topmost} · still in the player: {main.SelfTestInPlayer}");
        main.SelfTestExitPlayer();
        await Idle(1500);
        Note($"video stopped: {Mem()} · on top: {main.Topmost}");
        // Back in the app after full screen and picture-in-picture: other apps must be able to go in front again –
        // checked against what Windows itself has, for the window and the player-controls window over the video.
        await Task.Delay(1000);
        var onTop = new[] { (Window)main }.Concat(main.OwnedWindows.Cast<Window>())
            .Where(w => !w.Topmost && NativeWindow.IsTopmost(w)).Select(w => w.GetType().Name).ToList();
        Note($"after full screen: {main.LastTopmostFixes} window(s) had to be taken off 'always on top'; still on top: {(onTop.Count == 0 ? "none" : string.Join(", ", onTop))}");
        if (main.Topmost || onTop.Count > 0)
            Error(new Exception("Dingus is still always-on-top after leaving the player: " + string.Join(", ", onTop.DefaultIfEmpty("main window"))));
    }

    /// <summary>
    /// The title bar doubles as the window's drag handle: anything clickable in it has to be marked as such,
    /// or Windows turns the click into a window drag and the button does nothing.
    /// </summary>
    static void CheckTitleBarClickable(MainWindow main)
    {
        if (main.FindName("TitleBar") is not DependencyObject bar) return;
        var bad = new List<string>();
        void Walk(DependencyObject o)
        {
            if (o is System.Windows.Controls.Primitives.ButtonBase or TextBox && o is FrameworkElement fe
                && !System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome((IInputElement)o))
                bad.Add(string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name + " " + (fe.ToolTip ?? "") : fe.Name);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
        Walk(bar);
        if (bad.Count > 0) Error(new Exception("Title bar controls that can't be clicked (missing WindowChrome.IsHitTestVisibleInChrome): " + string.Join(", ", bad)));
        else Note("title bar: every button can be clicked");
    }

    static async Task Page(MainWindow main, RadioButton nav, string name, bool fullScroll = false)
    {
        nav.IsChecked = true;
        await Idle(1500);
        if (name == null) return;
        Snap(main.SelfTestRoot, name);
        if (fullScroll && main.SelfTestPage is UserControl { Content: ScrollViewer { Content: FrameworkElement inner } })
            Snap(inner, name + "-full");
    }

    static async Task GuideMode(MainWindow main, string mode, string name, string search = null)
    {
        main.SelfTestGuide.SelfTestMode(mode);
        if (search != null) main.SelfTestSearch(search);
        await Idle(1200);
        if (name != null) Snap(main.SelfTestRoot, name);
        if (search != null) main.SelfTestSearch("");
    }

    internal static void SnapForPerf(MainWindow main, string name) => Snap(main.SelfTestRoot, name);

    static void Snap(FrameworkElement el, string name)
    {
        try
        {
            el.UpdateLayout();
            double w = el.ActualWidth, h = el.ActualHeight;
            if (w < 2 || h < 2) { Note($"{name}: nothing to capture"); return; }
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)Application.Current.Resources["BgBrush"], null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(Dir, name + ".png"));
            enc.Save(fs);
            Note($"{name}: {w:0}×{h:0}");
        }
        catch (Exception ex) { Error(ex); }
    }
}
