namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Hides or shows the real desktop icons (the shell <c>SHELLDLL_DefView</c> layer).
/// </summary>
public interface IDesktopIconsService
{
    /// <summary>
    /// Raised after this service changed the desktop icons' visibility through
    /// <see cref="SetIconsVisible"/>, <see cref="Toggle"/> or a successful
    /// <see cref="ReapplyDesiredVisibility"/>.
    /// </summary>
    /// <remarks>
    /// Lets every UI mirror of the state (the tray's "Show desktop icons" checkmark, the settings
    /// window's toggle) stay in sync no matter which entry point changed it — tray, settings, the
    /// desktop double-click gesture or Explorer-restart recovery. Raised on the caller's thread.
    /// Not raised for changes made outside this application (Explorer's own "Show desktop icons").
    /// </remarks>
    event EventHandler? VisibilityChanged;

    /// <summary>
    /// Whether the desktop icons are currently visible.
    /// </summary>
    /// <remarks>
    /// Reports <see langword="false"/> both when the icons are hidden and when <c>SHELLDLL_DefView</c>
    /// cannot be found at all. Code that wants to <i>act</i> on a hidden desktop (rather than just display
    /// a checkmark) must use <see cref="QueryIconsVisibility"/>, which keeps those cases apart.
    /// </remarks>
    bool AreIconsVisible { get; }

    /// <summary>
    /// Observes the real desktop icon state without folding "could not look" into "hidden".
    /// </summary>
    /// <returns>
    /// <see cref="DesktopIconsVisibility.Unknown"/> when <c>SHELLDLL_DefView</c> does not exist (yet);
    /// <see cref="DesktopIconsVisibility.HiddenByExplorerSetting"/> when Explorer's own "Show desktop
    /// icons" is off (or the <c>NoDesktop</c> policy is on); otherwise
    /// <see cref="DesktopIconsVisibility.Visible"/>/<see cref="DesktopIconsVisibility.Hidden"/> by the
    /// window's visibility. Never throws (a failing lookup answers <see cref="DesktopIconsVisibility.Unknown"/>).
    /// Read-only: never changes any window and never records an intent.
    /// </returns>
    DesktopIconsVisibility QueryIconsVisibility();

    /// <summary>
    /// Shows or hides the desktop icons.
    /// </summary>
    /// <param name="visible"><see langword="true"/> to show, <see langword="false"/> to hide.</param>
    void SetIconsVisible(bool visible);

    /// <summary>
    /// Toggles desktop icon visibility.
    /// </summary>
    void Toggle();

    /// <summary>
    /// Gets the visibility this application last asked for through <see cref="SetIconsVisible"/> or
    /// <see cref="Toggle"/> during the current session, or <see langword="null"/> if it never asked.
    /// </summary>
    /// <remarks>
    /// Session-only, in memory — never persisted (the persistence contour is frozen, and a hidden
    /// desktop must never survive a crash into the next session: see <c>StartupIconsSelfHeal</c>). It
    /// exists because the actual state lives in the shell's <c>SHELLDLL_DefView</c> window, which is
    /// thrown away when Explorer restarts: the new one starts visible, so an intent of "hidden" has to
    /// be re-applied by <see cref="ReapplyDesiredVisibility"/> (ADR-034).
    /// </remarks>
    bool? DesiredVisible { get; }

    /// <summary>
    /// Re-applies a session "hidden" intent (<see cref="DesiredVisible"/> = <see langword="false"/>) to
    /// the shell's current <c>SHELLDLL_DefView</c> — used after an Explorer restart (ADR-034).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when there is nothing left to do: no intent was recorded, the intent is
    /// "visible" (a freshly created <c>SHELLDLL_DefView</c> already is, and forcing it would override
    /// the user's own Explorer "Show desktop icons" choice), or the icons were hidden successfully.
    /// <see langword="false"/> when the intent is "hidden" but the new <c>SHELLDLL_DefView</c> does not
    /// exist yet — the caller may retry. Never throws.
    /// </returns>
    bool ReapplyDesiredVisibility();
}
