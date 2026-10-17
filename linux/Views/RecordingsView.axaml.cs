using System.Diagnostics;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>A line on the Recordings &amp; reminders page.</summary>
public class ScheduleItem
{
    public Recording Recording { get; init; }
    public Reminder Reminder { get; init; }
    public string Title { get; init; }
    public string Line2 { get; init; }
    public string Line3 { get; init; }
    public IBrush Line3Brush { get; init; } = Brushes.Gray;
    public string Glyph { get; init; }
    public IBrush BadgeBrush { get; init; }
    public string ActionText { get; init; }
    public bool CanPlay { get; init; }
    public bool HasLine3 => !string.IsNullOrEmpty(Line3);
    public DateTime Sort { get; init; }
}

/// <summary>Recordings and reminders in one place: what's on its way, what's recording, what's recorded – and where recordings go.</summary>
public partial class RecordingsView : UserControl, IPage
{
    public string Title => "Recordings";
    public bool SupportsSearch => false;
    public string SearchHint => "";
    public GuideView Guide { get; set; }

    bool _loading;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(5) };

    static readonly IBrush RecBrush = Frozen(0xEF, 0x44, 0x44), RemBrush = Frozen(0x3B, 0x82, 0xF6),
                          DoneBrush = Frozen(0x33, 0x3D, 0x52), WarnBrush = Frozen(0xFB, 0xBF, 0x24), OkBrush = Frozen(0x4A, 0xDE, 0x80),
                          Muted = Frozen(0x8B, 0x94, 0xA7);

    static IBrush Frozen(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b)).ToImmutable();

    public RecordingsView()
    {
        InitializeComponent();
        RecordingService.Changed += () => { if (IsEffectivelyVisible) Build(); };
        ReminderService.Changed += () => { if (IsEffectivelyVisible) Build(); };
        _tick.Tick += (_, _) => Build(); // recording sizes and "in 5 min" times
    }

    public void ApplySearch(string query) { }

    public void OnShown()
    {
        _loading = true;
        var s = SettingsStore.Current;
        FolderBox.Text = RecordingService.Folder;
        Check(PadBeforeChips, s.RecordPadBefore.ToString());
        Check(PadAfterChips, s.RecordPadAfter.ToString());
        _loading = false;
        CheckFolder();
        Build();
        _tick.Start();
    }

    public void OnHidden()
    {
        _tick.Stop();
        NowList.ItemsSource = UpcomingList.ItemsSource = DoneList.ItemsSource = null;
    }

    static void Check(Panel p, string tag)
    {
        foreach (var rb in p.Children.OfType<RadioButton>()) rb.IsChecked = (rb.Tag as string) == tag;
    }

    // ------------------------------------------------------------------ lists

    void Build()
    {
        var now = DateTime.Now;
        var activeId = DataService.Instance.Active?.Id;
        var all = RecordingService.All.Where(r => r.PlaylistId == activeId || r.IsFinished || r.Status is RecordingStatus.Failed or RecordingStatus.Missed).ToList();

        var recordingNow = all.Where(r => r.IsActive).Select(r => new ScheduleItem
        {
            Recording = r, Title = r.Title, Glyph = Icons.Record, BadgeBrush = RecBrush, ActionText = "Stop",
            Line2 = $"{r.ChannelName}  ·  until {r.RecordUntil:HH:mm}  ·  {Size(r.Bytes)} so far",
            Line3 = RecordingService.Folder, Line3Brush = Muted,
        }).ToList();
        NowList.ItemsSource = recordingNow;
        NowHeader.IsVisible = NowList.IsVisible = recordingNow.Count > 0;

        var upcoming = new List<ScheduleItem>();
        int max = RecordingService.MaxConnections;
        foreach (var r in all.Where(r => r.IsUpcoming))
        {
            int need = RecordingService.ConnectionsNeeded(r.RecordFrom, r.RecordUntil);
            upcoming.Add(new ScheduleItem
            {
                Recording = r, Title = r.Title, Glyph = Icons.Record, BadgeBrush = RecBrush, ActionText = "Cancel", Sort = r.Start,
                Line2 = $"Record  ·  {When(r.Start)} – {r.End:HH:mm}  ·  {r.ChannelName}",
                Line3 = max > 0 && need > max ? $"At this time you'd need {need} streams (recordings + watching) – your plan allows {max}." : null,
                Line3Brush = WarnBrush,
            });
        }
        foreach (var rem in ReminderService.Upcoming)
            upcoming.Add(new ScheduleItem
            {
                Reminder = rem, Title = rem.Title, Glyph = Icons.BellRing, BadgeBrush = RemBrush, ActionText = "Cancel", Sort = rem.Start,
                Line2 = $"Reminder  ·  {When(rem.Start)}  ·  {rem.ChannelName}",
            });
        UpcomingList.ItemsSource = upcoming.OrderBy(x => x.Sort).ToList();
        UpcomingEmpty.IsVisible = upcoming.Count == 0;

        var done = all.Where(r => !r.IsUpcoming && !r.IsActive).OrderByDescending(r => r.Start).Select(r => new ScheduleItem
        {
            Recording = r, Title = r.Title, Glyph = r.IsFinished ? Icons.Films : Icons.Alert, BadgeBrush = r.IsFinished ? DoneBrush : RecBrush,
            ActionText = "Delete", CanPlay = r.IsFinished,
            Line2 = $"{r.ChannelName}  ·  {r.Start:ddd d MMM, HH:mm}" + (r.Bytes > 0 ? $"  ·  {Size(r.Bytes)}" : ""),
            Line3 = r.Status switch
            {
                RecordingStatus.Partial => "Partly recorded – " + r.Error,
                RecordingStatus.Failed => "Failed – " + r.Error,
                RecordingStatus.Missed => "Missed – " + r.Error,
                _ => null,
            },
            Line3Brush = r.Status == RecordingStatus.Partial ? WarnBrush : RecBrush,
        }).ToList();
        DoneList.ItemsSource = done;
        DoneEmpty.IsVisible = done.Count == 0;

        UpdateLimitCard(all);
    }

    void UpdateLimitCard(List<Recording> all)
    {
        int max = RecordingService.MaxConnections;
        // The busiest moment in the schedule (each recording, plus one for watching).
        int peak = 1;
        DateTime? peakAt = null;
        foreach (var r in all.Where(r => r.IsUpcoming || r.IsActive))
        {
            int n = RecordingService.ConnectionsNeeded(r.RecordFrom, r.RecordFrom.AddMinutes(1));
            if (n > peak) { peak = n; peakAt = r.RecordFrom; }
        }
        bool over = max > 0 && peak > max;
        LimitCard.Background = over ? new SolidColorBrush(Color.FromArgb(0x33, 0xFB, 0xBF, 0x24)) : MainWindow.Res("AccentSoftBrush");
        LimitGlyph.Text = over ? Icons.Alert : Icons.Info;
        LimitGlyph.Foreground = over ? WarnBrush : Brushes.White;
        if (max <= 0)
        {
            LimitTitle.Text = "Each recording uses its own stream from your provider";
            LimitBody.Text = "So does watching. If your plan only allows one stream at a time, recording while you watch (or two recordings at once) can fail or interrupt what you're watching.";
        }
        else if (over)
        {
            LimitTitle.Text = $"Your plan allows {max} stream{(max == 1 ? "" : "s")} at once – at {peakAt:ddd HH:mm} you'd need {peak}";
            LimitBody.Text = "That counts every recording running at that time plus one for watching. Cancel one, or don't watch while it records – otherwise a recording may fail or cut off what you're watching.";
        }
        else
        {
            LimitTitle.Text = $"Your plan allows {max} stream{(max == 1 ? "" : "s")} at once";
            LimitBody.Text = max == 1
                ? "Each recording uses one stream, and so does watching – so while something records you won't be able to watch at the same time."
                : $"Each recording uses one stream, and so does watching. You can record {max - 1} thing{(max - 1 == 1 ? "" : "s")} while you watch.";
        }
    }

    static string When(DateTime t)
    {
        var d = t.Date;
        string day = d == DateTime.Today ? "Today" : d == DateTime.Today.AddDays(1) ? "Tomorrow" : t.ToString("ddd d MMM");
        var mins = (int)Math.Ceiling((t - DateTime.Now).TotalMinutes);
        return mins > 0 && mins < 60 ? $"{day} {t:HH:mm} (in {mins} min)" : $"{day} {t:HH:mm}";
    }

    static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : $"{b / (double)(1 << 20):0} MB";

    // ------------------------------------------------------------------ actions

    static ScheduleItem ItemOf(object sender) => (sender as Control)?.DataContext as ScheduleItem;

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        var it = ItemOf(sender);
        if (it == null) return;
        if (it.Reminder != null) { ReminderService.Remove(it.Reminder); return; }
        var r = it.Recording;
        if (r.IsActive || r.IsUpcoming) { RecordingService.Cancel(r); return; }
        // Finished / failed: remove it (and ask about the file).
        if (!r.IsFinished || string.IsNullOrEmpty(r.FilePath) || !File.Exists(r.FilePath)) { RecordingService.Delete(r, false); return; }
        if (await Dialogs.Confirm(TopLevel.GetTopLevel(this) as Window, $"Delete the recording “{r.Title}”?",
                $"The file will be deleted from:\n{r.FilePath}", "Delete", danger: true))
            RecordingService.Delete(r, true);
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        var r = ItemOf(sender)?.Recording;
        if (r?.FilePath == null || !File.Exists(r.FilePath)) { MainWindow.Instance?.ShowToast("That recording's file can't be found."); return; }
        MainWindow.Instance?.PlayVod(new VodItem
        {
            Kind = "recording", Title = r.Title, Subtitle = $"{r.ChannelName}  ·  recorded {r.Start:ddd d MMM, HH:mm}",
            Url = new Uri(r.FilePath).AbsoluteUri, ResumeKey = "r:" + r.Id, ItemId = r.Id,
        }, resume: true);
    }

    void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        var r = ItemOf(sender)?.Recording;
        if (r?.FilePath == null) return;
        Updater.OpenUrl(Path.GetDirectoryName(r.FilePath));
    }

    void RecordSlot_Click(object sender, RoutedEventArgs e) => RecordUi.RecordTimeSlot(TopLevel.GetTopLevel(this) as Window, Guide);

    // ------------------------------------------------------------------ settings

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var start = Directory.Exists(RecordingService.Folder) ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(RecordingService.Folder)) : null;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Where should Dingus save recordings?", AllowMultiple = false, SuggestedStartLocation = start,
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) SetFolder(path);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RecordingService.Folder);
            Updater.OpenUrl(RecordingService.Folder);
        }
        catch (Exception ex) { MainWindow.Instance?.ShowToast("Couldn't open the folder: " + ex.Message); }
    }

    void FolderBox_LostFocus(object sender, RoutedEventArgs e) => SetFolder(FolderBox.Text);
    void FolderBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SetFolder(FolderBox.Text); }

    void SetFolder(string path)
    {
        path = path?.Trim().Trim('"') ?? "";
        if (path == RecordingService.Folder && FolderBox.Text == path) { CheckFolder(); return; }
        SettingsStore.Current.RecordingsFolder = path;
        SettingsStore.Save();
        FolderBox.Text = RecordingService.Folder;
        CheckFolder();
    }

    /// <summary>Tries writing to the folder (network shares can be slow, so off the UI thread).</summary>
    async void CheckFolder()
    {
        var folder = RecordingService.Folder;
        FolderStatus.Text = "Checking…";
        FolderStatus.Foreground = Muted;
        var check = Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var test = Path.Combine(folder, ".dingus-write-test");
            File.WriteAllText(test, "ok");
            File.Delete(test);
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            try { return new DriveInfo(root!).AvailableFreeSpace; } catch { return -1L; }
        });
        string msg;
        IBrush brush;
        if (await Task.WhenAny(check, Task.Delay(8000)) != check) { msg = "This folder isn't answering – if it's on a NAS, check it's switched on and connected."; brush = WarnBrush; }
        else if (check.IsFaulted) { msg = "Dingus can't save here: " + (check.Exception?.InnerException?.Message ?? "unknown error"); brush = RecBrush; }
        else
        {
            long free = check.Result;
            msg = free >= 0 ? $"✓ Ready  ·  {free / (double)(1L << 30):0} GB free (an HD hour is about 2–4 GB)" : "✓ Ready";
            brush = OkBrush;
        }
        if (folder != RecordingService.Folder) return;
        FolderStatus.Text = msg;
        FolderStatus.Foreground = brush;
    }

    void PadBefore_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { IsChecked: true, Tag: string t } || !int.TryParse(t, out var m)) return;
        SettingsStore.Current.RecordPadBefore = m;
        SettingsStore.Save();
    }

    void PadAfter_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { IsChecked: true, Tag: string t } || !int.TryParse(t, out var m)) return;
        SettingsStore.Current.RecordPadAfter = m;
        SettingsStore.Save();
    }
}

