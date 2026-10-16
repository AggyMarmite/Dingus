using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dingus;

// Keyboard shortcut sheet (? / F1) and the sleep timer.
public partial class MainWindow
{
    // =====================================================================================
    // Keyboard shortcut sheet
    // =====================================================================================

    static readonly (string Keys, string What)[] Shortcuts =
    {
        ("0 – 9", "Type a channel number"),
        ("0 / Backspace", "Last channel you watched"),
        ("↑  ↓", "Channel up / down (films: volume)"),
        ("←  →", "Films: back 10 s / forward 30 s"),
        ("Enter / L", "Channel list – what's on now"),
        ("Space", "Play / pause"),
        ("M", "Mute  ·  mouse wheel = volume"),
        ("F / F11", "Full screen on / off"),
        ("Esc", "Back to the guide"),
        ("P", "Picture-in-picture"),
        ("V", "Multi-view (4 / 6 / 8 channels)"),
        ("I", "Stream info"),
        ("S", "Sleep timer"),
        ("N", "Next episode"),
        ("Home", "Start a film from the beginning"),
        ("? / F1", "This list"),
    };

    bool SheetOpen => ShortcutSheet.Visibility == Visibility.Visible || OverlaySheet.Visibility == Visibility.Visible;

    void ToggleShortcutSheet()
    {
        if (SheetOpen) { CloseShortcutSheet(); return; }
        var (sheet, grid) = _playerMode ? (OverlaySheet, OverlaySheetGrid) : (ShortcutSheet, ShortcutSheetGrid);
        if (grid.Children.Count == 0)
            foreach (var (keys, what) in Shortcuts)
            {
                var row = new DockPanel { Margin = new Thickness(0, 5, 28, 5) };
                var key = new Border
                {
                    MinWidth = 120,
                    Child = new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 2, 8, 3),
                        Child = new TextBlock { Text = keys, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold },
                    },
                };
                DockPanel.SetDock(key, Dock.Left);
                row.Children.Add(key);
                row.Children.Add(new TextBlock
                {
                    Text = what, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                });
                grid.Children.Add(row);
            }
        sheet.Visibility = Visibility.Visible;
    }

    void CloseShortcutSheet()
    {
        ShortcutSheet.Visibility = Visibility.Collapsed;
        OverlaySheet.Visibility = Visibility.Collapsed;
    }

    void Sheet_MouseDown(object sender, MouseButtonEventArgs e) { CloseShortcutSheet(); e.Handled = true; }

    static bool IsSheetKey(Key k) => k == Key.F1 || k == Key.OemQuestion;

    // =====================================================================================
    // Sleep timer
    // =====================================================================================

    DispatcherTimer _sleepTimer;
    DateTime? _sleepAt;
    bool _sleepWarned;

    void Sleep_Click(object sender, RoutedEventArgs e) => ShowSleepMenu();

    void ShowSleepMenu()
    {
        var menu = new ContextMenu { PlacementTarget = SleepButton, Placement = PlacementMode.Top };
        if (_sleepAt is DateTime at)
            menu.Items.Add(new MenuItem { Header = $"Stops at {at:HH:mm} ({Remaining(at)})", IsEnabled = false });

        void Add(string text, Func<DateTime?> when, bool isChecked = false)
        {
            var item = new MenuItem { Header = text, IsChecked = isChecked };
            item.Click += (_, _) => SetSleep(when());
            menu.Items.Add(item);
        }

        Add("Off", () => null, _sleepAt == null);
        foreach (var m in new[] { 15, 30, 60, 90 })
            Add($"{m} minutes", () => DateTime.Now.AddMinutes(m));
        if (_nowLive && _nowChannel != null && _guide.NowOn(_nowChannel) is { } p && p.End > DateTime.Now)
            Add($"End of this programme ({p.End:HH:mm})", () => p.End.AddMinutes(1));
        else if (!_nowLive && _svc?.Player is { Length: > 0 } mp)
        {
            var left = mp.Length - mp.Time;
            if (left > 60_000) Add("End of this film / episode", () => DateTime.Now.AddMilliseconds(left + 15_000));
        }

        _hideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; if (_playerMode) _hideTimer.Start(); };
        _menuOpen = true;
        menu.IsOpen = true;
    }

    void SetSleep(DateTime? at)
    {
        _sleepAt = at;
        _sleepWarned = false;
        if (at == null)
        {
            _sleepTimer?.Stop();
            ShowToast("Sleep timer off");
        }
        else
        {
            if (_sleepTimer == null)
            {
                _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _sleepTimer.Tick += (_, _) => SleepTick();
            }
            _sleepTimer.Start();
            ShowToast($"Sleep timer: stopping at {at:HH:mm} ({Remaining(at.Value)})");
        }
        UpdateSleepButton();
    }

    void SleepTick()
    {
        if (_sleepAt is not DateTime at) { _sleepTimer.Stop(); return; }
        if (_currentUrl == null) { SetSleepOffQuietly(); return; } // nothing playing any more
        var left = at - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            SetSleepOffQuietly();
            if (_mini) ExitMini(false);
            if (_playerMode) ExitPlayer();
            StopPlayback();
            ShowToast("Sleep timer – playback stopped. Goodnight!");
            return;
        }
        if (!_sleepWarned && left <= TimeSpan.FromMinutes(1))
        {
            _sleepWarned = true;
            ShowToast("Sleep timer: stopping in 1 minute  ·  press S to change it");
        }
        UpdateSleepButton();
    }

    void SetSleepOffQuietly()
    {
        _sleepAt = null;
        _sleepTimer?.Stop();
        UpdateSleepButton();
    }

    void UpdateSleepButton()
    {
        bool on = _sleepAt != null;
        SleepButton.Foreground = on ? (Brush)FindResource("AccentBrush") : Brushes.White;
        SleepButton.ToolTip = on ? $"Sleep timer – stops at {_sleepAt:HH:mm} ({Remaining(_sleepAt.Value)}) (S)" : "Sleep timer (S)";
    }

    static string Remaining(DateTime at)
    {
        var m = (int)Math.Ceiling((at - DateTime.Now).TotalMinutes);
        return m <= 1 ? "1 min left" : m < 60 ? $"{m} min left" : $"{m / 60} h {m % 60:00} min left";
    }
}
