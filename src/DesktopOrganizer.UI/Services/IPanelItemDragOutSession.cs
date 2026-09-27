namespace DesktopOrganizer.UI.Services;

/// <summary>
/// What a panel-item drag-out gesture (ADR-036, release-hardening-1.0.md §E) ended up being, as
/// resolved by <see cref="IPanelItemDragOutSession.End"/>.
/// </summary>
public enum DragOutOutcomeKind
{
    /// <summary>
    /// The drag never left its own panel (a same-panel drop landed outside the file list itself, e.g.
    /// the title bar — a drop ON the file list is handled entirely inside the reorder gesture and never
    /// reaches the session at all). Nothing else to do: the item stays exactly where it was.
    /// </summary>
    Reordered,

    /// <summary>
    /// A DIFFERENT panel accepted the drag (<see cref="IPanelItemDragOutSession.TryClaim"/> returned
    /// <see langword="true"/>) — the source panel must remove its own entry (without restoring/recycling
    /// the underlying file: the managed copy is shared, only the reference moved).
    /// </summary>
    TransferredTo,

    /// <summary>
    /// Nothing claimed the drag — dropped on Explorer/the desktop, cancelled (Esc), or copied
    /// (Ctrl-drag). The source panel cannot trust the OLE drop effect and must verify whether the file
    /// actually left its original location (see <see cref="Behaviors.MovedOutVerifier"/>) before deciding
    /// whether to remove its own entry.
    /// </summary>
    External,
}

/// <summary>
/// The outcome of a finished panel-item drag-out gesture, as returned by
/// <see cref="IPanelItemDragOutSession.End"/>.
/// </summary>
/// <param name="Kind">Which of the three outcomes this was.</param>
/// <param name="TargetPanelId">
/// The panel that claimed the drag, when <paramref name="Kind"/> is
/// <see cref="DragOutOutcomeKind.TransferredTo"/>; <see langword="null"/> otherwise.
/// </param>
public readonly record struct DragOutOutcome(DragOutOutcomeKind Kind, Guid? TargetPanelId);

/// <summary>
/// Process-wide (singleton), UI-thread-only coordination for an in-app panel-item drag-out gesture
/// (ADR-036, release-hardening-1.0.md §E): the source panel <see cref="Begin"/>s tracking the dragged
/// path right before <see cref="System.Windows.DragDrop.DoDragDrop"/> is called; any panel the drag
/// passes over (or is dropped on) may <see cref="TryClaim"/> it; the source panel reads the outcome via
/// <see cref="End"/>, exactly once, right after <see cref="System.Windows.DragDrop.DoDragDrop"/> returns.
/// </summary>
/// <remarks>
/// At most one drag-out gesture is ever in flight at a time — <see cref="System.Windows.DragDrop.DoDragDrop"/>
/// is synchronous/modal on the UI thread, so a second <see cref="Begin"/> can never race a still-open
/// one in practice.
/// </remarks>
public interface IPanelItemDragOutSession
{
    /// <summary>Gets a value indicating whether a drag-out gesture is currently being tracked.</summary>
    bool IsActive { get; }

    /// <summary>Gets the source panel's <c>ChildWindowModel.Id</c> for the active gesture, or <see langword="null"/> when <see cref="IsActive"/> is <see langword="false"/>.</summary>
    Guid? SourcePanelId { get; }

    /// <summary>Gets the dragged item's path for the active gesture, or <see langword="null"/> when <see cref="IsActive"/> is <see langword="false"/>.</summary>
    string? Path { get; }

    /// <summary>
    /// Starts tracking a new drag-out gesture. Must be called right before
    /// <see cref="System.Windows.DragDrop.DoDragDrop"/>, and exactly once per gesture — a stray extra
    /// call before the previous gesture's <see cref="End"/> would silently discard it.
    /// </summary>
    /// <param name="sourcePanelId">The dragging panel's <c>ChildWindowModel.Id</c>.</param>
    /// <param name="path">The dragged item's path.</param>
    void Begin(Guid sourcePanelId, string path);

    /// <summary>
    /// Claims the active drag-out gesture on behalf of <paramref name="targetPanelId"/> — called by a
    /// panel that just accepted a drop whose <see cref="System.Windows.DataFormats.FileDrop"/> payload
    /// matches the active gesture's <paramref name="path"/>.
    /// </summary>
    /// <param name="targetPanelId">The panel claiming the drag.</param>
    /// <param name="path">The dropped path — must match the active gesture's <see cref="Path"/> (case-insensitive) or the claim is rejected.</param>
    /// <returns>
    /// <see langword="true"/> if this is a genuine cross-panel transfer the caller should now apply
    /// (add its own reference to <paramref name="path"/>). <see langword="false"/> for every other case:
    /// no active gesture, a path mismatch, a gesture already claimed by someone else, or
    /// <paramref name="targetPanelId"/> equal to the gesture's own <see cref="SourcePanelId"/> (a
    /// same-panel drop that lands outside the reorder gesture's own handling — still recorded, so
    /// <see cref="End"/> resolves to <see cref="DragOutOutcomeKind.Reordered"/> rather than
    /// <see cref="DragOutOutcomeKind.External"/>, but never treated as an accepted transfer).
    /// </returns>
    bool TryClaim(Guid targetPanelId, string path);

    /// <summary>
    /// Resolves and clears the active gesture's outcome. Must be called exactly once per
    /// <see cref="Begin"/>, right after <see cref="System.Windows.DragDrop.DoDragDrop"/> returns —
    /// regardless of the effect it returned, or whether it threw.
    /// </summary>
    /// <returns>The gesture's outcome (see <see cref="DragOutOutcomeKind"/>).</returns>
    DragOutOutcome End();
}
