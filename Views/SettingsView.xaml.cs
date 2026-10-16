using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

public partial class SettingsView : UserControl, IPage
{
    bool _loading;
    string _kind = "live";

    public string Title => "Settings";
    public bool SupportsSearch => false;
    public string SearchHint => "";

    public SettingsView()
    {
        InitializeComponent();
        DataService.Instance.CatalogChanged += () => { if (IsVisible) Populate(); };
        PowerService.Changed += () => { if (IsVisible && S.BatteryAware) BatteryStatus.Text = PowerService.Describe(); };
        DataService.Instance.EpgChanged += () => { if (IsVisible) PopulateStatus(); };
        DataService.Instance.BusyChanged += (busy, _) => { if (!busy && IsVisible) PopulateStatus(); };

    }

    public void ApplySearch(string query) { }
    public void OnShown() => Populate();

    /// <summary>The category list can be thousands of tick boxes – drop them while Settings isn't open.</summary>
    public void OnHidden()
    {
        CategoryChecks.ItemsSource = null;
        PlaylistPanel.Children.Clear();
    }

    static AppSettings S => SettingsStore.Current;

    // ------------------------------------------------------------------ populate

    void Populate()
    {
        _loading = true;
        try
        {
            PopulatePlaylists();
            PopulateStatus();

            var p = DataService.Instance.Active;
            EpgUrlBox.Text = p?.EpgUrl ?? "";
            AutoRefreshToggle.IsChecked = S.AutoRefresh;
            HwToggle.IsChecked = S.HardwareDecoding;
            AfrToggle.IsChecked = S.AdaptiveFrameRate;
            MergeToggle.IsChecked = S.MergeDuplicates;
            StartGuideToggle.IsChecked = S.StartOnGuide;
            BatteryToggle.IsChecked = S.BatteryAware;
            LowMemToggle.IsChecked = S.LowMemoryInterface;
            S.StartWithWindows = ThemeService.IsStartWithWindows(); // the installer can switch it on too
            StartWinToggle.IsChecked = S.StartWithWindows;
            CheckChip(ThemeChips, S.Theme ?? "midnight");
            BuildAccentChips();
            BatteryStatus.Text = S.BatteryAware ? PowerService.Describe() : "";
            CheckChip(QualityChips, S.PreferredQuality ?? "best");
            CheckChip(IntervalChips, S.RefreshIntervalHours.ToString());
            CheckChip(ShiftChips, S.EpgShiftMinutes.ToString());
            CheckChip(FormatChips, S.PreferHls ? "hls" : "ts");
            CheckChip(BufferChips, S.NetworkCachingMs.ToString());
            CheckChip(CatKindChips, _kind);
            PopulateCategories();

            var bytes = ImageLoader.DiskCacheBytes();
            CacheText.Text = $"{bytes / 1024.0 / 1024.0:0.0} MB on disk";
            var v = typeof(SettingsView).Assembly.GetName().Version;
            VersionText.Text = $"Dingus {v?.ToString(3)}  ·  Video engine: VLC  ·  Using {Memory.WorkingSetMB():N0} MB of memory";
        }
        finally { _loading = false; }
    }

    // ------------------------------------------------------------------ appearance

    // What the app started with – the restart bar shows only when that's been changed.
    static readonly string StartTheme = SettingsStore.Current.Theme, StartAccent = SettingsStore.Current.Accent;

