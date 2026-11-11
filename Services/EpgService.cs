using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>Programme descriptions written to a temporary file (deleted automatically when closed).</summary>
public sealed class DescStore : IDisposable
{
    readonly object _lock = new();
    FileStream _fs;
    byte[] _buf = new byte[2048];

    /// <summary>Where the descriptions are kept.</summary>
    public string Path { get; }

    /// <param name="keep">Keep the file after closing (the saved guide refers to it); otherwise it's deleted on close.</param>
    public DescStore(string path, bool keep = false)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                             1 << 16, (keep ? FileOptions.None : FileOptions.DeleteOnClose) | FileOptions.RandomAccess);
    }

    DescStore(FileStream fs, string path) { _fs = fs; Path = path; }

    /// <summary>Opens the descriptions of a saved guide (read only).</summary>
    public static DescStore Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.RandomAccess), path);

    /// <summary>Appends a description (only called while the guide is being built).</summary>
    public (long Offset, int Length) Add(string s)
    {
        int max = System.Text.Encoding.UTF8.GetMaxByteCount(s.Length);
        if (_buf.Length < max) _buf = new byte[max];
        int n = System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, 0);
        long off = _fs.Position;
        _fs.Write(_buf, 0, n);
        return (off, n);
    }

    public void Seal() => _fs.Flush();

    public string Read(long offset, int length)
    {
        lock (_lock)
        {
            if (_fs == null) return null;
            try
            {
                if (_buf.Length < length) _buf = new byte[length];
                _fs.Position = offset;
                int read = 0;
                while (read < length)
                {
                    int r = _fs.Read(_buf, read, length - read);
                    if (r <= 0) break;
                    read += r;
                }
                return System.Text.Encoding.UTF8.GetString(_buf, 0, read);
            }
            catch { return null; }
        }
    }

    public void Dispose()
    {
        lock (_lock) { _fs?.Dispose(); _fs = null; }
    }
}

public class EpgData : IDisposable
{
    public static readonly EpgData Empty = new();

    /// <summary>Where this guide's programme descriptions are kept (on disk).</summary>
    public DescStore Descs { get; set; }
    public void Dispose() => Descs?.Dispose();

