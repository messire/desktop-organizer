using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Unit tests for <see cref="PanelInteractionTracker"/> — ADR-021's transition matrix
/// (docs/PLANS/phase-1-resize-stabilization.md §2.2/§5.2, commit C3). No WPF/STA dependency: the
/// tracker is pure C# state.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelInteractionTrackerTests
{
    // T-TRK-01: starts Idle.

    /// <summary>Verifies that a freshly-constructed <see cref="PanelInteractionTracker"/> starts in <see cref="PanelInteractionState.Idle"/>.</summary>
    [Fact]
    public void InitialState_IsIdle()
    {
        var sut = new PanelInteractionTracker();

        Assert.Equal(PanelInteractionState.Idle, sut.State);
    }

    // T-TRK-02: TryEnter(Resizing) from Idle -> true.

    /// <summary>Verifies that entering <see cref="PanelInteractionState.Resizing"/> from <see cref="PanelInteractionState.Idle"/> succeeds.</summary>
    [Fact]
    public void TryEnter_Resizing_FromIdle_Succeeds()
    {
        var sut = new PanelInteractionTracker();

        var result = sut.TryEnter(PanelInteractionState.Resizing);

        Assert.True(result);
        Assert.Equal(PanelInteractionState.Resizing, sut.State);
    }

    // T-TRK-03: TryEnter(Dragging) while Resizing -> false, state unchanged.

    /// <summary>Verifies that entering <see cref="PanelInteractionState.Dragging"/> while already <see cref="PanelInteractionState.Resizing"/> fails and leaves the state unchanged.</summary>
    [Fact]
    public void TryEnter_Dragging_WhileResizing_FailsAndStateUnchanged()
    {
        var sut = new PanelInteractionTracker();
        Assert.True(sut.TryEnter(PanelInteractionState.Resizing));

        var result = sut.TryEnter(PanelInteractionState.Dragging);

        Assert.False(result);
        Assert.Equal(PanelInteractionState.Resizing, sut.State);
    }

    // T-TRK-04: Exit of a state the tracker doesn't currently hold is a no-op.

    /// <summary>Verifies that <see cref="PanelInteractionTracker.Exit"/> of a state the tracker does not currently hold is a no-op.</summary>
    [Fact]
    public void Exit_WrongState_IsNoOp()
    {
        var sut = new PanelInteractionTracker();
        Assert.True(sut.TryEnter(PanelInteractionState.Resizing));

        sut.Exit(PanelInteractionState.Dragging);

        Assert.Equal(PanelInteractionState.Resizing, sut.State);
    }

    // T-TRK-05: double Exit is idempotent.

    /// <summary>Verifies that calling <see cref="PanelInteractionTracker.Exit"/> twice for the same state is idempotent, leaving the tracker in <see cref="PanelInteractionState.Idle"/>.</summary>
    [Fact]
    public void Exit_CalledTwice_IsIdempotent()
    {
        var sut = new PanelInteractionTracker();
        Assert.True(sut.TryEnter(PanelInteractionState.Dragging));

        sut.Exit(PanelInteractionState.Dragging);
        sut.Exit(PanelInteractionState.Dragging);

        Assert.Equal(PanelInteractionState.Idle, sut.State);
    }

    // T-TRK-06: ForceIdle from any state -> Idle, and raises StateChanged.

    /// <summary>Verifies that <see cref="PanelInteractionTracker.ForceIdle"/> returns to <see cref="PanelInteractionState.Idle"/> from any <paramref name="from"/> state and raises <see cref="PanelInteractionTracker.StateChanged"/>.</summary>
    /// <param name="from">The state the tracker starts in.</param>
    [Theory]
    [InlineData(PanelInteractionState.Dragging)]
    [InlineData(PanelInteractionState.Resizing)]
    [InlineData(PanelInteractionState.Hover)]
    [InlineData(PanelInteractionState.Editing)]
    public void ForceIdle_FromAnyState_ReturnsToIdleAndRaisesEvent(PanelInteractionState from)
    {
        var sut = new PanelInteractionTracker();
        Assert.True(sut.TryEnter(from));

        PanelInteractionState? raised = null;
        sut.StateChanged += (_, s) => raised = s;

        sut.ForceIdle();

        Assert.Equal(PanelInteractionState.Idle, sut.State);
        Assert.Equal(PanelInteractionState.Idle, raised);
    }

    // T-TRK-07: StateChanged fires exactly once per real transition (not on no-ops).

    /// <summary>Verifies that <see cref="PanelInteractionTracker.StateChanged"/> fires exactly once per real transition, and not for no-op calls.</summary>
    [Fact]
    public void StateChanged_RaisedExactlyOncePerTransition()
    {
        var sut = new PanelInteractionTracker();
        var raisedCount = 0;
        sut.StateChanged += (_, _) => raisedCount++;

        Assert.True(sut.TryEnter(PanelInteractionState.Dragging)); // 1 real transition
        sut.Exit(PanelInteractionState.Resizing); // no-op: wrong state
        sut.Exit(PanelInteractionState.Dragging); // 1 real transition
        sut.ForceIdle(); // no-op: already Idle

        Assert.Equal(2, raisedCount);
    }

    // T-TRK-08: Hover/Editing are not dead states — TryEnter succeeds for both from Idle — but no
    // Phase 1 production code path (PanelDragMoveBehavior/NativePanelResizeController/ResizeGestureSession)
    // ever calls TryEnter with either value; they are reserved for Phase 6 (ADR-021 Consequences/Negative).

    /// <summary>Verifies that entering <see cref="PanelInteractionState.Hover"/> or <see cref="PanelInteractionState.Editing"/> from <see cref="PanelInteractionState.Idle"/> succeeds — neither is a dead state.</summary>
    /// <param name="state">The state to enter.</param>
    [Theory]
    [InlineData(PanelInteractionState.Hover)]
    [InlineData(PanelInteractionState.Editing)]
    public void TryEnter_HoverOrEditing_FromIdle_Succeeds_NotDeadStates(PanelInteractionState state)
    {
        var sut = new PanelInteractionTracker();

        var result = sut.TryEnter(state);

        Assert.True(result);
        Assert.Equal(state, sut.State);
    }

    // TryEnter(Idle) is never a valid target — returning to Idle always goes through Exit/ForceIdle.

    /// <summary>Verifies that <see cref="PanelInteractionState.Idle"/> is never a valid <see cref="PanelInteractionTracker.TryEnter"/> target.</summary>
    [Fact]
    public void TryEnter_Idle_AlwaysFails()
    {
        var sut = new PanelInteractionTracker();

        var result = sut.TryEnter(PanelInteractionState.Idle);

        Assert.False(result);
        Assert.Equal(PanelInteractionState.Idle, sut.State);
    }

    // Hover, like Idle, allows entering any other gesture state (matrix row "Hover").

    /// <summary>Verifies that entering <see cref="PanelInteractionState.Resizing"/> from <see cref="PanelInteractionState.Hover"/> succeeds.</summary>
    [Fact]
    public void TryEnter_Resizing_FromHover_Succeeds()
    {
        var sut = new PanelInteractionTracker();
        Assert.True(sut.TryEnter(PanelInteractionState.Hover));

        var result = sut.TryEnter(PanelInteractionState.Resizing);

        Assert.True(result);
        Assert.Equal(PanelInteractionState.Resizing, sut.State);
    }

    // Editing/Dragging/Resizing refuse every TryEnter target except via Exit/ForceIdle (matrix: only
    // "back to Idle" is possible from these three rows).

    /// <summary>Verifies that from an exclusive gesture state (Editing/Dragging/Resizing), every <see cref="PanelInteractionTracker.TryEnter"/> target fails, leaving the state unchanged.</summary>
    /// <param name="from">The exclusive gesture state the tracker starts in.</param>
    /// <param name="to">The target state that must be rejected.</param>
    [Theory]
    [InlineData(PanelInteractionState.Editing, PanelInteractionState.Dragging)]
    [InlineData(PanelInteractionState.Dragging, PanelInteractionState.Resizing)]
    [InlineData(PanelInteractionState.Resizing, PanelInteractionState.Editing)]
    public void TryEnter_FromExclusiveGestureState_AlwaysFails(PanelInteractionState from, PanelInteractionState to)
    {
        var sut = new PanelInteractionTracker();
        Assert.True(sut.TryEnter(from));

        var result = sut.TryEnter(to);

        Assert.False(result);
        Assert.Equal(from, sut.State);
    }
}