/// <summary>Recording from anywhere in the app (Live TV, Search, Home, the player), with the connection-limit warning.</summary>
public static class RecordUi
{
    /// <summary>Records a programme (or cancels it if it's already set to record).</summary>
    public static void ToggleProgramme(GuideRow row, Programme p)
    {
        if (row == null || p == null) return;
        var now = DateTime.Now;
        if (p.End <= now) { MainWindow.Instance?.ShowToast("That programme has already finished."); return; }
        var existing = RecordingService.Find(row.Channel.Id, p.Start);
        if (existing != null)
        {
            RecordingService.Cancel(existing);
            MainWindow.Instance?.ShowToast(existing.IsActive ? $"Stopped recording {p.Title}" : $"Recording cancelled: {p.Title}");
            return;
        }
        var r = RecordingService.Schedule(row.Channel.Id, row.DisplayName, p.Title, p.Start, p.End);
        Announce(r);
    }

    public static bool IsRecording(GuideRow row, Programme p) => row != null && p != null && RecordingService.IsScheduled(row.Channel.Id, p.Start);

    /// <summary>The player's Record button: the programme on now, to its end (or an hour if there's no guide).</summary>
    public static void RecordNow(GuideRow row)
    {
        if (row == null) return;
        var now = DateTime.Now;
        var p = DataService.Instance.Epg.At(MainWindow.Instance?.EpgIdForRow(row), now);
        if (p != null) { ToggleProgramme(row, p); return; }
        var r = RecordingService.Schedule(row.Channel.Id, row.DisplayName, row.DisplayName, now, now.AddHours(1));
        Announce(r, "Recording the next hour (no guide information for this channel).");
    }

