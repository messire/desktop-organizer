using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="WindowManager.ResolveGridAlignedBounds"/> — the
/// <see cref="PanelGeometryMode.GridAligned"/> half of panel restore (<c>ResolveMonitorPlacement</c>,
/// P03-W1) and of <c>OnMonitorsChanged</c> (P03-W3). Pure model+monitor math, tested directly for the
/// same reason <see cref="WindowManager.ClampIntoMonitor"/> already is: the loops that drive it touch
/// real WPF <c>ChildWindow</c> instances and are manual verify (Win10/Win11).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class WindowManagerGridPlacementTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    private static MonitorInfo Primary(Rect? workArea = null) =>
        new(Display1, new Rect(0, 0, 1920, 1080), workArea ?? new Rect(0, 40, 1920, 1040), true);

    /// <summary>A monitor placed left of and above the primary — negative DIP coordinates (ADR-028).</summary>
    private static MonitorInfo NegativeSecondary() =>
        new(Display2, new Rect(-1366, -200, 1366, 768), new Rect(-1366, -160, 1366, 728), false);

    private static ChildWindowModel GridPanel(int x, int y, int widthSpan = 31, int heightSpan = 31)
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

    // ── Инвариант В1: only GridAligned panels take this path ──────────────────────────────────────

    [Fact]
    public void ResolveGridAlignedBounds_LegacyFreeformPanel_ReturnsNull_SoTheUntouchedClampPathRuns()
    {
        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            Position = new DrawingPoint(37, 91),
            Size = new DrawingSize(300, 400),
        };

        Assert.Null(WindowManager.ResolveGridAlignedBounds(model, Primary()));

        // D3-4(a): the model is not touched at all on this path.
        Assert.Equal(new DrawingPoint(37, 91), model.Position);
        Assert.Equal(new DrawingSize(300, 400), model.Size);
    }

    [Fact]
    public void ResolveGridAlignedBounds_GridAlignedWithoutSpansAndWithoutSize_ReturnsNull()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };

        Assert.Null(WindowManager.ResolveGridAlignedBounds(model, Primary()));
    }

    [Fact]
    public void ResolveGridAlignedBounds_GridAlignedWithoutSpansButWithSize_MigratesSpansAndPlaces()
    {
        // The исход (b) case from plan §0.1: a document written before spans existed.
        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            MonitorDeviceId = Display1,
            Position = new DrawingPoint(104, 144),
            Size = new DrawingSize(248, 248),
        };

        var bounds = WindowManager.ResolveGridAlignedBounds(model, Primary());

        Assert.NotNull(bounds);
        Assert.NotNull(model.WidthSpanUnits);
        Assert.NotNull(model.HeightSpanUnits);
        Assert.Equal(
            WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(model.WidthSpanUnits!.Value, model.HeightSpanUnits!.Value), U, G),
            bounds!.Value.Size);
    }

    // ── D3-4: no jump on first launch ─────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveGridAlignedBounds_PanelAlreadyOnTheGrid_ReturnsExactlyItsSavedBounds()
    {
        var model = GridPanel(x: 104, y: 144);
        var savedPosition = model.Position;
        var savedSize = model.Size;

        var bounds = WindowManager.ResolveGridAlignedBounds(model, Primary());

        Assert.NotNull(bounds);
        Assert.Equal(savedPosition.X, bounds!.Value.X);
        Assert.Equal(savedPosition.Y, bounds.Value.Y);
        Assert.Equal(savedSize.Width, bounds.Value.Width);
        Assert.Equal(savedSize.Height, bounds.Value.Height);
    }

    [Fact]
    public void ResolveGridAlignedBounds_RepeatedResolution_IsIdempotent()
    {
        // R-9: WM_SETTINGCHANGE raises MonitorsChanged for something as ordinary as a theme change, so
        // re-resolving an unchanged configuration must not move the panel by a single DIP.
        var model = GridPanel(x: 104, y: 144);

        var first = WindowManager.ResolveGridAlignedBounds(model, Primary());
        Assert.NotNull(first);
        model.Position = new DrawingPoint((int)first!.Value.X, (int)first.Value.Y);
        model.Size = new DrawingSize((int)first.Value.Width, (int)first.Value.Height);

        var second = WindowManager.ResolveGridAlignedBounds(model, Primary());

        Assert.Equal(first, second);
    }

    [Fact]
    public void ResolveGridAlignedBounds_OffGridSavedPosition_SnapsWithinHalfAUnit()
    {
        var model = GridPanel(x: 104, y: 144);
        model.Position = new DrawingPoint(107, 147); // e.g. created by a lasso before P03

        var bounds = WindowManager.ResolveGridAlignedBounds(model, Primary());

        Assert.NotNull(bounds);
        Assert.True(Math.Abs(bounds!.Value.X - 107) <= U / 2d);
        Assert.True(Math.Abs(bounds.Value.Y - 147) <= U / 2d);
    }

    // ── D3-3: the panel keeps its slot on its own monitor ─────────────────────────────────────────

    /// <summary>
    /// A taskbar appears at the top of the monitor: the work area's origin moves from <c>y=40</c> to
    /// <c>y=88</c>, a whole number of units. The panel stays on the SAME monitor, stays on the grid of
    /// the NEW origin, and — because the shift is grid-aligned — does not move at all.
    /// </summary>
    /// <remarks>
    /// <b>Scope note (P03).</b> ADR-028 describes a <see cref="PanelGeometryMode.GridAligned"/> panel's
    /// position as "stored as slots from the origin", which would make it follow a work-area shift
    /// (keeping its logical slot index). The persisted schema (v5, owned by P02b) stores spans for the
    /// SIZE only — the POSITION is still an absolute DIP point — and the previous origin is not recorded
    /// anywhere, so a logical-slot re-derivation is not computable from a restored document. P03
    /// therefore implements plan §2.4's actual formula, <c>SnapPosition(model.Position, newOrigin, U)</c>:
    /// the panel keeps its ABSOLUTE position (re-quantised to the new origin's grid, so it moves by at
    /// most half a unit), which also happens to be the behavior that best satisfies the "no jump" DoD.
    /// Recorded as an open question for a future schema change rather than silently resolved here.
    /// </remarks>
    [Fact]
    public void ResolveGridAlignedBounds_WorkAreaOriginShiftsByWholeUnits_PanelDoesNotMove()
    {
        var model = GridPanel(x: 104, y: 144);

        var bounds = WindowManager.ResolveGridAlignedBounds(model, Primary(new Rect(0, 88, 1920, 992)));

        Assert.NotNull(bounds);
        Assert.Equal(104d, bounds!.Value.X);
        Assert.Equal(144d, bounds.Value.Y);
        Assert.Equal(Display1, model.MonitorDeviceId); // never relocated (D3-3)
    }

    [Fact]
    public void ResolveGridAlignedBounds_WorkAreaOriginShiftsOffGrid_PanelReQuantisesWithinHalfAUnit()
    {
        var model = GridPanel(x: 104, y: 144);

        var bounds = WindowManager.ResolveGridAlignedBounds(model, Primary(new Rect(0, 85, 1920, 995)));

        Assert.NotNull(bounds);
        Assert.True(Math.Abs(bounds!.Value.Y - 144d) <= U / 2d);
        Assert.Equal(0d, (bounds.Value.Y - 85d) % U, 9); // on the NEW origin's grid
    }

    [Fact]
    public void ResolveGridAlignedBounds_WorkAreaShrinksBelowThePanel_ClampsPositionOnGrid_NeverTheSize()
    {
        var model = GridPanel(x: 1000, y: 800);
        var savedSize = model.Size;

        var bounds = WindowManager.ResolveGridAlignedBounds(model, Primary(new Rect(0, 40, 800, 500)));

        Assert.NotNull(bounds);
        Assert.Equal(savedSize.Width, bounds!.Value.Width);   // never shrinks the panel
        Assert.Equal(savedSize.Height, bounds.Value.Height);
        Assert.Equal(0d, (bounds.Value.X - 0d) % U, 9);        // still on the grid
        Assert.Equal(0d, (bounds.Value.Y - 40d) % U, 9);
    }

    [Fact]
    public void ResolveGridAlignedBounds_NegativeCoordinateMonitor_PlacesOnThatMonitorsGrid()
    {
        var model = GridPanel(x: -1000, y: -100);
        model.MonitorDeviceId = Display2;
        var monitor = NegativeSecondary();

        var bounds = WindowManager.ResolveGridAlignedBounds(model, monitor);

        Assert.NotNull(bounds);
        Assert.Equal(0d, (bounds!.Value.X - monitor.WorkArea.X) % U, 9);
        Assert.Equal(0d, (bounds.Value.Y - monitor.WorkArea.Y) % U, 9);
        Assert.True(bounds.Value.X >= monitor.WorkArea.X);
        Assert.True(bounds.Value.Y >= monitor.WorkArea.Y);
        Assert.True(bounds.Value.Right <= monitor.WorkArea.Right + 0.001);
    }

    [Fact]
    public void ResolveGridAlignedBounds_UsesTheMonitorItIsGiven_NotTheOneOnTheModel()
    {
        // The caller (ResolveMonitorPlacement/OnMonitorsChanged) has already applied the "stored monitor
        // gone -> primary" rule (M4); this method must never second-guess it.
        var model = GridPanel(x: 104, y: 144);
        model.MonitorDeviceId = "\\\\.\\DISPLAY_GONE";

        var bounds = WindowManager.ResolveGridAlignedBounds(model, NegativeSecondary());

        Assert.NotNull(bounds);
        Assert.True(bounds!.Value.X >= NegativeSecondary().WorkArea.X);
        Assert.True(bounds.Value.Y >= NegativeSecondary().WorkArea.Y);
    }

    // ── D3-5: drag -> persist -> restore round-trip ───────────────────────────────────────────────

    [Fact]
    public void ResolveGridAlignedBounds_AfterADropAndPersist_RestoresTheSameBounds()
    {
        var state = new PanelGridState(
            IsGridAligned: true,
            Origin: new Point(0, 40),
            WorkArea: new Rect(0, 40, 1920, 1040),
            UnitSize: U,
            Gap: G,
            MinSpan: WorkspaceGridMetrics.MinSpanExpanded,
            MonitorDeviceId: Display1);

        var model = GridPanel(x: 0, y: 40);
        var dropped = PanelDragPipeline.ResolveGridDrop(
            new Rect(517.4, 311.9, model.Size.Width, model.Size.Height), state, []);

        // ... persisted exactly the way PanelGeometryWriter would ...
        model.Position = new DrawingPoint((int)dropped.X, (int)dropped.Y);

        var restored = WindowManager.ResolveGridAlignedBounds(model, Primary());

        Assert.NotNull(restored);
        Assert.Equal(dropped.X, restored!.Value.X);
        Assert.Equal(dropped.Y, restored.Value.Y);
    }
}
