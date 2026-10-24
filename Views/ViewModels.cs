using Dingus.Models;
#if LINUX
using GenreBrushType = Avalonia.Media.IBrush;
#else
using GenreBrushType = System.Windows.Media.Brush;
#endif

namespace Dingus.Views;

public interface IPage
{
    string Title { get; }
    bool SupportsSearch { get; }
    string SearchHint { get; }
    void ApplySearch(string query);
    void OnShown();
    /// <summary>Called when you switch to another page – a chance to let go of memory.</summary>
    void OnHidden();
}

public class CategoryItem : Observable
{
    public string Id { get; set; }
    public string Name { get; set; }
    int _count;
    public int Count { get => _count; set => Set(ref _count, value); }
}

public class GuideRow : Observable
{
    public GuideRow(Channel c, int index)
    {
        Channel = c;
        Number = c.Num > 0 ? c.Num.ToString() : index.ToString();
        Initials = MakeInitials(CleanName(c.Name)); // "UK| Channel One" → "CO", not "UB"
    }

    /// <summary>The channel as listed (first version found). Its id identifies the row for favourites/history.</summary>
    public Channel Channel { get; }
    public string Number { get; }
    public string Name => CustomName ?? Channel.Name;
    /// <summary>Name without the leading country tag ("UK| ", "[UK] ", "UK: ") that repeats on every row.</summary>
    public string DisplayName => CustomName ?? (_display ??= CleanName(Channel.Name));
    /// <summary>The provider's name, tidied – what the channel editor resets to.</summary>
    public string OriginalName => _display ??= CleanName(Channel.Name);
    /// <summary>Your own name for the channel (channel editor), or null.</summary>
    public string CustomName { get; set; }
    /// <summary>Hidden in the channel editor.</summary>
    public bool IsHidden { get; set; }
    string _display;
    public string Initials { get; }

