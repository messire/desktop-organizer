using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Maps a panel's real icon size (<see cref="Models.SizeStepPolicy.IconSizeMinimum"/>..<see cref="Models.SizeStepPolicy.IconSizeMaximum"/>,
/// today 16-128px) to <c>PanelPreviewCard</c>'s own small, fixed placeholder-tile size range (ADR-042) — so
/// the miniature preview's tiles visibly grow/shrink with the real "Icon size" control without the preview
/// card itself needing to grow. Never throws: a non-<see cref="double"/>/non-finite input clamps to the
/// minimum tile size.
/// </summary>
public sealed class PreviewIconSizeConverter : IValueConverter
{
    /// <summary>The smallest placeholder-tile size this converter returns, at <see cref="Models.SizeStepPolicy.IconSizeMinimum"/> and below.</summary>
    internal const double MinTileSize = 24d;

    /// <summary>The largest placeholder-tile size this converter returns, at <see cref="Models.SizeStepPolicy.IconSizeMaximum"/> and above.</summary>
    internal const double MaxTileSize = 40d;

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var iconSize = value is double d && double.IsFinite(d) ? d : SizeStepPolicy.IconSizeMinimum;

        var min = SizeStepPolicy.IconSizeMinimum;
        var max = SizeStepPolicy.IconSizeMaximum;
        var clamped = Math.Clamp(iconSize, min, max);

        var t = max > min ? (clamped - min) / (max - min) : 0d;
        return MinTileSize + (t * (MaxTileSize - MinTileSize));
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
