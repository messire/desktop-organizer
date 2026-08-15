using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a <see cref="PanelSortMode"/> value (bound to
/// <see cref="ViewModels.ChildWindowViewModel.EffectiveSortMode"/>) to the ToolTip text for the
/// panel's sort-mode toggle button. The button's glyph is a constant (<c>&amp;#xE8CB;</c> — Sort,
/// see <c>ChildWindow.xaml</c>) and its "selected" state is a highlight
/// (<c>PanelHeaderToggleButtonStyle</c>'s <c>DataTrigger</c> on
/// <see cref="ViewModels.ChildWindowViewModel.IsSortedByName"/> in Templates.xaml), not a glyph swap
/// — so, unlike the sibling <see cref="ViewModeGlyphConverter"/>/<see cref="BoolToGlyphConverter"/>,
/// there is no glyph branch left to convert to (ui-polishing-1 §4.3/§4.4; this type used to be named
/// <c>SortModeGlyphConverter</c> and also produced button content for a glyph that changed per mode).
/// </summary>
[ValueConversion(typeof(PanelSortMode), typeof(string))]
public sealed class SortModeTooltipConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PanelSortMode.Name
            ? "Sorted by name — click to arrange manually"
            : "Manual order — click to sort by name";

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
