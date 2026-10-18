using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Dingus.Services;

namespace Dingus.Models;

public enum PlaylistType { Xtream = 0, M3U = 1 }

public class PlaylistConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public PlaylistType Type { get; set; }
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Password encrypted with Windows DPAPI (current user only).</summary>
    public string ProtectedPassword { get; set; } = "";
    public string M3uUrl { get; set; } = "";
    /// <summary>Optional EPG override. Empty = use the provider's guide.</summary>
    public string EpgUrl { get; set; } = "";
    public List<string> HiddenCategories { get; set; } = new();
    public List<string> Favourites { get; set; } = new();
    /// <summary>Channel editor: your own names (channel id → name), kept across refreshes.</summary>
    public Dictionary<string, string> ChannelNames { get; set; } = new();
    /// <summary>Parental controls: locked categories ("live:12", "vod:7" …).</summary>
    public List<string> LockedCategories { get; set; } = new();
    /// <summary>Per-channel picture fixes: channel id → "decoder|deinterlace" (e.g. "dxva2|1", "software|0").</summary>
    public Dictionary<string, string> PictureFixes { get; set; } = new();
    /// <summary>Guide matches you've made by hand (channel id → guide channel id; "" = no guide).</summary>
    public Dictionary<string, string> EpgOverrides { get; set; } = new();
    /// <summary>Channel editor: channels you've hidden (every version's id).</summary>
    public List<string> HiddenChannels { get; set; } = new();
    /// <summary>Your own category order per kind ("live" / "vod" / "series" → category ids, top first).</summary>
    public Dictionary<string, List<string>> CategoryOrder { get; set; } = new();
    /// <summary>Channel editor: your own order per category (category id → channel ids, top first).</summary>
    public Dictionary<string, List<string>> ChannelOrder { get; set; } = new();
    public DateTime LastRefreshUtc { get; set; }
    public DateTime LastEpgRefreshUtc { get; set; }

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(ProtectedPassword);
        set => ProtectedPassword = Secret.Protect(value);
    }

    [JsonIgnore]
    public string Description => Type == PlaylistType.Xtream
        ? $"Xtream Codes · {HostOf(Server)}"
        : $"M3U · {HostOf(M3uUrl)}";

    static string HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !u.IsFile) return u.Host;
        if (Uri.TryCreate("http://" + url, UriKind.Absolute, out var u2)) return u2.Host;
        return url;
    }
}

