using System.ComponentModel;
using System.Globalization;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// One entry in the Settings → General "Language" picker (docs/PLANS/localization.md §2.9 contract C-5,
/// consumed by <c>GeneralSection.xaml</c>'s <c>ComboBox</c> via <c>MainWindowViewModel.LanguageOptions</c>/
/// <c>SelectedLanguageOption</c>). <see cref="Tag"/> is the raw <see cref="ILocalizationService.LanguagePreference"/>
/// value this option represents (<c>""</c> follows Windows, <c>"en"</c>/<c>"ru"</c> pin a language) and never
/// changes; <see cref="DisplayName"/> is what the <c>ComboBox</c> actually shows (bound via
/// <c>DisplayMemberPath</c>) and is re-resolved for the current UI language by <see cref="Refresh"/> —
/// called by <c>MainWindowViewModel</c> for every live option whenever
/// <see cref="ILocalizationService.CultureChanged"/> fires (D-3 hot switch).
/// </summary>
public sealed class LanguageOption : INotifyPropertyChanged
{
    private readonly CultureInfo _systemUiCulture;
    private string _displayName;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageOption"/> class, already resolved for
    /// <paramref name="localization"/>'s current culture.
    /// </summary>
    /// <param name="tag">The raw language preference this option represents: <c>""</c>, <c>"en"</c>, or <c>"ru"</c>.</param>
    /// <param name="localization">Supplies the localized text; also consulted by <see cref="Refresh"/>.</param>
    /// <param name="systemUiCulture">
    /// The Windows UI culture the <c>tag == ""</c> ("follow Windows") entry names in <see cref="Resolve"/>
    /// (fix cycle 1 MINOR-2: passed in explicitly by the caller — production:
    /// <see cref="SystemCulture.OriginalUiCulture"/>, via <c>MainWindowViewModel</c> — rather than this
    /// type reaching for that static itself, so tests can exercise both the "en" and "ru" branches
    /// deterministically regardless of the test runner machine's own culture, docs/PLANS/localization.md R-5).
    /// </param>
    public LanguageOption(string tag, ILocalizationService localization, CultureInfo systemUiCulture)
    {
        Tag = tag;
        _systemUiCulture = systemUiCulture ?? throw new ArgumentNullException(nameof(systemUiCulture));
        _displayName = Resolve(tag, localization);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The raw language preference this option represents: <c>""</c>, <c>"en"</c>, or <c>"ru"</c>.</summary>
    public string Tag { get; }

    /// <summary>The localized text shown in the picker for this option.</summary>
    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (_displayName == value) return;
            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    /// <summary>
    /// Re-resolves <see cref="DisplayName"/> for <paramref name="localization"/>'s current culture —
    /// called by <c>MainWindowViewModel</c> for every live option on
    /// <see cref="ILocalizationService.CultureChanged"/>.
    /// </summary>
    /// <param name="localization">Supplies the localized text.</param>
    internal void Refresh(ILocalizationService localization) => DisplayName = Resolve(Tag, localization);

    /// <summary>
    /// Resolves the display text for <paramref name="tag"/> under <paramref name="localization"/>'s
    /// current culture. The "follow Windows" entry (<c>tag == ""</c>) names the language this instance's
    /// own <c>systemUiCulture</c> (see constructor) resolves to, in that language's own self-name —
    /// <c>Language_Name_En</c>/<c>Language_Name_Ru</c> are identical text in both RESX files
    /// (docs/PLANS/localization.md §3.2), so it doesn't matter which culture the lookup itself runs under.
    /// </summary>
    /// <param name="tag">The raw language preference: <c>""</c>, <c>"en"</c>, or <c>"ru"</c>.</param>
    /// <param name="localization">Supplies the localized text.</param>
    private string Resolve(string tag, ILocalizationService localization)
    {
        if (tag == "en") return localization.GetString("Language_Name_En");
        if (tag == "ru") return localization.GetString("Language_Name_Ru");

        var resolved = SupportedLanguages.Resolve(null, _systemUiCulture);
        var nativeNameKey = resolved.TwoLetterISOLanguageName == "ru" ? "Language_Name_Ru" : "Language_Name_En";
        return localization.Format("Language_FollowWindows_Format", localization.GetString(nativeNameKey));
    }
}
