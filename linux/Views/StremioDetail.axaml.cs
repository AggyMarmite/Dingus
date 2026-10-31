using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// A Stremio film's or show's page inside Films / TV Shows: its details, a show's seasons and episodes, and every
/// add-on's sources for the film or the episode you pick.
/// </summary>
public partial class StremioDetail : UserControl
{
    CancellationTokenSource _cts;
    StremioMeta _meta;
    StremioVideo _episode;
    List<int> _seasons = new();
    bool _suppress;

    /// <summary>Back was pressed.</summary>
    public event Action Closed;

    public StremioDetail() => InitializeComponent();

    public StremioMeta Meta => _meta;

    /// <summary>Opens a title's page.</summary>
    public async void OpenMeta(StremioMeta m, string backText)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _meta = m;
        _episode = null;
        DetailScroll.Offset = default;
        BackText.Text = backText ?? "Back";
        ShowMeta(m);
        DetailStatus.Text = "Loading details…";
        Sources.Clear();
        bool series = m.Type == "series";
        SeriesPart.IsVisible = series;
        Sources.IsVisible = !series;
        SeasonList.ItemsSource = null;
        EpisodeList.ItemsSource = null;
        if (!series) Sources.Show(m, null);
        try
        {
            var full = await Stremio.MetaAsync(m.Type, m.Id, m.From, cts.Token);
            if (cts.IsCancellationRequested) return;
            DetailStatus.Text = "";
            if (full != null) { full.From ??= m.From; _meta = full; ShowMeta(full); }
            if (_meta.Type == "series") ShowSeasons();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { DetailStatus.Text = "Couldn't load the details: " + Http.Friendly(ex); }
    }

    void ShowMeta(StremioMeta m)
    {
        DetailTitle.Text = m.Name;
        DetailMeta.Text = string.Join("  ·  ", new[] { m.ReleaseInfo, m.Runtime, string.IsNullOrEmpty(m.ImdbRating) ? null : "★ " + m.ImdbRating,
            m.Genres.Count > 0 ? string.Join(", ", m.Genres.Take(3)) : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
        DetailPlot.Text = m.Description ?? "";
        var credits = new List<string>();
        if (m.Cast.Count > 0) credits.Add("Starring  " + string.Join(", ", m.Cast.Take(5)));
        if (m.Director.Count > 0) credits.Add("Director  " + string.Join(", ", m.Director.Take(2)));
        DetailCredits.Text = string.Join("\n", credits);
        ImageLoader.SetUrl(DetailPoster, m.Poster);
        ImageLoader.SetUrl(DetailBackdrop, m.Background);
    }

    List<string> _seasonLabels = new();

    void ShowSeasons()
    {
        _seasons = _meta.Videos.Select(v => v.Season).Distinct().OrderBy(s => s == 0 ? int.MaxValue : s).ToList();
        if (_seasons.Count == 0) { DetailStatus.Text = "No episodes listed."; return; }
        _seasonLabels = _seasons.Select(s => s == 0 ? "Specials" : $"Season {s}").ToList();
        SeasonList.ItemsSource = _seasonLabels;
        ShowEpisodes(_seasons[0]);
        Dispatcher.UIThread.Post(() =>
        {
            // Tick the first season's chip (its RadioButton is made by the list).
            var first = SeasonList.GetRealizedContainers().Select(c => c is ContentPresenter cp ? cp.Child as RadioButton : c as RadioButton).FirstOrDefault();
            if (first != null) { _suppress = true; first.IsChecked = true; _suppress = false; }
        }, DispatcherPriority.Loaded);
    }

    void Season_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not RadioButton { IsChecked: true, DataContext: string label }) return;
        int i = _seasonLabels.IndexOf(label);
        if (i >= 0) ShowEpisodes(_seasons[i]);
    }

    void ShowEpisodes(int season)
    {
        EpisodeList.ItemsSource = _meta.Videos.Where(v => v.Season == season).OrderBy(v => v.Episode).Select(v => new StremioEpisodeItem
        {
            Video = v,
            Code = season == 0 ? $"Special {v.Episode}" : $"S{v.Season:00} E{v.Episode:00}",
            Title = string.IsNullOrWhiteSpace(v.Title) ? $"Episode {v.Episode}" : v.Title,
            Aired = v.Released?.ToLocalTime().ToString("d MMM yyyy") ?? "",
            Selected = _episode?.Id == v.Id,
        }).ToList();
    }

    void Episode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not StremioEpisodeItem ep) return;
        _episode = ep.Video;
        ShowEpisodes(ep.Video.Season);
        Sources.IsVisible = true;
        Sources.Show(_meta, ep.Video, ep.Code + "  ·  " + ep.Title);
        Dispatcher.UIThread.Post(() => Sources.BringIntoView(), DispatcherPriority.Loaded);
    }

    void Back_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();

    /// <summary>Lets go of the page (pictures, add-on requests).</summary>
    public void Close()
    {
        _cts?.Cancel();
        _meta = null;
        Sources.Clear();
        ImageLoader.SetUrl(DetailBackdrop, null);
    }

    // ------------------------------------------------------------------ self-test
    internal int SelfTestSources => Sources.SelfTestCount;
    internal int SelfTestEpisodes => (EpisodeList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal void SelfTestOpenEpisode()
    {
        if ((EpisodeList.ItemsSource as List<StremioEpisodeItem>)?.FirstOrDefault() is { } ep)
            Episode_Click(new Button { DataContext = ep }, null);
    }
    internal void SelfTestPlayFirst() => Sources.SelfTestPlayDirect();
}
