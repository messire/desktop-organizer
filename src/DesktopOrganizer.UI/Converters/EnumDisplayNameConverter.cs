using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Maps <see cref="PanelGeometryMode"/>/<see cref="IconLayoutPolicy"/> enum members to the SAME
/// human-readable labels <c>Views/ChildWindow.xaml</c>'s "Icon layout" context-menu radio group already
/// uses (P06 fix-cycle UX-P1, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06") — <c>MainWindow.xaml</c>'s
/// "Defaults for new panels" <c>ComboBox</c>es previously bound <c>ItemsSource</c> straight to the enum
/// values with no <c>ItemTemplate</c>, so each row showed the raw C# member name (e.g.
/// <c>"LegacyFreeform"</c>, <c>"FixedGap"</c>) — a debug-looking string that also disagreed with the
/// wording the same setting's effect uses everywhere else in the UI.
/// </summary>
/// <remarks>
/// A single shared converter (rather than one per enum) because both enums are only ever bound from the
/// same two <c>ComboBox</c> <c>ItemTemplate</c>s, and a <see langword="switch"/> pattern-matching on the
/// boxed value's runtime type/value costs nothing extra here. Falls back to <see cref="object.ToString"/>
/// for any value this converter does not recognize, so a future enum member is never silently blanked —
/// only unstyled, exactly like before this fix-cycle.
/// </remarks>
public sealed class EnumDisplayNameConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PanelGeometryMode.GridAligned => "Grid aligned",
        PanelGeometryMode.LegacyFreeform => "Free-form (legacy)",
        IconLayoutPolicy.FixedGap => "Fixed spacing",
        IconLayoutPolicy.LegacyStretch => "Adaptive stretch (legacy)",
        _ => value?.ToString() ?? string.Empty,
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
