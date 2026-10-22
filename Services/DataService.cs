using System.Net.Http;
using System.Text.Json;
#if LINUX
using Avalonia.Threading;
#else
using System.Windows.Threading;
#endif
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Owns the active playlist's catalogue and guide. Opens instantly from the on-disk cache and
/// refreshes from the provider in the background when the data is out of date.
/// </summary>
public sealed class DataService
{
    public static DataService Instance { get; } = new();

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public CatalogData Catalog { get; private set; } = new();
    public EpgData Epg { get; private set; } = EpgData.Empty;

    /// <summary>Swaps in a new guide and releases the old one's description file.</summary>
    void SetEpg(EpgData e)
    {
        var old = Epg;
        Epg = e;
        if (!ReferenceEquals(old, e) && !ReferenceEquals(old, EpgData.Empty)) old.Dispose();
    }
    public XtreamClient Xtream { get; private set; }
    public bool IsBusy { get; private set; }
    /// <summary>True once any playlist load has started (so start-up doesn't start a second one).</summary>
    public bool HasStartedLoading => _gen > 0;

    /// <summary>Raised when channels/films/shows or category visibility change.</summary>
    /// <summary>Speed test only: told how long named pieces of work took (null otherwise – costs nothing).</summary>
    public static Action<string, double> TimingHook;

