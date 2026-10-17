namespace Dingus.Services;

/// <summary>Linux: "Start when I log in" (an autostart entry in ~/.config/autostart).</summary>
public static class ThemeService
{
    static string AutostartFile =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "dingus.desktop");

    public static bool IsStartWithWindows() => File.Exists(AutostartFile);

    public static void ApplyStartWithWindows(bool on)
    {
        try
        {
            if (!on) { if (File.Exists(AutostartFile)) File.Delete(AutostartFile); return; }
            var exe = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile, $"[Desktop Entry]\nType=Application\nName=Dingus\nExec=\"{exe}\"\nIcon=dingus\nX-GNOME-Autostart-enabled=true\nComment=IPTV player\n");
        }
        catch (Exception ex) { Log.Error(ex); }
    }
}
