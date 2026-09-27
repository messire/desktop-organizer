using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;
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
/// Text comes from <c>Panel_SortToggle_SortedByName_Tooltip</c>/<c>Panel_SortToggle_Manual_Tooltip</c>
/// (docs/PLANS/localization.md §4.3, WP-2).
/// </summary>
[ValueConversion(typeof(PanelSortMode), typeof(string))]
public sealed class SortModeTooltipConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        LocalizationResources.GetString(
            value is PanelSortMode.Name ? "Panel_SortToggle_SortedByName_Tooltip" : "Panel_SortToggle_Manual_Tooltip",
            culture);

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
