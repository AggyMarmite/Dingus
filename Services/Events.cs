using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dingus.Models;
using Dingus.Views;

namespace Dingus.Services;

/// <summary>A game in the TV guide – one match, however many channels show it.</summary>
public sealed class EventGame
{
    /// <summary>"Arsenal v Chelsea" (from the programme's title, or its description when the title is just "Live Football").</summary>
    public string Title { get; init; }
    /// <summary>The programme's own title when it isn't the match itself, e.g. "Live Premier League Football".</summary>
    public string Subtitle { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    /// <summary>Every channel showing it – your favourites first.</summary>
    public List<(GuideRow Row, Programme Programme)> Showings { get; } = new();
    /// <summary>The followed team it was found for (null for a search).</summary>
    public string Team { get; set; }
    public GuideRow Row => Showings[0].Row;
    public Programme Programme => Showings[0].Programme;
    public bool IsLiveAt(DateTime now) => Start <= now && End > now;
    internal string Key;
}

/// <summary>
/// Finding games in your TV guide: searching for one ("Arsenal v Chelsea") and the games of teams you follow.
/// Everything comes from your playlist's own guide and channel names – no outside service – so it reaches as far
/// ahead as your guide does. Shared by the Windows and Linux apps; safe to run off the window's thread.
/// </summary>
public static class Events
{
    /// <summary>How far ahead to look (guides rarely go further).</summary>
    public const int Days = 14;

    // ------------------------------------------------------------------ names

