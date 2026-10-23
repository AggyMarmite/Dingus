using System.Text.RegularExpressions;

namespace Dingus.Services;

/// <summary>
/// Film and show trailers. Xtream providers give one per title as "youtube_trailer" – sometimes a bare YouTube id,
/// sometimes a full link, often blank or junk. This turns whatever is there into a YouTube link (or null).
/// </summary>
public static partial class Trailer
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex IdOnly();

    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:watch\?(?:.*&)?v=|embed/|v/|shorts/)|youtu\.be/)([A-Za-z0-9_-]{11})")]
    private static partial Regex InLink();

    /// <summary>A YouTube watch link for the trailer, or null when there isn't a usable one.</summary>
    public static string Url(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (IdOnly().IsMatch(raw)) return "https://www.youtube.com/watch?v=" + raw;
        var m = InLink().Match(raw);
        if (m.Success) return "https://www.youtube.com/watch?v=" + m.Groups[1].Value;
        return null;
    }
}
