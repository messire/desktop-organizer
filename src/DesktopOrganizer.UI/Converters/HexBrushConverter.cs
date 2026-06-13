using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a hex colour string (e.g. <c>#DDDDDD</c>) to a <see cref="SolidColorBrush"/>.
/// </summary>
public class HexBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && TryParseColor(hex, out var color))
            return new SolidColorBrush(color);

        return Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SolidColorBrush brush ? brush.Color.ToString() : Binding.DoNothing;

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch (FormatException)
        {
            color = Colors.Transparent;
            return false;
        }
    }
}
