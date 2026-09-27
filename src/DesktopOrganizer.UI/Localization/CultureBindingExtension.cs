using System.Windows.Data;
using System.Windows.Markup;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// <c>{loc:CultureBinding}</c> — a one-way <see cref="Binding"/> on
/// <see cref="DesktopOrganizer.Services.Abstractions.ILocalizationService.CurrentCulture"/>, sourced
/// from <see cref="LocalizationSource.Current"/> (docs/PLANS/localization.md §2.4). Exists to be added
/// as an extra child binding to an ALREADY-EXISTING <see cref="MultiBinding"/> that needs to know which
/// culture to convert/format against — e.g. <c>PanelsSection.xaml</c>'s own <see cref="MultiBinding"/> —
/// without that consumer having to hand-build the binding itself.
/// </summary>
public sealed class CultureBindingExtension : MarkupExtension
{
    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = LocBindingFactory.CreateCultureBinding(LocalizationSource.Current);
        return LocBindingFactory.ProvideValueOrBinding(binding, serviceProvider);
    }
}
