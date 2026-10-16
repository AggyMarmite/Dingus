using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dingus.Services;

/// <summary>
/// Parental PIN. Locked categories are hidden everywhere (guide, search, films, shows, Home) until the PIN
/// is entered; they then stay unlocked until the app closes or you lock them again.
/// The PIN itself is never stored – only a salted hash of it.
/// </summary>
public static class Parental
{
    static readonly Regex AdultRx = new(@"\b(adults?|xxx|18\s*\+|porn\w*|erotic\w*|for\s+adults)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Unlocked { get; private set; }
    public static bool HasPin => !string.IsNullOrEmpty(SettingsStore.Current.ParentalPinHash);
    /// <summary>Locks are in force right now.</summary>
    public static bool Active => HasPin && !Unlocked;

    public static event Action Changed;

    /// <summary>Locked category ids of one kind (live | vod | series), whether or not they're unlocked right now.</summary>
    public static HashSet<string> LockedSet(string kind)
    {
        var set = new HashSet<string>();
        var p = DataService.Instance.Active;
        if (!HasPin || p == null) return set;
        var prefix = kind + ":";
        foreach (var l in p.LockedCategories)
            if (l.StartsWith(prefix, StringComparison.Ordinal)) set.Add(l[prefix.Length..]);
        if (SettingsStore.Current.AutoLockAdult)
        {
            var data = DataService.Instance.Catalog;
            var cats = kind switch { "vod" => data.VodCategories, "series" => data.SeriesCategories, _ => data.LiveCategories };
            foreach (var c in cats) if (IsAdultName(c.Name)) set.Add(c.Id);
        }
        return set;
    }

    public static bool IsAdultName(string name) => name != null && AdultRx.IsMatch(name);

    public static void SetPin(string pin)
    {
        var s = SettingsStore.Current;
        if (string.IsNullOrEmpty(pin))
        {
            s.ParentalPinHash = null;
            s.ParentalPinSalt = null;
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            s.ParentalPinSalt = Convert.ToBase64String(salt);
            s.ParentalPinHash = Convert.ToBase64String(Hash(pin, salt));
        }
        Unlocked = false;
        SettingsStore.Save();
        Notify();
    }

    public static bool Verify(string pin)
    {
        var s = SettingsStore.Current;
        if (!HasPin || pin == null) return false;
        try
        {
            var expected = Convert.FromBase64String(s.ParentalPinHash);
            var got = Hash(pin, Convert.FromBase64String(s.ParentalPinSalt ?? ""));
            return CryptographicOperations.FixedTimeEquals(expected, got);
        }
        catch { return false; }
    }

    static byte[] Hash(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);

    /// <summary>Asks for the PIN and unlocks for this session. Returns true if unlocked.</summary>
    public static bool TryUnlock(Window owner)
    {
        if (!Active) return true;
        var pin = AskPin(owner, "Enter your PIN", "Unlocks the locked categories until you close Dingus.");
        if (pin == null) return false;
        if (!Verify(pin))
        {
            MainWindow.Instance?.ShowToast("That PIN isn't right.");
            return false;
        }
        Unlocked = true;
        Notify();
        return true;
    }

    public static void Lock()
    {
        if (!HasPin) return;
        Unlocked = false;
        Notify();
    }

    public static void Notify()
    {
        Changed?.Invoke();
        DataService.Instance.NotifyCatalogChanged(); // everything re-reads the hidden categories
    }

    /// <summary>A PIN prompt (digits only, shown as dots). Returns null if cancelled.</summary>
    public static string AskPin(Window owner, string title, string prompt)
    {
        var res = Application.Current.Resources;
        var box = new PasswordBox
        {
            MaxLength = 8, FontSize = 22, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 18),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = (Brush)res["Surface2Brush"], Foreground = (Brush)res["TextBrush"], BorderBrush = (Brush)res["LineBrush"],
            CaretBrush = (Brush)res["TextBrush"],
        };
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        var ok = new Button { Content = "OK", Style = (Style)res["PrimaryButton"], IsDefault = true };
        var cancel = new Button { Content = "Cancel", Style = (Style)res["GhostButton"], Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)res["IconFont"], FontSize = 22, Foreground = (Brush)res["AccentBrush"] });
        stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        stack.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["MutedBrush"] });
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        var w = new Window
        {
            Owner = owner, Title = title, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false, Content = stack, BorderThickness = new Thickness(1), Topmost = owner?.Topmost == true,
            Background = (Brush)res["SurfaceBrush"], BorderBrush = (Brush)res["LineBrush"],
            Foreground = (Brush)res["TextBrush"], FontFamily = (FontFamily)res["UiFont"], FontSize = 13,
        };
        ok.Click += (_, _) => { if (box.Password.Length >= 4) w.DialogResult = true; };
        w.Loaded += (_, _) => box.Focus();
        stack.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
        return w.ShowDialog() == true ? box.Password : null;
    }
}
