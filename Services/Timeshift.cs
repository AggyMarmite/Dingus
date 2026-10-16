using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Dingus.Services;

/// <summary>
/// Pause &amp; rewind for live TV. The live stream is downloaded once into a rolling buffer on disk
/// (fixed-size chunk files, oldest deleted as it fills), and VLC plays it back from
/// <see cref="LocalStreamServer"/> on 127.0.0.1 – from any point in the buffer. Memory use stays flat
/// whatever the buffer length: data only passes through small copy buffers.
/// Only MPEG-TS streams are buffered; anything else (HLS) plays directly as before.
/// </summary>
public sealed class TimeshiftSession : IDisposable
{
    const int ChunkSize = 32 * 1024 * 1024;
    public const int PacketSize = 188; // MPEG-TS packet: jump targets are kept on packet boundaries

    static readonly HttpClient Client = MakeClient();

    readonly string _dir;
    readonly CancellationTokenSource _cts = new();
    readonly object _lock = new();
    readonly List<Chunk> _chunks = new();
    readonly List<(DateTime Utc, long Offset)> _index = new(); // ~one entry a second: stream time → byte offset
    readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TimeSpan _keep;
    readonly long _maxBytes;
    long _written; // absolute end of the data received so far
    long _first;   // absolute offset of the oldest byte still on disk
    bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string SourceUrl { get; }
    /// <summary>The download has stopped for good (provider closed the stream and retries failed).</summary>
    public bool Ended { get; private set; }

    sealed class Chunk
    {
        public long Start;
        public string Path;
        public FileStream Writer;
        public long Length;
        public DateTime EndUtc;
    }

    TimeshiftSession(string url, TimeSpan keep, long maxBytes)
    {
        SourceUrl = url;
        _keep = keep;
        _maxBytes = maxBytes;
        _dir = Path.Combine(Root, Id);
        Directory.CreateDirectory(_dir);
    }

    public static string Root => Path.Combine(AppPaths.Root, "timeshift");

