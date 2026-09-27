using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// Converts a page identifier — DATA, never translated itself (docs/PLANS/localization.md D-5/L-10) —
/// into its display name: <c>"default"</c> shows the localized <c>Page_Default_DisplayName</c>; every
/// other identifier (e.g. a user-renamed page, or the un-translated <c>"Page 2"</c> ID) is shown exactly
/// as-is, since page IDs are not re-translated when the language changes.
/// </summary>
public sealed class PageDisplayNameConverter : IValueConverter
{
    private const string DefaultPageId = "default";

    /// <summary>Converts <paramref name="value"/> (a page identifier) to its display name — see this type's own remarks.</summary>
    /// <param name="value">The page identifier, or <see langword="null"/>/anything else (returns <see cref="string.Empty"/>).</param>
    /// <param name="targetType">Ignored — always converts to a <see cref="string"/>.</param>
    /// <param name="parameter">Unused — no converter parameter is expected.</param>
    /// <param name="culture">The culture to look up <c>Page_Default_DisplayName</c> for.</param>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string pageId)
            return string.Empty;

        return string.Equals(pageId, DefaultPageId, StringComparison.Ordinal)
            ? LocalizationResources.GetString("Page_Default_DisplayName", culture)
            : pageId;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(PageDisplayNameConverter)} only supports one-way conversion.");
}
