using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Dingus.Services;

/// <summary>
/// Eased mouse-wheel scrolling for the long pages (Home, Settings, Search, Recordings, details):
///   svc:SmoothScroll.Enabled="True" on a ScrollViewer.
/// Instead of jumping a fixed step at once, each wheel notch glides there over a few frames, and quick notches
/// add up. It only runs while the page is moving, so it costs nothing at rest.
/// </summary>
public class SmoothScroll : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScroll, ScrollViewer, bool>("Enabled");
    public static bool GetEnabled(ScrollViewer o) => o.GetValue(EnabledProperty);
    public static void SetEnabled(ScrollViewer o, bool v) => o.SetValue(EnabledProperty, v);

    sealed class State { public double Target; public bool Running; }
    static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    static SmoothScroll()
    {
        EnabledProperty.Changed.AddClassHandler<ScrollViewer>((sv, e) =>
        {
            sv.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            if (e.NewValue is true) sv.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        });
    }

    static double Scrollable(ScrollViewer sv) => Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

    static void OnWheel(object sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.KeyModifiers != KeyModifiers.None || e.Delta.Y == 0) return;
        // Touchpads already scroll smoothly in small steps – leave those alone.
        if (Math.Abs(e.Delta.Y) < 0.5) return;
        // A list or row inside that scrolls by itself (and isn't at its end) keeps the wheel.
        if (e.Source is Visual src && InnerScrollerWantsIt(src, sv, e.Delta.Y)) return;
        e.Handled = true;
        ScrollBy(sv, -e.Delta.Y * 132);
    }

    static bool InnerScrollerWantsIt(Visual v, ScrollViewer outer, double delta)
    {
        for (var p = v; p != null && p != outer; p = p.GetVisualParent())
            if (p is ScrollViewer inner && Scrollable(inner) > 0 && inner.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                return delta > 0 ? inner.Offset.Y > 0 : inner.Offset.Y < Scrollable(inner);
        return false;
    }

    /// <summary>Scrolls by <paramref name="pixels"/> (positive = down), gliding there.</summary>
    public static void ScrollBy(ScrollViewer sv, double pixels)
    {
        var top = TopLevel.GetTopLevel(sv);
        if (top == null) { sv.Offset = sv.Offset.WithY(Math.Clamp(sv.Offset.Y + pixels, 0, Scrollable(sv))); return; }
        var st = States.GetOrCreateValue(sv);
        if (!st.Running) st.Target = sv.Offset.Y;
        st.Target = Math.Clamp(st.Target + pixels, 0, Scrollable(sv));
        if (st.Running) return;
        st.Running = true;
        TimeSpan? last = null;
        void Step(TimeSpan now)
        {
            double dt = last == null ? 16.7 : Math.Clamp((now - last.Value).TotalMilliseconds, 1, 50);
            last = now;
            double diff = st.Target - sv.Offset.Y;
            // Ease out: cover ~22% of what's left every 16 ms, whatever the frame rate.
            double k = 1 - Math.Pow(1 - 0.22, dt / 16.7);
            if (Math.Abs(diff) < 0.6 || !sv.IsEffectivelyVisible)
            {
                sv.Offset = sv.Offset.WithY(st.Target);
                st.Running = false;
                return;
            }
            sv.Offset = sv.Offset.WithY(sv.Offset.Y + diff * k);
            top.RequestAnimationFrame(Step);
        }
        top.RequestAnimationFrame(Step);
    }
}
