namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Default <see cref="IPanelSelectionCoordinator"/> implementation. Registered as a DI singleton (see
/// <c>DiConfig.ConfigureServices</c>) — there is exactly one coordinator for the whole process, shared
/// by every <c>ChildWindowViewModel</c> instance (transient, one per panel) and, from ui-polishing-1
/// Block D, the system icons side panel's ViewModel.
/// </summary>
public sealed class PanelSelectionCoordinator : IPanelSelectionCoordinator
{
    private object? _currentOwner;

    /// <inheritdoc/>
    public event EventHandler<object>? SelectionClaimed;

    /// <inheritdoc/>
    public void NotifySelected(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Idempotent no-op when the same owner re-claims a selection it already holds. This is the
        // coordinator-side half of the reentrancy guard (ui-polishing-1 §2.4 Block A2, risk R3):
        // even if some subscriber's SelectionClaimed handler ever re-entered NotifySelected for the
        // owner that's already current, this stops the chain here instead of re-raising the event.
        if (ReferenceEquals(_currentOwner, owner)) return;

        _currentOwner = owner;
        SelectionClaimed?.Invoke(this, owner);
    }

    /// <inheritdoc/>
    public void ClearAll()
    {
        _currentOwner = null;

        // A fresh sentinel object can never reference-equal any real subscriber, so every subscriber's
        // own "owner != me" comparison clears its selection.
        SelectionClaimed?.Invoke(this, new object());
    }

    /// <inheritdoc/>
    public void Release(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Idempotent no-op for a panel that never held the selection (or already released it) — a
        // disposing panel calls this unconditionally, it doesn't first check whether it was the owner.
        if (!ReferenceEquals(_currentOwner, owner)) return;

        _currentOwner = null;

        // Deliberately does NOT raise SelectionClaimed: no other subscriber's selection was ever tied
        // to this owner's claim, and the disposing owner itself has nothing left to react to.
    }
}
