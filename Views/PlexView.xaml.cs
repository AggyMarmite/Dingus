using System.Windows;
using System.Windows.Controls;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

public class PlexRow
{
    public PlexItem[] Items { get; init; }
}

/// <summary>Your Plex server's films and shows, with Plex's own "Continue watching".</summary>
public partial class PlexView : UserControl, IPage
{
    const string ContinueKey = "__continue";

    public string Title => "Plex";
    public bool SupportsSearch => Plex.SignedIn;
    public string SearchHint => "Search this library";

    List<PlexItem> _items = new();
    List<PlexItem> _filtered = new();
    string _search = "", _sort = "added", _libKey;
    bool _shown, _loading;
    CancellationTokenSource _signIn;
    PlexItem _show;
    List<PlexItem> _episodes = new();

    public PlexView()
    {
        InitializeComponent();
        PosterList.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) Regroup(); };
        Plex.Changed += () => Dispatcher.InvokeAsync(() => { if (_shown) Refresh(); });
    }

    public void ApplySearch(string query)
    {
        _search = query ?? "";
        ApplyFilter();
    }

    public void OnShown()
    {
        _shown = true;
        Refresh();
    }

    public void OnHidden()
    {
        _shown = false;
        PosterList.ItemsSource = null; // posters are the memory here; reload next time
        EpisodeList.ItemsSource = null;
    }

    async void Refresh()
    {
        bool signedIn = Plex.SignedIn;
        SignInPane.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        MainPane.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!signedIn) return;

        if (Plex.Server == null)
        {
            ServerText.Text = "Connecting…";
            ShowStatus("Connecting to your Plex server…");
            bool ok;
            try { ok = await Plex.ConnectAsync(); } catch (Exception ex) { Log.Error(ex); ok = false; }
            if (!ok)
            {
                ServerText.Text = "Not connected";
                ShowStatus("Couldn't reach your Plex server. Check it's switched on and online, then open this page again.");
                return;
            }
        }
        ServerText.Text = Plex.Server.Name;
        var libs = new List<PlexLibrary> { new() { Key = ContinueKey, Title = "Continue watching", Type = "mixed" } };
        try { libs.AddRange(await Plex.LibrariesAsync()); }
        catch (Exception ex) { Log.Error(ex); ShowStatus("Couldn't read your libraries: " + Http.Friendly(ex)); return; }
        var keep = _libKey;
        LibraryList.ItemsSource = libs;
        LibraryList.SelectedItem = libs.FirstOrDefault(l => l.Key == keep) ?? libs[0];
    }

    async void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is not PlexLibrary lib) return;
        _libKey = lib.Key;
        CloseShow();
        HeaderText.Text = lib.Title;
        CountText.Text = "";
        SortChips.Visibility = lib.Key == ContinueKey ? Visibility.Collapsed : Visibility.Visible;
        ShowStatus("Loading…");
        _items = new();
        Regroup();
        _loading = true;
        try
        {
            var items = lib.Key == ContinueKey ? await Plex.ContinueWatchingAsync() : await Plex.ItemsAsync(lib.Key);
            if (_libKey != lib.Key) return; // switched library meanwhile
            _items = items;
        }
        catch (Exception ex) { Log.Error(ex); ShowStatus("Couldn't load this library: " + Http.Friendly(ex)); return; }
        finally { _loading = false; }
        ApplyFilter();
    }

    void Sort_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string t) return;
        _sort = t;
        if (IsLoaded) ApplyFilter();
    }

    void ApplyFilter()
    {
        IEnumerable<PlexItem> q = _items;
        if (_search.Length > 0) q = q.Where(i => (i.Title ?? "").Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                                                (i.ShowTitle ?? "").Contains(_search, StringComparison.OrdinalIgnoreCase));
        if (_libKey != ContinueKey)
            q = _sort switch
            {
                "name" => q.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
                "unwatched" => q.Where(i => !i.Watched).OrderByDescending(i => i.AddedAt),
                _ => q.OrderByDescending(i => i.AddedAt),
            };
        _filtered = q.ToList();
        CountText.Text = _filtered.Count > 0 ? $"{_filtered.Count:N0}" : "";
        Regroup();
        if (_loading) return;
        if (_filtered.Count == 0)
            ShowStatus(_search.Length > 0 ? $"Nothing called “{_search}” here."
                : _libKey == ContinueKey ? "Nothing in progress – films and episodes you start will appear here." : "This library is empty.");
        else BrowseStatus.Visibility = Visibility.Collapsed;
    }

    void Regroup()
    {
        double w = PosterList.ActualWidth - 40;
        int cols = Math.Max(1, (int)(w / 172));
        PosterList.ItemsSource = _filtered.Chunk(cols).Select(c => new PlexRow { Items = c }).ToList();
    }

    void ShowStatus(string text)
    {
        BrowseStatus.Text = text;
        BrowseStatus.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ opening & playing

    async void Item_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlexItem it) return;
        if (it.Type == "show") { await OpenShow(it); return; }
        if (it.Type == "season") { await OpenShow(new PlexItem { RatingKey = it.ShowKey, Title = it.ShowTitle, Type = "show" }); return; }
        await Play(it, _episodes);
    }

    /// <summary>Plays a film or episode (from Plex's resume point). Used by Home too.</summary>
    public static async Task Play(PlexItem it, List<PlexItem> season = null)
    {
        try
        {
            var full = it.PartKey != null ? it : await Plex.DetailsAsync(it.RatingKey);
            var url = Plex.StreamUrl(full);
            if (url == null) { MainWindow.Instance?.ShowToast("Plex didn't return a playable file for this title."); return; }

            var vod = BuildVod(full, url, season); // in a season, "next episode" plays the following one
            MainWindow.Instance?.PlayVod(vod, resume: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            MainWindow.Instance?.ShowToast("Couldn't start this from Plex: " + Http.Friendly(ex));
        }
    }

    static VodItem BuildVod(PlexItem it, string url, List<PlexItem> season)
    {
        bool ep = it.Type == "episode";
        var v = new VodItem
        {
            Kind = "plex",
            Title = ep ? it.ShowTitle : it.Title,
            Subtitle = ep ? $"S{it.Season} E{it.Index} · {it.Title}" : it.Year,
            Url = url,
            Image = it.Image,
            StartMs = it.ViewOffsetMs,
            PlexKey = it.RatingKey,
        };
        if (ep && season != null)
        {
            int i = season.FindIndex(x => x.RatingKey == it.RatingKey);
            if (i >= 0 && i + 1 < season.Count)
            {
                var n = season[i + 1];
                v.NextLabel = $"E{n.Index} · {n.Title}";
                v.Next = () => n.PartKey != null ? BuildVod(n, Plex.StreamUrl(n), season) : null;
            }
        }
        return v;
    }

    async Task OpenShow(PlexItem show)
    {
        _show = show;
        BrowsePane.Visibility = Visibility.Collapsed;
        ShowPane.Visibility = Visibility.Visible;
        ShowPane.ScrollToTop();
        BackText.Text = (LibraryList.SelectedItem as PlexLibrary)?.Title ?? "Back";
        ShowTitle.Text = show.Title;
        ShowMeta.Text = show.Subtitle ?? "";
        ShowSummary.Text = show.Summary ?? "";
        ImageLoader.SetUrl(ShowPoster, show.Image);
        SeasonList.ItemsSource = null;
        EpisodeList.ItemsSource = null;
        try
        {
            var seasons = await Plex.ChildrenAsync(show.RatingKey);
            if (_show != show) return;
            SeasonList.ItemsSource = seasons;
            // Open on the first season that isn't finished.
            SeasonList.SelectedItem = seasons.FirstOrDefault(s => !s.Watched) ?? seasons.FirstOrDefault();
        }
        catch (Exception ex) { Log.Error(ex); MainWindow.Instance?.ShowToast("Couldn't load this show: " + Http.Friendly(ex)); }
    }

    async void SeasonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeasonList.SelectedItem is not PlexItem season) return;
        try
        {
            var eps = await Plex.ChildrenAsync(season.RatingKey);
            if (SeasonList.SelectedItem != season) return;
            _episodes = eps;
            EpisodeList.ItemsSource = eps;
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    void BackToLibrary_Click(object sender, RoutedEventArgs e) => CloseShow();

    void CloseShow()
    {
        _show = null;
        _episodes = new();
        ShowPane.Visibility = Visibility.Collapsed;
        BrowsePane.Visibility = Visibility.Visible;
        EpisodeList.ItemsSource = null;
    }

    // ------------------------------------------------------------------ sign in

    async void SignIn_Click(object sender, RoutedEventArgs e) => await SignIn();

    public async Task SignIn()
    {
        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        SignInButton.Visibility = Visibility.Collapsed;
        CodePanel.Visibility = Visibility.Visible;
        CodeText.Text = "…";
        try
        {
            bool ok = await Plex.SignInAsync(code => CodeText.Text = code, _signIn.Token);
            MainWindow.Instance?.ShowToast(ok ? $"Signed in to Plex – {Plex.Server?.Name}" :
                Plex.SignedIn ? "Signed in, but your Plex server didn't answer." : "Plex sign-in timed out – try again.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error(ex); MainWindow.Instance?.ShowToast("Plex sign-in failed: " + Http.Friendly(ex)); }
        finally
        {
            SignInButton.Visibility = Visibility.Visible;
            CodePanel.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    void CancelSignIn_Click(object sender, RoutedEventArgs e) => _signIn?.Cancel();
}
