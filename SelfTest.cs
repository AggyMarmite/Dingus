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
            // Wait for the sample playlist and guide to load.
            for (int i = 0; i < 60 && (DataService.Instance.IsBusy || DataService.Instance.Catalog.Channels.Count == 0); i++) await Task.Delay(500);
            Note($"channels: {DataService.Instance.Catalog.Channels.Count}, guide-matched: {DataService.Instance.Epg.MatchedChannels}");
            await Idle(1500);

            await Page(main, main.NavHome, "01-home", fullScroll: true);
            await Page(main, main.NavGuide, "02-guide");
            await GuideMode(main, "now", "03-guide-on-now");
            await GuideMode(main, "tonight", "04-guide-tonight");
            await GuideMode(main, "find", "05-guide-find", "news");
            await GuideMode(main, "grid", null);
            await Page(main, main.NavFilms, "06-films");
            await Page(main, main.NavShows, "07-shows");
            await Page(main, main.NavPlex, "08-plex");
            await Page(main, main.NavSettings, "09-settings", fullScroll: true);
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

            var card = ReminderService.SelfTestCard();
            await Idle(800);
            if (card != null) { Snap((FrameworkElement)card.Content, "12-reminder"); card.Close(); }

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
