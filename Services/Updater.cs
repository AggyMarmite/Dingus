using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dingus.Services;

/// <summary>
/// In-app updates from the GitHub releases page: checks the latest release once a day, offers it,
/// downloads the installer and runs it silently (it closes Dingus, updates it and opens it again).
/// </summary>
public static class Updater
{
    public const string Repo = "AggyMarmite/Dingus";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    static readonly HttpClient Client = MakeClient();

    public record Release(Version Version, string Tag, string Notes, string InstallerUrl, long Size, string PageUrl);

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Dingus/{Current.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>The newest release on GitHub, or null if there isn't one with an installer.</summary>
    public static async Task<Release> LatestAsync()
    {
        using var resp = await Client.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        string url = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("Dingus-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
            }
        }
        if (url == null) return null;
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : ReleasesPage;
        return new Release(v, tag, notes, url, size, page);
    }

    static bool IsNewer(Version v) => v > new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));

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

    static async Task<string> DownloadAsync(Release r, Action<double> progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Dingus-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Dingus-Setup-{r.Version.ToString(3)}.exe");
        using var resp = await Client.GetAsync(r.InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? r.Size;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0 && DateTime.UtcNow - last > TimeSpan.FromMilliseconds(150))
                {
                    last = DateTime.UtcNow;
                    progress(done * 100.0 / total);
                }
            }
        }
        if (r.Size > 0 && new FileInfo(path).Length != r.Size) throw new IOException("the download was incomplete");
        return path;
    }

    /// <summary>Release notes without the markdown clutter.</summary>
    static string Tidy(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var lines = md.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !l.StartsWith("**Full Changelog**"))
            .Select(l => l.StartsWith("## ") ? l[3..].ToUpperInvariant() : l.StartsWith("* ") || l.StartsWith("- ") ? "•  " + l[2..] : l)
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\*\*(.+?)\*\*", "$1"))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @" by @\S+ in https://\S+", ""));
        return string.Join("\n", lines).Trim();
    }
}
