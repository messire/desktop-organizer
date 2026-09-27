using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="ExplorerRestartMonitor"/> (ADR-034) against a mocked <see cref="INativeApi"/>.
/// There is no real window or message loop: the <see cref="NativeWindowProc"/> handed to
/// <see cref="INativeApi.CreateMessageWindow"/> is captured and invoked directly, the same technique
/// <see cref="MonitorServiceNativeApiTests"/> uses for <c>WM_DISPLAYCHANGE</c>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class ExplorerRestartMonitorTests
{
    private const int TaskbarCreated = 0xC0DE;
    private const int WmDisplayChange = 0x007E;

    private static readonly IntPtr ListenerHwnd = new(0x1234);
    private static readonly IntPtr ProgmanA = new(0xA000);
    private static readonly IntPtr ProgmanB = new(0xB000);

    private readonly Mock<INativeApi> _nativeApi = new();
    private readonly Mock<IAppLog> _log = new();
    private readonly ExplorerRestartMonitor _sut;
    private NativeWindowProc? _wndProc;
    private IntPtr _progman = ProgmanA;
    private int _raised;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExplorerRestartMonitorTests"/> class with a happy-path
    /// native layer: the message registers, the window is created, the timer starts.
    /// </summary>
    public ExplorerRestartMonitorTests()
    {
        _nativeApi.Setup(api => api.RegisterWindowMessage(ExplorerRestartMonitor.TaskbarCreatedMessageName)).Returns(TaskbarCreated);
        _nativeApi
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Callback<string, NativeWindowProc>((_, proc) => _wndProc = proc)
            .Returns(new NativeMessageWindow(ListenerHwnd, 42, 0));
        _nativeApi.Setup(api => api.SetTimer(ListenerHwnd, ExplorerRestartMonitor.ProgmanPollTimerId, ExplorerRestartMonitor.ProgmanPollIntervalMs)).Returns(true);
        _nativeApi.Setup(api => api.FindWindow("Progman", null)).Returns(() => _progman);

        _sut = new ExplorerRestartMonitor(_nativeApi.Object, _log.Object);
        _sut.ExplorerRestarted += (_, _) => _raised++;
    }

    /// <summary>Start registers TaskbarCreated, creates its own listener window, lets the message through UIPI and starts the poll.</summary>
    [Fact]
    public void Start_CreatesListenerWindow_AllowsTaskbarCreated_AndStartsPoll()
    {
        _sut.Start();

        Assert.True(_sut.IsListening);
        _nativeApi.Verify(
            api => api.CreateMessageWindow(It.Is<string>(name => name.StartsWith("DesktopOrganizer.ExplorerWatcher.", StringComparison.Ordinal)), It.IsAny<NativeWindowProc>()),
            Times.Once);
        _nativeApi.Verify(api => api.AllowWindowMessage(ListenerHwnd, TaskbarCreated), Times.Once);
        _nativeApi.Verify(api => api.SetTimer(ListenerHwnd, ExplorerRestartMonitor.ProgmanPollTimerId, ExplorerRestartMonitor.ProgmanPollIntervalMs), Times.Once);
    }

    /// <summary>A second Start is a no-op — one listener window per instance.</summary>
    [Fact]
    public void Start_IsIdempotent()
    {
        _sut.Start();
        _sut.Start();

        _nativeApi.Verify(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()), Times.Once);
    }

    /// <summary>The TaskbarCreated broadcast raises the event and still falls through to DefWindowProc.</summary>
    [Fact]
    public void TaskbarCreated_RaisesExplorerRestarted()
    {
        _nativeApi.Setup(api => api.DefaultWindowProc(ListenerHwnd, TaskbarCreated, IntPtr.Zero, IntPtr.Zero)).Returns(new IntPtr(7));
        _sut.Start();

        var result = _wndProc!(ListenerHwnd, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);

        Assert.Equal(1, _raised);
        Assert.Equal(new IntPtr(7), result);
    }

    /// <summary>Unrelated messages never raise the event.</summary>
    [Fact]
    public void OtherMessages_DoNotRaise()
    {
        _sut.Start();

        _wndProc!(ListenerHwnd, WmDisplayChange, IntPtr.Zero, IntPtr.Zero);
        _wndProc!(ListenerHwnd, ExplorerRestartMonitor.WmTimer, new IntPtr(99), IntPtr.Zero); // someone else's timer id

        Assert.Equal(0, _raised);
    }

    /// <summary>The poll tick with an unchanged Progman handle is silent.</summary>
    [Fact]
    public void PollTick_ProgmanUnchanged_DoesNotRaise()
    {
        _sut.Start();

        _wndProc!(ListenerHwnd, ExplorerRestartMonitor.WmTimer, ExplorerRestartMonitor.ProgmanPollTimerId, IntPtr.Zero);

        Assert.Equal(0, _raised);
    }

    /// <summary>
    /// Insurance path: a changed Progman handle (Explorer restarted, TaskbarCreated lost to UIPI) raises
    /// the event exactly once, and a later unchanged tick stays silent.
    /// </summary>
    [Fact]
    public void PollTick_ProgmanChanged_RaisesOnce()
    {
        _sut.Start();
        _progman = ProgmanB;

        _wndProc!(ListenerHwnd, ExplorerRestartMonitor.WmTimer, ExplorerRestartMonitor.ProgmanPollTimerId, IntPtr.Zero);
        _wndProc!(ListenerHwnd, ExplorerRestartMonitor.WmTimer, ExplorerRestartMonitor.ProgmanPollTimerId, IntPtr.Zero);

        Assert.Equal(1, _raised);
    }

    /// <summary>
    /// While Explorer is down (no Progman) nothing is raised; the NEW desktop window appearing is the
    /// change that raises — the last known handle is kept across the gap.
    /// </summary>
    [Fact]
    public void PollTick_ExplorerDownThenBack_RaisesWhenNewProgmanAppears()
    {
        _sut.Start();

        _progman = IntPtr.Zero;
        Assert.False(_sut.PollProgmanOnce());

        _progman = ProgmanB;
        Assert.True(_sut.PollProgmanOnce());

        Assert.Equal(1, _raised);
    }

    /// <summary>
    /// TaskbarCreated re-baselines the poll, so the same restart is not reported a second time by the
    /// next poll tick.
    /// </summary>
    [Fact]
    public void TaskbarCreated_RebaselinesPoll_NoDuplicateFromPoll()
    {
        _sut.Start();
        _progman = ProgmanB;

        _wndProc!(ListenerHwnd, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
        _sut.PollProgmanOnce();

        Assert.Equal(1, _raised);
    }

    /// <summary>Listener window creation failure degrades (no listener, no timer), logs a warning, never throws.</summary>
    [Fact]
    public void Start_WindowCreationFails_DegradesAndLogs()
    {
        _nativeApi
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(IntPtr.Zero, 0, 1410));

        _sut.Start();

        Assert.False(_sut.IsListening);
        _nativeApi.Verify(api => api.SetTimer(It.IsAny<IntPtr>(), It.IsAny<IntPtr>(), It.IsAny<uint>()), Times.Never);
        _log.Verify(
            l => l.Warn("ExplorerRestartMonitor", "ListenerWindowCreateFailed", It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>A native exception during Start is logged, not thrown.</summary>
    [Fact]
    public void Start_NativeThrows_LogsError_DoesNotThrow()
    {
        _nativeApi
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Throws(new InvalidOperationException("boom"));

        var ex = Record.Exception(_sut.Start);

        Assert.Null(ex);
        Assert.False(_sut.IsListening);
        _log.Verify(
            l => l.Error("ExplorerRestartMonitor", "StartFailed", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>Without a registered TaskbarCreated id the monitor still listens (poll-only) and warns.</summary>
    [Fact]
    public void Start_RegisterMessageFails_StillListensViaPoll()
    {
        _nativeApi.Setup(api => api.RegisterWindowMessage(It.IsAny<string>())).Returns(0);

        _sut.Start();
        _progman = ProgmanB;
        _wndProc!(ListenerHwnd, ExplorerRestartMonitor.WmTimer, ExplorerRestartMonitor.ProgmanPollTimerId, IntPtr.Zero);

        Assert.True(_sut.IsListening);
        Assert.Equal(1, _raised);
        _nativeApi.Verify(api => api.AllowWindowMessage(It.IsAny<IntPtr>(), It.IsAny<int>()), Times.Never);
        _log.Verify(
            l => l.Warn("ExplorerRestartMonitor", "RegisterTaskbarCreatedFailed", It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>A throwing subscriber never escapes the window procedure; DefWindowProc still runs.</summary>
    [Fact]
    public void WndProc_SubscriberThrows_IsSwallowedAndLogged()
    {
        _sut.ExplorerRestarted += (_, _) => throw new InvalidOperationException("subscriber");
        _sut.Start();

        var ex = Record.Exception(() => _wndProc!(ListenerHwnd, TaskbarCreated, IntPtr.Zero, IntPtr.Zero));

        Assert.Null(ex);
        _nativeApi.Verify(api => api.DefaultWindowProc(ListenerHwnd, TaskbarCreated, IntPtr.Zero, IntPtr.Zero), Times.Once);
        _log.Verify(
            l => l.Error("ExplorerRestartMonitor", "WndProcError", It.IsAny<Exception?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>Dispose stops the timer and destroys the window exactly once, and is idempotent.</summary>
    [Fact]
    public void Dispose_KillsTimerAndDestroysWindow_Once()
    {
        _sut.Start();

        _sut.Dispose();
        _sut.Dispose();

        Assert.False(_sut.IsListening);
        _nativeApi.Verify(api => api.KillTimer(ListenerHwnd, ExplorerRestartMonitor.ProgmanPollTimerId), Times.Once);
        _nativeApi.Verify(api => api.DestroyMessageWindow(It.Is<NativeMessageWindow>(w => w.Handle == ListenerHwnd)), Times.Once);
    }

    /// <summary>Start after Dispose does nothing.</summary>
    [Fact]
    public void Start_AfterDispose_IsNoOp()
    {
        _sut.Dispose();
        _sut.Start();

        _nativeApi.Verify(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()), Times.Never);
    }
}
