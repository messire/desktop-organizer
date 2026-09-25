using System.IO;
using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for the concrete <see cref="MonitorService"/> implementation, exercised against a mocked
/// <see cref="INativeApi"/>. <see cref="MonitorServiceTests"/> already covers
/// <see cref="IMonitorService"/>'s query contract (<c>FindMonitorAt</c>/<c>FindByDeviceName</c>) through a
/// hand-built <see cref="Mock{T}"/> of the interface itself — these tests instead cover what only the real
/// class has: the DIP-scaling math (<c>ToDipRect</c>), the last-resort <c>FallbackMonitor</c> path, the
/// hidden broadcast-window lifecycle (<c>Start</c>/<c>Dispose</c>), and <c>WM_DISPLAYCHANGE</c>/
/// <c>WM_SETTINGCHANGE</c> handling in its <c>NativeWindowProc</c>.
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
    private const string ClassNamePrefix = "DesktopOrganizer.MonitorWatcher.";

    private readonly Mock<INativeApi> _nativeApiMock = new();
    private readonly MonitorService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorServiceNativeApiTests"/> class.
    /// </summary>
    public MonitorServiceNativeApiTests()
    {
        _service = new MonitorService(_nativeApiMock.Object);
    }

    // ── GetMonitors: DIP scaling ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="MonitorService.GetMonitors"/> scales the native physical-pixel bounds and
    /// work area into DIPs using <see cref="INativeApi.GetSystemDpiScale"/>.
    /// </summary>
    [Fact]
    public void GetMonitors_ScalesPhysicalPixelsToDip_UsingSystemDpiScale()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(2.0);
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
    }

    /// <summary>
    /// Verifies that a zero or negative reported DPI scale is treated as 1.0 (no scaling), instead of
    /// producing a degenerate or inverted rectangle.
    /// </summary>
    /// <param name="invalidScale">The invalid DPI scale value under test.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void GetMonitors_WhenDpiScaleIsZeroOrNegative_TreatsScaleAsOne(double invalidScale)
    {
        // Arrange
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(invalidScale);
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(
                @"\\.\DISPLAY1",
                new System.Drawing.Rectangle(0, 0, 1920, 1080),
                new System.Drawing.Rectangle(0, 0, 1920, 1080),
                true),
        ]);

        // Act
        var monitors = _service.GetMonitors();

        // Assert
        Assert.Equal(new Rect(0, 0, 1920, 1080), monitors[0].Bounds);
    }

    /// <summary>
    /// Verifies that when the native monitor enumeration throws, <see cref="MonitorService.GetMonitors"/>
    /// degrades to an empty list and logs the failure to <see cref="Console.Error"/> instead of throwing.
    /// </summary>
    [Fact]
    public void GetMonitors_WhenNativeApiThrows_ReturnsEmptyListAndLogsToConsoleError()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(1.0);
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Throws(new InvalidOperationException("EnumDisplayMonitors failed"));

        using var consoleOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(consoleOutput);

        IReadOnlyList<MonitorInfo> monitors;
        try
        {
            // Act
            monitors = _service.GetMonitors();
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Assert
        Assert.Empty(monitors);
        Assert.Contains("MonitorService.GetMonitors failed: EnumDisplayMonitors failed", consoleOutput.ToString());
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
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(1.0);
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
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(1.0);
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
    /// synthesizes a last-resort fallback monitor sized from <see cref="INativeApi.GetPrimaryScreenSizePx"/>.
    /// </summary>
    [Fact]
    public void GetPrimaryMonitor_WhenEnumerationYieldsNothing_ReturnsFallbackMonitor()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(2.0);
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
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(1.0);
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
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(1.0);
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
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(1.0);
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(@"\\.\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1080), true),
        ]);

        // Act
        var result = _service.FindByDeviceName(@"\\.\display1");

        // Assert
        Assert.NotNull(result);
    }

    // ── GetSystemDpiScale / GetWindowRectDip: Stage S additive members ───────────────────────────
    // (docs/PLANS/workspace-grid-03-04-integration.md §1.2)

    /// <summary>
    /// Verifies that <see cref="MonitorService.GetSystemDpiScale"/> delegates directly to
    /// <see cref="INativeApi.GetSystemDpiScale"/> without any additional transformation.
    /// </summary>
    /// <param name="scale">The DPI scale value the mocked native API is set up to return.</param>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void GetSystemDpiScale_DelegatesDirectlyToNativeApi(double scale)
    {
        // Arrange
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(scale);

        // Act
        var result = _service.GetSystemDpiScale();

        // Assert
        Assert.Equal(scale, result);
    }

    /// <summary>
    /// Verifies that <see cref="MonitorService.GetWindowRectDip"/> scales the native window rectangle
    /// into DIPs using the same DPI coefficient as <see cref="MonitorService.GetMonitors"/>.
    /// </summary>
    [Fact]
    public void GetWindowRectDip_ValidHandle_ScalesPhysicalPixelsToDip_UsingSameCoefficientAsGetMonitors()
    {
        // Arrange
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(2.0);
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
    /// consulting the DPI scale when the native <c>GetWindowRect</c> call itself reports failure.
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
        _nativeApiMock.Verify(api => api.GetSystemDpiScale(), Times.Never);
    }

    // ── Start/Dispose: hidden broadcast-window lifecycle ─────────────────────────

    /// <summary>
    /// Verifies that <see cref="MonitorService.Start"/> creates the hidden broadcast message window used
    /// to receive <c>WM_DISPLAYCHANGE</c>/<c>WM_SETTINGCHANGE</c>.
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
    /// Verifies that calling <see cref="MonitorService.Start"/> a second time after a successful first
    /// call is a no-op and does not create a second broadcast window.
    /// </summary>
    [Fact]
    public void Start_CalledTwice_WhenFirstSucceeded_CreatesWindowOnlyOnce()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(new IntPtr(1), 1, 0));

        // Act
        _service.Start();
        _service.Start();

        // Assert
        _nativeApiMock.Verify(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()), Times.Once);
    }

    /// <summary>
    /// Verifies that a failed broadcast-window creation is logged to <see cref="Console.Error"/> and
    /// degrades silently rather than throwing.
    /// </summary>
    [Fact]
    public void Start_WhenCreateMessageWindowFails_LogsToConsoleErrorAndDoesNotThrow()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Returns(new NativeMessageWindow(IntPtr.Zero, 0, 87));

        using var consoleOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(consoleOutput);

        try
        {
            // Act
            var ex = Record.Exception(() => _service.Start());

            // Assert
            Assert.Null(ex);
            Assert.Contains("MonitorService could not create its broadcast window", consoleOutput.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// Verifies that an exception thrown by the native window-registration call is caught, logged to
    /// <see cref="Console.Error"/>, and does not escape <see cref="MonitorService.Start"/>.
    /// </summary>
    [Fact]
    public void Start_WhenNativeApiThrows_LogsToConsoleErrorAndDoesNotThrow()
    {
        // Arrange
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Throws(new InvalidOperationException("RegisterClassEx failed"));

        using var consoleOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(consoleOutput);

        try
        {
            // Act
            var ex = Record.Exception(() => _service.Start());

            // Assert
            Assert.Null(ex);
            Assert.Contains("MonitorService.Start failed: RegisterClassEx failed", consoleOutput.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
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

    // ── WndProc: WM_DISPLAYCHANGE / WM_SETTINGCHANGE -> MonitorsChanged ──────────────────────────

    /// <summary>
    /// Verifies that a <c>WM_DISPLAYCHANGE</c> message delivered to the captured <c>NativeWindowProc</c>
    /// raises <see cref="MonitorService.MonitorsChanged"/>.
    /// </summary>
    [Fact]
    public void WndProc_DisplayChangeMessage_RaisesMonitorsChanged()
    {
        // Arrange
        var wndProc = CaptureWndProc();
        var raised = false;
        _service.MonitorsChanged += () => raised = true;

        // Act
        wndProc(new IntPtr(1), WmDisplayChange, IntPtr.Zero, IntPtr.Zero);

        // Assert
        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that a <c>WM_SETTINGCHANGE</c> message delivered to the captured <c>NativeWindowProc</c>
    /// also raises <see cref="MonitorService.MonitorsChanged"/>.
    /// </summary>
    [Fact]
    public void WndProc_SettingChangeMessage_RaisesMonitorsChanged()
    {
        // Arrange
        var wndProc = CaptureWndProc();
        var raised = false;
        _service.MonitorsChanged += () => raised = true;

        // Act
        wndProc(new IntPtr(1), WmSettingChange, IntPtr.Zero, IntPtr.Zero);

        // Assert
        Assert.True(raised);
    }

    /// <summary>
    /// Verifies that a message unrelated to display/setting changes (e.g. <c>WM_CLOSE</c>) does not raise
    /// <see cref="MonitorService.MonitorsChanged"/>.
    /// </summary>
    [Fact]
    public void WndProc_UnrelatedMessage_DoesNotRaiseMonitorsChanged()
    {
        // Arrange
        var wndProc = CaptureWndProc();
        var raised = false;
        _service.MonitorsChanged += () => raised = true;

        // Act — WM_CLOSE, unrelated to display/setting changes.
        wndProc(new IntPtr(1), 0x0010, IntPtr.Zero, IntPtr.Zero);

        // Assert
        Assert.False(raised);
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
    /// Verifies that an exception thrown by a <see cref="MonitorService.MonitorsChanged"/> subscriber is
    /// caught and logged, and that the WndProc still forwards to <see cref="INativeApi.DefaultWindowProc"/>.
    /// </summary>
    [Fact]
    public void WndProc_WhenSubscriberThrows_LogsAndStillCallsDefaultWindowProc()
    {
        // Arrange
        var wndProc = CaptureWndProc();
        _service.MonitorsChanged += () => throw new InvalidOperationException("boom");
        var expected = new IntPtr(444);
        _nativeApiMock
            .Setup(api => api.DefaultWindowProc(It.IsAny<IntPtr>(), WmDisplayChange, It.IsAny<IntPtr>(), It.IsAny<IntPtr>()))
            .Returns(expected);

        using var consoleOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(consoleOutput);

        IntPtr result;
        try
        {
            // Act
            result = wndProc(new IntPtr(1), WmDisplayChange, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Assert — a bug in a MonitorsChanged subscriber must never crash the WndProc.
        Assert.Equal(expected, result);
        Assert.Contains("MonitorService WndProc error: boom", consoleOutput.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────
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
