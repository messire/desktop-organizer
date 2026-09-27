using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Maps ANY enum member to its human-readable, localized label, following the
/// <c>Enum_{EnumType}_{Member}</c> RESX key convention (docs/PLANS/localization.md §2.6/L-9). Closes the
/// long-standing ROADMAP backlog item "общий EnumDisplayConverter" (docs/ROADMAP.md:1229-1232) — this used
/// to be a hardcoded <see langword="switch"/> over a hand-picked list of enum types/members (P06 fix-cycle
/// UX-P1, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06"; docs/prompts/settings-window-redesign.md WP-B),
/// which meant every new user-facing enum needed a converter change to avoid showing its raw C# member
/// name (e.g. <c>"LegacyFreeform"</c>) in the UI. It now works for ANY enum as long as its members have
/// matching RESX entries — no code change needed to localize a new enum.
/// </summary>
/// <remarks>
/// A single shared converter (rather than one per enum) because every consuming enum is only ever bound
/// from a <c>ComboBox</c>/<c>MenuItem</c> <c>ItemTemplate</c> or a direct value binding, and looking up
/// <c>$"Enum_{value.GetType().Name}_{value}"</c> costs nothing extra here. Falls back to
/// <see cref="object.ToString"/> (both for a non-<see cref="Enum"/> value and for an <see cref="Enum"/>
/// value whose key is missing from RESX) — a future enum member is never silently blanked, only
/// unstyled, exactly like the old switch's default branch.
/// </remarks>
public sealed class EnumDisplayNameConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Enum enumValue)
            return value?.ToString() ?? string.Empty;

        var key = $"Enum_{enumValue.GetType().Name}_{enumValue}";
        var localized = LocalizationResources.GetString(key, culture);

        // LocalizationResources.GetString never throws and returns the key itself when it cannot find
        // a matching RESX entry (G-L2) — that is indistinguishable here from "the key IS its own value",
        // so treat it as a miss and fall back to the enum member's own ToString(), matching the pre-WP-2
        // switch's default branch.
        return localized == key ? enumValue.ToString() : localized;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
