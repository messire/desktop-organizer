using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a <see cref="PanelVisualState"/> into the accent indicator strip's rendered width
/// (<c>Resources/Styles/Panels.xaml</c>'s <c>PART_AccentIndicator</c>): <see cref="PanelMetrics.AccentIndicatorThickness"/>
/// (the Idle/base thickness) scaled by <see cref="InteractionVisualState.AccentEmphasisFor"/> — MAJOR-B
/// fix. <see cref="InteractionVisualState.AccentEmphasisFor"/> already existed and was already exercised
/// by <c>InteractionVisualStateTests</c>, but nothing in XAML ever consumed it (a grep for
/// <c>ConverterParameter=accent</c> across the whole UI project found zero matches) — the "third channel"
/// the phase spec's DoD relies on to help distinguish <see cref="PanelVisualState.Dragging"/> from
/// <see cref="PanelVisualState.Resizing"/> was dead code. Binding this converter's output straight to
/// Width (an Auto-sized Grid column) rather than to Opacity: the strip's Background is already fully
/// opaque (<c>AccentBrushConverter</c>'s <c>"indicator"</c> mode, alpha 1.0), so Opacity has no headroom
/// left to visibly increase with — Width does. Never throws: a non-<see cref="PanelVisualState"/> value
/// defaults to <see cref="PanelVisualState.Idle"/>.
/// </summary>
public sealed class AccentIndicatorWidthConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value is PanelVisualState s ? s : PanelVisualState.Idle;
        return PanelMetrics.AccentIndicatorThickness * InteractionVisualState.AccentEmphasisFor(state);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
