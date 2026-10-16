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
            MessageBox.Show("Something went wrong:\n\n" + a.Exception.Message, "Dingus",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception); a.SetObserved(); };

        // Cap UI animation frame rate – saves GPU/CPU, nothing here needs 60fps.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),
            new FrameworkPropertyMetadata { DefaultValue = 30 });

        LibVLCSharp.Shared.Core.Initialize();
        SettingsStore.Load();
        TimeshiftSession.CleanUp(); // live TV buffers from last time
        ThemeService.Apply(Resources, SettingsStore.Current); // before any window is created
        if (SettingsStore.Current.LowMemoryInterface)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);

        var main = new Dingus.MainWindow();
        MainWindow = main;
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
