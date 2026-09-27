using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Picks a black-or-white <see cref="SolidColorBrush"/> that reads legibly over a given accent hex —
/// backs the swatch grid's selected-preset checkmark glyph (ADR-042, PanelColorEditor.xaml): "a check glyph
/// in the contrasting colour" against whichever of the twelve preset accents (or a custom accent) is
/// selected. Picks whichever of pure black/white has the HIGHER <see cref="ContrastCalculator.ContrastRatio"/>
/// against the accent (release-polish-1.0 review round M5) — a direct "which reads better here" measurement,
/// rather than a fixed relative-luminance threshold that can pick the visually worse of the two for some
/// accents. Never throws: unparseable input resolves to white (this app's presets are all mid-to-saturated
/// colours, where white is the safer default of the two); an exact tie also resolves to black, matching this
/// converter's own previous (threshold-based) tie direction.
/// </summary>
public sealed class AccentContrastForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && HexBrushConverter.TryParseColor(hex, out var color))
        {
            var contrastWithBlack = ContrastCalculator.ContrastRatio(color, Colors.Black);
            var contrastWithWhite = ContrastCalculator.ContrastRatio(color, Colors.White);
            return contrastWithBlack >= contrastWithWhite ? Brushes.Black : Brushes.White;
        }

        return Brushes.White;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