    /// <summary>Removes buffers left behind by a previous run (e.g. after a crash).</summary>
    public static void CleanUp()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }

    /// <summary>True for addresses worth trying (HLS playlists are played directly).</summary>
    public static bool Eligible(string url) =>
        url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) && !url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts downloading and waits until the first data arrives. Returns null if the stream can't be
    /// buffered (not MPEG-TS, refused, too slow) – the caller then plays it directly.
    /// </summary>
    public static TimeshiftSession TryStart(string url, int minutes, TimeSpan wait)
    {
        long free = long.MaxValue;
        try { free = new DriveInfo(Path.GetPathRoot(AppPaths.Root)!).AvailableFreeSpace; } catch { }
        // Never use more than 8 GB, or a quarter of the free space.
        long max = Math.Min(8L << 30, free / 4);
        if (max < 256L << 20) return null; // nearly full disk – just play directly

        var s = new TimeshiftSession(url, TimeSpan.FromMinutes(Math.Clamp(minutes, 5, 240)), max);
        _ = Task.Run(() => s.RunAsync());
        bool ok;
        try { ok = s._started.Task.Wait(wait) && s._started.Task.Result; }
        catch { ok = false; }
        if (!ok) { s.Dispose(); return null; }
        LocalStreamServer.Register(s);
        return s;
    }

    static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        // Same identity VLC uses, so providers treat it exactly like direct playback.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.21 LibVLC/3.0.21");
        return c;
    }

    async Task RunAsync()
    {
        var buf = new byte[64 * 1024];
        int failures = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
                using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                resp.EnsureSuccessStatusCode();
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || type.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("dash", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Not an MPEG-TS stream");

                await using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                bool first = true;
                while (true)
                {
                    readCts.CancelAfter(TimeSpan.FromSeconds(15)); // provider went quiet → reconnect
                    int n = await stream.ReadAsync(buf, readCts.Token);
                    if (n <= 0) break;
                    if (first)
                    {
                        first = false;
                        // A fresh MPEG-TS stream starts with the sync byte.
                        if (_written == 0 && buf[0] != 0x47) throw new NotSupportedException("Not an MPEG-TS stream");
                    }
                    Append(buf, n);
                    failures = 0;
                    _started.TrySetResult(true);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (NotSupportedException) { break; }
            catch (Exception ex)
            {
                if (!_started.Task.IsCompleted) { Log.Error(ex); break; } // couldn't start: play directly instead
            }
            if (!_started.Task.IsCompleted || ++failures > 5) break;
            try { await Task.Delay(1000 * failures, _cts.Token); } catch { break; }
        }
        Ended = true;
        _started.TrySetResult(false);
    }

    void Append(byte[] b, int n)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_disposed) return;
            if (_index.Count == 0 || now - _index[^1].Utc >= TimeSpan.FromSeconds(1)) _index.Add((now, _written));
            int off = 0;
            var c = _chunks.Count > 0 ? _chunks[^1] : null;
            while (off < n)
            {
                if (c == null || c.Length >= ChunkSize)
                {
                    if (c != null) { c.Writer.Dispose(); c.Writer = null; }
                    c = new Chunk { Start = _written + off, Path = Path.Combine(_dir, $"{_chunks.Count:D6}.ts") };
                    c.Writer = new FileStream(c.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                    _chunks.Add(c);
                }
                int take = (int)Math.Min(n - off, ChunkSize - c.Length);
                c.Writer.Write(b, off, take);
                c.Length += take;
                off += take;
            }
            c.EndUtc = now;
            _written += n;
            Trim(now);
        }
    }

    void Trim(DateTime now)
    {
        while (_chunks.Count > 1 && (_chunks[0].EndUtc < now - _keep || _written - _chunks[0].Start > _maxBytes))
        {
            var old = _chunks[0];
            _chunks.RemoveAt(0);
            try { old.Writer?.Dispose(); File.Delete(old.Path); } catch { }
            _first = _chunks[0].Start;
        }
        int drop = 0;
        while (drop < _index.Count - 1 && _index[drop + 1].Offset <= _first) drop++;
        if (drop > 0) _index.RemoveRange(0, drop);
    }

    // ------------------------------------------------------------------ positions

    public DateTime LatestUtc { get { lock (_lock) return _index.Count > 0 ? _index[^1].Utc : DateTime.UtcNow; } }
    public DateTime OldestUtc { get { lock (_lock) return _index.Count > 0 ? _index[0].Utc : DateTime.UtcNow; } }

    /// <summary>The byte offset to start from to show the stream as it was at <paramref name="utc"/>, and that point's time.</summary>
    public (long Offset, DateTime Utc) Locate(DateTime utc)
    {
        lock (_lock)
        {
            if (_index.Count == 0) return (0, DateTime.UtcNow);
            int lo = 0, hi = _index.Count - 1, best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_index[mid].Utc <= utc) { best = mid; lo = mid + 1; } else hi = mid - 1;
            }
            var e = _index[best];
            long off = Math.Max(e.Offset, _first);
            return (off - off % PacketSize, e.Utc);
        }
    }

    /// <summary>
    /// Finds data at <paramref name="pos"/> for a reader. Moves <paramref name="pos"/> forward if that part has
    /// already been deleted. Returns false when there's nothing there yet.
    /// </summary>
    internal bool TryLocate(ref long pos, out string path, out long chunkStart, out long available)
    {
        lock (_lock)
        {
            path = null; chunkStart = 0; available = 0;
            if (_disposed || _chunks.Count == 0) return false;
            if (pos < _first) pos = _first;
            for (int i = _chunks.Count - 1; i >= 0; i--)
            {
                var c = _chunks[i];
                if (pos >= c.Start)
                {
                    available = c.Start + c.Length - pos;
                    if (available <= 0) return false; // caught up with live
                    path = c.Path;
                    chunkStart = c.Start;
                    return true;
                }
            }
            return false;
        }
    }

    internal bool Closed => _disposed || Ended;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var c in _chunks) { try { c.Writer?.Dispose(); } catch { } }
            _chunks.Clear();
            _index.Clear();
        }
        try { _cts.Cancel(); } catch { }
        LocalStreamServer.Unregister(this);
        var dir = _dir;
        // Readers may still have a file open for a moment.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
                catch { await Task.Delay(1000); }
            }
        });
    }
}

