using System.Windows.Interop;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Smoke tests for <see cref="NativePanelSizingService"/> (Phase 2 commit C3): they pin the hook seam
/// itself — that <c>Attach</c> installs exactly one hook, that the captured hook procedure answers
/// <c>WM_NCHITTEST</c>/<c>WM_MOUSEACTIVATE</c>/<c>WM_SIZING</c> as specified, and that the token is a
/// deterministic, idempotent unhook (R-10).
/// </summary>
/// <remarks>
/// There is no real window or message loop here: the production hook goes through
/// <see cref="HwndSource"/>, which needs a real HWND, so the tests pass their own
/// <see cref="SizingHookInstaller"/> and invoke the captured <see cref="HwndSourceHook"/> directly — the
/// same technique used for the <c>NativeWindowProc</c> in <see cref="MonitorServiceNativeApiTests"/> and
/// for the WH_MOUSE_LL callback in <see cref="DesktopGestureServiceTests"/>.
/// The full behavioural matrix (T-SVC-01..08 of <c>docs/PLANS/phase-2-native-panel-sizing.md</c> §5 —
/// gesture lifecycle, rejected cycles, capture loss, DPI, exception containment) extends this class.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class NativePanelSizingServiceTests
{
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmSizing = 0x0214;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmCaptureChanged = 0x0215;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDpiChangedAfterParent = 0x02E3;
    private const int WmszRight = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int MaNoActivate = 3;

    private static readonly IntPtr Hwnd = new(0x1234);

    private readonly Mock<INativeApi> _nativeApiMock = new();
    private readonly Mock<INativePanelSizingClient> _clientMock = new();
    private readonly Mock<IDisposable> _hookRemovalMock = new();

    private HwndSourceHook? _hook;
    private int _installCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativePanelSizingServiceTests"/> class.
    /// </summary>
    public NativePanelSizingServiceTests()
    {
        var windowRect = new NativeRectPx(100, 200, 500, 600);
        _nativeApiMock
            .Setup(api => api.GetWindowRect(Hwnd, out windowRect))
            .Returns(true);
        _nativeApiMock.Setup(api => api.GetDpiScaleForWindow(Hwnd)).Returns(1.0);
    }

    // ── Attach / Dispose lifecycle ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="NativePanelSizingService.Attach"/> installs the sizing hook exactly once
    /// per HWND, and that a second <c>Attach</c> for the same handle is a no-op that returns a distinct,
    /// separately disposable token without touching the underlying hook removal.
    /// </summary>
    [Fact]
    public void Attach_InstallsHookOnce_AndSecondAttachIsNoOp()
    {
        // Arrange
        var service = CreateService();

        // Act
        var first = service.Attach(Hwnd, _clientMock.Object);
        var second = service.Attach(Hwnd, Mock.Of<INativePanelSizingClient>());

        // Assert
        Assert.Equal(1, _installCount);
        Assert.NotSame(first, second);

        second.Dispose();
        _hookRemovalMock.Verify(d => d.Dispose(), Times.Never);
    }

    /// <summary>
    /// Verifies that disposing the token returned by <see cref="NativePanelSizingService.Attach"/>
    /// removes the hook exactly once even when disposed twice, and that the HWND is free for a fresh
    /// attach afterwards.
    /// </summary>
    [Fact]
    public void Dispose_RemovesHook_AndIsIdempotent()
    {
        // Arrange
        var service = CreateService();
        var token = service.Attach(Hwnd, _clientMock.Object);

        // Act
        token.Dispose();
        token.Dispose();

        // Assert
        _hookRemovalMock.Verify(d => d.Dispose(), Times.Once);

        // ...and the HWND is free for a fresh attach afterwards.
        service.Attach(Hwnd, _clientMock.Object);
        Assert.Equal(2, _installCount);
    }

    // ── WM_NCHITTEST ────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>WM_NCHITTEST</c> is answered with the hit-test code matching the sizing edge the
    /// client reports for the hit point.
    /// </summary>
    [Fact]
    public void WmNcHitTest_MapsClientEdge_ToSizingHitTestCode()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        _clientMock
            .Setup(c => c.HitTest(It.IsAny<NativePointPx>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(NativeSizingEdge.Right);
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmNcHitTest, IntPtr.Zero, MakePoint(499, 400), ref handled);

        // Assert
        Assert.True(handled);
        Assert.Equal(HtRight, result.ToInt32());
        _clientMock.Verify(
            c => c.HitTest(new NativePointPx(499, 400), new NativeRectPx(100, 200, 500, 600), 1.0),
            Times.Once);
    }

    /// <summary>
    /// Verifies that <c>WM_NCHITTEST</c> is left unhandled when the client reports
    /// <see cref="NativeSizingEdge.None"/>, so WPF/<c>DefWindowProc</c> keep their own hit testing.
    /// </summary>
    [Fact]
    public void WmNcHitTest_LeavesMessageUnhandled_WhenClientReportsNoEdge()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        _clientMock
            .Setup(c => c.HitTest(It.IsAny<NativePointPx>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(NativeSizingEdge.None);
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmNcHitTest, IntPtr.Zero, MakePoint(300, 400), ref handled);

        // Assert — WPF/DefWindowProc keep their own hit testing (HTCLIENT).
        Assert.False(handled);
        Assert.Equal(IntPtr.Zero, result);
    }

    /// <summary>
    /// Verifies that an exception thrown by the client's hit-test callback never escapes the window
    /// procedure.
    /// </summary>
    [Fact]
    public void WmNcHitTest_DoesNotThrow_WhenClientThrows()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        _clientMock
            .Setup(c => c.HitTest(It.IsAny<NativePointPx>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Throws(new InvalidOperationException("boom"));
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmNcHitTest, IntPtr.Zero, MakePoint(499, 400), ref handled);

        // Assert — an exception must never escape a window procedure.
        Assert.False(handled);
        Assert.Equal(IntPtr.Zero, result);
    }

    // ── WM_MOUSEACTIVATE (R-2) ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>WM_MOUSEACTIVATE</c> is always answered with <c>MA_NOACTIVATE</c>, per R-2.
    /// </summary>
    [Fact]
    public void WmMouseActivate_AlwaysAnswersNoActivate()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmMouseActivate, IntPtr.Zero, IntPtr.Zero, ref handled);

        // Assert
        Assert.True(handled);
        Assert.Equal(MaNoActivate, result.ToInt32());
    }

    // ── WM_SIZING outside a gesture ─────────────────────────────────────────────

    /// <summary>
    /// Verifies that a <c>WM_SIZING</c> message received without a preceding <c>WM_ENTERSIZEMOVE</c> is
    /// ignored outright: not even the proposed rectangle is read.
    /// </summary>
    [Fact]
    public void WmSizing_IsIgnored_WithoutEnterSizeMove()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmSizing, new IntPtr(WmszRight), new IntPtr(0xBADF00D), ref handled);

        // Assert — no cycle is running, so the proposed rectangle is not even read.
        Assert.False(handled);
        Assert.Equal(IntPtr.Zero, result);
        _nativeApiMock.Verify(api => api.ReadRect(It.IsAny<IntPtr>()), Times.Never);
        _clientMock.Verify(
            c => c.ProposeBounds(It.IsAny<NativeSizingEdge>(), It.IsAny<NativeRectPx>()),
            Times.Never);
    }

    // ── WM_NCHITTEST — full 8-edge mapping ──────────────────────────────────────

    /// <summary>
    /// Verifies that every <see cref="NativeSizingEdge"/> value the client can report maps to its own,
    /// distinct <c>WM_NCHITTEST</c> result code.
    /// </summary>
    /// <param name="edge">The sizing edge the mocked client is set up to report.</param>
    /// <param name="expectedHitTestCode">The hit-test code expected for that edge.</param>
    [Theory]
    [InlineData(NativeSizingEdge.Left, HtLeft)]
    [InlineData(NativeSizingEdge.Top, HtTop)]
    [InlineData(NativeSizingEdge.Right, HtRight)]
    [InlineData(NativeSizingEdge.Bottom, HtBottom)]
    [InlineData(NativeSizingEdge.TopLeft, HtTopLeft)]
    [InlineData(NativeSizingEdge.TopRight, HtTopRight)]
    [InlineData(NativeSizingEdge.BottomLeft, HtBottomLeft)]
    [InlineData(NativeSizingEdge.BottomRight, HtBottomRight)]
    public void WmNcHitTest_MapsEveryEdge_ToItsOwnHitTestCode(NativeSizingEdge edge, int expectedHitTestCode)
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        _clientMock
            .Setup(c => c.HitTest(It.IsAny<NativePointPx>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(edge);
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmNcHitTest, IntPtr.Zero, MakePoint(300, 400), ref handled);

        // Assert
        Assert.True(handled);
        Assert.Equal(expectedHitTestCode, result.ToInt32());
    }

    // ── WM_ENTERSIZEMOVE lifecycle ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>WM_ENTERSIZEMOVE</c> seeds the sizing edge from the most recent
    /// <c>WM_NCHITTEST</c> result and notifies the client with the current window rectangle and DPI.
    /// </summary>
    [Fact]
    public void WmEnterSizeMove_SeedsEdgeFromLastHitTest_AndCallsClientWithWindowRectAndDpi()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        _clientMock
            .Setup(c => c.HitTest(It.IsAny<NativePointPx>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(NativeSizingEdge.Bottom);
        var handled = false;
        _hook!(Hwnd, WmNcHitTest, IntPtr.Zero, MakePoint(300, 599), ref handled); // seeds _lastSizingHit = Bottom
        _clientMock
            .Setup(c => c.SizeMoveEntered(NativeSizingEdge.Bottom, new NativeRectPx(100, 200, 500, 600), 1.0))
            .Returns(true);

        // Act
        _hook!(Hwnd, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero, ref handled);

        // Assert
        _clientMock.Verify(
            c => c.SizeMoveEntered(NativeSizingEdge.Bottom, new NativeRectPx(100, 200, 500, 600), 1.0),
            Times.Once);
    }

    /// <summary>
    /// Verifies that once the client rejects <c>WM_ENTERSIZEMOVE</c>, subsequent <c>WM_SIZING</c>
    /// messages in the same cycle never reach the client — the rectangle is not even read.
    /// </summary>
    [Fact]
    public void WmEnterSizeMove_ClientRejects_SubsequentWmSizingInSameCycleNeverReachesClient()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginRejectedCycle(NativeSizingEdge.Right, 499, 400);
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmSizing, new IntPtr(WmszRight), new IntPtr(0xBADF00D), ref handled);

        // Assert — same contract as WmSizing_IsIgnored_WithoutEnterSizeMove: a rejected cycle must not
        // touch the rectangle at all.
        Assert.False(handled);
        Assert.Equal(IntPtr.Zero, result);
        _nativeApiMock.Verify(api => api.ReadRect(It.IsAny<IntPtr>()), Times.Never);
        _clientMock.Verify(
            c => c.ProposeBounds(It.IsAny<NativeSizingEdge>(), It.IsAny<NativeRectPx>()),
            Times.Never);
    }

    // ── WM_SIZING — accepted cycle ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that within an accepted sizing cycle, <c>WM_SIZING</c> reads the proposed rectangle,
    /// passes it to the client for adjustment, and writes the adjusted rectangle back.
    /// </summary>
    [Fact]
    public void WmSizing_AcceptedCycle_ReadsProposedRect_CallsClient_AndWritesAdjustedRectBack()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Right, 499, 400);

        var lParam = new IntPtr(0x1000);
        var proposed = new NativeRectPx(100, 200, 550, 600);
        var adjusted = new NativeRectPx(100, 200, 548, 600);
        _nativeApiMock.Setup(api => api.ReadRect(lParam)).Returns(proposed);
        _clientMock.Setup(c => c.ProposeBounds(NativeSizingEdge.Right, proposed)).Returns(adjusted);
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmSizing, new IntPtr(WmszRight), lParam, ref handled);

        // Assert
        Assert.True(handled);
        Assert.Equal(1, result.ToInt32());
        _nativeApiMock.Verify(api => api.ReadRect(lParam), Times.Once);
        _clientMock.Verify(c => c.ProposeBounds(NativeSizingEdge.Right, proposed), Times.Once);
        _nativeApiMock.Verify(api => api.WriteRect(lParam, adjusted), Times.Once);
    }

    /// <summary>
    /// Verifies that a <c>WMSZ_*</c> wParam value outside the documented 1..8 range falls back to the
    /// edge the cycle was seeded with at <c>WM_ENTERSIZEMOVE</c>, instead of <c>None</c>.
    /// </summary>
    [Fact]
    public void WmSizing_UnmappedWmszValue_FallsBackToTheCyclesSeededEdge()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Bottom, 300, 599);

        var lParam = new IntPtr(0x2000);
        var proposed = new NativeRectPx(100, 200, 500, 650);
        _nativeApiMock.Setup(api => api.ReadRect(lParam)).Returns(proposed);
        _clientMock
            .Setup(c => c.ProposeBounds(It.IsAny<NativeSizingEdge>(), It.IsAny<NativeRectPx>()))
            .Returns(proposed);
        var handled = false;

        // Act — 999 is not a documented WMSZ_* (1..8) value.
        _hook!(Hwnd, WmSizing, new IntPtr(999), lParam, ref handled);

        // Assert — falls back to the edge WM_ENTERSIZEMOVE seeded, not None.
        _clientMock.Verify(c => c.ProposeBounds(NativeSizingEdge.Bottom, proposed), Times.Once);
    }

    /// <summary>
    /// Verifies that an exception thrown by the client's <c>ProposeBounds</c> callback never escapes the
    /// window procedure.
    /// </summary>
    [Fact]
    public void WmSizing_ClientThrows_DoesNotEscapeTheHook()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Right, 499, 400);
        _nativeApiMock.Setup(api => api.ReadRect(It.IsAny<IntPtr>())).Returns(new NativeRectPx(100, 200, 550, 600));
        _clientMock
            .Setup(c => c.ProposeBounds(It.IsAny<NativeSizingEdge>(), It.IsAny<NativeRectPx>()))
            .Throws(new InvalidOperationException("boom"));
        var handled = false;

        // Act
        var result = _hook!(Hwnd, WmSizing, new IntPtr(WmszRight), new IntPtr(0x3000), ref handled);

        // Assert — an exception must never escape a window procedure.
        Assert.False(handled);
        Assert.Equal(IntPtr.Zero, result);
    }

    // ── WM_EXITSIZEMOVE ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that ending an accepted sizing cycle with <c>WM_EXITSIZEMOVE</c> reports the final window
    /// rectangle to the client via <c>SizeMoveExited</c>, without ever calling <c>SizeMoveAborted</c>.
    /// </summary>
    [Fact]
    public void WmExitSizeMove_AcceptedCycle_ReportsFinalWindowRectToClient()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Right, 499, 400);
        var handled = false;

        // Act
        _hook!(Hwnd, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero, ref handled);

        // Assert
        _clientMock.Verify(c => c.SizeMoveExited(new NativeRectPx(100, 200, 500, 600)), Times.Once);
        _clientMock.Verify(c => c.SizeMoveAborted(), Times.Never);
    }

    /// <summary>
    /// Verifies that ending a cycle the client had rejected never notifies the client — neither
    /// <c>SizeMoveExited</c> nor <c>SizeMoveAborted</c> fires.
    /// </summary>
    [Fact]
    public void WmExitSizeMove_RejectedCycle_NeverNotifiesTheClient()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginRejectedCycle(NativeSizingEdge.Right, 499, 400);
        var handled = false;

        // Act
        _hook!(Hwnd, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero, ref handled);

        // Assert
        _clientMock.Verify(c => c.SizeMoveExited(It.IsAny<NativeRectPx>()), Times.Never);
        _clientMock.Verify(c => c.SizeMoveAborted(), Times.Never);
    }

    /// <summary>
    /// Verifies that when the native <c>GetWindowRect</c> call fails at <c>WM_EXITSIZEMOVE</c> time, the
    /// service reports <c>SizeMoveAborted</c> instead of a bogus <c>SizeMoveExited</c> rectangle.
    /// </summary>
    [Fact]
    public void WmExitSizeMove_GetWindowRectFails_ReportsAbortedInsteadOfExited()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Right, 499, 400);
        var failedRect = default(NativeRectPx);
        _nativeApiMock.Setup(api => api.GetWindowRect(Hwnd, out failedRect)).Returns(false);
        var handled = false;

        // Act
        _hook!(Hwnd, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero, ref handled);

        // Assert — no trustworthy geometry to report; SizeMoveExited must not fire with a bogus rect.
        _clientMock.Verify(c => c.SizeMoveAborted(), Times.Once);
        _clientMock.Verify(c => c.SizeMoveExited(It.IsAny<NativeRectPx>()), Times.Never);
    }

    // ── WM_CAPTURECHANGED ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>WM_CAPTURECHANGED</c> with an lParam identifying a foreign window aborts the
    /// active sizing cycle.
    /// </summary>
    [Fact]
    public void WmCaptureChanged_ForeignWindowTakesCapture_AbortsTheActiveCycle()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Right, 499, 400);
        var handled = false;
        var foreignHwnd = new IntPtr(0x9999);

        // Act
        _hook!(Hwnd, WmCaptureChanged, IntPtr.Zero, foreignHwnd, ref handled);

        // Assert
        _clientMock.Verify(c => c.SizeMoveAborted(), Times.Once);
    }

    /// <summary>
    /// Verifies that a zero-lParam <c>WM_CAPTURECHANGED</c> — the system's ordinary release of capture
    /// right before <c>WM_EXITSIZEMOVE</c> — does not abort the cycle.
    /// </summary>
    [Fact]
    public void WmCaptureChanged_ZeroLParam_IsTheOrdinaryEndOfLoop_DoesNotAbort()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Right, 499, 400);
        var handled = false;

        // Act — the system sizing loop releases capture (lParam == 0) right before WM_EXITSIZEMOVE.
        _hook!(Hwnd, WmCaptureChanged, IntPtr.Zero, IntPtr.Zero, ref handled);

        // Assert
        _clientMock.Verify(c => c.SizeMoveAborted(), Times.Never);
    }

    /// <summary>
    /// Verifies that <c>WM_CAPTURECHANGED</c> with an lParam identifying the panel's own HWND does not
    /// abort the cycle, since the system may legitimately capture on the panel's own behalf.
    /// </summary>
    [Fact]
    public void WmCaptureChanged_OwnHwndTakesCapture_DoesNotAbort()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        BeginAcceptedCycle(NativeSizingEdge.Right, 499, 400);
        var handled = false;

        // Act — the system may capture on the panel's own behalf when the loop starts.
        _hook!(Hwnd, WmCaptureChanged, IntPtr.Zero, Hwnd, ref handled);

        // Assert
        _clientMock.Verify(c => c.SizeMoveAborted(), Times.Never);
    }

    // ── WM_DPICHANGED / WM_DPICHANGED_AFTERPARENT (D-6, observational only) ──────

    /// <summary>
    /// Verifies that <c>WM_DPICHANGED</c> reads the new DPI from the wParam high word and the suggested
    /// rectangle from the lParam payload, forwarding both to the client.
    /// </summary>
    [Fact]
    public void WmDpiChanged_ReadsScaleFromWParamHiword_AndRectFromLParam()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        var suggestedRect = new NativeRectPx(50, 60, 400, 500);
        var lParam = new IntPtr(0x4000);
        _nativeApiMock.Setup(api => api.ReadRect(lParam)).Returns(suggestedRect);
        const int newDpi = 144; // 150%
        var wParam = new IntPtr((newDpi << 16) | newDpi);
        var handled = false;

        // Act
        _hook!(Hwnd, WmDpiChanged, wParam, lParam, ref handled);

        // Assert
        _clientMock.Verify(c => c.DpiChanged(1.5, suggestedRect), Times.Once);
    }

    /// <summary>
    /// Verifies that <c>WM_DPICHANGED</c>'s wParam-hiword DPI is converted through
    /// <see cref="DesktopScaleModel.ScaleFromDpi"/> (release-hardening F2 #7) rather than a private
    /// <c>dpi / 96.0</c> literal, for a representative set of DPI values including non-"nice" percentages.
    /// </summary>
    /// <param name="dpi">The DPI value carried in wParam's high word.</param>
    /// <param name="expectedScale">The scale <see cref="DesktopScaleModel.ScaleFromDpi"/> produces for it.</param>
    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(192, 2.0)]
    public void WmDpiChanged_ScalesWParamHiwordDpi_ThroughDesktopScaleModelScaleFromDpi(int dpi, double expectedScale)
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        var suggestedRect = new NativeRectPx(50, 60, 400, 500);
        var lParam = new IntPtr(0x4000);
        _nativeApiMock.Setup(api => api.ReadRect(lParam)).Returns(suggestedRect);
        var wParam = new IntPtr((dpi << 16) | dpi);
        var handled = false;

        // Act
        _hook!(Hwnd, WmDpiChanged, wParam, lParam, ref handled);

        // Assert
        Assert.Equal(expectedScale, DesktopScaleModel.ScaleFromDpi((uint)dpi));
        _clientMock.Verify(c => c.DpiChanged(expectedScale, suggestedRect), Times.Once);
    }

    /// <summary>
    /// Verifies that a zero DPI in wParam's high word (never expected from real
    /// <c>WM_DPICHANGED</c>, but defensive) falls back to <see cref="INativeApi.GetDpiScaleForWindow"/>
    /// instead of <see cref="DesktopScaleModel.ScaleFromDpi"/>'s own <c>1.0</c> fallback — unchanged
    /// behavior across the F2 migration.
    /// </summary>
    [Fact]
    public void WmDpiChanged_ZeroDpiInWParam_FallsBackToGetDpiScaleForWindow()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        var suggestedRect = new NativeRectPx(50, 60, 400, 500);
        var lParam = new IntPtr(0x4000);
        _nativeApiMock.Setup(api => api.ReadRect(lParam)).Returns(suggestedRect);
        _nativeApiMock.Setup(api => api.GetDpiScaleForWindow(Hwnd)).Returns(1.75);
        var handled = false;

        // Act
        _hook!(Hwnd, WmDpiChanged, IntPtr.Zero, lParam, ref handled);

        // Assert
        _clientMock.Verify(c => c.DpiChanged(1.75, suggestedRect), Times.Once);
    }

    /// <summary>
    /// Verifies that <c>WM_DPICHANGED_AFTERPARENT</c>, which carries no RECT payload, asks the native API
    /// directly for both the current DPI scale and the window rectangle instead of reading the message.
    /// </summary>
    [Fact]
    public void WmDpiChangedAfterParent_HasNoPayload_AsksNativeApiForScaleAndRectInstead()
    {
        // Arrange
        var service = CreateService();
        service.Attach(Hwnd, _clientMock.Object);
        _nativeApiMock.Setup(api => api.GetDpiScaleForWindow(Hwnd)).Returns(1.25);
        var handled = false;

        // Act
        _hook!(Hwnd, WmDpiChangedAfterParent, IntPtr.Zero, IntPtr.Zero, ref handled);

        // Assert — no RECT payload on this message; both the scale and the rect are asked for directly.
        _clientMock.Verify(c => c.DpiChanged(1.25, new NativeRectPx(100, 200, 500, 600)), Times.Once);
        _nativeApiMock.Verify(api => api.ReadRect(It.IsAny<IntPtr>()), Times.Never);
    }

    private static IntPtr MakePoint(int x, int y) => new((y << 16) | (x & 0xFFFF));

    private NativePanelSizingService CreateService() =>
        new(_nativeApiMock.Object, (_, hook) =>
        {
            _installCount++;
            _hook = hook;
            return _hookRemovalMock.Object;
        });

    /// <summary>Seeds a sizing edge via <c>WM_NCHITTEST</c>, then runs an ACCEPTED <c>WM_ENTERSIZEMOVE</c>
    /// — the shared setup for every test above that needs an already-running, client-accepted cycle.</summary>
    private void BeginAcceptedCycle(NativeSizingEdge edge, int hitX, int hitY)
    {
        _clientMock
            .Setup(c => c.HitTest(It.IsAny<NativePointPx>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(edge);
        var handled = false;
        _hook!(Hwnd, WmNcHitTest, IntPtr.Zero, MakePoint(hitX, hitY), ref handled);
        _clientMock
            .Setup(c => c.SizeMoveEntered(It.IsAny<NativeSizingEdge>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(true);
        _hook!(Hwnd, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero, ref handled);
    }

    /// <summary>Same as <see cref="BeginAcceptedCycle"/>, but the client REJECTS <c>WM_ENTERSIZEMOVE</c>
    /// (returns <see langword="false"/>) — the cycle stays running at the service level but is never
    /// forwarded to the client again until the next <c>WM_ENTERSIZEMOVE</c>.</summary>
    private void BeginRejectedCycle(NativeSizingEdge edge, int hitX, int hitY)
    {
        _clientMock
            .Setup(c => c.HitTest(It.IsAny<NativePointPx>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(edge);
        var handled = false;
        _hook!(Hwnd, WmNcHitTest, IntPtr.Zero, MakePoint(hitX, hitY), ref handled);
        _clientMock
            .Setup(c => c.SizeMoveEntered(It.IsAny<NativeSizingEdge>(), It.IsAny<NativeRectPx>(), It.IsAny<double>()))
            .Returns(false);
        _hook!(Hwnd, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero, ref handled);
    }
}
