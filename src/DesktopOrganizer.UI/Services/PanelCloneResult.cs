using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// What <see cref="IWindowManager.ClonePanelToPage"/> did (ADR-045, «Panel clone to page»).
/// </summary>
public enum PanelCloneOutcome
{
    /// <summary>
    /// The clone was created: a brand-new, fully independent <see cref="ChildWindowModel"/> with its own
    /// managed item copies, registered and persisted exactly like any other panel.
    /// </summary>
    Cloned,

    /// <summary>
    /// The destination page id is blank or isn't one of <c>IPageManager.Pages</c>'s existing pages
    /// (mirrors <see cref="IWindowManager.MovePanelToPage"/>'s own guard). Nothing was created or copied.
    /// </summary>
    UnknownPage,

    /// <summary>
    /// One of the source panel's managed items is too large to duplicate (the same safety cap
    /// <c>IPanelItemStorageService.MoveInDirectory</c> applies to a cross-volume import). Every managed
    /// copy already made for this attempt was discarded; the source is untouched and no clone was created.
    /// </summary>
    TooLarge,

    /// <summary>
    /// There wasn't enough free disk space to duplicate one of the source panel's managed items. Every
    /// managed copy already made for this attempt was discarded; the source is untouched and no clone was
    /// created.
    /// </summary>
    InsufficientSpace,

    /// <summary>
    /// Copying one of the source panel's managed items failed for any other reason. Every managed copy
    /// already made for this attempt was discarded; the source is untouched and no clone was created.
    /// </summary>
    CopyFailed,
}

/// <summary>
/// Result of <see cref="IWindowManager.ClonePanelToPage"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ClonedPanel">The newly created panel, set only when <paramref name="Outcome"/> is <see cref="PanelCloneOutcome.Cloned"/>; otherwise <see langword="null"/>.</param>
/// <param name="SkippedMissingItems">
/// How many of the source panel's managed items were left out of the clone because their file was already
/// gone from disk — never a failure by itself (see <see cref="PanelCloneOutcome.Cloned"/>), just fewer
/// items than the source had. Always <c>0</c> for any outcome other than <see cref="PanelCloneOutcome.Cloned"/>.
/// </param>
public readonly record struct PanelCloneResult(PanelCloneOutcome Outcome, ChildWindowModel? ClonedPanel, int SkippedMissingItems);
