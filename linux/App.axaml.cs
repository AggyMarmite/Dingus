using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Controls;
using Dingus.Services;

namespace Dingus;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, a) =>
            {
                Log.Error(a.Exception);
                if (SelfTest.Dir != null) { SelfTest.Error(a.Exception); a.Handled = true; return; }
                a.Handled = true;
                MainWindow.Instance?.ShowToast("Something went wrong: " + a.Exception.Message);
            };

            try { VlcPaths.Prepare(); LibVLCSharp.Shared.Core.Initialize(); } // the AppImage puts its own VLC on the library path
            catch (Exception ex) { VlcPaths.StartError = ex; Log.Error(ex); } // the window still opens and explains
            SettingsStore.Load();
            TimeshiftSession.CleanUp(); // live TV buffers from last time

            var main = new MainWindow();
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                SettingsStore.Save();
                HistoryStore.SaveNow();
                KeepAwake.Set(false);
            };
            SingleInstance.Activated += () => Dispatcher.UIThread.Post(() => main.BringToFront());
            if (SelfTest.Dir != null) main.Opened += async (_, _) => await SelfTest.RunAsync(main);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Restarts the app (e.g. after restoring a backup).</summary>
    public static void Restart(bool saveState = true)
    {
        try
        {
            if (saveState)
            {
                SettingsStore.Save();
                HistoryStore.SaveNow();
            }
            SingleInstance.Release();
            var exe = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error(ex); }
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public static void ReleaseSingleInstance() => SingleInstance.Release();
}
