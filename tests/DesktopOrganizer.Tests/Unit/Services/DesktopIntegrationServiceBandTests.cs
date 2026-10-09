using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for the desktop-band Z-order keeping of top-level fence panels (ADR-audit WP-D, ADR-034
/// amendment 2026-10-09): <see cref="DesktopIntegrationService.KeepInDesktopBand"/> and
/// <see cref="DesktopIntegrationService.ConstrainWindowPosChangingToDesktopBand"/>, against a mocked
/// <see cref="INativeApi"/> that simulates a real top-to-bottom sibling list.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DesktopIntegrationServiceBandTests
{
    private const int GwlExStyle = -20;
    private const int GwHwndPrev = 3;

    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint DesktopStyles = WsExToolWindow | WsExNoActivate;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoOwnerZOrder = 0x0200;

    /// <summary>The flag mask <c>KeepInDesktopBand</c> is contracted to pass.</summary>
    private const uint KeepFlags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder;

    private const uint ForeignProcessId = 4242;

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private static readonly IntPtr Panel = new (101);
    private static readonly IntPtr OtherPanel = new (102);
    private static readonly IntPtr Sidebar = new (103);
    private static readonly IntPtr Progman = new (200);
    private static readonly IntPtr WallpaperWorkerW = new (201);
    private static readonly IntPtr App = new (300);
    private static readonly IntPtr SecondApp = new (301);
    private static readonly IntPtr SettingsWindow = new (302);
    private static readonly IntPtr Taskbar = new (400);
    private static readonly IntPtr WorkerWRoot = new (500);
    private static readonly IntPtr WindowPos = new (0x1000);

    private readonly Mock<INativeApi> _nativeApiMock = new ();
    private readonly Mock<IAppLog> _appLogMock = new ();
    private readonly DesktopIntegrationService _service;

    /// <summary>The simulated sibling list, top to bottom (index 0 = top of the Z-order).</summary>
    private readonly List<IntPtr> _zOrder = [];

    /// <summary>Initializes a new instance of the <see cref="DesktopIntegrationServiceBandTests"/> class.</summary>
    public DesktopIntegrationServiceBandTests()
    {
        _service = new DesktopIntegrationService(_nativeApiMock.Object, _appLogMock.Object);

        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(Progman);
        _nativeApiMock
            .Setup(api => api.GetWindow(It.IsAny<IntPtr>(), GwHwndPrev))
            .Returns<IntPtr, int>((hwnd, _) =>
            {
                var index = _zOrder.IndexOf(hwnd);
                return index > 0 ? _zOrder[index - 1] : IntPtr.Zero;
            });
        _nativeApiMock
            .Setup(api => api.SetWindowPos(
                It.IsAny<IntPtr>(),
                It.IsAny<IntPtr>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<uint>()))
            .Callback<IntPtr, IntPtr, int, int, int, int, uint>((hwnd, insertAfter, _, _, _, _, _) => Move(hwnd, insertAfter))
            .Returns(true);

        // Panels are top-level (mode T) unless a test says otherwise.
        TopLevel(Panel);
        TopLevel(OtherPanel);

        Describe(Panel, OwnProcessId, DesktopStyles);
        Describe(OtherPanel, OwnProcessId, DesktopStyles);
        Describe(Sidebar, OwnProcessId, DesktopStyles);
        Describe(Progman, ForeignProcessId, 0, "Progman");
        Describe(WallpaperWorkerW, ForeignProcessId, 0, "WorkerW");
        Describe(App, ForeignProcessId, 0);
        Describe(SecondApp, ForeignProcessId, 0);
        Describe(SettingsWindow, OwnProcessId, 0);
        Describe(Taskbar, ForeignProcessId, WsExTopmost | WsExToolWindow, "Shell_TrayWnd");
    }

    /// <summary>A freshly shown panel on top of the apps is moved directly below the first app above the band.</summary>
    [Fact]
    public void KeepInDesktopBand_PanelAboveApps_InsertsItAfterFirstForeignWindowAboveBand()
    {
        Arrange(Taskbar, Panel, App, SecondApp, Progman);

        _service.KeepInDesktopBand(Panel);

        VerifySetWindowPos(Panel, SecondApp, Times.Once());
        Assert.Equal(new[] { Taskbar, App, SecondApp, Panel, Progman }, _zOrder);
    }

    /// <summary>A panel directly above <c>Progman</c> is already in the band: zero writes.</summary>
    [Fact]
    public void KeepInDesktopBand_PanelDirectlyAboveProgman_DoesNothing()
    {
        Arrange(Taskbar, App, Panel, Progman);

        _service.KeepInDesktopBand(Panel);

        VerifyNoSetWindowPos();
    }

    /// <summary>
    /// A panel above other panels, the sidebar and Explorer's WorkerW is still inside the band — the walk passes
    /// over our own desktop windows and WorkerW, so N panels never compete for one slot.
    /// </summary>
    [Fact]
    public void KeepInDesktopBand_PanelAboveOwnDesktopWindowsAndWorkerW_DoesNothing()
    {
        Arrange(Taskbar, App, Panel, OtherPanel, Sidebar, WallpaperWorkerW, Progman);

        _service.KeepInDesktopBand(Panel);
        _service.KeepInDesktopBand(OtherPanel);

        VerifyNoSetWindowPos();
    }

    /// <summary>Calling twice is idempotent: the first call places the panel, the second writes nothing.</summary>
    [Fact]
    public void KeepInDesktopBand_CalledTwice_WritesOnlyOnce()
    {
        Arrange(Taskbar, Panel, App, OtherPanel, Progman);

        _service.KeepInDesktopBand(Panel);
        _service.KeepInDesktopBand(Panel);

        VerifySetWindowPos(Panel, App, Times.Once());
        Assert.Equal(new[] { Taskbar, App, Panel, OtherPanel, Progman }, _zOrder);
    }

    /// <summary>
    /// No churn between N panels and the sidebar: alternating re-asserts converge after one placement each
    /// and then never write again (the failure mode a shared "one slot above Progman" would have).
    /// </summary>
    [Fact]
    public void KeepInDesktopBand_AlternatingPanels_ConvergesWithoutChurn()
    {
        Arrange(Taskbar, Panel, OtherPanel, App, Sidebar, Progman);

        for (var i = 0; i < 10; i++)
        {
            _service.KeepInDesktopBand(Panel);
            _service.KeepInDesktopBand(OtherPanel);
        }

        _nativeApiMock.Verify(
            api => api.SetWindowPos(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), 0, 0, 0, 0, KeepFlags),
            Times.Exactly(2));
        Assert.Equal(new[] { Taskbar, App, OtherPanel, Panel, Sidebar, Progman }, _zOrder);
    }

    /// <summary>
    /// Our own non-desktop windows (Settings, dialogs) end the band: a panel never gets lifted above the
    /// Settings window.
    /// </summary>
    [Fact]
    public void KeepInDesktopBand_OwnWindowWithoutNoActivate_EndsTheBand()
    {
        Arrange(Taskbar, Panel, App, SettingsWindow, Progman);

        _service.KeepInDesktopBand(Panel);

        VerifySetWindowPos(Panel, SettingsWindow, Times.Once());
    }

    /// <summary>
    /// A foreign tool window with the desktop styles is not ours: it ends the band like any application window.
    /// </summary>
    [Fact]
    public void KeepInDesktopBand_ForeignDesktopStyledWindow_EndsTheBand()
    {
        var widget = new IntPtr(303);
        Describe(widget, ForeignProcessId, DesktopStyles);
        Arrange(Taskbar, Panel, App, widget, Progman);

        _service.KeepInDesktopBand(Panel);

        VerifySetWindowPos(Panel, widget, Times.Once());
    }

    /// <summary>
    /// When the band reaches the always-on-top run (no application window above it, e.g. everything
    /// minimised), the panel goes to <c>HWND_TOP</c> — the top of the non-topmost run, never above a topmost window.
    /// </summary>
    [Fact]
    public void KeepInDesktopBand_BandReachesTopmostRun_UsesHwndTop()
    {
        Arrange(Taskbar, OtherPanel, Progman, Panel);

        _service.KeepInDesktopBand(Panel);

        VerifySetWindowPos(Panel, HwndTop, Times.Once());
    }

    /// <summary>When the band reaches the very top of the sibling list, the panel goes to <c>HWND_TOP</c>.</summary>
    [Fact]
    public void KeepInDesktopBand_BandReachesTopOfList_UsesHwndTop()
    {
        Arrange(Sidebar, Progman, Panel);

        _service.KeepInDesktopBand(Panel);

        VerifySetWindowPos(Panel, HwndTop, Times.Once());
    }

    /// <summary>Mode E: a panel embedded into WorkerW (root is Explorer's window) is never touched.</summary>
    [Fact]
    public void KeepInDesktopBand_EmbeddedPanel_DoesNothingAtAll()
    {
        _nativeApiMock.Setup(api => api.GetRootWindow(Panel)).Returns(WorkerWRoot);
        Arrange(Taskbar, App, Progman);

        _service.KeepInDesktopBand(Panel);

        VerifyNoSetWindowPos();
        _nativeApiMock.Verify(api => api.FindWindow(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>A failed root query counts as "not top-level" — nothing is done.</summary>
    [Fact]
    public void KeepInDesktopBand_RootQueryFails_DoesNothing()
    {
        _nativeApiMock.Setup(api => api.GetRootWindow(Panel)).Returns(IntPtr.Zero);
        Arrange(Taskbar, Panel, App, Progman);

        _service.KeepInDesktopBand(Panel);

        VerifyNoSetWindowPos();
    }

    /// <summary>A zero handle is a no-op.</summary>
    [Fact]
    public void KeepInDesktopBand_ZeroHandle_DoesNothing()
    {
        _service.KeepInDesktopBand(IntPtr.Zero);

        VerifyNoSetWindowPos();
        _nativeApiMock.Verify(api => api.GetRootWindow(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>No <c>Progman</c> (Explorer down): no decision, no write — unlike the sidebar's HWND_BOTTOM fallback.</summary>
    [Fact]
    public void KeepInDesktopBand_ProgmanMissing_DoesNothing()
    {
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);
        Arrange(Taskbar, Panel, App);

        _service.KeepInDesktopBand(Panel);

        VerifyNoSetWindowPos();
    }

    /// <summary>A sibling list longer than the walk cap (corrupted/racing) answers "unknown": no write.</summary>
    [Fact]
    public void KeepInDesktopBand_WalkCapExceeded_DoesNothing()
    {
        var list = new List<IntPtr> { Taskbar, Panel, App };
        for (var i = 0; i < DesktopIntegrationService.MaxPanelBandWalk + 1; i++)
        {
            var own = new IntPtr(10_000 + i);
            Describe(own, OwnProcessId, DesktopStyles);
            list.Add(own);
        }

        list.Add(Progman);
        Arrange(list.ToArray());

        _service.KeepInDesktopBand(Panel);

        VerifyNoSetWindowPos();
    }

    /// <summary>Native failures are swallowed — never thrown at the UI thread.</summary>
    [Fact]
    public void KeepInDesktopBand_NativeThrows_IsSwallowed()
    {
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Throws(new InvalidOperationException("boom"));

        var exception = Record.Exception(() => _service.KeepInDesktopBand(Panel));

        Assert.Null(exception);
    }

    /// <summary>The hot path (move/resize frames, <c>SWP_NOZORDER</c> without show) reads the structure and stops.</summary>
    [Fact]
    public void Constrain_NoZOrderWithoutShow_OnlyReadsTheStructure()
    {
        Request(HwndTop, SwpNoZOrder | SwpNoActivate);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        _nativeApiMock.Verify(api => api.GetRootWindow(It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.FindWindow(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        VerifyNoWrite();
    }

    /// <summary>
    /// ADR-022 phase 0-B × ADR-049: the frames of the native sizing loop (<c>DefWindowProc</c>'s live resize —
    /// <c>SWP_NOZORDER</c> plus any mix of <c>SWP_NOACTIVATE</c>/<c>SWP_NOOWNERZORDER</c>/<c>SWP_NOMOVE</c>, never
    /// a show) take the early-return hot path even for a top-level panel sitting above the apps: no root query,
    /// no Progman walk, no write — the band filter cannot interfere with a resize gesture.
    /// </summary>
    /// <param name="flags">The <c>WINDOWPOS.flags</c> of one sizing-loop frame.</param>
    [Theory]
    [InlineData(SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder)]
    [InlineData(SwpNoZOrder | SwpNoMove | SwpNoActivate)]
    [InlineData(SwpNoZOrder | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder)]
    [InlineData(SwpNoZOrder)]
    public void Constrain_SizingLoopFrame_TakesTheHotPath(uint flags)
    {
        Arrange(Taskbar, Panel, App, Progman);
        Request(HwndTop, flags);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        _nativeApiMock.Verify(api => api.GetRootWindow(It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.FindWindow(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        VerifyNoWrite();
    }

    /// <summary>A hide request is never altered.</summary>
    [Fact]
    public void Constrain_HideRequest_IsIgnored()
    {
        Arrange(Taskbar, Panel, App, Progman);
        Request(HwndTop, SwpHideWindow);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoWrite();
    }

    /// <summary>Mode E: a WorkerW child's Z-order requests are never altered.</summary>
    [Fact]
    public void Constrain_EmbeddedPanel_IsIgnored()
    {
        _nativeApiMock.Setup(api => api.GetRootWindow(Panel)).Returns(WorkerWRoot);
        Arrange(Taskbar, App, Progman);
        Request(HwndTop, SwpNoMove | SwpNoSize);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoWrite();
        _nativeApiMock.Verify(api => api.FindWindow(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>
    /// A raise (e.g. activation's <c>HWND_TOP</c>) of a panel that is not in the band is redirected to the top of
    /// the band, with <c>SWP_NOOWNERZORDER</c> added and all other flags preserved.
    /// </summary>
    [Fact]
    public void Constrain_RaiseOutsideBand_RedirectsIntoBand()
    {
        Arrange(Taskbar, App, Progman, Panel);
        Request(HwndTop, SwpNoMove | SwpNoSize);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        _nativeApiMock.Verify(
            api => api.WriteWindowPosZOrder(WindowPos, App, SwpNoMove | SwpNoSize | SwpNoOwnerZOrder),
            Times.Once);
    }

    /// <summary>A raise of a panel already in the band is vetoed: same insert-after, <c>SWP_NOZORDER</c> added.</summary>
    [Fact]
    public void Constrain_RaiseInsideBand_VetoesZOrderChange()
    {
        Arrange(Taskbar, App, Panel, Sidebar, Progman);
        Request(HwndTop, SwpNoMove | SwpNoSize | SwpNoActivate);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        _nativeApiMock.Verify(
            api => api.WriteWindowPosZOrder(WindowPos, HwndTop, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoZOrder),
            Times.Once);
    }

    /// <summary>
    /// A show request that would leave the panel on top (fresh windows are linked at the top) gets its
    /// <c>SWP_NOZORDER</c> cleared and is pointed into the band, so the panel appears in its final slot.
    /// </summary>
    [Fact]
    public void Constrain_ShowOutsideBand_ClearsNoZOrderAndRedirects()
    {
        Arrange(Taskbar, Panel, App, Progman);
        Request(HwndTop, SwpShowWindow | SwpNoZOrder | SwpNoMove | SwpNoSize | SwpNoActivate);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        _nativeApiMock.Verify(
            api => api.WriteWindowPosZOrder(
                WindowPos,
                App,
                SwpShowWindow | SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder),
            Times.Once);
    }

    /// <summary>A show request of a panel already in the band (<c>SWP_NOZORDER</c>) needs no change.</summary>
    [Fact]
    public void Constrain_ShowInsideBand_WithNoZOrder_WritesNothing()
    {
        Arrange(Taskbar, App, Panel, Progman);
        Request(HwndTop, SwpShowWindow | SwpNoZOrder | SwpNoMove | SwpNoSize);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoWrite();
    }

    /// <summary>
    /// A request that already targets the band slot (KeepInDesktopBand's own <c>SetWindowPos</c> re-entering the
    /// filter) is left exactly as it is.
    /// </summary>
    [Fact]
    public void Constrain_RequestAlreadyTargetsBandSlot_WritesNothing()
    {
        Arrange(Taskbar, Panel, App, Progman);
        Request(App, KeepFlags);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoWrite();
    }

    /// <summary>No <c>Progman</c>: the system's proposal is left alone.</summary>
    [Fact]
    public void Constrain_ProgmanMissing_WritesNothing()
    {
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);
        Request(HwndTop, SwpNoMove | SwpNoSize);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoWrite();
    }

    /// <summary>An unreadable structure, a zero pointer or a zero handle are all no-ops.</summary>
    [Fact]
    public void Constrain_UnreadableOrZeroInputs_DoNothing()
    {
        Arrange(Taskbar, Panel, App, Progman);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos); // TryRead not set up → false
        _service.ConstrainWindowPosChangingToDesktopBand(Panel, IntPtr.Zero);
        _service.ConstrainWindowPosChangingToDesktopBand(IntPtr.Zero, WindowPos);

        VerifyNoWrite();
        _nativeApiMock.Verify(api => api.GetRootWindow(It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>Exceptions never leave the window-procedure filter.</summary>
    [Fact]
    public void Constrain_NativeThrows_IsSwallowed()
    {
        Request(HwndTop, SwpNoMove | SwpNoSize);
        _nativeApiMock.Setup(api => api.GetRootWindow(Panel)).Throws(new InvalidOperationException("boom"));

        var exception = Record.Exception(() => _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos));

        Assert.Null(exception);
    }

    // ── Once-per-session diagnostics (ADR-audit review cycle 1, G2) ──────────────────────────────────

    /// <summary>A missing <c>Progman</c> met by <c>KeepInDesktopBand</c> is logged once per session, not per call.</summary>
    [Fact]
    public void KeepInDesktopBand_ProgmanMissing_WarnsOncePerSession()
    {
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);

        _service.KeepInDesktopBand(Panel);
        _service.KeepInDesktopBand(Panel);
        _service.KeepInDesktopBand(OtherPanel);

        VerifyWarn("DesktopBandProgmanMissing", Times.Once());
    }

    /// <summary>Exceeding <see cref="DesktopIntegrationService.MaxPanelBandWalk"/> is logged once per session.</summary>
    [Fact]
    public void KeepInDesktopBand_WalkCapExceeded_WarnsOncePerSession()
    {
        ArrangeLongerThanWalkCap();

        _service.KeepInDesktopBand(Panel);
        _service.KeepInDesktopBand(Panel);

        _appLogMock.Verify(
            l => l.Warn(
                nameof(DesktopIntegrationService),
                "DesktopBandWalkCapExceeded",
                It.Is<KeyValuePair<string, object?>[]>(d => d.Any(kv => kv.Key == "maxWalk" && Equals(kv.Value, DesktopIntegrationService.MaxPanelBandWalk)))),
            Times.Once);
    }

    /// <summary>A swallowed failure of <c>KeepInDesktopBand</c> is logged once per session, with the exception type only.</summary>
    [Fact]
    public void KeepInDesktopBand_NativeThrows_WarnsOncePerSession()
    {
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Throws(new InvalidOperationException("boom"));

        _service.KeepInDesktopBand(Panel);
        _service.KeepInDesktopBand(Panel);

        _appLogMock.Verify(
            l => l.Warn(
                nameof(DesktopIntegrationService),
                "DesktopBandKeepFailed",
                It.Is<KeyValuePair<string, object?>[]>(d => d.Any(kv => kv.Key == "errorType" && Equals(kv.Value, nameof(InvalidOperationException))))),
            Times.Once);
    }

    /// <summary>
    /// Inside the window procedure nothing is logged: a missing <c>Progman</c> met by the filter is only recorded,
    /// and written (once) by the next <c>KeepInDesktopBand</c>.
    /// </summary>
    [Fact]
    public void Constrain_ProgmanMissing_DoesNotLogInsideTheFilter_KeepInDesktopBandWritesItOnce()
    {
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);
        Request(HwndTop, SwpNoMove | SwpNoSize);

        for (var i = 0; i < 10; i++) _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoLog();

        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(Progman);
        Arrange(Taskbar, App, Panel, Progman);
        _service.KeepInDesktopBand(Panel);
        _service.KeepInDesktopBand(Panel);

        VerifyWarn("DesktopBandProgmanMissing", Times.Once());
    }

    /// <summary>The filter's walk-cap anomaly is deferred the same way.</summary>
    [Fact]
    public void Constrain_WalkCapExceeded_IsDeferredToKeepInDesktopBand()
    {
        ArrangeLongerThanWalkCap();
        Request(HwndTop, SwpNoMove | SwpNoSize);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoLog();

        _service.KeepInDesktopBand(IntPtr.Zero); // a zero handle is a pure no-op — flushes nothing
        VerifyNoLog();

        _service.KeepInDesktopBand(Panel);

        VerifyWarn("DesktopBandWalkCapExceeded", Times.Once());
    }

    /// <summary>
    /// An exception swallowed inside the window procedure is not logged there; the next <c>KeepInDesktopBand</c>
    /// writes it once, carrying the exception type.
    /// </summary>
    [Fact]
    public void Constrain_NativeThrows_IsLoggedOnceByKeepInDesktopBand()
    {
        Request(HwndTop, SwpNoMove | SwpNoSize);
        _nativeApiMock.Setup(api => api.GetRootWindow(Panel)).Throws(new InvalidOperationException("boom"));

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);
        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);

        VerifyNoLog();

        _service.KeepInDesktopBand(OtherPanel);
        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);
        _service.KeepInDesktopBand(OtherPanel);

        _appLogMock.Verify(
            l => l.Warn(
                nameof(DesktopIntegrationService),
                "DesktopBandFilterFailed",
                It.Is<KeyValuePair<string, object?>[]>(d => d.Any(kv => kv.Key == "errorType" && Equals(kv.Value, nameof(InvalidOperationException))))),
            Times.Once);
    }

    /// <summary>The steady state (nothing anomalous) writes nothing at all.</summary>
    [Fact]
    public void KeepInDesktopBand_NormalOperation_WritesNoLog()
    {
        Arrange(Taskbar, Panel, App, Progman);
        Request(HwndTop, SwpNoMove | SwpNoSize);

        _service.ConstrainWindowPosChangingToDesktopBand(Panel, WindowPos);
        _service.KeepInDesktopBand(Panel);

        VerifyNoLog();
    }

    private void ArrangeLongerThanWalkCap()
    {
        var list = new List<IntPtr> { Taskbar, Panel, App };
        for (var i = 0; i < DesktopIntegrationService.MaxPanelBandWalk + 1; i++)
        {
            var own = new IntPtr(10_000 + i);
            Describe(own, OwnProcessId, DesktopStyles);
            list.Add(own);
        }

        list.Add(Progman);
        Arrange(list.ToArray());
    }

    private void VerifyWarn(string evt, Times times)
        => _appLogMock.Verify(l => l.Warn(nameof(DesktopIntegrationService), evt, It.IsAny<KeyValuePair<string, object?>[]>()), times);

    private void VerifyNoLog()
    {
        _appLogMock.Verify(l => l.Warn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Never);
        _appLogMock.Verify(l => l.Error(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()), Times.Never);
    }

    private void TopLevel(IntPtr hwnd) => _nativeApiMock.Setup(api => api.GetRootWindow(hwnd)).Returns(hwnd);

    private void Describe(IntPtr hwnd, uint processId, uint exStyle, string className = "HwndWrapper")
    {
        _nativeApiMock.Setup(api => api.GetWindowProcessId(hwnd)).Returns(processId);
        _nativeApiMock.Setup(api => api.GetWindowLongPtr(hwnd, GwlExStyle)).Returns(new IntPtr(exStyle));
        _nativeApiMock.Setup(api => api.GetWindowClassName(hwnd)).Returns(className);
    }

    /// <summary>Sets the simulated sibling list, top to bottom.</summary>
    private void Arrange(params IntPtr[] topToBottom)
    {
        _zOrder.Clear();
        _zOrder.AddRange(topToBottom);
    }

    private void Request(IntPtr insertAfter, uint flags)
    {
        var requestedInsertAfter = insertAfter;
        var requestedFlags = flags;
        _nativeApiMock
            .Setup(api => api.TryReadWindowPosZOrder(WindowPos, out requestedInsertAfter, out requestedFlags))
            .Returns(true);
    }

    /// <summary>Simulates <c>SetWindowPos</c>'s Z-order effect on the sibling list.</summary>
    private void Move(IntPtr hwnd, IntPtr insertAfter)
    {
        _zOrder.Remove(hwnd);
        if (insertAfter == HwndTop)
        {
            // Top of the non-topmost run: below the (single) simulated topmost window, if present.
            _zOrder.Insert(_zOrder.Contains(Taskbar) ? _zOrder.IndexOf(Taskbar) + 1 : 0, hwnd);
            return;
        }

        _zOrder.Insert(_zOrder.IndexOf(insertAfter) + 1, hwnd);
    }

    private void VerifySetWindowPos(IntPtr hwnd, IntPtr insertAfter, Times times)
        => _nativeApiMock.Verify(api => api.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, KeepFlags), times);

    private void VerifyNoSetWindowPos()
        => _nativeApiMock.Verify(
            api => api.SetWindowPos(
                It.IsAny<IntPtr>(),
                It.IsAny<IntPtr>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<uint>()),
            Times.Never);

    private void VerifyNoWrite()
        => _nativeApiMock.Verify(
            api => api.WriteWindowPosZOrder(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<uint>()),
            Times.Never);
}
