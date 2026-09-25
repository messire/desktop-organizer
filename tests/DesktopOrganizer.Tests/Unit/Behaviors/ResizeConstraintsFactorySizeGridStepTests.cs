using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// ADR-011 Amendment 2026-09-24 (resize extension): <see cref="ResizeConstraintsFactory.BuildConstraints"/>'s
/// <c>gridSize</c> parameter — resolves <see cref="ResizeConstraints.SizeGridStep"/> and, when active,
/// overrides the icon-grid snap cells.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeConstraintsFactorySizeGridStepTests
{
    private const double TitleBarHeight = 32d;

    // ── GridSize <= 1 ("no grid", the project-wide default) is a no-op for every mode — SizeGridStep
    // stays 0 and every existing (pre-amendment) call site's output is reproduced bit-for-bit. ──────────

    /// <summary>Verifies that a <paramref name="gridSize"/> at or below 1 ("no grid") leaves <c>SizeGridStep</c> at zero and still lets the icon-grid snap apply.</summary>
    /// <param name="gridSize">The <c>IAppSettings.GridSize</c> value under test.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GridSizeAtOrBelowOne_LeavesSizeGridStepAtZero(int gridSize)
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight, gridState: default, gridSize);

        Assert.Equal(0d, constraints.SizeGridStep);
        Assert.Equal(GridMetrics.CellSize(48d), constraints.SnapCellWidth); // icon-grid snap still applies
    }

    // ── Omitting gridSize entirely defaults to 1 ("no grid") — every call site written before this
    // amendment keeps compiling and keeps producing SizeGridStep == 0. ──────────────────────────────────

    /// <summary>Verifies that omitting the <c>gridSize</c> argument entirely defaults to "no grid" (<c>SizeGridStep == 0</c>).</summary>
    [Fact]
    public void OmittingGridSize_DefaultsToNoGrid()
    {
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight);

        Assert.Equal(0d, constraints.SizeGridStep);
    }

    // ── LegacyFreeform (no grid-aligned state): GridSize > 1 is unconditional — no commensurability
    // gate, because a LegacyFreeform panel is not on any grid to begin with. ────────────────────────────

    /// <summary>Verifies that for a LegacyFreeform panel, any <paramref name="gridSize"/> above 1 sets <c>SizeGridStep</c> unconditionally, with no commensurability gate.</summary>
    /// <param name="gridSize">The <c>IAppSettings.GridSize</c> value under test.</param>
    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    [InlineData(50)] // deliberately NOT a multiple of WorkspaceGridMetrics.UnitSize (8) — still fine for Legacy
    public void LegacyFreeform_GridSizeAboveOne_SetsSizeGridStepUnconditionally(int gridSize)
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.List };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight, gridState: default, gridSize);

        Assert.Equal((double)gridSize, constraints.SizeGridStep);
    }

    // ── SizeGridStep overrides the icon-grid snap entirely (priority: GridSize > SnapResizeToIconGrid)
    // — both SnapCellWidth and SnapCellHeight are zeroed even though Grid mode + snapEnabled would
    // otherwise populate them. ──────────────────────────────────────────────────────────────────────────

    /// <summary>Verifies that <c>SizeGridStep</c> overrides the icon-grid snap cells entirely, zeroing <c>SnapCellWidth</c>/<c>SnapCellHeight</c> even in Grid mode with snap enabled.</summary>
    [Fact]
    public void GridModeWithIconSnapEnabled_SizeGridStepOverridesTheIconGridSnapCells()
    {
        var model = new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = 48d };

        var constraints = ResizeConstraintsFactory.BuildConstraints(model, snapEnabled: true, TitleBarHeight, gridState: default, gridSize: 48);

        Assert.Equal(48d, constraints.SizeGridStep);
        Assert.Equal(0d, constraints.SnapCellWidth);
        Assert.Equal(0d, constraints.SnapCellHeight);
    }

    // ── GridAligned, GridSize commensurate with U (48 % 8 == 0): SizeGridStep is populated. ────────────

    /// <summary>Verifies that for a grid-aligned panel, a <paramref name="gridSize"/> commensurate with the unit size populates <c>SizeGridStep</c>.</summary>
    /// <param name="gridSize">The <c>IAppSettings.GridSize</c> value under test.</param>
    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    [InlineData(16)]
    [InlineData(8)]
    public void GridAligned_GridSizeCommensurateWithUnitSize_SetsSizeGridStep(int gridSize)
    {
        var state = FakePanelSizingGridHost.GridAligned();

        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state, gridSize);

        Assert.Equal((double)gridSize, constraints.SizeGridStep);
    }

    // ── GridAligned, GridSize NOT commensurate with U (50 % 8 != 0, ADR-028 "две сетки намеренно
    // несоизмеримы"): SizeGridStep stays 0 — the panel is left on its ordinary workspace-grid (U-only)
    // quantisation, exactly the same guard WindowManager.NormalizeGridAlignedToGridSize already applies. ──

    /// <summary>Verifies that for a grid-aligned panel, a <paramref name="gridSize"/> not commensurate with the unit size leaves <c>SizeGridStep</c> at zero.</summary>
    /// <param name="gridSize">The <c>IAppSettings.GridSize</c> value under test.</param>
    [Theory]
    [InlineData(50)]
    [InlineData(33)]
    [InlineData(100)]
    public void GridAligned_GridSizeNotCommensurateWithUnitSize_LeavesSizeGridStepAtZero(int gridSize)
    {
        var state = FakePanelSizingGridHost.GridAligned();

        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state, gridSize);

        Assert.Equal(0d, constraints.SizeGridStep);
    }

    // ── A degenerate/hand-built GridAligned state with UnitSize <= 0 must never divide by zero — falls
    // back exactly like the pre-existing `isGridAligned` guard already does for MinWidth/MinHeight. ──────

    /// <summary>Verifies that a degenerate/hand-built grid-aligned state with a non-positive unit size degrades to the legacy classification and still applies <c>GridSize</c> unconditionally, without dividing by zero.</summary>
    [Fact]
    public void GridAlignedWithDegenerateUnitSize_TreatsGestureAsLegacy_AndAppliesGridSizeUnconditionally()
    {
        var broken = new PanelGridState(
            IsGridAligned: true, Origin: default, WorkArea: default, UnitSize: 0d, Gap: 0d, MinSpan: default, MonitorDeviceId: null);

        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, broken, gridSize: 48);

        // The existing isGridAligned guard already degrades a degenerate UnitSize to "not grid-aligned"
        // for the floor computation — SizeGridStep follows the same degraded classification.
        Assert.Equal(48d, constraints.SizeGridStep);
    }
}
