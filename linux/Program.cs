using Avalonia;
using Dingus.Services;

namespace Dingus;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int st = Array.IndexOf(args, "--self-test");
        if (st >= 0 && st + 1 < args.Length)
        {
            SelfTest.Dir = Path.GetFullPath(args[st + 1]);
            Directory.CreateDirectory(SelfTest.Dir);
            File.WriteAllText(Path.Combine(SelfTest.Dir, "started.txt"), DateTime.Now.ToString("O"));
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
                File.AppendAllText(Path.Combine(SelfTest.Dir, "crash.txt"), a.ExceptionObject + "\n\n");
        }

        // Only one copy runs at a time (a second one would use a second IPTV connection):
        // opening Dingus again brings the running window to the front.
        if (SelfTest.Dir == null && !SingleInstance.TryBecomeFirst()) return 0;

        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception); a.SetObserved(); };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (SelfTest.Dir != null)
                File.WriteAllText(Path.Combine(SelfTest.Dir, "result.txt"), "FAILED – the app didn't start:\n\n" + ex);
            return 1;
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { EnableIme = true })
            .LogToTrace();
}
