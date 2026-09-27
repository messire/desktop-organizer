using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;
using Rectangle = System.Drawing.Rectangle;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for release-hardening F4 (docs/PLANS/release-hardening-1.0-F-dpi.md §7
/// "WindowManagerPlacementScaleTests", ADR-037): the desktop-DIP placement math
/// (<see cref="WindowManager.ClampIntoMonitor"/>, <see cref="WindowManager.NormalizeLegacyFreeformToGridSize"/>,
/// <see cref="WindowManager.ResolveGridAlignedBounds"/>, <see cref="WindowManager.PlaceNewPanelOnFreeSlot"/>) is
/// deliberately UNCHANGED by F (plan §5 #16) — what F changes is only where its <see cref="MonitorInfo"/>
/// input comes from: <see cref="DesktopScaleModel.GetBoundsDip"/>/<see cref="DesktopScaleModel.GetWorkAreaDip"/>
/// instead of a flat <c>px / systemDpiScale</c> division. These tests feed that math
/// <see cref="MonitorInfo"/> built the same way <see cref="DesktopOrganizer.Services.Tools.MonitorService.GetMonitors"/>
/// does, across uniform and anchored (mixed-DPI, two-monitor) snapshots, and verify a panel started on the
/// non-primary monitor stays on that monitor, inside its own work area — never silently reassigned to the
/// primary's numbers.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class WindowManagerPlacementScaleTests
{
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    /// <summary>The five scale-set scenarios the plan calls out (§7).</summary>
    public enum ScaleLayout
    {
        /// <summary>One monitor at 100%.</summary>
        Uniform100,

        /// <summary>One monitor at 150%.</summary>
        Uniform150,

        /// <summary>100% primary + 150% to the right.</summary>
        MixedRight,

        /// <summary>150% primary + 100% to the left (negative px, negative DIP).</summary>
        MixedLeft,

        /// <summary>100% primary + 150% above (negative px, negative DIP).</summary>
        MixedTop,
    }

    /// <summary>Every layout under test.</summary>
    public static TheoryData<ScaleLayout> Layouts => new()
    {
        ScaleLayout.Uniform100,
        ScaleLayout.Uniform150,
        ScaleLayout.MixedRight,
        ScaleLayout.MixedLeft,
        ScaleLayout.MixedTop,
    };

    /// <summary>Only the layouts with two monitors — used by the <c>FindMonitorAt</c> uniqueness test.</summary>
    public static TheoryData<ScaleLayout> MixedLayouts => new()
    {
        ScaleLayout.MixedRight,
        ScaleLayout.MixedLeft,
        ScaleLayout.MixedTop,
    };

    // ── ClampIntoMonitor: a panel far off its monitor clamps into THAT monitor's own work area ──────

    /// <summary>
    /// Verifies that <see cref="WindowManager.ClampIntoMonitor"/>, given a <see cref="MonitorInfo"/> built
    /// from a (possibly anchored, mixed-DPI) <see cref="DesktopScaleModel"/> snapshot, clamps a far-off-screen
    /// panel back inside that specific monitor's own work area — not the primary's.
    /// </summary>
    /// <param name="layout">The scale layout under test.</param>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void ClampIntoMonitor_PanelFarOffTheMonitor_ClampsWithinItsOwnWorkArea(ScaleLayout layout)
    {
        var monitors = BuildMonitorInfos(layout);
        var target = Target(monitors);

        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new DrawingPoint((int)target.Bounds.Right + 5000, (int)target.Bounds.Bottom + 5000),
            Size = new DrawingSize(300, 200),
        };

        WindowManager.ClampIntoMonitor(model, target);

        AssertInsideWorkArea(model, target);
    }

    // ── NormalizeLegacyFreeformToGridSize: re-lattice keeps the panel on its own monitor ─────────────

    /// <summary>
    /// Verifies that <see cref="WindowManager.NormalizeLegacyFreeformToGridSize"/> re-aligns a panel onto
    /// the current grid-size lattice while keeping it inside the SAME monitor's work area, whatever the
    /// monitor's own DPI scale or DIP origin (including a monitor with negative DIP coordinates).
    /// </summary>
    /// <param name="layout">The scale layout under test.</param>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void NormalizeLegacyFreeformToGridSize_PanelOnItsMonitor_StaysWithinThatMonitorsWorkArea(ScaleLayout layout)
    {
        var monitors = BuildMonitorInfos(layout);
        var target = Target(monitors);

        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new DrawingPoint((int)target.WorkArea.Left + 13, (int)target.WorkArea.Top + 27),
            Size = new DrawingSize(233, 177),
        };

        WindowManager.NormalizeLegacyFreeformToGridSize(model, target, gridSize: 48);

        AssertInsideWorkArea(model, target);
    }

    // ── ResolveGridAlignedBounds: the GridAligned half of ResolveMonitorPlacement ───────────────────

    /// <summary>
    /// Verifies that <see cref="WindowManager.ResolveGridAlignedBounds"/> — the
    /// <see cref="PanelGeometryMode.GridAligned"/> half of <c>ResolveMonitorPlacement</c> — resolves bounds
    /// inside the SAME monitor's work area (in <c>Window.Left</c>/<c>Top</c> space) it was given, across
    /// every scale layout.
    /// </summary>
    /// <param name="layout">The scale layout under test.</param>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void ResolveGridAlignedBounds_PanelOnItsMonitor_KeepsBoundsOnThatMonitor(ScaleLayout layout)
    {
        var monitors = BuildMonitorInfos(layout);
        var target = Target(monitors);

        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            MonitorDeviceId = target.DeviceName,
            Position = new DrawingPoint((int)target.WorkArea.Left + 40, (int)target.WorkArea.Top + 40),
            Size = new DrawingSize(240, 160),
        };

        var bounds = WindowManager.ResolveGridAlignedBounds(model, target);

        Assert.NotNull(bounds);
        var workAreaWindowSpace = DesktopCoordinateSpace.ToWindowSpace(target.WorkArea);
        Assert.InRange(bounds!.Value.Left, workAreaWindowSpace.Left - 0.5, workAreaWindowSpace.Right + 0.5);
        Assert.InRange(bounds.Value.Top, workAreaWindowSpace.Top - 0.5, workAreaWindowSpace.Bottom + 0.5);
    }

    // ── PlaceNewPanelOnFreeSlot: a new panel started on the secondary monitor stays there ───────────

    /// <summary>
    /// Verifies that <see cref="WindowManager.PlaceNewPanelOnFreeSlot"/> — driven by an
    /// <see cref="IMonitorService"/> whose <see cref="MonitorInfo"/> list came from a (possibly anchored,
    /// mixed-DPI) <see cref="DesktopScaleModel"/> snapshot — places a new
    /// <see cref="PanelGeometryMode.GridAligned"/> panel started on the non-primary monitor onto a free
    /// slot of THAT monitor, never the primary's.
    /// </summary>
    /// <param name="layout">The scale layout under test.</param>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void PlaceNewPanelOnFreeSlot_PanelStartedOnItsMonitor_LandsOnTheSameMonitor(ScaleLayout layout)
    {
        var monitors = BuildMonitorInfos(layout);
        var target = Target(monitors);
        var manager = BuildWindowManager(monitors);

        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            Position = new DrawingPoint((int)target.WorkArea.Left + 10, (int)target.WorkArea.Top + 10),
            Size = new DrawingSize(240, 160),
        };

        manager.PlaceNewPanelOnFreeSlot(model);

        Assert.Equal(target.DeviceName, model.MonitorDeviceId);
        Assert.InRange(model.Position.X, target.WorkArea.Left - 1, target.WorkArea.Right + 1);
        Assert.InRange(model.Position.Y, target.WorkArea.Top - 1, target.WorkArea.Bottom + 1);
    }

    // ── FindMonitorAt: an anchored layout's DIP rectangles never overlap, so every probe is unambiguous ──

    /// <summary>
    /// Verifies that on an anchored (mixed-DPI) layout, a point deep inside each monitor's own
    /// <see cref="MonitorInfo.Bounds"/> is contained by exactly that monitor's rectangle and no other —
    /// the same <c>Bounds.Contains</c> test <see cref="DesktopOrganizer.Services.Tools.MonitorService.FindMonitorAt"/>
    /// performs — proving the anchored DIP rectangles built from <see cref="DesktopScaleModel"/> don't overlap.
    /// </summary>
    /// <param name="layout">The mixed-scale layout under test.</param>
    [Theory]
    [MemberData(nameof(MixedLayouts))]
    public void FindMonitorAt_AnchoredLayout_EveryProbePointResolvesToExactlyOneMonitor(ScaleLayout layout)
    {
        var monitors = BuildMonitorInfos(layout);

        foreach (var monitor in monitors)
        {
            var probe = new Point(
                monitor.Bounds.Left + (monitor.Bounds.Width / 2),
                monitor.Bounds.Top + (monitor.Bounds.Height / 2));

            var matches = monitors.Where(m => m.Bounds.Contains(probe)).ToList();

            var only = Assert.Single(matches);
            Assert.Equal(monitor.DeviceName, only.DeviceName);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static void AssertInsideWorkArea(ChildWindowModel model, MonitorInfo monitor)
    {
        var area = monitor.WorkArea;
        var width = model.Size.Width;
        var height = model.Size.Height;

        Assert.True(
            model.Position.X >= area.Left - 0.5 && model.Position.X + width <= area.Right + 0.5,
            $"Position.X={model.Position.X} width={width} not inside work area {area} of {monitor.DeviceName}");
        Assert.True(
            model.Position.Y >= area.Top - 0.5 && model.Position.Y + height <= area.Bottom + 0.5,
            $"Position.Y={model.Position.Y} height={height} not inside work area {area} of {monitor.DeviceName}");
    }

    /// <summary>The monitor a panel "started on": the non-primary one for a two-monitor layout, otherwise the only one.</summary>
    private static MonitorInfo Target(IReadOnlyList<MonitorInfo> monitors)
        => monitors.FirstOrDefault(m => !m.IsPrimary) ?? monitors[0];

    /// <summary>
    /// Builds <see cref="MonitorInfo"/> the same way <see cref="DesktopOrganizer.Services.Tools.MonitorService.GetMonitors"/>
    /// does: <c>new MonitorInfo(name, current.GetBoundsDip(name), current.GetWorkAreaDip(name), isPrimary)</c>.
    /// </summary>
    private static IReadOnlyList<MonitorInfo> BuildMonitorInfos(ScaleLayout layout)
    {
        var (mode, layerScale, referenceScale, scales) = BuildScales(layout);
        var model = DesktopScaleModel.Create(mode, layerScale, referenceScale, scales);

        return scales
            .Select(m => new MonitorInfo(m.DeviceName, model.GetBoundsDip(m.DeviceName), model.GetWorkAreaDip(m.DeviceName), m.IsPrimary))
            .ToList();
    }

    private static (DesktopLayerMode Mode, double LayerScale, double ReferenceScale, MonitorScale[] Monitors) BuildScales(ScaleLayout layout) => layout switch
    {
        ScaleLayout.Uniform100 => (DesktopLayerMode.TopLevel, 1d, 1d, new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 1920, 1080), true, 1d),
        }),
        ScaleLayout.Uniform150 => (DesktopLayerMode.TopLevel, 1.5, 1.5, new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 2880, 1620), true, 1.5),
        }),
        ScaleLayout.MixedRight => (DesktopLayerMode.TopLevel, 1d, 1d, new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 1920, 1080), true, 1d),
            Monitor(Display2, new Rectangle(1920, 0, 3840, 2160), false, 1.5),
        }),
        ScaleLayout.MixedLeft => (DesktopLayerMode.TopLevel, 1.5, 1.5, new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 2880, 1620), true, 1.5),
            Monitor(Display2, new Rectangle(-1920, 0, 1920, 1080), false, 1d),
        }),
        ScaleLayout.MixedTop => (DesktopLayerMode.TopLevel, 1d, 1d, new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 1920, 1080), true, 1d),
            Monitor(Display2, new Rectangle(0, -2160, 3840, 2160), false, 1.5),
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, message: null),
    };

    /// <summary>A monitor with an 40-DIP-tall taskbar carved out of the top of its work area (scaled to px).</summary>
    private static MonitorScale Monitor(string name, Rectangle bounds, bool primary, double scale)
        => new(name, bounds, new Rectangle(bounds.X, bounds.Y + (int)(40 * scale), bounds.Width, bounds.Height - (int)(40 * scale)), primary, scale);

    private static WindowManager BuildWindowManager(IReadOnlyList<MonitorInfo> monitors)
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var monitorServiceMock = new Mock<IMonitorService>();
        monitorServiceMock
            .Setup(m => m.FindMonitorAt(It.IsAny<Point>()))
            .Returns((Point p) => monitors.FirstOrDefault(m => m.Bounds.Contains(p)));
        monitorServiceMock.Setup(m => m.GetPrimaryMonitor()).Returns(monitors.First(m => m.IsPrimary));

        var itemStorageMock = new Mock<IPanelItemStorageService>();
        var applicationStateServiceMock = new Mock<IApplicationStateService>();
        applicationStateServiceMock.SetupGet(s => s.Panels).Returns([]);
        var appSettingsMock = new Mock<IAppSettings>();
        appSettingsMock.SetupGet(s => s.GridSize).Returns(1);

        return new WindowManager(
            serviceProviderMock.Object,
            monitorServiceMock.Object,
            itemStorageMock.Object,
            applicationStateServiceMock.Object,
            appSettingsMock.Object,
            FakeDesktopScale.Uniform().Object);
    }
}
