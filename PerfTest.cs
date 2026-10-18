using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dingus.Services;

namespace Dingus;

/// <summary>
/// "Dingus.exe --perf-test &lt;folder&gt;": the speed test. Loads the playlist (a big one, from a stand-in server in the
/// GitHub build), then opens and scrolls the main pages, timing each step and noting how long the window stops
/// responding ("freezes": moments a click or key press would have to wait). Writes perf.txt and result.txt.
/// </summary>
public static class PerfTest
{
    public static bool On { get; set; }

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly List<string> Lines = new();
    static readonly object Gate = new();
    static readonly List<(string What, double Ms, bool Ui, string Phase)> Work = new();
    static double _startOffsetMs; // process start → this class first used

    // Responsiveness, measured from another thread: how long a tiny job waits for the window's thread.
    static string _phase = "start-up";
    static double _maxLag, _frozenMs;
    static int _freezes;

    // Loading milestones (ms since the process started).
    static double? _cachedShown, _catalogLoaded, _guideLoaded;
    static int _channels, _films, _shows, _guideChannels;

    static double Now => _startOffsetMs + Clock.Elapsed.TotalMilliseconds;

    /// <summary>Call as early as possible (app start-up).</summary>
    public static void Attach()
    {
        try { _startOffsetMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; } catch { }
        var ds = DataService.Instance;
        ds.CatalogChanged += () =>
        {
            var c = ds.Catalog;
            int n = c.Channels.Count + c.Movies.Count + c.Series.Count;
            if (n == 0) return;
            if (!ds.IsBusy) _cachedShown ??= Now; // the saved copy, shown before the fresh download (second runs)
            _channels = c.Channels.Count; _films = c.Movies.Count; _shows = c.Series.Count;
        };
        ds.BusyChanged += (busy, _) =>
        {
            if (busy) return;
            var c = ds.Catalog;
            if (c.Channels.Count + c.Movies.Count + c.Series.Count > 0 && !ds.BusyIsGuide) _catalogLoaded = Now;
        };
        ds.EpgChanged += () =>
        {
            if (ds.Epg.MatchedChannels > 0) { _guideLoaded = Now; _guideChannels = ds.Epg.MatchedChannels; }
        };
        DataService.TimingHook = (what, ms) =>
        {
            if (ms < 30) return;
            bool ui = Application.Current?.Dispatcher.CheckAccess() == true;
            lock (Work) Work.Add((what, ms, ui, _phase));
        };
        var t = new Thread(Watch) { IsBackground = true, Name = "Speed test: responsiveness" };
        t.Start();
    }

    static void Watch()
    {
        var d = Application.Current?.Dispatcher;
        while (d == null) { Thread.Sleep(20); d = Application.Current?.Dispatcher; }
        while (true)
        {
            var t0 = Clock.Elapsed.TotalMilliseconds;
            try { d.Invoke(() => { }, DispatcherPriority.Input); } catch { return; }
            var lag = Clock.Elapsed.TotalMilliseconds - t0;
            lock (Gate)
            {
                if (lag > _maxLag) _maxLag = lag;
                if (lag > 100) { _freezes++; _frozenMs += lag; }
            }
            Thread.Sleep(15);
        }
    }

    static void Begin(string name)
    {
        lock (Gate) { _phase = name; _maxLag = 0; _frozenMs = 0; _freezes = 0; }
    }

    static string Freezes()
    {
        lock (Gate)
            return _freezes == 0
                ? $"longest wait {_maxLag:0} ms"
                : $"longest freeze {_maxLag:N0} ms · {_freezes} freeze{(_freezes == 1 ? "" : "s")} over 0.1 s (total {_frozenMs / 1000:0.0} s)";
    }

    static void Line(string s)
    {
        lock (Lines) Lines.Add(s);
    }

    static Task WhenIdle() =>
        Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    /// <summary>Does something, waits until the window is idle again, and notes how long that took.</summary>
    static async Task Step(string name, Func<Task> action, int settleMs = 1200)
    {
        Begin(name);
        var t0 = Clock.Elapsed.TotalMilliseconds;
        await action();
        await Task.Yield();
        await WhenIdle();
        var took = Clock.Elapsed.TotalMilliseconds - t0;
        await Task.Delay(settleMs); // pictures and late work still count towards freezes
        await WhenIdle();
        Line($"{name,-34} {took,8:N0} ms   {Freezes()} · memory {Memory.WorkingSetMB():N0} MB");
    }

    static Task Nav(RadioButton b) { b.IsChecked = true; return Task.CompletedTask; }

