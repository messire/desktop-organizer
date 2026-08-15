using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Tools;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="DesktopIntegrationService"/>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DesktopIntegrationServiceTests
{
    private const int GwlExStyle = -20;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    /// <summary>The full flag mask <c>SendToBackOfZOrder</c> is contracted to pass.</summary>
    private const uint ZOrderFlags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder;

    /// <summary><c>GW_HWNDPREV</c> — the sibling immediately above the given window.</summary>
    private const int GwHwndPrev = 3;

    private static readonly IntPtr HwndBottom = new (1);

    private static readonly IntPtr Hwnd = new (111);
    private static readonly IntPtr ProgmanHwnd = new (222);
    private static readonly IntPtr ShellHwnd = new (333);
    private static readonly IntPtr DefViewHwnd = new (444);
    private static readonly IntPtr WorkerWHwnd = new (555);
    private static readonly IntPtr AboveProgmanHwnd = new (666);
    private static readonly IntPtr WallpaperWorkerWHwnd = new (777);
    private static readonly IntPtr SecondWorkerWHwnd = new (888);

    private readonly Mock<INativeApi> _nativeApiMock = new ();
    private readonly DesktopIntegrationService _service;

    public DesktopIntegrationServiceTests()
    {
        _service = new DesktopIntegrationService(_nativeApiMock.Object);
        _nativeApiMock.Setup(api => api.GetWindowLongPtr(Hwnd, GwlExStyle)).Returns(IntPtr.Zero);

        // Default the Z-order write to success; the HWND_BOTTOM retry path has its own tests below.
        _nativeApiMock
            .Setup(api => api.SetWindowPos(
                It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(true);
    }

    /// <summary>
    /// Ensures the Alt+Tab/taskbar-hiding styles are applied even when the WorkerW handle cannot be
    /// resolved (e.g. Explorer hasn't spawned it yet) — that failure must not leave the window as an
    /// ordinary top-level app window, since desktop-layer parenting and Alt+Tab visibility are
    /// independent guarantees per <see cref="IDesktopIntegrationService.EmbedInDesktop"/>.
    /// </summary>
    [Fact]
    public void EmbedInDesktop_AppliesToolWindowStyles_WhenWorkerWNotFound()
    {
        // Arrange: Progman itself can't be found, so GetOrCreateWorkerW short-circuits to Zero.
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);

        // Act
        _service.EmbedInDesktop(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowLongPtr(Hwnd, GwlExStyle, new IntPtr(WsExToolWindow | WsExNoActivate)),
            Times.Once);
        _nativeApiMock.Verify(api => api.SetParent(It.IsAny<IntPtr>(), It.IsAny<IntPtr>()), Times.Never);
    }

    /// <summary>
    /// Ensures the window is both re-parented into the WorkerW layer and given the Alt+Tab/taskbar
    /// -hiding styles when WorkerW resolves successfully.
    /// </summary>
    [Fact]
    public void EmbedInDesktop_AppliesStylesAndReparents_WhenWorkerWFound()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock
            .Setup(api => api.EnumWindows(It.IsAny<EnumWindowsCallback>(), IntPtr.Zero))
            .Callback<EnumWindowsCallback, IntPtr>((callback, lParam) => callback(ShellHwnd, lParam))
            .Returns(true);
        _nativeApiMock
            .Setup(api => api.FindWindowEx(ShellHwnd, IntPtr.Zero, "SHELLDLL_DefView", null))
            .Returns(DefViewHwnd);
        _nativeApiMock
            .Setup(api => api.FindWindowEx(IntPtr.Zero, ShellHwnd, "WorkerW", null))
            .Returns(WorkerWHwnd);

        // Act
        _service.EmbedInDesktop(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowLongPtr(Hwnd, GwlExStyle, new IntPtr(WsExToolWindow | WsExNoActivate)),
            Times.Once);
        _nativeApiMock.Verify(api => api.SetParent(Hwnd, WorkerWHwnd), Times.Once);
    }

    /// <summary>
    /// Ensures the existing extended style bits are preserved (OR'd, not overwritten) when applying
    /// the Alt+Tab/taskbar-hiding styles.
    /// </summary>
    [Fact]
    public void EmbedInDesktop_PreservesExistingExtendedStyleBits()
    {
        // Arrange
        const uint existingBit = 0x00000001; // arbitrary pre-existing WS_EX_* bit
        _nativeApiMock.Setup(api => api.GetWindowLongPtr(Hwnd, GwlExStyle)).Returns(new IntPtr(existingBit));
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);

        // Act
        _service.EmbedInDesktop(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowLongPtr(Hwnd, GwlExStyle, new IntPtr(existingBit | WsExToolWindow | WsExNoActivate)),
            Times.Once);
    }

    // ── ApplyDesktopWindowStyles (ADR-019): styles only, no WorkerW parenting ──

    /// <summary>
    /// Ensures <see cref="IDesktopIntegrationService.ApplyDesktopWindowStyles"/> sets
    /// <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c> while preserving whatever other extended style
    /// bits were already present (read-modify-write, per its XML-doc).
    /// </summary>
    [Fact]
    public void ApplyDesktopWindowStyles_SetsToolWindowAndNoActivate_PreservingOtherBits()
    {
        // Arrange
        const uint existingBit = 0x00000001; // arbitrary pre-existing WS_EX_* bit
        _nativeApiMock.Setup(api => api.GetWindowLongPtr(Hwnd, GwlExStyle)).Returns(new IntPtr(existingBit));

        // Act
        _service.ApplyDesktopWindowStyles(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowLongPtr(Hwnd, GwlExStyle, new IntPtr(existingBit | WsExToolWindow | WsExNoActivate)),
            Times.Once);
    }

    /// <summary>
    /// Regression guard for ADR-019 / V-R11: <see cref="IDesktopIntegrationService.ApplyDesktopWindowStyles"/>
    /// must be a strict subset of what <see cref="IDesktopIntegrationService.EmbedInDesktop"/> does —
    /// the window-style part only, never the WorkerW re-parenting part. If this ever calls
    /// <c>SetParent</c>, <c>SendMessage</c>, <c>FindWindow</c> or <c>EnumWindows</c>, the system icons
    /// panel (which calls only this member, never <c>EmbedInDesktop</c> — see
    /// <c>SystemIconsPanelManager.OpenPanel</c>) would silently start touching Explorer/WorkerW again,
    /// resurrecting the exact bug ADR-019 fixed.
    /// </summary>
    [Fact]
    public void ApplyDesktopWindowStyles_NeverTouchesExplorerOrWorkerW()
    {
        // Act
        _service.ApplyDesktopWindowStyles(Hwnd);

        // Assert — the only native call allowed is the style read/write pair.
        _nativeApiMock.Verify(api => api.SetParent(It.IsAny<IntPtr>(), It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.SendMessage(It.IsAny<IntPtr>(), It.IsAny<uint>(), It.IsAny<IntPtr>(), It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.FindWindow(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _nativeApiMock.Verify(api => api.EnumWindows(It.IsAny<EnumWindowsCallback>(), It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.FindWindowEx(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // ── SendToBackOfZOrder: keep the sidebar behind ordinary windows ──────────

    /// <summary>
    /// Ensures the exact <c>SetWindowPos</c> contract: the window is inserted after
    /// <c>Progman</c>'s <c>GW_HWNDPREV</c>, i.e. it lands in the single slot between that window and
    /// <c>Progman</c> — deterministically above the wallpaper and the desktop icons, below everything
    /// else. ADR-001 rejected a blind <c>HWND_BOTTOM</c> for exactly this reason: it means "below
    /// every other window", which does not exclude "below <c>Progman</c>", i.e. invisible.
    /// <para>
    /// Flags: <c>SWP_NOMOVE | SWP_NOSIZE</c> keep the panel's geometry (which
    /// <c>SystemIconsPanelManager.PositionWindow</c> owns), <c>SWP_NOACTIVATE</c> keeps focus with
    /// whatever the user is working in — without it, a timer that fires several times a second would
    /// repeatedly steal activation — and <c>SWP_NOOWNERZORDER</c> leaves the hidden owner window WPF
    /// creates for <c>ShowInTaskbar="False"</c> alone; without it the owner is dragged into the
    /// bottom band too and the two windows swap slots on every tick (measured, Win11 10.0.26200).
    /// </para>
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_InsertsWindowDirectlyAboveProgman_WithoutMovingResizingActivatingOrReorderingOwner()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(AboveProgmanHwnd);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, AboveProgmanHwnd, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
    }

    /// <summary>
    /// Steady state: the panel already holds the slot directly above <c>Progman</c>, so
    /// <c>GW_HWNDPREV</c> answers with the panel's own handle. Asking Windows to insert a window
    /// after itself is meaningless, and skipping the call is what keeps the 300 ms timer nearly free —
    /// this is the tick that fires the overwhelming majority of the time.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenAlreadyDirectlyAboveProgman_DoesNotCallSetWindowPos()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(Hwnd);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(
                It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()),
            Times.Never);
    }

    /// <summary>
    /// No <c>Progman</c> (Explorer restarting, or a session with no shell at all): fall back to
    /// <c>HWND_BOTTOM</c>. Deliberately <b>not</b> <c>HWND_TOP</c> — the worst case of the former is a
    /// panel hidden behind the wallpaper, the worst case of the latter is a panel parked on top of
    /// every window the user is working in.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenProgmanNotFound_FallsBackToHwndBottom()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, HwndBottom, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
        _nativeApiMock.Verify(api => api.GetWindow(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// <c>Progman</c> found but reported as the top-most sibling (no <c>GW_HWNDPREV</c>) — there is no
    /// anchor to insert after, so the same conservative <c>HWND_BOTTOM</c> fallback applies.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenProgmanHasNoWindowAboveIt_FallsBackToHwndBottom()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(IntPtr.Zero);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, HwndBottom, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
    }

    /// <summary>
    /// Windows 10 shape: Explorer keeps a real <c>WorkerW</c> that paints the wallpaper and sits
    /// <b>above</b> <c>Progman</c> in the Z-order. A single <c>GW_HWNDPREV</c> step would answer with
    /// that wallpaper window and bury the panel underneath it — invisible, and permanently so, since
    /// the next tick would find the panel already in that slot and short-circuit. The anchor must
    /// therefore skip the whole run of <c>WorkerW</c> siblings and target the top of the desktop band.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenWorkerWSitsAboveProgman_AnchorsAboveThatWorkerW()
    {
        // Arrange: [AboveProgman, WorkerW(wallpaper), Progman, …] top-to-bottom.
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(WallpaperWorkerWHwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(WallpaperWorkerWHwnd)).Returns("WorkerW");
        _nativeApiMock.Setup(api => api.GetWindow(WallpaperWorkerWHwnd, GwHwndPrev)).Returns(AboveProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(AboveProgmanHwnd)).Returns("Shell_TrayWnd");

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert — anchored above the wallpaper, never after it.
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, AboveProgmanHwnd, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, WallpaperWorkerWHwnd, 0, 0, 0, 0, It.IsAny<uint>()),
            Times.Never);
    }

    /// <summary>
    /// Same as above with two stacked <c>WorkerW</c> windows (Explorer transiently keeps a second one
    /// around, e.g. right after the <c>0x052C</c> trick or a wallpaper transition): the walk must skip
    /// the entire run, not just the first sibling.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WithTwoStackedWorkerWs_AnchorsAboveTheTopmostOne()
    {
        // Arrange: [AboveProgman, WorkerW#2, WorkerW#1, Progman, …] top-to-bottom.
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(WallpaperWorkerWHwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(WallpaperWorkerWHwnd)).Returns("WorkerW");
        _nativeApiMock.Setup(api => api.GetWindow(WallpaperWorkerWHwnd, GwHwndPrev)).Returns(SecondWorkerWHwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(SecondWorkerWHwnd)).Returns("workerw"); // case-insensitive
        _nativeApiMock.Setup(api => api.GetWindow(SecondWorkerWHwnd, GwHwndPrev)).Returns(AboveProgmanHwnd);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, AboveProgmanHwnd, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
    }

    /// <summary>
    /// Windows 11 shape (10.0.26200, measured): no <c>WorkerW</c> between the desktop band's top and
    /// <c>Progman</c>, so the walk stops on its first iteration and the anchor is exactly what the
    /// original single-step lookup produced — the desktop-band walk must not change behaviour there.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WithNoWorkerWAboveProgman_AnchorsWithASingleGetWindowStep()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(AboveProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(AboveProgmanHwnd)).Returns("Shell_TrayWnd");

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, AboveProgmanHwnd, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
        _nativeApiMock.Verify(api => api.GetWindow(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Once);
    }

    /// <summary>
    /// A sibling list that never leaves the <c>WorkerW</c> run (corrupted or racing with an Explorer
    /// restart) must not spin the UI-thread timer forever: the walk is capped and degrades to the
    /// conservative <c>HWND_BOTTOM</c> fallback.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenWorkerWRunNeverEnds_StopsWalkingAndFallsBackToHwndBottom()
    {
        // Arrange: an endless chain of WorkerW siblings.
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(It.IsAny<IntPtr>(), GwHwndPrev)).Returns(WallpaperWorkerWHwnd);
        _nativeApiMock.Setup(api => api.GetWindowClassName(It.IsAny<IntPtr>())).Returns("WorkerW");

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, HwndBottom, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
        _nativeApiMock.Verify(api => api.GetWindow(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.AtMost(8));
    }

    /// <summary>
    /// The anchor is resolved by a lookup separate from the placement, so it can be destroyed in
    /// between (the app owning it closed at that exact moment) and fail the whole call, leaving the
    /// panel wherever it was — possibly above everything. One retry against <c>HWND_BOTTOM</c> covers
    /// that race; the next tick re-resolves a fresh anchor anyway.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenSetWindowPosFails_RetriesOnceAgainstHwndBottom()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(AboveProgmanHwnd);
        _nativeApiMock
            .Setup(api => api.SetWindowPos(Hwnd, AboveProgmanHwnd, 0, 0, 0, 0, ZOrderFlags))
            .Returns(false);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(Hwnd, HwndBottom, 0, 0, 0, 0, ZOrderFlags),
            Times.Once);
    }

    /// <summary>
    /// When the anchor already <i>is</i> <c>HWND_BOTTOM</c>, a failing call must not be retried with
    /// the identical arguments — that would only double the cost of every tick while Explorer is down.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenHwndBottomFallbackItselfFails_DoesNotRetry()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(IntPtr.Zero);
        _nativeApiMock
            .Setup(api => api.SetWindowPos(
                It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(false);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(
            api => api.SetWindowPos(
                It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()),
            Times.Once);
    }

    [Fact]
    public void SendToBackOfZOrder_WithZeroHandle_TouchesNothing()
    {
        // Act
        _service.SendToBackOfZOrder(IntPtr.Zero);

        // Assert — a true no-op: not even the anchor lookup runs.
        _nativeApiMock.Verify(
            api => api.SetWindowPos(
                It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()),
            Times.Never);
        _nativeApiMock.Verify(api => api.FindWindow(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _nativeApiMock.Verify(api => api.GetWindow(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// Same regression guard as <see cref="ApplyDesktopWindowStyles_NeverTouchesExplorerOrWorkerW"/>,
    /// narrowed to what this member is now allowed to do. It runs off a timer several times a second,
    /// so a read-only <c>FindWindow("Progman")</c> + <c>GetWindow</c>/<c>GetWindowClassName</c> walk
    /// (all cheap in-kernel lookups, bounded to the desktop band) is in budget, but the expensive and
    /// destructive half of the desktop-integration
    /// toolkit is not: no <c>0x052C</c> to <c>Progman</c> (a synchronous cross-process
    /// <c>SendMessage</c> into Explorer, per tick), no <c>EnumWindows</c> sweep, no <c>SetParent</c>
    /// into <c>WorkerW</c> — the last of which would resurrect the ADR-019 bug outright.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_NeverTouchesExplorerOrWorkerW()
    {
        // Arrange — the full happy path, so nothing short-circuits before the forbidden calls.
        _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
        _nativeApiMock.Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev)).Returns(AboveProgmanHwnd);

        // Act
        _service.SendToBackOfZOrder(Hwnd);

        // Assert
        _nativeApiMock.Verify(api => api.SetParent(It.IsAny<IntPtr>(), It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.SendMessage(It.IsAny<IntPtr>(), It.IsAny<uint>(), It.IsAny<IntPtr>(), It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.EnumWindows(It.IsAny<EnumWindowsCallback>(), It.IsAny<IntPtr>()), Times.Never);
        _nativeApiMock.Verify(api => api.FindWindowEx(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _nativeApiMock.Verify(api => api.SetWindowLongPtr(It.IsAny<IntPtr>(), It.IsAny<int>(), It.IsAny<IntPtr>()), Times.Never);

        // The one lookup that IS allowed, and only that one.
        _nativeApiMock.Verify(api => api.FindWindow("Progman", null), Times.Once);
        _nativeApiMock.Verify(api => api.FindWindow(It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    /// <summary>
    /// The caller is a UI-thread <c>DispatcherTimer</c> tick: an escaping exception would take the
    /// whole application down, so a failing native call must degrade silently.
    /// </summary>
    [Fact]
    public void SendToBackOfZOrder_WhenNativeCallThrows_DoesNotPropagate()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.SetWindowPos(
                It.IsAny<IntPtr>(), It.IsAny<IntPtr>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Throws(new InvalidOperationException("USER32 went away"));

        // Act & Assert
        var ex = Record.Exception(() => _service.SendToBackOfZOrder(Hwnd));
        Assert.Null(ex);
    }

    /// <summary>
    /// The anchor lookup runs inside the same try/catch as the placement itself: a throwing
    /// <c>FindWindow</c>/<c>GetWindow</c> (e.g. Explorer dying mid-tick) must not escape either.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SendToBackOfZOrder_WhenAnchorLookupThrows_DoesNotPropagate(bool throwFromFindWindow)
    {
        // Arrange
        if (throwFromFindWindow)
        {
            _nativeApiMock
                .Setup(api => api.FindWindow("Progman", null))
                .Throws(new InvalidOperationException("Explorer went away"));
        }
        else
        {
            _nativeApiMock.Setup(api => api.FindWindow("Progman", null)).Returns(ProgmanHwnd);
            _nativeApiMock
                .Setup(api => api.GetWindow(ProgmanHwnd, GwHwndPrev))
                .Throws(new InvalidOperationException("Explorer went away"));
        }

        // Act & Assert
        var ex = Record.Exception(() => _service.SendToBackOfZOrder(Hwnd));
        Assert.Null(ex);
    }
}
