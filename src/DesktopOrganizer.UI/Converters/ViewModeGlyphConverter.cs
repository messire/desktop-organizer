using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a <see cref="PanelViewMode"/> value (bound to
/// <see cref="Models.ChildWindowModel.ViewMode"/>) to the glyph or tooltip for the panel's
/// view-mode toggle button. Use <c>ConverterParameter="glyph"</c> (default) for the button content,
/// or <c>ConverterParameter="tooltip"</c> for the ToolTip text — same two-parameter pattern as
/// <see cref="BoolToGlyphConverter"/>'s roll-up glyph/tooltip pair. The glyph itself is not
/// localizable text (a Segoe MDL2 Assets code point); the tooltip comes from
/// <c>Panel_ViewToggle_SwitchToList_Tooltip</c>/<c>Panel_ViewToggle_SwitchToGrid_Tooltip</c>
/// (docs/PLANS/localization.md §4.3, WP-2).
/// </summary>
/// <remarks>
/// Glyph convention (ui-polishing-1 §4.2): the button shows the <b>target</b> mode — what clicking it
/// switches TO, not the mode currently active. This mirrors <see cref="SortModeTooltipConverter"/>'s
/// sibling button, where state (not function) is communicated by a highlight rather than by swapping
/// the glyph; here it's the opposite split — the glyph itself carries the "what will happen" meaning,
/// with the ToolTip removing any remaining ambiguity.
/// </remarks>
[ValueConversion(typeof(PanelViewMode), typeof(string))]
public sealed class ViewModeGlyphConverter : IValueConverter
{
    /// <summary>Segoe MDL2 Assets glyph for the "switch to grid view" target (ViewAll, U+E8A9).</summary>
    private const string GridTargetGlyph = "\uE8A9";

    /// <summary>Segoe MDL2 Assets glyph for the "switch to list view" target (List, U+E8FD).</summary>
    private const string ListTargetGlyph = "\uE8FD";

    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isGrid = value is PanelViewMode.Grid;
        var isTooltip = parameter is string p && p.Equals("tooltip", StringComparison.OrdinalIgnoreCase);

        // Currently Grid -> next click switches to List; currently List (or unset) -> next click
        // switches to Grid. The glyph/tooltip always describe that upcoming target mode.
        return isTooltip
            ? LocalizationResources.GetString(
                isGrid ? "Panel_ViewToggle_SwitchToList_Tooltip" : "Panel_ViewToggle_SwitchToGrid_Tooltip",
                culture)
            : isGrid
                ? ListTargetGlyph
                : GridTargetGlyph;
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