    /// <summary>Runs <paramref name="work"/>, telling the speed test how long it took.</summary>
    public static T Time<T>(string what, Func<T> work)
    {
        if (TimingHook == null) return work();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return work(); }
        finally { TimingHook(what, sw.Elapsed.TotalMilliseconds); }
    }

    public static void Time(string what, Action work) => Time(what, () => { work(); return 0; });

    public static async Task<T> TimeAsync<T>(string what, Func<Task<T>> work)
    {
        if (TimingHook == null) return await work();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return await work(); }
        finally { TimingHook(what, sw.Elapsed.TotalMilliseconds); }
    }

    /// <summary>Raises an event one listener at a time, so the speed test can say which one was slow.</summary>
    static void Raise(Action ev, string what)
    {
        if (ev == null) return;
        if (TimingHook == null) { ev(); return; }
        foreach (Action h in ev.GetInvocationList())
            Time($"{what} → {h.Method.DeclaringType?.Name}.{h.Method.Name}", h);
    }

    public event Action CatalogChanged;
    public event Action EpgChanged;
    public event Action<bool, string> BusyChanged;
    /// <summary>True while a playlist is downloading with nothing saved to show yet (first sign-in).</summary>
    public bool IsFirstLoad { get; private set; }
    /// <summary>True while the busy work is the TV guide rather than the playlist.</summary>
    public bool BusyIsGuide { get; private set; }
    public event Action<string> Message;

    int _gen;
    bool _liveHiddenChanged, _launchRefresh;

    /// <summary>Channels the guide needs data for – hidden categories are skipped to save memory.</summary>
    List<Channel> GuideChannels()
    {
        var hidden = HiddenSet("live");
        return hidden.Count == 0 ? Catalog.Channels : Catalog.Channels.Where(c => !hidden.Contains(c.CategoryId)).ToList();
    }
    CancellationTokenSource _cts = new();
    readonly DispatcherTimer _catDebounce;

    readonly DispatcherTimer _autoTimer;

    DataService()
    {
        _catDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _catDebounce.Tick += async (_, _) =>
        {
            _catDebounce.Stop();
            Raise(CatalogChanged, "new playlist");
            Memory.CompactSoon();
            // Hidden live categories aren't matched in the guide (saves memory), so re-match after changes.
            if (_liveHiddenChanged) { _liveHiddenChanged = false; await ReloadEpgAsync(); }
        };

        // While the app stays open, refresh on the interval chosen in Settings.
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _autoTimer.Tick += async (_, _) =>
        {
            var p = Active;
            if (p == null || IsBusy || !S.AutoRefresh) return;
            if (S.BatteryAware && PowerService.OnBattery) return; // save battery: refresh when plugged in
            if (DateTime.UtcNow - p.LastRefreshUtc > TimeSpan.FromHours(S.RefreshIntervalHours))
                await RefreshAsync(includeGuide: false);
            else if (DateTime.UtcNow - p.LastEpgRefreshUtc > TimeSpan.FromHours(S.EpgRefreshHours))
                await RefreshEpgAsync();
        };
        _autoTimer.Start();
    }

    static AppSettings S => SettingsStore.Current;

    public PlaylistConfig Active =>
        S.Playlists.FirstOrDefault(p => p.Id == S.ActivePlaylistId) ?? S.Playlists.FirstOrDefault();

    static string CatalogFile(PlaylistConfig p) => Path.Combine(AppPaths.PlaylistDir(p.Id), "catalog.json");
    static string EpgFile(PlaylistConfig p) => Path.Combine(AppPaths.PlaylistDir(p.Id), "epg.xml");

    void SetBusy(bool busy, string text)
    {
        IsBusy = busy;
        BusyChanged?.Invoke(busy, text);
    }

    // ---------------------------------------------------------------- loading

    /// <param name="refreshNow">True at app launch: always fetch a fresh playlist (when auto refresh is on).</param>
    public async Task LoadActiveAsync(bool refreshNow = false)
    {
        _launchRefresh = refreshNow;
        var gen = ++_gen;
        // Cancel any download still running for the previous playlist, and let it wind down.
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        while (IsBusy) await Task.Delay(100);
        if (gen != _gen) return;

        var p = Active;
        Catalog = new CatalogData();
        SetEpg(EpgData.Empty);
        Xtream = null;

        if (p == null)
        {
            HistoryStore.Load(null);
            Raise(CatalogChanged, "new playlist");
            Raise(EpgChanged, "new guide");
            return;
        }

        S.ActivePlaylistId = p.Id;
        if (p.Type == PlaylistType.Xtream) Xtream = new XtreamClient(p);
        HistoryStore.Load(p.Id);

        var cached = await Task.Run(() => Time("playlist: reading the saved copy", () => ReadCatalog(CatalogFile(p))));
        if (gen != _gen) return;
        if (cached != null) Catalog = cached;
        Raise(CatalogChanged, "new playlist");
        Memory.CompactSoon();
        Raise(EpgChanged, "new guide");

        bool stale = cached == null || (S.AutoRefresh &&
                     (refreshNow || DateTime.UtcNow - p.LastRefreshUtc > TimeSpan.FromHours(S.RefreshIntervalHours)));
        if (stale)
        {
            // Show the saved channels and guide straight away while the fresh playlist downloads.
            if (cached != null) await ReloadEpgAsync();
            if (gen != _gen) return;
            await RefreshAsync(includeGuide: cached == null);
            return;
        }

        await ReloadEpgAsync();
        if (gen != _gen) return;
        bool noGuide = !File.Exists(EpgFile(p));
        bool epgStale = DateTime.UtcNow - p.LastEpgRefreshUtc > TimeSpan.FromHours(S.EpgRefreshHours);
        if (noGuide || (epgStale && S.AutoRefresh)) await RefreshEpgAsync();
    }

    /// <summary>Re-parses the cached guide file (e.g. after changing the time shift).</summary>
    public async Task ReloadEpgAsync()
    {
        var p = Active;
        if (p == null) return;
        var gen = _gen;
        var file = EpgFile(p);
        if (!File.Exists(file)) return;
        var channels = GuideChannels();
        var shift = S.EpgShiftMinutes;
        var overrides = new Dictionary<string, string>(p.EpgOverrides);
            var epg = await Task.Run(() => EpgService.LoadOrParse(file, channels, shift, overrides));
        if (gen != _gen) return;
        SetEpg(epg);
        Raise(EpgChanged, "new guide");
        Memory.CompactSoon();
    }

    // ---------------------------------------------------------------- refreshing

    /// <param name="includeGuide">True = always re-download the TV guide too (the Refresh button, a new playlist).
    /// False = only re-download the guide when it's missing or older than the guide interval.</param>
    public async Task RefreshAsync(bool includeGuide = true)
    {
        if (IsBusy) return;
        var p = Active;
        if (p == null) return;
        var gen = _gen;
        var ct = _cts.Token;
        bool ok = false;

        // Full loading screen: nothing saved to show yet, or the refresh you get when the app opens.
        IsFirstLoad = _launchRefresh || (Catalog.Channels.Count == 0 && Catalog.Movies.Count == 0 && Catalog.Series.Count == 0);
        _launchRefresh = false;
        BusyIsGuide = false;
        SetBusy(true, "Connecting…");
        try
        {
            var data = p.Type == PlaylistType.Xtream ? await FetchXtreamAsync(p, ct) : await FetchM3uAsync(p, ct);
            if (gen != _gen) return;
            await Task.Run(() => Time("playlist: saving a copy", () => WriteCatalog(CatalogFile(p), data)));
            p.LastRefreshUtc = DateTime.UtcNow;
            SettingsStore.Save();
            Catalog = data;
            Raise(CatalogChanged, "new playlist");
            Memory.CompactSoon();
            Message?.Invoke($"Playlist updated · {data.Channels.Count:N0} channels · {data.Movies.Count:N0} films · {data.Series.Count:N0} shows");
            ok = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex);
            Message?.Invoke("Refresh failed: " + Http.Friendly(ex));
        }
        finally { SetBusy(false, null); }

        if (!ok || gen != _gen) return;
        bool guideDue = includeGuide || !File.Exists(EpgFile(p)) ||
                        DateTime.UtcNow - p.LastEpgRefreshUtc > TimeSpan.FromHours(S.EpgRefreshHours);
        if (guideDue) await RefreshEpgAsync();
        else await ReloadEpgAsync(); // re-match the saved guide to the updated channel list
    }

    async Task<CatalogData> FetchXtreamAsync(PlaylistConfig p, CancellationToken ct)
    {
        var x = new XtreamClient(p);
        Xtream = x;

        SetBusy(true, "Signing in…");
        var account = await x.LoginAsync(ct);

        SetBusy(true, "Loading channels…");
        var liveCats = await x.GetCategoriesAsync("get_live_categories", ct);
        var live = await x.GetLiveStreamsAsync(ct);

        List<Category> vodCats = new(), serCats = new();
        List<Movie> movies = new();
        List<Series> series = new();
        try
        {
            SetBusy(true, "Loading films…");
            vodCats = await x.GetCategoriesAsync("get_vod_categories", ct);
            movies = await x.GetMoviesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error(ex); }
        try
        {
            SetBusy(true, "Loading TV shows…");
            serCats = await x.GetCategoriesAsync("get_series_categories", ct);
            series = await x.GetSeriesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error(ex); }

        return new CatalogData
        {
            Account = account,
            LiveCategories = liveCats,
            Channels = live,
            VodCategories = vodCats,
            Movies = movies,
            SeriesCategories = serCats,
            Series = series,
        };
    }

    async Task<CatalogData> FetchM3uAsync(PlaylistConfig p, CancellationToken ct)
    {
        var src = p.M3uUrl?.Trim() ?? "";
        SetBusy(true, "Downloading playlist…");
        if (File.Exists(src))
            return await Task.Run(() => { using var r = new StreamReader(src); return M3uParser.Parse(r); }, ct);

        using var resp = await Http.Client.GetAsync(src, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        SetBusy(true, "Reading playlist…");
        return await Task.Run(() => { using var r = new StreamReader(stream); return M3uParser.Parse(r); });
    }

    public async Task RefreshEpgAsync()
    {
        if (IsBusy) return;
        var p = Active;
        if (p == null) return;
        var gen = _gen;
        var ct = _cts.Token;
        var url = EpgUrlFor(p);
        if (string.IsNullOrWhiteSpace(url)) return;

        IsFirstLoad = false;
        BusyIsGuide = true;
        SetBusy(true, "Downloading TV guide…");
        try
        {
            await EpgService.DownloadAsync(url, EpgFile(p), ct);
            if (gen != _gen) return;
            SetBusy(true, "Matching TV guide…");
            var channels = GuideChannels();
            var shift = S.EpgShiftMinutes;
            var file = EpgFile(p);
            var overrides = new Dictionary<string, string>(p.EpgOverrides);
            var epg = await Task.Run(() => EpgService.LoadOrParse(file, channels, shift, overrides));
            if (gen != _gen) return;
            SetEpg(epg);
            Memory.CompactSoon();
            p.LastEpgRefreshUtc = DateTime.UtcNow;
            SettingsStore.Save();
            Raise(EpgChanged, "new guide");
            Message?.Invoke($"TV guide updated · {epg.MatchedChannels:N0} of {channels.Count:N0} channels matched");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex);
            Message?.Invoke("TV guide update failed: " + Http.Friendly(ex));
        }
        finally { SetBusy(false, null); }
    }

    public string EpgUrlFor(PlaylistConfig p)
    {
        if (!string.IsNullOrWhiteSpace(p.EpgUrl)) return p.EpgUrl.Trim();
        if (p.Type == PlaylistType.Xtream) return new XtreamClient(p).EpgUrl;
        return Catalog.SourceEpgUrl;
    }

    // ---------------------------------------------------------------- urls & details

    public string StreamUrl(Channel c) =>
        c.Url ?? Xtream?.LiveUrl(c, S.PreferHls);

    public string MovieUrl(Movie m) => m.Url ?? Xtream?.MovieUrl(m);

    DateTime _accountCheckedUtc;

    /// <summary>Re-reads subscription status and connections in use (at most every 5 minutes). Returns true if it changed.</summary>
    public async Task<bool> RefreshAccountAsync()
    {
        if (Xtream == null || IsBusy || DateTime.UtcNow - _accountCheckedUtc < TimeSpan.FromMinutes(5)) return false;
        _accountCheckedUtc = DateTime.UtcNow;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var acc = await Xtream.LoginAsync(cts.Token);
            Catalog.Account = acc;
            return true;
        }
        catch (Exception ex) { Log.Error(ex); return false; }
    }

    /// <summary>Rebuilds a playable film/episode from its "Continue watching" entry (null if it's gone from the playlist).</summary>
    /// <summary>The film or show is in a hidden or locked category.</summary>
    public bool IsHiddenEntry(ResumeEntry e)
    {
        if (e.Kind == "movie")
        {
            var m = Catalog.Movies.FirstOrDefault(x => x.Id == e.ItemId);
            return m != null && HiddenSet("vod").Contains(m.CategoryId);
        }
        var s = Catalog.Series.FirstOrDefault(x => x.Id == e.SeriesId);
        return s != null && HiddenSet("series").Contains(s.CategoryId);
    }

    public VodItem ResumeItem(ResumeEntry e)
    {
        if (IsHiddenEntry(e)) return null;
        string url = null;
        if (e.Kind == "movie")
        {
            var m = Catalog.Movies.FirstOrDefault(x => x.Id == e.ItemId);
            if (m != null) url = MovieUrl(m);
        }
        else if (e.Kind == "episode")
        {
            var s = Catalog.Series.FirstOrDefault(x => x.Id == e.SeriesId);
            var ep = s?.Episodes?.FirstOrDefault(x => x.Id == e.ItemId);
            url = ep != null ? EpisodeUrl(ep) : (s != null || Xtream != null ? EpisodeUrl(new Episode { Id = e.ItemId, Ext = e.Ext ?? "mp4" }) : null);
        }
        if (url == null) return null;
        return new VodItem
        {
            Kind = e.Kind, Title = e.Title, Subtitle = e.Subtitle, Url = url, Image = e.Image, Ext = e.Ext,
            ResumeKey = e.Key, ItemId = e.ItemId, SeriesId = e.SeriesId,
        };
    }

    // ---------------------------------------------------------------- catch-up

    /// <summary>Days of catch-up the channel offers (Xtream only). Unknown length = assume 2 days.</summary>
    public static int CatchupDays(Channel c) => c.HasArchive ? (c.ArchiveDays > 0 ? c.ArchiveDays : 2) : 0;

    /// <summary>True when a programme has started and is still inside the channel's catch-up window.</summary>
    public bool CanCatchup(Channel c, Programme p)
    {
        if (Xtream == null || c.Url != null || p == null) return false;
        int days = CatchupDays(c);
        var now = DateTime.Now;
        return days > 0 && p.Start <= now && p.Start >= now.AddDays(-days);
    }

    public string CatchupUrl(Channel c, Programme p)
    {
        if (!CanCatchup(c, p)) return null;
        var realStart = p.Start.AddMinutes(-S.EpgShiftMinutes); // undo the display shift
        var minutes = (int)Math.Ceiling((p.End - p.Start).TotalMinutes) + 2;
        return Xtream.CatchupUrl(c, realStart, minutes, Catalog.Account?.ServerTimeZone);
    }
    public string EpisodeUrl(Episode e) => e.Url ?? Xtream?.EpisodeUrl(e);

    /// <summary>Catch-up from any moment (not just a programme's start), running up to about now.</summary>
    public string CatchupUrlAt(Channel c, DateTime startLocal, int minutes)
    {
        if (Xtream == null || c?.Url != null || !c.HasArchive) return null;
        return Xtream.CatchupUrl(c, startLocal, minutes, Catalog.Account?.ServerTimeZone);
    }

    public async Task<MediaDetails> GetMovieDetailsAsync(Movie m, CancellationToken ct)
    {
        if (Xtream == null || m.Url != null) return new MediaDetails { Title = m.Name, Poster = m.Poster, Year = m.Year };
        var d = await Xtream.GetVodInfoAsync(m.Id, ct);
        d.Year ??= m.Year;
        return d;
    }

    public async Task<MediaDetails> GetSeriesDetailsAsync(Series s, CancellationToken ct)
    {
        if (s.Episodes != null)
            return new MediaDetails { Title = s.Name, Poster = s.Cover, Plot = s.Plot, Genre = s.Genre, Year = s.Year, Episodes = s.Episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList() };
        if (Xtream == null) return new MediaDetails();
        var d = await Xtream.GetSeriesInfoAsync(s.Id, ct);
        d.Plot ??= s.Plot;
        d.Genre ??= s.Genre;
        d.Year ??= s.Year;
        return d;
    }

    // ---------------------------------------------------------------- categories & favourites

    /// <summary>Categories not to show: ones you've hidden, plus locked ones while the parental PIN hasn't been entered.</summary>
    public HashSet<string> HiddenSet(string kind, bool includeLocked = true)
    {
        var prefix = kind + ":";
        var set = new HashSet<string>();
        var p = Active;
        if (p == null) return set;
        foreach (var h in p.HiddenCategories)
            if (h.StartsWith(prefix, StringComparison.Ordinal)) set.Add(h.Substring(prefix.Length));
        if (includeLocked && Parental.Active) set.UnionWith(Parental.LockedSet(kind));
        return set;
    }

    public void SetHidden(string kind, string categoryId, bool hidden)
    {
        var p = Active;
        if (p == null) return;
        var key = kind + ":" + categoryId;
        p.HiddenCategories.Remove(key);
        if (hidden) p.HiddenCategories.Add(key);
        if (kind == "live") _liveHiddenChanged = true;
        SettingsStore.Save();
        _catDebounce.Stop();
        _catDebounce.Start();
    }

    public void SetHiddenMany(string kind, IEnumerable<string> categoryIds, bool hidden)
    {
        var p = Active;
        if (p == null) return;
        var keys = categoryIds.Select(id => kind + ":" + id).ToList();
        var set = new HashSet<string>(p.HiddenCategories);
        foreach (var k in keys) { if (hidden) set.Add(k); else set.Remove(k); }
        p.HiddenCategories = set.ToList();
        if (kind == "live") _liveHiddenChanged = true;
        SettingsStore.Save();
        _catDebounce.Stop();
        _catDebounce.Start();
    }

    /// <summary>Asks every page to rebuild from the current catalogue (e.g. after a display setting changes).</summary>
    public void NotifyCatalogChanged() => Raise(CatalogChanged, "playlist changed");

    public HashSet<string> FavouriteSet() => new(Active?.Favourites ?? new List<string>());

    /// <summary>Adds (as <paramref name="primaryId"/>) or removes (every version) a favourite. Returns the new state.</summary>
    public bool SetFavourite(IEnumerable<string> allIds, string primaryId, bool on)
    {
        var p = Active;
        if (p == null) return false;
        foreach (var id in allIds) p.Favourites.Remove(id);
        if (on) p.Favourites.Add(primaryId);
        SettingsStore.Save();
        return on;
    }

    public bool ToggleFavourite(string channelId)
    {
        var p = Active;
        if (p == null) return false;
        bool on;
        if (p.Favourites.Remove(channelId)) on = false;
        else { p.Favourites.Add(channelId); on = true; }
        SettingsStore.Save();
        return on;
    }

    public void DeletePlaylistData(PlaylistConfig p)
    {
        try { Directory.Delete(AppPaths.PlaylistDir(p.Id), true); } catch { }
    }

    // ---------------------------------------------------------------- cache io

    static CatalogData ReadCatalog(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<CatalogData>(fs, JsonOpts);
        }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    static void WriteCatalog(string path, CatalogData data)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) JsonSerializer.Serialize(fs, data, JsonOpts);
        File.Move(tmp, path, true);
    }
}
