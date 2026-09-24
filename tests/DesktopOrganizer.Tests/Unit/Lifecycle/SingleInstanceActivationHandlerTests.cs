using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Services;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// T-ACT-01..03 (docs/PLANS/phase-3-single-instance-ipc.md §5.3) for
/// <see cref="SingleInstanceActivationHandler"/> — the machine-checkable definition of ADR-023 D-8
/// ("activating an existing instance never changes desktop panels").
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class SingleInstanceActivationHandlerTests
{
    private readonly Mock<IWindowManager> _windowManagerMock = new(MockBehavior.Strict);

    // Counts invocations instead of running the action synchronously in-place, so a test can tell
    // "the work was marshaled through uiInvoke" apart from "the work happened to run on this thread
    // anyway" — see the ctor remarks on SingleInstanceActivationHandler's uiInvoke parameter.
    private int _uiInvokeCallCount;

    private void UiInvoke(Action action)
    {
        _uiInvokeCallCount++;
        action();
    }

    // T-ACT-01
    [Fact]
    public void Handle_ActivateSettings_CallsShowMainWindow_AndNothingElse()
    {
        _windowManagerMock.Setup(m => m.ShowMainWindow());
        var handler = new SingleInstanceActivationHandler(_windowManagerMock.Object, UiInvoke);

        handler.Handle(new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 1));

        _windowManagerMock.Verify(m => m.ShowMainWindow(), Times.Once);
        _windowManagerMock.VerifyNoOtherCalls();
    }

    // T-ACT-02
    [Fact]
    public void Handle_ActivateSettings_MarshalsThroughUiInvoke()
    {
        _windowManagerMock.Setup(m => m.ShowMainWindow());
        var handler = new SingleInstanceActivationHandler(_windowManagerMock.Object, UiInvoke);

        handler.Handle(new ActivationRequest(1, ActivationCommand.ActivateSettings, SessionId: 1));

        Assert.Equal(1, _uiInvokeCallCount);
    }

    /// <summary>
    /// Never calls the fake uiInvoke at all for an unrecognized command — pins that a future protocol
    /// version (<c>DO/2</c>, ADR-023 D-5) with a command this build doesn't understand yet is inert,
    /// not just "harmless to IWindowManager".
    /// </summary>
    [Fact]
    public void Handle_UnrecognizedCommand_DoesNotInvokeWindowManager_OrUiInvoke()
    {
        var handler = new SingleInstanceActivationHandler(_windowManagerMock.Object, UiInvoke);

        handler.Handle(new ActivationRequest(1, (ActivationCommand)999, SessionId: 1));

        _windowManagerMock.VerifyNoOtherCalls();
        Assert.Equal(0, _uiInvokeCallCount);
    }
}
