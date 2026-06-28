using System.Globalization;
using System.Windows.Data;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Returns <see langword="true"/> when the bound enum value equals the converter parameter.
/// Used for <c>IsChecked</c> one-way bindings on checkable menu items to reflect the active filter.
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && value.Equals(parameter);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
