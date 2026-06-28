using DesktopOrganizer.Services.Tools;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="DesktopGestureStateMachine"/> — the pure gesture logic extracted out of
/// the WH_MOUSE_LL callback of <c>DesktopGestureService</c> (Phase 7, block B; ADR-003).
/// </summary>
/// <remarks>
/// The hook callback itself cannot be unit-tested: it needs a real global hook and a message loop.
/// What is covered here is everything the callback delegates to — the drag/double-click recognition
/// and, most importantly, the invariant that the machine resets its right-button state *before* a
/// decision is handed back for raising events, so a throwing subscriber can never latch the gesture.
/// The remaining behaviour (hook installed/removed, suppression of the desktop context menu, input
/// responsiveness) stays manual verify (Win10/Win11).
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DesktopGestureStateMachineTests
{
    private const int DoubleClickWindowMs = 500;

    /// <summary>
    /// A right-button drag longer than the threshold completes a lasso, suppresses the message and
    /// reports the dragged rectangle.
    /// </summary>
    [Fact]
    public void RightDrag_BeyondThreshold_CompletesLassoAndSwallows()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmRButtonDown, 100, 100);
        machine.Process(DesktopGestureStateMachine.WmMouseMove, 100 + DesktopGestureStateMachine.DragThreshold + 1, 100);
        var decision = machine.Process(DesktopGestureStateMachine.WmRButtonUp, 300, 250);

        // Assert
        Assert.True(decision.Swallow);
        Assert.NotNull(decision.Lasso);
        Assert.Equal(100, decision.Lasso!.X);
        Assert.Equal(100, decision.Lasso.Y);
        Assert.Equal(200, decision.Lasso.Width);
        Assert.Equal(150, decision.Lasso.Height);
    }

    /// <summary>
    /// A movement of exactly the threshold is not a drag: a plain right click must keep working and
    /// the desktop context menu must not be suppressed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(DesktopGestureStateMachine.DragThreshold)]
    public void RightClick_WithinThreshold_DoesNotLassoAndDoesNotSwallow(int travel)
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmRButtonDown, 100, 100);
        machine.Process(DesktopGestureStateMachine.WmMouseMove, 100 + travel, 100 + travel);
        var decision = machine.Process(DesktopGestureStateMachine.WmRButtonUp, 100 + travel, 100 + travel);

        // Assert
        Assert.False(decision.Swallow);
        Assert.Null(decision.Lasso);
        Assert.False(decision.RaisesEvent);
    }

    /// <summary>
    /// The lasso rectangle is normalised when dragging up/left, so width and height are never negative.
    /// </summary>
    [Fact]
    public void RightDrag_UpAndLeft_NormalisesRectangle()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmRButtonDown, 300, 250);
        machine.Process(DesktopGestureStateMachine.WmMouseMove, 200, 200);
        var decision = machine.Process(DesktopGestureStateMachine.WmRButtonUp, 100, 100);

        // Assert
        Assert.NotNull(decision.Lasso);
        Assert.Equal(100, decision.Lasso!.X);
        Assert.Equal(100, decision.Lasso.Y);
        Assert.Equal(200, decision.Lasso.Width);
        Assert.Equal(150, decision.Lasso.Height);
    }

    /// <summary>
    /// A right press that does not start on the desktop is ignored entirely (press over an app or panel).
    /// </summary>
    [Fact]
    public void RightDrag_StartedOffDesktop_IsIgnored()
    {
        // Arrange
        var env = new FakeGestureEnvironment { IsDesktop = false };
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmRButtonDown, 100, 100);
        machine.Process(DesktopGestureStateMachine.WmMouseMove, 400, 400);
        var decision = machine.Process(DesktopGestureStateMachine.WmRButtonUp, 400, 400);

        // Assert
        Assert.False(machine.IsRightDown);
        Assert.False(decision.Swallow);
        Assert.Null(decision.Lasso);
    }

    /// <summary>
    /// Regression guard for the "latched hook" defect: the right-button state is already reset when the
    /// completed-lasso decision is returned, so a subscriber that throws while the caller raises
    /// <c>LassoCompleted</c> cannot leave the machine stuck in "right button down".
    /// </summary>
    [Fact]
    public void RightButtonUp_ResetsStateBeforeDecisionIsReturned()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        machine.Process(DesktopGestureStateMachine.WmRButtonDown, 100, 100);
        machine.Process(DesktopGestureStateMachine.WmMouseMove, 400, 400);
        Assert.True(machine.IsRightDown);
        Assert.True(machine.IsRightDragging);

        // Act — the caller raises events only after this returns; state must already be clean.
        var decision = machine.Process(DesktopGestureStateMachine.WmRButtonUp, 400, 400);

        // Assert
        Assert.NotNull(decision.Lasso);
        Assert.False(machine.IsRightDown);
        Assert.False(machine.IsRightDragging);
    }

    /// <summary>
    /// After a completed lasso (whose subscriber may have thrown) the very next right click behaves
    /// normally: no leftover drag state turns it into a second lasso.
    /// </summary>
    [Fact]
    public void RightClick_AfterCompletedLasso_IsNotTreatedAsDrag()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        machine.Process(DesktopGestureStateMachine.WmRButtonDown, 100, 100);
        machine.Process(DesktopGestureStateMachine.WmMouseMove, 400, 400);
        machine.Process(DesktopGestureStateMachine.WmRButtonUp, 400, 400);

        // Act — a plain right click, no movement.
        machine.Process(DesktopGestureStateMachine.WmRButtonDown, 500, 500);
        var decision = machine.Process(DesktopGestureStateMachine.WmRButtonUp, 500, 500);

        // Assert
        Assert.False(decision.Swallow);
        Assert.Null(decision.Lasso);
    }

    /// <summary>
    /// Two quick clicks close together on empty desktop raise the double-click gesture, without
    /// suppressing the message.
    /// </summary>
    [Fact]
    public void DoubleClick_OnEmptyDesktop_RaisesGesture()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);
        env.TickCount += DoubleClickWindowMs - 1;
        var decision = machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);

        // Assert
        Assert.True(decision.RaiseDoubleClick);
        Assert.False(decision.Swallow);
    }

    /// <summary>
    /// The double-click slop is inclusive: within it the second click still counts, beyond it it does not.
    /// </summary>
    [Theory]
    [InlineData(DesktopGestureStateMachine.DoubleClickSlop, true)]
    [InlineData(DesktopGestureStateMachine.DoubleClickSlop + 1, false)]
    public void DoubleClick_RespectsSlop(int offset, bool expected)
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);
        env.TickCount += 50;
        var decision = machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100 + offset, 100);

        // Assert
        Assert.Equal(expected, decision.RaiseDoubleClick);
    }

    /// <summary>
    /// A second click after the system double-click time is a new single click, not a double.
    /// </summary>
    [Fact]
    public void DoubleClick_OutsideSystemWindow_IsNotRaised()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);
        env.TickCount += DoubleClickWindowMs + 1;
        var decision = machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);

        // Assert
        Assert.False(decision.RaiseDoubleClick);
    }

    /// <summary>
    /// A double click on an icon (non-empty desktop point) must not toggle the desktop icons.
    /// </summary>
    [Fact]
    public void DoubleClick_OnIcon_IsNotRaised()
    {
        // Arrange
        var env = new FakeGestureEnvironment { IsEmptyDesktop = false };
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);
        env.TickCount += 50;
        var decision = machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);

        // Assert
        Assert.False(decision.RaiseDoubleClick);
        Assert.False(decision.RaisesEvent);
    }

    /// <summary>
    /// A triple click raises exactly one double-click gesture: the second click is consumed.
    /// </summary>
    [Fact]
    public void TripleClick_RaisesDoubleClickOnlyOnce()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);
        env.TickCount += 50;
        var second = machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);
        env.TickCount += 50;
        var third = machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);

        // Assert
        Assert.True(second.RaiseDoubleClick);
        Assert.False(third.RaiseDoubleClick);
    }

    /// <summary>
    /// The expensive desktop hit-test is only performed for an actual double click, not on every click —
    /// the machine runs inside a global mouse hook, so needless native work is a system-wide cost.
    /// </summary>
    [Fact]
    public void SingleClick_DoesNotHitTestDesktop()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmLButtonDown, 100, 100);

        // Assert
        Assert.Equal(0, env.EmptyDesktopQueries);
    }

    /// <summary>
    /// Messages the gestures do not care about change nothing and are never suppressed.
    /// </summary>
    [Theory]
    [InlineData(0x0202)] // WM_LBUTTONUP
    [InlineData(0x0207)] // WM_MBUTTONDOWN
    [InlineData(0x020A)] // WM_MOUSEWHEEL
    public void UnrelatedMessages_AreIgnored(int message)
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        var decision = machine.Process(message, 100, 100);

        // Assert
        Assert.Equal(DesktopGestureDecision.None, decision);
        Assert.False(decision.RaisesEvent);
    }

    /// <summary>
    /// A mouse move without a preceding right press never arms the lasso.
    /// </summary>
    [Fact]
    public void MouseMove_WithoutRightButton_DoesNotArmLasso()
    {
        // Arrange
        var env = new FakeGestureEnvironment();
        var machine = new DesktopGestureStateMachine(env);

        // Act
        machine.Process(DesktopGestureStateMachine.WmMouseMove, 400, 400);
        var decision = machine.Process(DesktopGestureStateMachine.WmRButtonUp, 800, 800);

        // Assert
        Assert.False(machine.IsRightDragging);
        Assert.False(decision.Swallow);
        Assert.Null(decision.Lasso);
    }
}

/// <summary>
/// Deterministic stand-in for the OS: a controllable clock and scripted desktop hit-testing.
/// </summary>
file sealed class FakeGestureEnvironment : IDesktopGestureEnvironment
{
    /// <summary>Gets or sets the fake clock, advanced explicitly by the tests.</summary>
    public int TickCount { get; set; } = 10_000;

    /// <summary>Gets the double-click window; matches the constant used by the tests.</summary>
    public uint DoubleClickTime => 500;

    /// <summary>Gets or sets a value indicating whether points are reported as being over the desktop.</summary>
    public bool IsDesktop { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether points are reported as empty desktop.</summary>
    public bool IsEmptyDesktop { get; set; } = true;

    /// <summary>Gets the number of empty-desktop hit-tests performed.</summary>
    public int EmptyDesktopQueries { get; private set; }

    /// <inheritdoc />
    public bool IsDesktopPoint(int x, int y) => IsDesktop;

    /// <inheritdoc />
    public bool IsEmptyDesktopPoint(int x, int y)
    {
        EmptyDesktopQueries++;
        return IsEmptyDesktop;
    }
}
