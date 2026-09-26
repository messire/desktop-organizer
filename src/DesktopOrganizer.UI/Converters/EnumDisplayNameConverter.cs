using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Maps <see cref="PanelGeometryMode"/>/<see cref="IconLayoutPolicy"/>/<see cref="PanelContentFilter"/>
/// enum members to the SAME human-readable labels <c>Views/ChildWindow.xaml</c>'s "Icon layout"/
/// "Content" context-menu radio groups already use (P06 fix-cycle UX-P1,
/// adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"; <see cref="PanelContentFilter"/> added for
/// <c>Views/PanelCustomizeWindow.xaml</c>'s Content-filter combo, docs/prompts/settings-window-redesign.md
/// WP-B) — <c>MainWindow.xaml</c>'s "Defaults for new panels" <c>ComboBox</c>es previously bound
/// <c>ItemsSource</c> straight to the enum values with no <c>ItemTemplate</c>, so each row showed the
/// raw C# member name (e.g. <c>"LegacyFreeform"</c>, <c>"FixedGap"</c>) — a debug-looking string that
/// also disagreed with the wording the same setting's effect uses everywhere else in the UI.
/// </summary>
/// <remarks>
/// A single shared converter (rather than one per enum) because every consuming enum is only ever
/// bound from a <c>ComboBox</c> <c>ItemTemplate</c>, and a <see langword="switch"/> pattern-matching on
/// the boxed value's runtime type/value costs nothing extra here. Falls back to
/// <see cref="object.ToString"/> for any value this converter does not recognize, so a future enum
/// member is never silently blanked — only unstyled, exactly like before this fix-cycle.
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
        PanelContentFilter.ShowAll => "Show all",
        PanelContentFilter.HideAll => "Hide content",
        PanelContentFilter.HideShortcuts => "Hide shortcuts",
        PanelContentFilter.FoldersOnly => "Folders only",

        // Settings-window redesign (docs/prompts/settings-window-redesign.md): "New panel defaults" surface-
        // style ComboBox and the "Move/resize outside Edit layout" RadioButton group's labels.
        PanelSurfaceStyle.Solid => "Solid",
        PanelSurfaceStyle.Ambient => "Ambient",
        PanelSurfaceStyle.Glass => "Glass",
        NormalModeLayoutGestures.MoveAndResize => "Move and resize",
        NormalModeLayoutGestures.MoveOnly => "Move only",
        NormalModeLayoutGestures.None => "None (Edit layout mode only)",
        _ => value?.ToString() ?? string.Empty,
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
