using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dingus.Services;

/// <summary>
/// Updates on Linux and Mac. Linux: the AppImage downloads the new AppImage from the releases page, swaps
/// itself for it and starts again. Mac: downloads the new .dmg, copies the Dingus app out of it over this one
/// and starts again.
/// </summary>
public static partial class Updater
{
    static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;

    /// <summary>The AppImage file we're running from, if any.</summary>
    static string AppImagePath => Environment.GetEnvironmentVariable("APPIMAGE");

    /// <summary>The end of the release file name for this computer.</summary>
    internal static string AssetSuffix => Os.Mac ? $"-mac-{Os.MacArch}.dmg" : "-x86_64.AppImage";

    static bool CanSwap
    {
        get
        {
            if (!Os.Mac) return AppImagePath != null && File.Exists(AppImagePath);
            var app = Os.MacBundle;
            if (app == null) return false;
            try
            {
                // Needs to be able to write next to the app (fine in Applications for an admin account).
                var probe = Path.Combine(Path.GetDirectoryName(app)!, ".dingus-update-check");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

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
        await Offer(owner, r);
    }

    static async Task Offer(Window owner, Release r)
    {
        bool canSwap = CanSwap;
        var update = new Button { Content = canSwap ? "Update now" : "Open the download page", Classes = { "primary" }, IsDefault = true };
        var later = new Button { Content = "Later", IsCancel = true };
        var skip = new Button { Content = "Skip this version", Classes = { "ghost" } };
        var status = new TextBlock { Foreground = Res("MutedBrush"), Margin = new Thickness(0, 12, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var notes = new TextBox
        {
            Text = Tidy(r.Notes), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, FontSize = 12,
            Margin = new Thickness(0, 12, 0, 18),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(update); buttons.Children.Add(later); buttons.Children.Add(skip);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = $"Dingus {r.Version.ToString(3)} is available", FontSize = 19, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = canSwap
                ? $"You have {Current.ToString(3)}. The update downloads ({r.Size / 1024.0 / 1024.0:0} MB), then Dingus closes, updates and opens again."
                : $"You have {Current.ToString(3)}. Download the new version from the releases page.",
            Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        if (notes.Text.Length > 0) stack.Children.Add(notes); else stack.Children.Add(new Border { Height = 18 });
        stack.Children.Add(buttons);
        stack.Children.Add(status);
        var w = Dialogs.Frame("Update available", stack, 520);
        later.Click += (_, _) => w.Close();
        skip.Click += (_, _) => { SettingsStore.Current.SkippedVersion = r.Tag; SettingsStore.Save(); w.Close(); };
        update.Click += async (_, _) =>
        {
            if (!canSwap) { OpenUrl(r.PageUrl ?? ReleasesPage); w.Close(); return; }
            update.IsEnabled = later.IsEnabled = skip.IsEnabled = false;
            try
            {
                var path = await DownloadAsync(r, p => status.Text = $"Downloading… {p:0}%");
                status.Text = "Installing…";
                if (Os.Mac)
                {
                    await Task.Run(() => InstallMac(path));
                    App.Restart();
                    return;
                }
                var target = AppImagePath;
                var staged = target + ".new";
                File.Copy(path, staged, true);
                File.SetUnixFileMode(staged, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                File.Move(staged, target, true); // the running copy keeps working until it closes
                App.Restart();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                status.Text = "The update didn't work: " + Http.Friendly(ex) + ". You can get it from the releases page instead.";
                update.IsEnabled = later.IsEnabled = skip.IsEnabled = true;
                update.Content = "Try again";
            }
        };
        await Dialogs.Show(w, owner);
    }

    public static void OpenUrl(string url) => Os.Open(url);

    /// <summary>Mac: copies Dingus.app out of the downloaded disk image over the running one.</summary>
    static void InstallMac(string dmg)
    {
        var app = Os.MacBundle;
        var parent = Path.GetDirectoryName(app)!;
        var mount = Path.Combine(Path.GetTempPath(), "Dingus-update-mount");
        var staged = Path.Combine(parent, ".Dingus-new.app");
        var old = Path.Combine(parent, ".Dingus-old.app");
        Directory.CreateDirectory(mount);
        if (Os.Run("hdiutil", $"attach -nobrowse -readonly -noautoopen -mountpoint {Os.Quote(mount)} {Os.Quote(dmg)}") != 0)
            throw new IOException("the download couldn't be opened");
        try
        {
            var src = Directory.GetDirectories(mount, "*.app").FirstOrDefault()
                      ?? throw new IOException("the download doesn't contain the app");
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            if (Os.Run("ditto", $"{Os.Quote(src)} {Os.Quote(staged)}") != 0) throw new IOException("the new version couldn't be copied");
        }
        finally { Os.Run("hdiutil", $"detach -quiet -force {Os.Quote(mount)}", 30_000); }
        if (Directory.Exists(old)) Directory.Delete(old, true);
        Directory.Move(app, old);      // the running copy keeps working from its new place until it closes
        Directory.Move(staged, app);
        try { Directory.Delete(old, true); } catch { }
        try { File.Delete(dmg); } catch { }
    }
}