    static readonly System.Text.RegularExpressions.Regex TagRx = new(
        @"^\s*(?:\[[^\]]{1,6}\]|\|[^|]{1,6}\||[A-Za-z]{2,3}\s*[:|]|[A-Z]{2,3}\s+-\s+)\s*",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    static string CleanName(string n)
    {
        if (string.IsNullOrWhiteSpace(n)) return n;
        var c = TagRx.Replace(n, "").Trim();
        return c.Length >= 2 ? c : n.Trim();
    }

    /// <summary>All versions of this channel (HD, FHD, 4K, backups). One entry when nothing was merged.</summary>
    public List<Channel> Variants { get; } = new();
    /// <summary>Every category any version is listed in.</summary>
    public HashSet<string> CategoryIds { get; } = new();
    /// <summary>Which version's guide data to show (the first one that has any).</summary>
    public string EpgId { get; set; }
    /// <summary>A version that offers catch-up, if any.</summary>
    public Channel CatchupChannel => Variants.FirstOrDefault(v => v.HasArchive);

    public void AddVariant(Channel c)
    {
        Variants.Add(c);
        CategoryIds.Add(c.CategoryId ?? "");
    }

    public bool Has(string channelId) => Variants.Any(v => v.Id == channelId);

    /// <summary>"3 versions · 4K, 1080p, HD" – shown on the programme details.</summary>
    public string VersionsText => Variants.Count < 2 ? "" :
        $"{Variants.Count} versions · " + string.Join(", ", Variants.Select(Services.ChannelMerger.Label).Distinct());

    string _logo;
    public string Logo { get => _logo; set => Set(ref _logo, value); }

    bool _fav;
    public bool IsFavourite { get => _fav; set => Set(ref _fav, value); }

    /// <summary>Bumped whenever the guide's time window or data changes; rows rebuild lazily.</summary>
    public static int Version;
    /// <summary>Builds this row's programme blocks. Only called for rows that are actually on screen.</summary>
    public Func<GuideRow, IReadOnlyList<ProgBlock>> BlockBuilder { get; set; }

    IReadOnlyList<ProgBlock> _blocks = Array.Empty<ProgBlock>();
    int _builtVersion = -1;
    public IReadOnlyList<ProgBlock> Blocks
    {
        get
        {
            if (_builtVersion != Version && BlockBuilder != null)
            {
                _blocks = BlockBuilder(this);
                _builtVersion = Version;
            }
            return _blocks;
        }
    }

    /// <summary>Tells the screen to re-read Blocks. Rows that aren't on screen have no listeners, so they cost nothing.</summary>
    public void InvalidateBlocks() => Raise(nameof(Blocks));

    static string MakeInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "TV";
        var words = name.Split(new[] { ' ', '-', '|', ':', '_' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => char.IsLetterOrDigit(w[0])).Take(2).ToArray();
        if (words.Length == 0) return "TV";
        return string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
    }
}

/// <summary>A channel card on the Home page (recently watched / favourites / up next).</summary>
public class HomeChannelCard
{
    public GuideRow Row { get; init; }
    public string ListId { get; init; } // which guide list channel up/down should move through
    public string Name => Row.DisplayName;
    public string Logo => Row.Logo;
    public string Initials => Row.Initials;
    public string Title { get; init; }
    public string When { get; init; }
    public double Progress { get; init; }
    public bool HasProgress => Progress > 0.001;
    public bool Soon { get; init; }
    /// <summary>"  ·  In 10 min" after the channel name, for cards without a progress bar or badge.</summary>
    public string WhenSuffix => HasProgress || Soon || string.IsNullOrEmpty(When) ? "" : "  ·  " + When;
    public GenreBrushType GenreBrush { get; init; }
    /// <summary>For future programmes (Tonight's picks): opens the guide on it instead of playing now.</summary>
    public Programme Upcoming { get; init; }
}

/// <summary>A "new film / new show" poster on the Home page.</summary>
public class HomePosterCard
{
    public string Id { get; init; }
    public bool IsMovie { get; init; }
    public string Title { get; init; }
    public string Image { get; init; }
    public string Subtitle { get; init; }
    /// <summary>The "NEW" badge (the New films / New TV shows rows – not My List).</summary>
    public bool IsNew { get; init; } = true;
}

/// <summary>A film or episode card in "Continue watching".</summary>
public class HomeResumeCard
{
    public ResumeEntry Entry { get; init; }
    public string Title => Entry.Title;
    public string Subtitle { get; init; }
    public string Image => Entry.Image;
    public double Progress => Entry.Fraction;
}

/// <summary>A row in the player's channel list: channel plus what's on now.</summary>
public class ZapItem
{
    public GuideRow Row { get; init; }
    public string Number => Row.Number;
    public string Name => Row.DisplayName;
    public string Logo => Row.Logo;
    public string Initials => Row.Initials;
    public string Now { get; init; }
    public double Progress { get; init; }
    public bool IsCurrent { get; init; }
}

public class ProgBlock
{
#if LINUX
    /// <summary>Where the block sits along the row (Linux lays blocks out with a left margin).</summary>
    public Avalonia.Thickness LeftMargin => new(Left, 0, 0, 0);
#endif
    public string Title { get; set; }
    public string TimeText { get; set; }
    public double Left { get; set; }
    public double Width { get; set; }
    public bool IsNow { get; set; }
    public bool IsPast { get; set; }
    /// <summary>Started, still in the catch-up window – can be played from the beginning.</summary>
    public bool CanCatchup { get; set; }
    public Programme Programme { get; set; }
    public GuideRow Row { get; set; }
    public bool HasReminder { get; set; }
    public bool HasRecording { get; set; }
    public GenreBrushType GenreBrush => Services.Genres.Brush(Programme?.Genre);
}

/// <summary>A line in the "On now" / "Tonight" lists.</summary>
public class NowItem
{
    public GuideRow Row { get; init; }
    public Programme Programme { get; init; }
    public string TimeText { get; init; }
    public string SubTime { get; init; }
    public string Title => Programme?.Title ?? "No guide information";
    public string Genre => Programme?.Genre;
    public double Progress { get; init; }
    public bool IsLive { get; init; }
    public bool CanCatchup { get; init; }
    public bool HasReminder { get; init; }
    public GenreBrushType GenreBrush => Services.Genres.Brush(Programme?.Genre);
}

/// <summary>A game on the Events page.</summary>
public class EventItem
{
    public Services.EventGame Game { get; init; }
    /// <summary>"LIVE" or the kick-off time.</summary>
    public string TimeText { get; init; }
    /// <summary>"until 17:00", "Today", "Tomorrow", "Sat 25 Oct".</summary>
    public string DayText { get; init; }
    public string Title => Game.Title;
    /// <summary>The competition (when the guide says), the channel, and how many other channels show it.</summary>
    public string Detail { get; init; }
    public bool IsLive { get; init; }
    public bool IsUpcoming => !IsLive;
    public bool HasReminder { get; init; }
    public string RemindText => HasReminder ? "Reminder set" : "Remind me";

