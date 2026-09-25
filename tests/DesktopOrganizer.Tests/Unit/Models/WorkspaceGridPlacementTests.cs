using System.Windows;
using DesktopOrganizer.UI.Models;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="WorkspaceGridPlacement"/> — the explicit "Align to workspace grid" command
/// and the new-panel free-slot search (P03 §2.5/§2.6, ADR-028 «Явная команда»).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class WorkspaceGridPlacementTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    private static readonly Rect PrimaryWorkArea = new(0, 40, 1920, 1040);

    private static IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> Monitors =>
    [
        (Display1, new Rect(0, 0, 1920, 1080), PrimaryWorkArea),
        (Display2, new Rect(-1366, -200, 1366, 768), new Rect(-1366, -160, 1366, 728)),
    ];

    // ── ExpandedBounds: never let a collapsed/unmeasured panel define a span ──────────────────────

    /// <summary>
    /// Verifies that for a normal (not rolled-up) panel, <see cref="WorkspaceGridPlacement.ExpandedBounds"/>
    /// uses the given live measured size as-is.
    /// </summary>
    [Fact]
    public void ExpandedBounds_NormalPanel_UsesTheLiveSize()
    {
        var model = new ChildWindowModel { Size = new DrawingSize(300, 400) };

        var bounds = WorkspaceGridPlacement.ExpandedBounds(new Point(10, 20), new Size(248, 248), model);

        Assert.Equal(new Rect(10, 20, 248, 248), bounds);
    }

    /// <summary>
    /// Verifies that for a rolled-up panel, <see cref="WorkspaceGridPlacement.ExpandedBounds"/>
    /// substitutes the model's persisted expanded height for the live (collapsed) measured height,
    /// so a span is never derived from the collapsed header height.
    /// </summary>
    [Fact]
    public void ExpandedBounds_RolledUpPanel_SubstitutesThePersistedExpandedHeight()
    {
        // Regression guard: deriving a span from the collapsed header height would persist a ~4-unit
        // height span and the panel would restore permanently collapsed.
        var model = new ChildWindowModel { IsRolledUp = true, Size = new DrawingSize(248, 400) };

        var bounds = WorkspaceGridPlacement.ExpandedBounds(new Point(10, 20), new Size(248, 32), model);

        Assert.Equal(400d, bounds.Height);
        Assert.Equal(248d, bounds.Width);
    }

    /// <summary>
    /// Verifies that when the live measured size is degenerate (zero, i.e. never laid out),
    /// <see cref="WorkspaceGridPlacement.ExpandedBounds"/> falls back to the model's own persisted
    /// size.
    /// </summary>
    [Fact]
    public void ExpandedBounds_NeverMeasuredPanel_FallsBackToThePersistedSize()
    {
        var model = new ChildWindowModel { Size = new DrawingSize(248, 400) };

        var bounds = WorkspaceGridPlacement.ExpandedBounds(new Point(10, 20), new Size(0, 0), model);

        Assert.Equal(new Rect(10, 20, 248, 400), bounds);
    }

    /// <summary>
    /// Verifies that with neither a model nor a live measurement available,
    /// <see cref="WorkspaceGridPlacement.ExpandedBounds"/> returns a degenerate (zero-size) rectangle,
    /// and that <see cref="WorkspaceGridPlacement.TryAlign"/> correctly refuses that degenerate result.
    /// </summary>
    [Fact]
    public void ExpandedBounds_NoModelAndNoMeasurement_IsDegenerateSoCallersRefuse()
    {
        var bounds = WorkspaceGridPlacement.ExpandedBounds(new Point(10, 20), new Size(0, 0), model: null);

        Assert.Equal(0d, bounds.Width);
        Assert.Equal(0d, bounds.Height);
        Assert.False(WorkspaceGridPlacement.TryAlign(new ChildWindowModel(), bounds, Monitors, [], out _));
    }

    /// <summary>
    /// Verifies that a rolled-up panel with no persisted size stays degenerate (height zero) rather
    /// than falling back to persisting the collapsed live measurement as its span.
    /// </summary>
    [Fact]
    public void ExpandedBounds_RolledUpPanelWithoutPersistedSize_StaysDegenerateRatherThanCollapsing()
    {
        var model = new ChildWindowModel { IsRolledUp = true };

        var bounds = WorkspaceGridPlacement.ExpandedBounds(new Point(0, 0), new Size(248, 32), model);

        Assert.Equal(0d, bounds.Height); // refused by every caller, instead of persisting a 32 DIP span
    }

    // ── TryAlign ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridPlacement.TryAlign"/> switches the model to
    /// <see cref="PanelGeometryMode.GridAligned"/> and quantises the aligned position to the target
    /// monitor's own work-area origin.
    /// </summary>
    [Fact]
    public void TryAlign_SwitchesModeAndQuantisesPositionToTheMonitorOrigin()
    {
        var model = LegacyModel();
        var current = new Rect(103, 145, 250, 251);

        var ok = WorkspaceGridPlacement.TryAlign(model, current, Monitors, [], out var aligned);

        Assert.True(ok);
        Assert.Equal(PanelGeometryMode.GridAligned, model.GeometryMode);
        Assert.Equal(104d, aligned.Bounds.X);
        Assert.Equal(144d, aligned.Bounds.Y); // origin Y is 40, so the lines are 40 + k*8
        Assert.Equal(Display1, aligned.MonitorDeviceId);
    }

    /// <summary>
    /// Verifies that aligning rounds the size UP to whole grid spans, so the aligned bounds are never
    /// smaller than the original size in either dimension and match the span-derived bounds exactly.
    /// </summary>
    [Fact]
    public void TryAlign_RoundsSizeUpToWholeSpans_NeverClippingContent()
    {
        var model = LegacyModel();
        var current = new Rect(0, 40, 250, 251);

        Assert.True(WorkspaceGridPlacement.TryAlign(model, current, Monitors, [], out var aligned));

        Assert.True(aligned.Bounds.Width >= current.Width);
        Assert.True(aligned.Bounds.Height >= current.Height);
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(aligned.Span, U, G), aligned.Bounds.Size);
    }

    /// <summary>
    /// Verifies that aligning writes the resulting width/height span onto the model's own span
    /// properties, matching the aligned result exactly.
    /// </summary>
    [Fact]
    public void TryAlign_WritesSpansOntoTheModel_MatchingTheAlignedSize()
    {
        var model = LegacyModel();

        Assert.True(WorkspaceGridPlacement.TryAlign(model, new Rect(0, 40, 250, 251), Monitors, [], out var aligned));

        Assert.Equal(aligned.Span.WidthUnits, model.WidthSpanUnits);
        Assert.Equal(aligned.Span.HeightUnits, model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that aligning a tiny panel never produces a span below
    /// <see cref="WorkspaceGridMetrics.MinSpanExpanded"/> in either dimension.
    /// </summary>
    [Fact]
    public void TryAlign_NeverGoesBelowTheMinimumSpan()
    {
        var model = LegacyModel();
        var tiny = new Rect(0, 40, 20, 20);

        Assert.True(WorkspaceGridPlacement.TryAlign(model, tiny, Monitors, [], out var aligned));

        Assert.True(aligned.Span.WidthUnits >= WorkspaceGridMetrics.MinSpanExpanded.WidthUnits);
        Assert.True(aligned.Span.HeightUnits >= WorkspaceGridMetrics.MinSpanExpanded.HeightUnits);
    }

    /// <summary>
    /// Verifies that running <see cref="WorkspaceGridPlacement.TryAlign"/> a second time on its own
    /// output bounds produces the same bounds and span — alignment is idempotent.
    /// </summary>
    [Fact]
    public void TryAlign_IsIdempotent_RunningItTwiceChangesNothing()
    {
        var model = LegacyModel();
        var current = new Rect(103, 145, 250, 251);

        Assert.True(WorkspaceGridPlacement.TryAlign(model, current, Monitors, [], out var first));
        Assert.True(WorkspaceGridPlacement.TryAlign(model, first.Bounds, Monitors, [], out var second));

        Assert.Equal(first.Bounds, second.Bounds);
        Assert.Equal(first.Span, second.Span);
    }

    /// <summary>
    /// Verifies that aligning a panel whose current monitor has a negative-coordinate work area still
    /// quantises the result relative to that monitor's own origin.
    /// </summary>
    [Fact]
    public void TryAlign_NegativeCoordinateMonitor_AlignsToThatMonitorsOrigin()
    {
        var model = LegacyModel();
        model.MonitorDeviceId = Display2;
        var current = new Rect(-1000.4, -100.3, 250, 251);

        Assert.True(WorkspaceGridPlacement.TryAlign(model, current, Monitors, [], out var aligned));

        Assert.Equal(Display2, aligned.MonitorDeviceId);
        var offsetX = (aligned.Bounds.X - (-1366d)) / U;
        var offsetY = (aligned.Bounds.Y - (-160d)) / U;
        Assert.Equal(Math.Round(offsetX), offsetX, 9);
        Assert.Equal(Math.Round(offsetY), offsetY, 9);
    }

    /// <summary>
    /// Verifies that aligning a panel that would round up into a neighbour never introduces a NEW
    /// overlap it did not already have — ADR-028 accepts the panel not reaching its ideal slot, but
    /// never a new collision.
    /// </summary>
    [Fact]
    public void TryAlign_DoesNotTeleportIntoANeighbour_StopsAtTheWall()
    {
        var model = LegacyModel();
        var neighbour = new Rect(200, 40, 248, 248);
        var current = new Rect(100, 40, 250, 251); // already overlapping horizontally after rounding up

        Assert.True(WorkspaceGridPlacement.TryAlign(model, current, Monitors, [neighbour], out var aligned));

        // ADR-028: the panel may legitimately not reach its ideal slot, but it must never be placed so
        // that it NEWLY overlaps a neighbour it did not already overlap.
        var before = new Rect(current.TopLeft, aligned.Bounds.Size);
        Assert.False(PanelCollision.CreatesNewOverlap(before, aligned.Bounds, [neighbour]));
    }

    /// <summary>
    /// Verifies that aligning a panel hanging off the work area's edge pulls it back so the result
    /// stays fully inside the work area.
    /// </summary>
    [Fact]
    public void TryAlign_StaysInsideTheWorkArea()
    {
        var model = LegacyModel();
        var current = new Rect(1890, 1030, 250, 251);

        Assert.True(WorkspaceGridPlacement.TryAlign(model, current, Monitors, [], out var aligned));

        Assert.True(aligned.Bounds.Right <= PrimaryWorkArea.Right + 0.001);
        Assert.True(aligned.Bounds.Bottom <= PrimaryWorkArea.Bottom + 0.001);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridPlacement.TryAlign"/> refuses a degenerate (zero or NaN)
    /// size, returning false and restoring the model's geometry mode to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> rather than leaving it half-switched.
    /// </summary>
    /// <param name="width">A degenerate (zero or NaN) width to align against.</param>
    /// <param name="height">A degenerate (zero or NaN) height to align against.</param>
    [Theory]
    [InlineData(0d, 100d)]
    [InlineData(100d, 0d)]
    [InlineData(double.NaN, 100d)]
    public void TryAlign_DegenerateSize_RefusesAndRestoresTheMode(double width, double height)
    {
        var model = LegacyModel();

        var ok = WorkspaceGridPlacement.TryAlign(model, new Rect(0, 40, width, height), Monitors, [], out var aligned);

        Assert.False(ok);
        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
        Assert.Equal(default, aligned);
    }

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridPlacement.TryAlign"/> refuses when there are no monitors,
    /// returning false and restoring the model's geometry mode to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/>.
    /// </summary>
    [Fact]
    public void TryAlign_NoMonitors_RefusesAndRestoresTheMode()
    {
        var model = LegacyModel();

        Assert.False(WorkspaceGridPlacement.TryAlign(model, new Rect(0, 0, 250, 250), [], [], out _));
        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
    }

    // ── ClearAlignment (OQ-3 = B, the "uncheck" direction) ────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="WorkspaceGridPlacement.ClearAlignment"/> drops the model back to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/>, clears its width/height spans, and leaves the
    /// model's own position/size bounds untouched.
    /// </summary>
    [Fact]
    public void ClearAlignment_DropsToLegacyFreeform_ClearsSpans_AndLeavesBoundsUntouched()
    {
        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            WidthSpanUnits = 31,
            HeightSpanUnits = 32,
            Position = new DrawingPoint(104, 144),
            Size = new DrawingSize(248, 248),
        };

        WorkspaceGridPlacement.ClearAlignment(model);

        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
        Assert.Null(model.WidthSpanUnits);
        Assert.Null(model.HeightSpanUnits);
        Assert.Equal(new DrawingPoint(104, 144), model.Position);
        Assert.Equal(new DrawingSize(248, 248), model.Size);
    }

    // ── FindFreeSlot (P03-L3 / baseline M8) ───────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that on an empty workspace, <see cref="WorkspaceGridPlacement.FindFreeSlot"/> returns
    /// the work area's own top-left origin as the first free slot.
    /// </summary>
    [Fact]
    public void FindFreeSlot_EmptyWorkspace_ReturnsTheOrigin()
    {
        var slot = WorkspaceGridPlacement.FindFreeSlot(PrimaryWorkArea, new Size(248, 248), U, G, []);

        Assert.Equal(PrimaryWorkArea.TopLeft, slot);
    }

    /// <summary>
    /// Verifies that when the origin slot is already occupied, the next free slot found leaves at
    /// least the required gap, stays on the grid, and stays inside the work area.
    /// </summary>
    [Fact]
    public void FindFreeSlot_OriginTaken_ReturnsTheNextSlotWithAtLeastTheGap()
    {
        var occupied = new Rect(PrimaryWorkArea.TopLeft, new Size(248, 248));

        var slot = WorkspaceGridPlacement.FindFreeSlot(PrimaryWorkArea, new Size(248, 248), U, G, [occupied]);

        Assert.NotNull(slot);
        var placed = new Rect(slot!.Value, new Size(248, 248));
        Assert.False(PanelCollision.Overlaps(placed, occupied));
        Assert.True(placed.X - occupied.Right >= G - 0.001, $"expected at least {G} DIP of clearance, got {placed.X - occupied.Right}");

        // Still on the grid, still inside the work area.
        Assert.Equal(0d, (placed.X - PrimaryWorkArea.X) % U, 9);
        Assert.True(placed.Right <= PrimaryWorkArea.Right);
    }

    /// <summary>
    /// Verifies that repeatedly placing panels via <see cref="WorkspaceGridPlacement.FindFreeSlot"/>,
    /// feeding each result back as an obstacle for the next call, never produces an overlapping
    /// placement.
    /// </summary>
    [Fact]
    public void FindFreeSlot_SequentialPlacements_NeverOverlap()
    {
        var size = new Size(248, 248);
        var placed = new List<Rect>();

        for (var i = 0; i < 12; i++)
        {
            var slot = WorkspaceGridPlacement.FindFreeSlot(PrimaryWorkArea, size, U, G, placed);
            Assert.NotNull(slot);

            var rect = new Rect(slot!.Value, size);
            foreach (var other in placed)
                Assert.False(PanelCollision.Overlaps(rect, other));

            placed.Add(rect);
        }
    }

    /// <summary>
    /// Verifies that calling <see cref="WorkspaceGridPlacement.FindFreeSlot"/> twice with identical
    /// inputs returns the exact same slot.
    /// </summary>
    [Fact]
    public void FindFreeSlot_IsDeterministic_SameInputSameAnswer()
    {
        var size = new Size(248, 248);
        var obstacles = new[] { new Rect(0, 40, 248, 248), new Rect(400, 400, 248, 248) };

        var first = WorkspaceGridPlacement.FindFreeSlot(PrimaryWorkArea, size, U, G, obstacles);
        var second = WorkspaceGridPlacement.FindFreeSlot(PrimaryWorkArea, size, U, G, obstacles);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Verifies that a panel size larger than the entire work area makes
    /// <see cref="WorkspaceGridPlacement.FindFreeSlot"/> return null rather than an out-of-bounds slot.
    /// </summary>
    [Fact]
    public void FindFreeSlot_PanelLargerThanTheWorkArea_ReturnsNull()
    {
        var slot = WorkspaceGridPlacement.FindFreeSlot(new Rect(0, 0, 200, 200), new Size(400, 400), U, G, []);

        Assert.Null(slot);
    }

    /// <summary>
    /// Verifies that when a single obstacle covers the entire work area,
    /// <see cref="WorkspaceGridPlacement.FindFreeSlot"/> returns null.
    /// </summary>
    [Fact]
    public void FindFreeSlot_NoRoomLeft_ReturnsNull()
    {
        var workArea = new Rect(0, 0, 300, 300);
        var occupied = new Rect(-100, -100, 500, 500); // covers everything

        Assert.Null(WorkspaceGridPlacement.FindFreeSlot(workArea, new Size(248, 248), U, G, [occupied]));
    }

    /// <summary>
    /// Verifies that on a monitor with a negative-coordinate work area,
    /// <see cref="WorkspaceGridPlacement.FindFreeSlot"/> returns that work area's own origin as the
    /// first free slot, staying on that monitor's grid.
    /// </summary>
    [Fact]
    public void FindFreeSlot_NegativeOriginMonitor_StaysOnThatMonitorsGrid()
    {
        var workArea = new Rect(-1366, -160, 1366, 728);

        var slot = WorkspaceGridPlacement.FindFreeSlot(workArea, new Size(248, 248), U, G, []);

        Assert.Equal(workArea.TopLeft, slot);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static ChildWindowModel LegacyModel() => new() { GeometryMode = PanelGeometryMode.LegacyFreeform };
}
