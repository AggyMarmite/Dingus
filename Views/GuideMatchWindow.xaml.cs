using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dingus.Services;

namespace Dingus.Views;

public record GuideSource(string Id, string Name, string Now);

/// <summary>Pick which channel in the TV guide file belongs to a playlist channel.</summary>
public partial class GuideMatchWindow : Window
{
    /// <summary>The guide channel id chosen; "" = no guide; null = match automatically.</summary>
    public string Result { get; private set; }

    readonly List<GuideSource> _all;
    readonly string _currentId;

    public GuideMatchWindow(GuideRow row)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindow.ApplyDarkTitleBar(this);
        var epg = DataService.Instance.Epg;
        _all = epg.Sources.Select(s => new GuideSource(s.Id, s.Name,
            epg.NowTitles.TryGetValue(s.Id, out var t) && t.Length > 0 ? "Now: " + t : "")).ToList();

        ForText.Text = $"Which guide channel is {row.DisplayName}?";
        _currentId = row.Variants.Select(v => epg.MatchedTo.TryGetValue(v.Id, out var x) ? x : null).FirstOrDefault(x => x != null);
        var p = DataService.Instance.Active;
        bool manual = p != null && row.Variants.Any(v => p.EpgOverrides.ContainsKey(v.Id));
        var cur = _all.FirstOrDefault(s => s.Id == _currentId);
        CurrentText.Text = _currentId == null
            ? (manual ? "Currently: no guide (your choice)." : "Currently: not matched – no guide data.")
            : $"Currently: {cur?.Name ?? _currentId} ({_currentId}){(manual ? " – your choice" : " – matched automatically")}.";

        Loaded += (_, _) =>
        {
            SearchBox.Text = EpgService.Norm(row.DisplayName).Length > 0 ? row.DisplayName : "";
            Filter();
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => Filter();

    void Filter()
    {
        var q = SearchBox.Text.Trim();
        IEnumerable<GuideSource> list = _all;
        if (q.Length > 0)
        {
            var norm = EpgService.Norm(q);
            var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list = _all
                .Select(s => (s, score: Score(s, q, norm, words)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.s);
        }
        var shown = list.Take(400).ToList();
        SourceList.ItemsSource = shown;
        CountText.Text = _all.Count == 0 ? "The TV guide hasn't loaded yet – refresh it in Settings first."
            : shown.Count == 0 ? "No guide channels match that." : $"{shown.Count:N0} of {_all.Count:N0} guide channels";
        var cur = shown.FirstOrDefault(s => s.Id == _currentId);
        if (cur != null) { SourceList.SelectedItem = cur; SourceList.ScrollIntoView(cur); }
    }

    static int Score(GuideSource s, string q, string norm, string[] words)
    {
        var name = s.Name ?? "";
        var n = EpgService.Norm(name);
        if (norm.Length > 0 && n == norm) return 100;
        if (norm.Length > 0 && EpgService.Norm(s.Id) == norm) return 90;
        if (name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) return 70;
        if (norm.Length > 2 && (n.Contains(norm) || norm.Contains(n) && n.Length > 2)) return 50;
        int hits = words.Count(w => name.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(w, StringComparison.OrdinalIgnoreCase));
        return hits == words.Length ? 30 : hits > 0 ? 10 * hits / words.Length + 1 : 0;
    }

    void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UseButton.IsEnabled = SourceList.SelectedItem != null;

    void SourceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SourceList.SelectedItem != null) Use_Click(sender, e);
    }

    void Use_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not GuideSource s) return;
        Result = s.Id;
        DialogResult = true;
    }

    void NoGuide_Click(object sender, RoutedEventArgs e) { Result = ""; DialogResult = true; }
    void Auto_Click(object sender, RoutedEventArgs e) { Result = null; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
