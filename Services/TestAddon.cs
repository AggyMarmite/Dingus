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

    /// <summary>
    /// The self-test's downloads check (both apps): downloads a film from a stand-in server into a scratch folder, picks up a
    /// half-finished one where it stopped, checks both files match the original byte for byte and that the player would use
    /// the file, then removes them. Returns the scratch folder's download (still there) for the page's screenshot.
    /// </summary>
    public static async Task CheckDownloadsAsync(Action<string> note, Action<Exception> error)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dingus-selftest-downloads");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "source.mp4");
        var bytes = new byte[3 * 1024 * 1024 + 12345];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(src, bytes);
        var oldFolder = SettingsStore.Current.DownloadsFolder;
        foreach (var old in DownloadService.All.Where(d => d.ResumeKey is "st:tt9000001" or "st:tt9000010:1:2").ToList()) DownloadService.Remove(old);
        DownloadService.SetFolder(Path.Combine(dir, "out"));
        using var server = new TestAddon(src);
        var video = server.Url.Replace("/manifest.json", "/video.mp4");
        try
        {
            static async Task<bool> WaitDone(Download d)
            {
                for (int i = 0; i < 120 && d.Status is not (DownloadStatus.Done or DownloadStatus.Failed); i++) await Task.Delay(250);
                return d.Status == DownloadStatus.Done;
            }

            // 1. A film, start to finish.
            var film = DownloadService.Add(new Models.VodItem
            {
                Kind = "stremio", Title = "The Test Pattern", Subtitle = "2021", Url = video, ResumeKey = "st:tt9000001", ItemId = "tt9000001", Ext = ".mp4",
            }, null);
            bool ok = await WaitDone(film);
            bool same = ok && File.ReadAllBytes(film.FilePath).AsSpan().SequenceEqual(bytes);
            note($"downloads: film {film.Status} → {Path.GetRelativePath(dir, film.FilePath ?? "")} · {film.Bytes} bytes · matches={same}{(film.Error != null ? " · " + film.Error : "")}");
            if (!same) error(new Exception("Downloading a film didn't give a complete copy of the file: " + (film.Error ?? film.Status.ToString())));
            if (DownloadService.LocalUrl("st:tt9000001") is not { } local || !local.StartsWith("file:"))
                error(new Exception("A downloaded film wouldn't play from its file."));

            // 2. An episode that had stopped half way: it carries on from there (and goes in the show's season folder).
            var ep = DownloadService.Add(new Models.VodItem
            {
                Kind = "stremio", Title = "Testing Times", Subtitle = "S01E02  ·  Episode 2", Url = video, ResumeKey = "st:tt9000010:1:2", ItemId = "tt9000010:1:2", Ext = "mp4",
            }, null, "Testing Times", 1, 2, paused: true);
            Directory.CreateDirectory(Path.GetDirectoryName(ep.PartPath)!);
            File.WriteAllBytes(ep.PartPath, bytes.AsSpan(0, bytes.Length / 2).ToArray());
            DownloadService.Resume(ep);
            ok = await WaitDone(ep);
            same = ok && File.ReadAllBytes(ep.FilePath).AsSpan().SequenceEqual(bytes);
            var rel = Path.GetRelativePath(dir, ep.FilePath ?? "");
            note($"downloads: half-done episode {ep.Status} → {rel} · matches={same}{(ep.Error != null ? " · " + ep.Error : "")}");
            if (!same) error(new Exception("A half-finished download didn't carry on correctly: " + (ep.Error ?? ep.Status.ToString())));
            if (!rel.Replace('\\', '/').Contains("Testing Times/Season 1/")) error(new Exception("An episode download didn't go in its show's season folder: " + rel));

            // 3. One that had all arrived but not been tidied away (Dingus closed at just the wrong moment): it finishes.
            DownloadService.Remove(ep);
            var whole = DownloadService.Add(new Models.VodItem
            {
                Kind = "stremio", Title = "Testing Times", Subtitle = "S01E02  ·  Episode 2", Url = video, ResumeKey = "st:tt9000010:1:2", ItemId = "tt9000010:1:2", Ext = "mp4",
            }, null, "Testing Times", 1, 2, paused: true);
            Directory.CreateDirectory(Path.GetDirectoryName(whole.PartPath)!);
            File.WriteAllBytes(whole.PartPath, bytes);
            DownloadService.Resume(whole);
            ok = await WaitDone(whole);
            same = ok && File.ReadAllBytes(whole.FilePath).AsSpan().SequenceEqual(bytes);
            note($"downloads: fully arrived but unfinished → {whole.Status} · matches={same}{(whole.Error != null ? " · " + whole.Error : "")}");
            if (!same) error(new Exception("A download that had all arrived didn't finish: " + (whole.Error ?? whole.Status.ToString())));

            long free = FolderCheck.FreeSpace(dir);
            note($"downloads: free space where they're saved {(free >= 0 ? Stremio.Size(free) : "unknown")}");
            if (free <= 0) error(new Exception("Couldn't tell how much space is free where downloads are saved."));

            // 3. A link that's gone: it says so rather than trying for ever.
            var gone = DownloadService.Add(new Models.VodItem
            {
                Kind = "stremio", Title = "Gone", Url = server.Url.Replace("/manifest.json", "/nothing-here.mp4"), ResumeKey = "st:selftest-gone", ItemId = "gone",
            }, null);
            await WaitDone(gone);
            note($"downloads: missing link → {gone.Status} · {gone.Error}");
            if (gone.Status != DownloadStatus.Failed) error(new Exception("A download from a link that's gone didn't say it failed."));
            DownloadService.Remove(gone);
        }
        catch (Exception ex) { error(ex); }
        finally
        {
            SettingsStore.Current.DownloadsFolder = oldFolder;
            SettingsStore.Save();
        }
    }

    /// <summary>Removes the self-test's downloads and their scratch folder.</summary>
    public static void CleanUpDownloads()
    {
        foreach (var d in DownloadService.All.Where(d => d.ResumeKey is "st:tt9000001" or "st:tt9000010:1:2").ToList()) DownloadService.Remove(d);
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "dingus-selftest-downloads"), true); } catch { }
    }
}
