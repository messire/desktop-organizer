using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a <see cref="PanelVisualState"/> into the opacity/emphasis double it renders with,
/// selected by <c>parameter</c>: <c>"border"</c>, <c>"chrome"</c>, or <c>"accent"</c> (docs/PLANS/phase-5-ambient-accent-ui.md
/// §3.4), delegating to <see cref="InteractionVisualState"/>. Never throws: a non-<see cref="PanelVisualState"/>
/// value defaults to <see cref="PanelVisualState.Idle"/>, and an unrecognized parameter defaults to
/// the <c>"border"</c> reading.
/// </summary>
public sealed class VisualStateToDoubleConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value is PanelVisualState s ? s : PanelVisualState.Idle;
        var mode = (parameter as string)?.Trim().ToLowerInvariant();

        return mode switch
        {
            "chrome" => InteractionVisualState.ChromeOpacityFor(state),
            "accent" => InteractionVisualState.AccentEmphasisFor(state),
            _ => InteractionVisualState.BorderOpacityFor(state),
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
