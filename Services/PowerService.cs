using System.Windows;
using Microsoft.Win32;

namespace Dingus.Services;

/// <summary>
/// Battery-aware mode: watches for the laptop being plugged in or unplugged, and tells Windows which
/// graphics chip Dingus should use – the dedicated card on mains power, built-in graphics on battery.
/// </summary>
public static class PowerService
{
    const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>Raised on the UI thread when the power source changes.</summary>
    public static event Action Changed;

    static PowerService()
    {
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode != PowerModes.StatusChange) return;
            Application.Current?.Dispatcher.InvokeAsync(() => Changed?.Invoke());
        };
    }

    public static bool OnBattery => SystemParameters.PowerLineStatus == PowerLineStatus.Offline;

    static string ExePath => Environment.ProcessPath;

    /// <summary>
    /// Sets this app's Windows graphics preference: 2 = high performance (dedicated card), 1 = power saving.
    /// The video engine picks it up the next time it opens a stream.
    /// </summary>
    public static void ApplyGpuPreference()
    {
        var s = SettingsStore.Current;
        if (!s.BatteryAware || string.IsNullOrEmpty(ExePath)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuKey);
            if (key == null) return;
            // Remember what was there before we first took over, so turning the mode off puts it back.
            if (!s.GpuPreferenceSaved)
            {
                s.GpuPreferenceOriginal = key.GetValue(ExePath) as string;
                s.GpuPreferenceSaved = true;
                SettingsStore.Save();
            }
            key.SetValue(ExePath, OnBattery ? "GpuPreference=1;" : "GpuPreference=2;", RegistryValueKind.String);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>Gives the graphics choice back to Windows (used when battery-aware mode is switched off).</summary>
    public static void RestoreGpuPreference()
    {
        var s = SettingsStore.Current;
        if (!s.GpuPreferenceSaved || string.IsNullOrEmpty(ExePath)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuKey);
            if (s.GpuPreferenceOriginal == null) key?.DeleteValue(ExePath, false);
            else key?.SetValue(ExePath, s.GpuPreferenceOriginal, RegistryValueKind.String);
        }
        catch (Exception ex) { Log.Error(ex); }
        s.GpuPreferenceSaved = false;
        s.GpuPreferenceOriginal = null;
        SettingsStore.Save();
    }

    public static string Describe() => OnBattery
        ? "On battery – using built-in graphics, 4K channels play in 1080p where available, background refreshes paused."
        : "Plugged in – using your dedicated graphics card.";

}

/// <summary>Keeps the screen on and the PC awake while video is playing (like any video player should).</summary>
public static class KeepAwake
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    static bool _video, _recording;
    static uint _applied = uint.MaxValue;

    /// <summary>Video playing: keep the screen on. Call from the UI thread (the setting belongs to that thread).</summary>
    public static void Set(bool on) { _video = on; Apply(); }

    /// <summary>A recording running: keep the PC awake (the screen may still turn off).</summary>
    public static void SetRecording(bool on) { _recording = on; Apply(); }

    static void Apply()
    {
        uint flags = ES_CONTINUOUS | (_video || _recording ? ES_SYSTEM_REQUIRED : 0) | (_video ? ES_DISPLAY_REQUIRED : 0);
        if (flags == _applied) return;
        _applied = flags;
        try { SetThreadExecutionState(flags); } catch { }
    }
}
