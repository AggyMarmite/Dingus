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

    public DescStore(string path)
    {
        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                             1 << 16, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
    }

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

    public static async Task DownloadAsync(string url, string dest, CancellationToken ct)
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

    /// <param name="overrides">Your own matches (playlist channel id → guide channel id; "" = no guide).</param>
    public static EpgData Parse(string path, IReadOnlyList<Channel> channels, int shiftMinutes, IReadOnlyDictionary<string, string> overrides = null)
    {
        var data = new EpgData();
        if (!File.Exists(path) || channels.Count == 0) return data;
        var store = new DescStore(path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".desc");
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
                        map ??= BuildMap(xmlNames, channels, overrides);
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
                        if (desc != null)
                        {
                            var (off, len) = store.Add(desc);
                            p.Store = store; p.DescOffset = off; p.DescLength = len;
                        }
                        foreach (var ch in targets)
                        {
                            if (!data.ByChannel.TryGetValue(ch.Id, out var list)) data.ByChannel[ch.Id] = list = new List<Programme>();
                            list.Add(p);
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

        map ??= BuildMap(xmlNames, channels, overrides);

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

    static Dictionary<string, List<Channel>> BuildMap(Dictionary<string, List<string>> xmlNames, IReadOnlyList<Channel> channels,
                                                      IReadOnlyDictionary<string, string> overrides)
    {
        var byName = new Dictionary<string, string>();
        var byLoose = new Dictionary<string, string>();
        foreach (var (id, names) in xmlNames)
        {
            foreach (var n in names)
            {
                AddKey(byName, Norm(n), id);
                AddKey(byLoose, Loose(n), id);
            }
            AddKey(byName, Norm(IdSuffixRx.Replace(id, "")), id);
        }

        var map = new Dictionary<string, List<Channel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in channels)
        {
            string x = null;
            if (overrides != null && overrides.TryGetValue(ch.Id, out var forced))
            {
                if (forced.Length == 0) continue;                 // you said "no guide for this channel"
                if (xmlNames.ContainsKey(forced)) x = forced;     // your match (if the guide still has it)
            }
            if (x == null && !string.IsNullOrWhiteSpace(ch.EpgId))
            {
                if (xmlNames.ContainsKey(ch.EpgId)) x = ch.EpgId;
                else x = Find(byName, Norm(IdSuffixRx.Replace(ch.EpgId, "")));
            }
            x ??= Find(byName, Norm(ch.Name)) ?? Find(byLoose, Loose(ch.Name));
            if (x == null) continue;
            if (!map.TryGetValue(x, out var list)) map[x] = list = new List<Channel>();
            list.Add(ch);
        }
        return map;
    }

    static void AddKey(Dictionary<string, string> d, string key, string id)
    {
        if (key.Length > 0) d.TryAdd(key, id);
    }

    static string Find(Dictionary<string, string> d, string key) =>
        key.Length > 0 && d.TryGetValue(key, out var v) ? v : null;

    /// <summary>"UK: BBC One HD" → "bbcone"; "Sky Sports+1" → "skysportsplus1".</summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = PrefixRx.Replace(s, "");
        s = PrefixRx.Replace(s, "");
        s = s.Replace("&", " and ").Replace("+1", " plus1 ").Replace("+", " plus ");
        s = QualityRx.Replace(s.ToLowerInvariant(), " ");
        return NonAlnumRx.Replace(s, "");
    }

    static string Loose(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = PrefixRx.Replace(s, "");
        s = s.Replace("&", " and ").Replace("+1", " plus1 ");
        s = QualityRx.Replace(s.ToLowerInvariant(), " ");
        s = LooseRx.Replace(s, " ");
        return NonAlnumRx.Replace(s, "");
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
