using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts an item count into its rolled-up-panel display text (docs/PLANS/phase-5-ambient-accent-ui.md
/// §3.4, §5.6): empty for zero/negative/non-<see cref="int"/> input, otherwise the localized
/// <c>Plural_Items</c> form for the requested culture (docs/PLANS/localization.md §4.3, WP-2 —
/// <c>"1 item"</c>/<c>"{n} items"</c> in en, <c>"{n} элемент/элемента/элементов"</c> in ru). Never throws.
/// </summary>
public sealed class ItemCountToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int count || count <= 0)
            return string.Empty;

        return LocalizationResources.FormatPlural("Plural_Items", count, culture);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
