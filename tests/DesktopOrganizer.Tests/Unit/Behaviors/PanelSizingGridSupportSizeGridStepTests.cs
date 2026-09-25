using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// ADR-011 Amendment 2026-09-24 (resize extension): <see cref="PanelSizingGridSupport.NormalizeCommittedSize"/>
/// — the idempotent commit-time safety net shared by both resize controllers' <c>EndResize</c>/
/// <c>PersistAfterGesture</c> paths.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelSizingGridSupportSizeGridStepTests
{
    private const double TitleBarHeight = 32d;

    // 144x144 (exact 48 multiples) — a no-op input for every "already on grid" assertion below.
    private static readonly Rect OnGridBounds = new(100d, 100d, 144d, 144d);

    // ── GridSize <= 1: unconditional no-op, no host/model/state touched at all. ─────────────────────────

    /// <summary>Verifies that <see cref="PanelSizingGridSupport.NormalizeCommittedSize"/> is an unconditional no-op when <c>gridSize</c> is at or below 1, touching no host/model/state.</summary>
    [Fact]
    public void GridSizeAtOrBelowOne_IsANoOp()
    {
        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: null,
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 1,
            finalBounds: new Rect(100d, 100d, 170d, 170d),
            committedEdge: ResizeEdge.Right);

        Assert.Equal(new Rect(100d, 100d, 170d, 170d), result);
    }

    // ── LegacyFreeform (no host): already-on-grid bounds are an idempotent no-op — a real value-equal
    // Rect is returned, not merely an equal one (proves the "already exact -> no write" short-circuit). ──

    /// <summary>Verifies that for a LegacyFreeform panel, already-on-grid bounds are returned unchanged by <see cref="PanelSizingGridSupport.NormalizeCommittedSize"/>.</summary>
    [Fact]
    public void LegacyFreeform_AlreadyOnGrid_IsANoOp()
    {
        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            OnGridBounds,
            ResizeEdge.Right);

        Assert.Equal(OnGridBounds, result);
    }

    // ── LegacyFreeform, off-grid: re-aligns Width to the nearest 48 multiple, repositioning nothing
    // (Right-edge drag: Left, the anchor, stays fixed) — and the UNTOUCHED height axis (Right does not
    // affect height) is returned byte-for-byte unchanged, even though 100 is itself off the 48 grid. ────

    /// <summary>Verifies that for a LegacyFreeform panel, an off-grid width is realigned to the nearest grid-size multiple while the anchor edge and the untouched height axis stay unchanged.</summary>
    [Fact]
    public void LegacyFreeform_OffGrid_RealignsOnlyTheAffectedAxis_KeepingTheAnchorFixed()
    {
        var offGrid = new Rect(100d, 100d, 184d, 100d); // width 184 -> nearest 48-multiple is 192

        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            offGrid,
            ResizeEdge.Right);

        Assert.Equal(192d, result.Width);
        Assert.Equal(100d, result.Left); // Right-edge drag: Left (anchor) untouched
        Assert.Equal(100d, result.Height); // untouched axis: Right does not affect height at all
        Assert.Equal(100d, result.Top);
    }

    // ── The LEFT edge case: the RIGHT edge (anchor) must stay fixed — Left is recomputed from the
    // quantised width. ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Verifies that for a Left-edge drag, the opposite (Right) edge stays fixed while Left is recomputed from the quantised width.</summary>
    [Fact]
    public void LegacyFreeform_OffGrid_LeftEdge_KeepsTheRightEdgeAnchored()
    {
        var offGrid = new Rect(52d, 100d, 184d, 144d); // Right = 236

        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            offGrid,
            ResizeEdge.Left);

        Assert.Equal(192d, result.Width);
        Assert.Equal(236d, result.Right);
        Assert.Equal(236d - 192d, result.Left);
    }

    // ── A null committedEdge normalises the SIZE only, anchored at the top-left (mirrors PersistSpans'
    // own contract for a non-resize caller). ──────────────────────────────────────────────────────────

    /// <summary>Verifies that a <see langword="null"/> committed edge normalises the size only, anchored at the top-left.</summary>
    [Fact]
    public void NullCommittedEdge_NormalisesSizeOnly_TopLeftAnchored()
    {
        var offGrid = new Rect(100d, 100d, 184d, 184d);

        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            offGrid,
            committedEdge: null);

        Assert.Equal(100d, result.Left);
        Assert.Equal(100d, result.Top);
        Assert.Equal(192d, result.Width);
        Assert.Equal(192d, result.Height);
    }

    // ── Idempotency: applying the normalization twice in a row is the identity — the exact property
    // this method's own doc comment claims ("идемпотентная нормализация"). ────────────────────────────

    /// <summary>Verifies that applying <see cref="PanelSizingGridSupport.NormalizeCommittedSize"/> twice in a row is the identity.</summary>
    [Fact]
    public void ApplyingTwice_IsIdempotent()
    {
        var offGrid = new Rect(100d, 100d, 184d, 184d);

        var once = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            offGrid,
            ResizeEdge.BottomRight);

        var twice = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            once,
            ResizeEdge.BottomRight);

        Assert.Equal(once, twice);
    }

    // ── GridAligned, GridSize commensurate with U: already-U-aligned/GridSize-aligned bounds (e.g. what
    // PersistSpans just committed) are a no-op — the belt-and-suspenders re-check never disturbs a
    // panel PersistSpans already put on the grid. ──────────────────────────────────────────────────────

    /// <summary>Verifies that already-exact, grid-aligned bounds with a grid size commensurate with the unit size are left untouched.</summary>
    [Fact]
    public void GridAligned_Commensurate_AlreadyExact_IsANoOp()
    {
        var host = new FakePanelSizingGridHost { State = FakePanelSizingGridHost.GridAligned() };

        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host, model: null, snapResizeToIconGrid: false, TitleBarHeight, gridSize: 48, OnGridBounds, ResizeEdge.Right);

        Assert.Equal(OnGridBounds, result);
    }

    // ── GridAligned, GridSize NOT commensurate with U: SizeGridStep resolves to 0 (see
    // ResizeConstraintsFactorySizeGridStepTests), so this method is a no-op even for off-grid input —
    // the panel is left exactly as PersistSpans (U-only) already committed it. ─────────────────────────

    /// <summary>Verifies that when the grid size is not commensurate with the unit size, <see cref="PanelSizingGridSupport.NormalizeCommittedSize"/> is a no-op even for off-grid input.</summary>
    [Fact]
    public void GridAligned_NotCommensurate_IsANoOp_EvenWhenOffGrid()
    {
        var host = new FakePanelSizingGridHost { State = FakePanelSizingGridHost.GridAligned() };
        var offGrid = new Rect(96d, 200d, 184d, 240d); // 184 is not a multiple of 50, and not of 8 either

        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host, model: null, snapResizeToIconGrid: false, TitleBarHeight, gridSize: 50, offGrid, ResizeEdge.Right);

        Assert.Equal(offGrid, result);
    }

    // ── A non-participating host (LegacyFreeform panel with a real host reference) behaves exactly like
    // host == null. ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Verifies that a non-participating host behaves exactly like a <see langword="null"/> host.</summary>
    [Fact]
    public void NonParticipatingHost_BehavesLikeNoHost()
    {
        var host = new FakePanelSizingGridHost { ParticipatesInGrid = false };
        var offGrid = new Rect(100d, 100d, 184d, 100d);

        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            offGrid,
            ResizeEdge.Right);

        Assert.Equal(192d, result.Width);
        Assert.Equal(0, host.ResolveCallCount);
    }

    // ── Degenerate bounds (non-finite / non-positive) are a defensive no-op, same "never write NaN"
    // contract PersistSpans' own IsFinite guard already enforces. ────────────────────────────────────────

    /// <summary>Verifies that degenerate (non-finite or non-positive) bounds are a defensive no-op.</summary>
    /// <param name="width">The candidate width.</param>
    /// <param name="height">The candidate height.</param>
    [Theory]
    [InlineData(double.NaN, 100d)]
    [InlineData(100d, double.NaN)]
    [InlineData(0d, 100d)]
    [InlineData(100d, 0d)]
    public void DegenerateBounds_AreANoOp(double width, double height)
    {
        var bounds = new Rect(100d, 100d, width, height);

        var result = PanelSizingGridSupport.NormalizeCommittedSize(
            host: null,
            model: new ChildWindowModel { ViewMode = PanelViewMode.List },
            snapResizeToIconGrid: false,
            TitleBarHeight,
            gridSize: 48,
            bounds,
            ResizeEdge.Right);

        Assert.Equal(bounds, result);
    }
}
