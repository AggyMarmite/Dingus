namespace Dingus.Services;

/// <summary>
/// Keeps a recording's timeline continuous. Live IPTV streams often jump in time – the provider restarts the stream,
/// or Dingus reconnects after a drop and the new connection's clock starts somewhere else entirely. Players work out a
/// .ts file's length from its first and last time marks, so one jump can make a 30-minute recording show as 6 hours
/// (and seeking goes wrong). This rewrites the time marks (PCR, and each audio/video packet's PTS/DTS) as the
/// packets go by, so the timeline runs straight through the jumps. The pictures and sound aren't touched.
/// </summary>
public sealed class TsTimeline
{
    const int Packet = 188;
    const long Wrap = 1L << 33;          // time marks are 33-bit, 90 kHz – they wrap every ~26.5 hours
    const long Hz = 90_000;
    const long MaxForward = 2 * Hz;      // consecutive clock marks are ~0.1 s apart; more than 2 s = a jump
    const long MaxBackward = Hz / 5;     // going back by more than 0.2 s = a jump
    const long Step = Hz / 25;           // what a jump is replaced with: one frame

    long _offset;            // added to every time mark (90 kHz)
    long _lastRaw = -1;      // last clock mark as received, unwrapped
    long _lastOut;           // last clock mark as written, unwrapped
    long _maxTsOut;          // latest audio/video time mark written, unwrapped (sound and pictures run a little ahead of the clock)
    bool _rebase;
    int _pcrPid = -1;

    /// <summary>How many jumps were smoothed over.</summary>
    public int Jumps { get; private set; }

    /// <summary>A new connection is starting: its first clock mark may jump, and may come from a different stream id.</summary>
    public void NewConnection()
    {
        _rebase = true;
        _pcrPid = -1;
    }

    /// <summary>
    /// Fixes whole 188-byte packets in place. <paramref name="data"/> must start on a packet boundary; returns how many
    /// bytes (whole packets) were processed – the rest is an incomplete packet to keep for next time.
    /// </summary>
    public int Process(Span<byte> data)
    {
        int n = data.Length / Packet * Packet;
        for (int i = 0; i < n; i += Packet)
        {
            var p = data.Slice(i, Packet);
            if (p[0] != 0x47) continue; // not a packet (shouldn't happen once in sync)
            ProcessPacket(p);
        }
        return n;
    }

    /// <summary>
    /// Lets the clock mark in <paramref name="pcrPacket"/> decide the timeline before the packets that came just ahead
    /// of it are written (a new connection's sound can arrive before its first clock mark). Works on a copy.
    /// </summary>
    public void Prime(ReadOnlySpan<byte> pcrPacket)
    {
        Span<byte> copy = stackalloc byte[Packet];
        pcrPacket[..Packet].CopyTo(copy);
        ProcessPacket(copy);
    }

