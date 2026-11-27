using Avalonia;
using Avalonia.Data.Converters;

namespace Dingus.Views;

/// <summary>Small converters for the screens' bindings.</summary>
public static class Converters
{
    /// <summary>A numbered chart card leaves room on its left for the big number behind it.</summary>
    public static readonly IValueConverter NumberedMargin = new FuncValueConverter<bool, Thickness>(n => n ? new Thickness(30, 0, 0, 0) : new Thickness(0));
}
