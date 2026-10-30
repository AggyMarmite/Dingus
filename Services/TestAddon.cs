using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Dingus.Services;

/// <summary>
/// A stand-in Stremio add-on for the self-tests (both apps): a small web server on this computer offering two films,
/// a show with episodes, and sources of each kind – one plays the test video, one is a torrent, one opens a web page.
/// Never used outside the self-test.
/// </summary>
public sealed class TestAddon : IDisposable
{
    readonly HttpListener _http = new();
    readonly string _video;
    public string Url { get; }

    public TestAddon(string videoPath)
    {
        _video = videoPath;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Url = $"http://127.0.0.1:{port}/manifest.json";
        _http.Prefixes.Add($"http://127.0.0.1:{port}/");
        _http.Start();
        _ = Task.Run(Loop);
    }

    public void Dispose() { try { _http.Stop(); _http.Close(); } catch { } }

    async Task Loop()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    void Serve(HttpListenerContext ctx)
    {
        try
        {
            var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
            if (path == "/video.mp4" && _video != null && File.Exists(_video))
            {
                // Like a real file server: whole file, or the part asked for (players jump around in an mp4).
                var bytes = File.ReadAllBytes(_video);
                long from = 0, to = bytes.Length - 1;
                var range = ctx.Request.Headers["Range"];
                if (range != null && System.Text.RegularExpressions.Regex.Match(range, @"bytes=(\d*)-(\d*)") is { Success: true } m)
                {
                    if (m.Groups[1].Value.Length > 0) from = long.Parse(m.Groups[1].Value);
                    if (m.Groups[2].Value.Length > 0) to = Math.Min(to, long.Parse(m.Groups[2].Value));
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{bytes.Length}";
                }
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                ctx.Response.ContentType = "video/mp4";
                ctx.Response.ContentLength64 = to - from + 1;
                if (ctx.Request.HttpMethod != "HEAD") ctx.Response.OutputStream.Write(bytes, (int)from, (int)(to - from + 1));
                ctx.Response.Close();
                return;
            }
            object body = Answer(path, $"http://127.0.0.1:{ctx.Request.Url.Port}");
            if (body == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = json.Length;
            ctx.Response.OutputStream.Write(json);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    static readonly (string Id, string Name, string Year)[] Films =
    {
        ("tt9000001", "The Test Pattern", "2021"),
        ("tt9000002", "Signal and Noise", "2019"),
    };

    static object Answer(string path, string host)
    {
        if (path == "/manifest.json")
            return new
            {
                id = "dingus.selftest",
                version = "1.0.0",
                name = "Test add-on",
                description = "A stand-in add-on for Dingus's self-test",
                types = new[] { "movie", "series" },
                idPrefixes = new[] { "tt9" },
                resources = new object[] { "catalog", "meta", "stream" },
                catalogs = new object[]
                {
                    new { type = "movie", id = "test", name = "Test films", extra = new object[] { new { name = "search" }, new { name = "skip" } } },
                    new { type = "series", id = "test", name = "Test shows", extra = new object[] { new { name = "search" } } },
                },
            };
        if (path.StartsWith("/catalog/movie/test"))
        {
            var q = path.Contains("search=") ? path[(path.IndexOf("search=") + 7)..].Replace(".json", "") : null;
            return new
            {
                metas = Films.Where(f => q == null || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new { id = f.Id, type = "movie", name = f.Name, releaseInfo = f.Year, description = "A film made for testing." }),
            };
        }
        if (path.StartsWith("/catalog/series/test"))
        {
            var q = path.Contains("search=") ? path[(path.IndexOf("search=") + 7)..].Replace(".json", "") : null;
            var shows = new[] { new { id = "tt9000010", type = "series", name = "Testing Times", releaseInfo = "2020–" } };
            return new { metas = shows.Where(x => q == null || x.name.Contains(q, StringComparison.OrdinalIgnoreCase)) };
        }
        if (path.StartsWith("/meta/movie/"))
        {
            var id = path["/meta/movie/".Length..].Replace(".json", "");
            var f = Films.FirstOrDefault(x => x.Id == id);
            return f.Id == null ? null : new { meta = new { id = f.Id, type = "movie", name = f.Name, releaseInfo = f.Year, runtime = "94 min", genres = new[] { "Drama" }, description = "A film made for testing." } };
        }
        if (path.StartsWith("/meta/series/tt9000010"))
            return new
            {
                meta = new
                {
                    id = "tt9000010", type = "series", name = "Testing Times", releaseInfo = "2020–", description = "A show made for testing.",
                    videos = Enumerable.Range(1, 2).SelectMany(s => Enumerable.Range(1, 3).Select(e => new
                    {
                        id = $"tt9000010:{s}:{e}", title = $"Episode {e}", season = s, episode = e, released = $"2020-0{s}-0{e}T00:00:00.000Z",
                    })),
                },
            };
        if (path.StartsWith("/stream/"))
            return new
            {
                streams = new object[]
                {
                    new { name = "Test\n1080p", title = "The.Test.Pattern.1080p.mp4\n💾 1.2 GB", url = host + "/video.mp4" },
                    new { name = "Test\n4K", title = "The.Test.Pattern.2160p.mkv\n💾 14.8 GB 👤 52", infoHash = "0123456789abcdef0123456789abcdef01234567", fileIdx = 0 },
                    new { name = "Test", title = "Trailer", externalUrl = "https://example.com/trailer" },
                },
            };
        return null;
    }
}
