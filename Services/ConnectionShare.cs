using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Dingus.Services;

/// <summary>
/// Multi-view's "Share one connection" (experimental): several live channels from your playlist over a single connection.
/// Dingus never has more than one connection to the provider open. It visits each channel in turn; when a channel is
/// opened, most providers send a head start of what they've already buffered (often 15–25 seconds) before settling to
/// real time. Each visit collects that head start, the part that repeats what the tile already has is trimmed, the rest
/// is joined on, and the tile with the least left to play is visited next. Each tile's player reads its channel from a
/// small server on this computer. Only for live channels sent as MPEG transport streams (not HLS); it needs a provider
/// that sends the head start quickly, and a line fast enough for all the channels together. Shared by both apps.
/// </summary>
public sealed class ConnectionShare : IDisposable
{
    const int Packet = 188;
    const double LowWaterSeconds = 10;   // visit a channel once it has less than this left to play
    const double EnoughSeconds = 40;     // don't bank more than this per channel
    const double MaxVisitSeconds = 10;
    const int TailBytes = Packet * 10;   // what a visit looks for to line up with what the tile already has

    sealed class Feed
    {
        public string Key, Url;
        public readonly object Lock = new();
        public readonly List<byte[]> Chunks = new();
        public long BaseOffset, Total, Served;
        public double BytesPerSecond = 4_000_000 / 8.0; // until the stream's own clock says otherwise
        public byte[] Tail = Array.Empty<byte>();
        public bool Removed;
        public int Failures, Starved;
        public readonly SemaphoreSlim Arrived = new(0, int.MaxValue);
        // the stream's clock (PCR) for working out its bitrate
        public long SyncBase = -1;
        public long FirstPcrOffset = -1, LastPcrOffset = -1;
        public double FirstPcr, LastPcr;
        public int PcrPid = -1;
        /// <summary>When the tile's player started reading it (it plays at real time from then on).</summary>
        public long PlayStartTicks = -1;
        /// <summary>
        /// Seconds collected but not yet played. Counted by the clock rather than by what the player has read, because a
        /// player (and the network buffers on the way) read well ahead of what's on screen.
        /// </summary>
        public double SecondsLeft
        {
            get
            {
                double collected = Total / Math.Max(1, BytesPerSecond);
                long start = Interlocked.Read(ref PlayStartTicks);
                return start < 0 ? collected : collected - (Environment.TickCount64 - start) / 1000.0;
            }
        }
    }

    readonly HttpListener _http = new();
    readonly Dictionary<string, Feed> _feeds = new();
    readonly CancellationTokenSource _cts = new();
    readonly HttpClient _client;
    Task _loop;
    int _active; // connections to the provider open right now (never more than 1)
    long _lastVisitEnded;

    /// <summary>"http://127.0.0.1:port/"</summary>
    public string Base { get; }

    /// <summary>The most connections to the provider that were ever open at once (for the self-test: always 1).</summary>
    public int PeakConnections { get; private set; }

    /// <summary>Something worth telling you about a channel ("the provider isn't sending a head start…"), or null when it's fine.</summary>
    public event Action<string, string> Problem;

