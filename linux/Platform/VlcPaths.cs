namespace Dingus.Services;

/// <summary>
/// Where the VLC video engine is. Linux: the AppImage carries its own copy (next to the app, in lib/vlc);
/// otherwise the system's VLC is used (sudo apt install vlc). Mac: inside the app, in Contents/Frameworks/vlc
/// (or an installed VLC.app when running a development build).
/// </summary>
public static class VlcPaths
{
    /// <summary>Why VLC couldn't be loaded, if it couldn't.</summary>
    public static Exception StartError { get; set; }

    /// <summary>Development only: run the interface without the video engine.</summary>
    public static bool Disabled => Environment.GetEnvironmentVariable("DINGUS_NO_VLC") == "1";

    /// <summary>Points VLC at its plugins when we carry our own copy.</summary>
    public static void Prepare() => _ = LibDir;

    public static string LibDir => Os.Mac ? MacLibDir() : LinuxLibDir();

    static string LinuxLibDir()
    {
        var here = AppContext.BaseDirectory;
        foreach (var dir in new[] { Path.Combine(here, "vlc"), Path.Combine(here, "..", "lib"), here })
        {
            if (File.Exists(Path.Combine(dir, "libvlc.so.5")))
            {
                SetPlugins(Path.Combine(dir, "vlc", "plugins"));
                return Path.GetFullPath(dir);
            }
        }
        return null; // LibVLCSharp then looks in the usual system places
    }

    static string MacLibDir()
    {
        var here = AppContext.BaseDirectory;
        foreach (var root in new[]
                 {
                     Path.Combine(here, "..", "Frameworks", "vlc"),          // inside Dingus.app
                     Path.Combine(here, "vlc"),
                     "/Applications/VLC.app/Contents/MacOS",                 // development builds
                 })
        {
            var lib = Path.Combine(root, "lib");
            if (File.Exists(Path.Combine(lib, "libvlc.dylib")))
            {
                SetPlugins(Path.Combine(root, "plugins"));
                return Path.GetFullPath(lib);
            }
        }
        return null;
    }

    static void SetPlugins(string plugins)
    {
        if (Directory.Exists(plugins) && Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH") == null)
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.GetFullPath(plugins));
    }
}
