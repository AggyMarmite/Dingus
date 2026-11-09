using System.Net;
using System.Net.Sockets;

namespace Dingus.Services;

/// <summary>
/// A small web server on this computer for the self-tests (both apps): serves a video file the way a provider would
/// (whole, or the part asked for). Never used outside the self-test.
/// </summary>
public sealed class TestServer : IDisposable
{
    readonly HttpListener _http = new();
    readonly string _video;
    /// <summary>"http://127.0.0.1:port/"</summary>
    public string Base { get; }
    public string VideoUrl => Base + "video.mp4";

    public TestServer(string videoPath)
    {
        _video = videoPath;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Base = $"http://127.0.0.1:{port}/";
        _http.Prefixes.Add(Base);
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
                // Like a real file server: whole file, or the part asked for.
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
                ctx.Response.ContentLength64 = Math.Max(0, to - from + 1);
                if (ctx.Request.HttpMethod != "HEAD" && to >= from) ctx.Response.OutputStream.Write(bytes, (int)from, (int)(to - from + 1));
                ctx.Response.Close();
                return;
            }
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
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
        foreach (var old in DownloadService.All.Where(d => d.ResumeKey is "m:selftest-1" or "e:selftest-2").ToList()) DownloadService.Remove(old);
        DownloadService.SetFolder(Path.Combine(dir, "out"));
        using var server = new TestServer(src);
        var video = server.VideoUrl;
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
                Kind = "movie", Title = "The Test Pattern", Subtitle = "2021", Url = video, ResumeKey = "m:selftest-1", ItemId = "selftest-1", Ext = ".mp4",
            }, null);
            bool ok = await WaitDone(film);
            bool same = ok && File.ReadAllBytes(film.FilePath).AsSpan().SequenceEqual(bytes);
            note($"downloads: film {film.Status} → {Path.GetRelativePath(dir, film.FilePath ?? "")} · {film.Bytes} bytes · matches={same}{(film.Error != null ? " · " + film.Error : "")}");
            if (!same) error(new Exception("Downloading a film didn't give a complete copy of the file: " + (film.Error ?? film.Status.ToString())));
            if (DownloadService.LocalUrl("m:selftest-1") is not { } local || !local.StartsWith("file:"))
                error(new Exception("A downloaded film wouldn't play from its file."));

            // 2. An episode that had stopped half way: it carries on from there (and goes in the show's season folder).
            var ep = DownloadService.Add(new Models.VodItem
            {
                Kind = "episode", Title = "Testing Times", Subtitle = "S01E02  ·  Episode 2", Url = video, ResumeKey = "e:selftest-2", ItemId = "selftest-2", Ext = "mp4",
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
                Kind = "episode", Title = "Testing Times", Subtitle = "S01E02  ·  Episode 2", Url = video, ResumeKey = "e:selftest-2", ItemId = "selftest-2", Ext = "mp4",
            }, null, "Testing Times", 1, 2, paused: true);
            Directory.CreateDirectory(Path.GetDirectoryName(whole.PartPath)!);
            File.WriteAllBytes(whole.PartPath, bytes);
            DownloadService.Resume(whole);
            ok = await WaitDone(whole);
            same = ok && File.ReadAllBytes(whole.FilePath).AsSpan().SequenceEqual(bytes);
            note($"downloads: fully arrived but unfinished → {whole.Status} · matches={same}{(whole.Error != null ? " · " + whole.Error : "")}");
            if (!same) error(new Exception("A download that had all arrived didn't finish: " + (whole.Error ?? whole.Status.ToString())));

            long free = FolderCheck.FreeSpace(dir);
            note($"downloads: free space where they're saved {(free >= 0 ? Titles.Size(free) : "unknown")}");
            if (free <= 0) error(new Exception("Couldn't tell how much space is free where downloads are saved."));

            // 3. A link that's gone: it says so rather than trying for ever.
            var gone = DownloadService.Add(new Models.VodItem
            {
                Kind = "movie", Title = "Gone", Url = server.Base + "nothing-here.mp4", ResumeKey = "m:selftest-gone", ItemId = "gone",
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
        foreach (var d in DownloadService.All.Where(d => d.ResumeKey is "m:selftest-1" or "e:selftest-2").ToList()) DownloadService.Remove(d);
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "dingus-selftest-downloads"), true); } catch { }
    }
}