    static void Announce(Recording r, string first = null)
    {
        int max = RecordingService.MaxConnections;
        int need = RecordingService.ConnectionsNeeded(r.RecordFrom, r.RecordUntil);
        string when = r.RecordFrom <= DateTime.Now ? "Recording now" : $"Will record {r.Start:ddd HH:mm}";
        string msg = first ?? $"{when}: {r.Title}";
        if (max > 0 && need > max)
            msg += $"  ·  Heads up: your plan allows {max} stream{(max == 1 ? "" : "s")} – at that time you'd need {need}, so watching may be interrupted.";
        MainWindow.Instance?.ShowToast(msg);
    }

    /// <summary>"Record a time slot…": pick a channel, a day and time, and how long.</summary>
    public static async void RecordTimeSlot(Window owner, GuideView guide)
    {
        if (guide == null) return;
        var pick = new ChannelPickerWindow(guide, "Record which channel?");
        if (!await pick.ShowDialog<bool>(owner) || pick.Result == null) return;
        var row = pick.Result;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Record a time slot", FontSize = 18, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = row.DisplayName, Classes = { "muted" }, Margin = new Thickness(0, 4, 0, 0) });
        TextBlock Label(string t) => new() { Text = t, Classes = { "field" }, Margin = new Thickness(0, 16, 0, 8) };

        stack.Children.Add(Label("Day"));
        var days = new WrapPanel();
        var today = DateTime.Today;
        for (int i = 0; i < 7; i++)
        {
            var d = today.AddDays(i);
            days.Children.Add(new RadioButton
            {
                Classes = { "chip" }, GroupName = "SlotDay", Tag = d, IsChecked = i == 0,
                Content = i == 0 ? "Today" : i == 1 ? "Tomorrow" : d.ToString("ddd d"),
            });
        }
        stack.Children.Add(days);
        stack.Children.Add(Label("Start time (24-hour, e.g. 21:30)"));
        var time = new TextBox { Text = DateTime.Now.AddMinutes(5).ToString("HH:mm"), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(time);
        stack.Children.Add(Label("For"));
        var lengths = new WrapPanel();
        foreach (var m in new[] { 30, 60, 90, 120, 180 })
            lengths.Children.Add(new RadioButton
            {
                Classes = { "chip" }, GroupName = "SlotLen", Tag = m, IsChecked = m == 60,
                Content = m < 60 ? $"{m} min" : m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60}",
            });
        stack.Children.Add(lengths);
        stack.Children.Add(Label("Name (optional)"));
        var name = new TextBox { Watermark = row.DisplayName };
        stack.Children.Add(name);
        var error = new TextBlock { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(error);
        var ok = new Button { Content = "Schedule recording", Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Classes = { "ghost" }, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        var w = Dialogs.Frame("Record a time slot", stack, 460);
        bool done = false;
        DateTime start = default; int minutes = 60;
        cancel.Click += (_, _) => w.Close();
        ok.Click += (_, _) =>
        {
            var day = days.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as DateTime? ?? today;
            if (!TimeSpan.TryParse((time.Text ?? "").Trim().Replace('.', ':'), out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
            { error.Text = "Type the start time like 21:30."; return; }
            minutes = lengths.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 60;
            start = day + t;
            if (start.AddMinutes(minutes) <= DateTime.Now) { error.Text = "That time has already gone."; return; }
            done = true;
            w.Close();
        };
        await Dialogs.Show(w, owner);
        if (!done) return;
        var title = string.IsNullOrWhiteSpace(name.Text) ? row.DisplayName : name.Text.Trim();
        var rec = RecordingService.Schedule(row.Channel.Id, row.DisplayName, title, start, start.AddMinutes(minutes));
        Announce(rec);
    }
}