    public ConnectionShare()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Base = $"http://127.0.0.1:{port}/";
        _http.Prefixes.Add(Base);
        _http.Start();
        _ = Task.Run(ListenAsync);
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.Zero, // a fresh connection each visit, closed straight after
            MaxResponseDrainSize = 0,                 // …really closed, not read on to the end (a live channel has no end)
            ResponseDrainTimeout = TimeSpan.Zero,
        };
        _client = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.21 LibVLC/3.0.21");
    }

    /// <summary>True for addresses this can share: live channels that aren't HLS.</summary>
    public static bool CanShare(string url) =>
        url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts sharing a channel; returns the address the tile's player should open.</summary>
    public string Add(string key, string url)
    {
        lock (_feeds)
        {
            if (_feeds.TryGetValue(key, out var old)) Stop(old);
            _feeds[key] = new Feed { Key = key, Url = url };
        }
        _loop ??= Task.Run(RotateAsync);
        return $"{Base}t/{Uri.EscapeDataString(key)}.ts?n={Environment.TickCount64}";
    }

    public void Remove(string key)
    {
        lock (_feeds)
            if (_feeds.Remove(key, out var f)) Stop(f);
    }

    static void Stop(Feed f)
    {
        f.Removed = true;
        f.Arrived.Release();
    }

    /// <summary>Seconds of a channel collected but not yet played (for the self-test and the tile's status).</summary>
    public double SecondsAhead(string key)
    {
        lock (_feeds) return _feeds.TryGetValue(key, out var f) ? f.SecondsLeft : 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_feeds) { foreach (var f in _feeds.Values) Stop(f); _feeds.Clear(); }
        try { _http.Stop(); _http.Close(); } catch { }
        _client.Dispose();
    }

    // ------------------------------------------------------------------ visiting the channels, one connection at a time

    async Task RotateAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Feed next;
            lock (_feeds) next = _feeds.Values.Where(f => !f.Removed).OrderBy(f => f.SecondsLeft).FirstOrDefault();
            if (next == null || next.SecondsLeft > LowWaterSeconds)
            {
                try { await Task.Delay(250, ct); } catch { return; }
                continue;
            }
            // The provider needs a moment to count the last channel's connection as closed.
            if (_lastVisitEnded > 0 && Environment.TickCount64 - _lastVisitEnded < 250)
                try { await Task.Delay(250, ct); } catch { return; }
            try { await VisitAsync(next, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Info($"Shared connection: {next.Key}: {ex.Message}");
                next.Failures++;
                try { await Task.Delay(Math.Min(5000, 500 * next.Failures), ct); } catch { return; }
            }
        }
    }

    async Task VisitAsync(Feed f, CancellationToken ct)
    {
        double before = f.SecondsLeft;
        long startTotal = f.Total;
        if (Interlocked.Increment(ref _active) > PeakConnections) PeakConnections = _active;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, f.Url);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            HttpResponseMessage resp;
            try { resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("the provider didn't answer"); }
            using var _ = resp;
            if (!resp.IsSuccessStatusCode)
            {
                f.Failures++;
                // Straight after closing the last channel some providers still count it for a moment: wait and try again.
                if (f.Failures >= 4)
                    Problem?.Invoke(f.Key, $"The provider refused the connection ({(int)resp.StatusCode}).");
                await Task.Delay(Math.Min(4000, 400 * f.Failures), ct);
                return;
            }
            f.Failures = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);

            var buf = new byte[1 << 16];
            bool aligned = f.Total == 0 || f.Tail.Length == 0; // the first visit has nothing to line up with
            var pending = aligned ? null : new MemoryStream();
            int searchFrom = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var window = TimeSpan.Zero;
            long windowBytes = 0;
            int slowWindows = 0;
            while (!f.Removed)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(5));
                int n;
                try { n = await src.ReadAsync(buf, stall.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                if (n == 0) break;
                windowBytes += n;

                if (!aligned)
                {
                    pending.Write(buf, 0, n);
                    if (TryLineUp(f, pending, ref searchFrom)) { aligned = true; pending = null; }
                }
                else Append(f, buf.AsSpan(0, n));

                var t = sw.Elapsed;
                if (t - window >= TimeSpan.FromMilliseconds(500))
                {
                    double rate = windowBytes / (t - window).TotalSeconds;
                    window = t;
                    windowBytes = 0;
                    // The head start is in once the provider settles to real time.
                    if (t.TotalSeconds > 1 && rate < f.BytesPerSecond * 1.6) slowWindows++;
                    else slowWindows = 0;
                }
                bool settled = slowWindows >= 2 && (aligned || pending == null);
                bool othersStarving;
                lock (_feeds) othersStarving = _feeds.Values.Any(o => o != f && !o.Removed && o.SecondsLeft < 4);
                if (settled && !aligned) break;
                if (settled || f.SecondsLeft >= EnoughSeconds || t.TotalSeconds >= MaxVisitSeconds || (othersStarving && f.SecondsLeft > 8)) break;
            }
            // The burst didn't repeat anything the tile had (it was away too long): join it on as it is.
            if (!aligned && pending is { Length: > 0 }) AppendGap(f, pending);
        }
        finally { Interlocked.Decrement(ref _active); _lastVisitEnded = Environment.TickCount64; }

        double gained = (f.Total - startTotal) / Math.Max(1, f.BytesPerSecond);
        if (before < 5 && gained < 3)
        {
            if (++f.Starved == 3)
                Problem?.Invoke(f.Key, "Your provider doesn't send a head start, so sharing one connection can't keep this channel going.");
        }
        else f.Starved = 0;
    }

    /// <summary>
    /// Looks for where this visit's head start repeats the end of what the tile already has, and joins on what comes after.
    /// True once lined up (or given up on, after a lot without finding it: then it's joined on as a gap).
    /// </summary>
    bool TryLineUp(Feed f, MemoryStream pending, ref int searchFrom)
    {
        var seen = pending.GetBuffer().AsSpan(0, (int)pending.Length);
        int at = seen[searchFrom..].IndexOf(f.Tail);
        if (at >= 0)
        {
            Append(f, seen[(searchFrom + at + f.Tail.Length)..]);
            return true;
        }
        searchFrom = Math.Max(0, seen.Length - f.Tail.Length + 1);
        if (seen.Length > 64 << 20) { AppendGap(f, pending); return true; }
        return false;
    }

    void AppendGap(Feed f, MemoryStream pending) => AppendGap(f, pending.GetBuffer().AsSpan(0, (int)pending.Length));

    // ------------------------------------------------------------------ the tile's buffer

    void Append(Feed f, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var copy = data.ToArray();
        lock (f.Lock)
        {
            ReadClock(f, copy, f.Total);
            f.Chunks.Add(copy);
            f.Total += copy.Length;
            // What the next visit lines up with.
            var tail = new byte[Math.Min(TailBytes, (int)Math.Min(f.Total, int.MaxValue))];
            CopyOut(f, f.Total - tail.Length, tail);
            f.Tail = tail;
            // Let go of what's been played (keeping a little, in case the player reconnects).
            while (f.Chunks.Count > 1 && f.BaseOffset + f.Chunks[0].Length < f.Served - (1 << 20))
            {
                f.BaseOffset += f.Chunks[0].Length;
                f.Chunks.RemoveAt(0);
            }
        }
        f.Arrived.Release();
    }

    /// <summary>A part that doesn't follow on (a gap): joined from its first whole packet; the stream's clock starts again.</summary>
    void AppendGap(Feed f, ReadOnlySpan<byte> data)
    {
        int s = FindSync(data);
        if (s < 0) return;
        lock (f.Lock) { f.SyncBase = -1; f.FirstPcrOffset = f.LastPcrOffset = -1; }
        Append(f, data[s..]);
    }

    static int FindSync(ReadOnlySpan<byte> d)
    {
        for (int i = 0; i + Packet * 2 < d.Length && i < Packet * 4; i++)
            if (d[i] == 0x47 && d[i + Packet] == 0x47 && d[i + Packet * 2] == 0x47) return i;
        return d.Length > 0 && d[0] == 0x47 ? 0 : -1;
    }

    static void CopyOut(Feed f, long from, Span<byte> into)
    {
        long pos = f.BaseOffset;
        int written = 0;
        foreach (var c in f.Chunks)
        {
            long end = pos + c.Length;
            if (end > from && written < into.Length)
            {
                int start = (int)Math.Max(0, from + written - pos);
                int len = Math.Min(c.Length - start, into.Length - written);
                if (len > 0) { c.AsSpan(start, len).CopyTo(into[written..]); written += len; }
            }
            pos = end;
        }
    }

    /// <summary>Reads the stream's clock (PCR) as it arrives, to know its real bitrate – and so how many seconds a tile has left.</summary>
    static void ReadClock(Feed f, byte[] data, long absStart)
    {
        if (f.SyncBase < 0)
        {
            int s = FindSync(data);
            if (s < 0) return;
            f.SyncBase = absStart + s;
        }
        long first = absStart + ((f.SyncBase - absStart) % Packet + Packet) % Packet;
        for (long p = first; p + Packet <= absStart + data.Length; p += Packet)
        {
            int i = (int)(p - absStart);
            if (data[i] != 0x47) { f.SyncBase = -1; return; } // lost the packet boundaries: find them again next time
            int pid = ((data[i + 1] & 0x1F) << 8) | data[i + 2];
            if ((data[i + 3] & 0x20) == 0 || data[i + 4] < 7 || (data[i + 5] & 0x10) == 0) continue; // no clock in this packet
            if (f.PcrPid >= 0 && pid != f.PcrPid) continue;
            f.PcrPid = pid;
            long b = ((long)data[i + 6] << 25) | ((long)data[i + 7] << 17) | ((long)data[i + 8] << 9) | ((long)data[i + 9] << 1) | ((long)data[i + 10] >> 7);
            int ext = ((data[i + 10] & 1) << 8) | data[i + 11];
            double pcr = (b * 300 + ext) / 27_000_000.0;
            if (f.FirstPcrOffset < 0 || pcr < f.LastPcr) { f.FirstPcrOffset = p; f.FirstPcr = pcr; }
            f.LastPcrOffset = p;
            f.LastPcr = pcr;
            double span = f.LastPcr - f.FirstPcr;
            if (span >= 2) f.BytesPerSecond = (f.LastPcrOffset - f.FirstPcrOffset) / span;
        }
    }

    // ------------------------------------------------------------------ handing each tile its channel

    async Task ListenAsync()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => ServeAsync(ctx));
        }
    }

    async Task ServeAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            Feed f = null;
            if (path.StartsWith("/t/") && path.EndsWith(".ts"))
            {
                var key = Uri.UnescapeDataString(path[3..^3]);
                lock (_feeds) _feeds.TryGetValue(key, out f);
            }
            if (f == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            ctx.Response.ContentType = "video/mp2t";
            ctx.Response.SendChunked = true;
            var output = ctx.Response.OutputStream;
            long pos;
            lock (f.Lock) pos = Math.Max(f.BaseOffset, f.Served); // a reconnecting player carries on where it was
            var buf = new byte[1 << 16];
            while (!f.Removed && !_cts.IsCancellationRequested)
            {
                int n;
                lock (f.Lock)
                {
                    pos = Math.Max(pos, f.BaseOffset);
                    n = (int)Math.Min(buf.Length, f.Total - pos);
                    if (n > 0) CopyOut(f, pos, buf.AsSpan(0, n));
                }
                if (n <= 0) { await f.Arrived.WaitAsync(1000); continue; }
                await output.WriteAsync(buf.AsMemory(0, n));
                Interlocked.CompareExchange(ref f.PlayStartTicks, Environment.TickCount64, -1);
                pos += n;
                lock (f.Lock) f.Served = Math.Max(f.Served, pos);
            }
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }
}
