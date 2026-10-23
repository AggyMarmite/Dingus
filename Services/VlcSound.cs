using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Dingus.Services;

/// <summary>
/// Sound filters (volume levelling, night mode) for a VLC player.
///
/// VLC 3's player only lets its own equalizer choose sound filters: the usual "--audio-filter" option and per-stream
/// ":audio-filter" are both ignored, because the player keeps an empty filter list of its own that hides them. So
/// this sets that list directly through VLC's core library – on the player (for sound outputs it opens later) and on
/// its current sound output, which rebuilds its filters straight away, even mid-stream. The filters' own settings
/// (how strong) come from the VLC engine's start-up options (<see cref="PlayerService"/>).
/// If the core library can't be reached, it quietly does nothing and Dingus plays as before.
/// </summary>
public static class VlcSound
{
    const int VarString = 0x0040; // VLC_VAR_STRING

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int VarSetChecked(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr ListChildren(IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ListRelease(IntPtr list);

    static readonly object Gate = new();
    static bool _loaded;
    static VarSetChecked _set;
    static ListChildren _children;
    static ListRelease _release;

    /// <summary>Why the filters couldn't be set (null when fine) – for the self-test and the log.</summary>
    public static string Problem { get; private set; }

    /// <summary>The filters wanted by the current settings, in VLC's "a:b" form ("" for none).</summary>
    public static string Wanted()
    {
        var s = SettingsStore.Current;
        var f = new List<string>();
        if (s.EvenVolume) f.Add("normvol");     // levelling first: channels arrive at very different volumes
        if (s.NightMode) f.Add("compressor");   // then night mode: quiet dialogue up, explosions down
        return string.Join(":", f);
    }

    /// <summary>Sets the player's sound filters to what the settings want. Safe to call while playing.</summary>
    public static void Apply(MediaPlayer player)
    {
        if (player == null || !Load()) return;
        var filters = Wanted();
        var value = Marshal.StringToCoTaskMemUTF8(filters);
        try
        {
            IntPtr mp = player.NativeReference;
            _set(mp, "audio-filter", VarString, value);
            // The player's sound output is one of its children (the others don't have the setting and say so).
            IntPtr list = _children(mp);
            if (list == IntPtr.Zero) return;
            try
            {
                // vlc_list_t: int i_type; int i_count; vlc_value_t *p_values
                int count = Marshal.ReadInt32(list, 4);
                IntPtr values = Marshal.ReadIntPtr(list, 8);
                for (int i = 0; i < count && i < 64; i++)
                {
                    IntPtr child = Marshal.ReadIntPtr(values, i * 8); // each vlc_value_t is 8 bytes
                    if (child != IntPtr.Zero) _set(child, "audio-filter", VarString, value);
                }
            }
            finally { _release(list); }
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            Log.Error(ex);
        }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    static bool Load()
    {
        lock (Gate)
        {
            if (_loaded) return _set != null;
            _loaded = true;
            if (IntPtr.Size != 8) { Problem = "32-bit"; return false; }
            // VLC's core library is already loaded alongside libvlc – these find that copy.
            string[] names = OperatingSystem.IsWindows()
                ? new[] { "libvlccore.dll", Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "libvlccore.dll") }
                : OperatingSystem.IsMacOS()
                    ? new[] { "libvlccore.dylib", "libvlccore.9.dylib" }
                    : new[] { "libvlccore.so.9", "libvlccore.so" };
            foreach (var n in names)
            {
                if (!NativeLibrary.TryLoad(n, out var h)) continue;
                if (NativeLibrary.TryGetExport(h, "var_SetChecked", out var s) &&
                    NativeLibrary.TryGetExport(h, "vlc_list_children", out var c) &&
                    NativeLibrary.TryGetExport(h, "vlc_list_release", out var r))
                {
                    _set = Marshal.GetDelegateForFunctionPointer<VarSetChecked>(s);
                    _children = Marshal.GetDelegateForFunctionPointer<ListChildren>(c);
                    _release = Marshal.GetDelegateForFunctionPointer<ListRelease>(r);
                    return true;
                }
            }
            Problem = "VLC's core library wasn't found";
            Log.Info("Sound filters unavailable: " + Problem);
            return false;
        }
    }
}