/// <summary>
/// A tiny HTTP server on 127.0.0.1 that plays timeshift buffers to VLC:
/// GET /ts/{session}?from={offset} streams from that byte to the live edge and keeps following it.
/// </summary>
public static class LocalStreamServer
{
    static TcpListener _listener;
    static readonly Dictionary<string, TimeshiftSession> Sessions = new();
    static readonly object Lock = new();

    public static int Port { get; private set; }

    public static void Register(TimeshiftSession s)
    {
        lock (Lock)
        {
            Sessions[s.Id] = s;
            if (_listener == null)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoop);
            }
        }
    }

    public static void Unregister(TimeshiftSession s) { lock (Lock) Sessions.Remove(s.Id); }

    public static string UrlFor(TimeshiftSession s, long from) => $"http://127.0.0.1:{Port}/ts/{s.Id}?from={from}";

    static async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    static async Task Serve(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        FileStream fs = null;
        try
        {
            var net = client.GetStream();
            var (path, range) = await ReadRequest(net);
            TimeshiftSession s = null;
            long pos = 0;
            if (path != null && path.StartsWith("/ts/"))
            {
                var q = path.IndexOf('?');
                var id = path[4..(q < 0 ? path.Length : q)];
                lock (Lock) Sessions.TryGetValue(id, out s);
                if (q >= 0)
                    foreach (var part in path[(q + 1)..].Split('&'))
                        if (part.StartsWith("from=") && long.TryParse(part[5..], out var f)) pos = f;
            }
            if (s == null)
            {
                await Write(net, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }
            // VLC reconnecting after a long pause asks to continue from where it was ("Range: bytes=N-").
            pos += range;
            await Write(net, "HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");

            var buf = new byte[64 * 1024];
            string fsPath = null;
            int idle = 0;
            while (true)
            {
                if (!s.TryLocate(ref pos, out var file, out var start, out var avail))
                {
                    if (s.Closed) break;
                    if (++idle > 400) break; // 20 s without new data: let VLC's stall handling take over
                    await Task.Delay(50);
                    continue;
                }
                idle = 0;
                if (file != fsPath)
                {
                    fs?.Dispose();
                    fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                    fsPath = file;
                }
                fs.Position = pos - start;
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, avail));
                if (n <= 0) { await Task.Delay(50); continue; }
                await net.WriteAsync(buf.AsMemory(0, n)); // blocks while VLC is paused – that's the pause
                pos += n;
            }
        }
        catch (IOException) { } // VLC closed the connection (zap, jump, stop)
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Error(ex); }
        finally { fs?.Dispose(); }
    }

    static async Task<(string Path, long Range)> ReadRequest(NetworkStream net)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 8192)
        {
            int n = await net.ReadAsync(one);
            if (n <= 0) break;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
        }
        var lines = sb.ToString().Split("\r\n");
        var first = lines[0].Split(' ');
        string path = first.Length >= 2 ? first[1] : null;
        long range = 0;
        foreach (var l in lines)
            if (l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            {
                var v = l[6..].Trim();
                if (v.StartsWith("bytes=")) long.TryParse(v[6..].Split('-')[0], out range);
            }
        return (path, range);
    }

    static Task Write(NetworkStream net, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        return net.WriteAsync(b, 0, b.Length);
    }
}
