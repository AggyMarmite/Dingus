using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>
/// Home: a big featured picture at the top (carry on watching, films and episodes you're part-way through,
/// what's on your favourites, new films and shows) that moves on by itself, then rows of channels and titles:
/// favourites, continue watching, coming up next, live sport, reminders, tonight's picks, recently watched, new.
/// Built only while it's on screen, and refreshed every minute so times stay current.
/// </summary>
public partial class HomeView : UserControl, IPage
{
    public string Title => "Home";
    public bool SupportsSearch => false;
    public string SearchHint => "";

    /// <summary>The guide's rows (merged channels, favourites, guide data) are shared with Home.</summary>
    public GuideView Guide { get; set; }
    /// <summary>Kept for the main window; Home doesn't play video in the page any more (Watch opens full screen).</summary>
    public Func<bool> CanPreview { get; set; } = () => false;
    public FrameworkElement PreviewSlotElement => null;

    bool _shown;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer _advance = new() { Interval = TimeSpan.FromSeconds(9) };

    public HomeView()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => Build();
        _advance.Tick += (_, _) => { if (!_heroHover && IsVisible) Step(+1); };
        DataService.Instance.CatalogChanged += () => { if (_shown) Dispatcher.InvokeAsync(Build, DispatcherPriority.Background); };
        DataService.Instance.EpgChanged += () => { if (_shown) Build(); };
        HistoryStore.Changed += () => { if (_shown) Build(); };
        ReminderService.Changed += () => { if (_shown) Build(); };
    }

    public void ApplySearch(string query) { }

    public void OnShown()
    {
        _shown = true;
        Build();
        _refresh.Start();
        _advance.Start();
        RefreshAccount();
    }

    public void OnHidden()
    {
        _shown = false;
        _refresh.Stop();
        _advance.Stop();
        foreach (var list in new ItemsControl[] { NextList, SportList, RemindList, PicksList, ResumeList, RecentList, FavList, NewFilmsList, NewShowsList })
            list.ItemsSource = null;
        HeroBackdrop.Source = HeroBlur.Source = null;
        HeroPosterBrush.ImageSource = null;
    }

    public void SetPreviewPlaying(bool playing) { }
    public void TryStartPreview() { }

    async void RefreshAccount()
    {
        if (await DataService.Instance.RefreshAccountAsync() && _shown) UpdateAccount();
    }

    // ------------------------------------------------------------------ building

    void Build()
    {
        if (!_shown || Guide == null) return;
        var ds = DataService.Instance;
        var epg = ds.Epg;
        var now = DateTime.Now;
        UpdateAccount();

        var favs = Guide.FavouriteRows();
        var recent = Guide.RecentlyWatchedRows().Take(20).ToList();
        var all = Guide.RowsIn(GuideView.AllId);

        // Favourites and recently watched: what's on now.
        FavList.ItemsSource = favs.Select(r => NowCard(r, GuideView.FavId, epg, now)).ToList();
        FavEmpty.Visibility = favs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavSection.Visibility = favs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentList.ItemsSource = recent.Select(r => NowCard(r, GuideView.RecentId, epg, now)).ToList();
        RecentSection.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Coming up next: the next programme on your favourites (or recent channels) in the next 3 hours.
        var source = favs.Count > 0 ? favs : recent;
        var listId = favs.Count > 0 ? GuideView.FavId : GuideView.RecentId;
        var next = new List<(DateTime Start, HomeChannelCard Card)>();
        foreach (var r in source)
        {
            var id = Guide.EpgIdOf(r);
            var cur = epg.At(id, now);
            var p = cur != null ? epg.StartingFrom(id, cur.End) : epg.StartingFrom(id, now);
            if (p == null || p.Start - now > TimeSpan.FromHours(3)) continue;
            int mins = (int)Math.Ceiling((p.Start - now).TotalMinutes);
            next.Add((p.Start, new HomeChannelCard
            {
                Row = r, ListId = listId, Title = p.Title,
                When = mins <= 0 ? "Starting now" : mins < 60 ? $"In {mins} min" : $"At {p.Start:HH:mm}",
                Soon = mins <= 15,
                GenreBrush = Genres.Brush(p.Genre),
            }));
        }
        NextList.ItemsSource = next.OrderBy(x => x.Start).Take(20).Select(x => x.Card).ToList();
        NextSection.Visibility = next.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Live sport now, across every channel (favourites first).
        var favSet = new HashSet<GuideRow>(favs);
        var sport = new List<(bool Fav, DateTime End, HomeChannelCard Card)>();
        foreach (var r in all)
        {
            var p = epg.At(Guide.EpgIdOf(r), now);
            if (!Genres.IsSport(p, r.Name)) continue;
            sport.Add((favSet.Contains(r), p.End, NowCard(r, GuideView.AllId, epg, now)));
        }
        SportList.ItemsSource = sport.OrderByDescending(x => x.Fav).ThenBy(x => x.End).Take(30).Select(x => x.Card).ToList();
        SportSection.Visibility = sport.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Your reminders.
        var reminders = new List<HomeChannelCard>();
        foreach (var rem in ReminderService.Upcoming)
        {
            var r = Guide.RowFor(rem.ChannelId);
            if (r == null) continue;
            bool on = rem.Start <= now;
            reminders.Add(new HomeChannelCard
            {
                Row = r, ListId = GuideView.AllId, Title = rem.Title,
                Upcoming = epg.At(Guide.EpgIdOf(r), rem.Start.AddSeconds(1)) ?? new Programme { Title = rem.Title, Start = rem.Start, End = rem.End },
                When = on ? "On now" : $"{(rem.Start.Date == DateTime.Today ? "Today" : rem.Start.ToString("ddd d MMM"))}  {rem.Start:HH:mm}",
                Soon = !on && rem.Start - now < TimeSpan.FromMinutes(30),
            });
        }
        RemindList.ItemsSource = reminders;
        RemindSection.Visibility = reminders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Tonight's picks: films and sport starting this evening (6pm–midnight, or from now if later).
        var start = DateTime.Today.AddHours(18);
        var end = DateTime.Today.AddDays(1);
        PicksHeader.Text = "Tonight’s picks";
        if (now >= end.AddHours(-1)) { start = DateTime.Today.AddDays(1).AddHours(18); end = start.AddHours(6); PicksHeader.Text = "Tomorrow night’s picks"; }
        else if (now > start) start = now;
        var picks = new List<(bool Fav, DateTime Start, HomeChannelCard Card)>();
        foreach (var r in all)
        {
            var l = epg.Get(Guide.EpgIdOf(r));
            if (l == null) continue;
            for (int i = EpgData.FirstEndingAfter(l, start); i < l.Count && l[i].Start < end; i++)
            {
                var p = l[i];
                if (p.Start < start) continue;
                if (!Genres.IsFilm(p) && !Genres.IsSport(p, r.Name)) continue;
                picks.Add((favSet.Contains(r), p.Start, new HomeChannelCard
                {
                    Row = r, ListId = GuideView.AllId, Title = p.Title, Upcoming = p,
                    When = $"{p.Start:HH:mm}  ·  {(Genres.IsFilm(p) ? "Film" : "Sport")}",
                    GenreBrush = Genres.Brush(p.Genre) ?? Genres.Brush("sport"),
                }));
            }
        }
        PicksList.ItemsSource = picks.OrderByDescending(x => x.Fav).ThenBy(x => x.Start).Take(30).Select(x => x.Card).ToList();
        PicksSection.Visibility = picks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Continue watching: films and episodes you stopped part-way through.
        var resume = HistoryStore.Recent("movie").Concat(HistoryStore.Recent("episode"))
            .Where(e => e.CanResume && !DataService.Instance.IsHiddenEntry(e))
            .OrderByDescending(e => e.UpdatedUtc)
            .Take(20)
            .Select(e => new HomeResumeCard
            {
                Entry = e,
                Subtitle = e.Kind == "episode" && !string.IsNullOrEmpty(e.Subtitle)
                    ? e.Subtitle
                    : e.DurationMs > 0 ? $"{Left(e.DurationMs - e.PositionMs)} left" : "Film",
            })
            .ToList();
        ResumeList.ItemsSource = resume;
        ResumeSection.Visibility = resume.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // New films and shows from your provider (hidden categories left out).
        var data = ds.Catalog;
        var hiddenVod = ds.HiddenSet("vod");
        var hiddenSeries = ds.HiddenSet("series");
        var newFilms = data.Movies.Where(m => !hiddenVod.Contains(m.CategoryId)).OrderByDescending(m => m.Added).Take(20)
            .Select(m => new HomePosterCard { Id = m.Id, IsMovie = true, Title = m.Name, Image = m.Poster, Subtitle = m.Year ?? "Film" }).ToList();
        var newShows = data.Series.Where(x => !hiddenSeries.Contains(x.CategoryId)).OrderByDescending(x => x.Added).Take(20)
            .Select(x => new HomePosterCard { Id = x.Id, IsMovie = false, Title = x.Name, Image = x.Cover, Subtitle = x.Year ?? "TV show" }).ToList();
        NewFilmsList.ItemsSource = newFilms;
        NewFilmsSection.Visibility = newFilms.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NewShowsList.ItemsSource = newShows;
        NewShowsSection.Visibility = newShows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        BuildHero(epg, now, favs);
    }

    // ------------------------------------------------------------------ featured slides

    sealed class Slide
    {
        public string Key, Label, Title, Meta, Desc, Logo, Poster, Art;
        public double Progress = -1;
        public string PrimaryText, PrimaryIcon, SecondaryText, SecondaryIcon;
        public Action Primary, Secondary;
        /// <summary>Fetches a better picture / description when the slide is first shown.</summary>
        public Func<Task<Extra>> Enrich;
    }
    sealed record Extra(string Art, string Desc, string Meta);
    public sealed class Dot
    {
        public string Title { get; init; }
        public bool On { get; init; }
    }

    readonly Dictionary<string, Extra> _extras = new();
    readonly HashSet<string> _enriching = new();
    List<Slide> _slides = new();
    int _index;
    bool _heroHover;
    int _showToken;

    void BuildHero(EpgData epg, DateTime now, List<GuideRow> favs)
    {
        var slides = new List<Slide>();
        var ds = DataService.Instance;

        // 1. Carry on watching: your last channel.
        var last = SettingsStore.Current.LastChannelId;
        var lastRow = last == null ? null : Guide.RowFor(last);
        if (lastRow != null) slides.Add(LiveSlide(lastRow, epg, now, "Carry on watching"));

        // 2. Films and episodes you're part-way through.
        foreach (var e in HistoryStore.Recent("movie").Concat(HistoryStore.Recent("episode"))
                     .Where(e => e.CanResume && !ds.IsHiddenEntry(e)).OrderByDescending(e => e.UpdatedUtc).Take(2))
            slides.Add(ResumeSlide(e));

        // 3. What's on two of your favourites now.
        foreach (var r in favs.Where(r => r != lastRow && epg.At(Guide.EpgIdOf(r), now) != null).Take(2))
            slides.Add(LiveSlide(r, epg, now, "On now · Favourites"));

        // 4. New films and shows.
        var hiddenVod = ds.HiddenSet("vod");
        var hiddenSeries = ds.HiddenSet("series");
        foreach (var m in ds.Catalog.Movies.Where(m => !hiddenVod.Contains(m.CategoryId)).OrderByDescending(m => m.Added).Take(2))
            slides.Add(FilmSlide(m));
        foreach (var x in ds.Catalog.Series.Where(x => !hiddenSeries.Contains(x.CategoryId)).OrderByDescending(x => x.Added).Take(2))
            slides.Add(ShowSlide(x));

        if (slides.Count == 0) slides.Add(WelcomeSlide(now));
        if (slides.Count > 8) slides = slides.Take(8).ToList();

        bool same = slides.Count == _slides.Count && slides.Zip(_slides).All(t => t.First.Key == t.Second.Key);
        var currentKey = _index < _slides.Count ? _slides[_index].Key : null;
        _slides = slides;
        if (same)
        {
            ApplyText(_slides[_index]); // just refresh the times
            return;
        }
        int keep = currentKey == null ? -1 : _slides.FindIndex(s => s.Key == currentKey);
        _index = keep >= 0 ? keep : 0;
        Show(_index, animate: false);
    }

    Slide LiveSlide(GuideRow r, EpgData epg, DateTime now, string label)
    {
        var id = Guide.EpgIdOf(r);
        var p = epg.At(id, now);
        var s = new Slide
        {
            Key = "live|" + r.Channel.Id + "|" + (p?.Start.Ticks ?? 0),
            Label = label,
            Title = p?.Title ?? r.DisplayName,
            Logo = r.Logo,
            PrimaryText = "Watch", PrimaryIcon = "\uE768",
            Primary = () => MainWindow.Instance?.PlayLive(r.Channel, true),
            SecondaryText = "Live TV", SecondaryIcon = "\uE7F4",
            Secondary = () => MainWindow.Instance?.ShowInGuide(r, p),
        };
        if (p != null)
        {
            var left = Math.Max(1, (int)Math.Ceiling((p.End - now).TotalMinutes));
            s.Meta = $"{r.DisplayName}  ·  {p.Start:HH:mm} – {p.End:HH:mm}  ·  {left} min left";
            s.Progress = Math.Clamp((now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds), 0, 1);
            var n = epg.StartingFrom(id, p.End);
            s.Desc = p.Desc ?? (n != null ? $"Next at {n.Start:HH:mm}: {n.Title}" : null);
            s.Enrich = async () => new Extra(await ArtworkService.FindAsync(p), null, null);
        }
        else s.Meta = "No guide information";
        return s;
    }

    Slide ResumeSlide(ResumeEntry e)
    {
        bool movie = e.Kind == "movie";
        var detailsId = movie ? e.ItemId : e.SeriesId;
        var left = e.DurationMs > 0 ? $"{Left(e.DurationMs - e.PositionMs)} left" : null;
        var s = new Slide
        {
            Key = "resume|" + e.Key,
            Label = "Continue watching",
            Title = e.Title,
            Meta = string.Join("  ·  ", new[] { movie ? "Film" : e.Subtitle, left }.Where(x => !string.IsNullOrEmpty(x))),
            Poster = e.Image,
            Progress = e.Fraction,
            PrimaryText = "Resume", PrimaryIcon = "\uE768",
            Primary = () =>
            {
                var item = DataService.Instance.ResumeItem(e);
                if (item == null) MainWindow.Instance?.ShowToast("That title isn't in your playlist any more.");
                else MainWindow.Instance?.PlayVod(item, resume: true);
            },
        };
        if (!string.IsNullOrEmpty(detailsId))
        {
            s.SecondaryText = "More info"; s.SecondaryIcon = "\uE946";
            s.Secondary = () => MainWindow.Instance?.OpenLibraryItem(movie, detailsId);
            var cat = DataService.Instance.Catalog;
            if (movie && cat.Movies.FirstOrDefault(m => m.Id == detailsId) is Movie mv) s.Enrich = () => FilmExtra(mv);
            else if (!movie && cat.Series.FirstOrDefault(x => x.Id == detailsId) is Series sr) s.Enrich = () => ShowExtra(sr, false);
        }
        return s;
    }

    Slide FilmSlide(Movie m) => new()
    {
        Key = "film|" + m.Id,
        Label = "New film",
        Title = StripYear(m.Name),
        Meta = MetaLine(null, m.Year, m.Rating),
        Poster = m.Poster,
        PrimaryText = "More info", PrimaryIcon = "\uE946",
        Primary = () => MainWindow.Instance?.OpenLibraryItem(true, m.Id),
        Enrich = () => FilmExtra(m),
    };

    Slide ShowSlide(Series x) => new()
    {
        Key = "show|" + x.Id,
        Label = "New TV show",
        Title = StripYear(x.Name),
        Meta = MetaLine(x.Genre, x.Year, x.Rating),
        Desc = x.Plot,
        Poster = x.Cover,
        PrimaryText = "More info", PrimaryIcon = "\uE946",
        Primary = () => MainWindow.Instance?.OpenLibraryItem(false, x.Id),
        Enrich = () => ShowExtra(x, true),
    };

    Slide WelcomeSlide(DateTime now) => new()
    {
        Key = "welcome",
        Label = "Dingus",
        Title = now.Hour < 12 ? "Good morning" : now.Hour < 18 ? "Good afternoon" : "Good evening",
        Meta = DataService.Instance.Active == null ? $"{now:dddd d MMMM}" : $"{DataService.Instance.Active.Name}  ·  {now:dddd d MMMM}",
        Desc = "Pick a channel in Live TV to get started. Your last channel, favourites and new films and shows will show up here.",
        PrimaryText = "Open Live TV", PrimaryIcon = "\uE7F4",
        Primary = () => { if (MainWindow.Instance != null) MainWindow.Instance.NavGuide.IsChecked = true; },
    };

    static async Task<Extra> FilmExtra(Movie m)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var d = await DataService.Instance.GetMovieDetailsAsync(m, cts.Token);
        return new Extra(d?.Backdrop, d?.Plot, d == null ? null : MetaLine(d.Genre, d.Year ?? m.Year, ParseRating(d.Rating) ?? m.Rating));
    }

    static async Task<Extra> ShowExtra(Series x, bool meta)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var d = await DataService.Instance.GetSeriesDetailsAsync(x, cts.Token);
        return new Extra(d?.Backdrop, d?.Plot, d == null || !meta ? null : MetaLine(d.Genre ?? x.Genre, d.Year ?? x.Year, ParseRating(d.Rating) ?? x.Rating));
    }

    static double? ParseRating(string r) =>
        double.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    static string MetaLine(string genre, string year, double rating)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(genre)) parts.Add(string.Join(", ", genre.Split(new[] { ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2)));
        if (!string.IsNullOrWhiteSpace(year)) parts.Add(year.Length > 4 ? year[..4] : year);
        if (rating > 0) parts.Add($"★ {rating:0.#}");
        return string.Join("  ·  ", parts);
    }

    static string StripYear(string name) => System.Text.RegularExpressions.Regex.Replace(name ?? "", @"\s*\(\d{4}\)\s*$", "");

    // ------------------------------------------------------------------ showing a slide

    void Step(int by)
    {
        if (_slides.Count < 2) return;
        _index = (_index + by + _slides.Count) % _slides.Count;
        Show(_index, animate: true);
    }

    void Show(int index, bool animate)
    {
        var token = ++_showToken;
        var s = _slides[index];
        HeroDots.ItemsSource = _slides.Count > 1 ? _slides.Select((x, i) => new Dot { Title = x.Title, On = i == index }).ToList() : null;
        HeroDotsBox.Visibility = _slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        HeroNext.Visibility = _slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _advance.Stop(); _advance.Start(); // a full interval on every slide

        if (!animate)
        {
            HeroTextShift.BeginAnimation(TranslateTransform.XProperty, null);
            Fade(HeroText, 1, 200);
            ApplyText(s);
            _ = ApplyArt(s, token, fade: false);
            return;
        }
        // Quick fade out, swap, then fade in (the text drifts in slightly).
        var outA = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        outA.Completed += (_, _) =>
        {
            if (token != _showToken) return;
            ApplyText(s);
            _ = ApplyArt(s, token, fade: true);
            Fade(HeroText, 1, 420);
            HeroTextShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        HeroText.BeginAnimation(OpacityProperty, outA);
        Fade(HeroArt, 0, 160);
        PreloadNext();
    }

    static void Fade(UIElement e, double to, int ms) =>
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    void ApplyText(Slide s)
    {
        _extras.TryGetValue(s.Key, out var x);
        HeroLabel.Text = s.Label;
        HeroChip.Visibility = string.IsNullOrEmpty(s.Label) ? Visibility.Collapsed : Visibility.Visible;
        HeroTitle.Text = s.Title;
        HeroTitle.FontSize = s.Title?.Length > 34 ? 40 : 52;
        HeroTitle.LineHeight = HeroTitle.FontSize * 1.18;
        HeroTitle.MaxHeight = HeroTitle.LineHeight * 2 + 4;
        var meta = !string.IsNullOrEmpty(x?.Meta) ? x.Meta : s.Meta;
        HeroMeta.Text = meta ?? "";
        HeroMeta.Visibility = string.IsNullOrEmpty(meta) ? Visibility.Collapsed : Visibility.Visible;
        var desc = !string.IsNullOrWhiteSpace(x?.Desc) ? x.Desc : s.Desc;
        HeroDesc.Text = desc?.Trim() ?? "";
        HeroDesc.Visibility = string.IsNullOrWhiteSpace(desc) ? Visibility.Collapsed : Visibility.Visible;
        HeroProgress.Visibility = s.Progress >= 0 ? Visibility.Visible : Visibility.Collapsed;
        HeroProgress.Value = Math.Max(0, s.Progress);
        HeroPrimaryText.Text = s.PrimaryText;
        HeroPrimaryIcon.Text = s.PrimaryIcon ?? "";
        HeroSecondary.Visibility = s.Secondary == null ? Visibility.Collapsed : Visibility.Visible;
        HeroSecondaryText.Text = s.SecondaryText ?? "";
        HeroSecondaryIcon.Text = s.SecondaryIcon ?? "";
    }

    /// <summary>Shows the best picture we have for the slide now, and fetches a better one if there's a way to.</summary>
    async Task ApplyArt(Slide s, int token, bool fade)
    {
        _extras.TryGetValue(s.Key, out var x);
        var url = !string.IsNullOrEmpty(x?.Art) ? x.Art : !string.IsNullOrEmpty(s.Art) ? s.Art : s.Poster;
        await ShowPicture(url, s.Logo, token);
        if (token != _showToken) return;
        Fade(HeroArt, 1, fade ? 500 : 250);

        if (x != null || s.Enrich == null || !_enriching.Add(s.Key)) return;
        try
        {
            var extra = await s.Enrich();
            _extras[s.Key] = extra ?? new Extra(null, null, null);
        }
        catch (Exception ex) { Log.Error(ex); _extras[s.Key] = new Extra(null, null, null); }
        finally { _enriching.Remove(s.Key); }
        if (token != _showToken || !_shown) return;
        var better = _extras[s.Key];
        ApplyText(s);
        if (!string.IsNullOrEmpty(better.Art) && better.Art != url)
        {
            var bmp = await ImageLoader.GetAsync(better.Art, 1600);
            if (bmp == null || token != _showToken) return;
            Fade(HeroArt, 0, 200);
            await Task.Delay(200);
            if (token != _showToken) return;
            Place(bmp, s.Logo);
            Fade(HeroArt, 1, 500);
        }
    }

    /// <summary>Gets the next slide's details ready, so it appears with its picture.</summary>
    async void PreloadNext()
    {
        if (_slides.Count < 2) return;
        var n = _slides[(_index + 1) % _slides.Count];
        if (_extras.ContainsKey(n.Key) || n.Enrich == null || !_enriching.Add(n.Key)) return;
        try { _extras[n.Key] = await n.Enrich() ?? new Extra(null, null, null); }
        catch (Exception ex) { Log.Error(ex); _extras[n.Key] = new Extra(null, null, null); }
        finally { _enriching.Remove(n.Key); }
        var art = _extras[n.Key].Art ?? n.Poster;
        if (!string.IsNullOrEmpty(art)) _ = ImageLoader.GetAsync(art, 1600); // warms the cache
    }

    async Task ShowPicture(string url, string logo, int token)
    {
        var bmp = string.IsNullOrEmpty(url) ? null : await ImageLoader.GetAsync(url, 1600);
        if (token != _showToken) return;
        Place(bmp, logo);
    }

    /// <summary>Lays the picture out: wide pictures fill the area; posters sit on the right on a blurred copy; no picture → the logo on a colour wash.</summary>
    void Place(BitmapSource bmp, string logo)
    {
        bool wide = bmp != null && bmp.PixelWidth >= bmp.PixelHeight * 1.25;
        bool tall = bmp != null && !wide;
        HeroBackdrop.Source = wide ? bmp : null;
        HeroBackdrop.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        HeroBlur.Source = tall ? bmp : null;
        HeroBlur.Visibility = tall ? Visibility.Visible : Visibility.Collapsed;
        HeroPosterBrush.ImageSource = tall ? bmp : null;
        HeroPosterFrame.Visibility = tall ? Visibility.Visible : Visibility.Collapsed;
        if (tall)
        {
            double h = Hero.Height * 0.66;
            HeroPosterFrame.Height = h;
            HeroPosterFrame.Width = h * bmp.PixelWidth / Math.Max(1, bmp.PixelHeight);
        }
        HeroWash.Visibility = bmp == null ? Visibility.Visible : Visibility.Collapsed;
        HeroLogo.Visibility = bmp == null && logo != null ? Visibility.Visible : Visibility.Collapsed;
        ImageLoader.SetUrl(HeroLogo, bmp == null ? logo : null);
    }

    void Home_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged && !e.WidthChanged) return;
        Hero.Height = Math.Clamp(ActualHeight * 0.74, 420, 780);
        double side = Math.Clamp(ActualWidth * 0.08, 40, 160);
        HeroPosterFrame.Margin = new Thickness(0, 0, side, 60);
        HeroLogo.Margin = new Thickness(0, 0, side, 60);
        if (HeroPosterFrame.Visibility == Visibility.Visible && HeroPosterBrush.ImageSource is BitmapSource b)
        {
            HeroPosterFrame.Height = Hero.Height * 0.66;
            HeroPosterFrame.Width = HeroPosterFrame.Height * b.PixelWidth / Math.Max(1, b.PixelHeight);
        }
        // Narrow windows: the text uses more of the width.
        HeroText.MaxWidth = Math.Max(360, Math.Min(720, ActualWidth - 120));
    }

    void Hero_MouseEnter(object sender, MouseEventArgs e) => _heroHover = true;
    void Hero_MouseLeave(object sender, MouseEventArgs e) => _heroHover = false;

    void HeroNext_Click(object sender, RoutedEventArgs e) => Step(+1);

    void HeroDot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Dot d) return;
        int i = (HeroDots.ItemsSource as List<Dot>)?.IndexOf(d) ?? -1;
        if (i < 0 || i == _index) return;
        _index = i;
        Show(i, animate: true);
    }

    void HeroPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _slides.Count) _slides[_index].Primary?.Invoke();
    }

    void HeroSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _slides.Count) _slides[_index].Secondary?.Invoke();
    }

    void ViewAll_Click(object sender, RoutedEventArgs e)
    {
        var mw = MainWindow.Instance;
        if (mw == null) return;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "fav": mw.NavGuide.IsChecked = true; Guide.ShowAndSelect(GuideView.FavId, null); break;
            case "recent": mw.NavGuide.IsChecked = true; Guide.ShowAndSelect(GuideView.RecentId, null); break;
            case "films": mw.NavFilms.IsChecked = true; break;
            case "shows": mw.NavShows.IsChecked = true; break;
        }
    }

    void UpdateAccount()
    {
        var acc = DataService.Instance.Catalog.Account;
        if (acc == null) { AccountStrip.Visibility = Visibility.Collapsed; return; }
        AccountStrip.Visibility = Visibility.Visible;

        var parts = new List<string>();
        var status = string.IsNullOrEmpty(acc.Status) ? "Unknown" : char.ToUpper(acc.Status[0]) + acc.Status.Substring(1);
        parts.Add((acc.Trial ? "Trial · " : "Subscription ") + status.ToLowerInvariant());
        int? days = null;
        if (acc.ExpiresUtc is DateTime exp)
        {
            days = (int)Math.Floor((exp - DateTime.UtcNow).TotalDays);
            parts.Add(days < 0 ? $"expired {exp.ToLocalTime():d MMM yyyy}"
                    : days == 0 ? "expires today"
                    : $"expires {exp.ToLocalTime():d MMM yyyy} ({days} day{(days == 1 ? "" : "s")})");
        }
        else parts.Add("no expiry date");
        if (acc.MaxConnections > 0) parts.Add($"{acc.ActiveConnections} of {acc.MaxConnections} connection{(acc.MaxConnections == 1 ? "" : "s")} in use");
        AccountText.Text = string.Join("  ·  ", parts);

        bool bad = days < 0 || !string.Equals(acc.Status, "active", StringComparison.OrdinalIgnoreCase);
        bool warn = days <= 14 || (acc.MaxConnections > 0 && acc.ActiveConnections >= acc.MaxConnections);
        AccountDot.Fill = new System.Windows.Media.SolidColorBrush(bad ? System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71)
                                                                : warn ? System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24)
                                                                : System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80));
    }

    HomeChannelCard NowCard(GuideRow r, string listId, EpgData epg, DateTime now)
    {
        var p = epg.At(Guide.EpgIdOf(r), now);
        return new HomeChannelCard
        {
            Row = r,
            ListId = listId,
            Title = p?.Title ?? "No guide information",
            When = p != null ? $"{p.Start:HH:mm} – {p.End:HH:mm}" : "",
            Progress = p == null ? 0 : (now - p.Start).TotalSeconds / Math.Max(1, (p.End - p.Start).TotalSeconds),
            GenreBrush = Genres.Brush(p?.Genre),
        };
    }

    static string Left(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";
    }

    // ------------------------------------------------------------------ interaction

    void Channel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeChannelCard c) return;
        if (c.Upcoming != null && c.Upcoming.Start > DateTime.Now)
        {
            // Not on yet: open the guide on that programme.
            MainWindow.Instance?.ShowInGuide(c.Row, c.Upcoming);
            return;
        }
        // Channel up/down in the player then moves through this row's list (favourites, recent, all).
        Guide.ShowAndSelect(c.ListId, c.Row);
        MainWindow.Instance?.PlayLive(c.Row.Channel, true);
    }

    /// <summary>Right-click a programme that hasn't started yet: set or cancel a reminder.</summary>
    void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeChannelCard c || c.Upcoming == null || c.Upcoming.Start <= DateTime.Now) return;
        e.Handled = true;
        bool set = ReminderService.Has(c.Row.Channel.Id, c.Upcoming.Start);
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };
        var item = new MenuItem { Header = set ? "Cancel reminder" : $"Remind me ({c.Upcoming.Start:ddd HH:mm})" };
        item.Click += (_, _) =>
        {
            bool on = ReminderService.Toggle(c.Row.Channel.Id, c.Row.DisplayName, c.Upcoming);
            MainWindow.Instance?.ShowToast(on ? $"Reminder set for {c.Upcoming.Title}" : "Reminder removed");
        };
        var guide = new MenuItem { Header = "Show in Live TV" };
        guide.Click += (_, _) => MainWindow.Instance?.ShowInGuide(c.Row, c.Upcoming);
        var recItem = new MenuItem { Header = RecordUi.IsRecording(c.Row, c.Upcoming) ? "Cancel recording" : "Record" };
        recItem.Click += (_, _) => RecordUi.ToggleProgramme(c.Row, c.Upcoming);
        menu.Items.Add(recItem);
        menu.Items.Add(item);
        menu.Items.Add(guide);
        menu.IsOpen = true;
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeResumeCard c) return;
        var item = DataService.Instance.ResumeItem(c.Entry);
        if (item == null) { MainWindow.Instance?.ShowToast("That title isn't in your playlist any more."); return; }
        MainWindow.Instance?.PlayVod(item, resume: true);
    }

    void Poster_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HomePosterCard c) MainWindow.Instance?.OpenLibraryItem(c.IsMovie, c.Id);
    }

    /// <summary>Mouse wheel scrolls the page (not the row it's over); Shift + wheel scrolls the row sideways.</summary>
    void Row_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer row) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) row.ScrollToHorizontalOffset(row.HorizontalOffset - e.Delta);
        else Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
    }
}
