namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// What <see cref="IDesktopIconsService.QueryIconsVisibility"/> actually observed about the real desktop
/// icons — as opposed to <see cref="IDesktopIconsService.AreIconsVisible"/>, which folds "could not look"
/// into "hidden".
/// </summary>
public enum DesktopIconsVisibility
{
    /// <summary>
    /// The shell's <c>SHELLDLL_DefView</c> window does not exist (yet) — typically autostart at logon
    /// before Explorer has built the desktop, or mid Explorer restart. Nothing is known; nothing may be
    /// changed on this basis.
    /// </summary>
    Unknown,

    /// <summary><c>SHELLDLL_DefView</c> exists and is visible.</summary>
    Visible,

    /// <summary>
    /// <c>SHELLDLL_DefView</c> exists and is hidden — the way this application hides icons (Explorer's own
    /// "Show desktop icons" setting does not hide that window, see <see cref="HiddenByExplorerSetting"/>).
    /// </summary>
    Hidden,

    /// <summary>
    /// The user (or policy) turned desktop icons off in Explorer itself ("View &gt; Show desktop icons"
    /// unchecked, or the <c>NoDesktop</c> policy). That choice belongs to the user and must never be
    /// overridden by this application's own heuristics.
    /// </summary>
    HiddenByExplorerSetting,
}
