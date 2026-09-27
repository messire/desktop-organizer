using System.Windows.Markup;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// <c>{loc:Loc Key}</c> — the plain, hot-switching text markup extension (docs/PLANS/localization.md
/// §2.4, D-3/L-4). Produces a one-way <see cref="System.Windows.Data.Binding"/> on
/// <see cref="LocalizationSource.Current"/>'s string indexer path <c>"[Key]"</c>: since
/// <see cref="DesktopOrganizer.Services.Abstractions.ILocalizationService"/> raises
/// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for the synthetic
/// <c>"Item[]"</c> property name on every hot language switch, every element bound this way updates
/// itself with no window/control ever needing to be rebuilt.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LocExtension"/> class with no <see cref="Key"/> set
    /// yet (the parameterless XAML/design-time constructor).
    /// </summary>
    public LocExtension()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocExtension"/> class for <paramref name="key"/>.</summary>
    /// <param name="key">The RESX key, e.g. <c>{loc:Loc Common_Ok_Button}</c>.</param>
    public LocExtension(string key) => Key = key;

    /// <summary>The RESX key this extension resolves.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><see cref="Key"/> was never set (empty) — a missing
    /// <c>{loc:Loc Key}</c> argument would otherwise silently resolve to whatever
    /// <see cref="DesktopOrganizer.Services.Abstractions.ILocalizationService"/>'s string indexer returns
    /// for <c>""</c> (the key itself, i.e. an empty string) instead of failing loudly at the call site
    /// that forgot it.</exception>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            throw new InvalidOperationException($"{nameof(LocExtension)} requires a non-empty {nameof(Key)} (e.g. {{loc:Loc Common_Ok_Button}}).");

        var binding = LocBindingFactory.CreateKeyBinding(Key, LocalizationSource.Current);
        return LocBindingFactory.ProvideValueOrBinding(binding, serviceProvider);
    }
}