    public Dictionary<string, List<Programme>> ByChannel { get; } = new();
    public Dictionary<string, string> Icons { get; } = new();
    public int MatchedChannels => ByChannel.Count;
    /// <summary>Every channel in the guide file (id, first name) – for matching a channel by hand.</summary>
    public List<(string Id, string Name)> Sources { get; } = new();
    /// <summary>What's on now on every guide channel (matched or not) – helps pick the right one by hand.</summary>
    public Dictionary<string, string> NowTitles { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Which guide channel each playlist channel was matched to.</summary>
    public Dictionary<string, string> MatchedTo { get; } = new();

    public List<Programme> Get(string channelId) =>
        channelId != null && ByChannel.TryGetValue(channelId, out var l) ? l : null;

    public string Icon(string channelId) =>
        channelId != null && Icons.TryGetValue(channelId, out var s) ? s : null;

    /// <summary>Index of the first programme that ends after <paramref name="t"/> (binary search).</summary>
    public static int FirstEndingAfter(List<Programme> l, DateTime t)
    {
        int lo = 0, hi = l.Count - 1, res = l.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (l[mid].End > t) { res = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return res;
    }

    public Programme At(string channelId, DateTime t)
    {
        var l = Get(channelId);
        if (l == null) return null;
        int i = FirstEndingAfter(l, t);
        return i < l.Count && l[i].Start <= t ? l[i] : null;
    }

    public Programme StartingFrom(string channelId, DateTime t)
    {
        var l = Get(channelId);
        if (l == null) return null;
        for (int i = FirstEndingAfter(l, t); i < l.Count; i++)
            if (l[i].Start >= t.AddSeconds(-30)) return l[i];
        return null;
    }
}

/// <summary>
/// Streams XMLTV (plain or gzipped), keeps only the channels in the playlist and a ~3 day window,
/// and auto-matches guide channels to playlist channels by id, then by normalised name.
/// </summary>
public static class EpgService
{
    /// <summary>How far back the guide keeps programmes for catch-up channels (and how far back you can scroll).</summary>
    public const int PastDays = 3;
    /// <summary>How far ahead the guide keeps programmes (the guide lets you look ~2 days ahead).</summary>
    const int FutureHours = 54;
    /// <summary>Descriptions longer than this are cut – the guide only shows two lines anyway.</summary>
    const int MaxDesc = 600;

    static readonly Regex PrefixRx = new(
        @"^\s*(?:\[[^\]]{1,14}\]|\([^)]{1,14}\)|\|[^|]{1,14}\||[a-z]{2,4}\s*[:|]|[A-Z]{2,3}\s+-\s+)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex QualityRx = new(
        @"\b(?:uhd|fhd|hd|sd|4k|8k|hevc|h\.?265|h\.?264|1080[pi]?|720p|2160p|50fps|60fps|hdr|raw|vip|backup|multi|ᴴᴰ|ʰᵈ)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex LooseRx = new(@"\b(?:uk|us|usa|ie|ca|au|tv|channel|the|east|west)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex IdSuffixRx = new(@"\.[a-z]{2,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex NonAlnumRx = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Downloads the guide to a file, on a background thread (it can be 100 MB+).</summary>
    public static Task DownloadAsync(string url, string dest, CancellationToken ct) =>
        Task.Run(() => DataService.TimeAsync("guide: downloading", async () => { await DownloadCoreAsync(url, dest, ct); return 0; }), ct);

    static async Task DownloadCoreAsync(string url, string dest, CancellationToken ct)
    {
        var tmp = dest + ".part";
        if (File.Exists(url)) { File.Copy(url, dest, true); return; } // a guide file on this PC
        using (var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(tmp);
            await src.CopyToAsync(fs, ct);
        }
        if (new FileInfo(tmp).Length < 64) { File.Delete(tmp); throw new InvalidOperationException("the guide was empty"); }
        File.Move(tmp, dest, true);
    }

    /// <summary>Guide addresses from a box that may hold several (one per line, or separated by commas/spaces).</summary>
    public static List<string> SplitUrls(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        foreach (var part in Regex.Split(s, @"[\s,;|]+(?=https?://|[a-zA-Z]:\\|/)|[\r\n]+"))
        {
            var u = part.Trim().TrimEnd(',', ';', '|').Trim();
            if (u.Length > 0) list.Add(u);
        }
        return list;
    }

    /// <summary>
    /// Downloads every guide and combines them into one file, in order: a guide channel that an earlier guide
    /// already has is skipped in later ones, so the first guide wins and later ones only fill in what it lacks.
    /// A guide that fails to download is skipped (the others still work); it fails only if none download.
    /// </summary>
    public static Task DownloadAllAsync(IReadOnlyList<string> urls, string dest, CancellationToken ct)
    {
        if (urls.Count == 1) return DownloadAsync(urls[0], dest, ct);
        return Task.Run(() => DataService.TimeAsync("guide: downloading", async () =>
        {
            var parts = new List<string>();
            Exception first = null;
            try
            {
                for (int i = 0; i < urls.Count; i++)
                {
                    var part = dest + ".src" + i;
                    try { await DownloadCoreAsync(urls[i], part, ct); parts.Add(part); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error(ex); first ??= ex; }
                }
                if (parts.Count == 0) throw first ?? new InvalidOperationException("no guide could be downloaded");
                if (parts.Count == 1) File.Move(parts[0], dest, true);
                else Merge(parts, dest);
            }
            finally { foreach (var p in parts) try { File.Delete(p); } catch { } }
            return 0;
        }), ct);
    }

    static Stream OpenGuide(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        int b1 = fs.ReadByte(), b2 = fs.ReadByte();
        fs.Position = 0;
        return (b1 == 0x1f && b2 == 0x8b) ? new GZipStream(fs, CompressionMode.Decompress) : fs;
    }

    static readonly XmlReaderSettings ReadSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true,
        IgnoreComments = true, IgnoreProcessingInstructions = true, CheckCharacters = false,
    };

    /// <summary>Combines several XMLTV guides into one: all channels first (the matching needs them up front), then programmes.</summary>
    internal static void Merge(IReadOnlyList<string> parts, string dest)
    {
        var tmp = dest + ".part";
        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // guide channel id → which guide has it
        using (var outFs = File.Create(tmp))
        using (var w = XmlWriter.Create(outFs, new XmlWriterSettings { Indent = false, CheckCharacters = false }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("tv");
            w.WriteAttributeString("generator-info-name", "Dingus");
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < parts.Count; i++)
                {
                    try
                    {
                        using var s = OpenGuide(parts[i]);
                        using var r = XmlReader.Create(s, ReadSettings);
                        r.MoveToContent();
                        while (!r.EOF)
                        {
                            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
                            if (r.LocalName == "channel")
                            {
                                if (pass == 1) { r.Skip(); continue; }
                                var id = r.GetAttribute("id");
                                if (string.IsNullOrWhiteSpace(id) || (owner.TryGetValue(id, out var o) && o != i)) { r.Skip(); continue; }
                                owner[id] = i;
                                XNode.ReadFrom(r).WriteTo(w);
                                continue;
                            }
                            if (r.LocalName == "programme")
                            {
                                if (pass == 0) { r.Skip(); continue; }
                                var cid = r.GetAttribute("channel");
                                if (cid == null || (owner.TryGetValue(cid, out var o) && o != i)) { r.Skip(); continue; }
                                XNode.ReadFrom(r).WriteTo(w);
                                continue;
                            }
                            r.Read();
                        }
                    }
                    catch (XmlException ex) { Log.Error(ex); }        // a broken guide: keep what was read
                    catch (InvalidDataException ex) { Log.Error(ex); }
                }
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        File.Move(tmp, dest, true);
    }

    // ---------------------------------------------------------------- the saved, matched guide

    /// <summary>
    /// The guide for these channels: from the saved, already-matched copy when nothing has changed (a fraction of a
    /// second), otherwise read and matched from the provider's guide file (seconds for a big guide) and saved for
    /// next time. The saved copy is used only while it's for the same guide file, channels and settings, and less
    /// than six hours old (so "what's on now" and the guide's time window stay right).
    /// </summary>
    public static EpgData LoadOrParse(string path, IReadOnlyList<Channel> channels, int shiftMinutes, IReadOnlyDictionary<string, string> overrides = null)
    {
        if (!File.Exists(path) || channels.Count == 0) return Parse(path, channels, shiftMinutes, overrides);
        var key = CacheKey(path, channels, shiftMinutes, overrides);
        var cache = path + ".matched";
        var saved = DataService.Time("guide: loading the saved copy", () => LoadCache(cache, key));
        if (saved != null) return saved;
        var data = DataService.Time("guide: reading and matching", () => Parse(path, channels, shiftMinutes, overrides));
        _ = Task.Run(() => DataService.Time("guide: saving a copy", () => SaveCache(cache, key, data, path)));
        return data;
    }

    const string CacheMagic = "DingusGuide3"; // 3: smarter matching (re-match guides saved by older versions)

    static string CacheKey(string path, IReadOnlyList<Channel> channels, int shiftMinutes, IReadOnlyDictionary<string, string> overrides)
    {
        var fi = new FileInfo(path);
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string v) { h.AppendData(System.Text.Encoding.UTF8.GetBytes(v ?? "")); h.AppendData(new byte[] { 0 }); }
        Add(fi.Length.ToString()); Add(fi.LastWriteTimeUtc.Ticks.ToString()); Add(shiftMinutes.ToString());
        foreach (var c in channels) { Add(c.Id); Add(c.Name); Add(c.EpgId); Add(c.HasArchive ? c.ArchiveDays.ToString() : "-"); }
        if (overrides != null) foreach (var kv in overrides.OrderBy(k => k.Key, StringComparer.Ordinal)) { Add(kv.Key); Add(kv.Value); }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    static void SaveCache(string cache, string key, EpgData d, string guidePath)
    {
        if (d.Descs == null) return;
        var tmp = cache + ".tmp";
        try
        {
            // Strings (titles, genres, ids) are stored once and referred to by number.
            var strings = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            int S(string v)
            {
                if (v == null) return -1;
                if (!index.TryGetValue(v, out var i)) { i = strings.Count; strings.Add(v); index[v] = i; }
                return i;
            }
            // One programme list per guide channel (versions of a channel share it).
            var lists = new Dictionary<List<Programme>, int>(ReferenceEqualityComparer.Instance);
            var listOrder = new List<List<Programme>>();
            foreach (var l in d.ByChannel.Values)
                if (!lists.ContainsKey(l)) { lists[l] = listOrder.Count; listOrder.Add(l); }
            // Programmes can be shared between lists too – each is written once, lists refer to it.
            var progs = new Dictionary<Programme, int>(ReferenceEqualityComparer.Instance);
            var progOrder = new List<Programme>();
            foreach (var l in listOrder)
                foreach (var p in l)
                    if (!progs.ContainsKey(p)) { progs[p] = progOrder.Count; progOrder.Add(p); }
            foreach (var p in progOrder) { S(p.Title); S(p.Genre); }

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(fs, System.Text.Encoding.UTF8))
            {
                w.Write(CacheMagic); w.Write(key); w.Write(DateTime.UtcNow.Ticks); w.Write(System.IO.Path.GetFileName(d.Descs.Path));
                // strings referred to below need to be in the table first
                foreach (var kv in d.NowTitles) { S(kv.Key); S(kv.Value); }
                foreach (var (id, name) in d.Sources) { S(id); S(name); }
                foreach (var kv in d.Icons) { S(kv.Key); S(kv.Value); }
                foreach (var kv in d.MatchedTo) { S(kv.Key); S(kv.Value); }
                foreach (var k in d.ByChannel.Keys) S(k);

                w.Write(strings.Count); foreach (var v in strings) w.Write(v);
                w.Write(progOrder.Count);
                foreach (var p in progOrder)
                {
                    w.Write(p.Start.ToBinary()); w.Write(p.End.ToBinary());
                    w.Write(S(p.Title)); w.Write(S(p.Genre)); w.Write(p.DescOffset); w.Write(p.DescLength);
                }
                w.Write(listOrder.Count);
                foreach (var l in listOrder) { w.Write(l.Count); foreach (var p in l) w.Write(progs[p]); }
                w.Write(d.ByChannel.Count); foreach (var kv in d.ByChannel) { w.Write(S(kv.Key)); w.Write(lists[kv.Value]); }
                w.Write(d.NowTitles.Count); foreach (var kv in d.NowTitles) { w.Write(S(kv.Key)); w.Write(S(kv.Value)); }
                w.Write(d.Sources.Count); foreach (var (id, name) in d.Sources) { w.Write(S(id)); w.Write(S(name)); }
                w.Write(d.Icons.Count); foreach (var kv in d.Icons) { w.Write(S(kv.Key)); w.Write(S(kv.Value)); }
                w.Write(d.MatchedTo.Count); foreach (var kv in d.MatchedTo) { w.Write(S(kv.Key)); w.Write(S(kv.Value)); }
            }
            File.Move(tmp, cache, true);
            // Descriptions from earlier guides aren't needed any more (one still open stays readable until it closes).
            var keep = d.Descs.Path;
            foreach (var f in Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(guidePath)!, System.IO.Path.GetFileName(guidePath) + ".*.desc"))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
        }
        catch (Exception ex) { Log.Error(ex); try { File.Delete(tmp); } catch { } }
    }

    static EpgData LoadCache(string cache, string key)
    {
        if (!File.Exists(cache)) return null;
        try
        {
            using var fs = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8);
            if (r.ReadString() != CacheMagic || r.ReadString() != key) return null;
            var made = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            if (DateTime.UtcNow - made > TimeSpan.FromHours(6) || made > DateTime.UtcNow.AddMinutes(5)) return null;
            var descPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(cache)!, r.ReadString());
            if (!File.Exists(descPath)) return null;

            var strings = new string[r.ReadInt32()];
            for (int i = 0; i < strings.Length; i++) strings[i] = r.ReadString();
            string Str(int i) => i < 0 ? null : strings[i];

            var store = DescStore.Open(descPath);
            var data = new EpgData { Descs = store };
            var progs = new Programme[r.ReadInt32()];
            for (int i = 0; i < progs.Length; i++)
            {
                var p = new Programme { Start = DateTime.FromBinary(r.ReadInt64()), End = DateTime.FromBinary(r.ReadInt64()) };
                p.Title = Str(r.ReadInt32()) ?? ""; p.Genre = Str(r.ReadInt32());
                p.DescOffset = r.ReadInt64(); p.DescLength = r.ReadInt32();
                if (p.DescLength > 0) p.Store = store;
                progs[i] = p;
            }
            var lists = new List<Programme>[r.ReadInt32()];
            for (int i = 0; i < lists.Length; i++)
            {
                int n = r.ReadInt32();
                var l = new List<Programme>(n);
                for (int j = 0; j < n; j++) l.Add(progs[r.ReadInt32()]);
                lists[i] = l;
            }
            for (int i = 0, n = r.ReadInt32(); i < n; i++) data.ByChannel[Str(r.ReadInt32())] = lists[r.ReadInt32()];
            for (int i = 0, n = r.ReadInt32(); i < n; i++) data.NowTitles[Str(r.ReadInt32())] = Str(r.ReadInt32());
            for (int i = 0, n = r.ReadInt32(); i < n; i++) data.Sources.Add((Str(r.ReadInt32()), Str(r.ReadInt32())));
            for (int i = 0, n = r.ReadInt32(); i < n; i++) data.Icons[Str(r.ReadInt32())] = Str(r.ReadInt32());
            for (int i = 0, n = r.ReadInt32(); i < n; i++) data.MatchedTo[Str(r.ReadInt32())] = Str(r.ReadInt32());
            return data;
        }
        catch (Exception ex) { Log.Error(ex); return null; } // damaged or from an older Dingus: read the guide again
    }

