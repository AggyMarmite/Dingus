using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dingus.Services;

/// <summary>
/// Eased mouse-wheel scrolling for the long pages (Home, Settings, Search, Recordings, details):
///   svc:SmoothScroll.Enabled="True" on a ScrollViewer.
/// Instead of jumping a fixed step at once, each wheel notch glides there over a few frames, and quick notches
/// add up. It only runs while the page is moving, so it costs nothing at rest.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

    sealed class State { public double Target; public bool Running; }
    static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += OnWheel;
        else sv.PreviewMouseWheel -= OnWheel;
    }

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || Keyboard.Modifiers != ModifierKeys.None) return;
        // A list or row inside that scrolls by itself (and isn't at its end) keeps the wheel.
        if (e.OriginalSource is DependencyObject src && InnerScrollerWantsIt(src, sv, e.Delta)) return;
        e.Handled = true;
        ScrollBy(sv, -e.Delta * 1.1);
    }

    static bool InnerScrollerWantsIt(DependencyObject o, ScrollViewer outer, int delta)
    {
        for (var p = o; p != null && p != outer; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (p is ScrollViewer inner && inner.ScrollableHeight > 0 && inner.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                return delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight;
        return false;
    }

    /// <summary>Scrolls by <paramref name="pixels"/> (positive = down), gliding there.</summary>
    public static void ScrollBy(ScrollViewer sv, double pixels)
    {
        var st = States.GetOrCreateValue(sv);
        if (!st.Running) st.Target = sv.VerticalOffset;
        st.Target = Math.Clamp(st.Target + pixels, 0, sv.ScrollableHeight);
        if (st.Running) return;
        st.Running = true;
        var last = DateTime.UtcNow;
        void Step(object s, EventArgs a)
        {
            var now = DateTime.UtcNow;
            double dt = Math.Clamp((now - last).TotalMilliseconds, 1, 50);
            last = now;
            double diff = st.Target - sv.VerticalOffset;
            // Ease out: cover ~22% of what's left every 16 ms, whatever the frame rate.
            double k = 1 - Math.Pow(1 - 0.22, dt / 16.7);
            if (Math.Abs(diff) < 0.6 || !sv.IsVisible)
            {
                sv.ScrollToVerticalOffset(st.Target);
                CompositionTarget.Rendering -= Step;
                st.Running = false;
                return;
            }
            sv.ScrollToVerticalOffset(sv.VerticalOffset + diff * k);
        }
        CompositionTarget.Rendering += Step;
    }
}
