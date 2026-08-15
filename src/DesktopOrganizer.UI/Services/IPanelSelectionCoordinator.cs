namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Coordinates a single, global "currently selected item" across every desktop-layer panel window
/// that owns its own local selection state — fence panels (<see cref="ViewModels.ChildWindowViewModel"/>)
/// and, from ui-polishing-1 Block D, the system icons side panel. Panels carry <c>WS_EX_NOACTIVATE</c>
/// and are embedded in the desktop's <c>WorkerW</c> layer (ADR-013), so they never receive normal
/// window activation/focus — a focus-based "only one selection active at a time" signal
/// (<c>Window.Deactivated</c>, <c>IsKeyboardFocusWithin</c>, <c>ListBoxItem.IsSelectionActive</c>) is
/// unavailable by construction. This coordinator is the explicit, event-driven substitute: whichever
/// panel last claimed the selection tells every other subscribed panel to clear its own
/// (ui-polishing-1 §2.4 Block A2).
/// </summary>
public interface IPanelSelectionCoordinator
{
    /// <summary>
    /// Declares <paramref name="owner"/> as the current holder of the (single, global) selection and
    /// raises <see cref="SelectionClaimed"/> so every other subscriber clears its own selection. A
    /// no-op — <see cref="SelectionClaimed"/> is <b>not</b> raised — when <paramref name="owner"/>
    /// already holds the selection; this is the coordinator-side half of the reentrancy guard against
    /// runaway recursion (ui-polishing-1 §2.4 Block A2, risk R3).
    /// </summary>
    /// <param name="owner">
    /// The panel ViewModel instance that just set a non-null selection. Compared by reference, never
    /// by value — two different panels must never be treated as the same owner even if they happen to
    /// currently point at value-equal state.
    /// </param>
    void NotifySelected(object owner);

    /// <summary>
    /// Clears the current selection everywhere by raising <see cref="SelectionClaimed"/> with an owner
    /// argument that can never reference-equal any real subscriber, so every subscriber's own
    /// "owner != me" check clears its selection.
    /// </summary>
    void ClearAll();

    /// <summary>
    /// Releases <paramref name="owner"/>'s claim on the current selection, but only if it actually still
    /// holds it (<c>ReferenceEquals(_currentOwner, owner)</c>) — idempotent and safe to call
    /// unconditionally from a panel ViewModel's <c>Dispose()</c>, whether or not that panel ever became
    /// the current owner. Unlike <see cref="ClearAll"/>, this does <b>not</b> raise
    /// <see cref="SelectionClaimed"/>: a disposed panel's own selection state is already gone (nothing
    /// left to echo back to it), and every other panel's selection was never affected by this panel's
    /// claim to begin with, so there is nothing for them to react to. Without this, the coordinator (a
    /// process-wide singleton, see the type-level remarks) would keep a stale, otherwise-unreferenced
    /// panel ViewModel reachable as <c>_currentOwner</c> indefinitely after the panel closes — a
    /// resource leak (fence-ux-parity/code-quality review, ui-polishing-1 finalization MAJOR M-1).
    /// </summary>
    /// <param name="owner">The panel ViewModel instance that is being disposed.</param>
    void Release(object owner);

    /// <summary>
    /// Raised whenever a different owner claims the selection (via <see cref="NotifySelected"/>) or the
    /// selection is cleared everywhere (via <see cref="ClearAll"/>). The event argument is the new
    /// owner (an opaque sentinel for <see cref="ClearAll"/>). Subscribers must compare it by reference
    /// against themselves and clear their own selection only when it differs — the coordinator
    /// deliberately does not filter this on their behalf, since it has no other way to know which
    /// subscriber instance is "self" for each one.
    /// </summary>
    event EventHandler<object>? SelectionClaimed;
}
