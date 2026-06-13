using System.Globalization;
using System.Windows.Data;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Returns <see langword="true"/> when the bound value equals the converter parameter.
/// Used to highlight the active page tab without a DataTrigger whose Value
/// cannot itself be a binding.
/// </summary>
public class EqualityToBoolConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length >= 2 && Equals(values[0], values[1]);

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
