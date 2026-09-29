using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// Converts a page's name binding value to its display name via <see cref="PageDisplayNames.Resolve"/>
/// (ADR-043, docs/PLANS/page-display-names.md §5.3): accepts a <see cref="PageDisplayInfo"/> or
/// <see cref="PageDescriptor"/> (the current bindings — <c>{loc:LocBinding Display, ...}</c> against a
/// <c>PageItemViewModel</c>, or a raw <see cref="PageDescriptor"/> item), or — for backward compatibility
/// with call sites that still bind a bare page identifier — a <see cref="string"/>, treated as an unnamed
/// page (i.e. only <c>"default"</c> resolves to the localized <c>Page_Default_DisplayName</c>; any other
/// identifier is shown exactly as-is, since page IDs themselves are never re-translated, docs/PLANS/localization.md D-5/L-10).
/// </summary>
public sealed class PageDisplayNameConverter : IValueConverter
{
    /// <summary>Converts <paramref name="value"/> to its display name — see this type's own remarks.</summary>
    /// <param name="value">
    /// A <see cref="PageDisplayInfo"/>, a <see cref="PageDescriptor"/>, a raw page identifier
    /// <see cref="string"/>, or <see langword="null"/>/anything else (returns <see cref="string.Empty"/>).
    /// </param>
    /// <param name="targetType">Ignored — always converts to a <see cref="string"/>.</param>
    /// <param name="parameter">Unused — no converter parameter is expected.</param>
    /// <param name="culture">The culture to look up <c>Page_Default_DisplayName</c> for.</param>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PageDisplayInfo info => PageDisplayNames.Resolve(info.Id, info.Name, culture),
            PageDescriptor descriptor => PageDisplayNames.Resolve(descriptor.Id, descriptor.Name, culture),
            string pageId => PageDisplayNames.Resolve(pageId, null, culture),
            _ => string.Empty,
        };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(PageDisplayNameConverter)} only supports one-way conversion.");
}
