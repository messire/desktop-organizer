namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// A tiny, pure, one-bit state machine extracted (M3, release-hardening 1.0 review) so the "suppress a
/// side effect for the span of a transition" and "run a follow-up correction exactly once" guard logic
/// used by <see cref="Views.ChildWindow"/>'s <c>OnDpiChanged</c>/<c>OnLocationChanged</c> pair and
/// <see cref="Views.PanelCustomizeWindow"/>'s <c>PositionForPanel</c>/<c>OnDpiChanged</c> pair is directly
/// unit-testable without either heavy, DI-constructed <see cref="System.Windows.Window"/> subclass.
/// </summary>
/// <remarks>
/// Two usage shapes, both covered by the same three members:
/// <list type="bullet">
/// <item><description>
/// <b>Span guard</b> (<c>ChildWindow</c>'s DPI-transition guard): <see cref="Set"/> at the start of the
/// transition, <see cref="IsSet"/> read (WITHOUT consuming) by every side effect that must be suppressed
/// while the transition is in flight, <see cref="Clear"/> once the transition's own deferred follow-up
/// actually runs.
/// </description></item>
/// <item><description>
/// <b>One-shot correction</b> (<c>PanelCustomizeWindow</c>'s "re-centre only once" guard): <see cref="Set"/>
/// arms it every time the caller performs the explicit, intentional action; <see cref="TryConsume"/> is
/// called by the very next follow-up event — it both reports whether the latch was armed AND clears it
/// atomically, so every LATER occurrence of that same follow-up event (until the next explicit
/// <see cref="Set"/>) sees it unarmed.
/// </description></item>
/// </list>
/// Not thread-safe — every consumer of this type today reads/writes it exclusively from the UI thread.
/// </remarks>
public sealed class TransitionLatch
{
    /// <summary>Gets a value indicating whether this latch is currently set.</summary>
    public bool IsSet { get; private set; }

    /// <summary>Sets the latch.</summary>
    public void Set() => IsSet = true;

    /// <summary>Clears the latch unconditionally, regardless of its current value.</summary>
    public void Clear() => IsSet = false;

    /// <summary>
    /// Atomically reads and clears the latch: returns whether it was set, and — regardless of the
    /// result — leaves it cleared afterward.
    /// </summary>
    /// <returns><see langword="true"/> if the latch was set (and has now been cleared); otherwise <see langword="false"/>.</returns>
    public bool TryConsume()
    {
        var wasSet = IsSet;
        IsSet = false;
        return wasSet;
    }
}
