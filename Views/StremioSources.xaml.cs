using System.Windows;
using System.Windows.Controls;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Every Stremio add-on's sources for a film or an episode, filling in as each add-on answers – with Play (torrents go
/// through Real-Debrid), Download, and web pages opening in the browser. Used on Stremio titles' pages and, as
/// "More sources", under your playlist's films and episodes.
/// </summary>
public partial class StremioSources : UserControl
{
    readonly List<StremioStream> _streams = new();
    int _asked, _answered;
    CancellationTokenSource _cts;
    StremioMeta _meta;
    StremioVideo _episode;

    public StremioSources() => InitializeComponent();

    /// <summary>The section's heading ("SOURCES", "MORE SOURCES").</summary>
    public string Heading { get; set; } = "SOURCES";

    public int Count => _streams.Count;

    public void Clear()
    {
        _cts?.Cancel();
        _meta = null;
        _episode = null;
        _streams.Clear();
        SourceList.ItemsSource = null;
        SourcesStatus.Text = "";
    }

    /// <summary>Shows a message instead of sources (e.g. "Looking for it in Stremio…").</summary>
    public void Message(string heading, string text)
    {
        Clear();
        SourcesHeader.Text = heading ?? Heading;
        SourcesStatus.Text = text;
    }

    /// <summary>Asks every add-on for sources for a film (<paramref name="episode"/> null) or an episode of a show.</summary>
    public async void Show(StremioMeta meta, StremioVideo episode, string what = null)
    {
        Clear();
        var cts = _cts = new CancellationTokenSource();
        _meta = meta;
        _episode = episode;
        string type = episode == null ? meta.Type : "series", id = episode?.Id ?? meta.Id;
        SourcesHeader.Text = what == null ? Heading : Heading + "  ·  " + what.ToUpperInvariant();
        _asked = Stremio.StreamAddonCount(type, id);
        _answered = 0;
        if (_asked == 0)
        {
            SourcesStatus.Text = "None of your add-ons has sources for this. Sign in to Stremio or add a source add-on in Settings.";
            return;
        }
        SourcesStatus.Text = _asked == 1 ? "Asking 1 add-on…" : $"Asking {_asked} add-ons…";
        var errors = new List<string>();
        await Stremio.StreamsAsync(type, id, (a, list, err) => Dispatcher.InvokeAsync(() =>
        {
            if (cts.IsCancellationRequested) return;
            _answered++;
            if (err != null) errors.Add($"{a.Name}: {err}");
            _streams.AddRange(list);
            SourceList.ItemsSource = Stremio.Items(_streams, RealDebrid.Connected);
            SourcesStatus.Text = Stremio.SourcesStatus(_streams, _asked, _answered, errors);
        }), cts.Token);
    }

    async void Source_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StremioSourceItem it || it.Busy || _meta == null) return;
        var s = it.Stream;
        if (s.Kind == StreamKind.External)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s.ExternalUrl) { UseShellExecute = true }); } catch { }
            return;
        }
        it.Busy = true;
        try
        {
            var url = await Resolve(s);
            MainWindow.Instance?.PlayVod(Stremio.ToVod(_meta, _episode, url), resume: true);
        }
        catch (RdNotReadyException ex) { SourcesStatus.Text = ex.Message; MainWindow.Instance?.ShowToast(ex.Message); }
        catch (Exception ex)
        {
            Log.Error(ex);
            var msg = ex is InvalidOperationException ? ex.Message : "Couldn't play that: " + Http.Friendly(ex);
            SourcesStatus.Text = msg;
            MainWindow.Instance?.ShowToast(msg);
        }
        finally { it.Busy = false; }
    }

    async Task<string> Resolve(StremioStream s)
    {
        if (s.Kind != StreamKind.Torrent) return s.Url;
        var progress = new Progress<string>(msg => SourcesStatus.Text = msg);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return await RealDebrid.ResolveTorrentAsync(s.InfoHash, s.FileIdx, s.Filename, progress, cts.Token);
    }

    /// <summary>Downloads a source (a torrent goes through Real-Debrid first).</summary>
    async void SourceDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StremioSourceItem it || it.Saving || _meta == null) return;
        var meta = _meta;
        var ep = _episode;
        if (DownloadService.Find(Stremio.ResumeKey(meta, ep)) is { Status: not DownloadStatus.Failed }) { MainWindow.Instance?.OpenDownloads(); return; }
        it.Saving = true;
        try
        {
            var url = await Resolve(it.Stream);
            var (_, msg) = DownloadService.Start(Stremio.ToVod(meta, ep, url, it.Stream.Filename), ep?.Season ?? 0, ep?.Episode ?? 0);
            SourcesStatus.Text = msg;
            MainWindow.Instance?.ShowToast(msg);
        }
        catch (RdNotReadyException ex) { SourcesStatus.Text = ex.Message; MainWindow.Instance?.ShowToast(ex.Message); }
        catch (Exception ex)
        {
            Log.Error(ex);
            var msg = ex is InvalidOperationException ? ex.Message : "Couldn't download that: " + Http.Friendly(ex);
            SourcesStatus.Text = msg;
            MainWindow.Instance?.ShowToast(msg);
        }
        finally { it.Saving = false; }
    }

    // ------------------------------------------------------------------ self-test
    internal int SelfTestCount => (SourceList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal void SelfTestPlayDirect()
    {
        if ((SourceList.ItemsSource as List<StremioSourceItem>)?.FirstOrDefault(s => s.Stream.Kind == StreamKind.Direct) is { } it)
            Source_Click(new Button { DataContext = it }, null);
    }
}
