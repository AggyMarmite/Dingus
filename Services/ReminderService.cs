using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// "Remind me" for programmes: a small always-on-top card appears a minute before the programme
/// starts, with a button to watch it. Saved in reminders.json, so they survive closing the app.
/// </summary>
public static class ReminderService
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

    // ------------------------------------------------------------------ the pop-up card

    static readonly List<Window> Cards = new();

    /// <summary>Self-test: shows a sample card.</summary>
    internal static Window SelfTestCard() => ShowCard(new Reminder
    {
        ChannelId = "x", ChannelName = "BBC One HD", Title = "Match of the Day", Start = DateTime.Now.AddMinutes(1), End = DateTime.Now.AddMinutes(80),
    });

    static Window ShowCard(Reminder r)
    {
        var res = Application.Current.Resources;
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Title = "Dingus reminder",
        };
        bool started = r.Start <= DateTime.Now;
        var title = new TextBlock { Text = r.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var sub = new TextBlock
        {
            Text = $"{(started ? "On now" : "Starts at " + r.Start.ToString("HH:mm"))}  ·  {r.ChannelName}",
            Foreground = (Brush)res["MutedBrush"], Margin = new Thickness(0, 4, 0, 14), TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        };
        var watch = new Button { Content = "Watch", Style = (Style)res["PrimaryButton"] };
        var dismiss = new Button { Content = "Dismiss", Style = (Style)res["GhostButton"], Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(watch);
        buttons.Children.Add(dismiss);
        var head = new TextBlock
        {
            Text = "  REMINDER", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)res["AccentBrush"], FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI"),
        };
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(title);
        stack.Children.Add(sub);
        stack.Children.Add(buttons);
        w.Content = new Border
        {
            Background = (Brush)res["Surface2Brush"], BorderBrush = (Brush)res["LineBrush"], BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(12),
            Child = stack, MinWidth = 300,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5 },
        };
        w.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        w.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");

        var close = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        close.Tick += (_, _) => { close.Stop(); w.Close(); };
        watch.Click += (_, _) => { w.Close(); Watch?.Invoke(r.ChannelId); };
        dismiss.Click += (_, _) => w.Close();
        w.Closed += (_, _) => { close.Stop(); Cards.Remove(w); };
        w.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            double offset = Cards.Where(c => c != w).Sum(c => c.ActualHeight);
            w.Left = area.Right - w.ActualWidth - 8;
            w.Top = area.Bottom - w.ActualHeight - 8 - offset;
        };
        Cards.Add(w);
        w.Show();
        close.Start();
        System.Media.SystemSounds.Asterisk.Play();
        return w;
    }
}
