using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;
using Rectangle = System.Drawing.Rectangle;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Covers <see cref="MonitorService"/>'s release-hardening F2 wiring to <see cref="IDesktopScaleProvider"/>
/// (docs/PLANS/release-hardening-1.0-F-dpi.md §6 F2): the golden bit-identity of uniform snapshots with the
/// 0.6.1 <c>px / scale</c> formula, and the end-to-end <c>WM_DPICHANGED</c> → <see cref="IDesktopScaleProvider.Refresh"/>
/// → exactly one <see cref="IMonitorService.MonitorsChanged"/> path through a real
/// <see cref="DesktopScaleProvider"/> (mocked only at its own native/integration seams) — as opposed to
/// <see cref="MonitorServiceNativeApiTests"/>, which mocks <see cref="IDesktopScaleProvider"/> itself.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class MonitorServiceScaleTests
{
    private const int WmDpiChanged = 0x02E0;
    private const string Display1 = @"\\.\DISPLAY1";

    private readonly Mock<INativeApi> _nativeApiMock = new();
    private readonly Mock<IDesktopIntegrationService> _desktopIntegrationMock = new();
    private readonly Mock<IAppLog> _appLogMock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorServiceScaleTests"/> class.
    /// </summary>
    public MonitorServiceScaleTests()
    {
        // Default: no live panel, no WorkerW found -> TopLevel prediction (release-hardening F §3.2 step 2).
        _nativeApiMock.Setup(api => api.IsWindow(It.IsAny<IntPtr>())).Returns(false);
        _desktopIntegrationMock.Setup(d => d.FindDesktopLayerHost()).Returns(IntPtr.Zero);
    }

    // ── Golden test: uniform GetMonitors() is bit-identical to the 0.6.1 px/scale formula ────────

    /// <summary>
    /// Verifies that for a uniform (single-scale) snapshot — Embedded or TopLevel — <see cref="MonitorService.GetMonitors"/>
    /// produces the exact same DIP rectangle the 0.6.1 formula (<c>px / scale</c>, each component divided
    /// separately) produced, for a representative set of scales (F2 DoD #2).
    /// </summary>
    /// <param name="scale">The uniform DPI scale under test.</param>
    /// <param name="embedded">Whether the snapshot is built in Embedded mode.</param>
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    [InlineData(1.5, false)]
    [InlineData(2.0, false)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(1.5, true)]
    [InlineData(2.0, true)]
    public void GetMonitors_UniformSnapshot_IsBitIdenticalTo061Formula(double scale, bool embedded)
    {
        // Arrange
        var boundsPx = new Rectangle(0, 0, 3840, 2160);
        var workAreaPx = new Rectangle(0, 48, 3840, 2112);
        SetUpSingleMonitor(scale, boundsPx, workAreaPx);
        if (embedded)
        {
            var host = new IntPtr(0x1234);
            _desktopIntegrationMock.Setup(d => d.FindDesktopLayerHost()).Returns(host);
            _nativeApiMock.Setup(api => api.GetDpiScaleForWindow(host)).Returns(scale);
        }

        var provider = new DesktopScaleProvider(_nativeApiMock.Object, _desktopIntegrationMock.Object);
        var service = new MonitorService(_nativeApiMock.Object, provider, _appLogMock.Object);

        // 0.6.1 formula: every rectangle component divided independently by the same scale.
        var expectedBounds = new Rect(boundsPx.Left / scale, boundsPx.Top / scale, boundsPx.Width / scale, boundsPx.Height / scale);
        var expectedWorkArea = new Rect(workAreaPx.Left / scale, workAreaPx.Top / scale, workAreaPx.Width / scale, workAreaPx.Height / scale);

        // Act
        var monitors = service.GetMonitors();

        // Assert
        var m = Assert.Single(monitors);
        Assert.Equal(embedded ? DesktopLayerMode.Embedded : DesktopLayerMode.TopLevel, provider.Mode);
        Assert.True(provider.Current.IsUniform);
        Assert.Equal(expectedBounds, m.Bounds);
        Assert.Equal(expectedWorkArea, m.WorkArea);
    }

    // ── WM_DPICHANGED -> Refresh -> exactly one MonitorsChanged, Changed forwarded ────────────────

    /// <summary>
    /// Verifies the full chain (TopLevel mode): a <c>WM_DPICHANGED</c> message reaches
    /// <see cref="MonitorService"/>'s <c>WndProc</c>, which calls the real <see cref="DesktopScaleProvider"/>'s
    /// <see cref="IDesktopScaleProvider.Refresh"/>; because the scale actually changed, the provider raises
    /// <see cref="IDesktopScaleProvider.Changed"/> exactly once, and <see cref="MonitorService"/> forwards it
    /// as exactly one <see cref="IMonitorService.MonitorsChanged"/> — never twice.
    /// </summary>
    [Fact]
    public void WmDpiChanged_TopLevelMode_RefreshesProvider_AndRaisesMonitorsChangedExactlyOnce()
    {
        // Arrange
        SetUpSingleMonitor(1.0, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080));
        var provider = new DesktopScaleProvider(_nativeApiMock.Object, _desktopIntegrationMock.Object);
        Assert.Equal(DesktopLayerMode.TopLevel, provider.Current.Mode); // force initial build, no Changed yet

        var service = new MonitorService(_nativeApiMock.Object, provider, _appLogMock.Object);
        var wndProc = CaptureWndProc(service);

        var changedCount = 0;
        service.MonitorsChanged += () => changedCount++;

        // Act — the reference scale changes from 1.0 to 1.5 (simulates a real WM_DPICHANGED payload).
        SetUpSingleMonitor(1.5, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080));
        wndProc(new IntPtr(1), WmDpiChanged, IntPtr.Zero, IntPtr.Zero);

        // Assert
        Assert.Equal(1, changedCount);
        Assert.Equal(1.5, provider.Current.ReferenceScale);
    }

    /// <summary>
    /// Same as <see cref="WmDpiChanged_TopLevelMode_RefreshesProvider_AndRaisesMonitorsChangedExactlyOnce"/>
    /// but for Embedded mode (panels' root window DPI as the layer scale).
    /// </summary>
    [Fact]
    public void WmDpiChanged_EmbeddedMode_RefreshesProvider_AndRaisesMonitorsChangedExactlyOnce()
    {
        // Arrange
        var host = new IntPtr(0x2000);
        _desktopIntegrationMock.Setup(d => d.FindDesktopLayerHost()).Returns(host);
        _nativeApiMock.Setup(api => api.GetDpiScaleForWindow(host)).Returns(1.0);
        SetUpSingleMonitor(1.0, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080));

        var provider = new DesktopScaleProvider(_nativeApiMock.Object, _desktopIntegrationMock.Object);
        Assert.Equal(DesktopLayerMode.Embedded, provider.Current.Mode); // force initial build, no Changed yet

        var service = new MonitorService(_nativeApiMock.Object, provider, _appLogMock.Object);
        var wndProc = CaptureWndProc(service);

        var changedCount = 0;
        service.MonitorsChanged += () => changedCount++;

        // Act — the panel layer's root window is now reported at 1.25 (simulates the WorkerW itself having
        // changed DPI, e.g. the user moved the whole desktop layer to another monitor).
        _nativeApiMock.Setup(api => api.GetDpiScaleForWindow(host)).Returns(1.25);
        wndProc(new IntPtr(1), WmDpiChanged, IntPtr.Zero, IntPtr.Zero);

        // Assert
        Assert.Equal(1, changedCount);
        Assert.Equal(DesktopLayerMode.Embedded, provider.Current.Mode);
        Assert.Equal(1.25, provider.Current.LayerScale);
    }

    /// <summary>
    /// Verifies that a <c>WM_DPICHANGED</c> whose refresh finds nothing actually different does not raise
    /// <see cref="IMonitorService.MonitorsChanged"/> at all (no spurious re-layout) — the no-op counterpart
    /// of the "exactly once" assertions above.
    /// </summary>
    [Fact]
    public void WmDpiChanged_NothingActuallyChanged_DoesNotRaiseMonitorsChanged()
    {
        // Arrange
        SetUpSingleMonitor(1.0, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080));
        var provider = new DesktopScaleProvider(_nativeApiMock.Object, _desktopIntegrationMock.Object);
        _ = provider.Current; // force initial build

        var service = new MonitorService(_nativeApiMock.Object, provider, _appLogMock.Object);
        var wndProc = CaptureWndProc(service);

        var changedCount = 0;
        service.MonitorsChanged += () => changedCount++;

        // Act — same monitors, same scale: nothing for Refresh() to actually change.
        wndProc(new IntPtr(1), WmDpiChanged, IntPtr.Zero, IntPtr.Zero);

        // Assert
        Assert.Equal(0, changedCount);
    }

    /// <summary>
    /// Verifies that <see cref="IDesktopScaleProvider.Changed"/> raised from a source other than
    /// <see cref="MonitorService"/>'s own <c>WndProc</c> (e.g. <c>WindowManager.RecoverDesktopLayer</c>
    /// calling <see cref="IDesktopScaleProvider.Refresh"/> directly, or a window's own <c>OnDpiChanged</c>)
    /// is still forwarded as <see cref="IMonitorService.MonitorsChanged"/> — the provider is the single
    /// source of truth, not just the WndProc path.
    /// </summary>
    [Fact]
    public void ScaleProviderChanged_RaisedFromOutsideWndProc_IsStillForwardedAsMonitorsChanged()
    {
        // Arrange
        SetUpSingleMonitor(1.0, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080));
        var provider = new DesktopScaleProvider(_nativeApiMock.Object, _desktopIntegrationMock.Object);
        _ = provider.Current; // force initial build

        var service = new MonitorService(_nativeApiMock.Object, provider, _appLogMock.Object);
        service.Start(); // subscribes to provider.Changed; broadcast window creation is irrelevant here.

        var changedCount = 0;
        service.MonitorsChanged += () => changedCount++;

        // Act — simulate WindowManager.RecoverDesktopLayer() calling Refresh() directly after re-embedding,
        // with an actually different snapshot (scale changed underneath).
        SetUpSingleMonitor(1.25, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080));
        provider.Refresh();

        // Assert
        Assert.Equal(1, changedCount);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private void SetUpSingleMonitor(double scale, Rectangle boundsPx, Rectangle workAreaPx)
    {
        _nativeApiMock.Setup(api => api.GetSystemDpiScale()).Returns(scale);
        _nativeApiMock.Setup(api => api.GetMonitorInfos()).Returns([
            new NativeMonitorInfo(Display1, boundsPx, workAreaPx, IsPrimary: true, EffectiveDpi: (uint)Math.Round(scale * DesktopScaleModel.BaseDpi)),
        ]);
    }

    private NativeWindowProc CaptureWndProc(MonitorService service)
    {
        NativeWindowProc? captured = null;
        _nativeApiMock
            .Setup(api => api.CreateMessageWindow(It.IsAny<string>(), It.IsAny<NativeWindowProc>()))
            .Callback<string, NativeWindowProc>((_, proc) => captured = proc)
            .Returns(new NativeMessageWindow(new IntPtr(1), 1, 0));

        service.Start();

        Assert.NotNull(captured);
        return captured!;
    }
}
