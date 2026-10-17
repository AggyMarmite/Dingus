using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>Pick a channel (e.g. for "Record a time slot"): categories on the left, channels with what's on now on the right.</summary>
public partial class ChannelPickerWindow : Window
{
    readonly GuideView _guide;
    public GuideRow Result { get; private set; }

    public ChannelPickerWindow() : this(null, "Choose a channel") { }

    public ChannelPickerWindow(GuideView guide, string heading)
    {
        _guide = guide;
        InitializeComponent();
        Heading.Text = heading;
        if (guide == null) return;
        var cats = guide.Categories.Where(c => c.Count > 0).ToList();
        CategoryList.ItemsSource = cats;
        CategoryList.SelectedItem = cats.FirstOrDefault(c => c.Id == guide.CurrentCategoryId) ?? cats.FirstOrDefault();
        Opened += (_, _) => SearchBox.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
    }

    void Fill(IEnumerable<GuideRow> rows)
    {
        var epg = DataService.Instance.Epg;
        var now = DateTime.Now;
        var items = rows.Select(r => new ZapItem { Row = r, Now = epg.At(_guide.EpgIdOf(r), now)?.Title ?? "No guide information" }).ToList();
        ChannelList.ItemsSource = items;
        if (items.Count > 0) ChannelList.SelectedIndex = 0;
    }

    void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not CategoryItem c) return;
        if ((SearchBox.Text ?? "").Length > 0) SearchBox.Text = "";
        Fill(_guide.RowsIn(c.Id));
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var q = (SearchBox.Text ?? "").Trim();
        if (q.Length == 0) { Category_Changed(null, null); return; }
        Fill(_guide.RowsIn(GuideView.AllId).Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { ChannelList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Pick(ChannelList.SelectedItem as ZapItem); e.Handled = true; }
    }

    void ChannelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Pick(ChannelList.SelectedItem as ZapItem); e.Handled = true; }
    }

    void Channel_Click(object sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is ZapItem z) Pick(z);
    }

    void Pick(ZapItem z)
    {
        if (z == null) return;
        Result = z.Row;
        Close(true);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);
}
