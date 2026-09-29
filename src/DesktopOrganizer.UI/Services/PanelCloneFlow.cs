using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <inheritdoc cref="IPanelCloneFlow" />
public sealed class PanelCloneFlow : IPanelCloneFlow
{
    private readonly IWindowManager _windowManager;
    private readonly IConfirmationService _confirmation;
    private readonly ILocalizationService _localization;
    private readonly IBusyIndicator _busyIndicator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelCloneFlow"/> class.
    /// </summary>
    /// <param name="windowManager">Performs the actual clone.</param>
    /// <param name="confirmation">Shows the localized failure notification.</param>
    /// <param name="localization">Resolves the failure message/title RESX keys.</param>
    /// <param name="busyIndicator">Shows a busy indication for the duration of the clone call.</param>
    public PanelCloneFlow(
        IWindowManager windowManager,
        IConfirmationService confirmation,
        ILocalizationService localization,
        IBusyIndicator busyIndicator)
    {
        _windowManager = windowManager;
        _confirmation = confirmation;
        _localization = localization;
        _busyIndicator = busyIndicator;
    }

    /// <inheritdoc />
    public PanelCloneResult CopyToPage(ChildWindowModel source, string pageId, Action onUnknownPage)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onUnknownPage);

        PanelCloneResult result;
        using (_busyIndicator.Show())
        {
            result = _windowManager.ClonePanelToPage(source, pageId);
        }

        if (result.Outcome == PanelCloneOutcome.Cloned) return result;

        // A stale/typo'd destination page id is not a failure worth alarming the user about with the
        // generic "copy failed" message — it just means the caller's own page menu was already out of
        // date by the time this ran (e.g. the page was removed from under a still-open "Copy to page"
        // menu). Silently re-sync instead, mirroring MainWindowViewModel.OnPagesChanged.
        if (result.Outcome == PanelCloneOutcome.UnknownPage)
        {
            onUnknownPage();
            return result;
        }

        // SkippedMissingItems (ADR-045 §8.4) is deliberately never surfaced here — it is expected,
        // recoverable behavior (a managed item's file already vanished from disk), not a failure the user
        // needs to act on; only logged by IWindowManager.ClonePanelToPage's own implementation.
        var messageKey = result.Outcome switch
        {
            PanelCloneOutcome.TooLarge => "Panel_Copy_TooLarge_Message",
            PanelCloneOutcome.InsufficientSpace => "Panel_Copy_NoSpace_Message",
            _ => "Panel_Copy_Failed_Message",
        };

        _confirmation.Notify(
            _localization.GetString(messageKey),
            _localization.GetString("Panel_Copy_Failed_Title"),
            MessageBoxImage.Warning);

        return result;
    }
}