    void BuildAccentChips()
    {
        if (AccentChips.Children.Count == 0)
            foreach (var (id, name, color) in ThemeService.Accents)
            {
                var swatch = new System.Windows.Shapes.Ellipse
                {
                    Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = new System.Windows.Media.SolidColorBrush(color),
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(swatch);
                content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = content, Tag = id, GroupName = "Accent" };
                rb.Checked += Accent_Checked;
                AccentChips.Children.Add(rb);
            }
        foreach (var rb in AccentChips.Children.OfType<RadioButton>())
            rb.IsChecked = (rb.Tag as string) == (S.Accent ?? "blue");
        UpdateRestartBar();
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string t }) return;
        S.Theme = t;
        SettingsStore.Save();
        UpdateRestartBar();
    }

    void Accent_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string t }) return;
        S.Accent = t;
        SettingsStore.Save();
        UpdateRestartBar();
    }

    void UpdateRestartBar() =>
        RestartBar.Visibility = S.Theme != StartTheme || S.Accent != StartAccent ? Visibility.Visible : Visibility.Collapsed;

    void Restart_Click(object sender, RoutedEventArgs e) => App.Restart();

    void StartWin_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.StartWithWindows = StartWinToggle.IsChecked == true;
        ThemeService.ApplyStartWithWindows(S.StartWithWindows);
        SettingsStore.Save();
    }

    static void CheckChip(Panel panel, string tag)
    {
        foreach (var rb in panel.Children.OfType<RadioButton>())
            rb.IsChecked = (rb.Tag as string) == tag;
    }

    void PopulatePlaylists()
    {
        PlaylistPanel.Children.Clear();
        var active = DataService.Instance.Active;
        foreach (var p in S.Playlists)
        {
            bool isActive = p == active;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(buttons, Dock.Right);
            if (!isActive) buttons.Children.Add(MakeButton("Use", "PrimaryButton", () => UsePlaylist(p)));
            buttons.Children.Add(MakeButton("Edit", "BaseButton", () => EditPlaylist(p)));
            buttons.Children.Add(MakeButton("Delete", "DangerButton", () => DeletePlaylist(p)));
            row.Children.Add(buttons);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.SemiBold });
            if (isActive)
            {
                var badge = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 1, 7, 2),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "In use", FontSize = 11 },
                };
                title.Children.Add(badge);
            }
            info.Children.Add(title);
            info.Children.Add(new TextBlock
            {
                Text = p.Description,
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            });
            row.Children.Add(info);
            PlaylistPanel.Children.Add(row);
        }
        if (S.Playlists.Count == 0)
            PlaylistPanel.Children.Add(new TextBlock
            {
                Text = "No playlists yet.",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 0, 0, 8),
            });
    }

    Button MakeButton(string text, string style, Action onClick)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(style), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    void PopulateStatus()
    {
        var ds = DataService.Instance;
        var p = ds.Active;
        PlaylistUpdated.Text = p == null || p.LastRefreshUtc == default
            ? "Never refreshed"
            : $"Last updated {Ago(p.LastRefreshUtc)} · {ds.Catalog.Channels.Count:N0} channels, {ds.Catalog.Movies.Count:N0} films, {ds.Catalog.Series.Count:N0} shows";
        EpgUpdated.Text = p == null || p.LastEpgRefreshUtc == default
            ? "Never downloaded"
            : $"Last updated {Ago(p.LastEpgRefreshUtc)}";
        EpgMatchText.Text = ds.Catalog.Channels.Count == 0
            ? ""
            : $"{ds.Epg.MatchedChannels:N0} of {ds.Catalog.Channels.Count:N0} channels have guide data.";

        var acc = ds.Catalog.Account;
        AccountSection.Visibility = acc == null ? Visibility.Collapsed : Visibility.Visible;
        if (acc != null)
        {
            AccStatus.Text = (acc.Trial ? "Trial · " : "") + Capitalise(acc.Status);
            AccExpiry.Text = acc.ExpiresUtc is DateTime exp ? exp.ToLocalTime().ToString("d MMM yyyy") : "Never";
            AccConnections.Text = acc.MaxConnections > 0 ? $"{acc.ActiveConnections} of {acc.MaxConnections} in use" : "—";
        }
    }

    static string Capitalise(string s) => string.IsNullOrEmpty(s) ? "Unknown" : char.ToUpper(s[0]) + s.Substring(1);

    static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return utc.ToLocalTime().ToString("d MMM, HH:mm");
    }

    void PopulateCategories()
    {
        var data = DataService.Instance.Catalog;
        var cats = _kind switch
        {
            "vod" => data.VodCategories,
            "series" => data.SeriesCategories,
            _ => data.LiveCategories,
        };
        var hidden = DataService.Instance.HiddenSet(_kind);
        var boxes = new List<CheckBox>(cats.Count);
        foreach (var c in cats)
        {
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = c.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                IsChecked = !hidden.Contains(c.Id),
                Style = (Style)FindResource("Tick"),
                Tag = c.Id,
                ToolTip = c.Name,
            };
            cb.Checked += Category_Toggled;
            cb.Unchecked += Category_Toggled;
            boxes.Add(cb);
        }
        CategoryChecks.ItemsSource = boxes;
        NoCategories.Visibility = cats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ playlists

    async void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PlaylistDialog(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        S.Playlists.Add(dlg.Result);
        S.ActivePlaylistId = dlg.Result.Id;
        SettingsStore.Save();
        MainWindow.Instance?.OnPlaylistChanged();
        Populate();
        await DataService.Instance.LoadActiveAsync();
    }

    async void UsePlaylist(PlaylistConfig p)
    {
        S.ActivePlaylistId = p.Id;
        SettingsStore.Save();
        MainWindow.Instance?.OnPlaylistChanged();
        Populate();
        await DataService.Instance.LoadActiveAsync();
    }

    async void EditPlaylist(PlaylistConfig p)
    {
        var dlg = new PlaylistDialog(p) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        SettingsStore.Save();
        Populate();
        if (p == DataService.Instance.Active)
        {
            MainWindow.Instance?.OnPlaylistChanged();
            await DataService.Instance.LoadActiveAsync();
            await DataService.Instance.RefreshAsync();
        }
    }

    async void DeletePlaylist(PlaylistConfig p)
    {
        var r = MessageBox.Show(Window.GetWindow(this), $"Remove the playlist “{p.Name}”?\n\nIts cached channels and guide will be deleted.",
            "Remove playlist", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        bool wasActive = p == DataService.Instance.Active;
        S.Playlists.Remove(p);
        DataService.Instance.DeletePlaylistData(p);
        if (wasActive) S.ActivePlaylistId = S.Playlists.FirstOrDefault()?.Id;
        SettingsStore.Save();
        Populate();
        if (wasActive)
        {
            MainWindow.Instance?.OnPlaylistChanged();
            await DataService.Instance.LoadActiveAsync();
        }
    }

    // ------------------------------------------------------------------ refresh

    async void RefreshPlaylist_Click(object sender, RoutedEventArgs e) => await DataService.Instance.RefreshAsync();
    async void RefreshEpg_Click(object sender, RoutedEventArgs e) => await DataService.Instance.RefreshEpgAsync();

    void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AutoRefresh = AutoRefreshToggle.IsChecked == true;
        SettingsStore.Save();
    }

    void Interval_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || !TryTag(sender, out var v)) return;
        S.RefreshIntervalHours = v;
        SettingsStore.Save();
    }

    // ------------------------------------------------------------------ guide

    async void SaveEpgUrl_Click(object sender, RoutedEventArgs e)
    {
        var p = DataService.Instance.Active;
        if (p == null) return;
        p.EpgUrl = EpgUrlBox.Text.Trim();
        SettingsStore.Save();
        await DataService.Instance.RefreshEpgAsync();
    }

    async void Shift_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || !TryTag(sender, out var v)) return;
        S.EpgShiftMinutes = v;
        SettingsStore.Save();
        await DataService.Instance.ReloadEpgAsync();
    }

    // ------------------------------------------------------------------ categories

    void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string k) return;
        _kind = k;
        if (!_loading) PopulateCategories();
    }

    void Category_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox cb || cb.Tag is not string id) return;
        DataService.Instance.SetHidden(_kind, id, cb.IsChecked != true);
    }

    void ShowAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    void HideAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    void SetAll(bool visible)
    {
        if (CategoryChecks.ItemsSource is not IEnumerable<CheckBox> boxes) return;
        var list = boxes.ToList();
        _loading = true; // tick the boxes without saving once per box
        try { foreach (var cb in list) cb.IsChecked = visible; }
        finally { _loading = false; }
        DataService.Instance.SetHiddenMany(_kind, list.Select(cb => (string)cb.Tag), !visible);
    }

    // ------------------------------------------------------------------ playback

    void Hw_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.HardwareDecoding = HwToggle.IsChecked == true;
        SettingsStore.Save();
    }

    void StartGuide_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.StartOnGuide = StartGuideToggle.IsChecked == true;
        SettingsStore.Save();
    }

    void Merge_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.MergeDuplicates = MergeToggle.IsChecked == true;
        SettingsStore.Save();
        DataService.Instance.NotifyCatalogChanged(); // rebuild the guide with/without merging
    }

    void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.Tag is not string t) return;
        S.PreferredQuality = t;
        SettingsStore.Save();
    }

    void Battery_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.BatteryAware = BatteryToggle.IsChecked == true;
        SettingsStore.Save();
        if (S.BatteryAware) PowerService.ApplyGpuPreference();
        else PowerService.RestoreGpuPreference();
        BatteryStatus.Text = S.BatteryAware ? PowerService.Describe() + " Takes effect from the next channel you open." : "";
    }

    void LowMem_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.LowMemoryInterface = LowMemToggle.IsChecked == true;
        SettingsStore.Save();
    }


    void Afr_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AdaptiveFrameRate = AfrToggle.IsChecked == true;
        SettingsStore.Save();
    }

    void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.Tag is not string t) return;
        S.PreferHls = t == "hls";
        SettingsStore.Save();
    }

    void Buffer_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || !TryTag(sender, out var v)) return;
        S.NetworkCachingMs = v;
        SettingsStore.Save();
    }

    // ------------------------------------------------------------------ storage

    void ClearImages_Click(object sender, RoutedEventArgs e)
    {
        ImageLoader.ClearDiskCache();
        CacheText.Text = "0.0 MB on disk";
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex); }
    }

    static bool TryTag(object sender, out int value)
    {
        value = 0;
        return (sender as FrameworkElement)?.Tag is string s && int.TryParse(s, out value);
    }
}
