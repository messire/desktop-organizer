using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// P04 §3.5 (docs/PLANS/workspace-grid-03-04-integration.md): the pure geometry behind the
/// "Size → Small/Medium/Large/Extra large" command.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelSizePresetCalculatorTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    private static readonly Rect OnGridCurrent = new(96d, 200d, 320d, 240d);

    /// <summary>Supplies every <see cref="PanelSizePreset"/> value for theories exercised across all presets.</summary>
    public static TheoryData<PanelSizePreset> AllPresets =>
    [
        PanelSizePreset.Small, PanelSizePreset.Medium, PanelSizePreset.Large, PanelSizePreset.ExtraLarge,
    ];

    // ── GridAligned: the result is exactly the preset's span, anchored at the (quantised) top-left. ──

    /// <summary>
    /// Verifies that in <see cref="PanelGeometryMode.GridAligned"/> mode, applying a size preset
    /// produces exactly that preset's span, anchored at the panel's own (already on-grid) top-left.
    /// </summary>
    /// <param name="preset">The size preset applied, drawn from <see cref="AllPresets"/>.</param>
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void GridAligned_ProducesThePresetSpan_AnchoredAtTheTopLeft(PanelSizePreset preset)
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var expectedSize = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.SpanForPreset(preset), U, G);

        var result = PanelSizePresetCalculator.ComputeGridAligned(OnGridCurrent, preset, state, []);

        Assert.Equal(OnGridCurrent.TopLeft, result.TopLeft);
        Assert.Equal(expectedSize.Width, result.Width);
        Assert.Equal(expectedSize.Height, result.Height);
    }

    // ── An off-grid panel is quantised by the same command (it is a resize on the grid, after all). ──

    /// <summary>
    /// Verifies that applying a size preset to a panel currently off-grid also snaps its position to
    /// the grid, since a preset resize is a resize on the grid like any other.
    /// </summary>
    [Fact]
    public void GridAligned_OffGridPanel_SnapsThePositionToTheGrid()
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var current = new Rect(101d, 203d, 317d, 243d);

        var result = PanelSizePresetCalculator.ComputeGridAligned(current, PanelSizePreset.Medium, state, []);

        Assert.Equal(WorkspaceLayoutPolicy.SnapPosition(current.TopLeft, state.Origin, U), result.TopLeft);
        Assert.Equal(0d, Math.IEEERemainder(result.Left - state.Origin.X, U), 9);
        Assert.Equal(0d, Math.IEEERemainder(result.Top - state.Origin.Y, U), 9);
    }

    // ── A negative-origin monitor quantises against ITS origin, not absolute zero. ───────────────────

    /// <summary>
    /// Verifies that on a monitor with a negative-coordinate origin, the preset result is quantised
    /// relative to that monitor's own origin, not absolute zero.
    /// </summary>
    [Fact]
    public void GridAligned_NegativeOrigin_QuantisesAgainstThatOrigin()
    {
        var origin = new Point(-1366d, -37d);
        var state = FakePanelSizingGridHost.GridAligned(origin, workArea: new Rect(-1366d, -37d, 1366d, 731d));

        var result = PanelSizePresetCalculator.ComputeGridAligned(new Rect(-1000d, 100d, 300d, 200d), PanelSizePreset.Small, state, []);

        Assert.Equal(0d, Math.IEEERemainder(result.Left - origin.X, U), 9);
        Assert.Equal(0d, Math.IEEERemainder(result.Top - origin.Y, U), 9);
    }

    // ── The work-area clamp keeps a large preset on screen without shrinking it. ─────────────────────

    /// <summary>
    /// Verifies that applying a preset near the work-area edge pulls the panel back fully inside the
    /// work area by repositioning it, without shrinking the preset's size.
    /// </summary>
    [Fact]
    public void GridAligned_NearTheWorkAreaEdge_PullsThePanelBackInside_WithoutChangingTheSize()
    {
        var state = FakePanelSizingGridHost.GridAligned(workArea: new Rect(0d, 0d, 800d, 600d));
        var expectedSize = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.SpanForPreset(PanelSizePreset.ExtraLarge), U, G);

        var result = PanelSizePresetCalculator.ComputeGridAligned(new Rect(700d, 500d, 120d, 120d), PanelSizePreset.ExtraLarge, state, []);

        Assert.Equal(expectedSize.Width, result.Width);
        Assert.Equal(expectedSize.Height, result.Height);
        Assert.True(result.Right <= 800d);
        Assert.True(result.Bottom <= 600d);
    }

    // ── Anti-overlap: only the colliding axis shrinks, and the anchor never moves. ───────────────────

    /// <summary>
    /// Verifies that a neighbour blocking only the horizontal axis shrinks the preset's width to
    /// leave exactly one gap of clearance, while the height stays at the preset's own value and the
    /// anchor never moves.
    /// </summary>
    [Fact]
    public void GridAligned_WithANeighbourToTheRight_ShrinksOnlyTheWidth()
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var neighbour = new Rect(400d, 200d, 200d, 240d);
        var unobstructed = PanelSizePresetCalculator.ComputeGridAligned(OnGridCurrent, PanelSizePreset.Large, state, []);

        var result = PanelSizePresetCalculator.ComputeGridAligned(OnGridCurrent, PanelSizePreset.Large, state, [neighbour]);

        Assert.Equal(OnGridCurrent.TopLeft, result.TopLeft);            // anchor fixed
        Assert.Equal(neighbour.Left - G, result.Right);                 // exactly one gap of clearance
        Assert.True(result.Width < unobstructed.Width);                 // the width really did shrink
        Assert.Equal(unobstructed.Height, result.Height);               // ...and the height did NOT
    }

    // ── A neighbour that a bottom-right resize can never clear (it straddles the anchor on both axes)
    // leaves the preset alone rather than destroying it down to the minimum (fix-cycle 2 MAJOR-B). ───

    /// <summary>
    /// Verifies the fix-cycle 2 MAJOR-B guard: a neighbour that straddles the anchor corner on both
    /// axes, so no anti-overlap shrink can clear it, leaves the preset's size exactly as computed
    /// without obstacles, rather than collapsing it toward the minimum span.
    /// </summary>
    [Fact]
    public void GridAligned_WithAnUnclearableNeighbour_KeepsThePresetSize()
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var straddling = new Rect(40d, 160d, 200d, 200d); // covers the anchor corner on both axes
        var unobstructed = PanelSizePresetCalculator.ComputeGridAligned(OnGridCurrent, PanelSizePreset.Medium, state, []);

        var result = PanelSizePresetCalculator.ComputeGridAligned(OnGridCurrent, PanelSizePreset.Medium, state, [straddling]);

        Assert.Equal(unobstructed, result);
    }

    // ── The minimum span still wins over a (hypothetically smaller) preset. ─────────────────────────

    /// <summary>
    /// Verifies that when the host's minimum span is larger than a preset's own span, the result is
    /// clamped up to the minimum span rather than shrinking below it.
    /// </summary>
    [Fact]
    public void GridAligned_PresetBelowTheMinimumSpan_IsClampedUpToIt()
    {
        var hugeMinimum = new GridSpan(40, 36);
        var state = FakePanelSizingGridHost.GridAligned(minSpan: hugeMinimum);
        var expected = WorkspaceLayoutPolicy.SpansToBounds(hugeMinimum, U, G);

        var result = PanelSizePresetCalculator.ComputeGridAligned(OnGridCurrent, PanelSizePreset.Small, state, []);

        Assert.Equal(expected.Width, result.Width);
        Assert.Equal(expected.Height, result.Height);
    }

    // ── LegacyFreeform: size only. The position is untouched (not even quantised) and the mode is not
    // the calculator's business at all — see ComputeFreeform's remarks for the decision. ─────────────

    /// <summary>
    /// Verifies that in <see cref="PanelGeometryMode.LegacyFreeform"/> mode, applying a size preset
    /// resizes the panel to that preset's extent while leaving the current position — including its
    /// off-grid, fractional coordinates — completely untouched.
    /// </summary>
    /// <param name="preset">The size preset applied, drawn from <see cref="AllPresets"/>.</param>
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Freeform_AppliesThePresetExtentAtTheCurrentPosition(PanelSizePreset preset)
    {
        var current = new Rect(101.5d, 203.25d, 317d, 243d); // deliberately off-grid and fractional
        var expectedSize = WorkspaceLayoutPolicy.PreferredSize(preset, U, G);

        var result = PanelSizePresetCalculator.ComputeFreeform(current, preset, new Rect(0d, 0d, 1920d, 1040d));

        Assert.Equal(current.TopLeft, result.TopLeft);
        Assert.Equal(expectedSize.Width, result.Width);
        Assert.Equal(expectedSize.Height, result.Height);
    }

    /// <summary>
    /// Verifies that in freeform mode, applying a preset near the work-area edge still pulls the
    /// panel back fully inside the work area.
    /// </summary>
    [Fact]
    public void Freeform_NearTheWorkAreaEdge_IsPulledBackInside()
    {
        var result = PanelSizePresetCalculator.ComputeFreeform(new Rect(700d, 500d, 120d, 120d), PanelSizePreset.Large, new Rect(0d, 0d, 800d, 600d));

        Assert.True(result.Right <= 800d);
        Assert.True(result.Bottom <= 600d);
    }

    // ── No monitor information available (IMonitorService returned nothing): resize in place, never
    // teleport to (0,0). ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that when no monitor work area is available (an empty <see cref="Rect"/>), applying a
    /// preset resizes in place and leaves the position untouched, rather than teleporting to (0,0).
    /// </summary>
    [Fact]
    public void Freeform_WithNoWorkArea_LeavesThePositionUntouched()
    {
        var current = new Rect(700d, 500d, 120d, 120d);

        var result = PanelSizePresetCalculator.ComputeFreeform(current, PanelSizePreset.Large, Rect.Empty);

        Assert.Equal(current.TopLeft, result.TopLeft);
        Assert.Equal(WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset.Large, U, G).Width, result.Width);
    }
}
