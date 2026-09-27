using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Builds the Panels settings section's per-row subtitle (docs/prompts/settings-window-redesign.md
/// finding M-5) from three independent bindings: <see cref="ChildWindowModel.ViewMode"/>,
/// <see cref="ChildWindowModel.SourceFolderPath"/> and <c>ChildWindowModel.FilePaths.Count</c> (the
/// persisted item-path list — NOT the runtime-only <see cref="ChildWindowModel.FileItems"/>, which stays
/// empty for a model that has never been the live content of an on-screen <c>ChildWindow</c>, e.g. a
/// panel that is not on the currently displayed page).
/// </summary>
/// <remarks>
/// A Folder Portal panel (non-null/non-blank <see cref="ChildWindowModel.SourceFolderPath"/>) shows
/// <c>"Grid view · Folder Portal"</c> (en) — the portal note REPLACES the item count rather than
/// appending a second line, since a portal's item count is just whatever the watched folder currently
/// contains, not something the user curated. A non-portal panel shows <c>"Grid view · 12 items"</c> /
/// <c>"List view · 1 item"</c> (en). Localized (docs/PLANS/localization.md §4.3, WP-2): the view-mode
/// word comes from <c>Enum_PanelViewMode_*</c> wrapped in <c>Settings_Panels_Subtitle_View_Format</c>,
/// the portal note from <c>Settings_Panels_Subtitle_Portal</c>, and the count from
/// <see cref="LocalizationResources.FormatPlural"/>(<c>"Plural_Items"</c>, …) — so ru renders e.g.
/// <c>"Сетка · 12 элементов"</c> / <c>"Список · Портал папки"</c>.
/// </remarks>
public sealed class PanelSubtitleConverter : IMultiValueConverter
{
    /// <summary>Builds the subtitle text — see this type's remarks for the exact format.</summary>
    /// <param name="values">
    /// Expected, in order: <see cref="ChildWindowModel.ViewMode"/> (<see cref="PanelViewMode"/>),
    /// <see cref="ChildWindowModel.SourceFolderPath"/> (<see cref="string"/>, nullable), the item count
    /// (<see cref="int"/>), and OPTIONALLY a fourth element (<see cref="CultureInfo"/>) — a live
    /// <c>{loc:CultureBinding}</c> added by the consuming <c>MultiBinding</c> so the subtitle re-evaluates
    /// on a hot language switch even though <paramref name="culture"/> itself is fixed at binding-creation
    /// time (docs/PLANS/localization.md §4.3). When absent, <paramref name="culture"/> is used instead.
    /// </param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Unused — no converter parameter is expected.</param>
    /// <param name="culture">
    /// The culture to localize with when <paramref name="values"/> has no fourth (<see cref="CultureInfo"/>)
    /// element.
    /// </param>
    /// <returns>The subtitle text, or <see cref="string.Empty"/> for malformed input.</returns>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [PanelViewMode viewMode, ..] || values.Length < 3)
            return string.Empty;

        var effectiveCulture = values.Length >= 4 && values[3] is CultureInfo liveCulture ? liveCulture : culture;

        var viewModeKey = $"Enum_PanelViewMode_{viewMode}";
        var viewModeName = LocalizationResources.GetString(viewModeKey, effectiveCulture);
        var viewModeText = viewModeName == viewModeKey
            ? viewMode.ToString()
            : LocalizationResources.Format("Settings_Panels_Subtitle_View_Format", effectiveCulture, viewModeName);

        var sourceFolderPath = values[1] as string;

        if (!string.IsNullOrWhiteSpace(sourceFolderPath))
        {
            var portalText = LocalizationResources.GetString("Settings_Panels_Subtitle_Portal", effectiveCulture);
            return LocalizationResources.Format("Settings_Panels_Subtitle_Format", effectiveCulture, viewModeText, portalText);
        }

        var count = values[2] is int itemCount ? itemCount : 0;
        var countText = LocalizationResources.FormatPlural("Plural_Items", count, effectiveCulture);
        return LocalizationResources.Format("Settings_Panels_Subtitle_Format", effectiveCulture, viewModeText, countText);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
