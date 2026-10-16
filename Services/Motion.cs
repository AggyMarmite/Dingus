using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Dingus.Services;

/// <summary>
/// Short, eased fades and slides for things that appear and disappear (player controls, toasts, panels).
/// Animations only run for the ~150–200 ms an element is changing, so they cost nothing at rest.
/// </summary>
public static class Motion
{
    static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static readonly IEasingFunction In = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });

    static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    // Which fade is current for each element, so a fade-out finishing late can't hide something shown again.
    static readonly ConditionalWeakTable<UIElement, object> Tokens = new();
    static object NewToken(UIElement e) { var t = new object(); Tokens.AddOrUpdate(e, t); return t; }
    static bool IsCurrent(UIElement e, object t) => Tokens.TryGetValue(e, out var cur) && ReferenceEquals(cur, t);
    static readonly ConditionalWeakTable<UIElement, object> FadingOut = new();

    /// <summary>Shows an element with a fade (and a small slide: + = from below, − = from above, or sideways).</summary>
    public static void FadeIn(UIElement e, double fromY = 0, double fromX = 0, int ms = 180)
    {
        if (e == null) return;
        bool fadingOut = FadingOut.TryGetValue(e, out _);
        if (e.Visibility == Visibility.Visible && !fadingOut) return; // already showing
        FadingOut.Remove(e);
        NewToken(e);
        double from = e.Visibility == Visibility.Visible ? e.Opacity : 0;
        e.Visibility = Visibility.Visible;
        var d = TimeSpan.FromMilliseconds(ms);
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, 1, d) { EasingFunction = Out });
        if (fromX != 0 || fromY != 0)
        {
            var tt = Translate(e);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX * (1 - from), 0, d) { EasingFunction = Out });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY * (1 - from), 0, d) { EasingFunction = Out });
        }
    }

    /// <summary>Hides an element with a quick fade, then sets it to <paramref name="end"/> (Collapsed or Hidden).</summary>
    public static void FadeOut(UIElement e, Visibility end = Visibility.Collapsed, double toY = 0, double toX = 0, int ms = 140)
    {
        if (e == null || e.Visibility != Visibility.Visible || FadingOut.TryGetValue(e, out _)) return;
        var token = NewToken(e);
        FadingOut.AddOrUpdate(e, token);
        var d = TimeSpan.FromMilliseconds(ms);
        var a = new DoubleAnimation(e.Opacity, 0, d) { EasingFunction = In };
        a.Completed += (_, _) =>
        {
            if (!IsCurrent(e, token)) return; // shown again meanwhile
            FadingOut.Remove(e);
            e.Visibility = end;
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            if (e.RenderTransform is TranslateTransform t)
            {
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.X = t.Y = 0;
            }
        };
        e.BeginAnimation(UIElement.OpacityProperty, a);
        if (toX != 0 || toY != 0)
        {
            var tt = Translate(e);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, toX, d) { EasingFunction = In });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, toY, d) { EasingFunction = In });
        }
    }

    /// <summary>Hides straight away (no animation) – for when the player closes.</summary>
    public static void HideNow(UIElement e, Visibility end = Visibility.Collapsed)
    {
        if (e == null) return;
        NewToken(e);
        FadingOut.Remove(e);
        e.BeginAnimation(UIElement.OpacityProperty, null);
        e.Opacity = 1;
        e.Visibility = end;
    }

    static TranslateTransform Translate(UIElement e)
    {
        if (e.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        t = new TranslateTransform();
        e.RenderTransform = t;
        return t;
    }
}
