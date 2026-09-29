using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// The shared flow behind every "Copy to page" action (ADR-045) — the Settings window's Panels section
/// (<c>MainWindowViewModel.CopyPanelToPageCommand</c>) and a panel's own header context menu
/// (<c>ChildWindowViewModel.CopyPanelToPageCommand</c>) both call it instead of
/// <see cref="IWindowManager.ClonePanelToPage"/> directly.
/// </summary>
/// <remarks>
/// H-1 fix (code review, panel clone to page, backlog 1.x wave 3 review): before this fix, only
/// <c>MainWindowViewModel.CopyPanelToPage</c> showed a busy cursor and surfaced a failure outcome —
/// <c>ChildWindowViewModel.CopyPanelToPage</c>'s own call site discarded <see cref="IWindowManager.ClonePanelToPage"/>'s
/// result outright, so <c>TooLarge</c>/<c>InsufficientSpace</c>/<c>CopyFailed</c> failed completely silently
/// there. This type is the single place that busy-cursor bracket, the localized failure notification, and
/// the "stale destination page" silent re-sync live, so both call sites get the exact same behavior.
/// </remarks>
public interface IPanelCloneFlow
{
    /// <summary>
    /// Clones <paramref name="source"/> onto <paramref name="pageId"/> via
    /// <see cref="IWindowManager.ClonePanelToPage"/>, showing a busy indication
    /// (<see cref="IBusyIndicator"/>) for the (synchronous, potentially slow) duration of the call.
    /// </summary>
    /// <remarks>
    /// <see cref="PanelCloneOutcome.Cloned"/> is returned as-is for the caller to act on (select the clone,
    /// refresh its own panel list) — this method never touches selection or panel-list state itself, since
    /// that differs between the two call sites. Every OTHER outcome is fully handled here: a
    /// <see cref="PanelCloneOutcome.UnknownPage"/> (a stale/typo'd destination — e.g. the page was removed
    /// from under a still-open "Copy to page" menu) invokes <paramref name="onUnknownPage"/> instead of
    /// showing anything to the user, mirroring <c>MainWindowViewModel.OnPagesChanged</c>'s own silent
    /// re-sync; every remaining failure (<see cref="PanelCloneOutcome.TooLarge"/>/
    /// <see cref="PanelCloneOutcome.InsufficientSpace"/>/<see cref="PanelCloneOutcome.CopyFailed"/>) shows a
    /// localized <see cref="IConfirmationService.Notify"/>. <see cref="PanelCloneResult.SkippedMissingItems"/>
    /// is deliberately never surfaced (ADR-045 §8.4) — only logged by
    /// <see cref="IWindowManager.ClonePanelToPage"/> itself.
    /// </remarks>
    /// <param name="source">The panel to clone. Never mutated.</param>
    /// <param name="pageId">The destination page id.</param>
    /// <param name="onUnknownPage">
    /// Invoked (instead of a failure notification) when the destination page no longer exists.
    /// </param>
    /// <returns>The raw result, so a successful clone's caller can still select it / refresh its own panel list.</returns>
    PanelCloneResult CopyToPage(ChildWindowModel source, string pageId, Action onUnknownPage);
}