    /// <summary>Scrolls the named list in a page down by a page at a time.</summary>
    static async Task Scroll(MainWindow main, string listName, int pages)
    {
        if ((main.SelfTestPage as FrameworkElement)?.FindName(listName) is not DependencyObject list) return;
        var sv = FindScroller(list);
        if (sv == null) return;
        for (int i = 0; i < pages; i++)
        {
            sv.PageDown();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    static ScrollViewer FindScroller(DependencyObject o)
    {
        if (o is ScrollViewer sv) return sv;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
            if (FindScroller(System.Windows.Media.VisualTreeHelper.GetChild(o, i)) is { } f) return f;
        return null;
    }

    public static async Task RunAsync(MainWindow main)
    {
        var shown = Now;
        string startFreezes = Freezes();
        Begin("loading");
        try
        {
            main.WindowState = WindowState.Maximized;
            // Wait for the playlist and the guide (a big guide can take a while on the first run).
            var ds = DataService.Instance;
            var until = DateTime.UtcNow.AddMinutes(8);
            while (DateTime.UtcNow < until && (ds.IsBusy || _catalogLoaded == null || (_guideLoaded == null && ds.Catalog.Channels.Count > 0)))
                await Task.Delay(250);
            await WhenIdle();
            string loadFreezes = Freezes();

            Line($"Playlist: {_channels:N0} channels · {_films:N0} films · {_shows:N0} shows · guide for {_guideChannels:N0} channels");
            Line("");
            Line($"{"Window open",-34} {shown,8:N0} ms   (from starting Dingus) {startFreezes}");
            if (_cachedShown is double cs) Line($"{"Saved playlist on screen",-34} {cs,8:N0} ms");
            Line($"{"Playlist loaded",-34} {_catalogLoaded ?? -1,8:N0} ms");
            Line($"{"TV guide loaded",-34} {_guideLoaded ?? -1,8:N0} ms");
            Line($"{"While loading",-34} {"",8}      {loadFreezes} · memory {Memory.WorkingSetMB():N0} MB");
            Line("");

            await Step("Home", () => Nav(main.NavHome));
            SelfTest.SnapForPerf(main, "perf-home");
            await Step("Live TV", () => Nav(main.NavGuide));
            SelfTest.SnapForPerf(main, "perf-guide");
            await Step("Live TV: scroll 40 pages", () => Scroll(main, "ChannelList", 40));
            await Step("Live TV: On now", () => { main.SelfTestGuide.SelfTestMode("now"); return Task.CompletedTask; });
            await Step("Live TV: Tonight", () => { main.SelfTestGuide.SelfTestMode("tonight"); return Task.CompletedTask; });
            await Step("Live TV: back to the guide", () => { main.SelfTestGuide.SelfTestMode("grid"); return Task.CompletedTask; });
            await Step("Films", () => Nav(main.NavFilms));
            SelfTest.SnapForPerf(main, "perf-films");
            await Step("Films: scroll 30 pages", () => Scroll(main, "PosterList", 30));
            await Step("TV Shows", () => Nav(main.NavShows));
            await Step("TV Shows: scroll 30 pages", () => Scroll(main, "PosterList", 30));
            await Step("Search everything: \"the\"", () => { main.SelfTestSearch("the"); return Task.Delay(600); });
            SelfTest.SnapForPerf(main, "perf-search");
            await Step("Search everything: \"news\"", () => { main.SelfTestSearch("news"); return Task.Delay(600); });
            main.SelfTestSearch("");
            await Step("Home again", () => Nav(main.NavHome));
            await Step("Settings", () => Nav(main.NavSettings));
            Line("");
            Line($"Memory at the end: {Memory.WorkingSetMB():N0} MB");
            Line("");
            Line("Slowest work (over 30 ms; \"window\" = the window waits for it, \"background\" = it doesn't):");
            lock (Work)
                foreach (var w in Work.OrderByDescending(w => w.Ms).Take(25))
                    Line($"  {w.Ms,7:N0} ms  {(w.Ui ? "window    " : "background")}  {w.What}  ({w.Phase})");
        }
        catch (Exception ex) { SelfTest.Error(ex); }

        string text;
        lock (Lines) text = string.Join("\n", Lines);
        File.WriteAllText(Path.Combine(SelfTest.Dir, "perf.txt"), text + "\n");
        lock (SelfTest.Errors)
        {
            File.WriteAllText(Path.Combine(SelfTest.Dir, "result.txt"),
                text + "\n\n" + (SelfTest.Errors.Count == 0 ? "PASSED" : $"FAILED – {SelfTest.Errors.Count} error(s):\n\n" + string.Join("\n\n", SelfTest.Errors)));
            Environment.ExitCode = SelfTest.Errors.Count == 0 ? 0 : 1;
        }
        App.ReleaseSingleInstance();
        Application.Current.Shutdown(Environment.ExitCode);
    }
}
