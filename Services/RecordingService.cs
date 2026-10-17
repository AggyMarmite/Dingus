using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Dingus.Models;

namespace Dingus.Services;

public enum RecordingStatus { Scheduled, Recording, Done, Partial, Failed, Missed, Cancelled }

/// <summary>A programme (or time slot) to record, being recorded, or recorded.</summary>
public class Recording
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string PlaylistId { get; set; }
    public string ChannelId { get; set; }
    public string ChannelName { get; set; }
    public string Title { get; set; }
    /// <summary>The programme's own times (local).</summary>
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>Minutes added before / after, in case the programme starts early or overruns.</summary>
    public int PadBefore { get; set; }
    public int PadAfter { get; set; }
    public RecordingStatus Status { get; set; }
    public string FilePath { get; set; }
    public long Bytes { get; set; }
    public string Error { get; set; }

    [JsonIgnore] public DateTime RecordFrom => Start.AddMinutes(-PadBefore);
    [JsonIgnore] public DateTime RecordUntil => End.AddMinutes(PadAfter);
    [JsonIgnore] public bool IsActive => Status == RecordingStatus.Recording;
    [JsonIgnore] public bool IsUpcoming => Status == RecordingStatus.Scheduled;
    [JsonIgnore] public bool IsFinished => Status is RecordingStatus.Done or RecordingStatus.Partial;
}

/// <summary>
/// Records live channels to files (MPEG-TS, playable in Dingus, VLC and most players). Each recording is a
/// separate connection to the provider, written straight to disk through a small buffer – memory stays flat.
/// Dingus has to be open (minimised is fine) for scheduled recordings to happen; the PC is kept awake while one runs.
/// </summary>
public static class RecordingService
{
    static List<Recording> _items = new();
    static readonly Dictionary<string, CancellationTokenSource> Running = new();
    static DispatcherTimer _timer;
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static string FilePath => Path.Combine(AppPaths.Root, "recordings.json");
    static readonly HttpClient Client = MakeClient();

    public static event Action Changed;

    public static IReadOnlyList<Recording> All
    {
        get { lock (_items) return _items.ToList(); }
    }

    public static int ActiveCount { get { lock (_items) return _items.Count(r => r.IsActive); } }

    /// <summary>Where recordings go (a folder on this PC, another drive, or a network share such as \\NAS\Recordings).</summary>
    public static string Folder
    {
        get
        {
            var f = SettingsStore.Current.RecordingsFolder;
            return string.IsNullOrWhiteSpace(f)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Dingus Recordings")
                : f;
        }
    }

