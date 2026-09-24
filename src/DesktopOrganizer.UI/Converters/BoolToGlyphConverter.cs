using System.Globalization;
using System.Windows.Data;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a boolean <c>IsRolledUp</c> value to a glyph character or tooltip string.
/// Use <c>ConverterParameter="glyph"</c> (default) for the button content,
/// or <c>ConverterParameter="tooltip"</c> for the ToolTip text.
/// </summary>
[ValueConversion(typeof(bool), typeof(string))]
public sealed class BoolToGlyphConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isRolledUp = value is true;
        var isTooltip = parameter is string p && p.Equals("tooltip", StringComparison.OrdinalIgnoreCase);

        // NIT fix (docs/PLANS/workspace-grid-07-visual.md fix-cycle 1): bare "Expand"/"Collapse" reads
        // ambiguously out of context for a screen reader (could be confused with e.g. a tree-node
        // disclosure control) — "panel" ties the announced action to what this button actually does,
        // matching the "Roll up / down" wording used elsewhere for the same feature (HeaderContextMenu's
        // MenuItem, ChildWindow.xaml). RollUpMenu_Click (the button's own Click handler) toggles the
        // panel's roll-up state directly — it is also reused, unchanged, by that same context-menu
        // MenuItem, which is the only reason "Menu" appears in its name; it does not itself open a menu.
        return isTooltip
            ? isRolledUp
                ? "Expand panel"
                : "Collapse panel"
            : isRolledUp
                ? ""
                : "";
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