    // The card's picture: both teams' crests (ESPN), else the guide's artwork, else a drawn "A v B" card.
    public string Home { get; init; }
    public string Away { get; init; }
    public string HomeCrest { get; init; }
    public string AwayCrest { get; init; }
    public string Art { get; init; }
    public string HomeInitials { get; init; }
    public string AwayInitials { get; init; }
    public string HomeColor { get; init; }
    public string AwayColor { get; init; }
    public GenreBrushType HomeBrush => ToBrush(HomeColor);
    public GenreBrushType AwayBrush => ToBrush(AwayColor);

    static GenreBrushType ToBrush(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
#if LINUX
        return Avalonia.Media.Brush.Parse(hex);
#else
        var b = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex);
        b?.Freeze();
        return b;
#endif
    }
    public bool ShowCrests => HomeCrest != null && AwayCrest != null;
    public bool ShowArt => !ShowCrests && Art != null;
    public bool ShowDrawn => !ShowCrests && Art == null;
    public bool HasAway => Away != null;
    /// <summary>"Sky Sports Main Event" or "Sky Sports Main Event + 2 more".</summary>
    public string Where { get; init; }
    /// <summary>"Today 15:00", "Sat 25 Oct 17:30", "LIVE · until 17:00".</summary>
    public string When { get; init; }
}

/// <summary>One channel showing a game (the list you get by clicking a game).</summary>
public class EventSourceItem
{
    public GuideRow Row { get; init; }
    public Models.Programme Programme { get; init; }
    public string Name => Row.DisplayName;
    public string Logo => Row.Logo;
    public string Initials => Row.Initials;
    /// <summary>The programme on that channel, with its times.</summary>
    public string Line { get; init; }
    public bool IsLive { get; init; }
    public bool IsUpcoming => !IsLive;
    public bool IsFavourite => Row.IsFavourite;
    public bool HasReminder { get; init; }
    public string RemindText => HasReminder ? "Reminder set" : "Remind me";
    public bool IsRecording { get; init; }
    public string RecordText => IsRecording ? (IsLive ? "Stop recording" : "Recording set") : "Record";
}

/// <summary>A followed team on the Events page.</summary>
public class TeamChip
{
    public string Name { get; init; }
    /// <summary>The team's crest (ESPN), or null.</summary>
    public string Crest { get; init; }
}

/// <summary>A provider's "event" channel whose name matches the search.</summary>
public class EventChannelItem
{
    public GuideRow Row { get; init; }
    public string Name => Row.DisplayName;
    public string Now { get; init; }
}

public class PosterItem : Observable
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Image { get; set; }
    public string Subtitle { get; set; }
    public string CategoryId { get; set; }
    public long Added { get; set; }
    public double Rating { get; set; }
    public object Source { get; set; }

    double _progress;
    /// <summary>0..1 watched (films in progress). 0 hides the bar.</summary>
    public double Progress
    {
        get => _progress;
        set { if (Set(ref _progress, value)) Raise(nameof(HasProgress)); }
    }
    public bool HasProgress => _progress > 0.01;

    bool _saved;
    /// <summary>On My List (a small bookmark on the poster).</summary>
    public bool Saved { get => _saved; set => Set(ref _saved, value); }
}

public class PosterRow
{
    public PosterItem[] Items { get; set; }
}

public class SeasonItem
{
    public int Number { get; set; }
    public string Label { get; set; }
    public List<EpisodeItem> Episodes { get; set; }
}

public class EpisodeItem : Observable
{
    public EpisodeItem(Episode e, int index)
    {
        Episode = e;
        Index = index;
        Header = $"{e.Number}. {e.Title}";
        Meta = string.IsNullOrWhiteSpace(e.Duration) ? $"Episode {e.Number}" : e.Duration;
    }
    public Episode Episode { get; }
    /// <summary>Position in the whole show (all seasons), used for "next episode".</summary>
    public int Index { get; }
    public string Header { get; }
    public string Meta { get; }
    public string Plot => Episode.Plot;
    public string Image => Episode.Image;

    double _progress;
    public double Progress
    {
        get => _progress;
        set { if (Set(ref _progress, value)) Raise(nameof(HasProgress)); }
    }
    public bool HasProgress => _progress > 0.01 && !_watched;

    bool _watched;
    public bool Watched
    {
        get => _watched;
        set { if (Set(ref _watched, value)) Raise(nameof(HasProgress)); }
    }
}
