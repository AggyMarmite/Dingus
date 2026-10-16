using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

public class XtreamClient
{
    static readonly Regex YearRx = new(@"(19|20)\d{2}", RegexOptions.Compiled);

    public string BaseUrl { get; }
    readonly string _user, _pass;

    public XtreamClient(PlaylistConfig p) : this(p.Server, p.Username, p.Password) { }

    public XtreamClient(string server, string user, string pass)
    {
        BaseUrl = NormalizeServer(server);
        _user = user?.Trim() ?? "";
        _pass = pass?.Trim() ?? "";
    }

    public static string NormalizeServer(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return s;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s;
        // People often paste ".../player_api.php" or a full get.php link – keep scheme://host:port only.
        return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : s.TrimEnd('/');
    }

    string Api(string action, string extra = "") =>
        $"{BaseUrl}/player_api.php?username={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_pass)}"
        + (action == null ? "" : "&action=" + action) + extra;

    public string EpgUrl => $"{BaseUrl}/xmltv.php?username={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_pass)}";
    public string LiveUrl(Channel c, bool hls) => $"{BaseUrl}/live/{_user}/{_pass}/{c.Id}.{(hls ? "m3u8" : "ts")}";
    /// <summary>
    /// Xtream catch-up ("timeshift") stream. <paramref name="startLocal"/> is the programme's real
    /// start in this PC's time; the panel expects it in the provider's own time zone.
    /// </summary>
    public string CatchupUrl(Channel c, DateTime startLocal, int durationMinutes, string serverTimeZone)
    {
        var t = startLocal;
        if (!string.IsNullOrWhiteSpace(serverTimeZone))
        {
            try { t = TimeZoneInfo.ConvertTime(startLocal, TimeZoneInfo.Local, TimeZoneInfo.FindSystemTimeZoneById(serverTimeZone)); }
            catch { /* unknown zone – fall back to local time */ }
        }
        var start = t.ToString("yyyy-MM-dd:HH-mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"{BaseUrl}/timeshift/{_user}/{_pass}/{Math.Max(1, durationMinutes)}/{start}/{c.Id}.ts";
    }

    public string MovieUrl(Movie m) => $"{BaseUrl}/movie/{_user}/{_pass}/{m.Id}.{m.Ext ?? "mp4"}";
    public string EpisodeUrl(Episode e) => $"{BaseUrl}/series/{_user}/{_pass}/{e.Id}.{e.Ext ?? "mp4"}";

    static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)resp.StatusCode;
        if (code == 401 || code == 403)
            throw new InvalidOperationException($"The server rejected the login (HTTP {code}). Check your username and password.");
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(s, new JsonDocumentOptions { AllowTrailingCommas = true }, ct);
    }

    /// <summary>
    /// Downloads a list (channels, films, shows) and maps it item by item as it streams in, so a
    /// 100 MB film list never has to sit in memory all at once.
    /// </summary>
    async Task<List<T>> GetListAsync<T>(string action, Func<JsonElement, T> map, CancellationToken ct, string extra = "") where T : class
    {
        var list = new List<T>();
        using var resp = await Http.Client.GetAsync(Api(action, extra), HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)resp.StatusCode;
        if (code == 401 || code == 403)
            throw new InvalidOperationException($"The server rejected the login (HTTP {code}). Check your username and password.");
        resp.EnsureSuccessStatusCode();
        await using var raw = await resp.Content.ReadAsStreamAsync(ct);

        // Peek at the first real character: '[' = a normal list we can stream; anything else
        // (an object keyed by id, which a few panels send) is read the ordinary way.
        var head = new MemoryStream();
        int first = -1;
        var one = new byte[1];
        while (await raw.ReadAsync(one, ct) == 1)
        {
            byte c = one[0];
            head.WriteByte(c);
            bool skip = c == 32 || c == 9 || c == 13 || c == 10 || c == 0xEF || c == 0xBB || c == 0xBF; // whitespace / BOM
            if (skip) continue;
            first = c;
            break;
        }
        if (first < 0) return list; // empty body = nothing in this section
        head.Position = 0;
        using var stream = new PrefixedStream(head, raw);

        try
        {
            if (first == '[')
            {
                await foreach (var e in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, cancellationToken: ct))
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var item = map(e);
                    if (item != null) list.Add(item);
                }
            }
            else
            {
                using var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = true }, ct);
                foreach (var e in doc.RootElement.Items())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var item = map(e);
                    if (item != null) list.Add(item);
                }
            }
        }
        catch (JsonException ex) when (list.Count == 0) { Log.Error(ex); } // not JSON = nothing in this section
        list.TrimExcess();
        return list;
    }

    /// <summary>Reads a few already-consumed bytes, then the rest of the network stream.</summary>
    sealed class PrefixedStream : Stream
    {
        readonly Stream _prefix, _rest;
        public PrefixedStream(Stream prefix, Stream rest) { _prefix = prefix; _rest = rest; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _prefix.Read(buffer, offset, count);
            return n > 0 ? n : _rest.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = _prefix.Read(buffer.Span);
            return n > 0 ? n : await _rest.ReadAsync(buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    public async Task<AccountInfo> LoginAsync(CancellationToken ct = default)
    {
        JsonDocument doc;
        try { doc = await GetJsonAsync(Api(null), ct); }
        catch (JsonException) { throw new InvalidOperationException("The server didn't return a valid response – check the server address."); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("user_info", out var ui) || ui.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Login failed – check the server address, username and password.");
            if (ui.Str("auth") == "0")
                throw new InvalidOperationException("Login failed – check your username and password.");

            var acc = new AccountInfo
            {
                Status = ui.NonEmpty("status") ?? "Unknown",
                ActiveConnections = ui.Int("active_cons"),
                MaxConnections = ui.Int("max_connections"),
                Trial = ui.Str("is_trial") == "1",
            };
            var exp = ui.Long("exp_date");
            if (exp > 0) acc.ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
            if (root.TryGetProperty("server_info", out var si) && si.ValueKind == JsonValueKind.Object)
                acc.ServerTimeZone = si.NonEmpty("timezone");
            return acc;
        }
    }

    public Task<List<Category>> GetCategoriesAsync(string action, CancellationToken ct = default) =>
        GetListAsync(action, e => new Category
        {
            Id = e.Str("category_id") ?? "",
            Name = e.NonEmpty("category_name") ?? "Unnamed",
        }, ct);

    public Task<List<Channel>> GetLiveStreamsAsync(CancellationToken ct = default) =>
        GetListAsync("get_live_streams", e =>
        {
            var id = e.Str("stream_id");
            if (string.IsNullOrEmpty(id)) return null;
            return new Channel
            {
                Id = id,
                Num = e.Int("num"),
                Name = e.NonEmpty("name") ?? "Channel " + id,
                Logo = e.NonEmpty("stream_icon"),
                EpgId = e.NonEmpty("epg_channel_id"),
                CategoryId = e.Str("category_id") ?? "",
                HasArchive = e.Str("tv_archive") == "1",
                ArchiveDays = e.Int("tv_archive_duration"),
            };
        }, ct);

    public Task<List<Movie>> GetMoviesAsync(CancellationToken ct = default) =>
        GetListAsync("get_vod_streams", e =>
        {
            var id = e.Str("stream_id");
            if (string.IsNullOrEmpty(id)) return null;
            var name = e.NonEmpty("name") ?? "Untitled";
            return new Movie
            {
                Id = id,
                Name = name,
                Poster = e.NonEmpty("stream_icon"),
                CategoryId = e.Str("category_id") ?? "",
                Rating = e.Dbl("rating"),
                Year = YearOf(e.Str("year")) ?? YearOf(name),
                Ext = e.NonEmpty("container_extension") ?? "mp4",
                Added = e.Long("added"),
            };
        }, ct);

    public Task<List<Series>> GetSeriesAsync(CancellationToken ct = default) =>
        GetListAsync("get_series", e =>
        {
            var id = e.Str("series_id");
            if (string.IsNullOrEmpty(id)) return null;
            return new Series
            {
                Id = id,
                Name = e.NonEmpty("name") ?? "Untitled",
                Cover = e.NonEmpty("cover"),
                CategoryId = e.Str("category_id") ?? "",
                Genre = e.NonEmpty("genre"),
                Rating = e.Dbl("rating"),
                Year = YearOf(e.Str("releaseDate")) ?? YearOf(e.Str("year")),
                Added = e.Long("last_modified"),
            };
        }, ct);

    public async Task<MediaDetails> GetVodInfoAsync(string id, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_vod_info", "&vod_id=" + Uri.EscapeDataString(id)), ct);
        var d = new MediaDetails();
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            d.Title = info.NonEmpty("name");
            d.Plot = info.NonEmpty("plot") ?? info.NonEmpty("description");
            d.Cast = info.NonEmpty("cast") ?? info.NonEmpty("actors");
            d.Director = info.NonEmpty("director");
            d.Genre = info.NonEmpty("genre");
            d.Year = YearOf(info.Str("releasedate")) ?? YearOf(info.Str("release_date"));
            d.Duration = info.NonEmpty("duration");
            d.Rating = info.NonEmpty("rating");
            d.Poster = info.NonEmpty("movie_image") ?? info.NonEmpty("cover_big");
            d.Backdrop = info.FirstString("backdrop_path");
        }
        return d;
    }

    public async Task<MediaDetails> GetSeriesInfoAsync(string seriesId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(Api("get_series_info", "&series_id=" + Uri.EscapeDataString(seriesId)), ct);
        var d = new MediaDetails();
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return d;

        if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            d.Title = info.NonEmpty("name");
            d.Plot = info.NonEmpty("plot");
            d.Cast = info.NonEmpty("cast");
            d.Director = info.NonEmpty("director");
            d.Genre = info.NonEmpty("genre");
            d.Year = YearOf(info.Str("releaseDate")) ?? YearOf(info.Str("release_date"));
            d.Rating = info.NonEmpty("rating");
            d.Poster = info.NonEmpty("cover");
            d.Backdrop = info.FirstString("backdrop_path");
        }

        if (root.TryGetProperty("episodes", out var eps))
        {
            if (eps.ValueKind == JsonValueKind.Object)
            {
                foreach (var season in eps.EnumerateObject())
                {
                    int.TryParse(season.Name, out var sn);
                    foreach (var e in season.Value.Items()) AddEpisode(d, e, sn);
                }
            }
            else if (eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in eps.EnumerateArray())
                {
                    if (g.ValueKind == JsonValueKind.Array) foreach (var e in g.EnumerateArray()) AddEpisode(d, e, 0);
                    else AddEpisode(d, g, 0);
                }
            }
        }

        d.Episodes = d.Episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
        return d;
    }

    static void AddEpisode(MediaDetails d, JsonElement e, int seasonHint)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        var id = e.Str("id");
        if (string.IsNullOrEmpty(id)) return;
        int season = e.Int("season");
        var ep = new Episode
        {
            Id = id,
            Title = e.NonEmpty("title") ?? "Episode",
            Season = season > 0 ? season : seasonHint,
            Number = e.Int("episode_num"),
            Ext = e.NonEmpty("container_extension") ?? "mp4",
        };
        if (e.TryGetProperty("info", out var ei) && ei.ValueKind == JsonValueKind.Object)
        {
            ep.Plot = ei.NonEmpty("plot");
            ep.Image = ei.NonEmpty("movie_image");
            ep.Duration = ei.NonEmpty("duration");
        }
        d.Episodes.Add(ep);
    }

    static string YearOf(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = YearRx.Match(s);
        return m.Success ? m.Value : null;
    }
}
