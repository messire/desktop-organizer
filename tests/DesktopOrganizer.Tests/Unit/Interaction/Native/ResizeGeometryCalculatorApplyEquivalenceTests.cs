using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Interaction.Native;

/// <summary>
/// A pure algebraic property of <see cref="ResizeGeometryCalculator.Apply"/> itself: it produces a
/// BIT-FOR-BIT identical <see cref="Rect"/> whether its <c>raw</c> argument arrived via an
/// <see cref="ResizeGeometryCalculator.Accumulate"/> chain of N small per-frame deltas (the shape
/// <c>Behaviors.PanelResizeController.OnResizeDragDelta</c> feeds it) or via one directly-computed
/// absolute extent (the shape <c>Behaviors.NativePanelResizeController.ProposeBounds</c> feeds it, D-4),
/// for the same total movement, on every one of the 8 <see cref="ResizeEdge"/> values, in both Grid+snap
/// and List+free constraint shapes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Renamed from the original <c>NativeResizeEquivalenceTests</c>/"T-EQ-*" (code-quality-reviewer
/// finding, Phase 2 refactoring §1.5/§3/§5): this class does NOT exercise
/// <c>Behaviors.NativePanelResizeController.ProposeBounds</c> or the legacy Thumb path's controller code
/// at all</b> — both sides below call <see cref="ResizeGeometryCalculator.Apply"/> directly, with the
/// "native" side's <c>raw</c> argument built by copying <c>Accumulate</c>'s own
/// <c>AnchorsRight()</c>/<c>AnchorsBottom()</c> sign arithmetic inline (deliberately, so as not to call
/// <see cref="ResizeGeometryCalculator.Accumulate"/> on both sides — that would trivially always agree
/// with itself and prove nothing). That makes this a genuine, useful characterization test of
/// <see cref="ResizeGeometryCalculator.Apply"/>'s own invariant ("path-independent of how <c>raw</c> was
/// reached") — but it was previously ALSO presented as proof that the real native controller and the real
/// legacy controller agree, which it never was: neither controller class is constructed or called
/// anywhere in this file. Neither controller's own glue — px↔DIP conversion
/// (<see cref="Interaction.Native.NativeSizingCoordinateMapper"/>), the <c>WM_SIZING</c> coordinate-space
/// self-calibration (<see cref="Interaction.Native.NativeSizingOriginResolver"/>, the exact mechanism
/// behind commit 646e04c's C-1 bug), or <c>Thumb</c>/<c>Window</c> event wiring — is exercised here at
/// all, so a bug in any of THAT code (like C-1 itself) would never have shown up as a failure in this
/// file. The real native-vs-legacy golden-equivalence suite, driven through the actual
/// <c>NativePanelResizeController.ProposeBounds</c> (including a non-zero origin offset and a px→DIP→px
/// round trip at non-1.0 DPI scale), now lives in
/// <c>Unit.Behaviors.NativePanelResizeControllerTests</c> — see that class's "Honest golden equivalence"
/// region.
/// </para>
/// <para>
/// Input geometry/deltas and the Grid-snap constraint shape (IconSize 48) reuse the same fixed
/// <c>StartBounds</c> as <c>ResizeGeometryCalculatorTests</c>/<c>ResizeGeometryCharacterizationTests</c>
/// (Phase 1) — read for structure only, those two files are NOT edited (plan §3, "жёсткое требование").
/// </para>
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGeometryCalculatorApplyEquivalenceTests
{
    private const double IconSize = 48d;

    // Same fixed "current window" bounds as the Phase 1 suites: rawRight = 400, rawBottom = 450.
    private static readonly Rect StartBounds = new(100, 200, 300, 250);

    private static ResizeConstraints FreeConstraints() => new(
        MinWidth: GridMetrics.MinPanelSize,
        MinHeight: GridMetrics.MinPanelSize,
        SnapCellWidth: 0d,
        SnapCellHeight: 0d,
        TitleBarHeight: ChildWindow.TitleBarHeight,
        HorizontalChrome: GridMetrics.FileListHPadding,
        VerticalChrome: GridMetrics.FileListVPadding);

    private static ResizeConstraints GridSnapConstraints() => new(
        MinWidth: GridMetrics.MinPanelWidth(IconSize),
        MinHeight: GridMetrics.MinPanelSize,
        SnapCellWidth: GridMetrics.CellSize(IconSize), // == 84 at IconSize 48
        SnapCellHeight: GridMetrics.RowHeight(IconSize), // == 92 at IconSize 48
        TitleBarHeight: ChildWindow.TitleBarHeight,
        HorizontalChrome: GridMetrics.FileListHPadding,
        VerticalChrome: GridMetrics.FileListVPadding);

    // ── MINOR-2 (fix-cycle 2): the constraint shape above is LegacyStretch-equivalent by construction
    // (GridMetrics.FileListHPadding/MinPanelWidth(iconSize) never reserved M-2's scrollbar width) — this
    // suite never exercised the FixedGap constraint shape at all before this fix-cycle, even though
    // ResizeConstraintsFactory.BuildConstraints (the REAL production path both PanelResizeController and
    // NativePanelResizeController's SnapWidthToGrid use) has read model.IconLayoutPolicy since P05.
    // Built via the SAME ResizeConstraintsFactory.BuildConstraints the two SnapWidthToGrid copies use
    // (not hand-rolled like the two constraint factories above), so this coverage would also catch either
    // copy silently drifting from what the factory actually produces for a FixedGap panel. ──────────────
    private static ResizeConstraints FixedGapGridSnapConstraints()
        => ResizeConstraintsFactory.BuildConstraints(
            new ChildWindowModel { ViewMode = PanelViewMode.Grid, IconSize = IconSize, IconLayoutPolicy = IconLayoutPolicy.FixedGap },
            snapEnabled: true,
            ChildWindow.TitleBarHeight);

    // ── T-EQ-01..08 x 2 modes: one case per ResizeEdge, in both List+free and Grid+snap constraint
    // shapes — 16 combinations total, each driving the SAME total on-screen movement through both the
    // legacy accumulate-chain path and the native single-proposed-extent path. ──────────────────────
    [Theory]
    [InlineData(ResizeEdge.Left, false, 40d, 0d)]
    [InlineData(ResizeEdge.Top, false, 0d, 25d)]
    [InlineData(ResizeEdge.Right, false, 40d, 0d)]
    [InlineData(ResizeEdge.Bottom, false, 0d, 25d)]
    [InlineData(ResizeEdge.TopLeft, false, 40d, 25d)]
    [InlineData(ResizeEdge.TopRight, false, 40d, 25d)]
    [InlineData(ResizeEdge.BottomLeft, false, 40d, 25d)]
    [InlineData(ResizeEdge.BottomRight, false, 40d, 25d)]
    [InlineData(ResizeEdge.Left, true, 84d, 0d)]
    [InlineData(ResizeEdge.Top, true, 0d, 92d)]
    [InlineData(ResizeEdge.Right, true, 84d, 0d)]
    [InlineData(ResizeEdge.Bottom, true, 0d, 92d)]
    [InlineData(ResizeEdge.TopLeft, true, 84d, 92d)]
    [InlineData(ResizeEdge.TopRight, true, 84d, 92d)]
    [InlineData(ResizeEdge.BottomLeft, true, 84d, 92d)]
    [InlineData(ResizeEdge.BottomRight, true, 84d, 92d)]
    public void AccumulateChain_And_SingleAbsoluteApply_ProduceBitForBitIdenticalRect(
        ResizeEdge edge, bool gridSnap, double totalDx, double totalDy)
        => AssertAccumulateChainMatchesSingleApply(edge, totalDx, totalDy, gridSnap ? GridSnapConstraints() : FreeConstraints());

    // ── MINOR-2 (fix-cycle 2): the same 8-edge Grid+snap coverage as the "true" branch of the theory
    // above, but against FixedGapGridSnapConstraints() instead — pins that Apply's path-independence
    // invariant (and, by construction, ResizeConstraintsFactory.BuildConstraints' own FixedGap output)
    // holds for the FixedGap constraint shape too, not just the LegacyStretch-equivalent one every other
    // case in this file uses. Same magnitudes as the Grid+snap cases above (IconSize 48's cell/rowHeight
    // don't depend on IconLayoutPolicy — only HorizontalChrome/MinWidth do). ─────────────────────────────
    [Theory]
    [InlineData(ResizeEdge.Left, 84d, 0d)]
    [InlineData(ResizeEdge.Top, 0d, 92d)]
    [InlineData(ResizeEdge.Right, 84d, 0d)]
    [InlineData(ResizeEdge.Bottom, 0d, 92d)]
    [InlineData(ResizeEdge.TopLeft, 84d, 92d)]
    [InlineData(ResizeEdge.TopRight, 84d, 92d)]
    [InlineData(ResizeEdge.BottomLeft, 84d, 92d)]
    [InlineData(ResizeEdge.BottomRight, 84d, 92d)]
    public void AccumulateChain_And_SingleAbsoluteApply_ProduceBitForBitIdenticalRect_FixedGapGridSnap(
        ResizeEdge edge, double totalDx, double totalDy)
        => AssertAccumulateChainMatchesSingleApply(edge, totalDx, totalDy, FixedGapGridSnapConstraints());

    private static void AssertAccumulateChainMatchesSingleApply(ResizeEdge edge, double totalDx, double totalDy, ResizeConstraints constraints)
    {
        // (a) N small per-frame deltas accumulated one at a time, then ONE Apply — the shape
        // Accumulate's own doc comment describes (a Thumb.DragDelta chain). Quarters of clean
        // binary-friendly magnitudes (40/25/84/92) so the split sums back to the total bit-exactly —
        // the point of this test is to pin Apply's equivalence, not to fight floating-point rounding.
        double[] dxSteps = [totalDx * 0.25, totalDx * 0.25, totalDx * 0.25, totalDx * 0.25];
        double[] dySteps = [totalDy * 0.25, totalDy * 0.25, totalDy * 0.25, totalDy * 0.25];
        Assert.Equal(totalDx, dxSteps.Sum()); // sanity: the split really does sum back to the total
        Assert.Equal(totalDy, dySteps.Sum());

        var chainedRaw = new ResizeRawExtent(StartBounds.Width, StartBounds.Height);
        for (var i = 0; i < dxSteps.Length; i++)
            chainedRaw = ResizeGeometryCalculator.Accumulate(chainedRaw, edge, new Vector(dxSteps[i], dySteps[i]));

        var chainedResult = ResizeGeometryCalculator.Apply(
            StartBounds, edge, chainedRaw, StartBounds.Right, StartBounds.Bottom, constraints);

        // (b) ONE absolute raw extent, built directly from the total movement WITHOUT going through
        // Accumulate (calling Accumulate on both sides would just test it against itself and prove
        // nothing) — then ONE Apply call with the SAME anchors/constraints.
        var absoluteRaw = new ResizeRawExtent(
            edge.AffectsWidth() ? StartBounds.Width + (edge.AnchorsRight() ? -totalDx : totalDx) : StartBounds.Width,
            edge.AffectsHeight() ? StartBounds.Height + (edge.AnchorsBottom() ? -totalDy : totalDy) : StartBounds.Height);

        var absoluteResult = ResizeGeometryCalculator.Apply(
            StartBounds, edge, absoluteRaw, StartBounds.Right, StartBounds.Bottom, constraints);

        // Bit-for-bit — no Assert.Equal(double, double, precision:) tolerance anywhere: this pins
        // Apply's own path-independence invariant.
        Assert.Equal(chainedResult, absoluteResult);
    }

    // ── R-4 scenario: the min-size clamp is hit — both raw-extent shapes must still agree bit-for-bit
    // once the Apply-side Math.Max floor kicks in. ──────────────────────────────────────────────────
    [Theory]
    [InlineData(ResizeEdge.Left, 250d, 0d)]
    [InlineData(ResizeEdge.Right, -250d, 0d)]
    [InlineData(ResizeEdge.Top, 0d, 200d)]
    [InlineData(ResizeEdge.Bottom, 0d, -200d)]
    public void AccumulateChain_And_SingleAbsoluteApply_AgreeBitForBit_WhenMinSizeClampIsHit(ResizeEdge edge, double totalDx, double totalDy)
    {
        var constraints = FreeConstraints();

        double[] dxSteps = [totalDx / 5, totalDx / 5, totalDx / 5, totalDx / 5, totalDx / 5];
        double[] dySteps = [totalDy / 5, totalDy / 5, totalDy / 5, totalDy / 5, totalDy / 5];

        var chainedRaw = new ResizeRawExtent(StartBounds.Width, StartBounds.Height);
        for (var i = 0; i < dxSteps.Length; i++)
            chainedRaw = ResizeGeometryCalculator.Accumulate(chainedRaw, edge, new Vector(dxSteps[i], dySteps[i]));
        var chainedResult = ResizeGeometryCalculator.Apply(
            StartBounds, edge, chainedRaw, StartBounds.Right, StartBounds.Bottom, constraints);

        var absoluteRaw = new ResizeRawExtent(
            edge.AffectsWidth() ? StartBounds.Width + (edge.AnchorsRight() ? -totalDx : totalDx) : StartBounds.Width,
            edge.AffectsHeight() ? StartBounds.Height + (edge.AnchorsBottom() ? -totalDy : totalDy) : StartBounds.Height);
        var absoluteResult = ResizeGeometryCalculator.Apply(
            StartBounds, edge, absoluteRaw, StartBounds.Right, StartBounds.Bottom, constraints);

        Assert.Equal(chainedResult, absoluteResult);

        // Not vacuous: prove the clamp was actually reached on the dragged axis.
        if (edge.AffectsWidth())
            Assert.Equal(GridMetrics.MinPanelSize, chainedResult.Width);
        else
            Assert.Equal(GridMetrics.MinPanelSize, chainedResult.Height);
    }
}
