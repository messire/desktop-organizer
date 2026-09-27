using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// <c>{loc:LocBinding Path, Converter=..., FormatKey=...}</c> — makes an ordinary data binding
/// hot-switch aware (docs/PLANS/localization.md §2.4, finding 2/L-9): WPF only re-runs a
/// <see cref="Binding"/>'s own <see cref="IValueConverter"/> when the SOURCE value changes, never merely
/// because the calling culture did — so a plain <c>{Binding X, Converter={StaticResource Y}}</c> never
/// notices <see cref="LocalizationSource"/> switching language. This extension instead builds a
/// <see cref="MultiBinding"/> from the original value binding plus a second binding on
/// <see cref="DesktopOrganizer.Services.Abstractions.ILocalizationService.CurrentCulture"/>, with
/// <see cref="LocalizedValueConverter"/> re-running the SAME, unmodified <see cref="Converter"/> —
/// class and parameters unchanged — whenever either one changes.
/// </summary>
public sealed class LocBindingExtension : MarkupExtension
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LocBindingExtension"/> class with no
    /// <see cref="Path"/> — the source binding targets the element's own <c>DataContext</c> directly.
    /// </summary>
    public LocBindingExtension()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocBindingExtension"/> class bound to <paramref name="path"/>.</summary>
    /// <param name="path">The binding path, e.g. <c>{loc:LocBinding EffectiveSortMode}</c>.</param>
    public LocBindingExtension(string path) => Path = path;

    /// <summary>The source binding path. <see langword="null"/> binds to the element's own <c>DataContext</c> directly.</summary>
    [ConstructorArgument("path")]
    public string? Path { get; set; }

    /// <summary>The consumer's own single-value converter, run first against the source value — never re-implemented (L-9).</summary>
    public IValueConverter? Converter { get; set; }

    /// <summary>The parameter passed through to <see cref="Converter"/> unchanged.</summary>
    public object? ConverterParameter { get; set; }

    /// <summary>The <c>_Format</c> RESX key applied, via <c>string.Format</c>, after <see cref="Converter"/> — <c>{0}</c> is the (possibly converted) value.</summary>
    public string? FormatKey { get; set; }

    /// <summary>Equivalent to <see cref="Binding.RelativeSource"/> on the source binding.</summary>
    public RelativeSource? RelativeSource { get; set; }

    /// <summary>Equivalent to <see cref="Binding.ElementName"/> on the source binding.</summary>
    public string? ElementName { get; set; }

    /// <summary>Equivalent to <see cref="Binding.Source"/> on the source binding.</summary>
    public object? Source { get; set; }

    /// <summary>Returned instead of <see cref="DependencyProperty.UnsetValue"/> whenever the pipeline cannot produce a value.</summary>
    public object? FallbackValue { get; set; }

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var valueBinding = Path is { } path ? new Binding(path) : new Binding();
        valueBinding.Mode = BindingMode.OneWay;

        if (RelativeSource is not null)
            valueBinding.RelativeSource = RelativeSource;

        if (ElementName is not null)
            valueBinding.ElementName = ElementName;

        if (Source is not null)
            valueBinding.Source = Source;

        var cultureBinding = LocBindingFactory.CreateCultureBinding(LocalizationSource.Current);

        var multiBinding = new MultiBinding
        {
            Mode = BindingMode.OneWay,
            Converter = LocalizedValueConverter.Instance,
            ConverterParameter = new LocBindingConversion(Converter, ConverterParameter, FormatKey, FallbackValue),
        };
        multiBinding.Bindings.Add(valueBinding);
        multiBinding.Bindings.Add(cultureBinding);

        if (FallbackValue is not null)
            multiBinding.FallbackValue = FallbackValue;

        return LocBindingFactory.ProvideValueOrBinding(multiBinding, serviceProvider);
    }
}
