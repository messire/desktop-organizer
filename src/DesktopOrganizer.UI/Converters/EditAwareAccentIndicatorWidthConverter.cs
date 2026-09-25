using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Edit-Layout-aware counterpart of <see cref="AccentIndicatorWidthConverter"/> (docs/PLANS/phase-6-edit-layout-ux.md
/// §1 D-6): an <see cref="IMultiValueConverter"/> over [<see cref="PanelVisualState"/>, <see langword="bool"/>
/// <c>isLayoutEditing</c>] scaling <see cref="PanelMetrics.AccentIndicatorThickness"/> by
/// <see cref="LayoutEditVisuals.AccentEmphasisFor"/> instead of <see cref="InteractionVisualState.AccentEmphasisFor"/>
/// directly — identical output to the single-value converter when <c>isLayoutEditing</c> is
/// <see langword="false"/> (see <see cref="LayoutEditVisuals"/>'s own regression guarantee). The original
/// converter is left untouched; this is what <c>PART_AccentIndicator</c> now binds through via
/// <c>MultiBinding</c>. Never throws: a non-<see cref="PanelVisualState"/> first value defaults to
/// <see cref="PanelVisualState.Idle"/>, a non-<see langword="bool"/> second value defaults to
/// <see langword="false"/>.
/// </summary>
/// <remarks>
/// ADR-030 (docs/PLANS/reference-panel-chrome.md §1 D-4) added a <c>"marker"</c> <c>parameter</c> reading:
/// scales <see cref="PanelMetrics.AccentMarkerStrokeThickness"/> (the curved marker's
/// <see cref="System.Windows.Shapes.Shape.StrokeThickness"/>) instead of
/// <see cref="PanelMetrics.AccentIndicatorThickness"/> — same <see cref="LayoutEditVisuals.AccentEmphasisFor"/>
/// multiplier either way. <see langword="null"/>/any other <c>parameter</c> keeps the original
/// <see cref="PanelMetrics.AccentIndicatorThickness"/>-based behavior (regression-tested).
/// </remarks>
public sealed class EditAwareAccentIndicatorWidthConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = values.Length > 0 && values[0] is PanelVisualState s ? s : PanelVisualState.Idle;
        var isLayoutEditing = values.Length > 1 && values[1] is bool b && b;
        var mode = (parameter as string)?.Trim();

        // string.Equals(..., OrdinalIgnoreCase) instead of ToLowerInvariant() — case-insensitive
        // comparison without allocating a lowercased copy of `mode` on every Convert call.
        var baseThickness = string.Equals(mode, "marker", StringComparison.OrdinalIgnoreCase)
            ? PanelMetrics.AccentMarkerStrokeThickness
            : PanelMetrics.AccentIndicatorThickness;

        return baseThickness * LayoutEditVisuals.AccentEmphasisFor(state, isLayoutEditing);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
