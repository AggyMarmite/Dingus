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

            try
            {
                VlcPaths.Prepare();
                // Linux: the AppImage puts its own VLC on the library path. Mac: VLC is inside the app bundle.
                if (Os.Mac) LibVLCSharp.Shared.Core.Initialize(VlcPaths.LibDir);
                else LibVLCSharp.Shared.Core.Initialize();
            }
            catch (Exception ex) { VlcPaths.StartError = ex; Log.Error(ex); } // the window still opens and explains
            if (SelfTest.Dir != null && VlcPaths.StartError == null && !VlcPaths.Disabled) SelfTest.VlcCheck();
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
            if (Os.Mac && Os.MacBundle is { } app)
            {
                // Open the app again once this copy has gone (macOS would otherwise just bring this one forward).
                var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("sleep 1; open -n \"$0\"");
                psi.ArgumentList.Add(app);
                System.Diagnostics.Process.Start(psi);
            }
            else
            {
                var exe = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
            }
        }
        catch (Exception ex) { Log.Error(ex); }
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // Mac menu bar
    void MenuAbout_Click(object sender, EventArgs e) => MainWindow.Instance?.OpenSettingsPage();
    void MenuSettings_Click(object sender, EventArgs e) => MainWindow.Instance?.OpenSettingsPage();
    async void MenuUpdates_Click(object sender, EventArgs e)
    {
        if (MainWindow.Instance is { } w) await Updater.CheckAsync(w, manual: true);
    }

    public static void ReleaseSingleInstance() => SingleInstance.Release();
}
