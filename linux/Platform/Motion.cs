using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Dingus.Services;

/// <summary>Short, eased fades (and small slides) for things that appear and disappear.</summary>
public static class Motion
{
    static readonly ConditionalWeakTable<Visual, object> Tokens = new();

    static object NewToken(Visual v) { var t = new object(); Tokens.AddOrUpdate(v, t); return t; }
    static bool IsCurrent(Visual v, object t) => Tokens.TryGetValue(v, out var cur) && ReferenceEquals(cur, t);

    public static void FadeIn(Visual e, double fromY = 0, double fromX = 0, int ms = 180)
    {
        if (e == null) return;
        if (e.IsVisible && e.Opacity >= 0.99 && !Tokens.TryGetValue(e, out _)) return;
        var token = NewToken(e);
        double from = e.IsVisible ? e.Opacity : 0;
        e.IsVisible = true;
        _ = Run(e, from, 1, fromX * (1 - from), fromY * (1 - from), 0, 0, ms, new CubicEaseOut(), () =>
        {
            if (IsCurrent(e, token)) Tokens.Remove(e);
        });
    }

    public static void FadeOut(Visual e, double toY = 0, double toX = 0, int ms = 140)
    {
        if (e == null || !e.IsVisible) return;
        var token = NewToken(e);
        _ = Run(e, e.Opacity, 0, 0, 0, toX, toY, ms, new CubicEaseIn(), () =>
        {
            if (!IsCurrent(e, token)) return;
            Tokens.Remove(e);
            e.IsVisible = false;
            e.Opacity = 1;
            e.RenderTransform = null;
        });
    }

    public static void HideNow(Visual e)
    {
        if (e == null) return;
        NewToken(e);
        Tokens.Remove(e);
        e.IsVisible = false;
        e.Opacity = 1;
        e.RenderTransform = null;
    }

    static async Task Run(Visual e, double o0, double o1, double x0, double y0, double x1, double y1, int ms, Easing ease, Action done)
    {
        var tt = e.RenderTransform as TranslateTransform ?? new TranslateTransform();
        e.RenderTransform = tt;
        var start = DateTime.UtcNow;
        var token = Tokens.TryGetValue(e, out var t) ? t : null;
        while (true)
        {
            double p = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / ms, 0, 1);
            double k = ease.Ease(p);
            e.Opacity = o0 + (o1 - o0) * k;
            tt.X = x0 + (x1 - x0) * k;
            tt.Y = y0 + (y1 - y0) * k;
            if (p >= 1) break;
            await Task.Delay(16);
            if (token != null && !(Tokens.TryGetValue(e, out var cur) && ReferenceEquals(cur, token))) return; // superseded
        }
        done();
    }
}
