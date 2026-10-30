using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dingus.Models;

namespace Dingus.Services;

public enum DownloadStatus { Queued, Downloading, Waiting, Paused, Done, Failed }

/// <summary>A film or episode saved to watch without a connection.</summary>
public sealed class Download
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    /// <summary>movie | episode | stremio</summary>
    public string Kind { get; set; }
    public string Title { get; set; }
    public string Subtitle { get; set; }
    public string Image { get; set; }
    /// <summary>The show, for episodes (files are kept in a folder per show).</summary>
    public string Series { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    /// <summary>The same key the player uses for "Continue watching", so a downloaded title picks up where you were.</summary>
    public string ResumeKey { get; set; }
    public string ItemId { get; set; }
    public string SeriesId { get; set; }
    public string Ext { get; set; }
    /// <summary>The address (it can hold your playlist login, so it's kept encrypted).</summary>
    public string UrlProtected { get; set; }
    /// <summary>From your playlist (counts towards its connection limit), or from Stremio / Real-Debrid.</summary>
    public string PlaylistId { get; set; }
    public string FilePath { get; set; }
    public long Bytes { get; set; }
    public long Total { get; set; }
    public DownloadStatus Status { get; set; }
    public string Error { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }

    [JsonIgnore] public double BytesPerSecond { get; set; }
    [JsonIgnore] public string WaitReason { get; set; }
    [JsonIgnore] public string PartPath => FilePath + ".part";
    [JsonIgnore] public double Fraction => Total > 0 ? Math.Clamp((double)Bytes / Total, 0, 1) : 0;
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Waiting;
}

/// <summary>
/// Downloads films and episodes (from your playlist, or Stremio / Real-Debrid sources) to watch offline. One at a time,
/// in the order you asked; each picks up where it stopped (after a dropped connection, a pause, or closing Dingus),
/// retries on its own when the server hiccups, checks there's room on the disk first, and waits while you're watching
/// if your plan only allows so many connections at once. Shared by the Windows and Linux apps.
/// </summary>
public static class DownloadService
{
    static List<Download> _items = new();
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static string FilePath => Path.Combine(AppPaths.Root, "downloads.json");
    static readonly HttpClient Client = MakeClient();
    static CancellationTokenSource _current;
    static Download _running;
    static readonly SemaphoreSlim Wake = new(0);
    static bool _started;

    /// <summary>Raised (off the window's thread) when a download is added, changes state or makes progress.</summary>
    public static event Action Changed;

    /// <summary>Set by the main window: true while you're watching something from your playlist (it uses a connection).</summary>
    public static Func<bool> WatchingPlaylist { get; set; } = () => false;

