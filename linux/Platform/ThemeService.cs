namespace Dingus.Services;

/// <summary>
/// "Start when I log in". Linux: an autostart entry in ~/.config/autostart.
/// Mac: a login item in ~/Library/LaunchAgents that opens the Dingus app.
/// </summary>
public static class ThemeService
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string AutostartFile => Os.Mac
        ? Path.Combine(Home, "Library", "LaunchAgents", "app.dingus.login.plist")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"),
            "autostart", "dingus.desktop");

    public static bool IsStartWithWindows() => File.Exists(AutostartFile);

    public static void ApplyStartWithWindows(bool on)
    {
        try
        {
            if (!on) { if (File.Exists(AutostartFile)) File.Delete(AutostartFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            if (Os.Mac)
            {
                var app = System.Security.SecurityElement.Escape(Os.MacBundle ?? Environment.ProcessPath);
                File.WriteAllText(AutostartFile, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                        <key>Label</key><string>app.dingus.login</string>
                        <key>ProgramArguments</key>
                        <array><string>/usr/bin/open</string><string>-a</string><string>{app}</string></array>
                        <key>RunAtLoad</key><true/>
                    </dict>
                    </plist>
                    """);
                return;
            }
            var exe = Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath;
            File.WriteAllText(AutostartFile, $"[Desktop Entry]\nType=Application\nName=Dingus\nExec=\"{exe}\"\nIcon=dingus\nX-GNOME-Autostart-enabled=true\nComment=IPTV player\n");
        }
        catch (Exception ex) { Log.Error(ex); }
    }
}
