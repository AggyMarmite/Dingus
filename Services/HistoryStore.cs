using System.Text.Json;
#if LINUX
using Avalonia.Threading;
#else
using System.Windows.Threading;
#endif
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Per-playlist viewing history: recently watched channels and resume positions for films/episodes.
/// Kept in its own small file (not settings.json) because it changes every few seconds while playing.
/// </summary>
public static class HistoryStore
{
    const int MaxRecent = 30;
    const int MaxResume = 400;

    static HistoryData _data = new();
    static string _file;
    static readonly DispatcherTimer SaveTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    static readonly DispatcherTimer ChangedTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>Raised (debounced) when recent channels or resume positions change.</summary>
    public static event Action Changed;

    static HistoryStore()
    {
        SaveTimer.Tick += (_, _) => { SaveTimer.Stop(); SaveNow(); };
        ChangedTimer.Tick += (_, _) => { ChangedTimer.Stop(); Changed?.Invoke(); };
    }

    public static void Load(string playlistId)
    {
        SaveNow();
        _file = playlistId == null ? null : Path.Combine(AppPaths.PlaylistDir(playlistId), "history.json");
        _data = new HistoryData();
        try
        {
            if (_file != null && File.Exists(_file))
                _data = JsonSerializer.Deserialize<HistoryData>(File.ReadAllText(_file)) ?? new HistoryData();
        }
        catch (Exception ex) { Log.Error(ex); _data = new HistoryData(); }
        _data.RecentChannels ??= new();
        _data.Resume ??= new();
        Changed?.Invoke();
    }

    public static IReadOnlyList<string> RecentChannels => _data.RecentChannels;

    public static void AddRecentChannel(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return;
        var l = _data.RecentChannels;
        if (l.Count > 0 && l[0] == channelId) return;
        l.Remove(channelId);
        l.Insert(0, channelId);
        if (l.Count > MaxRecent) l.RemoveRange(MaxRecent, l.Count - MaxRecent);
        Touch();
    }

    public static ResumeEntry Get(string key) =>
        key != null && _data.Resume.TryGetValue(key, out var e) ? e : null;

    public static void UpdateProgress(VodItem item, long positionMs, long durationMs, bool ended = false)
    {
        if (item?.ResumeKey == null) return;
        if (!ended && positionMs < 5_000) return; // nothing worth remembering yet
        if (!_data.Resume.TryGetValue(item.ResumeKey, out var e))
            _data.Resume[item.ResumeKey] = e = new ResumeEntry { Key = item.ResumeKey };

        e.Kind = item.Kind;
        e.ItemId = item.ItemId;
        e.SeriesId = item.SeriesId;
        e.Title = item.Title;
        e.Subtitle = item.Subtitle;
        e.Image = item.Image ?? e.Image;
        e.Ext = item.Ext ?? e.Ext;
        if (durationMs > 0) e.DurationMs = durationMs;
        e.PositionMs = ended ? e.DurationMs : positionMs;
        // Treat the last ~8% (credits) as finished.
        e.Finished = ended || (e.DurationMs > 0 && positionMs >= e.DurationMs * 0.92);
        e.UpdatedUtc = DateTime.UtcNow;

        if (_data.Resume.Count > MaxResume)
            foreach (var old in _data.Resume.Values.OrderBy(x => x.UpdatedUtc).Take(_data.Resume.Count - MaxResume).ToList())
                _data.Resume.Remove(old.Key);
        Touch();
    }

    /// <summary>Most recently watched entries of a kind, newest first.</summary>
    public static IEnumerable<ResumeEntry> Recent(string kind) =>
        _data.Resume.Values.Where(e => e.Kind == kind).OrderByDescending(e => e.UpdatedUtc);

    static void Touch()
    {
        SaveTimer.Stop(); SaveTimer.Start();
        ChangedTimer.Stop(); ChangedTimer.Start();
    }

    /// <summary>Stops saving (a backup has just been restored over the files on disk).</summary>
    public static void Detach()
    {
        SaveTimer.Stop();
        _file = null;
    }

    public static void SaveNow()
    {
        SaveTimer.Stop();
        if (_file == null) return;
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) { Log.Error(ex); }
    }
}
