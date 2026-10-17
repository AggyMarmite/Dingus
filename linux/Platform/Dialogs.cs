using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dingus;

/// <summary>Small dark dialogs: a message, a yes/no question, a line of text, a PIN.</summary>
public static class Dialogs
{
    static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;

    public static Window Frame(string title, Control content, double width = 440)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            Background = Res("SurfaceBrush"),
            Content = new Border
            {
                BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(26, 22, 26, 22), Child = content,
            },
        };
        content.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(w).Properties.IsLeftButtonPressed && e.Source is not TextBox) w.BeginMoveDrag(e);
        };
        return w;
    }

    static StackPanel Body(string title, string text)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(text))
            s.Children.Add(new TextBlock { Text = text, Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        return s;
    }

    static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Spacing = 8 };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static async Task Message(Window owner, string title, string text)
    {
        var ok = new Button { Content = "OK", Classes = { "primary" }, IsDefault = true };
        var body = Body(title, text);
        body.Children.Add(Buttons(ok));
        var w = Frame(title, body);
        ok.Click += (_, _) => w.Close();
        await Show(w, owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string text, string yes = "Yes", string no = "Cancel", bool danger = false)
    {
        bool result = false;
        var y = new Button { Content = yes, Classes = { danger ? "danger" : "primary" }, IsDefault = true };
        var n = new Button { Content = no, IsCancel = true };
        var body = Body(title, text);
        body.Children.Add(Buttons(n, y));
        var w = Frame(title, body);
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        await Show(w, owner);
        return result;
    }

    /// <summary>Asks for a line of text. Returns null if cancelled.</summary>
    public static async Task<string> Ask(Window owner, string title, string prompt, string initial = "", bool password = false)
    {
        string result = null;
        var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 14, 0, 0) };
        if (password) { box.PasswordChar = '•'; box.MaxLength = 12; }
        var ok = new Button { Content = "OK", Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var body = Body(title, prompt);
        body.Children.Add(box);
        body.Children.Add(Buttons(cancel, ok));
        var w = Frame(title, body, 420);
        ok.Click += (_, _) => { result = box.Text ?? ""; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await Show(w, owner);
        return result;
    }

    public static Task Show(Window w, Window owner) =>
        owner != null && owner.IsVisible ? w.ShowDialog(owner) : ShowAndWait(w);

    static Task ShowAndWait(Window w)
    {
        var tcs = new TaskCompletionSource();
        w.Closed += (_, _) => tcs.TrySetResult();
        w.Show();
        return tcs.Task;
    }
}