    static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(20), AutomaticDecompression = DecompressionMethods.None };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.21 LibVLC/3.0.21");
        return c;
    }

    /// <summary>Where downloads go (a folder on this computer, another drive or a network share).</summary>
    public static string Folder
    {
        get
        {
            var f = SettingsStore.Current.DownloadsFolder;
            return string.IsNullOrWhiteSpace(f)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Dingus Downloads")
                : f;
        }
    }

    public static IReadOnlyList<Download> All { get { lock (Gate) return _items.ToList(); } }

    // ------------------------------------------------------------------ start-up

    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            if (File.Exists(FilePath)) _items = JsonSerializer.Deserialize<List<Download>>(File.ReadAllText(FilePath), Opts) ?? new();
        }
        catch (Exception ex) { Log.Error(ex); _items = new(); }
        foreach (var d in _items)
        {
            if (d.Status is DownloadStatus.Downloading or DownloadStatus.Waiting) d.Status = DownloadStatus.Queued; // carry on where it was
            if (d.Status == DownloadStatus.Done && !File.Exists(d.FilePath)) { d.Status = DownloadStatus.Failed; d.Error = "The file isn't there any more."; }
            if (d.Status != DownloadStatus.Done) d.Bytes = File.Exists(d.PartPath) ? new FileInfo(d.PartPath).Length : 0;
        }
        Save();
        _ = Task.Run(WorkerAsync);
    }

    static void Save()
    {
        try
        {
            List<Download> copy;
            lock (Gate) copy = _items.ToList();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    static void Raise() { try { Changed?.Invoke(); } catch (Exception ex) { Log.Error(ex); } }

    // ------------------------------------------------------------------ what's downloaded

    /// <summary>The download for this title (any state), or null.</summary>
    public static Download Find(string resumeKey)
    {
        if (resumeKey == null) return null;
        lock (Gate) return _items.FirstOrDefault(d => d.ResumeKey == resumeKey && d.Status != DownloadStatus.Failed)
                            ?? _items.FirstOrDefault(d => d.ResumeKey == resumeKey);
    }

    /// <summary>The downloaded file to play instead of streaming, when there's a finished one.</summary>
    public static string LocalUrl(string resumeKey)
    {
        var d = Find(resumeKey);
        return d is { Status: DownloadStatus.Done } && File.Exists(d.FilePath) ? new Uri(d.FilePath).AbsoluteUri : null;
    }

    // ------------------------------------------------------------------ adding

    /// <summary>
    /// Adds a film or episode to the downloads (or returns the one already there). <paramref name="playlistId"/> is set
    /// for your playlist's titles, so they count towards its connection limit.
    /// </summary>
    public static Download Add(VodItem item, string playlistId, string series = null, int season = 0, int episode = 0, bool paused = false)
    {
        if (item?.Url == null) throw new InvalidOperationException("There's nothing to download for this one.");
        lock (Gate)
        {
            var existing = item.ResumeKey == null ? null : _items.FirstOrDefault(d => d.ResumeKey == item.ResumeKey);
            if (existing != null && existing.Status != DownloadStatus.Failed) return existing;
            if (existing != null) _items.Remove(existing); // a failed one: start again with the fresh address
            var d = new Download
            {
                Kind = item.Kind,
                Title = item.Title,
                Subtitle = item.Subtitle,
                Image = item.Image,
                Series = series,
                Season = season,
                Episode = episode,
                ResumeKey = item.ResumeKey,
                ItemId = item.ItemId,
                SeriesId = item.SeriesId,
                Ext = CleanExt(item.Ext) ?? ExtFromUrl(item.Url),
                UrlProtected = Secret.Protect(item.Url),
                PlaylistId = playlistId,
                Status = paused ? DownloadStatus.Paused : DownloadStatus.Queued,
            };
            d.FilePath = UniquePath(MakePath(d));
            _items.Add(d);
        }
        Save();
        Raise();
        Wake.Release();
        return Find(item.ResumeKey) ?? _items[^1];
    }

    static string CleanExt(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        ext = ext.Trim().TrimStart('.').ToLowerInvariant();
        return Regex.IsMatch(ext, "^[a-z0-9]{2,4}$") ? ext : null;
    }

    static string ExtFromUrl(string url)
    {
        try { return CleanExt(Path.GetExtension(new Uri(url).AbsolutePath)); } catch { return null; }
    }

    static string Safe(string s)
    {
        s = (s ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' })) s = s.Replace(c, ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '.');
        return s.Length == 0 ? "Untitled" : s.Length > 120 ? s[..120].Trim() : s;
    }

    /// <summary>"Film (2021).mkv", or "Show/Season 1/Show – S01E02 – Title.mkv".</summary>
    static string MakePath(Download d)
    {
        var ext = d.Ext ?? "mkv";
        if (d.Series != null && d.Season >= 0 && d.Episode > 0)
        {
            var show = Safe(Stremio.CleanTitle(d.Series));
            var epTitle = d.Subtitle != null && d.Subtitle.Contains("·") ? d.Subtitle[(d.Subtitle.LastIndexOf('·') + 1)..] : d.Subtitle;
            var name = $"{show} – S{d.Season:00}E{d.Episode:00}" + (string.IsNullOrWhiteSpace(epTitle) ? "" : " – " + Safe(epTitle));
            return Path.Combine(Folder, show, d.Season == 0 ? "Specials" : $"Season {d.Season}", Safe(name) + "." + ext);
        }
        return Path.Combine(Folder, Safe(Stremio.CleanTitle(d.Title)) + "." + ext);
    }

    static string UniquePath(string path)
    {
        lock (Gate)
        {
            string p = path;
            for (int i = 2; File.Exists(p) || File.Exists(p + ".part") || _items.Any(x => x.FilePath == p); i++)
                p = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
            return p;
        }
    }

    // ------------------------------------------------------------------ controls

    public static void Pause(Download d)
    {
        if (!d.IsActive) return;
        d.Status = DownloadStatus.Paused;
        if (_running == d) _current?.Cancel();
        Save();
        Raise();
    }

    public static void Resume(Download d)
    {
        if (d.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
        d.Status = DownloadStatus.Queued;
        d.Error = null;
        Save();
        Raise();
        Wake.Release();
    }

    /// <summary>Stops a download and removes it, with whatever was saved so far (or the finished file, when <paramref name="deleteFile"/>).</summary>
    public static void Remove(Download d, bool deleteFile = true)
    {
        lock (Gate) _items.Remove(d);
        if (_running == d) _current?.Cancel();
        if (deleteFile)
        {
            try { if (File.Exists(d.PartPath)) File.Delete(d.PartPath); } catch (Exception ex) { Log.Error(ex); }
            try { if (d.Status == DownloadStatus.Done && File.Exists(d.FilePath)) File.Delete(d.FilePath); } catch (Exception ex) { Log.Error(ex); }
        }
        Save();
        Raise();
    }

    /// <summary>Changes where new downloads go (ones already saved stay where they are).</summary>
    public static string SetFolder(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) { SettingsStore.Current.DownloadsFolder = null; SettingsStore.Save(); return null; }
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { return "Dingus can't use that folder: " + ex.Message; }
        SettingsStore.Current.DownloadsFolder = path;
        SettingsStore.Save();
        return null;
    }

    // ------------------------------------------------------------------ the work

    /// <summary>How many connections a playlist download would need right now, against the plan's limit.</summary>
    static string ConnectionLimitReason(Download d)
    {
        if (d.PlaylistId == null) return null;
        int max = RecordingService.MaxConnections;
        if (max <= 0) return null;
        int used = RecordingService.ActiveCount + (WatchingPlaylist() ? 1 : 0) + 1;
        return used > max
            ? max == 1 ? "Waiting – your plan allows one connection at a time, so it carries on when you stop watching."
                       : $"Waiting – your plan allows {max} connections at a time, and they're in use."
            : null;
    }

    static async Task WorkerAsync()
    {
        while (true)
        {
            List<Download> waiting;
            lock (Gate) waiting = _items.Where(d => d.Status is DownloadStatus.Queued or DownloadStatus.Waiting).ToList();
            if (waiting.Count == 0) { await Wake.WaitAsync(TimeSpan.FromSeconds(30)); continue; }

            // The first one that can go now (one from Stremio / Real-Debrid needn't wait for your playlist's connections).
            Download next = null;
            bool changed = false;
            foreach (var d in waiting)
            {
                var wait = ConnectionLimitReason(d);
                if (wait == null) { next = d; break; }
                if (d.Status != DownloadStatus.Waiting || d.WaitReason != wait) { d.Status = DownloadStatus.Waiting; d.WaitReason = wait; changed = true; }
            }
            if (changed) Raise();
            if (next == null) { await Wake.WaitAsync(TimeSpan.FromSeconds(10)); continue; }
            try { await RunAsync(next); }
            catch (Exception ex) { Log.Error(ex); Fail(next, "Something went wrong: " + ex.Message); }
        }
    }

    static async Task RunAsync(Download d)
    {
        _running = d;
        using var cts = _current = new CancellationTokenSource();
        int failures = 0;
        try
        {
            while (d.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Waiting)
            {
                d.Status = DownloadStatus.Downloading;
                d.WaitReason = null;
                Raise();
                try
                {
                    await TransferAsync(d, cts.Token);
                    d.Status = DownloadStatus.Done;
                    d.FinishedUtc = DateTime.UtcNow;
                    d.Error = null;
                    Save();
                    Raise();
                    return;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    if (d.Status == DownloadStatus.Downloading) d.Status = DownloadStatus.Queued; // paused for the connection limit
                    Save();
                    Raise();
                    return;
                }
                catch (WaitForConnectionException)
                {
                    Save(); // stays Waiting; the worker carries on with it when a connection's free
                    Raise();
                    return;
                }
                catch (FatalDownloadException ex)
                {
                    Fail(d, ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    // A dropped connection or a server hiccup: try again (from where it got to), waiting a bit longer each time.
                    failures++;
                    Log.Info($"Download {d.Title}: {ex.Message} (try {failures})");
                    if (failures > 6) { Fail(d, "The download kept stopping: " + Http.Friendly(ex) + ". Press Resume to try again."); return; }
                    d.Error = $"Connection lost – trying again ({failures} of 6)…";
                    Raise();
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * failures * failures)), cts.Token); }
                    catch (OperationCanceledException) { if (d.Status == DownloadStatus.Downloading) d.Status = DownloadStatus.Queued; Save(); Raise(); return; }
                }
            }
        }
        finally
        {
            _running = null;
            _current = null;
            d.BytesPerSecond = 0;
            // Cancelled (removed) while it was being written: tidy up what was saved, now the file's closed.
            bool removed;
            lock (Gate) removed = !_items.Contains(d);
            if (removed) try { if (File.Exists(d.PartPath)) File.Delete(d.PartPath); } catch (Exception ex) { Log.Error(ex); }
        }
    }

    static void Fail(Download d, string why)
    {
        d.Status = DownloadStatus.Failed;
        d.Error = why;
        Save();
        Raise();
    }

    sealed class FatalDownloadException : Exception
    {
        public FatalDownloadException(string m) : base(m) { }
    }

    sealed class WaitForConnectionException : Exception { }

    static async Task TransferAsync(Download d, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(d.FilePath)!);
        long have = File.Exists(d.PartPath) ? new FileInfo(d.PartPath).Length : 0;
        var url = Secret.Unprotect(d.UrlProtected);
        if (string.IsNullOrEmpty(url)) throw new FatalDownloadException("The download's address couldn't be read – start it again from its page.");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 && d.Total > 0 && have >= d.Total)
        {
            Finish(d);
            return;
        }
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Unauthorized)
            throw new FatalDownloadException(d.PlaylistId != null
                ? $"Your provider refused it ({(int)resp.StatusCode}) – it may not be available any more, or your plan's connections are in use."
                : $"The link has expired ({(int)resp.StatusCode}) – start the download again from its page.");
        if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"the server is busy ({(int)resp.StatusCode})");
        resp.EnsureSuccessStatusCode();

        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("text/") || type.Contains("json"))
            throw new FatalDownloadException("That address gives a web page, not a video – it can't be downloaded.");

        bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!partial && have > 0) have = 0; // the server doesn't do resuming: start again
        long total = resp.Content.Headers.ContentRange?.Length ?? (resp.Content.Headers.ContentLength is long len ? len + (partial ? have : 0) : 0);
        if (total > 0) d.Total = total;

        // Room on the disk for the rest (and a little to spare).
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(d.FilePath));
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\"))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (d.Total > 0 && free < d.Total - have + 200L * 1024 * 1024)
                    throw new FatalDownloadException($"Not enough space – it needs {Stremio.Size(d.Total - have)} and there's {Stremio.Size(free)} free in {Path.GetDirectoryName(d.FilePath)}.");
            }
        }
        catch (FatalDownloadException) { throw; }
        catch (Exception ex) { Log.Info("Free space check: " + ex.Message); }

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(d.PartPath, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true);
        var buf = new byte[1 << 18];
        d.Bytes = have;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long windowBytes = 0;
        var lastRaise = TimeSpan.Zero;
        var lastSave = TimeSpan.Zero;
        while (true)
        {
            // Stop if you've started watching and the plan's connections are all in use (it carries on afterwards).
            if (ConnectionLimitReason(d) is { } reason)
            {
                d.Status = DownloadStatus.Waiting;
                d.WaitReason = reason;
                await dst.FlushAsync(CancellationToken.None);
                throw new WaitForConnectionException();
            }
            // A read that stalls for a minute means the connection has gone quietly.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            int n;
            try { n = await src.ReadAsync(buf, stall.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("the connection stalled"); }
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            d.Bytes += n;
            windowBytes += n;
            var t = sw.Elapsed;
            if (t - lastRaise > TimeSpan.FromMilliseconds(700))
            {
                d.BytesPerSecond = windowBytes / (t - lastRaise).TotalSeconds;
                windowBytes = 0;
                lastRaise = t;
                if (d.Error != null) d.Error = null;
                Raise();
            }
            if (t - lastSave > TimeSpan.FromSeconds(10)) { lastSave = t; Save(); }
        }
        await dst.FlushAsync(ct);
        dst.Close();
        if (d.Total > 0 && d.Bytes < d.Total) throw new IOException($"the connection closed early ({Stremio.Size(d.Bytes)} of {Stremio.Size(d.Total)})");
        Finish(d);
    }

    static void Finish(Download d)
    {
        if (File.Exists(d.FilePath)) d.FilePath = UniquePath(d.FilePath);
        File.Move(d.PartPath, d.FilePath);
        d.Bytes = new FileInfo(d.FilePath).Length;
        if (d.Total <= 0) d.Total = d.Bytes;
    }

    // ------------------------------------------------------------------ for the page

    public static string StatusText(Download d) => d.Status switch
    {
        DownloadStatus.Done => $"Downloaded  ·  {Stremio.Size(d.Bytes)}",
        DownloadStatus.Downloading when d.Error != null => d.Error,
        DownloadStatus.Downloading => d.Total > 0
            ? $"{d.Fraction:P0}  ·  {Stremio.Size(d.Bytes)} of {Stremio.Size(d.Total)}" + (d.BytesPerSecond > 1024 ? $"  ·  {Speed(d.BytesPerSecond)}" + Eta(d) : "")
            : $"{Stremio.Size(d.Bytes)}" + (d.BytesPerSecond > 1024 ? $"  ·  {Speed(d.BytesPerSecond)}" : ""),
        DownloadStatus.Waiting => d.WaitReason ?? "Waiting…",
        DownloadStatus.Queued => d.Bytes > 0 ? $"Queued  ·  {Stremio.Size(d.Bytes)} so far" : "Queued",
        DownloadStatus.Paused => d.Total > 0 ? $"Paused at {d.Fraction:P0}" : "Paused",
        DownloadStatus.Failed => d.Error ?? "Didn't finish",
        _ => "",
    };

    static string Speed(double bps) => bps >= 1 << 20 ? $"{bps / (1 << 20):0.0} MB/s" : $"{bps / 1024:0} KB/s";

    static string Eta(Download d)
    {
        if (d.BytesPerSecond <= 0 || d.Total <= 0) return "";
        var left = TimeSpan.FromSeconds((d.Total - d.Bytes) / d.BytesPerSecond);
        return left.TotalHours >= 1 ? $"  ·  {(int)left.TotalHours} h {left.Minutes} min left" : left.TotalMinutes >= 1 ? $"  ·  {(int)Math.Ceiling(left.TotalMinutes)} min left" : "  ·  under a minute left";
    }

    /// <summary>The button text for a title's page: "Download", "Downloading 34%", "Downloaded"…</summary>
    public static string ButtonText(string resumeKey)
    {
        var d = Find(resumeKey);
        return d?.Status switch
        {
            null or DownloadStatus.Failed => "Download",
            DownloadStatus.Done => "Downloaded",
            DownloadStatus.Downloading when d.Total > 0 => $"Downloading {d.Fraction:P0}",
            DownloadStatus.Paused => "Download paused",
            _ => "Downloading…",
        };
    }

    /// <summary>The player item for a finished download (plays the file, keeps the same "Continue watching" place).</summary>
    public static VodItem ToVod(Download d) => new()
    {
        Kind = d.Kind,
        Title = d.Series ?? d.Title,
        Subtitle = d.Subtitle,
        Url = new Uri(d.FilePath).AbsoluteUri,
        Image = d.Image,
        ResumeKey = d.ResumeKey,
        ItemId = d.ItemId,
        SeriesId = d.SeriesId,
        Ext = d.Ext,
    };

    /// <summary>
    /// The Download buttons: adds a film or episode (an episode when <paramref name="episode"/> is set) and says what happened.
    /// Titles from your playlist count towards its connection limit; Stremio / Real-Debrid ones don't.
    /// </summary>
    public static (Download Download, string Message) Start(VodItem item, int season = 0, int episode = 0)
    {
        try
        {
            var playlist = item.Kind == "stremio" ? null : DataService.Instance.Active?.Id ?? "playlist";
            var d = Add(item, playlist, episode > 0 ? item.Title : null, season, episode);
            return (d, AddedMessage(d));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return (null, "Couldn't download that: " + (ex is InvalidOperationException ? ex.Message : Http.Friendly(ex)));
        }
    }

    /// <summary>The "Download season" button's text, from its episodes' keys.</summary>
    public static string SeasonText(IReadOnlyCollection<string> keys)
    {
        var ds = keys.Select(Find).Where(d => d != null && d.Status != DownloadStatus.Failed).ToList();
        if (ds.Count == 0 || keys.Count == 0) return "Download season";
        int done = ds.Count(d => d.Status == DownloadStatus.Done);
        if (done == keys.Count) return "Season downloaded";
        if (ds.Count == keys.Count || ds.Any(d => d.IsActive)) return $"Downloading season ({done} of {keys.Count})";
        return $"Download the rest ({keys.Count - ds.Count})";
    }

    /// <summary>What to tell you after pressing Download.</summary>
    public static string AddedMessage(Download d) => d.Status switch
    {
        DownloadStatus.Done => $"Already downloaded: {Label(d)}",
        DownloadStatus.Paused => $"{Label(d)} is paused in Downloads",
        _ => $"Downloading {Label(d)}  ·  see Downloads",
    };

    static string Label(Download d) => d.Series != null ? $"{Stremio.CleanTitle(d.Series)} S{d.Season:00}E{d.Episode:00}" : d.Title;

    /// <summary>Signals the worker to look again (watching stopped, so a waiting download can carry on).</summary>
    public static void Nudge() => Wake.Release();
}
