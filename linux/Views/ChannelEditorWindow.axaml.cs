using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Dingus.Models;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>One line in the channel editor.</summary>
public class EditRow : Observable
{
    public GuideRow Row { get; init; }

    public string Name
    {
        get => Row.DisplayName;
        set
        {
            ChannelEditorWindow.SetName(Row, value);
            Raise();
            Raise(nameof(HasCustomName));
        }
    }

    public bool Visible
    {
        get => !Row.IsHidden;
        set { ChannelEditorWindow.SetHidden(Row, !value); Raise(); Raise(nameof(NameOpacity)); }
    }

    public bool HasCustomName => Row.CustomName != null;
    public double NameOpacity => Row.IsHidden ? 0.45 : 1;

    public void Refresh() { Raise(nameof(Name)); Raise(nameof(Visible)); Raise(nameof(HasCustomName)); Raise(nameof(NameOpacity)); }
}

public partial class ChannelEditorWindow : Window
{
    readonly GuideView _guide;
    ObservableCollection<EditRow> _rows = new();
    string _cat;

    static PlaylistConfig Active => DataService.Instance.Active;

    public ChannelEditorWindow() { InitializeComponent(); }

    public ChannelEditorWindow(GuideView guide, string catId)
    {
        InitializeComponent();
        _guide = guide;
        var cats = guide.EditableCategories();
        CategoryList.ItemsSource = cats;
        CategoryList.SelectedItem = cats.FirstOrDefault(c => c.Id == catId) ?? cats.FirstOrDefault();
        Closed += (_, _) => SettingsStore.Save();
    }

    // ------------------------------------------------------------------ editing (also used by the guide's right-click menu)

    public static void SetName(GuideRow r, string name)
    {
        var p = Active;
        if (p == null || r == null) return;
        foreach (var v in r.Variants) p.ChannelNames.Remove(v.Id);
        name = name?.Trim();
        if (!string.IsNullOrEmpty(name) && name != r.OriginalName && name != r.Channel.Name)
        {
            p.ChannelNames[r.Channel.Id] = name;
            r.CustomName = name;
        }
        else r.CustomName = null;
    }

    public static void SetHidden(GuideRow r, bool hidden)
    {
        var p = Active;
        if (p == null || r == null) return;
        var ids = r.Variants.Select(v => v.Id).ToHashSet();
        p.HiddenChannels.RemoveAll(ids.Contains);
        if (hidden) p.HiddenChannels.AddRange(ids);
        r.IsHidden = hidden;
    }

    // ------------------------------------------------------------------ list

    void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not CategoryItem c) return;
        _cat = c.Id;
        Load();
    }

    void Load()
    {
        var filter = (FilterBox.Text ?? "").Trim();
        var rows = _guide.EditableRows(_cat);
        _all = rows.Select(r => new EditRow { Row = r }).ToList();
        IEnumerable<EditRow> shown = _all;
        if (filter.Length > 0)
            shown = _all.Where(x => x.Row.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                    x.Row.Channel.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        _rows = new ObservableCollection<EditRow>(shown);
        RowList.ItemsSource = _rows;
        UpdateCounts();
    }

    List<EditRow> _all = new();

    void UpdateCounts()
    {
        int hidden = _all.Count(x => !x.Visible);
        CountText.Text = $"{_all.Count:N0} channels" + (hidden > 0 ? $" · {hidden:N0} hidden" : "");
        StatusText.Text = (FilterBox.Text ?? "").Trim().Length > 0 ? "Clear the filter to move channels." : "";
    }

    DispatcherTimer _filterTimer;

    void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filterTimer == null)
        {
            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); Load(); };
        }
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    // ------------------------------------------------------------------ moving

    static EditRow RowOf(object sender) => (sender as Control)?.DataContext as EditRow;

    bool CanMove()
    {
        if ((FilterBox.Text ?? "").Trim().Length == 0) return true;
        StatusText.Text = "Clear the filter to move channels.";
        return false;
    }

    void Up_Click(object sender, RoutedEventArgs e) => Move(RowOf(sender), -1);
    void Down_Click(object sender, RoutedEventArgs e) => Move(RowOf(sender), 1);
    void Top_Click(object sender, RoutedEventArgs e) => Move(RowOf(sender), int.MinValue);

    void Move(EditRow r, int delta)
    {
        if (r == null || !CanMove()) return;
        int i = _rows.IndexOf(r);
        if (i < 0) return;
        int j = delta == int.MinValue ? 0 : Math.Clamp(i + delta, 0, _rows.Count - 1);
        if (j == i) return;
        _rows.Move(i, j);
        _all = _rows.ToList();
        SaveOrder();
        RowList.SelectedItem = r;
        RowList.ScrollIntoView(r);
    }

    void SaveOrder()
    {
        var p = Active;
        if (p == null || _cat == null) return;
        p.ChannelOrder[_cat] = _rows.Select(x => x.Row.Channel.Id).ToList();
    }

    void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        Active?.ChannelOrder.Remove(_cat);
        Load();
    }

    void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        Active?.HiddenChannels.Clear();
        foreach (var x in _guide.EditableRows(GuideView.AllId)) x.IsHidden = false;
        foreach (var x in _all) x.Refresh();
        UpdateCounts();
    }

    void ResetName_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } r) r.Name = "";
    }

    void Header_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not TextBox) BeginMoveDrag(e);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
