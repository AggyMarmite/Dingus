using System.Windows;
using System.Windows.Controls;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>Your own order for categories (Live TV, Films, TV Shows), kept per playlist across refreshes.</summary>
public static class CategoryOrder
{
    static Dictionary<string, List<string>> Store => DataService.Instance.Active?.CategoryOrder;

    /// <summary>The categories in your order (ones you haven't moved keep the provider's order, after them).</summary>
    public static List<Category> Apply(string kind, IEnumerable<Category> cats)
    {
        var list = cats.ToList();
        var s = Store;
        if (s == null || !s.TryGetValue(kind, out var ids) || ids.Count == 0) return list;
        var rank = new Dictionary<string, int>();
        for (int i = 0; i < ids.Count; i++) rank.TryAdd(ids[i], i);
        return list.Select((c, i) => (c, k: rank.TryGetValue(c.Id, out var r) ? r : ids.Count + i))
                   .OrderBy(x => x.k).Select(x => x.c).ToList();
    }

    public const int Top = int.MinValue, Bottom = int.MaxValue;

    /// <summary>Moves a category within the order you see (<paramref name="shown"/>): -1 up, +1 down, Top, Bottom.</summary>
    public static void Move(string kind, IReadOnlyList<string> shown, string id, int delta)
    {
        var s = Store;
        if (s == null) return;
        var order = shown.ToList();
        int i = order.IndexOf(id);
        if (i < 0) return;
        int j = delta == Top ? 0 : delta == Bottom ? order.Count - 1 : Math.Clamp(i + delta, 0, order.Count - 1);
        if (i == j) return;
        order.RemoveAt(i);
        order.Insert(j, id);
        // Keep any categories hidden right now in their old place at the end, so they aren't lost.
        if (s.TryGetValue(kind, out var old)) order.AddRange(old.Where(x => !order.Contains(x)));
        s[kind] = order;
        SettingsStore.Save();
    }

    public static void Reset(string kind)
    {
        if (Store?.Remove(kind) == true) SettingsStore.Save();
    }

    /// <summary>The right-click menu for a category.</summary>
    public static ContextMenu Menu(string kind, IReadOnlyList<string> shown, string id, Action changed)
    {
        var m = new ContextMenu();
        int i = shown.ToList().IndexOf(id);
        void Add(string text, int delta, bool enabled)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => { Move(kind, shown, id, delta); changed(); };
            m.Items.Add(mi);
        }
        Add("Move up", -1, i > 0);
        Add("Move down", 1, i >= 0 && i < shown.Count - 1);
        Add("Move to the top", Top, i > 0);
        Add("Move to the bottom", Bottom, i >= 0 && i < shown.Count - 1);
        m.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset to the provider's order", IsEnabled = Store?.ContainsKey(kind) == true };
        reset.Click += (_, _) => { Reset(kind); changed(); };
        m.Items.Add(reset);
        return m;
    }
}
