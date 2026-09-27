using System.Globalization;
using System.Windows.Data;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// Converts a RESX key stored as DATA (docs/PLANS/localization.md §2.4) — e.g.
/// <c>SettingsSectionInfo.TitleKey</c> — into its localized string for the given culture. Meant to be
/// used as <c>{loc:LocBinding TitleKey, Converter={StaticResource LocKey}}</c>'s inner
/// <see cref="IValueConverter"/>, so <see cref="LocalizedValueConverter"/> re-runs it with the live
/// culture on every hot language switch.
/// </summary>
public sealed class LocKeyConverter : IValueConverter
{
    /// <summary>Looks up <paramref name="value"/> (the RESX key) for <paramref name="culture"/>.</summary>
    /// <param name="value">The RESX key, or anything else (returns <see cref="string.Empty"/>).</param>
    /// <param name="targetType">Ignored — always converts to a <see cref="string"/>.</param>
    /// <param name="parameter">Unused — no converter parameter is expected.</param>
    /// <param name="culture">The culture to look the key up for.</param>
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? LocalizationResources.GetString(key, culture) : string.Empty;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(LocKeyConverter)} only supports one-way conversion.");
}
