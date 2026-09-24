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
    [Fact]
    public void InitialState_IsIdle()
    {
        var sut = new PanelInteractionTracker();

        Assert.Equal(PanelInteractionState.Idle, sut.State);
    }

    // T-TRK-02: TryEnter(Resizing) from Idle -> true.
    [Fact]
    public void TryEnter_Resizing_FromIdle_Succeeds()
    {
        var sut = new PanelInteractionTracker();

        var result = sut.TryEnter(PanelInteractionState.Resizing);

        Assert.True(result);
        Assert.Equal(PanelInteractionState.Resizing, sut.State);
    }

    // T-TRK-03: TryEnter(Dragging) while Resizing -> false, state unchanged.
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
    [Fact]
    public void Exit_WrongState_IsNoOp()
    {
        var sut = new PanelInteractionTracker();
        Assert.True(sut.TryEnter(PanelInteractionState.Resizing));

        sut.Exit(PanelInteractionState.Dragging);

        Assert.Equal(PanelInteractionState.Resizing, sut.State);
    }

    // T-TRK-05: double Exit is idempotent.
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
    // Phase 1 production code path (PanelDragMoveBehavior/PanelResizeController/ResizeGestureSession)
    // ever calls TryEnter with either value; they are reserved for Phase 6 (ADR-021 Consequences/Negative).
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
    [Fact]
    public void TryEnter_Idle_AlwaysFails()
    {
        var sut = new PanelInteractionTracker();

        var result = sut.TryEnter(PanelInteractionState.Idle);

        Assert.False(result);
        Assert.Equal(PanelInteractionState.Idle, sut.State);
    }

    // Hover, like Idle, allows entering any other gesture state (matrix row "Hover").
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
