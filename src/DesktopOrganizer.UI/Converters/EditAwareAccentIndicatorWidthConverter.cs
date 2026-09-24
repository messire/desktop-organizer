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
public sealed class EditAwareAccentIndicatorWidthConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = values.Length > 0 && values[0] is PanelVisualState s ? s : PanelVisualState.Idle;
        var isLayoutEditing = values.Length > 1 && values[1] is bool b && b;

        return PanelMetrics.AccentIndicatorThickness * LayoutEditVisuals.AccentEmphasisFor(state, isLayoutEditing);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