    static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.None };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.21 LibVLC/3.0.21");
        return c;
    }

    // ------------------------------------------------------------------ start-up & schedule

    public static void Start()
    {
        try
        {
            if (File.Exists(FilePath))
                _items = JsonSerializer.Deserialize<List<Recording>>(File.ReadAllText(FilePath), Opts) ?? new();
        }
        catch (Exception ex) { Log.Error(ex); _items = new(); }

        // Dingus was closed (or crashed) while recording: keep what was saved.
        foreach (var r in _items.Where(r => r.IsActive))
        {
            r.Status = File.Exists(r.FilePath) && new FileInfo(r.FilePath).Length > 0 ? RecordingStatus.Partial : RecordingStatus.Failed;
            r.Error ??= "Dingus was closed during the recording.";
        }
        Save();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public static Recording Schedule(string channelId, string channelName, string title, DateTime start, DateTime end)
    {
        var s = SettingsStore.Current;
        var r = new Recording
        {
            PlaylistId = DataService.Instance.Active?.Id,
            ChannelId = channelId, ChannelName = channelName, Title = string.IsNullOrWhiteSpace(title) ? channelName : title,
            Start = start, End = end,
            PadBefore = start > DateTime.Now ? s.RecordPadBefore : 0,
            PadAfter = s.RecordPadAfter,
            Status = RecordingStatus.Scheduled,
        };
        lock (_items) _items.Add(r);
        Save();
        Changed?.Invoke();
        Tick(); // starts straight away if it's already on
        return r;
    }

    public static bool IsScheduled(string channelId, DateTime start)
    {
        lock (_items) return _items.Any(r => r.ChannelId == channelId && r.Start == start && (r.IsUpcoming || r.IsActive));
    }

    public static Recording Find(string channelId, DateTime start)
    {
        lock (_items) return _items.FirstOrDefault(r => r.ChannelId == channelId && r.Start == start && (r.IsUpcoming || r.IsActive));
    }

    /// <summary>Cancels a scheduled recording, or stops one that's running (what's recorded so far is kept).</summary>
    public static void Cancel(Recording r)
    {
        if (r.IsActive)
        {
            lock (Running) if (Running.TryGetValue(r.Id, out var cts)) cts.Cancel();
            r.Error = "Stopped early.";
        }
        else if (r.IsUpcoming)
        {
            r.Status = RecordingStatus.Cancelled;
            lock (_items) _items.Remove(r);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes a finished recording from the list, and its file if asked.</summary>
    public static void Delete(Recording r, bool deleteFile)
    {
        if (r.IsActive) return;
        if (deleteFile && !string.IsNullOrEmpty(r.FilePath))
        {
            try { if (File.Exists(r.FilePath)) File.Delete(r.FilePath); }
            catch (Exception ex) { Log.Error(ex); }
        }
        lock (_items) _items.Remove(r);
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// How many connections would be in use at <paramref name="at"/>: recordings running then, plus one for
    /// watching. Compared with the plan's limit to warn before it's hit.
    /// </summary>
    public static int ConnectionsNeeded(DateTime from, DateTime until, Recording except = null)
    {
        lock (_items)
            return _items.Count(r => r != except && (r.IsUpcoming || r.IsActive) && r.RecordFrom < until && r.RecordUntil > from) + 1;
    }

    public static int MaxConnections => DataService.Instance.Catalog.Account?.MaxConnections ?? 0;

    static void Save()
    {
        try
        {
            List<Recording> copy;
            lock (_items) copy = _items.ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy, Opts));
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    static void Tick()
    {
        var now = DateTime.Now;
        var activeId = DataService.Instance.Active?.Id;
        List<Recording> due, missed;
        lock (_items)
        {
            due = _items.Where(r => r.IsUpcoming && r.RecordFrom <= now && r.RecordUntil > now && r.PlaylistId == activeId).ToList();
            missed = _items.Where(r => r.IsUpcoming && r.RecordUntil <= now).ToList();
        }
        foreach (var r in missed) { r.Status = RecordingStatus.Missed; r.Error = "Dingus wasn't open at the time."; }
        foreach (var r in due) Begin(r);
        if (missed.Count > 0) { Save(); Changed?.Invoke(); }
        KeepAwake.SetRecording(ActiveCount > 0);
    }

    // ------------------------------------------------------------------ recording

    static void Begin(Recording r)
    {
        var ds = DataService.Instance;
        var ch = ds.Catalog.Channels.FirstOrDefault(c => c.Id == r.ChannelId);
        var url = ch == null ? null : ds.StreamUrl(ch);
        if (url == null)
        {
            r.Status = RecordingStatus.Failed;
            r.Error = "That channel isn't in your playlist any more.";
            Save(); Changed?.Invoke();
            return;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            r.FilePath = UniquePath(Path.Combine(Folder, SafeName($"{r.ChannelName} - {r.Title} - {r.Start:yyyy-MM-dd HHmm}") + ".ts"));
        }
        catch (Exception ex)
        {
            r.Status = RecordingStatus.Failed;
            r.Error = $"Couldn't use the recordings folder ({ex.Message}).";
            Save(); Changed?.Invoke();
            MainWindow.Instance?.ShowToast($"Recording failed: {r.Error}");
            return;
        }
        r.Status = RecordingStatus.Recording;
        r.Error = null;
        var cts = new CancellationTokenSource();
        lock (Running) Running[r.Id] = cts;
        Save();
        Changed?.Invoke();
        MainWindow.Instance?.ShowToast($"Recording {r.Title} on {r.ChannelName}");
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            string error = null;
            try { await RecordAsync(r, url, cts.Token); }
            catch (Exception ex) { error = ex.Message; Log.Error(ex); }
            await dispatcher.InvokeAsync(() => Finish(r, error, cts.IsCancellationRequested));
        });
    }

    static async Task RecordAsync(Recording r, string url, CancellationToken stop)
    {
        var buf = new byte[128 * 1024];
        int failures = 0;
        await using var file = new FileStream(r.FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        while (!stop.IsCancellationRequested && DateTime.Now < r.RecordUntil)
        {
            try
            {
                using var until = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var left = r.RecordUntil - DateTime.Now;
                if (left <= TimeSpan.Zero) break;
                until.CancelAfter(left);
                using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, until.Token);
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable or (HttpStatusCode)458)
                    throw new IOException("the provider refused the connection – your plan's connection limit may be reached");
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(until.Token);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(until.Token);
                int n;
                while (true)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(20));
                    n = await src.ReadAsync(buf, idle.Token);
                    if (n <= 0) break;
                    await file.WriteAsync(buf.AsMemory(0, n), CancellationToken.None);
                    r.Bytes += n;
                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested || DateTime.Now >= r.RecordUntil) { break; }
            catch (Exception ex) when (++failures <= 6)
            {
                Log.Error(ex); // dropped: reconnect and carry on (a few seconds may be missing)
            }
            if (!stop.IsCancellationRequested && DateTime.Now < r.RecordUntil)
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 * failures)), stop); } catch { break; }
            if (failures > 6) throw new IOException("the stream kept dropping");
        }
        await file.FlushAsync();
    }

    static void Finish(Recording r, string error, bool stoppedByYou)
    {
        lock (Running) Running.Remove(r.Id);
        long size = 0;
        try { size = File.Exists(r.FilePath) ? new FileInfo(r.FilePath).Length : 0; } catch { }
        r.Bytes = size;
        if (size == 0)
        {
            r.Status = RecordingStatus.Failed;
            r.Error = error ?? (stoppedByYou ? "Stopped before anything was recorded." : "Nothing was received from the provider.");
            try { if (File.Exists(r.FilePath)) File.Delete(r.FilePath); } catch { }
        }
        else if (error != null || (stoppedByYou && DateTime.Now < r.RecordUntil.AddSeconds(-30)))
        {
            r.Status = RecordingStatus.Partial;
            r.Error = error ?? r.Error ?? "Stopped early.";
        }
        else r.Status = RecordingStatus.Done;
        Save();
        Changed?.Invoke();
        KeepAwake.SetRecording(ActiveCount > 0);
        MainWindow.Instance?.ShowToast(r.Status == RecordingStatus.Done ? $"Recorded: {r.Title}"
            : r.Status == RecordingStatus.Partial ? $"Recording of {r.Title} ended early – the part recorded is saved."
            : $"Recording of {r.Title} failed: {r.Error}");
    }

    /// <summary>Stops everything (Dingus is closing). What's recorded so far is kept.</summary>
    public static void StopAll()
    {
        lock (Running) foreach (var c in Running.Values) c.Cancel();
        foreach (var r in All.Where(r => r.IsActive)) { r.Status = RecordingStatus.Partial; r.Error = "Dingus was closed during the recording."; }
        Save();
    }

    static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 150 ? s[..150] : s;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}).ts");
            if (!File.Exists(p)) return p;
        }
    }
}
