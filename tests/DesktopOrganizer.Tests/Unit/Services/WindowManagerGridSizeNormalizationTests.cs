using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.State;
using Moq;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="WindowManager"/>'s ADR-011 Amendment (2026-09-24) grid-size normalization —
/// <see cref="WindowManager.NormalizeLegacyFreeformToGridSize"/> (restore/settings-change re-alignment of a
/// <see cref="PanelGeometryMode.LegacyFreeform"/> panel onto the CURRENT <c>IAppSettings.GridSize</c>
/// lattice) and <see cref="WindowManager.NormalizeGridAlignedToGridSize"/> (the commensurate-<c>GridSize</c>
/// counterpart for <see cref="PanelGeometryMode.GridAligned"/>). Pure model+monitor math, exercised the
/// same way <see cref="WindowManager.ClampIntoMonitor"/>/<see cref="WindowManager.ResolveGridAlignedBounds"/>
/// already are — the loops that drive these in production touch real WPF <c>ChildWindow</c> instances and
/// are manual verify (Win10/Win11).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class WindowManagerGridSizeNormalizationTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;
    private const string Display1 = @"\\.\DISPLAY1";

    private static MonitorInfo Primary(Rect? workArea = null) =>
        new(Display1, new Rect(0, 0, 1920, 1080), workArea ?? new Rect(0, 0, 1920, 1032), true);

    // ── NormalizeLegacyFreeformToGridSize — the user-reported repro ─────────────────────────────────

    [Fact]
    public void NormalizeLegacyFreeformToGridSize_UserRepro_PositionAndSizeMoveToTheNewLattice()
    {
        // Panel placed at (64,64)/64x64 while GridSize=32 (on that old lattice). GridSize is now 48.
        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new DrawingPoint(64, 64),
            Size = new DrawingSize(64, 64),
            ViewMode = PanelViewMode.List, // List mode -> flat GridMetrics.MinPanelSize (120) floor.
        };

        var changed = WindowManager.NormalizeLegacyFreeformToGridSize(model, Primary(), gridSize: 48);

        Assert.True(changed);
        Assert.Equal(new DrawingPoint(48, 48), model.Position);
        Assert.Equal(new DrawingSize(144, 144), model.Size); // 64 -> 48 (below the 120 floor) -> 144.
    }

    [Fact]
    public void NormalizeLegacyFreeformToGridSize_GridSizeOne_NeverChangesTheModel()
    {
        var model = new ChildWindowModel
        {
            Position = new DrawingPoint(65, 95),
            Size = new DrawingSize(64, 64),
        };

        var changed = WindowManager.NormalizeLegacyFreeformToGridSize(model, Primary(), gridSize: 1);

        Assert.False(changed);
        Assert.Equal(new DrawingPoint(65, 95), model.Position);
        Assert.Equal(new DrawingSize(64, 64), model.Size);
    }

    [Fact]
    public void NormalizeLegacyFreeformToGridSize_AlreadyOnTheLattice_ReportsNoChange()
    {
        var model = new ChildWindowModel
        {
            Position = new DrawingPoint(144, 144),
            Size = new DrawingSize(144, 144),
            ViewMode = PanelViewMode.List,
        };

        var changed = WindowManager.NormalizeLegacyFreeformToGridSize(model, Primary(), gridSize: 48);

        Assert.False(changed);
        Assert.Equal(new DrawingPoint(144, 144), model.Position);
        Assert.Equal(new DrawingSize(144, 144), model.Size);
    }

    [Fact]
    public void NormalizeLegacyFreeformToGridSize_GridModePanel_UsesTheIconGridDerivedWidthFloor()
    {
        // Grid mode at IconSize=48 has a LegacyStretch min width well above 120 (GridMetrics.MinPanelWidth)
        // -- normalizing a tiny persisted width must respect that floor, not the flat List-mode 120 floor.
        var model = new ChildWindowModel
        {
            Position = new DrawingPoint(0, 0),
            Size = new DrawingSize(64, 64),
            ViewMode = PanelViewMode.Grid,
            IconSize = 48,
        };
        var expectedMinWidth = GridMetrics.MinPanelWidth(
            48, model.IconLayoutPolicy, SystemParameters.VerticalScrollBarWidth);

        WindowManager.NormalizeLegacyFreeformToGridSize(model, Primary(), gridSize: 48);

        Assert.True(model.Size.Width >= expectedMinWidth);
        Assert.Equal(0d, model.Size.Width % 48);
    }

    [Fact]
    public void NormalizeLegacyFreeformToGridSize_NegativePosition_SnapsSymmetrically()
    {
        // A large enough work area that the position-clamp step never engages — isolates the negative
        // coordinate rounding itself (WindowManagerGridSizeNormalizationTests below covers the interplay
        // between a work-area clamp and a size that grows past its nearest-multiple during normalization).
        var model = new ChildWindowModel
        {
            Position = new DrawingPoint(-65, -65),
            Size = new DrawingSize(200, 200),
        };

        WindowManager.NormalizeLegacyFreeformToGridSize(model, Primary(new Rect(-5000, -5000, 10000, 10000)), gridSize: 48);

        Assert.Equal(-48, model.Position.X);
        Assert.Equal(-48, model.Position.Y);
    }

    [Fact]
    public void NormalizeLegacyFreeformToGridSize_ClampsIntoTheMonitorsWorkArea()
    {
        var model = new ChildWindowModel
        {
            Position = new DrawingPoint(1900, 100),
            Size = new DrawingSize(144, 144),
            ViewMode = PanelViewMode.List,
        };

        WindowManager.NormalizeLegacyFreeformToGridSize(model, Primary(new Rect(0, 0, 1920, 1032)), gridSize: 48);

        Assert.Equal(1776, model.Position.X); // 1920 - 144, already a 48-multiple.
    }

    // ── NormalizeGridAlignedToGridSize — only touches GridAligned when GridSize is commensurate with U ─

    private static ChildWindowModel GridPanel(int x, int y, int widthSpan, int heightSpan)
    {
        var size = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(widthSpan, heightSpan), U, G);
        return new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            MonitorDeviceId = Display1,
            WidthSpanUnits = widthSpan,
            HeightSpanUnits = heightSpan,
            Position = new DrawingPoint(x, y),
            Size = new DrawingSize((int)size.Width, (int)size.Height),
        };
    }

    [Fact]
    public void NormalizeGridAlignedToGridSize_GridSizeCommensurateWithU_RealignsAndRecomputesSpans()
    {
        // 48 is a whole multiple of U=8, so every 48-aligned coordinate is also on the U grid.
        var model = GridPanel(x: 64, y: 64, widthSpan: 20, heightSpan: 20); // 20*8-8=152 DIP, not a 48-multiple.
        var bounds = new Rect(64, 64, 152, 152);

        var result = WindowManager.NormalizeGridAlignedToGridSize(model, bounds, Primary(), gridSize: 48);

        Assert.Equal(0d, result.X % 48);
        Assert.Equal(0d, result.Y % 48);
        Assert.Equal(0d, result.Width % 48);
        Assert.Equal(0d, result.Height % 48);

        // Spans recomputed from the NEW size so the model stays self-consistent (ADR-028 invariant).
        var recomputedSize = WorkspaceLayoutPolicy.SpansToBounds(
            new GridSpan(model.WidthSpanUnits!.Value, model.HeightSpanUnits!.Value), U, G);
        Assert.Equal(result.Size, recomputedSize);
    }

    [Fact]
    public void NormalizeGridAlignedToGridSize_GridSizeNotAMultipleOfU_IsANoOp()
    {
        // 20 is not a whole multiple of U=8 -- forcing a GridAligned panel onto it would pull it off the
        // workspace grid it is locked to, so this must leave bounds/spans untouched.
        var model = GridPanel(x: 64, y: 64, widthSpan: 20, heightSpan: 20);
        var originalSpanW = model.WidthSpanUnits;
        var originalSpanH = model.HeightSpanUnits;
        var bounds = new Rect(64, 64, 152, 152);

        var result = WindowManager.NormalizeGridAlignedToGridSize(model, bounds, Primary(), gridSize: 20);

        Assert.Equal(bounds, result);
        Assert.Equal(originalSpanW, model.WidthSpanUnits);
        Assert.Equal(originalSpanH, model.HeightSpanUnits);
    }

    [Fact]
    public void NormalizeGridAlignedToGridSize_GridSizeOne_IsANoOp()
    {
        var model = GridPanel(x: 64, y: 64, widthSpan: 20, heightSpan: 20);
        var bounds = new Rect(64, 64, 152, 152);

        var result = WindowManager.NormalizeGridAlignedToGridSize(model, bounds, Primary(), gridSize: 1);

        Assert.Equal(bounds, result);
    }

    [Fact]
    public void NormalizeGridAlignedToGridSize_AlreadyOnTheGridSizeLattice_ReturnsBoundsUnchanged()
    {
        // 144x144 is already a multiple of 48 -- both grids agree, nothing should move.
        var model = GridPanel(x: 96, y: 96, widthSpan: 19, heightSpan: 19); // 19*8-8=144 DIP.
        var bounds = new Rect(96, 96, 144, 144);

        var result = WindowManager.NormalizeGridAlignedToGridSize(model, bounds, Primary(), gridSize: 48);

        Assert.Equal(bounds, result);
    }

    // ── ResolveMonitorPlacement / OnMonitorsChanged wiring — SaveWindowsState only when changed ──────

    private static WindowManager BuildWindowManager(int gridSize, out Mock<IApplicationStateService> stateMock)
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var monitorServiceMock = new Mock<IMonitorService>();
        var itemStorageMock = new Mock<IPanelItemStorageService>();
        stateMock = new Mock<IApplicationStateService>();
        stateMock.SetupGet(s => s.Panels).Returns([]);
        var appSettingsMock = new Mock<IAppSettings>();
        appSettingsMock.SetupGet(s => s.GridSize).Returns(gridSize);

        return new WindowManager(
            serviceProviderMock.Object,
            monitorServiceMock.Object,
            itemStorageMock.Object,
            stateMock.Object,
            appSettingsMock.Object);
    }

    [Fact]
    public void NormalizePanelsToGrid_NoOpenPanels_NeverSaves()
    {
        var manager = BuildWindowManager(gridSize: 48, out var stateMock);

        manager.NormalizePanelsToGrid();

        stateMock.Verify(s => s.SaveNow(), Times.Never);
    }

    [Fact]
    public void NormalizePanelsToGrid_GridSizeOne_NeverSaves()
    {
        var manager = BuildWindowManager(gridSize: 1, out var stateMock);

        var exception = Record.Exception(manager.NormalizePanelsToGrid);

        Assert.Null(exception);
        stateMock.Verify(s => s.SaveNow(), Times.Never);
    }
}
