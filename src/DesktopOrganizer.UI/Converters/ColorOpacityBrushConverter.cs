using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Combines a hex colour string and an opacity (0.0–1.0) into a <see cref="SolidColorBrush"/>
/// whose alpha channel reflects the opacity. Applying alpha to the brush (instead of the element's
/// Opacity) keeps the panel background translucent without fading its icons or text.
/// </summary>
public class ColorOpacityBrushConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string hex)
            return Brushes.Transparent;

        var opacity = values[1] is double d ? d : 1.0;
        opacity = Math.Clamp(opacity, 0.0, 1.0);

        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }

        color.A = (byte)Math.Round(opacity * 255);
        return new SolidColorBrush(color);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
