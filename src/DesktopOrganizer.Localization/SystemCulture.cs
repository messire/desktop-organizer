using System.Globalization;

namespace DesktopOrganizer.Localization;

/// <summary>
/// Captures the process's original Windows UI culture before anything overrides
/// <see cref="CultureInfo.CurrentUICulture"/>/<see cref="CultureInfo.DefaultThreadCurrentUICulture"/>
/// (docs/PLANS/localization.md finding 5, §2.1). <see cref="OriginalUiCulture"/> is what "follow Windows"
/// (D-4, <see cref="SupportedLanguages.FollowSystem"/>) resolves against — it must be read as the very
/// first line of <c>Program.Main</c>, before <c>SupportedLanguages.Resolve</c> ever assigns
/// <see cref="CultureInfo.CurrentUICulture"/> to one of the two supported languages. Deliberately not
/// <see cref="CultureInfo.InstalledUICulture"/>: that is the OS installation language, not the signed-in
/// user's MUI display language.
/// </summary>
public static class SystemCulture
{
    /// <summary>
    /// The value of <see cref="CultureInfo.CurrentUICulture"/> captured the first time this type is
    /// touched. A <see langword="static readonly"/> field initializer runs exactly once, on first access
    /// (CLR type-initialization semantics), which is what makes this a reliable "before any override"
    /// snapshot as long as the very first access happens before the app assigns a new UI culture.
    /// </summary>
    public static CultureInfo OriginalUiCulture { get; } = CultureInfo.CurrentUICulture;
}
