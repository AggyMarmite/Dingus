using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Accent colour and background theme. Applied at start-up by overriding the colour resources in
/// Theme.xaml before any window is created (so a change takes effect the next time the app opens).
/// </summary>
public static class ThemeService
{
    public static readonly (string Id, string Name, Color Color)[] Accents =
    {
        ("blue", "Blue", Color.FromRgb(0x3B, 0x82, 0xF6)),
        ("purple", "Purple", Color.FromRgb(0x8B, 0x5C, 0xF6)),
        ("teal", "Teal", Color.FromRgb(0x14, 0xB8, 0xA6)),
        ("green", "Green", Color.FromRgb(0x22, 0xC5, 0x5E)),
        ("orange", "Orange", Color.FromRgb(0xF9, 0x73, 0x16)),
        ("pink", "Pink", Color.FromRgb(0xEC, 0x48, 0x99)),
    };

    // Bg, Surface, Surface2, Surface3, Line
    static readonly Dictionary<string, string[]> Themes = new()
    {
        ["midnight"] = new[] { "#0A0D13", "#10151F", "#171D2A", "#212938", "#232B3B" },
        ["slate"] = new[] { "#15181E", "#1C2027", "#252A33", "#2F3541", "#343B48" },
        ["black"] = new[] { "#000000", "#0B0B0E", "#141419", "#1D1D24", "#24242C" },
    };

    public static void Apply(ResourceDictionary res, AppSettings s)
    {
        try
        {
            var accent = Accents.FirstOrDefault(a => a.Id == s.Accent).Color;
            if (accent == default) accent = Accents[0].Color;
            var t = Themes.TryGetValue(s.Theme ?? "", out var v) ? v : Themes["midnight"];
            var bg = Parse(t[0]);

            Set(res, "Bg", bg);
            Set(res, "Surface", Parse(t[1]));
            Set(res, "Surface2", Parse(t[2]));
            Set(res, "Surface3", Parse(t[3]));
            Set(res, "Line", Parse(t[4]));
            Set(res, "Accent", accent);
            Set(res, "AccentSoft", Mix(accent, bg, 0.28)); // selected rows, chips, "now" in the guide
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>
    /// Replaces the entry inside Theme.xaml itself (not just on top of it): the styles there look their
    /// colours up in their own dictionary when they're first used, so that's where the change must go.
    /// </summary>
    static void Set(ResourceDictionary res, string name, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Put(res, name + "Color", c);
        Put(res, name + "Brush", b);
    }

    static void Put(ResourceDictionary res, string key, object value)
    {
        foreach (var md in res.MergedDictionaries)
            if (md.Contains(key)) { md[key] = value; return; }
        res[key] = value;
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static Color Mix(Color a, Color b, double amountOfA) => Color.FromRgb(
        (byte)(a.R * amountOfA + b.R * (1 - amountOfA)),
        (byte)(a.G * amountOfA + b.G * (1 - amountOfA)),
        (byte)(a.B * amountOfA + b.B * (1 - amountOfA)));

    /// <summary>The native title bar colour for the current theme (COLORREF 0x00BBGGRR).</summary>
    public static int TitleBarColorRef()
    {
        var c = Application.Current?.TryFindResource("SurfaceColor") is Color col ? col : Color.FromRgb(0x10, 0x15, 0x1F);
        return c.R | (c.G << 8) | (c.B << 16);
    }

    // ---------------------------------------------------------------- start with Windows

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void ApplyStartWithWindows(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on && !string.IsNullOrEmpty(Environment.ProcessPath)) key.SetValue("Dingus", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Dingus", false);
            key.DeleteValue("NimbusTV", false); // left over from the old name
        }
        catch (Exception ex) { Log.Error(ex); }
    }
}
