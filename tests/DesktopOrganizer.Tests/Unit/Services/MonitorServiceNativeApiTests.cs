using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for the concrete <see cref="MonitorService"/> implementation, exercised against a mocked
/// <see cref="INativeApi"/> and a mocked <see cref="IDesktopScaleProvider"/>. <see cref="MonitorServiceTests"/>
/// already covers <see cref="IMonitorService"/>'s query contract (<c>FindMonitorAt</c>/<c>FindByDeviceName</c>)
/// through a hand-built <see cref="Mock{T}"/> of the interface itself — these tests instead cover what only
/// the real class has: delegating px↔DIP conversion to <see cref="IDesktopScaleProvider.Current"/>, the
/// last-resort <c>FallbackMonitor</c> path, the hidden broadcast-window lifecycle (<c>Start</c>/<c>Dispose</c>),
/// and <c>WM_DISPLAYCHANGE</c>/<c>WM_SETTINGCHANGE</c>/<c>WM_DPICHANGED</c> handling in its
/// <c>NativeWindowProc</c>. <see cref="MonitorServiceScaleTests"/> covers the release-hardening F wiring
/// itself (mode-aware scaling, <c>Refresh</c> → single <c>MonitorsChanged</c>, <c>Changed</c> forwarding).
/// </summary>
/// <remarks>
/// There is no real window or message loop here: the <c>NativeWindowProc</c> passed to
/// <c>CreateMessageWindow</c> is captured via the mock and invoked directly to simulate a broadcast
/// message, per the same technique used for the WH_MOUSE_LL callback in
/// <see cref="DesktopGestureServiceTests"/>.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class MonitorServiceNativeApiTests
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const string ClassNamePrefix = "DesktopOrganizer.MonitorWatcher.";

    private readonly Mock<INativeApi> _nativeApiMock = new();
    private readonly Mock<IDesktopScaleProvider> _scaleProviderMock = new();
    private readonly Mock<IAppLog> _appLogMock = new();
    private readonly MonitorService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorServiceNativeApiTests"/> class.
    /// </summary>
    public MonitorServiceNativeApiTests()
    {
        _service = new MonitorService(_nativeApiMock.Object, _scaleProviderMock.Object, _appLogMock.Object);
    }

    // ── GetMonitors: reads through IDesktopScaleProvider.Current ────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="MonitorService.GetMonitors"/> converts the native physical-pixel bounds and
    /// work area into DIPs through <see cref="IDesktopScaleProvider.Current"/> instead of dividing by
    /// <see cref="INativeApi.GetSystemDpiScale"/> itself.
    /// </summary>
    [Fact]
    public void GetMonitors_ConvertsPhysicalPixelsToDip_ThroughScaleProviderCurrent()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(
            2.0,
            (@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 3840, 2160), new System.Drawing.Rectangle(0, 40, 3840, 2120), true)));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(
                @"\\.\DISPLAY1",
                new System.Drawing.Rectangle(0, 0, 3840, 2160),
                new System.Drawing.Rectangle(0, 40, 3840, 2120),
                true),
        ]);

        // Act
        var monitors = _service.GetMonitors();

        // Assert
        var m = Assert.Single(monitors);
        Assert.Equal(@"\\.\DISPLAY1", m.DeviceName);
        Assert.True(m.IsPrimary);
        Assert.Equal(new Rect(0, 0, 1920, 1080), m.Bounds);
        Assert.Equal(new Rect(0, 20, 1920, 1060), m.WorkArea);
        _nativeApiMock.Verify(api => api.GetSystemDpiScale(), Times.Never);
    }

    /// <summary>
    /// Verifies that when the native monitor enumeration throws, <see cref="MonitorService.GetMonitors"/>
    /// degrades to an empty list and logs the failure via <see cref="IAppLog"/> instead of throwing.
    /// </summary>
    [Fact]
    public void GetMonitors_WhenNativeApiThrows_ReturnsEmptyListAndLogs()
    {
        // Arrange
        var thrown = new InvalidOperationException("EnumDisplayMonitors failed");
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(1.0));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Throws(thrown);

        // Act
        var monitors = _service.GetMonitors();

        // Assert
        Assert.Empty(monitors);
        _appLogMock.Verify(
            log => log.Error("MonitorService", "GetMonitorsFailed", thrown, It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    // ── GetPrimaryMonitor: primary flag, first-enumerated fallback, last-resort fallback ────────

    /// <summary>
    /// Verifies that <see cref="MonitorService.GetPrimaryMonitor"/> returns the monitor the native API
    /// flags as primary, even when it is not the first one enumerated.
    /// </summary>
    [Fact]
    public void GetPrimaryMonitor_ReturnsMonitorFlaggedPrimary()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(
            1.0,
            (@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), false),
            (@"\\.\DISPLAY2", new System.Drawing.Rectangle(1920, 0, 1920, 1080), new System.Drawing.Rectangle(1920, 0, 1920, 1080), true)));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), false),
            new NativeMonitorInfo(@"\\.\DISPLAY2", new System.Drawing.Rectangle(1920, 0, 1920, 1080), new System.Drawing.Rectangle(1920, 0, 1920, 1080), true),
        ]);

        // Act
        var primary = _service.GetPrimaryMonitor();

        // Assert
        Assert.Equal(@"\\.\DISPLAY2", primary.DeviceName);
    }

    /// <summary>
    /// Verifies that when no enumerated monitor is flagged primary, <see cref="MonitorService.GetPrimaryMonitor"/>
    /// falls back to the first monitor enumerated.
    /// </summary>
    [Fact]
    public void GetPrimaryMonitor_WhenNoneFlaggedPrimary_ReturnsFirstEnumerated()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(
            1.0,
            (@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), false)));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), false),
        ]);

        // Act
        var primary = _service.GetPrimaryMonitor();

        // Assert
        Assert.Equal(@"\\.\DISPLAY1", primary.DeviceName);
    }

    /// <summary>
    /// Verifies that when monitor enumeration yields nothing at all, <see cref="MonitorService.GetPrimaryMonitor"/>
    /// synthesizes a last-resort fallback monitor sized from <see cref="INativeApi.GetPrimaryScreenSizePx"/>
    /// and scaled by <see cref="IDesktopScaleProvider.Current"/>'s <see cref="DesktopScaleModel.ReferenceScale"/>.
    /// </summary>
    [Fact]
    public void GetPrimaryMonitor_WhenEnumerationYieldsNothing_ReturnsFallbackMonitor()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(2.0));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([]);
        _nativeApiMock.Setup(api => api.GetPrimaryScreenSizePx()).Returns(new System.Drawing.Size(3840, 2160));

        // Act
        var primary = _service.GetPrimaryMonitor();

        // Assert
        Assert.Equal(@"\\.\DISPLAY1", primary.DeviceName);
        Assert.True(primary.IsPrimary);
        Assert.Equal(new Rect(0, 0, 1920, 1080), primary.Bounds);
        Assert.Equal(primary.Bounds, primary.WorkArea);
    }

    /// <summary>
    /// Verifies that the last-resort fallback monitor defaults to 1920x1080 when the native API reports a
    /// zero-sized primary screen.
    /// </summary>
    [Fact]
    public void GetPrimaryMonitor_FallbackWithZeroPrimaryScreenSize_DefaultsTo1920x1080()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(1.0));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([]);
        _nativeApiMock.Setup(api => api.GetPrimaryScreenSizePx()).Returns(new System.Drawing.Size(0, 0));

        // Act
        var primary = _service.GetPrimaryMonitor();

        // Assert
        Assert.Equal(new Rect(0, 0, 1920, 1080), primary.Bounds);
    }

    // ── FindMonitorAt / FindByDeviceName: thin wrappers over GetMonitors ─────────────────────────

    /// <summary>
    /// Verifies that <see cref="MonitorService.FindMonitorAt"/> returns the monitor whose bounds contain
    /// the given point.
    /// </summary>
    [Fact]
    public void FindMonitorAt_PointInsideAMonitor_ReturnsIt()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(
            1.0,
            (@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), true)));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), true),
        ]);

        // Act
        var result = _service.FindMonitorAt(new Point(100, 100));

        // Assert
        Assert.NotNull(result);
        Assert.Equal(@"\\.\DISPLAY1", result!.DeviceName);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.FindByDeviceName"/> returns <see langword="null"/> for a
    /// <see langword="null"/> or empty device name without ever querying the native API.
    /// </summary>
    /// <param name="deviceName">The null or empty device name under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FindByDeviceName_NullOrEmpty_ReturnsNullWithoutQueryingNativeApi(string? deviceName)
    {
        // Act
        var result = _service.FindByDeviceName(deviceName!);

        // Assert
        Assert.Null(result);
        _nativeApiMock.Verify(api => api.GetMonitorInfos(), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.FindByDeviceName"/> matches the device name
    /// case-insensitively.
    /// </summary>
    [Fact]
    public void FindByDeviceName_CaseInsensitiveMatch_ReturnsMonitor()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(
            1.0,
            (@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), true)));
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), true),
        ]);

        // Act
        var result = _service.FindByDeviceName(@"\\.\display1");

        // Assert
        Assert.NotNull(result);
    }

    // ── GetWindowRectDip: reads through IDesktopScaleProvider.Current ────────────────────────────
    // (docs/PLANS/workspace-grid-03-04-integration.md §1.2; release-hardening-1.0-F-dpi.md F2 #2/#4;
    // F4 removed IMonitorService.GetSystemDpiScale itself — zero UI callers, confirmed by grep — so only
    // GetWindowRectDip remains here.)

    /// <summary>
    /// Verifies that <see cref="MonitorService.GetWindowRectDip"/> converts the native window rectangle
    /// into DIPs through <see cref="IDesktopScaleProvider.Current"/> — the same conversion
    /// <see cref="MonitorService.GetMonitors"/> now uses.
    /// </summary>
    [Fact]
    public void GetWindowRectDip_ValidHandle_ConvertsPhysicalPixelsToDip_ThroughScaleProviderCurrent()
    {
        // Arrange
        _scaleProviderMock.Setup(p => p.Current).Returns(UniformModel(
            2.0,
            (@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 3840, 2160), new System.Drawing.Rectangle(0, 0, 3840, 2160), true)));
        _nativeApiMock
            .Setup(api => api.GetWindowRect(new IntPtr(42), out It.Ref<NativeRectPx>.IsAny))
            .Returns((IntPtr _, out NativeRectPx rect) =>
            {
                rect = new NativeRectPx(100, 200, 500, 600);
                return true;
            });

        // Act
        var result = _service.GetWindowRectDip(new IntPtr(42));

        // Assert
        Assert.NotNull(result);
        Assert.Equal(new Rect(50, 100, 200, 200), result!.Value);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.GetWindowRectDip"/> returns <see langword="null"/> without
    /// consulting the scale provider when the native <c>GetWindowRect</c> call itself reports failure.
    /// </summary>
    [Fact]
    public void GetWindowRectDip_NativeApiReportsFailure_ReturnsNull()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.GetWindowRect(It.IsAny<IntPtr>(), out It.Ref<NativeRectPx>.IsAny))
            .Returns((IntPtr _, out NativeRectPx rect) =>
            {
                rect = default;
                return false;
            });

        // Act
        var result = _service.GetWindowRectDip(new IntPtr(42));

        // Assert
        Assert.Null(result);
        _scaleProviderMock.Verify(p => p.Current, Times.Never);
    }

    // ── Start/Dispose: hidden broadcast-window lifecycle + IDesktopScaleProvider.Changed wiring ──

    /// <summary>
    /// Verifies that <see cref="MonitorService.Start"/> creates the hidden broadcast message window used
    /// to receive <c>WM_DISPLAYCHANGE</c>/<c>WM_SETTINGCHANGE</c>/<c>WM_DPICHANGED</c>.
    /// </summary>
    [Fact]
    public void Start_CreatesHiddenMessageWindow()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.Is<string>(n => n.StartsWith(ClassNamePrefix)), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(new IntPtr(1), 1, 0));

        // Act
        _service.Start();

        // Assert
        _nativeApiMock.Verify(
            api => api.CreateMessageWindow(It.Is<string>(n => n.StartsWith(ClassNamePrefix)), It.IsAny<NativeWindowProc>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.Start"/> subscribes to <see cref="IDesktopScaleProvider.Changed"/>
    /// exactly once, even when the broadcast window itself fails to create.
    /// </summary>
    [Fact]
    public void Start_SubscribesToScaleProviderChanged_EvenWhenBroadcastWindowFails()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(IntPtr.Zero, 0, 87));

        // Act
        _service.Start();
        var raised = false;
        _service.MonitorsChanged += () => raised = true;
        _scaleProviderMock.Raise(p => p.Changed += null, new DesktopScaleChangedEventArgs(UniformModel(1.0), UniformModel(1.25)));

        // Assert
        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that an exception thrown by a <see cref="MonitorService.MonitorsChanged"/> subscriber is
    /// caught and logged (not the same log source as a failed native <c>WndProc</c> call), and does not
    /// escape whatever caused <see cref="IDesktopScaleProvider.Changed"/> to be raised.
    /// </summary>
    [Fact]
    public void ScaleProviderChanged_WhenMonitorsChangedSubscriberThrows_LogsAndDoesNotThrow()
    {
        // Arrange
        var thrown = new InvalidOperationException("boom");
        _service.Start();
        _service.MonitorsChanged += () => throw thrown;

        // Act
        var ex = Record.Exception(() =>
            _scaleProviderMock.Raise(p => p.Changed += null, new DesktopScaleChangedEventArgs(UniformModel(1.0), UniformModel(1.25))));

        // Assert
        Assert.Null(ex);
        _appLogMock.Verify(
            log => log.Error("MonitorService", "MonitorsChangedHandlerFailed", thrown, It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that calling <see cref="MonitorService.Start"/> a second time after a successful first
    /// call is a no-op and does not create a second broadcast window, nor subscribe to
    /// <see cref="IDesktopScaleProvider.Changed"/> a second time.
    /// </summary>
    [Fact]
    public void Start_CalledTwice_WhenFirstSucceeded_CreatesWindowOnlyOnceAndSubscribesOnlyOnce()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(new IntPtr(1), 1, 0));

        // Act
        _service.Start();
        _service.Start();

        var callCount = 0;
        _service.MonitorsChanged += () => callCount++;
        _scaleProviderMock.Raise(p => p.Changed += null, new DesktopScaleChangedEventArgs(UniformModel(1.0), UniformModel(1.25)));

        // Assert
        _nativeApiMock.Verify(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()), Times.Once);
        Assert.Equal(1, callCount);
    }

    /// <summary>
    /// Verifies that a failed broadcast-window creation is logged via <see cref="IAppLog"/> and
    /// degrades silently rather than throwing.
    /// </summary>
    [Fact]
    public void Start_WhenCreateMessageWindowFails_LogsAndDoesNotThrow()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(IntPtr.Zero, 0, 87));

        // Act
        var ex = Record.Exception(() => _service.Start());

        // Assert
        Assert.Null(ex);
        _appLogMock.Verify(
            log => log.Warn("MonitorService", "BroadcastWindowCreateFailed", It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that an exception thrown by the native window-registration call is caught, logged via
    /// <see cref="IAppLog"/>, and does not escape <see cref="MonitorService.Start"/>.
    /// </summary>
    [Fact]
    public void Start_WhenNativeApiThrows_LogsAndDoesNotThrow()
    {
        // Arrange
        var thrown = new InvalidOperationException("RegisterClassEx failed");
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Throws(thrown);

        // Act
        var ex = Record.Exception(() => _service.Start());

        // Assert
        Assert.Null(ex);
        _appLogMock.Verify(
            log => log.Error("MonitorService", "StartFailed", thrown, It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.Dispose"/> does nothing when
    /// <see cref="MonitorService.Start"/> was never called.
    /// </summary>
    [Fact]
    public void Dispose_WithoutStart_DoesNotCallDestroyMessageWindow()
    {
        // Act
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.DestroyMessageWindow(It.IsAny<NativeMessageWindow>()), Times.Never);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.Dispose"/> destroys the broadcast window that was created
    /// by a preceding <see cref="MonitorService.Start"/> call.
    /// </summary>
    [Fact]
    public void Dispose_AfterSuccessfulStart_DestroysMessageWindow()
    {
        // Arrange
        var window = new NativeMessageWindow(new IntPtr(1), 1, 0);
        _nativeApiMock.Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>())).Returns(window);
        _service.Start();

        // Act
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.DestroyMessageWindow(window), Times.Once);
    }

    /// <summary>
    /// Verifies that calling <see cref="MonitorService.Dispose"/> twice destroys the broadcast window
    /// only once.
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_DestroysOnlyOnce()
    {
        // Arrange
        var window = new NativeMessageWindow(new IntPtr(1), 1, 0);
        _nativeApiMock.Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>())).Returns(window);
        _service.Start();

        // Act
        _service.Dispose();
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.DestroyMessageWindow(It.IsAny<NativeMessageWindow>()), Times.Once);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.Dispose"/> unsubscribes from
    /// <see cref="IDesktopScaleProvider.Changed"/>, so a later raise no longer reaches <see cref="MonitorService.MonitorsChanged"/>.
    /// </summary>
    [Fact]
    public void Dispose_UnsubscribesFromScaleProviderChanged()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(new IntPtr(1), 1, 0));
        _service.Start();
        var raised = false;
        _service.MonitorsChanged += () => raised = true;

        // Act
        _service.Dispose();
        _scaleProviderMock.Raise(p => p.Changed += null, new DesktopScaleChangedEventArgs(UniformModel(1.0), UniformModel(1.25)));

        // Assert
        Assert.False(raised);
    }

    /// <summary>
    /// Handle creation failed but the window class was registered (<c>ClassAtom != 0</c>) — <c>HasResources</c>
    /// is true even though <c>IsValid</c> is false, and <c>Dispose</c> must still release it.
    /// </summary>
    [Fact]
    public void Dispose_WhenStartLeftPartialResources_StillDestroysThem()
    {
        // Arrange
        var window = new NativeMessageWindow(IntPtr.Zero, 5, 87);
        _nativeApiMock.Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>())).Returns(window);
        _service.Start();

        // Act
        _service.Dispose();

        // Assert
        _nativeApiMock.Verify(api => api.DestroyMessageWindow(window), Times.Once);
    }

    // ── WndProc: WM_DISPLAYCHANGE / WM_SETTINGCHANGE / WM_DPICHANGED -> Refresh() ────────────────
    // Forwarding Refresh()'s resulting Changed event into MonitorsChanged is covered end-to-end (no
    // double-fire) by MonitorServiceScaleTests; these tests only cover which messages trigger Refresh().

    /// <summary>
    /// Verifies that a <c>WM_DISPLAYCHANGE</c> message delivered to the captured <c>NativeWindowProc</c>
    /// calls <see cref="IDesktopScaleProvider.Refresh"/>.
    /// </summary>
    [Fact]
    public void WndProc_DisplayChangeMessage_CallsScaleProviderRefresh()
    {
        // Arrange
        var wndProc = CaptureWndProc();

        // Act
        wndProc(new IntPtr(1), WmDisplayChange, IntPtr.Zero, IntPtr.Zero);

        // Assert
        _scaleProviderMock.Verify(p => p.Refresh(), Times.Once);
    }

    /// <summary>
    /// Verifies that a <c>WM_SETTINGCHANGE</c> message delivered to the captured <c>NativeWindowProc</c>
    /// also calls <see cref="IDesktopScaleProvider.Refresh"/>.
    /// </summary>
    [Fact]
    public void WndProc_SettingChangeMessage_CallsScaleProviderRefresh()
    {
        // Arrange
        var wndProc = CaptureWndProc();

        // Act
        wndProc(new IntPtr(1), WmSettingChange, IntPtr.Zero, IntPtr.Zero);

        // Assert
        _scaleProviderMock.Verify(p => p.Refresh(), Times.Once);
    }

    /// <summary>
    /// Verifies that the new <c>WM_DPICHANGED</c> message delivered to the captured <c>NativeWindowProc</c>
    /// calls <see cref="IDesktopScaleProvider.Refresh"/> (release-hardening F2 #5).
    /// </summary>
    [Fact]
    public void WndProc_DpiChangedMessage_CallsScaleProviderRefresh()
    {
        // Arrange
        var wndProc = CaptureWndProc();

        // Act
        wndProc(new IntPtr(1), WmDpiChanged, IntPtr.Zero, IntPtr.Zero);

        // Assert
        _scaleProviderMock.Verify(p => p.Refresh(), Times.Once);
    }

    /// <summary>
    /// Verifies that a message unrelated to display/setting/DPI changes (e.g. <c>WM_CLOSE</c>) does not
    /// call <see cref="IDesktopScaleProvider.Refresh"/>.
    /// </summary>
    [Fact]
    public void WndProc_UnrelatedMessage_DoesNotCallScaleProviderRefresh()
    {
        // Arrange
        var wndProc = CaptureWndProc();

        // Act — WM_CLOSE, unrelated to display/setting/DPI changes.
        wndProc(new IntPtr(1), 0x0010, IntPtr.Zero, IntPtr.Zero);

        // Assert
        _scaleProviderMock.Verify(p => p.Refresh(), Times.Never);
    }

    /// <summary>
    /// Verifies that the captured <c>NativeWindowProc</c> always forwards the message to
    /// <see cref="INativeApi.DefaultWindowProc"/> and returns exactly that call's result.
    /// </summary>
    [Fact]
    public void WndProc_AlwaysForwardsToDefaultWindowProc_AndReturnsItsResult()
    {
        // Arrange
        var wndProc = CaptureWndProc();
        var hwnd = new IntPtr(1);
        var wParam = new IntPtr(11);
        var lParam = new IntPtr(22);
        var expected = new IntPtr(333);
        _nativeApiMock.Setup(api => api.DefaultWindowProc(hwnd, WmDisplayChange, wParam, lParam)).Returns(expected);

        // Act
        var result = wndProc(hwnd, WmDisplayChange, wParam, lParam);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that an exception thrown by <see cref="IDesktopScaleProvider.Refresh"/> is caught and
    /// logged, and that the WndProc still forwards to <see cref="INativeApi.DefaultWindowProc"/>.
    /// </summary>
    [Fact]
    public void WndProc_WhenScaleProviderRefreshThrows_LogsAndStillCallsDefaultWindowProc()
    {
        // Arrange
        var wndProc = CaptureWndProc();
        var thrown = new InvalidOperationException("boom");
        _scaleProviderMock.Setup(p => p.Refresh()).Throws(thrown);
        var expected = new IntPtr(444);
        _nativeApiMock
            .Setup(api => api.DefaultWindowProc(It.IsAny<IntPtr>(), WmDisplayChange, It.IsAny<IntPtr>(), It.IsAny<IntPtr>()))
            .Returns(expected);

        // Act
        var result = wndProc(new IntPtr(1), WmDisplayChange, IntPtr.Zero, IntPtr.Zero);

        // Assert — a failed Refresh() must never crash the WndProc.
        Assert.Equal(expected, result);
        _appLogMock.Verify(
            log => log.Error("MonitorService", "WndProcError", thrown, It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Once);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a uniform top-level <see cref="DesktopScaleModel"/> snapshot at the given scale, spanning the
    /// given monitors (device name/bounds/work area/primary only — the scale is applied uniformly), the
    /// same shape <see cref="MonitorService.GetMonitors"/> now reads through instead of dividing rectangles
    /// itself.
    /// </summary>
    private static DesktopScaleModel UniformModel(double scale, params (string DeviceName, System.Drawing.Rectangle Bounds, System.Drawing.Rectangle WorkArea, bool IsPrimary)[] monitors)
        => DesktopScaleModel.Create(
            DesktopLayerMode.TopLevel,
            scale,
            scale,
            monitors.Select(m => new MonitorScale(m.DeviceName, m.Bounds, m.WorkArea, m.IsPrimary, scale)).ToList());

    private NativeWindowProc CaptureWndProc(NativeMessageWindow? window = null)
    {
        NativeWindowProc? captured = null;
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Callback<string, NativeWindowProc>((_, proc) => captured = proc)
            .Returns(window ?? new NativeMessageWindow(new IntPtr(1), 1, 0));

        _service.Start();

        Assert.NotNull(captured);
        return captured!;
    }
}