public class AppSettings
{
    public List<PlaylistConfig> Playlists { get; set; } = new();
    public string ActivePlaylistId { get; set; }
    public bool HardwareDecoding { get; set; } = true;
    public int NetworkCachingMs { get; set; } = 1500;
    public bool PreferHls { get; set; }
    public int EpgShiftMinutes { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public int RefreshIntervalHours { get; set; } = 24;
    public int EpgRefreshHours { get; set; } = 12;
    public int Volume { get; set; } = 80;
    public string LastChannelId { get; set; }
    public bool ShowStreamInfo { get; set; }
    /// <summary>Switch the display's refresh rate to match the video while watching full screen.</summary>
    public bool AdaptiveFrameRate { get; set; } = true;
    /// <summary>Show "Channel One", "Channel One HD", "Channel One FHD" … as one channel with automatic fallback.</summary>
    public bool MergeDuplicates { get; set; } = true;
    /// <summary>Highest quality to pick for merged channels: best | 1080 | 720.</summary>
    public string PreferredQuality { get; set; } = "best";
    /// <summary>Dedicated graphics on mains power, built-in graphics (and lighter settings) on battery.</summary>
    public bool BatteryAware { get; set; } = true;
    public bool GpuPreferenceSaved { get; set; }
    public string GpuPreferenceOriginal { get; set; }
    /// <summary>Open on the TV Guide instead of Home.</summary>
    public bool StartOnGuide { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>Accent colour: blue | purple | teal | green | orange | pink.</summary>
    public string Accent { get; set; } = "blue";
    /// <summary>Background theme: midnight | slate | black.</summary>
    public string Theme { get; set; } = "midnight";
    public bool AutoUpdate { get; set; } = true;
    public DateTime LastUpdateCheckUtc { get; set; }
    public string SkippedVersion { get; set; }
    /// <summary>Draw the app's menus/guide without the graphics card (saves the interface's own GPU driver memory).</summary>
    public bool LowMemoryInterface { get; set; }

    // Window placement (device-independent units). null = not saved yet.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public double? MiniWidth { get; set; }
    // Parental controls (only a salted hash of the PIN is kept).
    public string ParentalPinHash { get; set; }
    public string ParentalPinSalt { get; set; }
    /// <summary>Lock categories whose names look adult ("Adult", "XXX", "18+") automatically.</summary>
    public bool AutoLockAdult { get; set; } = true;

    // Picture & sound
    /// <summary>Aspect ratio: null = as broadcast, or "16:9", "4:3", "21:9".</summary>
    public string AspectRatio { get; set; }
    /// <summary>Crop: null = off, "fill" = fill the screen, or a ratio such as "16:9" / "2.35:1".</summary>
    public string Crop { get; set; }
    /// <summary>Evens out loud and quiet sounds (late-night viewing).</summary>
    public bool NightMode { get; set; }
    /// <summary>Preferred audio / subtitle language (ISO code such as "eng"); null = the stream's default / off.</summary>
    public string AudioLanguage { get; set; }
    public string SubtitleLanguage { get; set; }

    /// <summary>Look up programme artwork online (TVmaze) when the guide and your library don't have any.</summary>
    public bool OnlineArtwork { get; set; } = true;

    // Recordings
    /// <summary>Where recordings are saved (empty = Videos\Dingus Recordings). Can be another drive or a network share.</summary>
    public string RecordingsFolder { get; set; }
    /// <summary>Minutes to start early / carry on after, in case programmes start early or overrun.</summary>
    public int RecordPadBefore { get; set; } = 2;
    public int RecordPadAfter { get; set; } = 5;

    /// <summary>Pause &amp; rewind live TV (buffers the channel you're watching to disk).</summary>
    public bool Timeshift { get; set; } = true;
    /// <summary>How far back you can rewind live TV.</summary>
    public int TimeshiftMinutes { get; set; } = 60;
    /// <summary>Multi-view layout: 4, 6 or 8 screens.</summary>
    public int MultiViewLayout { get; set; } = 4;
}

public class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class Channel
{
    public string Id { get; set; } = "";
    public int Num { get; set; }
    public string Name { get; set; } = "";
    public string Logo { get; set; }
    public string EpgId { get; set; }
    public string CategoryId { get; set; } = "";
    /// <summary>Direct stream URL (M3U playlists only).</summary>
    public string Url { get; set; }
    public bool HasArchive { get; set; }
    /// <summary>How many days back catch-up is available (0 = unknown).</summary>
    public int ArchiveDays { get; set; }
}

public class Movie
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Poster { get; set; }
    public string CategoryId { get; set; } = "";
    public double Rating { get; set; }
    public string Year { get; set; }
    public string Ext { get; set; } = "mp4";
    public string Url { get; set; }
    public long Added { get; set; }
}

public class Series
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Cover { get; set; }
    public string CategoryId { get; set; } = "";
    public string Plot { get; set; }
    public string Genre { get; set; }
    public double Rating { get; set; }
    public string Year { get; set; }
    public long Added { get; set; }
    /// <summary>Only used for M3U playlists, where episodes come with the playlist.</summary>
    public List<Episode> Episodes { get; set; }
}

public class Episode
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int Season { get; set; }
    public int Number { get; set; }
    public string Ext { get; set; } = "mp4";
    public string Url { get; set; }
    public string Plot { get; set; }
    public string Image { get; set; }
    public string Duration { get; set; }
}

