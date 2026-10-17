using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dingus.Services;

/// <summary>The "update available" window (Windows).</summary>
public static partial class Updater
{
    /// <summary>
    /// Checks for an update. Automatic checks are quiet (at most once a day, skipped versions ignored);
    /// a check from Settings always says what it found.
    /// </summary>
    public static async Task CheckAsync(Window owner, bool manual)
    {
        var s = SettingsStore.Current;
        if (!manual && (!s.AutoUpdate || DateTime.UtcNow - s.LastUpdateCheckUtc < TimeSpan.FromHours(20))) return;
        Release r;
        try
        {
            r = await LatestAsync();
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (manual) MainWindow.Instance?.ShowToast("Couldn't check for updates: " + Http.Friendly(ex));
            return;
        }
        if (r == null || !IsNewer(r.Version))
        {
            if (manual) MainWindow.Instance?.ShowToast($"You're up to date (Dingus {Current.ToString(3)}).");
            return;
        }
        if (!manual && s.SkippedVersion == r.Tag) return;
        Offer(owner, r);
    }

    static void Offer(Window owner, Release r)
    {
        var res = Application.Current.Resources;
        var notes = new TextBox
        {
            Text = Tidy(r.Notes), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 18), FontSize = 12,
        };
        var update = new Button { Content = "Update now", Style = (Style)res["PrimaryButton"], IsDefault = true };
        var later = new Button { Content = "Later", Style = (Style)res["BaseButton"], Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var skip = new Button { Content = "Skip this version", Style = (Style)res["GhostButton"], Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(update);
        buttons.Children.Add(later);
        buttons.Children.Add(skip);
        var status = new TextBlock { Foreground = (Brush)res["MutedBrush"], Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        stack.Children.Add(new TextBlock { Text = $"Dingus {r.Version.ToString(3)} is available", FontSize = 19, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = $"You have {Current.ToString(3)}. The update downloads ({r.Size / 1024.0 / 1024.0:0} MB), then Dingus closes, updates and opens again.",
            Foreground = (Brush)res["MutedBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        if (notes.Text.Length > 0) stack.Children.Add(notes); else stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(buttons);
        stack.Children.Add(status);
        var w = new Window
        {
            Owner = owner, Title = "Update available", Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = stack, BorderThickness = new Thickness(1), Topmost = owner?.Topmost == true,
            Background = (Brush)res["SurfaceBrush"], BorderBrush = (Brush)res["LineBrush"],
            Foreground = (Brush)res["TextBrush"], FontFamily = (FontFamily)res["UiFont"], FontSize = 13,
        };
        stack.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed && e.OriginalSource is not TextBox) w.DragMove(); };
        later.Click += (_, _) => w.Close();
        skip.Click += (_, _) => { SettingsStore.Current.SkippedVersion = r.Tag; SettingsStore.Save(); w.Close(); };
        update.Click += async (_, _) =>
        {
            update.IsEnabled = later.IsEnabled = skip.IsEnabled = false;
            try
            {
                var path = await DownloadAsync(r, p => status.Text = $"Downloading… {p:0}%");
                status.Text = "Installing…";
                Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
                App.ReleaseSingleInstance(); // let the updated copy start
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                status.Text = "The download didn't work: " + Http.Friendly(ex) + ". You can get it from the releases page instead.";
                update.IsEnabled = later.IsEnabled = skip.IsEnabled = true;
                update.Content = "Try again";
            }
        };
        w.ShowDialog();
    }
}
