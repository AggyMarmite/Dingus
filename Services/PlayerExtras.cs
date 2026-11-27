using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// What the player shows on top of a film or episode, as on the Fire TV: Skip intro / Skip recap / Skip credits (or
/// Next episode) while the episode is at that part (from TheIntroDB), and the pause screen's details. Shared by
/// Windows and Linux; the screens only draw it.
/// </summary>
public sealed class PlayerExtras
{
    public VodItem Item { get; }
    public Tmdb.Segments Segments { get; private set; }
    public string Logo { get; private set; }
    public Tmdb.Ratings Ratings { get; private set; }

    public PlayerExtras(VodItem item) { Item = item; }

    static readonly Regex Code = new(@"S(\d{1,3})\s*E(\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    bool IsEpisode => Item.Kind == "episode" && Item.SeriesId != null;
    bool Tv => Item.Kind == "episode";
    string TitleId => Tv ? Item.SeriesId : Item.ItemId;

    /// <summary>Looks up the skip points (episodes) and the pause screen's logo and ratings. Never throws.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (Item.Kind is not ("movie" or "episode") || TitleId == null) return;
        Logo = Tmdb.CachedLogo(Tv, TitleId);
        Ratings = Tmdb.CachedRatings(Tv, TitleId);
        try
        {
            var (logo, _) = await Tmdb.ImagesAsync(Tv, TitleId, Item.Title, Item.Year, null, ct);
            Logo = logo ?? Logo;
            Ratings = await Tmdb.RatingsAsync(Tv, TitleId, Item.Title, Item.Year, null, ct) ?? Ratings;
            if (IsEpisode && Code.Match(Item.Subtitle ?? "") is { Success: true } m)
            {
                var id = await Tmdb.IdForAsync(true, Item.SeriesId, Item.Title, Item.Year, null, ct);
                if (id != null) Segments = await Tmdb.SegmentsAsync(id, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>A Skip button to show now: its words, where it jumps to (-1 = the end) and whether it plays the next episode.</summary>
    public record Skip(string Text, long ToMs, bool PlaysNext, long PartStartMs);

    /// <summary>The skip that applies at <paramref name="timeMs"/>, or null.</summary>
    public Skip SkipAt(long timeMs, long lengthMs)
    {
        var s = Segments;
        if (s == null || timeMs <= 0) return null;
        static bool In(Tmdb.Part p, long t, long len) => p != null && t >= p.StartMs && t < (p.EndMs < 0 ? len : p.EndMs) - 1500;
        if (In(s.Recap, timeMs, lengthMs)) return new Skip("Skip recap", s.Recap.EndMs, false, s.Recap.StartMs);
        if (In(s.Intro, timeMs, lengthMs)) return new Skip("Skip intro", s.Intro.EndMs, false, s.Intro.StartMs);
        if (In(s.Credits, timeMs, lengthMs))
            return Item.Next != null ? new Skip("Next episode", -1, true, s.Credits.StartMs) : new Skip("Skip credits", s.Credits.EndMs, false, s.Credits.StartMs);
        return null;
    }

    /// <summary>"FILM · 2023" / "EPISODE · S2 E4" – the pause screen's top line (the tags follow it).</summary>
    public string KindLine()
    {
        if (Tv)
        {
            var m = Code.Match(Item.Subtitle ?? "");
            return m.Success ? $"EPISODE  ·  S{int.Parse(m.Groups[1].Value)} E{int.Parse(m.Groups[2].Value)}" : "EPISODE";
        }
        return string.IsNullOrEmpty(Item.Year) ? "FILM" : $"FILM  ·  {Item.Year}";
    }

    /// <summary>"42 min left  ·  ends 21:14".</summary>
    public static string Left(long timeMs, long lengthMs)
    {
        if (lengthMs <= 0) return "";
        var left = TimeSpan.FromMilliseconds(Math.Max(0, lengthMs - timeMs));
        var mins = (int)Math.Ceiling(left.TotalMinutes);
        var l = mins >= 60 ? $"{mins / 60} h {mins % 60} min left" : $"{mins} min left";
        return $"{l}  ·  ends {DateTime.Now + left:HH:mm}";
    }
}
