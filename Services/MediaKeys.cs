using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Dingus.Services;

/// <summary>
/// Keyboard media keys (play/pause, stop, next, previous) that work even when the app isn't focused.
/// They're only claimed while something is playing, so other apps (Spotify etc.) get them back
/// as soon as playback stops.
/// </summary>
public sealed class MediaKeys
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;
    static readonly (int Id, uint Vk, Key Key)[] Keys =
    {
        (0xB001, 0xB3, Key.MediaPlayPause),
        (0xB002, 0xB2, Key.MediaStop),
        (0xB003, 0xB0, Key.MediaNextTrack),
        (0xB004, 0xB1, Key.MediaPreviousTrack),
    };

    readonly Action<Key> _onKey;
    IntPtr _hwnd;
    bool _on;

    public MediaKeys(Window w, Action<Key> onKey)
    {
        _onKey = onKey;
        w.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(w).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
        };
        w.Closed += (_, _) => Enable(false);
    }

    public void Enable(bool on)
    {
        if (on == _on || _hwnd == IntPtr.Zero) return;
        _on = on;
        foreach (var k in Keys)
        {
            if (on) RegisterHotKey(_hwnd, k.Id, MOD_NOREPEAT, k.Vk); // fails quietly if another app owns the key
            else UnregisterHotKey(_hwnd, k.Id);
        }
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        int id = (int)wParam;
        foreach (var k in Keys)
            if (k.Id == id) { _onKey(k.Key); handled = true; break; }
        return IntPtr.Zero;
    }
}
