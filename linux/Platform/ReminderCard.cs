using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>The reminder pop-up card (Linux): bottom-right of the screen, on top, with Watch / Dismiss.</summary>
public static partial class ReminderService
{
    static readonly List<Window> Cards = new();

    static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;

    internal static Window SelfTestCard() => ShowCard(new Reminder
    {
        ChannelId = "x", ChannelName = "Channel One HD", Title = "Football Highlights", Start = DateTime.Now.AddMinutes(1), End = DateTime.Now.AddMinutes(80),
    });

    static Window ShowCard(Reminder r)
    {
        bool started = r.Start <= DateTime.Now;
        var watch = new Button { Content = "Watch", Classes = { "primary" } };
        var dismiss = new Button { Content = "Dismiss", Classes = { "ghost" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(watch);
        buttons.Children.Add(dismiss);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Spacing = 8 };
        head.Children.Add(new TextBlock { Text = Icons.BellRing, Classes = { "icon" }, FontSize = 14, Foreground = Res("AccentBrush") });
        head.Children.Add(new TextBlock { Text = "REMINDER", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = r.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
        stack.Children.Add(new TextBlock
        {
            Text = $"{(started ? "On now" : "Starts at " + r.Start.ToString("HH:mm"))}  ·  {r.ChannelName}",
            Foreground = Res("MutedBrush"), Margin = new Thickness(0, 4, 0, 14), TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
        });
        stack.Children.Add(buttons);

        var w = new Window
        {
            Title = "Dingus reminder",
            SystemDecorations = SystemDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Res("Surface2Brush"),
            Content = new Border
            {
                BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 16, 20, 18), MinWidth = 300, Child = stack,
            },
        };
        var close = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        close.Tick += (_, _) => { close.Stop(); w.Close(); };
        watch.Click += (_, _) => { w.Close(); Watch?.Invoke(r.ChannelId); };
        dismiss.Click += (_, _) => w.Close();
        w.Closed += (_, _) => { close.Stop(); Cards.Remove(w); };
        w.Opened += (_, _) =>
        {
            var screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            double offset = Cards.Where(c => c != w).Sum(c => c.Bounds.Height + 8);
            var size = w.ClientSize;
            w.Position = new PixelPoint(
                (int)(area.Right - (size.Width + 16) * scale),
                (int)(area.Bottom - (size.Height + 16 + offset) * scale));
        };
        Cards.Add(w);
        w.Show();
        close.Start();
        return w;
    }
}
