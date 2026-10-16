// Checks the live TV buffer end to end: a fake MPEG-TS source → TimeshiftSession → LocalStreamServer → reader.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dingus.Services;

namespace Dingus.Services
{
    static class AppPaths { public static readonly string Root = Path.Combine(Path.GetTempPath(), "dingus-test"); }
    static class Log { public static void Error(Exception ex) => Console.WriteLine("LOG: " + ex.Message); }
}

static class Program
{
    static int _fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) _fails++; }

    static async Task<int> Main()
    {
        int srcPort = StartSource("video/mp2t");
        int hlsPort = StartSource("application/vnd.apple.mpegurl");

        // 1. A TS stream starts buffering.
        var s = TimeshiftSession.TryStart($"http://127.0.0.1:{srcPort}/live.ts", 5, TimeSpan.FromSeconds(10));
        Check(s != null, "TS stream starts buffering");
        if (s == null) return 1;

        // 2. An HLS stream is refused (it will be played directly instead).
        var h = TimeshiftSession.TryStart($"http://127.0.0.1:{hlsPort}/live.m3u8x", 5, TimeSpan.FromSeconds(10));
        Check(h == null, "HLS stream is not buffered");

        await Task.Delay(3000);

        // 3. Reading from the start gives every packet in order.
        var first = await Read(LocalStreamServer.UrlFor(s, 0), 2_000_000, null);
        Check(first.Count > 10000 && Sequential(first, 0), $"read from start: {first.Count} packets in order");

        // 4. Jumping to a time lands on a packet boundary with the right packet.
        var (off, utc) = s.Locate(s.LatestUtc.AddSeconds(-1));
        Check(off % 188 == 0 && off > 0, $"locate 1 s before live → offset {off}");
        var mid = await Read(LocalStreamServer.UrlFor(s, off), 500_000, null);
        Check(mid.Count > 0 && mid[0] == off / 188 && Sequential(mid, off / 188), $"read from jump point starts at packet {(mid.Count > 0 ? mid[0] : -1)}");

        // 5. VLC reconnecting with a Range header continues from where it was.
        var ranged = await Read(LocalStreamServer.UrlFor(s, 0), 100_000, 188_000);
        Check(ranged.Count > 0 && ranged[0] == 1000, $"Range: bytes=188000- continues at packet {(ranged.Count > 0 ? ranged[0] : -1)}");

        // 6. A reader at the live edge keeps receiving new data as it arrives.
        var live = s.Locate(s.LatestUtc);
        var t0 = DateTime.UtcNow;
        var tail = await Read(LocalStreamServer.UrlFor(s, live.Offset), 3_000_000, null);
        Check(tail.Count > 1000 && Sequential(tail, tail[0]) && DateTime.UtcNow - t0 > TimeSpan.FromSeconds(0.5),
            $"following live: {tail.Count} packets over {(DateTime.UtcNow - t0).TotalSeconds:0.0} s");

        // 7. Behind-live maths.
        Check(s.LatestUtc - s.OldestUtc > TimeSpan.FromSeconds(3), $"buffered {(s.LatestUtc - s.OldestUtc).TotalSeconds:0} s");

        // 8. Disposing deletes the buffer and stops the download.
        var dir = Path.Combine(TimeshiftSession.Root, s.Id);
        Check(Directory.GetFiles(dir).Length > 0, "buffer files exist while watching");
        s.Dispose();
        await Task.Delay(2500);
        Check(!Directory.Exists(dir), "buffer deleted after changing channel");

        Console.WriteLine(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        return _fails == 0 ? 0 : 1;
    }

    static bool Sequential(List<long> p, long start)
    {
        for (int i = 0; i < p.Count; i++) if (p[i] != start + i) { Console.WriteLine($"  gap at {i}: {p[i]} != {start + i}"); return false; }
        return true;
    }

    /// <summary>Reads packets (their counters) from the local server.</summary>
    static async Task<List<long>> Read(string url, int bytes, long? range)
    {
        var u = new Uri(url);
        using var c = new TcpClient();
        await c.ConnectAsync(u.Host, u.Port);
        var net = c.GetStream();
        var req = $"GET {u.PathAndQuery} HTTP/1.1\r\nHost: x\r\n" + (range != null ? $"Range: bytes={range}-\r\n" : "") + "\r\n";
        await net.WriteAsync(Encoding.ASCII.GetBytes(req));
        var all = new MemoryStream();
        var buf = new byte[65536];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (all.Length < bytes + 1024)
        {
            int n;
            try { n = await net.ReadAsync(buf, cts.Token); } catch { break; }
            if (n <= 0) break;
            all.Write(buf, 0, n);
        }
        var data = all.ToArray();
        int body = IndexOf(data, "\r\n\r\n"u8.ToArray()) + 4;
        var list = new List<long>();
        for (int i = body; i + 188 <= data.Length && list.Count * 188 < bytes; i += 188)
        {
            if (data[i] != 0x47) { Console.WriteLine($"  bad sync byte at packet {list.Count}"); break; }
            list.Add(BitConverter.ToInt64(data, i + 4));
        }
        return list;
    }

    static int IndexOf(byte[] d, byte[] p)
    {
        for (int i = 0; i + p.Length <= d.Length; i++)
        {
            int j = 0; while (j < p.Length && d[i + j] == p[j]) j++;
            if (j == p.Length) return i;
        }
        return -1;
    }

    /// <summary>A fake live channel: endless numbered 188-byte packets at ~2 MB/s, sent in odd-sized writes.</summary>
    static int StartSource(string contentType)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var _ = c;
                        var net = c.GetStream();
                        var tmp = new byte[4096];
                        await net.ReadAsync(tmp);
                        await net.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n"));
                        long n = 0;
                        var block = new byte[188 * 100];
                        while (true)
                        {
                            for (int k = 0; k < 100; k++)
                            {
                                block[k * 188] = 0x47;
                                BitConverter.GetBytes(n++).CopyTo(block, k * 188 + 4);
                            }
                            // Odd write sizes, so packets straddle reads and chunk boundaries.
                            await net.WriteAsync(block.AsMemory(0, 7001));
                            await net.WriteAsync(block.AsMemory(7001));
                            await Task.Delay(9);
                        }
                    }
                    catch { }
                });
            }
        });
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
