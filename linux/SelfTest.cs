using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
            for (int i = 0; i < 60 && (DataService.Instance.IsBusy || DataService.Instance.Catalog.Channels.Count == 0); i++) await Task.Delay(500);
            Note($"channels: {DataService.Instance.Catalog.Channels.Count}, guide-matched: {DataService.Instance.Epg.MatchedChannels}");
            await Idle(1500);

            await Page(main, main.NavHome, "01-home");
            await Page(main, main.NavGuide, "02-guide");
            await GuideMode(main, "now", "03-guide-on-now");
            await GuideMode(main, "tonight", "04-guide-tonight");
            await GuideMode(main, "grid", null);
            await Page(main, main.NavFilms, "06-films");
            await Page(main, main.NavShows, "07-shows");
            await Page(main, main.NavSettings, "09-settings");
            var row0 = main.SelfTestGuide.SelfTestFirstRow;
            if (row0 != null)
            {
                var t = DateTime.Today.AddDays(1).AddHours(21);
                RecordingService.Schedule(row0.Channel.Id, row0.DisplayName, "Football Highlights", t, t.AddMinutes(80));
                ReminderService.Toggle(row0.Channel.Id, row0.DisplayName, new Models.Programme { Title = "Dance Off", Start = t.AddHours(-2), End = t.AddHours(-1) });
            }
            await Page(main, main.NavRecordings, "16-recordings");
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
                if (main.SelfTestOverlay is { } ov) Snap(ov, "18-player-controls");
                File.WriteAllText(Path.Combine(Dir, "player-ready.txt"), "ok"); // the build captures the screen now
                await Task.Delay(2500);
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