    /// <param name="overrides">Your own matches (playlist channel id → guide channel id; "" = no guide).</param>
    public static EpgData Parse(string path, IReadOnlyList<Channel> channels, int shiftMinutes, IReadOnlyDictionary<string, string> overrides = null)
    {
        var data = new EpgData();
        if (!File.Exists(path) || channels.Count == 0) return data;
        var store = new DescStore(path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".desc", keep: true);
        data.Descs = store;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        int b1 = fs.ReadByte(), b2 = fs.ReadByte();
        fs.Position = 0;
        Stream s = (b1 == 0x1f && b2 == 0x8b) ? new GZipStream(fs, CompressionMode.Decompress) : fs;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
        };

        var xmlNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var xmlIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<Channel>> map = null;
        Dictionary<string, int> shifts = null;
        var now = DateTime.Now;
        var minT = now.AddHours(-6);
        var maxT = now.AddHours(FutureHours);
        // The same titles ("News", "Live Premier League", …) repeat thousands of times – keep one copy of each.
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        // Channels with catch-up keep their past programmes for the whole catch-up window (max 7 days).
        int maxArchive = channels.Where(c => c.HasArchive).Select(c => Math.Min(PastDays, c.ArchiveDays > 0 ? c.ArchiveDays : 2)).DefaultIfEmpty(0).Max();
        var archiveMinT = now.AddDays(-maxArchive);

