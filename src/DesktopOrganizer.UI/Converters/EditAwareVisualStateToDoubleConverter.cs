using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Edit-Layout-aware counterpart of <see cref="VisualStateToDoubleConverter"/> (docs/PLANS/phase-6-edit-layout-ux.md
/// §1 D-6): an <see cref="IMultiValueConverter"/> over [<see cref="PanelVisualState"/>, <see langword="bool"/>
/// <c>isLayoutEditing</c>] that delegates to <see cref="LayoutEditVisuals"/> instead of
/// <see cref="InteractionVisualState"/> directly. <see cref="LayoutEditVisuals"/> itself returns exactly
/// <see cref="InteractionVisualState"/>'s own value whenever <c>isLayoutEditing</c> is
/// <see langword="false"/>, so this converter is a strict superset of <see cref="VisualStateToDoubleConverter"/>'s
/// behavior — the original single-value converter is left untouched (its own dedicated unit tests keep
/// covering it exactly as before) and this new sibling is what <c>Resources/Styles/Panels.xaml</c>'s
/// <c>PART_InteractionOverlay</c>/<c>PART_HeaderActions</c> now bind through instead, via
/// <c>MultiBinding</c>. Same "never throw" contract as the original: a non-<see cref="PanelVisualState"/>
/// first value defaults to <see cref="PanelVisualState.Idle"/>, a non-<see langword="bool"/> second value
/// defaults to <see langword="false"/>, and an unrecognized <c>parameter</c> defaults to the <c>"border"</c>
/// reading.
/// </summary>
public sealed class EditAwareVisualStateToDoubleConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = values.Length > 0 && values[0] is PanelVisualState s ? s : PanelVisualState.Idle;
        var isLayoutEditing = values.Length > 1 && values[1] is bool b && b;
        var mode = (parameter as string)?.Trim().ToLowerInvariant();

        return mode switch
        {
            "chrome" => LayoutEditVisuals.ChromeOpacityFor(state, isLayoutEditing),
            _ => LayoutEditVisuals.BorderOpacityFor(state, isLayoutEditing),
        };
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
