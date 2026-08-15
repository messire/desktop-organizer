using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts an icon size (double) to the StackPanel box width used in GridViewTemplate.
/// Returns <see cref="GridMetrics.BoxSize"/> so the template and the quantisation logic
/// share one source of truth.
/// </summary>
[ValueConversion(typeof(double), typeof(double))]
public class IconBoxWidthConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double iconSize ? GridMetrics.BoxSize(iconSize) : 72d;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