public class Programme
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Title { get; set; } = "";
    /// <summary>First XMLTV category (e.g. "Sport", "Film"), used to colour the guide.</summary>
    public string Genre;

    // Descriptions are the bulk of a TV guide, so they live in a small file on disk and are read
    // only when you look at a programme.
    public DescStore Store;
    public long DescOffset;
    public int DescLength;
    // The stored text is "description" or "description\u0001image-url" (image only if the guide has one).
    public const char ImageSeparator = '\u0001';
    string Stored => DescLength > 0 ? Store?.Read(DescOffset, DescLength) : null;
    public string Desc
    {
        get
        {
            var s = Stored;
            if (s == null) return null;
            int i = s.IndexOf(ImageSeparator);
            var d = i < 0 ? s : s[..i];
            return d.Length == 0 ? null : d;
        }
    }
    /// <summary>The programme's own artwork from the TV guide, if there is any.</summary>
    public string ImageUrl
    {
        get
        {
            var s = Stored;
            int i = s?.IndexOf(ImageSeparator) ?? -1;
            return i < 0 ? null : s[(i + 1)..];
        }
    }
}

/// <summary>"Remind me" for a programme that hasn't started yet.</summary>
public class Reminder
{
    public string PlaylistId { get; set; }
    public string ChannelId { get; set; }
    public string ChannelName { get; set; }
    public string Title { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
}

public class AccountInfo
{
    public string Status { get; set; } = "";
    public DateTime? ExpiresUtc { get; set; }
    public int ActiveConnections { get; set; }
    public int MaxConnections { get; set; }
    public bool Trial { get; set; }
    /// <summary>Provider's time zone (IANA name, e.g. "Europe/London") – catch-up start times are in this zone.</summary>
    public string ServerTimeZone { get; set; }
}

/// <summary>A film, episode or catch-up programme handed to the full-screen player.</summary>
public class VodItem
{
    public string Kind { get; set; } = "movie"; // movie | episode | catchup
    public string Title { get; set; }
    public string Subtitle { get; set; }
    public string Url { get; set; }
    public string Image { get; set; }
    /// <summary>History key for resume ("m:id" / "e:id"); null = don't remember position.</summary>
    public string ResumeKey { get; set; }
    public string ItemId { get; set; }
    public string SeriesId { get; set; }
    /// <summary>Builds the next episode, or null when this is the last one.</summary>
    public Func<VodItem> Next { get; set; }
    public string NextLabel { get; set; }
    /// <summary>File type (mp4/mkv…) – lets "Continue watching" rebuild the address without storing your login.</summary>
    public string Ext { get; set; }
}

public class ResumeEntry
{
    public string Key { get; set; }
    public string Kind { get; set; }
    public string ItemId { get; set; }
    public string SeriesId { get; set; }
    public string Title { get; set; }
    public string Subtitle { get; set; }
    public string Image { get; set; }
    public string Ext { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public bool Finished { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public double Fraction => DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;
    /// <summary>Worth offering "resume": past the first 30 s and not finished.</summary>
    public bool CanResume => !Finished && PositionMs > 30_000 && (DurationMs <= 0 || PositionMs < DurationMs - 60_000);
}

public class HistoryData
{
    public List<string> RecentChannels { get; set; } = new();
    public Dictionary<string, ResumeEntry> Resume { get; set; } = new();
    /// <summary>Films and shows saved for later ("My List"), newest first.</summary>
    public List<SavedItem> MyList { get; set; } = new();
}

/// <summary>A film or show on My List. The name and picture are kept so the list still reads well if the
/// provider drops the title.</summary>
public class SavedItem
{
    public string Kind { get; set; } // movie | series
    public string Id { get; set; }
    public string Title { get; set; }
    public string Image { get; set; }
    public DateTime AddedUtc { get; set; }
}

public class CatalogData
{
    public List<Category> LiveCategories { get; set; } = new();
    public List<Category> VodCategories { get; set; } = new();
    public List<Category> SeriesCategories { get; set; } = new();
    public List<Channel> Channels { get; set; } = new();
    public List<Movie> Movies { get; set; } = new();
    public List<Series> Series { get; set; } = new();
    public AccountInfo Account { get; set; }
    public string SourceEpgUrl { get; set; }
}

public class MediaDetails
{
    public string Title { get; set; }
    public string Plot { get; set; }
    public string Cast { get; set; }
    public string Director { get; set; }
    public string Genre { get; set; }
    public string Year { get; set; }
    public string Duration { get; set; }
    public string Rating { get; set; }
    public string Backdrop { get; set; }
    public string Poster { get; set; }
    public List<Episode> Episodes { get; set; } = new();
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
