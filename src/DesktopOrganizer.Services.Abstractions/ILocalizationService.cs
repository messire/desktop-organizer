using System.ComponentModel;
using System.Globalization;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Hot-switchable UI localization (docs/PLANS/localization.md D-3, §2.2). UI-thread affine: every
/// mutating call — <see cref="ApplyLanguage"/> — and every event this type raises happen only on the UI
/// thread; there is no internal marshaling to it. XAML binds to <see cref="this[string]"/> via the
/// indexer path <c>"[Key]"</c> (<c>{loc:Loc Key}</c>, docs/PLANS/localization.md §2.4) so hot language
/// switches only require this type to raise <see cref="INotifyPropertyChanged.PropertyChanged"/> for the
/// synthetic <c>"Item[]"</c> property name — no window/control needs to be rebuilt (L-4).
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>
    /// Looks up <paramref name="key"/> for <see cref="CurrentCulture"/>. A missing key returns the key
    /// itself (never throws; see <c>DesktopOrganizer.Localization.LocalizationResources.GetString</c>).
    /// </summary>
    /// <param name="key">The RESX key.</param>
    string this[string key] { get; }

    /// <summary>
    /// The effective, resolved UI culture — always the neutral <c>en</c> or <c>ru</c>
    /// <see cref="CultureInfo"/>, never a region-specific one.
    /// </summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>
    /// The raw, unresolved language preference: <c>""</c> ("follow Windows"), <c>"en"</c>, or <c>"ru"</c>.
    /// A supported tag (<c>"en"</c>/<c>"ru"</c>, matched case-insensitively) is normalized to that
    /// canonical lower-case form regardless of the case passed to <see cref="ApplyLanguage"/> (fix cycle
    /// 1 NIT) — e.g. a hand-edited <c>"RU"</c> in <c>user.config</c> reads back as <c>"ru"</c> here, so
    /// every exact-case comparison against this value (such as a picker matching it against its own
    /// lower-case option tags) agrees with the effective culture <see cref="ApplyLanguage"/> actually
    /// resolved. <c>""</c> and any other, unsupported tag pass through unchanged.
    /// </summary>
    string LanguagePreference { get; }

    /// <summary>Every UI culture this app supports, in picker display order.</summary>
    IReadOnlyList<CultureInfo> SupportedCultures { get; }

    /// <summary>
    /// Raised after <see cref="INotifyPropertyChanged.PropertyChanged"/> has already fired for
    /// <c>"Item[]"</c>/<c>"CurrentCulture"</c> — i.e. only when the effective <see cref="CurrentCulture"/>
    /// itself actually changed, not merely the raw <see cref="LanguagePreference"/>. Setting a
    /// preference that resolves to the same effective culture (e.g. "" -&gt; "ru" on an already-Russian
    /// Windows) raises <c>PropertyChanged("LanguagePreference")</c> alone and does <b>not</b> raise this
    /// event; setting the exact same preference again (e.g. selecting "Follow Windows" twice) raises no
    /// events at all.
    /// </summary>
    event EventHandler<CultureChangedEventArgs>? CultureChanged;

    /// <summary>Equivalent to <see cref="this[string]"/>; provided for non-indexer call sites (C# code, not XAML bindings).</summary>
    /// <param name="key">The RESX key.</param>
    string GetString(string key);

    /// <summary>
    /// Looks up a <c>_Format</c> RESX key for <see cref="CurrentCulture"/> and formats it with
    /// <paramref name="args"/>, using <see cref="CurrentCulture"/> as the format provider.
    /// </summary>
    /// <param name="key">The <c>_Format</c> RESX key.</param>
    /// <param name="args">Positional format arguments.</param>
    string Format(string key, params object?[] args);

    /// <summary>
    /// Resolves the plural category for <paramref name="count"/> under <see cref="CurrentCulture"/> and
    /// formats <c>$"{baseKey}_{category}"</c> with <paramref name="count"/> as <c>{0}</c> followed by
    /// <paramref name="args"/> as <c>{1}</c>, <c>{2}</c>, ….
    /// </summary>
    /// <param name="baseKey">The plural base key, e.g. <c>"Plural_Items"</c>.</param>
    /// <param name="count">The cardinal count driving both category selection and <c>{0}</c>.</param>
    /// <param name="args">Additional format arguments, available as <c>{1}</c>, <c>{2}</c>, ….</param>
    string FormatPlural(string baseKey, long count, params object?[] args);

    /// <summary>
    /// Resolves <paramref name="preference"/> against <see cref="SupportedCultures"/> (following Windows
    /// for <c>""</c>/<see langword="null"/>/an unrecognised tag, D-4) and applies it as
    /// <see cref="CurrentCulture"/>/<see cref="LanguagePreference"/>. Does <b>not</b> persist
    /// <paramref name="preference"/> anywhere — the caller (UI code owning <c>IAppSettings</c>) is
    /// responsible for saving it.
    /// </summary>
    /// <param name="preference">The raw preference: <c>""</c>/<see langword="null"/>, <c>"en"</c>, or <c>"ru"</c>.</param>
    /// <returns>The resulting effective <see cref="CurrentCulture"/>.</returns>
    CultureInfo ApplyLanguage(string? preference);
}
