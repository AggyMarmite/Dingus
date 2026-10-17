using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>The reminder pop-up card (Windows).</summary>
public static partial class ReminderService
{
    // ------------------------------------------------------------------ the pop-up card

    static readonly List<Window> Cards = new();

    /// <summary>Self-test: shows a sample card.</summary>
    internal static Window SelfTestCard() => ShowCard(new Reminder
    {
        ChannelId = "x", ChannelName = "Channel One HD", Title = "Football Highlights", Start = DateTime.Now.AddMinutes(1), End = DateTime.Now.AddMinutes(80),
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
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new TextBlock
        {
            Text = "\uEA8F", FontSize = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)res["AccentBrush"], FontFamily = (FontFamily)res["IconFont"],
        });
        head.Children.Add(new TextBlock
        {
            Text = "REMINDER", FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)res["AccentBrush"],
        });
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
