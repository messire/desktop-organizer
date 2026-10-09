using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.State;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// ADR-audit WP-B (B1): tells the user, through the non-blocking <see cref="IStatusNotifier"/>/InfoBar
/// (ADR-047 D-9), that this session is read-only — the state store reports
/// <see cref="IApplicationStateStore.IsWritable"/> <see langword="false"/> (newer on-disk schema, or the
/// advisory storage lock was not acquired), so panel changes are not saved. A DI singleton; the message is
/// shown at most once per session. Reads the existing store flag and the load status only — nothing is persisted.
/// </summary>
/// <remarks>
/// Decisions review cycle 1 (U4/U5): the text names the actual reason (<see cref="StateLoadStatus.NewerSchema"/>
/// vs. a held storage lock) and, when the session starts read-only with no panels on screen, the settings
/// window is opened once so the InfoBar is actually seen (an empty desktop plus a silent notice is the
/// "all my panels are gone" scenario).
/// </remarks>
public sealed class ReadOnlySessionNotice
{
    private readonly IApplicationStateStore _stateStore;
    private readonly IStatusNotifier _statusNotifier;
    private readonly ILocalizationService _localization;
    private readonly IApplicationStateService _applicationState;
    private readonly IWindowManager _windowManager;
    private bool _shown;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlySessionNotice"/> class.
    /// </summary>
    /// <param name="stateStore">The single source of truth for "is this session read-only".</param>
    /// <param name="statusNotifier">The non-blocking status surface the notice is shown on.</param>
    /// <param name="localization">Resolves the localized title and message.</param>
    /// <param name="applicationState">Source of the load status (the reason) and the loaded panel count.</param>
    /// <param name="windowManager">Opens the settings window when a read-only session has no panels to show.</param>
    public ReadOnlySessionNotice(
        IApplicationStateStore stateStore,
        IStatusNotifier statusNotifier,
        ILocalizationService localization,
        IApplicationStateService applicationState,
        IWindowManager windowManager)
    {
        _stateStore = stateStore;
        _statusNotifier = statusNotifier;
        _localization = localization;
        _applicationState = applicationState;
        _windowManager = windowManager;
    }

    /// <summary>
    /// Shows the read-only notice (a <see cref="StatusSeverity.Caution"/> status, which stays until
    /// dismissed) if the session is read-only and it has not been shown yet. When that session has no
    /// panels at all, also opens the settings window once so the notice is visible.
    /// </summary>
    /// <returns><see langword="true"/> if the notice was shown by this call; otherwise <see langword="false"/>.</returns>
    public bool ShowIfReadOnly()
    {
        if (_shown || _stateStore.IsWritable) return false;

        _shown = true;

        // Reason-specific sentences (what happened / what it means / what is safe / what to do), each its own
        // RESX key so no single value becomes a "wall of text" (ResxTextLengthGuardTests). The shared
        // "files stay where they are" sentence is the same for both reasons.
        // Literal keys (not built from the reason) so KeyUsageCoverageTests can see every key is used.
        var newerSchema = _applicationState.LastLoadStatus == StateLoadStatus.NewerSchema;
        var message = string.Join(
            ' ',
            _localization.GetString(newerSchema ? "ReadOnlySession_Notice_NewerSchema_Message" : "ReadOnlySession_Notice_Locked_Message"),
            _localization.GetString(newerSchema ? "ReadOnlySession_Notice_NewerSchema_Detail" : "ReadOnlySession_Notice_Locked_Detail"),
            _localization.GetString("ReadOnlySession_Notice_Hint"),
            _localization.GetString(newerSchema ? "ReadOnlySession_Notice_NewerSchema_Action" : "ReadOnlySession_Notice_Locked_Action"));
        _statusNotifier.Show(
            StatusSeverity.Caution,
            message,
            _localization.GetString("ReadOnlySession_Notice_Title"));

        // Read-only and nothing on the desktop: the InfoBar waiting in a closed settings window would never
        // be seen. Once per session by construction (_shown); with panels on screen the notice just waits.
        if (_applicationState.Panels.Count == 0) _windowManager.ShowMainWindow();

        return true;
    }
}
