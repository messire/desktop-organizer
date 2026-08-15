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

        return isTooltip
            ? isRolledUp
                ? "Expand"
                : "Collapse"
            : isRolledUp
                ? ""
                : "";
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
