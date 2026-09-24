using System.Globalization;
using System.Windows.Data;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts an item count into its rolled-up-panel display text (docs/PLANS/phase-5-ambient-accent-ui.md
/// §3.4, §5.6): empty for zero/negative/non-<see cref="int"/> input, <c>"1 item"</c> singular, otherwise
/// <c>"{n} items"</c>. Never throws.
/// </summary>
public sealed class ItemCountToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int count || count <= 0)
            return string.Empty;

        return count == 1 ? "1 item" : $"{count} items";
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
