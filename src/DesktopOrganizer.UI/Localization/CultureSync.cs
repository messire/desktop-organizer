using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Localization;

/// <summary>
/// Keeps every <see cref="Window"/>'s <see cref="FrameworkElement.LanguageProperty"/> — and the UI
/// thread's own <see cref="CultureInfo.CurrentUICulture"/>/<see cref="CultureInfo.DefaultThreadCurrentUICulture"/>
/// — in sync with <see cref="ILocalizationService.CurrentCulture"/> (docs/PLANS/localization.md §2.4/L-6).
/// Deliberately never touches <see cref="CultureInfo.CurrentCulture"/> itself: regional number/date
/// FORMAT stays whatever Windows is set to — only the UI TEXT language changes (L-6).
/// </summary>
internal static class CultureSync
{
    private static bool _languageMetadataOverridden;
    private static bool _classHandlerRegistered;
    private static ILocalizationService? _localization;
    private static Application? _application;

    /// <summary>
    /// Wires <paramref name="localization"/> into <paramref name="application"/>: overrides
    /// <see cref="FrameworkElement.LanguageProperty"/>'s default metadata once, before the first
    /// <see cref="Window"/> exists; registers a class handler — once per process — that stamps every
    /// <see cref="FrameworkElement.LoadedEvent"/>-raising <see cref="Window"/> with the currently attached
    /// service's culture; and subscribes to <see cref="ILocalizationService.CultureChanged"/> to keep both
    /// the thread's UI culture and every already-open <see cref="Window"/>'s
    /// <see cref="FrameworkElement.Language"/> current from then on. Idempotent and re-attachable: calling
    /// this again (e.g. across independent tests in the same process) replaces the previously attached
    /// <paramref name="localization"/>/<paramref name="application"/> pair — the earlier
    /// <see cref="ILocalizationService.CultureChanged"/> subscription is removed first — rather than
    /// stacking a second, duplicate subscription or re-registering the class handler.
    /// </summary>
    /// <param name="localization">The single <see cref="ILocalizationService"/> instance driving this app.</param>
    /// <param name="application">The <see cref="Application"/> whose <see cref="Application.Windows"/> are kept in sync.</param>
    public static void Attach(ILocalizationService localization, Application application)
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(application);

        OverrideLanguageMetadata(localization.CurrentCulture);

        if (_localization is not null)
            _localization.CultureChanged -= OnCultureChanged;

        _localization = localization;
        _application = application;
        _localization.CultureChanged += OnCultureChanged;

        RegisterClassHandlerOnce();
    }

    /// <summary>
    /// Detaches whatever <see cref="ILocalizationService"/>/<see cref="Application"/> pair is currently
    /// attached — unsubscribing from <see cref="ILocalizationService.CultureChanged"/> — and clears them,
    /// so a subsequent <see cref="FrameworkElement.Loaded"/> is a no-op until <see cref="Attach"/> runs again.
    /// Test-only (docs/PLANS/localization.md §4.1): lets each test start from a clean slate without
    /// leaking a subscription onto a throwaway <see cref="ILocalizationService"/> instance, or onto a
    /// <see cref="Window"/> owned by a previous test. Deliberately does not reset
    /// <see cref="_languageMetadataOverridden"/> or unregister the class handler — both are genuinely
    /// process-lifetime, one-time WPF registrations that cannot be undone.
    /// </summary>
    internal static void ResetForTests()
    {
        if (_localization is not null)
            _localization.CultureChanged -= OnCultureChanged;

        _localization = null;
        _application = null;
    }

    /// <summary>
    /// Overrides <see cref="FrameworkElement.LanguageProperty"/>'s default metadata for every
    /// <see cref="FrameworkElement"/>, exactly once per process — WPF throws if the same
    /// <see cref="DependencyProperty"/> is overridden twice for the same owner type, and only an
    /// override registered before the type's first instance is constructed is honoured, so this must
    /// run before the first <see cref="Window"/> does (docs/PLANS/localization.md §2.4).
    /// </summary>
    /// <param name="culture">The initial culture to default <see cref="FrameworkElement.Language"/> to.</param>
    private static void OverrideLanguageMetadata(CultureInfo culture)
    {
        if (_languageMetadataOverridden)
            return;

        _languageMetadataOverridden = true;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.Name)));
    }

    /// <summary>
    /// Registers <see cref="OnWindowLoaded"/> as a <see cref="Window"/> class handler for
    /// <see cref="FrameworkElement.LoadedEvent"/>, exactly once per process — WPF class handlers stack
    /// indefinitely with no way to remove one, so re-registering on every <see cref="Attach"/> call would
    /// stamp each newly loaded <see cref="Window"/> once per past <see cref="Attach"/> call.
    /// </summary>
    private static void RegisterClassHandlerOnce()
    {
        if (_classHandlerRegistered)
            return;

        _classHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    /// <summary>
    /// Stamps <paramref name="sender"/> with the currently attached service's
    /// <see cref="ILocalizationService.CurrentCulture"/>. A no-op once <see cref="ResetForTests"/> has run
    /// and nothing has re-<see cref="Attach"/>ed yet.
    /// </summary>
    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && _localization is not null)
            window.Language = XmlLanguage.GetLanguage(_localization.CurrentCulture.Name);
    }

    /// <summary>The named <see cref="ILocalizationService.CultureChanged"/> handler — named, rather than a
    /// lambda, so <see cref="Attach"/>/<see cref="ResetForTests"/> can unsubscribe the exact previous
    /// subscription instead of leaking it.</summary>
    private static void OnCultureChanged(object? sender, CultureChangedEventArgs args)
    {
        if (_application is not null)
            ApplyCulture(_application, args.NewCulture);
    }

    /// <summary>
    /// Applies <paramref name="newCulture"/> to the UI thread's own culture and to every window
    /// currently open in <paramref name="application"/>.
    /// </summary>
    /// <param name="application">The <see cref="Application"/> whose <see cref="Application.Windows"/> are updated.</param>
    /// <param name="newCulture">The newly effective UI culture.</param>
    private static void ApplyCulture(Application application, CultureInfo newCulture)
    {
        CultureInfo.DefaultThreadCurrentUICulture = newCulture;
        CultureInfo.CurrentUICulture = newCulture;

        var language = XmlLanguage.GetLanguage(newCulture.Name);
        foreach (Window window in application.Windows)
            window.Language = language;
    }
}
