using System.Windows;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Views;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// ADR-011 Amendment 2026-09-24 (resize extension): <see cref="ResizeGestureSession.TryUpdate"/>'s
/// interaction with <see cref="ResizeConstraints.SizeGridStep"/> — per-frame quantisation for a
/// <see cref="PanelGeometryMode.LegacyFreeform"/> gesture, the GridAligned commensurability pass-through,
/// and the widened <c>Alt</c> free-resize gate (now also consulted for a LegacyFreeform gesture whenever
/// <c>SizeGridStep</c> is active, not just for a GridAligned one).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGestureSessionSizeGridStepTests
{
    private const double TitleBarHeight = ChildWindow.TitleBarHeight;
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    // ── LegacyFreeform gesture, GridSize=48: TryUpdate quantises per frame (not just at Apply's own
    // characterization level — this proves the session actually wires SizeGridStep through end to end). ──
    [Fact]
    public void LegacyFreeform_GridSizeActive_QuantisesPerFrame_ThroughTheRealSession()
    {
        var constraints = ResizeConstraintsFactory.BuildConstraints(
            new ChildWindowModel { ViewMode = PanelViewMode.List }, snapEnabled: false, TitleBarHeight, gridState: default, gridSize: 48);
        Assert.Equal(48d, constraints.SizeGridStep);

        var session = new ResizeGestureSession(new PanelInteractionTracker());
        var start = new Rect(100d, 100d, 144d, 144d);
        Assert.True(session.TryBegin(ResizeEdge.Right, start, constraints));

        // Single frame, total delta +40 -> raw width 184 -> nearest 48-multiple is 192 (see
        // ResizeGeometryCalculatorSizeGridStepTests for the same arithmetic pinned at the Apply level).
        Assert.True(session.TryUpdate(ResizeEdge.Right, new Vector(40d, 0d), start, out var applied));

        Assert.Equal(192d, applied.Width);
        Assert.Equal(start.Left, applied.Left);
    }

    // ── GridAligned, GridSize=48 (commensurate with U=8): per-frame quantisation lands on BOTH the
    // workspace U-grid AND the coarser GridSize lattice — Apply's SizeGridStep branch produces the
    // coarse value, and the subsequent U-based SnapBounds step is then a no-op on it (48 is already an
    // exact multiple of 8). ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void GridAligned_GridSizeCommensurateWithUnit_QuantisesToTheCoarserGridSizeLattice()
    {
        var state = FakePanelSizingGridHost.GridAligned(); // origin (0,0), UnitSize 8, Gap 8
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state, gridSize: 48);
        Assert.Equal(48d, constraints.SizeGridStep);

        // Start on BOTH grids: Left=96 (48*2, also 8*12) and Width=336 (48*7, also a valid U-span extent
        // since (336+8)/8 = 43 exactly).
        var start = new Rect(96d, 96d, 336d, 336d);
        var result = RunGesture(constraints, state, ResizeEdge.Right, start, new Vector(140d, 0d));

        Assert.Equal(480d, result.Width); // 48*10 — see the header comment's derivation
        Assert.Equal(0d, result.Width % 48d);
        Assert.Equal(0d, result.Width % U); // still on the U grid too — the two are commensurate
        Assert.Equal(start.Left, result.Left); // anchor untouched
    }

    // ── GridAligned, GridSize=50 (NOT commensurate with U=8): ResizeConstraintsFactory already resolves
    // SizeGridStep to 0 for this shape (see ResizeConstraintsFactorySizeGridStepTests) — this test proves
    // the SESSION-level result is then bit-for-bit IDENTICAL to the pre-amendment (GridSize<=1) behaviour,
    // i.e. pure U-grid quantisation, completely undisturbed by the incommensurate GridSize setting. ──────
    [Fact]
    public void GridAligned_GridSizeNotCommensurateWithUnit_BehavesExactlyLikeGridSizeOff()
    {
        var state = FakePanelSizingGridHost.GridAligned();
        var start = new Rect(101d, 203d, 317d, 243d); // deliberately off-grid, matches other GridAligned suites
        var delta = new Vector(37d, 0d);

        var withIncommensurateGridSize = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state, gridSize: 50);
        var withoutGridSize = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight, state, gridSize: 1);
        Assert.Equal(0d, withIncommensurateGridSize.SizeGridStep);
        Assert.Equal(withoutGridSize, withIncommensurateGridSize); // the two constraint shapes are IDENTICAL

        var resultWith = RunGesture(withIncommensurateGridSize, state, ResizeEdge.Right, start, delta);
        var resultWithout = RunGesture(withoutGridSize, state, ResizeEdge.Right, start, delta);

        Assert.Equal(resultWithout, resultWith);
    }

    // ── Alt (widened gate): for a LegacyFreeform gesture with SizeGridStep active, Alt now suppresses
    // quantisation for that frame only — the edge tracks the cursor 1:1, exactly like ResizeGeometryCalculator
    // .Apply's OWN free/icon-grid path (i.e. as if SizeGridStep were 0 for this one frame). ───────────────
    [Fact]
    public void AltHeld_SuppressesSizeGridStepQuantisation_ForLegacyFreeform_ForThatFrameOnly()
    {
        var constraints = ResizeConstraintsFactory.BuildConstraints(
            new ChildWindowModel { ViewMode = PanelViewMode.List }, snapEnabled: false, TitleBarHeight, gridState: default, gridSize: 48);
        var start = new Rect(100d, 100d, 144d, 144d);
        var delta = new Vector(40d, 0d); // would quantise to 192 if NOT suppressed

        var altDown = true;
        var session = new ResizeGestureSession(new PanelInteractionTracker(), () => altDown);
        Assert.True(session.TryBegin(ResizeEdge.Right, start, constraints));

        Assert.True(session.TryUpdate(ResizeEdge.Right, delta, start, out var free));

        var raw = ResizeGeometryCalculator.Accumulate(new ResizeRawExtent(start.Width, start.Height), ResizeEdge.Right, delta);
        var unquantisedConstraints = constraints with { SizeGridStep = 0d };
        var unquantised = ResizeGeometryCalculator.Apply(start, ResizeEdge.Right, raw, start.Right, start.Bottom, unquantisedConstraints);
        Assert.Equal(unquantised, free);
        Assert.NotEqual(192d, free.Width); // sanity: genuinely NOT the quantised answer

        // Releasing Alt takes effect on the very next frame, exactly like the GridAligned case.
        altDown = false;
        Assert.True(session.TryUpdate(ResizeEdge.Right, new Vector(0d, 0d), free, out var quantised));
        Assert.Equal(0d, quantised.Width % 48d);
    }

    // ── The counterpart of ResizeGestureSessionFixCycleTests.ALegacyGesture_NeverAsksForTheModifier:
    // once SizeGridStep is active, a LegacyFreeform gesture DOES consult Alt every frame — the widened
    // IsFreeResizeFrame gate applies regardless of PanelGeometryMode. ─────────────────────────────────────
    [Fact]
    public void ALegacyGestureWithSizeGridStepActive_DoesAskForTheModifier()
    {
        var asked = 0;
        var session = new ResizeGestureSession(
            new PanelInteractionTracker(),
            () =>
            {
                asked++;
                return false;
            });
        var constraints = ResizeConstraintsFactory.BuildConstraints(
            new ChildWindowModel { ViewMode = PanelViewMode.List }, snapEnabled: false, TitleBarHeight, gridState: default, gridSize: 48);
        var start = new Rect(100d, 100d, 144d, 144d);

        Assert.True(session.TryBegin(ResizeEdge.Right, start, constraints));
        Assert.True(session.TryUpdate(ResizeEdge.Right, new Vector(40d, 0d), start, out _));

        Assert.Equal(1, asked);
    }

    // ── And the inverse sanity: with SizeGridStep == 0 (GridSize <= 1, the project default), a
    // LegacyFreeform gesture still never asks — the pre-amendment "pointless per-frame cost" guard is
    // unaffected for the overwhelmingly common (GridSize-off) case. ────────────────────────────────────
    [Fact]
    public void ALegacyGestureWithoutSizeGridStep_StillNeverAsksForTheModifier()
    {
        var asked = 0;
        var session = new ResizeGestureSession(
            new PanelInteractionTracker(),
            () =>
            {
                asked++;
                return true;
            });
        var constraints = ResizeConstraintsFactory.BuildConstraints(model: null, snapEnabled: false, TitleBarHeight);
        var start = new Rect(101d, 203d, 317d, 243d);

        Assert.True(session.TryBegin(ResizeEdge.Right, start, constraints));
        Assert.True(session.TryUpdate(ResizeEdge.Right, new Vector(37d, 0d), start, out _));

        Assert.Equal(0, asked);
    }

    private static Rect RunGesture(
        ResizeConstraints constraints, PanelGridState state, ResizeEdge edge, Rect start, Vector totalDelta, int frames = 4)
    {
        var session = new ResizeGestureSession(new PanelInteractionTracker());
        Assert.True(session.TryBegin(edge, start, constraints, state));

        var current = start;
        var frameDelta = new Vector(totalDelta.X / frames, totalDelta.Y / frames);
        for (var i = 0; i < frames; i++)
        {
            Assert.True(session.TryUpdate(edge, frameDelta, current, out var applied));
            current = applied;
        }

        session.End(ResizeEndReason.Completed);
        return current;
    }
}
