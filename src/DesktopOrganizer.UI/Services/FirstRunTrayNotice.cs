using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.UI.State;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Store-publication B2: on the very first run, tells the user through a one-off tray balloon that the app
/// lives in the notification area and how to reach Settings/Exit — a tray-only app has no window to say it.
/// A DI singleton; shown at most once per session. Works in both distribution channels.
/// </summary>
/// <remarks>
/// "First run" is the existing <see cref="StateLoadStatus.FirstRun"/> load status (no state document existed,
/// no legacy data) — the same signal <c>WindowManager</c> uses to seed the first panel. The fact that the
/// balloon was shown is deliberately NOT stored anywhere (no setting, no state field): the next start loads
/// an existing document and reports <see cref="StateLoadStatus.Loaded"/>, so the balloon does not come back.
/// A session that cannot save (read-only) never shows it.
/// </remarks>
public sealed class FirstRunTrayNotice
{
    private readonly IApplicationStateStore _stateStore;
    private readonly IApplicationStateService _applicationState;
    private readonly ITrayIconManager _trayIcon;
    private readonly ILocalizationService _localization;
    private bool _shown;

    /// <summary>
    /// Initializes a new instance of the <see cref="FirstRunTrayNotice"/> class.
    /// </summary>
    /// <param name="stateStore">Source of "is this session writable" — a first run that cannot even save is not announced.</param>
    /// <param name="applicationState">Source of the load status.</param>
    /// <param name="trayIcon">Shows the balloon.</param>
    /// <param name="localization">Resolves the localized title and message.</param>
    public FirstRunTrayNotice(
        IApplicationStateStore stateStore,
        IApplicationStateService applicationState,
        ITrayIconManager trayIcon,
        ILocalizationService localization)
    {
        _stateStore = stateStore;
        _applicationState = applicationState;
        _trayIcon = trayIcon;
        _localization = localization;
    }

    /// <summary>
    /// Shows the balloon if this is the first run (<see cref="StateLoadStatus.FirstRun"/>) of a writable
    /// session and it has not been shown yet.
    /// </summary>
    /// <returns><see langword="true"/> if the balloon was shown by this call; otherwise <see langword="false"/>.</returns>
    public bool ShowIfFirstRun()
    {
        if (_shown || !_stateStore.IsWritable || _applicationState.LastLoadStatus != StateLoadStatus.FirstRun)
            return false;

        _shown = true;
        _trayIcon.ShowInfoBalloon(
            _localization.GetString("FirstRun_TrayBalloon_Title"),
            _localization.GetString("FirstRun_TrayBalloon_Message"));
        return true;
    }
}