    /// <summary>Finds where packets start (0x47 every 188 bytes); -1 if not found.</summary>
    public static int FindSync(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + Packet * 2 < data.Length && i < Packet * 4; i++)
            if (data[i] == 0x47 && data[i + Packet] == 0x47 && data[i + Packet * 2] == 0x47) return i;
        return -1;
    }

    /// <summary>True when this packet carries a clock mark (used to hold a new connection's first packets until one arrives).</summary>
    public static bool HasPcr(ReadOnlySpan<byte> p) =>
        p.Length >= 12 && p[0] == 0x47 && (p[3] & 0x20) != 0 && p[4] >= 7 && (p[5] & 0x10) != 0;

    void ProcessPacket(Span<byte> p)
    {
        int pid = ((p[1] & 0x1F) << 8) | p[2];
        int afc = (p[3] >> 4) & 3;
        int pos = 4;
        if ((afc & 2) != 0)
        {
            int afLen = p[4];
            if (afLen >= 7 && (p[5] & 0x10) != 0 && (_pcrPid < 0 || pid == _pcrPid))
            {
                _pcrPid = pid;
                OnPcr(p.Slice(6, 6));
            }
            pos = 5 + afLen;
        }
        if ((afc & 1) == 0 || pos + 14 > Packet) return;
        bool start = (p[1] & 0x40) != 0;
        if (!start || _lastRaw < 0) return;
        // PES header: 00 00 01, stream id, length (2), flags (2), header length, PTS/DTS
        var pes = p[pos..];
        if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1) return;
        byte sid = pes[3];
        if (sid is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF) return; // no time marks in these
        int flags = (pes[7] >> 6) & 3;
        if ((flags & 2) != 0) ShiftTime(pes.Slice(9, 5));
        if (flags == 3 && pes.Length >= 19) ShiftTime(pes.Slice(14, 5));
    }

    void OnPcr(Span<byte> f)
    {
        long baseRaw = ((long)f[0] << 25) | ((long)f[1] << 17) | ((long)f[2] << 9) | ((long)f[3] << 1) | ((long)f[4] >> 7);
        long raw;
        if (_lastRaw < 0) raw = baseRaw;
        else
        {
            long d = Mod(baseRaw - Mod(_lastRaw));
            if (d > Wrap / 2) d -= Wrap; // a step back
            raw = _lastRaw + d;
        }
        if (_lastRaw >= 0)
        {
            long outNow = raw + _offset;
            long step = outNow - _lastOut;
            if (_rebase || step > MaxForward || step < -MaxBackward)
            {
                if (step > MaxForward || step < -MaxBackward)
                {
                    // Carry on just after everything already written, so nothing overlaps at the join.
                    _offset += Math.Max(_lastOut, _maxTsOut) + Step - outNow;
                    Jumps++;
                }
            }
        }
        _rebase = false;
        _lastRaw = raw;
        _lastOut = raw + _offset;
        WritePcr(f, Mod(_lastOut));
    }

    /// <summary>A PTS/DTS: moved by the same amount as the clock, keeping its place relative to it.</summary>
    void ShiftTime(Span<byte> t)
    {
        if ((t[0] & 1) == 0) return; // marker bits missing – not a time mark
        long v = ((long)(t[0] >> 1) & 7) << 30 | (long)t[1] << 22 | ((long)t[2] >> 1) << 15 | (long)t[3] << 7 | ((long)t[4] >> 1);
        // Unwrap next to the current clock (time marks sit within a few seconds of it).
        long d = Mod(v - Mod(_lastRaw));
        if (d > Wrap / 2) d -= Wrap;
        long unwrapped = _lastRaw + d + _offset;
        if (unwrapped > _maxTsOut) _maxTsOut = unwrapped;
        long outV = Mod(unwrapped);
        t[0] = (byte)((t[0] & 0xF1) | (int)((outV >> 29) & 0x0E));
        t[1] = (byte)(outV >> 22);
        t[2] = (byte)((((outV >> 15) & 0x7F) << 1) | 1);
        t[3] = (byte)(outV >> 7);
        t[4] = (byte)(((outV & 0x7F) << 1) | 1);
    }

    static void WritePcr(Span<byte> f, long b)
    {
        f[0] = (byte)(b >> 25);
        f[1] = (byte)(b >> 17);
        f[2] = (byte)(b >> 9);
        f[3] = (byte)(b >> 1);
        f[4] = (byte)((f[4] & 0x7F) | (int)((b & 1) << 7));
    }

    static long Mod(long v) => ((v % Wrap) + Wrap) % Wrap;

    /// <summary>
    /// Repairs a finished recording in place (e.g. one made before this fix): reads it through, smoothing the jumps,
    /// into a new file, then swaps it in. Returns how many jumps were found (0 = nothing needed fixing).
    /// </summary>
    public static async Task<int> RepairFileAsync(string path, IProgress<double> progress = null, CancellationToken ct = default)
    {
        var tmp = path + ".fixing";
        var tl = new TsTimeline();
        long total = new FileInfo(path).Length, done = 0;
        await using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[Packet * 4096];
            int have = 0;
            bool synced = false;
            while (true)
            {
                int n = await src.ReadAsync(buf.AsMemory(have, buf.Length - have), ct);
                if (n <= 0) break;
                have += n;
                done += n;
                int start = 0;
                if (!synced)
                {
                    int s = FindSync(buf.AsSpan(0, have));
                    if (s < 0) { if (have > Packet * 8) { await dst.WriteAsync(buf.AsMemory(0, have), ct); have = 0; } continue; }
                    if (s > 0) await dst.WriteAsync(buf.AsMemory(0, s), ct);
                    start = s;
                    synced = true;
                }
                int used = tl.ProcessResync(buf.AsSpan(start, have - start), out int skipped);
                await dst.WriteAsync(buf.AsMemory(start, used), ct);
                int rest = have - start - used;
                Buffer.BlockCopy(buf, start + used, buf, 0, rest);
                have = rest;
                progress?.Report(total == 0 ? 1 : (double)done / total);
            }
            if (have > 0) await dst.WriteAsync(buf.AsMemory(0, have), ct);
        }
        if (tl.Jumps == 0) { File.Delete(tmp); return 0; }
        File.Move(tmp, path, true);
        return tl.Jumps;
    }

    /// <summary>Like <see cref="Process"/>, but steps over stray bytes between packets (a joined-up recording).</summary>
    int ProcessResync(Span<byte> data, out int skipped)
    {
        skipped = 0;
        int i = 0;
        while (i + Packet <= data.Length)
        {
            if (data[i] != 0x47)
            {
                // lost sync (a reconnect mid-packet): find the next packet start
                int s = FindSync(data[i..]);
                if (s < 0) break;
                i += s;
                skipped += s;
                continue;
            }
            ProcessPacket(data.Slice(i, Packet));
            i += Packet;
        }
        return i;
    }
}
