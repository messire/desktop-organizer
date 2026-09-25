using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="DesktopGestureService"/>: the hook lifecycle (<see cref="DesktopGestureService.Start"/>/
/// <see cref="DesktopGestureService.Dispose"/>), the <c>IDesktopGestureEnvironment</c> members it exposes to
/// <see cref="DesktopGestureStateMachine"/>, and the wiring between the WH_MOUSE_LL callback and that state machine.
/// </summary>
/// <remarks>
/// The gesture-recognition logic itself (drag threshold, double-click slop, normalisation) is already covered by
/// <c>DesktopGestureStateMachineTests</c> against a fake environment — these tests only verify that
/// <see cref="DesktopGestureService"/> correctly translates <see cref="INativeApi"/> calls/results into that
/// machine's inputs and back into its public events. There is no real hook or message loop here: the hook
/// procedure passed to <c>SetWindowsHookEx</c> is captured via the mock and invoked directly, per ADR-003.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DesktopGestureServiceTests
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMouseMove = 0x0200;
    private const int WmRButtonUp = 0x0205;

    private static readonly IntPtr ModuleHandle = new(1000);
    private static readonly IntPtr HookHandle = new(2000);

    private readonly Mock<INativeApi> _nativeApiMock = new();
    private readonly DesktopGestureService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopGestureServiceTests"/> class.
    /// </summary>
    public DesktopGestureServiceTests()
    {
        _nativeApiMock.Setup(api => api.GetModuleHandle(null)).Returns(ModuleHandle);
        _service = new DesktopGestureService(_nativeApiMock.Object);
    }

    // ── Start/Dispose lifecycle ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DesktopGestureService.Start"/> installs the low-level mouse hook via
    /// <c>SetWindowsHookEx</c> for the module handle returned by <see cref="INativeApi.GetModuleHandle(string?)"/>.
    /// </summary>
    [Fact]
    public void Start_InstallsHook_ViaSetWindowsHookEx()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0))
            .Returns(HookHandle);

        // Act
        _service.Start();

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0),
            Times.Once);
    }

    /// <summary>
    /// Verifies that calling <see cref="DesktopGestureService.Start"/> a second time is a no-op and does not
    /// install a second hook.
    /// </summary>
    [Fact]
    public void Start_CalledTwice_InstallsHookOnlyOnce()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0))
            .Returns(HookHandle);

        // Act
        _service.Start();
        _service.Start();

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0),
            Times.Once);
    }

    /// <summary>
    /// Regression guard for the CRITICAL native-layer fix: a failed hook install must degrade the app
    /// to "gestures unavailable" rather than throwing, and the reason must still be observable.
    /// </summary>
    [Fact]
    public void Start_WhenHookInstallFails_LogsToConsoleErrorAndDoesNotThrow()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0))
            .Returns(IntPtr.Zero);

        using var consoleOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(consoleOutput);

        try
        {
            // Act
            var ex = Record.Exception(() => _service.Start());

            // Assert
            Assert.Null(ex);
            Assert.Contains("Desktop gesture hook could not be installed", consoleOutput.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// Verifies that <see cref="DesktopGestureService.Dispose"/> does nothing when the hook was never installed.
    /// </summary>
    [Fact]
    public void Dispose_WithoutStart_DoesNotCallUnhook()
    {
        // Act
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.UnhookWindowsHookEx(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopGestureService.Dispose"/> uninstalls the hook that was installed by
    /// a preceding <see cref="DesktopGestureService.Start"/> call.
    /// </summary>
    [Fact]
    public void Dispose_AfterSuccessfulStart_UninstallsHook()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0))
            .Returns(HookHandle);
        _nativeApiMock.Setup(api => api.UnhookWindowsHookEx(HookHandle)).Returns(true);
        _service.Start();

        // Act
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.UnhookWindowsHookEx(HookHandle), Times.Once);
    }

    /// <summary>
    /// Verifies that calling <see cref="DesktopGestureService.Dispose"/> twice uninstalls the hook only once.
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_UninstallsOnlyOnce()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0))
            .Returns(HookHandle);
        _service.Start();

        // Act
        _service.Dispose();
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.UnhookWindowsHookEx(HookHandle), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="DesktopGestureService.Dispose"/> does not attempt to uninstall the hook when
    /// the earlier <see cref="DesktopGestureService.Start"/> call failed to install one.
    /// </summary>
    [Fact]
    public void Dispose_WhenHookInstallFailed_DoesNotCallUnhook()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0))
            .Returns(IntPtr.Zero);
        _service.Start();

        // Act
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.UnhookWindowsHookEx(It.IsAny<IntPtr>()), Times.Never);
    }

    // ── IDesktopGestureEnvironment: explicit-interface members delegating to INativeApi ────────

    /// <summary>
    /// Verifies that the explicit <c>IDesktopGestureEnvironment.DoubleClickTime</c> property delegates to
    /// <see cref="INativeApi.GetDoubleClickTime"/>.
    /// </summary>
    [Fact]
    public void Environment_DoubleClickTime_DelegatesToNativeApi()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.GetDoubleClickTime()).Returns(777u);
        var env = (IDesktopGestureEnvironment)_service;

        // Act & Assert
        Assert.Equal(777u, env.DoubleClickTime);
    }

    /// <summary>
    /// Verifies that the explicit <c>IDesktopGestureEnvironment.TickCount</c> property returns the current
    /// <see cref="Environment.TickCount"/> value rather than a value derived from <see cref="INativeApi"/>.
    /// </summary>
    [Fact]
    public void Environment_TickCount_ReturnsEnvironmentTickCount()
    {
        // Arrange
        var env = (IDesktopGestureEnvironment)_service;
        var before = Environment.TickCount;

        // Act
        var actual = env.TickCount;
        var after = Environment.TickCount;

        // Assert
        Assert.InRange(actual, before, after);
    }

    /// <summary>
    /// Verifies that <c>IDesktopGestureEnvironment.IsDesktopPoint</c> classifies a point as desktop-owned
    /// based on the window class of the window found at that point.
    /// </summary>
    /// <param name="className">The window class name reported for the window under the point.</param>
    /// <param name="expected">The expected classification result for that class name.</param>
    [Theory]
    [InlineData("SysListView32", true)]
    [InlineData("SHELLDLL_DefView", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Progman", true)]
    [InlineData("Notepad", false)]
    [InlineData("", false)]
    public void Environment_IsDesktopPoint_ChecksWindowClassOfWindowUnderPoint(string className, bool expected)
    {
        // Arrange
        var hwnd = new IntPtr(42);
        _nativeApiMock.Setup(api => api.WindowFromPoint(10, 20)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns(className);
        var env = (IDesktopGestureEnvironment)_service;

        // Act & Assert
        Assert.Equal(expected, env.IsDesktopPoint(10, 20));
    }

    /// <summary>
    /// Verifies that <c>IDesktopGestureEnvironment.IsEmptyDesktopPoint</c> treats the WorkerW/Progman background
    /// layers as always empty, without querying the list view for the actual icon layout.
    /// </summary>
    /// <param name="className">The background-layer window class under test.</param>
    [Theory]
    [InlineData("WorkerW")]
    [InlineData("Progman")]
    public void Environment_IsEmptyDesktopPoint_BackgroundLayer_ReturnsTrueWithoutListViewCheck(string className)
    {
        // Arrange
        var hwnd = new IntPtr(42);
        _nativeApiMock.Setup(api => api.WindowFromPoint(10, 20)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns(className);
        var env = (IDesktopGestureEnvironment)_service;

        // Act
        var result = env.IsEmptyDesktopPoint(10, 20);

        // Assert
        Assert.True(result);
        _nativeApiMock.Verify(
            api => api.IsListViewPointEmpty(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    /// <summary>
    /// Verifies that <c>IDesktopGestureEnvironment.IsEmptyDesktopPoint</c> delegates directly to
    /// <see cref="INativeApi.IsListViewPointEmpty(IntPtr, int, int)"/> when the point is over a
    /// <c>SysListView32</c> window.
    /// </summary>
    [Fact]
    public void Environment_IsEmptyDesktopPoint_SysListView32_DelegatesToIsListViewPointEmpty()
    {
        // Arrange
        var hwnd = new IntPtr(42);
        _nativeApiMock.Setup(api => api.WindowFromPoint(10, 20)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("SysListView32");
        _nativeApiMock.Setup(api => api.IsListViewPointEmpty(hwnd, 10, 20)).Returns(true);
        var env = (IDesktopGestureEnvironment)_service;

        // Act & Assert
        Assert.True(env.IsEmptyDesktopPoint(10, 20));
    }

    /// <summary>
    /// Verifies that when the point is over a <c>SHELLDLL_DefView</c> window, the environment first locates
    /// the child <c>SysListView32</c> and checks emptiness against that child, not the parent.
    /// </summary>
    [Fact]
    public void Environment_IsEmptyDesktopPoint_ShellDllDefView_FindsChildListViewFirst()
    {
        // Arrange
        var hwnd = new IntPtr(42);
        var listView = new IntPtr(43);
        _nativeApiMock.Setup(api => api.WindowFromPoint(10, 20)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("SHELLDLL_DefView");
        _nativeApiMock.Setup(api => api.FindWindowEx(hwnd, IntPtr.Zero, "SysListView32", null)).Returns(listView);
        _nativeApiMock.Setup(api => api.IsListViewPointEmpty(listView, 10, 20)).Returns(true);
        var env = (IDesktopGestureEnvironment)_service;

        // Act & Assert
        Assert.True(env.IsEmptyDesktopPoint(10, 20));
    }

    /// <summary>
    /// Verifies that when no <c>SysListView32</c> child is found under a <c>SHELLDLL_DefView</c> window, the
    /// point is reported as not empty rather than throwing or guessing.
    /// </summary>
    [Fact]
    public void Environment_IsEmptyDesktopPoint_ShellDllDefView_WhenNoListViewChildFound_ReturnsFalse()
    {
        // Arrange
        var hwnd = new IntPtr(42);
        _nativeApiMock.Setup(api => api.WindowFromPoint(10, 20)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("SHELLDLL_DefView");
        _nativeApiMock.Setup(api => api.FindWindowEx(hwnd, IntPtr.Zero, "SysListView32", null)).Returns(IntPtr.Zero);
        var env = (IDesktopGestureEnvironment)_service;

        // Act & Assert
        Assert.False(env.IsEmptyDesktopPoint(10, 20));
    }

    /// <summary>
    /// Verifies that a point over a window class unrelated to the desktop's icon list is never reported as an
    /// empty desktop point.
    /// </summary>
    [Fact]
    public void Environment_IsEmptyDesktopPoint_UnknownWindowClass_ReturnsFalse()
    {
        // Arrange
        var hwnd = new IntPtr(42);
        _nativeApiMock.Setup(api => api.WindowFromPoint(10, 20)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("Notepad");
        var env = (IDesktopGestureEnvironment)_service;

        // Act & Assert
        Assert.False(env.IsEmptyDesktopPoint(10, 20));
    }

    // ── Hook callback wiring: HookCallback -> state machine -> public events ────────────────────

    /// <summary>
    /// Verifies that a negative hook code is forwarded to <c>CallNextHookEx</c> untouched, per the
    /// WH_MOUSE_LL contract, without reading the mouse point or touching the state machine.
    /// </summary>
    [Fact]
    public void HookCallback_NegativeCode_PassesThroughWithoutProcessingMessage()
    {
        // Arrange
        var proc = CaptureHookProc();
        var next = new IntPtr(999);
        _nativeApiMock.Setup(api => api.CallNextHookEx(HookHandle, -1, IntPtr.Zero, IntPtr.Zero)).Returns(next);

        // Act
        var result = proc(-1, IntPtr.Zero, IntPtr.Zero);

        // Assert
        Assert.Equal(next, result);
        _nativeApiMock.Verify(api => api.ReadMouseHookPoint(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Verifies that two left-button clicks within the double-click window over an empty desktop point raise
    /// <see cref="DesktopGestureService.DesktopDoubleClicked"/> and still let the message pass through.
    /// </summary>
    [Fact]
    public void HookCallback_DoubleClickOnEmptyDesktop_RaisesDesktopDoubleClickedAndDoesNotSwallow()
    {
        // Arrange
        var proc = CaptureHookProc();
        var hwnd = new IntPtr(50);
        var lParam = new IntPtr(1);
        var wParam = new IntPtr(WmLButtonDown);
        var next = new IntPtr(321);

        _nativeApiMock.Setup(api => api.ReadMouseHookPoint(lParam)).Returns(new System.Drawing.Point(100, 100));
        _nativeApiMock.Setup(api => api.GetDoubleClickTime()).Returns(1000u); // generous window for the test
        _nativeApiMock.Setup(api => api.WindowFromPoint(100, 100)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("Progman");
        _nativeApiMock.Setup(api => api.CallNextHookEx(HookHandle, 0, wParam, lParam)).Returns(next);

        var raised = false;
        _service.DesktopDoubleClicked += (_, _) => raised = true;

        // Act
        proc(0, wParam, lParam); // first click
        var result = proc(0, wParam, lParam); // second click within the double-click window

        // Assert
        Assert.True(raised);
        Assert.Equal(next, result); // a double click is never swallowed
    }

    /// <summary>
    /// Verifies that a right-button drag over the desktop raises <see cref="DesktopGestureService.LassoCompleted"/>
    /// with the drag's bounding rectangle and swallows the final button-up so the shell context menu never appears.
    /// </summary>
    [Fact]
    public void HookCallback_RightButtonLassoDragOverDesktop_SwallowsAndRaisesLassoCompleted()
    {
        // Arrange
        var proc = CaptureHookProc();
        var hwnd = new IntPtr(60);
        var lParamDown = new IntPtr(1);
        var lParamMove = new IntPtr(2);
        var lParamUp = new IntPtr(3);

        _nativeApiMock.Setup(api => api.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>())).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("Progman");
        _nativeApiMock.Setup(api => api.ReadMouseHookPoint(lParamDown)).Returns(new System.Drawing.Point(100, 100));
        _nativeApiMock.Setup(api => api.ReadMouseHookPoint(lParamMove)).Returns(new System.Drawing.Point(130, 100));
        _nativeApiMock.Setup(api => api.ReadMouseHookPoint(lParamUp)).Returns(new System.Drawing.Point(150, 130));
        _nativeApiMock
            .Setup(api => api.CallNextHookEx(HookHandle, 0, It.IsAny<IntPtr>(), It.IsAny<IntPtr>()))
            .Returns(new IntPtr(777));

        DesktopLassoEventArgs? lasso = null;
        _service.LassoCompleted += (_, e) => lasso = e;

        // Act
        proc(0, new IntPtr(WmRButtonDown), lParamDown);
        proc(0, new IntPtr(WmMouseMove), lParamMove);
        var result = proc(0, new IntPtr(WmRButtonUp), lParamUp);

        // Assert
        Assert.NotNull(lasso);
        Assert.Equal(100, lasso!.X);
        Assert.Equal(100, lasso.Y);
        Assert.Equal(50, lasso.Width);
        Assert.Equal(30, lasso.Height);
        Assert.Equal(new IntPtr(1), result); // suppressed so the desktop context menu does not appear
    }

    /// <summary>
    /// Verifies that a right-button click without any drag between down and up is treated as an ordinary click:
    /// no lasso is raised and the button-up message is passed through unswallowed.
    /// </summary>
    [Fact]
    public void HookCallback_RightButtonClickWithoutDrag_DoesNotSwallowOrRaiseLasso()
    {
        // Arrange
        var proc = CaptureHookProc();
        var hwnd = new IntPtr(60);
        var lParam = new IntPtr(1);
        var next = new IntPtr(654);

        _nativeApiMock.Setup(api => api.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>())).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("Progman");
        _nativeApiMock.Setup(api => api.ReadMouseHookPoint(lParam)).Returns(new System.Drawing.Point(100, 100));
        _nativeApiMock.Setup(api => api.CallNextHookEx(HookHandle, 0, It.IsAny<IntPtr>(), lParam)).Returns(next);

        var lassoRaised = false;
        _service.LassoCompleted += (_, _) => lassoRaised = true;

        // Act
        proc(0, new IntPtr(WmRButtonDown), lParam);
        var result = proc(0, new IntPtr(WmRButtonUp), lParam);

        // Assert
        Assert.False(lassoRaised);
        Assert.Equal(next, result);
    }

    /// <summary>
    /// Verifies that an exception thrown while decoding the mouse message is logged to <see cref="Console.Error"/>
    /// and swallowed by the hook procedure, which still forwards the message via <c>CallNextHookEx</c> so
    /// system-wide input is never stalled by a bug in the gesture logic.
    /// </summary>
    [Fact]
    public void HookCallback_WhenProcessingThrows_LogsAndDoesNotSwallowOrPropagate()
    {
        // Arrange
        var proc = CaptureHookProc();
        var lParam = new IntPtr(1);
        var wParam = new IntPtr(WmRButtonDown);
        var next = new IntPtr(555);

        _nativeApiMock.Setup(api => api.ReadMouseHookPoint(lParam)).Throws(new InvalidOperationException("garbage lParam"));
        _nativeApiMock.Setup(api => api.CallNextHookEx(HookHandle, 0, wParam, lParam)).Returns(next);

        using var consoleOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(consoleOutput);

        IntPtr result;
        try
        {
            // Act
            result = proc(0, wParam, lParam);
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Assert — a bug in processing must never suppress the message nor stall system-wide input.
        Assert.Equal(next, result);
        Assert.Contains("Desktop gesture hook failed: garbage lParam", consoleOutput.ToString());
    }

    /// <summary>
    /// Verifies that an exception thrown by an external <see cref="DesktopGestureService.DesktopDoubleClicked"/>
    /// subscriber is caught and logged rather than escaping through the hook procedure.
    /// </summary>
    [Fact]
    public void HookCallback_WhenSubscriberThrows_LogsAndDoesNotPropagate()
    {
        // Arrange
        var proc = CaptureHookProc();
        var hwnd = new IntPtr(70);
        var lParam = new IntPtr(1);
        var wParam = new IntPtr(WmLButtonDown);

        _nativeApiMock.Setup(api => api.ReadMouseHookPoint(lParam)).Returns(new System.Drawing.Point(50, 50));
        _nativeApiMock.Setup(api => api.GetDoubleClickTime()).Returns(1000u);
        _nativeApiMock.Setup(api => api.WindowFromPoint(50, 50)).Returns(hwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns("Progman");
        _nativeApiMock.Setup(api => api.CallNextHookEx(HookHandle, 0, wParam, lParam)).Returns(IntPtr.Zero);

        _service.DesktopDoubleClicked += (_, _) => throw new InvalidOperationException("boom");

        using var consoleOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(consoleOutput);

        try
        {
            // Act
            proc(0, wParam, lParam); // arms the double click
            var ex = Record.Exception(() => proc(0, wParam, lParam)); // raises it, subscriber throws

            // Assert
            Assert.Null(ex);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("Desktop gesture subscriber failed: boom", consoleOutput.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private LowLevelMouseCallback CaptureHookProc(IntPtr? hookHandleResult = null)
    {
        LowLevelMouseCallback? captured = null;
        _nativeApiMock
            .Setup(api => api.SetWindowsHookEx(WhMouseLl, It.IsAny<LowLevelMouseCallback>(), ModuleHandle, 0))
            .Callback<int, LowLevelMouseCallback, IntPtr, uint>((_, proc, _, _) => captured = proc)
            .Returns(hookHandleResult ?? HookHandle);

        _service.Start();

        Assert.NotNull(captured);
        return captured!;
    }
}
