using System.Windows;
using System.Windows.Media.Animation;
using Dingus.Services;

namespace Dingus;

public partial class App : Application
{
    // Only one copy runs at a time: a second copy would use a second IPTV connection and the two
    // would knock each other off. Opening it again just brings the existing window to the front.
    const string MutexName = @"Local\Dingus.SingleInstance";
    const string ActivateName = @"Local\Dingus.Activate";
    static Mutex _single;
    static EventWaitHandle _activate;

    protected override void OnStartup(StartupEventArgs e)
    {
        int st = Array.IndexOf(e.Args, "--self-test");
        int pt = Array.IndexOf(e.Args, "--perf-test");
        if (pt >= 0 && pt + 1 < e.Args.Length) { st = pt; PerfTest.On = true; PerfTest.Attach(); }
        if (st >= 0 && st + 1 < e.Args.Length)
        {
            SelfTest.Dir = e.Args[st + 1];
            Directory.CreateDirectory(SelfTest.Dir);
            File.WriteAllText(Path.Combine(SelfTest.Dir, "started.txt"), DateTime.Now.ToString("O"));
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
                File.AppendAllText(Path.Combine(SelfTest.Dir, "crash.txt"), a.ExceptionObject + "\n\n");
        }

        _single = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Not the first copy: wake the one that's running, and leave without touching any settings.
            _single.Dispose();
            _single = null;
            try { EventWaitHandle.OpenExisting(ActivateName).Set(); } catch { }
            Shutdown();
            return;
        }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        var waiter = new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.InvokeAsync(() => (MainWindow as Dingus.MainWindow)?.BringToFront());
        }) { IsBackground = true, Name = "Activate listener" };
        waiter.Start();

        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error(a.Exception);
            if (SelfTest.Dir != null) { SelfTest.Error(a.Exception); a.Handled = true; return; }
            MessageBox.Show("Something went wrong:\n\n" + a.Exception.Message, "Dingus",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception); a.SetObserved(); };

        // Smooth 60fps animations. They only run while something is moving (a fade or slide lasts ~0.2 s),
        // so this costs nothing when the app is still.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),
            new FrameworkPropertyMetadata { DefaultValue = 60 });

        LibVLCSharp.Shared.Core.Initialize();
        SettingsStore.Load();
        TimeshiftSession.CleanUp(); // live TV buffers from last time
        ThemeService.Apply(Resources, SettingsStore.Current); // before any window is created
        if (SettingsStore.Current.LowMemoryInterface)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);

        Dingus.MainWindow main;
        try { main = new Dingus.MainWindow(); }
        catch (Exception ex) when (SelfTest.Dir != null)
        {
            SelfTest.Error(ex);
            Directory.CreateDirectory(SelfTest.Dir);
            File.WriteAllText(Path.Combine(SelfTest.Dir, "result.txt"), "FAILED – the main window didn't open:\n\n" + ex);
            Shutdown(1);
            return;
        }
        MainWindow = main;
        // Memory is tidied up only once you've left the window alone for a moment (see Memory.CompactSoon).
        System.Windows.Input.InputManager.Current.PreProcessInput += (_, a) =>
        {
            if (a.StagingItem.Input is System.Windows.Input.KeyEventArgs or System.Windows.Input.MouseButtonEventArgs
                or System.Windows.Input.MouseWheelEventArgs) Memory.NoteInput();
        };
        if (PerfTest.On) main.Loaded += async (_, _) => await PerfTest.RunAsync(main);
        else if (SelfTest.Dir != null) main.Loaded += async (_, _) => await SelfTest.RunAsync(main);
        main.Show();
    }

    /// <summary>Restarts the app (e.g. after changing the theme).</summary>
    /// <param name="saveState">False after restoring a backup, so the files just restored aren't overwritten.</param>
    public static void Restart(bool saveState = true)
    {
        try
        {
            if (saveState)
            {
                SettingsStore.Save();
                HistoryStore.SaveNow();
            }
            ReleaseSingleInstance();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error(ex); }
        Current.Shutdown();
    }

    public static void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch { }
        try { _single?.Dispose(); _single = null; } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_single != null)
        {
            SettingsStore.Save();
            HistoryStore.SaveNow();
            DisplayRate.Restore();
        }
        base.OnExit(e);
    }
}
