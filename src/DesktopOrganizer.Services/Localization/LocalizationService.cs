using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Localization;

/// <inheritdoc cref="ILocalizationService" />
/// <remarks>
/// Never touches thread culture (<see cref="CultureInfo.CurrentUICulture"/>/
/// <see cref="CultureInfo.DefaultThreadCurrentUICulture"/>) itself — that is
/// <c>DesktopOrganizer.UI.Localization.CultureSync</c>'s job (docs/PLANS/localization.md L-3/L-6), kept
/// separate so this service has no thread-global side effects and is safe to construct freely in tests.
/// </remarks>
public sealed class LocalizationService : ILocalizationService
{
    private readonly CultureInfo _systemUiCulture;
    private readonly IDiagnosticsLog? _diagnosticsLog;
    private string _languagePreference;
    private CultureInfo _currentCulture;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalizationService"/> class.
    /// </summary>
    /// <param name="systemUiCulture">
    /// The Windows UI culture "follow Windows" resolves against (production:
    /// <see cref="SystemCulture.OriginalUiCulture"/>). Passing an explicit English/Russian culture here
    /// makes tests deterministic regardless of the test runner machine's own culture
    /// (docs/PLANS/localization.md R-5).
    /// </param>
    /// <param name="initialPreference">
    /// The initial raw preference — <c>""</c>/<see langword="null"/> (follow Windows), <c>"en"</c>, or
    /// <c>"ru"</c>. Defaults to <see langword="null"/> (follow Windows), matching
    /// <c>IAppSettings.Language</c>'s own default.
    /// </param>
    /// <param name="diagnosticsLog">
    /// Sink for a <see cref="CultureChanged"/> subscriber's exception (fix cycle 1 MINOR-1) — optional so
    /// every existing two-argument call site (tests constructing this service directly, per the type's
    /// own remarks) keeps compiling unchanged. <see langword="null"/> (the default) falls back to
    /// <see cref="Debug.WriteLine(string)"/> instead of <see cref="IDiagnosticsLog.Write"/>.
    /// </param>
    public LocalizationService(CultureInfo systemUiCulture, string? initialPreference = null, IDiagnosticsLog? diagnosticsLog = null)
    {
        _systemUiCulture = systemUiCulture ?? throw new ArgumentNullException(nameof(systemUiCulture));
        _languagePreference = NormalizePreferenceCasing(initialPreference ?? SupportedLanguages.FollowSystem);
        _currentCulture = SupportedLanguages.Resolve(_languagePreference, _systemUiCulture);
        _diagnosticsLog = diagnosticsLog;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public event EventHandler<CultureChangedEventArgs>? CultureChanged;

    /// <inheritdoc />
    public string this[string key] => GetString(key);

    /// <inheritdoc />
    public CultureInfo CurrentCulture => _currentCulture;

    /// <inheritdoc />
    public string LanguagePreference => _languagePreference;

    /// <inheritdoc />
    public IReadOnlyList<CultureInfo> SupportedCultures => SupportedLanguages.All;

    /// <inheritdoc />
    public string GetString(string key) => LocalizationResources.GetString(key, _currentCulture);

    /// <inheritdoc />
    public string Format(string key, params object?[] args) =>
        LocalizationResources.Format(key, _currentCulture, args);

    /// <inheritdoc />
    public string FormatPlural(string baseKey, long count, params object?[] args) =>
        LocalizationResources.FormatPlural(baseKey, count, _currentCulture, args);

    /// <inheritdoc />
    public CultureInfo ApplyLanguage(string? preference)
    {
        var normalizedPreference = NormalizePreferenceCasing(preference ?? SupportedLanguages.FollowSystem);

        // Setting the exact same preference again (interface contract on CultureChanged) raises no
        // events at all — not even PropertyChanged("LanguagePreference"), since nothing actually changed.
        if (string.Equals(normalizedPreference, _languagePreference, StringComparison.Ordinal))
            return _currentCulture;

        var oldCulture = _currentCulture;
        var resolvedCulture = SupportedLanguages.Resolve(normalizedPreference, _systemUiCulture);
        var effectiveCultureChanged = !resolvedCulture.Equals(oldCulture);

        _languagePreference = normalizedPreference;
        _currentCulture = resolvedCulture;

        // Fixed order (docs/PLANS/localization.md §2.2): LanguagePreference always fires first (the
        // preference did change, by the early-return above). CurrentCulture/Item[]/CultureChanged only
        // fire when the *effective* culture also changed — e.g. switching "" -> "ru" on an already-
        // Russian Windows changes the preference but not the resolved culture, so only LanguagePreference
        // fires.
        OnPropertyChanged(nameof(LanguagePreference));

        if (effectiveCultureChanged)
        {
            OnPropertyChanged(nameof(CurrentCulture));
            OnPropertyChanged("Item[]");
            RaiseCultureChanged(new CultureChangedEventArgs(oldCulture, resolvedCulture));
        }

        return _currentCulture;
    }

    /// <summary>
    /// Normalizes a supported language tag (<c>"en"</c>/<c>"ru"</c>, matched case-insensitively per
    /// <see cref="SupportedLanguages.IsSupported"/>) to its canonical lower-case form (fix cycle 1 NIT).
    /// Without this, a hand-edited <c>user.config</c> value like <c>"RU"</c> would resolve to the correct
    /// (case-insensitive) effective culture via <see cref="SupportedLanguages.Resolve"/>, while every
    /// exact-case <see cref="LanguagePreference"/> comparison elsewhere — e.g.
    /// <c>MainWindowViewModel</c>'s <c>LanguageOptions.FirstOrDefault(o =&gt; o.Tag ==
    /// _localization.LanguagePreference)</c> — would fail to match any picker entry and silently fall
    /// back to displaying "Follow Windows" despite the UI actually being pinned to Russian.
    /// <see cref="SupportedLanguages.FollowSystem"/> (<c>""</c>) and any other, unsupported tag pass
    /// through unchanged — this only closes the casing gap for the two tags that are actually resolved
    /// case-insensitively.
    /// </summary>
    private static string NormalizePreferenceCasing(string preference) =>
        SupportedLanguages.IsSupported(preference) ? preference.ToLowerInvariant() : preference;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Invokes every <see cref="CultureChanged"/> subscriber individually, isolating each in its own
    /// try/catch (fix cycle 1 MINOR-1) — a single misbehaving subscriber (e.g. a bug in a UI-layer
    /// callback) must never stop the remaining subscribers from observing the culture switch, and must
    /// never propagate out of <see cref="ApplyLanguage"/> itself.
    /// </summary>
    private void RaiseCultureChanged(CultureChangedEventArgs args)
    {
        var handler = CultureChanged;
        if (handler is null) return;

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<CultureChangedEventArgs>>())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                LogSubscriberException(ex);
            }
        }
    }

    /// <summary>
    /// Records a <see cref="CultureChanged"/> subscriber's exception via <see cref="_diagnosticsLog"/>
    /// when one was supplied (production: the shared <see cref="IDiagnosticsLog"/> singleton, ADR-027),
    /// otherwise via <see cref="Debug.WriteLine(string)"/> — either way, never rethrown.
    /// </summary>
    private void LogSubscriberException(Exception ex)
    {
        if (_diagnosticsLog is { IsEnabled: true } log)
        {
            // Privacy invariant (ADR-027): exception type name only — never ex.Message, which could
            // embed arbitrary subscriber-side state.
            log.Write(
                "localization",
                "CultureChangedSubscriberFailed",
                null,
                new KeyValuePair<string, object?>("exceptionType", ex.GetType().Name));
        }
        else
        {
            Debug.WriteLine($"LocalizationService: a CultureChanged subscriber threw {ex.GetType().Name}.");
        }
    }
}
