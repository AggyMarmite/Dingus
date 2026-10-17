using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dingus.Services;

/// <summary>The PIN prompt (Windows).</summary>
public static partial class Parental
{
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
