using System.Net;
using System.Net.Http;
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
            if (path.StartsWith("/live/") && path.EndsWith(".ts") && int.TryParse(path[6..^3], out var ch))
            {
                ServeLive(ctx, ch);
                return;
            }
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    // ------------------------------------------------------------------ a stand-in live provider (one connection allowed)

    const int LiveRate = 1330; // packets a second (about 2 Mbit/s)
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    int _liveOpen;
    /// <summary>Live connections refused because one was already open (the plan allows one).</summary>
    public int LiveRefused;
    /// <summary>The most live connections open at once.</summary>
    public int LivePeak;

    /// <summary>
    /// Like a live IPTV server on a one-connection plan: a second connection at the same time is refused; a new one gets a
    /// head start of the last 20 seconds straight away, then real time. Each packet carries its number, so the self-test can
    /// check that a tile's channel arrived complete and in order.
    /// </summary>
    void ServeLive(HttpListenerContext ctx, int ch)
    {
        if (Interlocked.Increment(ref _liveOpen) > 1)
        {
            Interlocked.Decrement(ref _liveOpen);
            Interlocked.Increment(ref LiveRefused);
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return;
        }
        LivePeak = Math.Max(LivePeak, _liveOpen);
        try
        {
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.SendChunked = true;
            var o = ctx.Response.OutputStream;
            long now() => (long)((_clock.Elapsed.TotalSeconds + 60) * LiveRate); // the channel has been on for a while
            long next = Math.Max(0, now() - 20 * LiveRate);
            var pkt = new byte[188];
            while (true)
            {
                long upTo = now();
                var batch = new MemoryStream();
                for (; next < upTo; next++) { MakePacket(pkt, ch, next); batch.Write(pkt); }
                if (batch.Length > 0) o.Write(batch.GetBuffer(), 0, (int)batch.Length);
                Thread.Sleep(40);
            }
        }
        catch { try { ctx.Response.Abort(); } catch { } }
        finally { Thread.Sleep(100); Interlocked.Decrement(ref _liveOpen); } // providers take a moment to free the slot
    }

    /// <summary>A transport stream packet: every 10th carries the stream's clock; all carry their number (and the channel).</summary>
    static void MakePacket(byte[] p, int ch, long n)
    {
        Array.Clear(p);
        p[0] = 0x47; p[1] = 0x01; p[2] = 0x00; // PID 0x100
        if (n % 10 == 0)
        {
            p[3] = 0x20; p[4] = 183; p[5] = 0x10;
            long pcr = (long)(n * 27_000_000.0 / LiveRate);
            long b = pcr / 300; int ext = (int)(pcr % 300);
            p[6] = (byte)(b >> 25); p[7] = (byte)(b >> 17); p[8] = (byte)(b >> 9); p[9] = (byte)(b >> 1);
            p[10] = (byte)(((b & 1) << 7) | 0x7E | ((ext >> 8) & 1)); p[11] = (byte)ext;
            BitConverter.TryWriteBytes(p.AsSpan(12), n);
            p[20] = (byte)ch;
        }
        else
        {
            p[3] = 0x10;
            BitConverter.TryWriteBytes(p.AsSpan(4), n);
            p[12] = (byte)ch;
        }
    }

    static (long N, int Ch) ReadPacket(ReadOnlySpan<byte> p) =>
        (p[3] & 0x20) != 0 ? (BitConverter.ToInt64(p[12..20]), p[20]) : (BitConverter.ToInt64(p[4..12]), p[12]);

    /// <summary>
    /// The self-test's "Share one connection" check (both apps): two live channels from a stand-in provider that allows one
    /// connection, each read at real time like a player would. Both must arrive complete and in order, with never more than
    /// one connection open.
    /// </summary>
    public static async Task CheckSharedConnectionAsync(Action<string> note, Action<Exception> error, int seconds = 16)
    {
        using var server = new TestServer(null);
        using var share = new ConnectionShare();
        string problem = null;
        share.Problem += (_, m) => problem = m;
        var a = share.Add("a", server.Base + "live/1.ts");
        var b = share.Add("b", server.Base + "live/2.ts");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        async Task<(long got, long gaps, long repeats, bool wrongChannel, double stalls)> Watch(string url, int ch)
        {
            long got = 0, gaps = 0, repeats = 0, last = -1;
            bool wrong = false;
            double stalled = 0;
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                await using var st = await http.GetStreamAsync(url, cts.Token);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var buf = new byte[188 * 64];
                int have = 0;
                double started = -1;
                while (!cts.IsCancellationRequested)
                {
                    // Like a player: take what's there, but no faster than real time once it's playing (a 2-second buffer).
                    if (started >= 0 && got > (sw.Elapsed.TotalSeconds - started + 2) * LiveRate) { await Task.Delay(50, cts.Token); continue; }
                    var t0 = sw.Elapsed;
                    int n = await st.ReadAsync(buf.AsMemory(have), cts.Token);
                    if (n == 0) break;
                    if (started >= 0 && (sw.Elapsed - t0).TotalSeconds > 0.25) stalled += (sw.Elapsed - t0).TotalSeconds;
                    have += n;
                    int whole = have / 188 * 188;
                    for (int i = 0; i < whole; i += 188)
                    {
                        var (num, c) = ReadPacket(buf.AsSpan(i, 188));
                        if (c != ch) wrong = true;
                        if (last >= 0 && num <= last) repeats++;
                        else if (last >= 0 && num > last + 1) gaps++;
                        last = Math.Max(last, num);
                        got++;
                        if (started < 0) started = sw.Elapsed.TotalSeconds;
                    }
                    Buffer.BlockCopy(buf, whole, buf, 0, have - whole);
                    have -= whole;
                }
            }
            catch (OperationCanceledException) { }
            return (got, gaps, repeats, wrong, stalled);
        }
        var ra = Watch(a, 1);
        var rb = Watch(b, 2);
        await Task.WhenAll(ra, rb);
        foreach (var (name, r) in new[] { ("first", ra.Result), ("second", rb.Result) })
        {
            double secs = r.got / (double)LiveRate;
            note($"shared connection: {name} channel {secs:0.0}s received · gaps {r.gaps} · repeats {r.repeats} · waited {r.stalls:0.0}s");
            if (r.wrongChannel || r.repeats > 0 || r.gaps > 0 || secs < seconds * 0.6)
                error(new Exception($"Sharing one connection didn't deliver the {name} channel complete and in order ({secs:0.0}s, {r.gaps} gaps, {r.repeats} repeats{(r.wrongChannel ? ", mixed up with the other" : "")})."));
        }
        note($"shared connection: most connections open at once {server.LivePeak} (Dingus's count {share.PeakConnections}) · refused {server.LiveRefused}{(problem != null ? " · " + problem : "")}");
        if (server.LivePeak > 1 || share.PeakConnections > 1) error(new Exception("Sharing one connection opened more than one."));
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
