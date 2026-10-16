using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dingus.Services;

namespace Dingus.Views;

/// <summary>Pick a channel for a multi-view tile: categories on the left, channels with what's on now on the right.</summary>
public partial class ChannelPickerWindow : Window
{
    readonly GuideView _guide;
    public GuideRow Result { get; private set; }

    public ChannelPickerWindow(GuideView guide, string heading)
    {
        _guide = guide;
        InitializeComponent();
        Heading.Text = heading;
        SourceInitialized += (_, _) => NativeWindow.ApplyDarkTitleBar(this);
        CategoryList.ItemsSource = guide.Categories.Where(c => c.Count > 0).ToList();
        var start = guide.Categories.FirstOrDefault(c => c.Id == guide.CurrentCategoryId && c.Count > 0)
                    ?? (CategoryList.ItemsSource as List<CategoryItem>)?.FirstOrDefault();
        CategoryList.SelectedItem = start;
        Loaded += (_, _) => SearchBox.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }

    void Fill(IEnumerable<GuideRow> rows)
    {
        var epg = DataService.Instance.Epg;
        var now = DateTime.Now;
        ChannelList.ItemsSource = rows.Select(r => new ZapItem
        {
            Row = r,
            Now = epg.At(_guide.EpgIdOf(r), now)?.Title ?? "No guide information",
        }).ToList();
        if (ChannelList.Items.Count > 0) ChannelList.SelectedIndex = 0;
    }

    void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not CategoryItem c) return;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        Fill(_guide.RowsIn(c.Id));
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
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

    void Channel_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ChannelList, e.OriginalSource as DependencyObject) is ListBoxItem item)
            Pick(item.DataContext as ZapItem);
    }

    void Channel_DoubleClick(object sender, MouseButtonEventArgs e) { }

    void Pick(ZapItem z)
    {
        if (z == null) return;
        Result = z.Row;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
