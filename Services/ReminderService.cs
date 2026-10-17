using System.Text.Json;
#if LINUX
using Avalonia.Threading;
#else
using System.Windows.Threading;
#endif
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// "Remind me" for programmes: a small always-on-top card appears a minute before the programme
/// starts, with a button to watch it. Saved in reminders.json, so they survive closing the app.
/// </summary>
public static partial class ReminderService
{
    static List<Reminder> _items = new();
    static DispatcherTimer _timer;
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    static string FilePath => Path.Combine(AppPaths.Root, "reminders.json");

    /// <summary>Raised when reminders are added or removed (the guide redraws its bells).</summary>
    public static event Action Changed;
    /// <summary>Set by the main window: plays a channel full screen ("Watch" on the card).</summary>
    public static Action<string> Watch { get; set; }

    public static IReadOnlyList<Reminder> Upcoming
    {
        get
        {
            var id = DataService.Instance.Active?.Id;
            return _items.Where(r => r.PlaylistId == id && r.End > DateTime.Now).OrderBy(r => r.Start).ToList();
        }
    }

    public static void Start()
    {
        try
        {
            if (File.Exists(FilePath))
                _items = JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Log.Error(ex); _items = new(); }
        _items.RemoveAll(r => r.End < DateTime.Now);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public static bool Has(string channelId, DateTime start)
    {
        if (_items.Count == 0 || channelId == null) return false;
        foreach (var r in _items) if (r.ChannelId == channelId && r.Start == start) return true;
        return false;
    }

    /// <summary>Adds the reminder, or removes it if it's already set. Returns true if it's now set.</summary>
    public static bool Toggle(string channelId, string channelName, Programme p)
    {
        if (channelId == null || p == null) return false;
        var existing = _items.FirstOrDefault(r => r.ChannelId == channelId && r.Start == p.Start);
        if (existing != null) _items.Remove(existing);
        else _items.Add(new Reminder
        {
            PlaylistId = DataService.Instance.Active?.Id,
            ChannelId = channelId,
            ChannelName = channelName,
            Title = p.Title,
            Start = p.Start,
            End = p.End,
        });
        Save();
        Changed?.Invoke();
        return existing == null;
    }

    public static void Remove(Reminder r)
    {
        if (_items.Remove(r)) { Save(); Changed?.Invoke(); }
    }

    static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_items, Opts)); }
        catch (Exception ex) { Log.Error(ex); }
    }

    static void Tick()
    {
        var now = DateTime.Now;
        var activeId = DataService.Instance.Active?.Id;
        var due = _items.Where(r => r.Start - now <= TimeSpan.FromMinutes(1) && r.End > now && r.PlaylistId == activeId).ToList();
        int expired = _items.RemoveAll(r => r.End <= now);
        foreach (var r in due)
        {
            _items.Remove(r);
            ShowCard(r);
        }
        if (due.Count > 0 || expired > 0) { Save(); Changed?.Invoke(); }
    }
}
