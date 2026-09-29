using System.Globalization;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// Pure helper resolving a page's effective display name for a given culture (ADR-043,
/// docs/PLANS/page-display-names.md §5.3): a stored <see cref="DesktopOrganizer.UI.Models.PageDescriptor.Name"/>
/// wins whenever present; an unnamed <c>"default"</c> page falls back to the localized
/// <c>Page_Default_DisplayName</c>; any other unnamed page (corrupted/hand-edited data —
/// <c>PagesStateMapper.FromDto</c> already repairs this at load time, so this branch should not be
/// reachable through normal use) falls back to its own id. Used by <c>PageDisplayNameConverter</c> (XAML
/// binding) and mirrors the same rule <c>PageNameRules.EffectiveName</c> applies for uniqueness
/// validation — kept as two separate methods only because one takes a resolved localized string
/// (<c>PageNameRules</c>, Services-layer-adjacent, no culture/resource dependency) and the other a
/// <see cref="CultureInfo"/> (this type, XAML-binding-adjacent) — see docs/PLANS/page-display-names.md
/// §4.2's own remarks on why the two are not merged into one.
/// </summary>
public static class PageDisplayNames
{
    private const string DefaultPageId = "default";

    /// <summary>Resolves the effective display name for a page.</summary>
    /// <param name="id">The page's stable identifier.</param>
    /// <param name="name">The page's stored display name, or <see langword="null"/>/blank when unnamed.</param>
    /// <param name="culture">The culture to look up <c>Page_Default_DisplayName</c> for, when <paramref name="id"/> is the unnamed <c>"default"</c> page.</param>
    public static string Resolve(string id, string? name, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(culture);

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return string.Equals(id, DefaultPageId, StringComparison.Ordinal)
            ? LocalizationResources.GetString("Page_Default_DisplayName", culture)
            : id;
    }
}
