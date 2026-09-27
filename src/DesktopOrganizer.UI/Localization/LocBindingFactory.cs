using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// Shared binding-construction and value/binding return logic (docs/PLANS/localization.md §2.4) used by
/// <see cref="LocExtension"/>, <see cref="LocBindingExtension"/>, and <see cref="CultureBindingExtension"/>
/// — factored out so each markup extension's own <see cref="MarkupExtension.ProvideValue"/> stays a thin
/// composition of these two primitives, and so tests (docs/PLANS/localization.md §4.1: "Биндинг строится
/// через internal-фабрику") can build the exact same <see cref="Binding"/> a markup extension would
/// produce against a LOCAL <see cref="ILocalizationService"/> instance, without ever touching the
/// process-wide <see cref="LocalizationSource.Current"/>.
/// </summary>
internal static class LocBindingFactory
{
    /// <summary>
    /// Builds the indexer binding <c>{loc:Loc Key}</c> resolves to: a <see cref="BindingMode.OneWay"/>
    /// <see cref="Binding"/> on the synthetic indexer path <c>"[key]"</c>, sourced explicitly from
    /// <paramref name="source"/> — never the ambient <c>DataContext</c>, which is what lets this same
    /// binding work unchanged inside a <see cref="System.Windows.Controls.ContextMenu"/>/
    /// <see cref="System.Windows.Controls.ToolTip"/>/<see cref="System.Windows.Controls.Primitives.Popup"/>
    /// (docs/PLANS/localization.md §2.4).
    /// </summary>
    /// <param name="key">The RESX key.</param>
    /// <param name="source">The <see cref="ILocalizationService"/> to bind against.</param>
    /// <returns>The <see cref="Binding"/> <c>{loc:Loc Key}</c> would produce.</returns>
    public static Binding CreateKeyBinding(string key, ILocalizationService source) => new($"[{key}]")
    {
        Source = source,
        Mode = BindingMode.OneWay,
    };

    /// <summary>
    /// Builds the <c>{loc:CultureBinding}</c> binding: a <see cref="BindingMode.OneWay"/>
    /// <see cref="Binding"/> on <see cref="ILocalizationService.CurrentCulture"/>, sourced explicitly
    /// from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The <see cref="ILocalizationService"/> to bind against.</param>
    /// <returns>The <see cref="Binding"/> <c>{loc:CultureBinding}</c> would produce.</returns>
    public static Binding CreateCultureBinding(ILocalizationService source) => new(nameof(ILocalizationService.CurrentCulture))
    {
        Source = source,
        Mode = BindingMode.OneWay,
    };

    /// <summary>
    /// Implements the shared "Setter/template vs. real dependency property" return rule
    /// (docs/PLANS/localization.md §2.4): when the markup extension is being applied to a real
    /// <see cref="DependencyObject"/>/<see cref="DependencyProperty"/> target, delegates to
    /// <paramref name="binding"/>'s own <see cref="MarkupExtension.ProvideValue"/> so WPF wires up a
    /// live binding right away; otherwise — a <see cref="System.Windows.Setter.Value"/>, a
    /// <see cref="FrameworkTemplate"/> factory property, and similar places where
    /// <see cref="IProvideValueTarget"/> reports no dependency-property target — returns
    /// <paramref name="binding"/> itself, unevaluated, so the consumer (a <c>Style</c>/
    /// <c>ControlTemplate</c>) can apply it later, once a real target exists.
    /// </summary>
    /// <param name="binding">The <see cref="Binding"/> or <see cref="MultiBinding"/> to provide.</param>
    /// <param name="serviceProvider">The service provider WPF's XAML parser passed to <c>ProvideValue</c>.</param>
    /// <returns>The bound value (DP target) or <paramref name="binding"/> itself (Setter/template target).</returns>
    public static object ProvideValueOrBinding(BindingBase binding, IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target &&
            target.TargetObject is DependencyObject &&
            target.TargetProperty is DependencyProperty)
        {
            return binding.ProvideValue(serviceProvider);
        }

        return binding;
    }
}
