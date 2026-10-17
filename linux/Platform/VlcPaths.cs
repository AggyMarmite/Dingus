namespace Dingus.Services;

/// <summary>
/// Where the VLC video engine is. The AppImage carries its own copy (next to the app, in lib/vlc);
/// otherwise the system's VLC is used (sudo apt install vlc).
/// </summary>
public static class VlcPaths
{
    /// <summary>Why VLC couldn't be loaded, if it couldn't.</summary>
    public static Exception StartError { get; set; }

    /// <summary>Development only: run the interface without the video engine.</summary>
    public static bool Disabled => Environment.GetEnvironmentVariable("DINGUS_NO_VLC") == "1";

    /// <summary>Points VLC at the AppImage's own plugins when they're there (libvlc itself comes from LD_LIBRARY_PATH).</summary>
    public static void Prepare() => _ = LibDir;

    public static string LibDir
    {
        get
        {
            var here = AppContext.BaseDirectory;
            foreach (var dir in new[] { Path.Combine(here, "vlc"), Path.Combine(here, "..", "lib"), here })
            {
                if (File.Exists(Path.Combine(dir, "libvlc.so.5")))
                {
                    var plugins = Path.Combine(dir, "vlc", "plugins");
                    if (Directory.Exists(plugins) && Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH") == null)
                        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", plugins);
                    return Path.GetFullPath(dir);
                }
            }
            return null; // LibVLCSharp then looks in the usual system places
        }
    }
}
