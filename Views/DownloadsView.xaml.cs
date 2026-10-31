using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>Downloads: what's on its way (with progress, pause and resume), what's downloaded, and where they're saved.</summary>
public partial class DownloadsView : UserControl, IPage
{
    public string Title => "Downloads";
    public bool SupportsSearch => false;
    public string SearchHint => "";

    static readonly Brush OkBrush = Frozen(0x4A, 0xDE, 0x80), WarnBrush = Frozen(0xFB, 0xBF, 0x24), BadBrush = Frozen(0xEF, 0x44, 0x44),
                          Muted = Frozen(0x8B, 0x94, 0xA7);
    static SolidColorBrush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    readonly ObservableCollection<DownloadItem> _active = new(), _done = new();

    public DownloadsView()
    {
        InitializeComponent();
        ActiveList.ItemsSource = _active;
        DoneList.ItemsSource = _done;
        DownloadService.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Sync(); }, DispatcherPriority.Background);
    }

    public void ApplySearch(string query) { }

    public void OnShown()
    {
        DlFolderBox.Text = DownloadService.Folder;
        CheckFolder();
        Sync();
    }

    public void OnHidden() { }

    /// <summary>Brings both lists up to date – in place, so progress moves smoothly.</summary>
    void Sync()
    {
        var all = DownloadService.All;
        Fill(_active, all.Where(d => d.Status != DownloadStatus.Done).OrderBy(d => d.AddedUtc));
        Fill(_done, all.Where(d => d.Status == DownloadStatus.Done).OrderByDescending(d => d.FinishedUtc ?? d.AddedUtc));
        ActiveHeader.Visibility = ActiveList.Visibility = _active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneEmpty.Visibility = _done.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static void Fill(ObservableCollection<DownloadItem> list, IEnumerable<Download> want)
    {
        var ds = want.ToList();
        if (!ds.Select(d => d.Id).SequenceEqual(list.Select(x => x.Download.Id)))
        {
            var old = list.ToDictionary(x => x.Download.Id);
            list.Clear();
            foreach (var d in ds) list.Add(old.TryGetValue(d.Id, out var x) && x.Download == d ? x : new DownloadItem(d));
        }
        foreach (var x in list) x.Refresh();
    }

    static Download DownloadOf(object sender) => ((sender as FrameworkElement)?.DataContext as DownloadItem)?.Download;

    void DlPlay_Click(object sender, RoutedEventArgs e)
    {
        var d = DownloadOf(sender);
        if (d == null) return;
        if (!File.Exists(d.FilePath)) { MainWindow.Instance?.ShowToast("That download's file can't be found."); return; }
        MainWindow.Instance?.PlayVod(DownloadService.ToVod(d), resume: true);
    }

    void DlShow_Click(object sender, RoutedEventArgs e)
    {
        var d = DownloadOf(sender);
        if (d?.FilePath == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{d.FilePath}\"") { UseShellExecute = true }); } catch { }
    }

    void DlPause_Click(object sender, RoutedEventArgs e) { if (DownloadOf(sender) is { } d) DownloadService.Pause(d); }
    void DlResume_Click(object sender, RoutedEventArgs e) { if (DownloadOf(sender) is { } d) DownloadService.Resume(d); }

    void DlRemove_Click(object sender, RoutedEventArgs e)
    {
        var d = DownloadOf(sender);
        if (d == null) return;
        if (d.Status == DownloadStatus.Done && File.Exists(d.FilePath))
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                $"Delete the download “{d.Title}”?\n\nThe file will be deleted from:\n{d.FilePath}",
                "Delete download", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }
        if (DownloadService.Remove(d) is { } problem) MainWindow.Instance?.ShowToast(problem);
    }

    void DlBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Where should Dingus save downloads?",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(DownloadService.Folder) ? DownloadService.Folder : "",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        SetDlFolder(dlg.SelectedPath);
    }

    void DlOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DownloadService.Folder);
            Process.Start(new ProcessStartInfo(DownloadService.Folder) { UseShellExecute = true });
        }
        catch (Exception ex) { MainWindow.Instance?.ShowToast("Couldn't open the folder: " + ex.Message); }
    }

    void DlFolderBox_LostFocus(object sender, RoutedEventArgs e) => SetDlFolder(DlFolderBox.Text);
    void DlFolderBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SetDlFolder(DlFolderBox.Text); }

    void SetDlFolder(string path)
    {
        path = path?.Trim().Trim('"') ?? "";
        if (path != DownloadService.Folder)
        {
            var err = DownloadService.SetFolder(path);
            if (err != null) MainWindow.Instance?.ShowToast(err);
        }
        DlFolderBox.Text = DownloadService.Folder;
        CheckFolder();
    }

    async void CheckFolder()
    {
        var folder = DownloadService.Folder;
        DlFolderStatus.Text = "Checking…";
        DlFolderStatus.Foreground = Muted;
        var (msg, level) = await FolderCheck.RunAsync(folder, "a film in HD is about 2–5 GB");
        if (folder != DownloadService.Folder) return;
        DlFolderStatus.Text = msg;
        DlFolderStatus.Foreground = level == 0 ? OkBrush : level == 1 ? WarnBrush : BadBrush;
    }
}
