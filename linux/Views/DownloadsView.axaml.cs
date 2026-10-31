using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>Downloads: what's on its way (with progress, pause and resume), what's downloaded, and where they're saved.</summary>
public partial class DownloadsView : UserControl, IPage
{
    public string Title => "Downloads";
    public bool SupportsSearch => false;
    public string SearchHint => "";

    static readonly IBrush OkBrush = Frozen(0x4A, 0xDE, 0x80), WarnBrush = Frozen(0xFB, 0xBF, 0x24), BadBrush = Frozen(0xEF, 0x44, 0x44),
                           Muted = Frozen(0x8B, 0x94, 0xA7);
    static IBrush Frozen(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b)).ToImmutable();

    readonly ObservableCollection<DownloadItem> _active = new(), _done = new();

    public DownloadsView()
    {
        InitializeComponent();
        ActiveList.ItemsSource = _active;
        DoneList.ItemsSource = _done;
        DownloadService.Changed += () => Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible) Sync(); }, DispatcherPriority.Background);
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
        ActiveHeader.IsVisible = ActiveList.IsVisible = _active.Count > 0;
        DoneEmpty.IsVisible = _done.Count == 0;
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

    static Download DownloadOf(object sender) => ((sender as Control)?.DataContext as DownloadItem)?.Download;

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
        Updater.OpenUrl(Path.GetDirectoryName(d.FilePath));
    }

    void DlPause_Click(object sender, RoutedEventArgs e) { if (DownloadOf(sender) is { } d) DownloadService.Pause(d); }
    void DlResume_Click(object sender, RoutedEventArgs e) { if (DownloadOf(sender) is { } d) DownloadService.Resume(d); }

    async void DlRemove_Click(object sender, RoutedEventArgs e)
    {
        var d = DownloadOf(sender);
        if (d == null) return;
        if (d.Status == DownloadStatus.Done && File.Exists(d.FilePath)
            && !await Dialogs.Confirm(TopLevel.GetTopLevel(this) as Window, $"Delete the download “{d.Title}”?",
                $"The file will be deleted from:\n{d.FilePath}", "Delete", danger: true))
            return;
        if (DownloadService.Remove(d) is { } problem) MainWindow.Instance?.ShowToast(problem);
    }

    async void DlBrowse_Click(object sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var start = Directory.Exists(DownloadService.Folder) ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(DownloadService.Folder)) : null;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Where should Dingus save downloads?", AllowMultiple = false, SuggestedStartLocation = start,
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) SetDlFolder(path);
    }

    void DlOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DownloadService.Folder);
            Updater.OpenUrl(DownloadService.Folder);
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
