using System.Windows;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// ADR-011 Amendment 2026-09-24 (resize extension): <see cref="ResizeConstraints.SizeGridStep"/>'s
/// effect on <see cref="ResizeGeometryCalculator.Apply"/> — the LegacyFreeform-shaped half of the
/// feature (a <see cref="Models.PanelGeometryMode.GridAligned"/> gesture's OWN <c>SizeGridStep</c>
/// commensurability gate is covered separately, at <c>ResizeConstraintsFactory.BuildConstraints</c> and
/// <c>ResizeGestureSession</c> level, since <see cref="ResizeGeometryCalculator"/> itself has no notion
/// of grid alignment).
/// </summary>
/// <remarks>
/// A NEW file, deliberately not added to <c>ResizeGeometryCalculatorTests</c>/
/// <c>ResizeGeometryCharacterizationTests</c> (Phase 1's pinned bit-for-bit characterization suites) —
/// this feature is additive (gated on <c>SizeGridStep &gt; 0</c>, default <c>0</c>) and does not change
/// a single byte of either suite's existing coverage; see the <c>GridSizeIsOff_IsBitForBitIdenticalToTheOldBehaviour</c>
/// test below for the machine-checked half of that claim.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGeometryCalculatorSizeGridStepTests
{
    private const double GridSize = 48d;
    private const double MinWidth = GridMetrics.MinPanelSize; // 120d
    private const double MinHeight = GridMetrics.MinPanelSize;

    // 144x144 (both exact multiples of 48) at (100, 100) — Right = 244, Bottom = 244.
    private static readonly Rect StartBounds = new(100d, 100d, 144d, 144d);

    private static ResizeConstraints SizeGridConstraints(double sizeGridStep = GridSize) => new(
        MinWidth: MinWidth,
        MinHeight: MinHeight,
        SnapCellWidth: 0d,
        SnapCellHeight: 0d,
        TitleBarHeight: 32d,
        HorizontalChrome: GridMetrics.FileListHPadding,
        VerticalChrome: GridMetrics.FileListVPadding,
        SizeGridStep: sizeGridStep);

    private static Rect ApplyOneShot(ResizeEdge edge, double dx, double dy, ResizeConstraints constraints, Rect? start = null)
    {
        var bounds = start ?? StartBounds;
        var raw = ResizeGeometryCalculator.Accumulate(new ResizeRawExtent(bounds.Width, bounds.Height), edge, new Vector(dx, dy));
        return ResizeGeometryCalculator.Apply(bounds, edge, raw, bounds.Right, bounds.Bottom, constraints);
    }

    // ── Round-half-up to the nearest 48 multiple, reusing GridSizeNormalizer verbatim (not a threshold
    // magnet like GridSnap.SnapAxis): 144 + 40 = 184 -> nearest of {144, 192} is 192 (distance 8 vs 40).
    // 144 + 20 = 164 -> nearest of {144, 192} is 144 (distance 20 vs 28). ────────────────────────────────
    [Theory]
    [InlineData(40d, 192d)]
    [InlineData(20d, 144d)]
    [InlineData(0d, 144d)] // already exact -> idempotent no-op
    public void Right_QuantisesToNearestGridSizeMultiple(double dx, double expectedWidth)
    {
        var result = ApplyOneShot(ResizeEdge.Right, dx, 0d, SizeGridConstraints());

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(StartBounds.Left, result.Left); // anchor (Left) untouched
        Assert.Equal(StartBounds.Height, result.Height); // untouched axis
    }

    // ── Exact tie (24 DIP from both neighbours, half of 48) resolves round-HALF-UP, not banker's
    // rounding: 144 + 24 = 168 is exactly between 144 and 192 -> 192. ───────────────────────────────────
    [Fact]
    public void ExactHalfwayTie_RoundsUp()
    {
        var result = ApplyOneShot(ResizeEdge.Right, 24d, 0d, SizeGridConstraints());

        Assert.Equal(192d, result.Width);
    }

    // ── The dragged LEFT edge moves; the anchor (Right, fixed at gesture start = 244) never does — the
    // generic anchor logic downstream of the branch selection applies uniformly. ─────────────────────────
    [Fact]
    public void Left_KeepsTheRightEdgeAnchored_AndRepositionsLeftFromTheQuantisedWidth()
    {
        // Dragging the Left edge outward (cursor moves left) by 40 grows the raw width the same way
        // Right+40 does (Accumulate's own sign convention) -> same 192 nearest multiple.
        var result = ApplyOneShot(ResizeEdge.Left, -40d, 0d, SizeGridConstraints());

        Assert.Equal(192d, result.Width);
        Assert.Equal(StartBounds.Right, result.Right); // anchor untouched
        Assert.Equal(StartBounds.Right - 192d, result.Left);
    }

    // ── Below the minimum: nearest multiple of 48 to a tiny raw width is BELOW MinWidth (120), so the
    // result promotes to the smallest multiple >= MinWidth. GridSizeNormalizer.NormalizeExtent's own
    // floor logic, exercised end to end through Apply. ─────────────────────────────────────────────────
    [Fact]
    public void BelowTheMinimum_PromotesToTheSmallestGridSizeMultipleAtOrAboveMinWidth()
    {
        var tinyStart = new Rect(100d, 100d, 60d, 60d);

        // Shrink further: raw width ends up well below 48*2=96, e.g. 20 DIP -> nearest multiple is 0,
        // but MinWidth=120 floors it to the smallest multiple >= 120, which is 144 (48*3).
        var result = ApplyOneShot(ResizeEdge.Right, -40d, 0d, SizeGridConstraints(), tinyStart);

        Assert.Equal(144d, result.Width);
        Assert.True(result.Width >= MinWidth);
    }

    // ── Height axis mirrors width exactly (SizeGridStep, unlike the pre-existing free-height branch,
    // operates on raw.Height directly and symmetrically with width — no chrome round trip on either axis). ──
    [Theory]
    [InlineData(40d, 192d)]
    [InlineData(20d, 144d)]
    public void Bottom_QuantisesHeightTheSameWayAsWidth(double dy, double expectedHeight)
    {
        var result = ApplyOneShot(ResizeEdge.Bottom, 0d, dy, SizeGridConstraints());

        Assert.Equal(expectedHeight, result.Height);
        Assert.Equal(StartBounds.Top, result.Top);
        Assert.Equal(StartBounds.Width, result.Width);
    }

    // ── All 8 edges: the untouched axis is never modified, and the anchor edge(s) never move — the same
    // "point 7" contract the icon-grid-snap/free branches already guarantee, now also true for the new
    // SizeGridStep branch (it reuses the exact same downstream anchor-application code). ─────────────────
    public static TheoryData<ResizeEdge, double, double> EightEdges => new()
    {
        { ResizeEdge.Left, -40d, 0d },
        { ResizeEdge.Right, 40d, 0d },
        { ResizeEdge.Top, 0d, -40d },
        { ResizeEdge.Bottom, 0d, 40d },
        { ResizeEdge.TopLeft, -40d, -40d },
        { ResizeEdge.TopRight, 40d, -40d },
        { ResizeEdge.BottomLeft, -40d, 40d },
        { ResizeEdge.BottomRight, 40d, 40d },
    };

    [Theory]
    [MemberData(nameof(EightEdges))]
    public void EveryEdge_QuantisesTheAffectedAxisOnly_AndKeepsTheAnchorFixed(ResizeEdge edge, double dx, double dy)
    {
        var result = ApplyOneShot(edge, dx, dy, SizeGridConstraints());

        if (edge.AffectsWidth())
        {
            Assert.Equal(0d, result.Width % GridSize);
            if (edge.AnchorsRight()) Assert.Equal(StartBounds.Right, result.Right);
            else Assert.Equal(StartBounds.Left, result.Left);
        }
        else
        {
            Assert.Equal(StartBounds.Width, result.Width);
            Assert.Equal(StartBounds.Left, result.Left);
        }

        if (edge.AffectsHeight())
        {
            Assert.Equal(0d, result.Height % GridSize);
            if (edge.AnchorsBottom()) Assert.Equal(StartBounds.Bottom, result.Bottom);
            else Assert.Equal(StartBounds.Top, result.Top);
        }
        else
        {
            Assert.Equal(StartBounds.Height, result.Height);
            Assert.Equal(StartBounds.Top, result.Top);
        }
    }

    // ── Priority: SizeGridStep wins even when SnapCellWidth/Height are ALSO (incorrectly) populated —
    // defense in depth matching ResizeConstraintsFactory's own "or the calculator checks first" note. ────
    [Fact]
    public void SizeGridStep_TakesPriorityOverIconGridSnap_WhenBothAreSomehowSet()
    {
        var constraints = new ResizeConstraints(
            MinWidth: MinWidth,
            MinHeight: MinHeight,
            SnapCellWidth: GridMetrics.CellSize(48d), // 84 — would give a very different answer
            SnapCellHeight: GridMetrics.RowHeight(48d),
            TitleBarHeight: 32d,
            HorizontalChrome: GridMetrics.FileListHPadding,
            VerticalChrome: GridMetrics.FileListVPadding,
            SizeGridStep: GridSize);

        var result = ApplyOneShot(ResizeEdge.Right, 40d, 0d, constraints);

        Assert.Equal(192d, result.Width); // the GridSize answer, not the icon-grid-snap one
    }

    // ── Path-independence (mirrors ResizeGeometryCalculatorApplyEquivalenceTests' own pattern): a
    // 4-step Accumulate chain and one absolute raw extent must agree bit-for-bit — the property both
    // resize controllers' golden-equivalence tests rely on. ────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void AccumulateChain_And_SingleAbsoluteApply_ProduceBitForBitIdenticalRect(ResizeEdge edge, double totalDx, double totalDy)
    {
        var constraints = SizeGridConstraints();

        double[] dxSteps = [totalDx * 0.25, totalDx * 0.25, totalDx * 0.25, totalDx * 0.25];
        double[] dySteps = [totalDy * 0.25, totalDy * 0.25, totalDy * 0.25, totalDy * 0.25];

        var chainedRaw = new ResizeRawExtent(StartBounds.Width, StartBounds.Height);
        for (var i = 0; i < dxSteps.Length; i++)
            chainedRaw = ResizeGeometryCalculator.Accumulate(chainedRaw, edge, new Vector(dxSteps[i], dySteps[i]));

        var chainedResult = ResizeGeometryCalculator.Apply(StartBounds, edge, chainedRaw, StartBounds.Right, StartBounds.Bottom, constraints);

        var absoluteRaw = new ResizeRawExtent(
            edge.AffectsWidth() ? StartBounds.Width + (edge.AnchorsRight() ? -totalDx : totalDx) : StartBounds.Width,
            edge.AffectsHeight() ? StartBounds.Height + (edge.AnchorsBottom() ? -totalDy : totalDy) : StartBounds.Height);

        var absoluteResult = ResizeGeometryCalculator.Apply(StartBounds, edge, absoluteRaw, StartBounds.Right, StartBounds.Bottom, constraints);

        Assert.Equal(chainedResult, absoluteResult);
    }

    // ── GridSize=1 (the "no grid" default): SizeGridStep is never populated to a non-zero value by the
    // factory for that input (see ResizeConstraintsFactoryTests), and this calculator's own guard
    // (`SizeGridStep > 0d`) means passing a literal 0 here reproduces the OLD icon-grid/free behaviour
    // bit-for-bit — the machine-checked half of "old tests stay unchanged". ───────────────────────────────
    [Fact]
    public void GridSizeIsOff_IsBitForBitIdenticalToTheOldBehaviour()
    {
        var withoutStep = new ResizeConstraints(
            MinWidth: MinWidth,
            MinHeight: MinHeight,
            SnapCellWidth: 0d,
            SnapCellHeight: 0d,
            TitleBarHeight: 32d,
            HorizontalChrome: GridMetrics.FileListHPadding,
            VerticalChrome: GridMetrics.FileListVPadding); // SizeGridStep defaults to 0d

        var result = ApplyOneShot(ResizeEdge.Right, 40d, 0d, withoutStep);

        // Old free-branch answer: Max(MinWidth, contentW + HorizontalChrome) — content-stripped free path.
        var expectedContentW = Math.Max(0d, (StartBounds.Width + 40d) - GridMetrics.FileListHPadding);
        var expected = Math.Max(MinWidth, expectedContentW + GridMetrics.FileListHPadding);
        Assert.Equal(expected, result.Width);
        Assert.NotEqual(192d, result.Width); // sanity: genuinely a different answer than the GridSize path
    }

    // ── Non-finite SizeGridStep is treated exactly like every other non-finite constraint field — the
    // H3 guard returns `current` unchanged rather than propagating NaN into Window.Width/Height. ────────
    [Fact]
    public void NonFiniteSizeGridStep_ReturnsCurrentUnchanged()
    {
        var constraints = SizeGridConstraints(double.NaN);

        var result = ResizeGeometryCalculator.Apply(
            StartBounds, ResizeEdge.Right, new ResizeRawExtent(200d, 144d), StartBounds.Right, StartBounds.Bottom, constraints);

        Assert.Equal(StartBounds, result);
    }
}
