using System.Globalization;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Localization;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// The one sanctioned static access point (docs/PLANS/localization.md L-5) for the app's single
/// <see cref="ILocalizationService"/> instance — used by every markup extension in this namespace,
/// by localization-aware converters, by code-behind, and by static helpers (e.g. <c>PanelDefaults</c>)
/// that WPF/CLR construct outside of DI. ViewModels and services still get
/// <see cref="ILocalizationService"/> injected through their own constructor (L-5) — this type exists
/// only for the call sites DI cannot reach, because WPF constructs markup extensions and value
/// converters itself, straight from parsed XAML.
/// </summary>
public static class LocalizationSource
{
    private static ILocalizationService? _current;
    private static bool _currentWasRead;

    /// <summary>
    /// The app's single <see cref="ILocalizationService"/> instance. Before <see cref="Initialize"/> is
    /// ever called — unit tests, the XAML designer — this lazily creates a fresh, English
    /// <see cref="LocalizationService"/> the first time it is read, so every consumer always sees a
    /// deterministic English string rather than <see langword="null"/> or whatever culture the calling
    /// thread happens to have (docs/PLANS/localization.md R-5).
    /// </summary>
    public static ILocalizationService Current
    {
        get
        {
            _current ??= new LocalizationService(CultureInfo.GetCultureInfo("en"));
            _currentWasRead = true;
            return _current;
        }
    }

    /// <summary>
    /// Sets the app's real, DI-resolved <see cref="ILocalizationService"/> instance. Must be called
    /// exactly once, from <c>App.StartApp</c>, before any XAML is parsed (docs/PLANS/localization.md
    /// §2.5). Calling it again with a DIFFERENT instance after <see cref="Current"/> has already been
    /// read throws: elements/bindings created against the OLD instance captured it as their binding
    /// <c>Source</c> and would silently keep pointing at it forever, never seeing further language
    /// switches.
    /// </summary>
    /// <param name="service">The real <see cref="ILocalizationService"/> to install as <see cref="Current"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Current"/> was already read by some earlier caller, and <paramref name="service"/> is
    /// not the same instance that read already returned.
    /// </exception>
    public static void Initialize(ILocalizationService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (_currentWasRead && !ReferenceEquals(_current, service))
        {
            throw new InvalidOperationException(
                $"{nameof(LocalizationSource)}.{nameof(Current)} was already read with a different " +
                $"{nameof(ILocalizationService)} instance — bindings created against that instance would " +
                $"silently stop updating. {nameof(Initialize)}() must run before any XAML is parsed.");
        }

        _current = service;
    }

    /// <summary>
    /// Resets <see cref="Current"/>/<see cref="Initialize"/>'s "already read" guard back to their
    /// pre-startup state. Test-only, and safe only inside a non-parallel test collection — this is a
    /// process-wide static, exactly like <see cref="Current"/> itself.
    /// </summary>
    internal static void ResetForTests()
    {
        _current = null;
        _currentWasRead = false;
    }
}