    /// <summary>Lower case, no accents or punctuation, no "FC"/"AFC", padded with spaces for whole-word matching.</summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return " ";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length + 2);
        sb.Append(' ');
        bool space = true;
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
            else if (ch == '&') { if (!space) sb.Append(' '); sb.Append("and "); space = true; }
            else if (!space) { sb.Append(' '); space = true; }
        }
        if (!space) sb.Append(' ');
        var r = sb.ToString();
        foreach (var w in new[] { " fc ", " afc ", " cf ", " sc " }) r = r.Replace(w, " ");
        return r.Length == 0 ? " " : r;
    }

    /// <summary>Other names a team goes by in guides (each line is one team).</summary>
    static readonly string[][] AliasGroups =
    {
        new[] { "manchester united", "man utd", "man united", "man u" },
        new[] { "manchester city", "man city" },
        new[] { "tottenham", "tottenham hotspur", "spurs" },
        new[] { "wolverhampton", "wolverhampton wanderers", "wolves" },
        new[] { "nottingham forest", "nottm forest", "notts forest", "forest" },
        new[] { "brighton", "brighton and hove albion", "brighton hove albion" },
        new[] { "west ham", "west ham united" },
        new[] { "newcastle", "newcastle united", "newcastle utd" },
        new[] { "leeds", "leeds united", "leeds utd" },
        new[] { "sheffield united", "sheffield utd", "sheff utd" },
        new[] { "sheffield wednesday", "sheff wed" },
        new[] { "west brom", "west bromwich albion", "west bromwich" },
        new[] { "qpr", "queens park rangers" },
        new[] { "aston villa", "villa" },
        new[] { "crystal palace", "palace" },
        new[] { "afc bournemouth", "bournemouth" },
        new[] { "inter", "inter milan", "internazionale" },
        new[] { "ac milan", "milan" },
        new[] { "psg", "paris saint germain", "paris sg", "paris st germain" },
        new[] { "barcelona", "barca" },
        new[] { "atletico madrid", "atletico", "atl madrid" },
        new[] { "bayern", "bayern munich", "bayern munchen" },
        new[] { "borussia dortmund", "dortmund", "bvb" },
        new[] { "rb leipzig", "leipzig" },
        new[] { "usa", "united states", "usmnt" },
    };

    /// <summary>The team's name and the other names it goes by, normalised.</summary>
    public static List<string> Variants(string team)
    {
        var n = Norm(team);
        var res = new List<string> { n };
        foreach (var g in AliasGroups)
        {
            var ng = g.Select(Norm).ToList();
            if (ng.Contains(n)) foreach (var x in ng) if (!res.Contains(x)) res.Add(x);
        }
        // Common short forms, either way round: "Utd" / "United", "St" / "Saint".
        foreach (var (a, b) in new[] { (" utd ", " united "), (" st ", " saint ") })
            foreach (var v in res.ToList())
            {
                if (v.Contains(a)) { var w = v.Replace(a, b); if (!res.Contains(w)) res.Add(w); }
                if (v.Contains(b)) { var w = v.Replace(b, a); if (!res.Contains(w)) res.Add(w); }
            }
        return res;
    }

    /// <summary>What to look for: one side ("Arsenal") or two ("Arsenal v Chelsea"), each with its other names.</summary>
    public sealed record Query(List<List<string>> Sides)
    {
        public bool IsEmpty => Sides.Count == 0;
    }

    static readonly Regex SideSplit = new(@"\s+(?:v|vs|versus|-|x|@)\.?\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Query Parse(string text)
    {
        var sides = new List<List<string>>();
        foreach (var part in SideSplit.Split((text ?? "").Trim()).Take(2))
            if (Norm(part).Trim().Length >= 2) sides.Add(Variants(part));
        return new Query(sides);
    }

    static Query TeamQuery(string team) => new(new List<List<string>> { Variants(team) });

    /// <summary>True when every side of the query (by any of its names) is in the normalised text.</summary>
    public static bool Matches(string normText, Query q)
    {
        if (q.IsEmpty) return false;
        foreach (var side in q.Sides)
        {
            bool any = false;
            foreach (var v in side) if (normText.Contains(v, StringComparison.Ordinal)) { any = true; break; }
            if (!any) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ "Team v Team"

    // A team name: up to five capitalised words (or numbers), with "&", "and", "de"… allowed between them –
    // so "the match between Real Madrid v Barcelona at the Bernabéu" gives "Real Madrid" and "Barcelona".
    const string Word = @"(?:[\p{Lu}\p{N}][\p{L}\p{N}.'’-]*)";
    const string Joiner = @"(?:&|and|de|del|da|di|la|le|of|upon|y)";
    const string Team = Word + @"(?:\s+(?:" + Joiner + @"\s+)?" + Word + "){0,4}";
    static readonly Regex Matchup = new(@"(?<a>" + Team + @")\s+(?:[vV][sS]?|versus|VERSUS)\.?\s+(?<b>" + Team + ")", RegexOptions.Compiled);
    static readonly Regex LivePrefix = new(@"^\s*(?:live|en vivo|en directo|direct|ao vivo)\b\s*[:\-–]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Arsenal v Chelsea" out of a title or description, or null.</summary>
    public static string FindMatchup(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Matchup.Match(LivePrefix.Replace(text, ""));
        if (!m.Success) return null;
        var a = m.Groups["a"].Value.Trim(' ', '.', '-');
        var b = m.Groups["b"].Value.Trim(' ', '.', '-');
        // A leading "Premier League: " or "EPL 01 | " belongs to the competition, not the team.
        int cut = Math.Max(a.LastIndexOf(':'), a.LastIndexOf('|'));
        if (cut >= 0) a = a[(cut + 1)..].Trim();
        if (a.Length < 2 || b.Length < 2) return null;
        return $"{a} v {b}";
    }

    static bool LooksLikeAGame(Programme p, GuideRow r) =>
        Genres.IsSport(p, r.DisplayName) || FindMatchup(p.Title) != null;

    // ------------------------------------------------------------------ finding games

    /// <summary>Normalised descriptions, kept while the guide stays the same (reading them is the slow part).</summary>
    static readonly Dictionary<Programme, string> DescCache = new(ReferenceEqualityComparer.Instance);
    static EpgData _cacheFor;
    static readonly object CacheLock = new();

    /// <summary>Lets go of the remembered descriptions (the Events page closed, or a background check finished).</summary>
    public static void ReleaseMemory()
    {
        lock (CacheLock) { DescCache.Clear(); DescCache.TrimExcess(); _cacheFor = null; }
    }

    static string NormDesc(Programme p, EpgData epg)
    {
        lock (CacheLock)
        {
            if (!ReferenceEquals(_cacheFor, epg)) { DescCache.Clear(); _cacheFor = epg; }
            if (DescCache.TryGetValue(p, out var hit)) return hit;
        }
        string d;
        try { d = p.Desc; } catch { d = null; }
        if (d != null && d.Length > 200) d = d[..200]; // the teams are near the start
        var n = d == null ? null : Norm(d);
        var mu = FindMatchup(d);
        var entry = n == null ? "" : n + "\u0001" + (mu ?? "");
        lock (CacheLock) if (DescCache.Count < 20_000) DescCache[p] = entry; // a few MB at most
        return entry;
    }

    /// <summary>
    /// Games in the guide that match <paramref name="q"/>: from now (including what's on) to <see cref="Days"/> ahead,
    /// sport only, the same match on several channels counted once. Soonest first.
    /// </summary>
    public static List<EventGame> Find(IReadOnlyList<GuideRow> rows, Func<GuideRow, string> epgIdOf, EpgData epg, Query q, DateTime now,
        CancellationToken ct = default)
    {
        var games = new List<EventGame>();
        if (q.IsEmpty || rows == null || epg == null) return games;
        var until = now.AddDays(Days);
        int descReads = 0;
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            var l = epg.Get(epgIdOf(r));
            if (l == null) continue;
            for (int i = EpgData.FirstEndingAfter(l, now); i < l.Count; i++)
            {
                var p = l[i];
                if (p.Start > until) break;
                string title = null, subtitle = null;
                var nt = Norm(p.Title);
                var mu = FindMatchup(p.Title);
                if (Matches(nt, q))
                {
                    if (!LooksLikeAGame(p, r)) continue;
                    title = mu ?? LivePrefix.Replace(p.Title, "");
                    if (mu != null && !string.Equals(Norm(mu), Norm(LivePrefix.Replace(p.Title, "")), StringComparison.Ordinal)) subtitle = p.Title;
                }
                else if (mu == null && Genres.IsSport(p, r.DisplayName) && descReads < 40_000)
                {
                    // "Live Premier League Football" – the teams are in the description.
                    descReads++;
                    var e = NormDesc(p, epg);
                    if (e.Length == 0) continue;
                    int sep = e.IndexOf('\u0001');
                    if (!Matches(e[..sep], q)) continue;
                    var dmu = e[(sep + 1)..];
                    title = dmu.Length > 0 ? dmu : p.Title;
                    subtitle = dmu.Length > 0 ? p.Title : null;
                }
                else continue;
                Add(games, r, p, title, subtitle);
            }
        }
        foreach (var g in games)
            g.Showings.Sort((a, b) => (b.Row.IsFavourite ? 1 : 0).CompareTo(a.Row.IsFavourite ? 1 : 0));
        games.Sort((a, b) => a.Start.CompareTo(b.Start));
        return games;
    }

    /// <summary>Adds a showing to the game it belongs to (same teams, starting within half an hour), or a new game.</summary>
    static void Add(List<EventGame> games, GuideRow r, Programme p, string title, string subtitle)
    {
        var key = Norm(title);
        foreach (var g in games)
            if (g.Key == key && Math.Abs((g.Start - p.Start).TotalMinutes) <= 30)
            {
                if (!g.Showings.Any(s => s.Row == r)) g.Showings.Add((r, p));
                return;
            }
        var game = new EventGame { Title = title.Trim(), Subtitle = subtitle, Start = p.Start, End = p.End, Key = key };
        game.Showings.Add((r, p));
        games.Add(game);
    }

    /// <summary>Channels whose own name is the game (providers' "event" channels, e.g. "EPL 03: Arsenal vs Chelsea 17:30").</summary>
    public static List<GuideRow> EventChannels(IReadOnlyList<GuideRow> rows, Query q)
    {
        var res = new List<GuideRow>();
        if (q.IsEmpty || rows == null) return res;
        foreach (var r in rows)
            if (Matches(Norm(r.DisplayName), q)) res.Add(r);
        return res;
    }

    /// <summary>Upcoming and live games for the teams you follow, soonest first.</summary>
    public static List<EventGame> ForTeams(IReadOnlyList<GuideRow> rows, Func<GuideRow, string> epgIdOf, EpgData epg, IEnumerable<string> teams,
        DateTime now, CancellationToken ct = default)
    {
        var all = new List<EventGame>();
        foreach (var t in teams ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            foreach (var g in Find(rows, epgIdOf, epg, TeamQuery(t), now, ct))
            {
                // Two followed teams playing each other: one game.
                var dup = all.FirstOrDefault(x => x.Key == g.Key && Math.Abs((x.Start - g.Start).TotalMinutes) <= 30);
                if (dup != null) { dup.Team = dup.Team + " & " + t; continue; }
                g.Team = t;
                all.Add(g);
            }
        }
        all.Sort((a, b) => a.Start.CompareTo(b.Start));
        return all;
    }

    // ------------------------------------------------------------------ followed teams

    /// <summary>Follows a team (returns false if it's already followed or the name is blank).</summary>
    public static bool Follow(string team)
    {
        team = (team ?? "").Trim();
        if (Norm(team).Trim().Length < 2) return false;
        var s = SettingsStore.Current;
        s.FollowedTeams ??= new();
        if (s.FollowedTeams.Any(t => Norm(t) == Norm(team))) return false;
        s.FollowedTeams.Add(team);
        SettingsStore.Save();
        return true;
    }

    public static void Unfollow(string team)
    {
        var s = SettingsStore.Current;
        s.FollowedTeams?.RemoveAll(t => Norm(t) == Norm(team));
        SettingsStore.Save();
    }

    /// <summary>True when the game already has a reminder on any of its channels.</summary>
    public static bool HasReminder(EventGame g) => g.Showings.Any(s => ReminderService.Has(s.Row.Channel.Id, s.Programme.Start));

    /// <summary>Sets a reminder for the game (on its best channel), or clears it from every channel. Returns true if it's now set.</summary>
    public static bool ToggleReminder(EventGame g)
    {
        var set = g.Showings.Where(s => ReminderService.Has(s.Row.Channel.Id, s.Programme.Start)).ToList();
        if (set.Count > 0)
        {
            foreach (var s in set) ReminderService.Toggle(s.Row.Channel.Id, s.Row.DisplayName, s.Programme);
            return false;
        }
        return ReminderService.Toggle(g.Row.Channel.Id, g.Row.DisplayName, g.Programme);
    }

    /// <summary>
    /// "Remind me before every game": sets reminders for your teams' upcoming games (worked out off the window's
    /// thread with <see cref="ForTeams"/>). Each game is reminded once – if you clear a reminder, it isn't put back.
    /// Call on the window's thread.
    /// </summary>
    public static int SyncTeamReminders(List<EventGame> teamGames)
    {
        var s = SettingsStore.Current;
        if (!s.RemindTeamGames || s.FollowedTeams == null || s.FollowedTeams.Count == 0 || teamGames == null) return 0;
        var now = DateTime.Now;
        s.TeamRemindersSet ??= new();
        int added = 0;
        bool changed = s.TeamRemindersSet.RemoveAll(k => k.Length < 12 || !DateTime.TryParseExact(k[..12], "yyyyMMddHHmm", null, DateTimeStyles.None, out var t) || t < now.AddDays(-1)) > 0;
        foreach (var g in teamGames)
        {
            if (g.Start <= now.AddMinutes(1)) continue;
            var key = g.Start.ToString("yyyyMMddHHmm") + "|" + g.Key.Trim();
            if (s.TeamRemindersSet.Contains(key)) continue;
            s.TeamRemindersSet.Add(key);
            changed = true;
            if (HasReminder(g)) continue;
            ReminderService.Toggle(g.Row.Channel.Id, g.Row.DisplayName, g.Programme);
            added++;
        }
        if (changed) SettingsStore.Save();
        return added;
    }

    // ------------------------------------------------------------------ for the page

    public static string Day(DateTime t)
    {
        var d = t.Date;
        if (d == DateTime.Today) return "Today";
        if (d == DateTime.Today.AddDays(1)) return "Tomorrow";
        return t.ToString("ddd d MMM");
    }

    static readonly string[] Palette = { "#2563EB", "#DC2626", "#16A34A", "#9333EA", "#EA580C", "#0891B2", "#DB2777", "#CA8A04", "#4F46E5", "#0D9488" };

    static string ColorFor(string name)
    {
        int h = 17;
        foreach (var ch in Norm(name)) h = h * 31 + ch;
        return Palette[(h & 0x7fffffff) % Palette.Length];
    }

    static string Initials(string name)
    {
        var words = Norm(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "and" or "of" or "de" or "live")).ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1) return words[0].Length >= 3 ? words[0][..3].ToUpperInvariant() : words[0].ToUpperInvariant();
        return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
    }

    /// <summary>The two teams of a game title ("Arsenal v Chelsea"), or the title and null when it isn't a match.</summary>
    public static (string Home, string Away) Sides(string title)
    {
        var mu = FindMatchup(title);
        if (mu != null)
        {
            int i = mu.IndexOf(" v ", StringComparison.Ordinal);
            if (i > 0) return (mu[..i].Trim(), mu[(i + 3)..].Trim());
        }
        return (LivePrefix.Replace(title ?? "", "").Trim(), null);
    }

    /// <summary>The page's cards. Reads the guide's artwork from disk, so call it off the window's thread.</summary>
    public static List<EventItem> Items(List<EventGame> games, DateTime now) => games.Select(g =>
    {
        bool live = g.IsLiveAt(now);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(g.Subtitle)) parts.Add(g.Subtitle.Trim());
        parts.Add(g.Row.DisplayName);
        int more = g.Showings.Count - 1;
        if (more > 0) parts.Add(more == 1 ? "+1 more channel" : $"+{more} more channels");
        var (home, away) = Sides(g.Title);
        var hc = TeamCrests.Find(home);
        var ac = away == null ? null : TeamCrests.Find(away);
        string art = null;
        if (hc == null || ac == null)
            foreach (var (_, p) in g.Showings.Take(4))
            {
                try { art = p.ImageUrl; } catch { }
                if (!string.IsNullOrEmpty(art)) break;
                art = null;
            }
        return new EventItem
        {
            Game = g,
            IsLive = live,
            TimeText = live ? "LIVE" : g.Start.ToString("HH:mm"),
            DayText = live ? $"until {g.End:HH:mm}" : Day(g.Start),
            When = live ? $"On now · until {g.End:HH:mm}" : $"{Day(g.Start)} {g.Start:HH:mm}",
            Detail = string.Join("  ·  ", parts),
            Where = more > 0 ? $"{g.Row.DisplayName}  +{more} more" : g.Row.DisplayName,
            HasReminder = !live && HasReminder(g),
            Home = home,
            Away = away,
            HomeCrest = hc?.Logo,
            AwayCrest = ac?.Logo,
            Art = art,
            HomeInitials = Initials(home),
            AwayInitials = away == null ? null : Initials(away),
            HomeColor = hc?.Color ?? ColorFor(home),
            AwayColor = away == null ? null : ac?.Color ?? ColorFor(away),
        };
    }).ToList();

    /// <summary>Every channel showing the game, for the list you see when you click it.</summary>
    public static List<EventSourceItem> Sources(EventGame g, DateTime now) => g.Showings.Select(s =>
    {
        var p = s.Programme;
        bool live = p.Start <= now && p.End > now;
        return new EventSourceItem
        {
            Row = s.Row,
            Programme = p,
            IsLive = live,
            Line = $"{p.Title}  ·  {p.Start:HH:mm}–{p.End:HH:mm}{(live ? "  ·  on now" : "")}",
            HasReminder = !live && ReminderService.Has(s.Row.Channel.Id, p.Start),
            IsRecording = RecordingService.IsScheduled(s.Row.Channel.Id, p.Start),
        };
    }).ToList();
}
