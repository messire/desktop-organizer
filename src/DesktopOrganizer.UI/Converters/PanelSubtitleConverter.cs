using System.Globalization;
using System.Windows.Data;
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
/// <c>"Grid view · Folder Portal"</c> — the portal note REPLACES the item count rather than appending a
/// second line, since a portal's item count is just whatever the watched folder currently contains, not
/// something the user curated. A non-portal panel shows <c>"Grid view · 12 items"</c> /
/// <c>"List view · 1 item"</c>.
/// </remarks>
public sealed class PanelSubtitleConverter : IMultiValueConverter
{
    /// <summary>Builds the subtitle text — see this type's remarks for the exact format.</summary>
    /// <param name="values">
    /// Expected, in order: <see cref="ChildWindowModel.ViewMode"/> (<see cref="PanelViewMode"/>),
    /// <see cref="ChildWindowModel.SourceFolderPath"/> (<see cref="string"/>, nullable), and the item
    /// count (<see cref="int"/>).
    /// </param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Unused — no converter parameter is expected.</param>
    /// <param name="culture">Unused — the built subtitle text is culture-invariant.</param>
    /// <returns>The subtitle text, or <see cref="string.Empty"/> for malformed input.</returns>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [PanelViewMode viewMode, ..] || values.Length < 3)
            return string.Empty;

        var viewModeText = viewMode == PanelViewMode.Grid ? "Grid view" : "List view";
        var sourceFolderPath = values[1] as string;

        if (!string.IsNullOrWhiteSpace(sourceFolderPath))
            return $"{viewModeText} · Folder Portal";

        var count = values[2] is int itemCount ? itemCount : 0;
        var itemWord = count == 1 ? "item" : "items";
        return $"{viewModeText} · {count} {itemWord}";
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