        try
        {
            using var reader = XmlReader.Create(s, settings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "channel")
                    {
                        var el = (XElement)XNode.ReadFrom(reader);
                        var id = (string)el.Attribute("id");
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            if (!xmlNames.TryGetValue(id, out var names)) xmlNames[id] = names = new List<string>();
                            foreach (var dn in el.Elements("display-name"))
                                if (!string.IsNullOrWhiteSpace(dn.Value)) names.Add(dn.Value.Trim());
                            var icon = (string)el.Element("icon")?.Attribute("src");
                            if (!string.IsNullOrWhiteSpace(icon)) xmlIcons[id] = icon;
                        }
                        continue;
                    }
                    if (reader.LocalName == "programme")
                    {
                        map ??= BuildMap(xmlNames, channels, overrides, out shifts);
                        var cid = reader.GetAttribute("channel");
                        if (cid == null) { reader.Skip(); continue; }
                        if (!map.TryGetValue(cid, out var targets))
                        {
                            // Not used by any channel: just note what's on now (for matching by hand).
                            if (!data.NowTitles.ContainsKey(cid))
                            {
                                var a = ParseTime(reader.GetAttribute("start"), shiftMinutes);
                                var b = ParseTime(reader.GetAttribute("stop"), shiftMinutes);
                                if (a <= now && b > now)
                                {
                                    var pe = (XElement)XNode.ReadFrom(reader);
                                    data.NowTitles[cid] = Pool(pool, pe.Element("title")?.Value.Trim() ?? "");
                                    continue;
                                }
                            }
                            reader.Skip();
                            continue;
                        }
                        var start = ParseTime(reader.GetAttribute("start"), shiftMinutes);
                        var stop = ParseTime(reader.GetAttribute("stop"), shiftMinutes);
                        if (start == null || stop == null || stop <= start || start >= maxT) { reader.Skip(); continue; }
                        if (stop <= minT && (maxArchive == 0 || start < archiveMinT || !KeepsPast(targets, start.Value, now)))
                        { reader.Skip(); continue; }

                        var el = (XElement)XNode.ReadFrom(reader);
                        var p = new Programme
                        {
                            Start = start.Value,
                            End = stop.Value,
                            Title = Pool(pool, el.Element("title")?.Value.Trim() ?? ""),
                            Genre = el.Element("category") is { } cat ? Pool(pool, cat.Value.Trim()) : null,

                        };
                        var desc = TrimDesc(el.Element("desc")?.Value);
                        // Programme artwork, if the guide has it: kept on disk next to the description (no extra memory).
                        var img = (string)el.Element("icon")?.Attribute("src");
                        if (!string.IsNullOrWhiteSpace(img) && img.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            desc = (desc ?? "") + Programme.ImageSeparator + img.Trim();
                        if (desc != null)
                        {
                            var (off, len) = store.Add(desc);
                            p.Store = store; p.DescOffset = off; p.DescLength = len;
                        }
                        foreach (var ch in targets)
                        {
                            if (!data.ByChannel.TryGetValue(ch.Id, out var list)) data.ByChannel[ch.Id] = list = new List<Programme>();
                            // "+1" channel using the main channel's guide: the same programme, an hour later.
                            list.Add(shifts.TryGetValue(ch.Id, out var sh) ? Shifted(p, sh) : p);
                        }
                        if (p.Start <= now && p.End > now) data.NowTitles.TryAdd(cid, p.Title);
                        continue;
                    }
                }
                reader.Read();
            }
        }
        catch (XmlException ex) { Log.Error(ex); } // truncated/odd file – keep what was read
        catch (InvalidDataException ex) { Log.Error(ex); } // broken gzip

        map ??= BuildMap(xmlNames, channels, overrides, out shifts);

        foreach (var list in data.ByChannel.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            // drop duplicates / overlaps that some providers emit
            for (int i = list.Count - 1; i > 0; i--)
                if (list[i].Start < list[i - 1].End && list[i].Start == list[i - 1].Start) list.RemoveAt(i);
        }

        foreach (var (xmlId, targets) in map)
        {
            foreach (var ch in targets) data.MatchedTo[ch.Id] = xmlId;
            if (xmlIcons.TryGetValue(xmlId, out var icon))
                foreach (var ch in targets) data.Icons[ch.Id] = icon;
        }
        foreach (var (id, names) in xmlNames)
            data.Sources.Add((id, names.Count > 0 ? names[0] : id));
        data.Sources.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        foreach (var list in data.ByChannel.Values) list.TrimExcess();
        store.Seal();
        return data;
    }

    static Programme Shifted(Programme p, int minutes) => new()
    {
        Start = p.Start.AddMinutes(minutes), End = p.End.AddMinutes(minutes), Title = p.Title, Genre = p.Genre,
        Store = p.Store, DescOffset = p.DescOffset, DescLength = p.DescLength,
    };

    static string Pool(Dictionary<string, string> pool, string s)
    {
        if (s.Length == 0) return "";
        if (pool.TryGetValue(s, out var existing)) return existing;
        pool[s] = s;
        return s;
    }

    static string TrimDesc(string d)
    {
        if (string.IsNullOrWhiteSpace(d)) return null;
        d = d.Trim();
        if (d.Length <= MaxDesc) return d;
        int cut = d.LastIndexOf(' ', MaxDesc);
        return d.Substring(0, cut > MaxDesc / 2 ? cut : MaxDesc) + "…";
    }

    static bool KeepsPast(List<Channel> targets, DateTime start, DateTime now)
    {
        foreach (var c in targets)
            if (c.HasArchive && start >= now.AddDays(-Math.Min(PastDays, c.ArchiveDays > 0 ? c.ArchiveDays : 2))) return true;
        return false;
    }

    // ---------------------------------------------------------------- matching channels to the guide
    //
    // Tried in order, stopping at the first that finds something:
    //  1. your own match (from "pick guide channel")
    //  2. the playlist's guide id (tvg-id / epg_channel_id)
    //  3. the cleaned-up name ("UK: Channel One HD" = "Channel One")
    //  4. a looser name (also ignores "TV", "the", country words, and treats "One" as "1")
    //  5. a close-enough name (small spelling differences), only when it's clearly the best fit and the
    //     numbers in the name agree, so "Sports 1" never lands on "Sports 2"
    //  6. "+1" channels with no guide of their own: the main channel's guide, an hour later
    // When several guide channels share a name (e.g. a UK and an Irish feed), the one from the playlist
    // channel's country ("UK: …") is picked.

    static Dictionary<string, List<Channel>> BuildMap(Dictionary<string, List<string>> xmlNames, IReadOnlyList<Channel> channels,
                                                      IReadOnlyDictionary<string, string> overrides) =>
        BuildMap(xmlNames, channels, overrides, out _);

    internal static Dictionary<string, List<Channel>> BuildMap(Dictionary<string, List<string>> xmlNames, IReadOnlyList<Channel> channels,
                                                      IReadOnlyDictionary<string, string> overrides, out Dictionary<string, int> shifts)
    {
        shifts = new Dictionary<string, int>();
        var byName = new Dictionary<string, List<string>>();
        var byLoose = new Dictionary<string, List<string>>();
        var fuzzy = new FuzzyIndex();
        foreach (var (id, names) in xmlNames)
        {
            foreach (var n in names)
            {
                AddKey(byName, Norm(n), id);
                AddKey(byLoose, Loose(n), id);
                fuzzy.Add(id, n);
            }
            AddKey(byName, Norm(IdSuffixRx.Replace(id, "")), id);
        }

        string ByName(string name, string cc) =>
            Find(byName, Norm(name), cc) ?? Find(byLoose, Loose(name), cc);

        var map = new Dictionary<string, List<Channel>>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<Channel>();
        void Put(Channel ch, string x)
        {
            if (!map.TryGetValue(x, out var list)) map[x] = list = new List<Channel>();
            list.Add(ch);
        }

        foreach (var ch in channels)
        {
            string x = null;
            if (overrides != null && overrides.TryGetValue(ch.Id, out var forced))
            {
                if (forced.Length == 0) continue;                 // you said "no guide for this channel"
                if (xmlNames.ContainsKey(forced)) x = forced;     // your match (if the guide still has it)
            }
            var cc = Country(ch.Name);
            if (x == null && !string.IsNullOrWhiteSpace(ch.EpgId))
            {
                if (xmlNames.ContainsKey(ch.EpgId)) x = ch.EpgId;
                else x = Find(byName, Norm(IdSuffixRx.Replace(ch.EpgId, "")), cc ?? IdCountry(ch.EpgId));
            }
            x ??= ByName(ch.Name, cc);
            if (x != null) Put(ch, x); else unmatched.Add(ch);
        }

        foreach (var ch in unmatched)
        {
            var cc = Country(ch.Name);
            var x = fuzzy.Best(ch.Name, cc);
            int shift = 0;
            if (x == null && TimeshiftOf(ch.Name, out var hours, out var baseName))
            {
                x = ByName(baseName, cc) ?? fuzzy.Best(baseName, cc);
                if (x != null) shift = hours * 60;
            }
            if (x == null) continue;
            Put(ch, x);
            if (shift != 0) shifts[ch.Id] = shift;
        }
        return map;
    }

    static void AddKey(Dictionary<string, List<string>> d, string key, string id)
    {
        if (key.Length == 0) return;
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<string>(1);
        if (!l.Contains(id)) l.Add(id);
    }

    static string Find(Dictionary<string, List<string>> d, string key, string cc)
    {
        if (key.Length == 0 || !d.TryGetValue(key, out var l)) return null;
        if (cc != null && l.Count > 1)
            foreach (var id in l)
                if (IdCountry(id) == cc) return id;
        return l[0];
    }

    static readonly Regex CountryRx = new(@"^\s*[\[\(|]?\s*([a-z]{2,3})\s*(?:[\]\)|:]|\s-\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex TimeshiftRx = new(@"(?:\+\s*([1-9])|\bplus\s*([1-9]))(?!\d)(?:\s*(?:h|hr|hrs|hour|hours)\b)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex NumberWordRx = new(@"\b(?:one|two|three|four|five|six|seven|eight|nine|ten)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly string[] NumberWords = { "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

    /// <summary>The country a playlist channel is labelled with ("UK: …", "[IE] …", "US | …"), as a guide id would end (".uk").</summary>
    internal static string Country(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var m = CountryRx.Match(name);
        if (!m.Success) return null;
        var c = m.Groups[1].Value.ToLowerInvariant();
        return c switch { "gb" => "uk", "usa" => "us", "eng" => "uk", _ => c.Length == 2 ? c : null };
    }

    /// <summary>"Channel1.uk" → "uk".</summary>
    static string IdCountry(string id)
    {
        var m = IdSuffixRx.Match(id ?? "");
        if (!m.Success) return null;
        var c = m.Value.Substring(1).ToLowerInvariant();
        return c == "gb" ? "uk" : c;
    }

    /// <summary>"Channel Four +1" → 1 hour, "Channel Four".</summary>
    internal static bool TimeshiftOf(string name, out int hours, out string baseName)
    {
        hours = 0; baseName = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var m = TimeshiftRx.Match(name);
        if (!m.Success) return false;
        hours = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        baseName = (name.Substring(0, m.Index) + " " + name.Substring(m.Index + m.Length)).Trim();
        return baseName.Length > 0;
    }

    /// <summary>"UK: Channel One HD" → "channelone"; "Sports Main+1" → "sportsmainplus1".</summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = PrefixRx.Replace(s, "");
        s = PrefixRx.Replace(s, "");
        s = s.Replace("&", " and ").Replace("+1", " plus1 ").Replace("+", " plus ");
        s = QualityRx.Replace(s.ToLowerInvariant(), " ");
        return NonAlnumRx.Replace(s, "");
    }

    static string Loose(string s) => string.Concat(Words(s));

    /// <summary>The words of a name with prefixes, quality tags and filler words removed, numbers as digits.</summary>
    internal static string[] Words(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        s = PrefixRx.Replace(s, "");
        s = PrefixRx.Replace(s, "");
        s = s.Replace("&", " and ").Replace("+1", " plus1 ");
        s = QualityRx.Replace(s.ToLowerInvariant(), " ");
        s = LooseRx.Replace(s, " ");
        s = NumberWordRx.Replace(s, m => (Array.IndexOf(NumberWords, m.Value.ToLowerInvariant()) + 1).ToString());
        return NonAlnumRx.Split(s).Where(w => w.Length > 0).ToArray();
    }

    /// <summary>Finds the guide channel whose name is closest to a playlist channel's (for small spelling differences).</summary>
    sealed class FuzzyIndex
    {
        const double MinScore = 0.80, MinLead = 0.04;
        const int CommonWord = 300; // words shared by this many guide channels ("sports", "news") don't pick candidates

        readonly List<(string Id, string Key, string Digits, string Country)> _entries = new();
        readonly Dictionary<string, List<int>> _byWord = new();
        readonly HashSet<string> _seen = new();

        public void Add(string id, string name)
        {
            var words = Words(name);
            var key = string.Concat(words);
            if (key.Length < 3 || !_seen.Add(id + "\n" + key)) return;
            int i = _entries.Count;
            _entries.Add((id, key, Digits(key), IdCountry(id)));
            foreach (var w in words.Distinct())
            {
                if (!_byWord.TryGetValue(w, out var l)) _byWord[w] = l = new List<int>();
                l.Add(i);
            }
        }

        public string Best(string name, string cc)
        {
            var words = Words(name);
            var key = string.Concat(words);
            if (key.Length < 3) return null;
            var digits = Digits(key);
            var grams = Bigrams(key);
            var tried = new HashSet<int>();
            string best = null, second = null;
            double bestScore = 0, secondScore = 0;
            foreach (var w in words.Distinct())
            {
                if (!_byWord.TryGetValue(w, out var l) || l.Count > CommonWord) continue;
                foreach (var i in l)
                {
                    if (!tried.Add(i)) continue;
                    var e = _entries[i];
                    if (e.Digits != digits) continue;             // "Sports 1" ≠ "Sports 2"
                    double score = Dice(grams, Bigrams(e.Key));
                    if (cc != null && e.Country == cc) score += 0.03;
                    if (score > bestScore)
                    {
                        if (best != null && best != e.Id) { second = best; secondScore = bestScore; }
                        best = e.Id; bestScore = score;
                    }
                    else if (e.Id != best && score > secondScore) { second = e.Id; secondScore = score; }
                }
            }
            if (best == null || bestScore < MinScore) return null;
            if (second != null && bestScore - secondScore < MinLead) return null; // two equally good fits: don't guess
            return best;
        }

        static string Digits(string key) => new(key.Where(char.IsDigit).ToArray());

        static Dictionary<int, int> Bigrams(string s)
        {
            var d = new Dictionary<int, int>(s.Length);
            for (int i = 0; i + 1 < s.Length; i++)
            {
                int g = (s[i] << 16) | s[i + 1];
                d[g] = d.TryGetValue(g, out var n) ? n + 1 : 1;
            }
            return d;
        }

        static double Dice(Dictionary<int, int> a, Dictionary<int, int> b)
        {
            int total = 0, common = 0;
            foreach (var n in a.Values) total += n;
            foreach (var n in b.Values) total += n;
            if (total == 0) return 0;
            foreach (var (g, n) in a)
                if (b.TryGetValue(g, out var m)) common += Math.Min(n, m);
            return 2.0 * common / total;
        }
    }

    /// <summary>XMLTV time: "20261015200000 +0100".</summary>
    static DateTime? ParseTime(string s, int shiftMinutes)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 14) return null;
        if (!DateTime.TryParseExact(s.AsSpan(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return null;
        var offset = TimeSpan.Zero;
        var rest = s.Substring(14).Trim().Replace(":", "");
        if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-') &&
            int.TryParse(rest.AsSpan(1, 2), out var hh) && int.TryParse(rest.AsSpan(3, 2), out var mm))
        {
            offset = new TimeSpan(hh, mm, 0);
            if (rest[0] == '-') offset = -offset;
        }
        var utc = DateTime.SpecifyKind(dt - offset, DateTimeKind.Utc);
        return utc.ToLocalTime().AddMinutes(shiftMinutes);
    }
}
