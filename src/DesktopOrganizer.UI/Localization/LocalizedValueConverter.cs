using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// The payload <see cref="LocBindingExtension"/> passes as <see cref="MultiBinding.ConverterParameter"/>
/// to <see cref="LocalizedValueConverter"/> — bundles the ORIGINAL, unmodified consumer-supplied
/// <see cref="IValueConverter"/>/parameter/format key/fallback (docs/PLANS/localization.md §2.4/L-9) so
/// <see cref="LocalizedValueConverter"/> itself stays a single, generic pipeline with no per-usage state
/// of its own.
/// </summary>
/// <param name="Converter">The consumer's own single-value converter, run first, or <see langword="null"/>.</param>
/// <param name="ConverterParameter">The parameter passed through to <paramref name="Converter"/> unchanged.</param>
/// <param name="FormatKey">The <c>_Format</c> RESX key applied after <paramref name="Converter"/>, or <see langword="null"/>.</param>
/// <param name="FallbackValue">Returned instead of <see cref="DependencyProperty.UnsetValue"/> on any failure.</param>
internal sealed record LocBindingConversion(IValueConverter? Converter, object? ConverterParameter, string? FormatKey, object? FallbackValue);

/// <summary>
/// The <see cref="IMultiValueConverter"/> every <c>{loc:LocBinding}</c> produces
/// (docs/PLANS/localization.md §2.4/L-9, finding 2): re-evaluates the SAME, unmodified inner
/// <see cref="IValueConverter"/> (if any) with the LIVE
/// <see cref="DesktopOrganizer.Services.Abstractions.ILocalizationService.CurrentCulture"/> every time
/// either the source value or the culture itself changes, then optionally formats the result through a
/// <c>_Format</c> RESX key. Never throws — any exception from the inner converter or from formatting is
/// swallowed and turned into <see cref="LocBindingConversion.FallbackValue"/> (or
/// <see cref="DependencyProperty.UnsetValue"/> if none was given).
/// </summary>
internal sealed class LocalizedValueConverter : IMultiValueConverter
{
    /// <summary>The single shared instance every <see cref="LocBindingExtension"/> assigns as its <see cref="MultiBinding.Converter"/>.</summary>
    public static readonly LocalizedValueConverter Instance = new();

    private LocalizedValueConverter()
    {
    }

    /// <inheritdoc />
    /// <param name="values">Exactly two: <c>[0]</c> the source value (the LocBinding's own <c>Path</c> binding), <c>[1]</c> the live culture.</param>
    /// <param name="targetType">Unused — the pipeline always converts to <see cref="object"/>.</param>
    /// <param name="parameter">The <see cref="LocBindingConversion"/> built by <see cref="LocBindingExtension"/>.</param>
    /// <param name="culture">Ignored — <paramref name="values"/><c>[1]</c> (the LIVE culture) is used instead, per L-3.</param>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var conversion = parameter as LocBindingConversion;

        try
        {
            if (values.Length == 0 || values[0] == DependencyProperty.UnsetValue)
                return conversion?.FallbackValue ?? DependencyProperty.UnsetValue;

            var effectiveCulture = values.Length > 1 && values[1] is CultureInfo liveCulture
                ? liveCulture
                : culture;

            var value = values[0];

            if (conversion?.Converter is { } innerConverter)
                value = innerConverter.Convert(value, typeof(object), conversion.ConverterParameter, effectiveCulture);

            if (value is null || value == DependencyProperty.UnsetValue)
                return conversion?.FallbackValue ?? DependencyProperty.UnsetValue;

            if (conversion?.FormatKey is { } formatKey)
                value = LocalizationResources.Format(formatKey, effectiveCulture, value);

            return value;
        }
        catch (Exception ex)
        {
            // Never throws (docs/PLANS/localization.md §2.4) — a misbehaving inner converter must not
            // take down the whole binding pipeline. Only the exception's type/message is logged, via
            // Debug.WriteLine, matching this codebase's convention for swallowed exceptions elsewhere
            // (e.g. NativePanelResizeController.ProposeBounds).
            Debug.WriteLine($"{nameof(LocalizedValueConverter)}: swallowed {ex.GetType().Name} converting a {{loc:LocBinding}} value — {ex.Message}");
            return conversion?.FallbackValue ?? DependencyProperty.UnsetValue;
        }
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(LocalizedValueConverter)} only supports one-way bindings.");
}
