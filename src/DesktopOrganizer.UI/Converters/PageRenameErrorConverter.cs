using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Converters;

/// <summary>
/// Converts a <see cref="PageRenameResult"/> — <see cref="PageItemViewModel.RenameError"/>, Settings →
/// "Pages" inline rename (ADR-043, docs/PLANS/page-display-names.md §5.1/§5.5) — to its localized,
/// non-blocking inline hint text. <see langword="null"/> and every outcome that never becomes
/// <see cref="PageItemViewModel.RenameError"/> in the first place (<see cref="PageRenameResult.Renamed"/>,
/// <see cref="PageRenameResult.Unchanged"/>, <see cref="PageRenameResult.NotFound"/> — the page always
/// exists in the list the UI offers to rename) convert to <see cref="string.Empty"/>, same as an unknown
/// value would.
/// </summary>
public sealed class PageRenameErrorConverter : IValueConverter
{
    /// <summary>Converts <paramref name="value"/> to its localized inline hint text — see this type's own remarks.</summary>
    /// <param name="value">A <see cref="PageRenameResult"/> (boxed, since the source property is nullable), or anything else.</param>
    /// <param name="targetType">Ignored — always converts to a <see cref="string"/>.</param>
    /// <param name="parameter">Unused — no converter parameter is expected.</param>
    /// <param name="culture">The culture to look up the <c>Page_Rename_Error_*</c> RESX keys for.</param>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PageRenameResult.Empty => LocalizationResources.GetString("Page_Rename_Error_Empty", culture),
            PageRenameResult.Duplicate => LocalizationResources.GetString("Page_Rename_Error_Duplicate", culture),
            PageRenameResult.TooLong => LocalizationResources.Format("Page_Rename_Error_TooLong_Format", culture, PageNameRules.MaxLength),
            PageRenameResult.InvalidCharacters => LocalizationResources.GetString("Page_Rename_Error_InvalidCharacters", culture),
            _ => string.Empty,
        };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(PageRenameErrorConverter)} only supports one-way conversion.");
}
