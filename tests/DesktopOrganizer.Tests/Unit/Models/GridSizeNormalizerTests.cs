using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="GridSizeNormalizer"/> — re-aligning an already-persisted
/// <c>PanelGeometryMode.LegacyFreeform</c> panel's bounds onto the CURRENT <c>IAppSettings.GridSize</c>
/// lattice (ADR-011 Amendment 2026-09-24). Numbers below are pinned to the user-reported repro: a panel
/// placed at <c>(64, 64)</c>/<c>64x64</c> while <c>GridSize=32</c>, then the grid changed to <c>48</c>.
/// </summary>
[Trait(UnitTests, "UI")]
public class GridSizeNormalizerTests
{
    private static readonly Size NoFloor = new(0d, 0d);

    // ── GridSize <= 1 disables normalization entirely ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> leaves the bounds completely unchanged
    /// when <c>gridSize</c> is 1 (normalization disabled).
    /// </summary>
    [Fact]
    public void Normalize_GridSizeOne_ReturnsBoundsUnchanged()
    {
        var bounds = new Rect(64, 64, 64, 64);

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 1, minSize: new Size(120, 120));

        Assert.Equal(bounds, result);
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> leaves the bounds completely unchanged
    /// when <c>gridSize</c> is 0 (normalization disabled).
    /// </summary>
    [Fact]
    public void Normalize_GridSizeZero_ReturnsBoundsUnchanged()
    {
        var bounds = new Rect(64, 64, 64, 64);

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 0, minSize: new Size(120, 120));

        Assert.Equal(bounds, result);
    }

    // ── The user's exact repro: (64,64) 64x64 @ GridSize=32 restored under GridSize=48 ──────────────

    /// <summary>
    /// Pins the user-reported repro: a panel placed at <c>(64, 64)</c>/<c>64x64</c> while
    /// <c>GridSize=32</c> re-aligns its position to the nearest 48-multiple, and its size jumps straight
    /// to the smallest 48-multiple at or above the 120 minimum-size floor.
    /// </summary>
    [Fact]
    public void Normalize_UserRepro_PositionSnapsTo48_SizeGrowsToSmallestMultipleAboveMinSize()
    {
        // Panel was placed at (64,64)/64x64 while GridSize=32 (both already on that OLD lattice). The
        // grid is later changed to 48; neither the position nor the size is a multiple of 48 anymore.
        var bounds = new Rect(64, 64, 64, 64);

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize: new Size(120, 120));

        // Position: nearest multiple of 48 to 64 is 48 (64/48 = 1.333 -> floor(1.833) = 1 -> 48).
        Assert.Equal(48d, result.X);
        Assert.Equal(48d, result.Y);

        // Size: nearest multiple of 48 to 64 is also 48, which is BELOW the 120 floor -> jumps straight
        // to the smallest multiple of 48 that is >= 120, which is 144 (48*3), not 96 (48*2).
        Assert.Equal(144d, result.Width);
        Assert.Equal(144d, result.Height);
    }

    // ── Position rounding: (65,95) -> (48,96) ───────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeCoordinate"/> rounds 65 down to the nearest
    /// 48-multiple, 48.
    /// </summary>
    [Fact]
    public void NormalizeCoordinate_65_RoundsDownTo48()
    {
        // 65/48 = 1.354 -> +0.5 = 1.854 -> floor = 1 -> 48.
        Assert.Equal(48d, GridSizeNormalizer.NormalizeCoordinate(65, 48));
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeCoordinate"/> rounds 95 up to the nearest
    /// 48-multiple, 96.
    /// </summary>
    [Fact]
    public void NormalizeCoordinate_95_RoundsUpTo96()
    {
        // 95/48 = 1.979 -> +0.5 = 2.479 -> floor = 2 -> 96.
        Assert.Equal(96d, GridSizeNormalizer.NormalizeCoordinate(95, 48));
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> snaps a position of <c>(65, 95)</c> to
    /// <c>(48, 96)</c> when the size is already on-grid and above any floor, isolating the position math.
    /// </summary>
    [Fact]
    public void Normalize_Position65x95_SnapsTo48x96()
    {
        var bounds = new Rect(65, 95, 144, 144); // size already on-grid and above any floor, isolates position.

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize: NoFloor);

        Assert.Equal(48d, result.X);
        Assert.Equal(96d, result.Y);
    }

    // ── Tie-break: exactly halfway between two grid lines resolves to the HIGHER one ────────────────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeCoordinate"/> resolves an exact halfway tie
    /// (72 is exactly 1.5 × 48) by rounding up to 96, not down.
    /// </summary>
    [Fact]
    public void NormalizeCoordinate_ExactHalfwayTie_72Over48_RoundsUpTo96()
    {
        // 72/48 = 1.5 exactly -> +0.5 = 2.0 -> floor = 2 -> 96. Documented tie-break: round-half-UP, not
        // Math.Round's banker's (round-to-even) rule GridSnap.SnapAxis uses for the unrelated live-drag
        // magnet — GridSizeNormalizer is a one-shot restore-time correction, not a per-frame magnet, so a
        // fixed, always-the-same-direction tie-break is preferable to one that alternates with parity.
        Assert.Equal(96d, GridSizeNormalizer.NormalizeCoordinate(72, 48));
    }

    // ── GridSize=1 short-circuits the axis-level helpers too ────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeCoordinate"/> returns the raw coordinate
    /// unchanged when <c>gridSize</c> is 1.
    /// </summary>
    [Fact]
    public void NormalizeCoordinate_GridSizeOne_ReturnsRawUnchanged()
    {
        Assert.Equal(103d, GridSizeNormalizer.NormalizeCoordinate(103, 1));
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeExtent"/> returns the raw extent unchanged
    /// when <c>gridSize</c> is 1.
    /// </summary>
    [Fact]
    public void NormalizeExtent_GridSizeOne_ReturnsRawUnchanged()
    {
        Assert.Equal(103d, GridSizeNormalizer.NormalizeExtent(103, 1, minExtent: 500));
    }

    // ── Negative coordinates (monitor left of/above the primary) ───────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeCoordinate"/> applies the same rounding
    /// formula to a negative coordinate, with no sign-sensitive special case, rounding -65 to -48.
    /// </summary>
    [Fact]
    public void NormalizeCoordinate_Negative65_RoundsTowardZeroTo48Negative()
    {
        // -65/48 = -1.354 -> +0.5 = -0.854 -> floor = -1 -> -48. Symmetric with the positive case: the
        // SAME formula, no special-casing for sign, matching GridSnap's/WorkspaceLayoutPolicy's own
        // "no sign-sensitive branch" convention for origin (0,0) math.
        Assert.Equal(-48d, GridSizeNormalizer.NormalizeCoordinate(-65, 48));
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> snaps a position on a monitor left of
    /// or above the primary (negative coordinates) onto the corresponding negative grid line.
    /// </summary>
    [Fact]
    public void Normalize_NegativePosition_SnapsToNegativeGridLine()
    {
        var bounds = new Rect(-65, -65, 144, 144);

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize: NoFloor);

        Assert.Equal(-48d, result.X);
        Assert.Equal(-48d, result.Y);
    }

    // ── Work area clamp: panel would land beyond the right/bottom edge after rounding up ────────────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> clamps a position that would round past
    /// the work area's right edge back to the largest grid multiple whose right edge still fits.
    /// </summary>
    [Fact]
    public void Normalize_PositionWouldExceedWorkArea_ClampsBackInsideOnAGridMultiple()
    {
        // Work area 1920x1032 (e.g. a 1920x1080 monitor with a 48px top taskbar). A panel already
        // normalized to 144-wide sits at x=1900 (about to round to the grid line AT the right edge,
        // which would push it fully off-screen given its width) -> must clamp back to the largest
        // 48-multiple whose right edge still fits: 1920 - 144 = 1776 (already exactly a multiple of 48).
        var bounds = new Rect(1900, 100, 144, 200);
        var workArea = new Rect(0, 0, 1920, 1032);

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize: NoFloor, workArea);

        Assert.Equal(1776d, result.X);
        Assert.Equal(144d, result.Width); // size itself was already inside the work area -> untouched.
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> applies plain nearest-multiple rounding
    /// with no clamp at all when no work area is supplied, even if the result ends up off-screen.
    /// </summary>
    [Fact]
    public void Normalize_NoWorkArea_NeverClampsPosition_EvenIfItWouldEndUpOffscreen()
    {
        var bounds = new Rect(1900, 100, 144, 200);

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize: NoFloor, workArea: null);

        Assert.Equal(1920d, result.X); // plain nearest-multiple rounding, no clamp applied.
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> shrinks a size larger than the work
    /// area down to the largest grid multiple that still fits within it.
    /// </summary>
    [Fact]
    public void Normalize_SizeLargerThanWorkArea_ShrinksToLargestFittingGridMultiple()
    {
        var bounds = new Rect(0, 0, 2000, 200);
        var workArea = new Rect(0, 0, 1920, 1032);

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize: NoFloor, workArea);

        // floor(1920/48)*48 == 1920 itself (an exact multiple) -> width capped at the work area's own width.
        Assert.Equal(1920d, result.Width);
    }

    // ── Idempotency: Normalize(Normalize(x)) == Normalize(x) ────────────────────────────────────────

    /// <summary>
    /// Verifies that applying <see cref="GridSizeNormalizer.Normalize"/> a second time to its own
    /// output is a no-op, across a spread of positions, sizes, grid sizes, and minimum sizes.
    /// </summary>
    /// <param name="x">The bounds' x-coordinate.</param>
    /// <param name="y">The bounds' y-coordinate.</param>
    /// <param name="width">The bounds' width.</param>
    /// <param name="height">The bounds' height.</param>
    /// <param name="gridSize">The grid size passed to <c>Normalize</c>.</param>
    /// <param name="minWidth">The minimum width floor passed to <c>Normalize</c>.</param>
    /// <param name="minHeight">The minimum height floor passed to <c>Normalize</c>.</param>
    [Theory]
    [InlineData(64, 64, 64, 64, 48, 120, 120)]
    [InlineData(65, 95, 300, 300, 48, 0, 0)]
    [InlineData(-65, -65, 200, 200, 32, 0, 0)]
    [InlineData(1900, 100, 144, 200, 48, 0, 0)]
    public void Normalize_ApplyingTwice_ProducesTheSameResultAsOnce(
        double x, double y, double width, double height, double gridSize, double minWidth, double minHeight)
    {
        var bounds = new Rect(x, y, width, height);
        var minSize = new Size(minWidth, minHeight);
        var workArea = new Rect(0, 0, 1920, 1032);

        var once = GridSizeNormalizer.Normalize(bounds, gridSize, minSize, workArea);
        var twice = GridSizeNormalizer.Normalize(once, gridSize, minSize, workArea);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// Verifies that applying <see cref="GridSizeNormalizer.Normalize"/> a second time to its own
    /// output is a no-op even when no work area is supplied.
    /// </summary>
    [Fact]
    public void Normalize_ApplyingTwice_NoWorkArea_ProducesTheSameResultAsOnce()
    {
        var bounds = new Rect(64, 64, 64, 64);
        var minSize = new Size(120, 120);

        var once = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize);
        var twice = GridSizeNormalizer.Normalize(once, gridSize: 48, minSize);

        Assert.Equal(once, twice);
    }

    // ── Already-on-grid bounds are left untouched ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.Normalize"/> leaves bounds unchanged when every
    /// component is already an exact multiple of the grid size.
    /// </summary>
    [Fact]
    public void Normalize_AlreadyOnGrid_ReturnsBoundsUnchanged()
    {
        var bounds = new Rect(96, 144, 192, 240); // every component already a multiple of 48.

        var result = GridSizeNormalizer.Normalize(bounds, gridSize: 48, minSize: NoFloor);

        Assert.Equal(bounds, result);
    }

    // ── Compatibility with the live drag-time magnet (GridSnap, ADR-011): normalize once, then drag ──

    /// <summary>
    /// Task item 5: after <see cref="GridSizeNormalizer.Normalize"/> puts a panel on the CURRENT
    /// <c>GridSize</c> lattice, a subsequent live drag — which nudges the position by whole
    /// <c>GridSize</c> steps and re-snaps via <see cref="GridSnap.SnapAxis"/>
    /// (<c>PanelDragMoveBehavior.OnTitleBarMouseMove</c>) — must never drift the panel back off that same
    /// lattice: every such raw position is already an EXACT multiple of <c>gridSize</c> (distance 0 from
    /// the nearest line), so the magnet is a no-op at any non-negative threshold, including the ADR-011
    /// default of 12px.
    /// </summary>
    [Fact]
    public void Normalize_ThenLiveDragByWholeGridSteps_RemainsExactlyOnTheGrid()
    {
        const int gridSize = 48;
        const int dragMagnetThreshold = 12; // IAppSettings.GridSnapDistance's own default (ADR-011).
        var bounds = new Rect(64, 64, 64, 64);
        var normalized = GridSizeNormalizer.Normalize(bounds, gridSize, new Size(120, 120));
        Assert.Equal(0d, normalized.X % gridSize); // sanity: normalization itself landed on the grid.

        var x = normalized.X;
        foreach (var deltaSteps in new[] { 3, -7, 100, -1 })
        {
            var raw = x + (deltaSteps * gridSize);
            x = GridSnap.SnapAxis(raw, gridSize, dragMagnetThreshold);
            Assert.Equal(0d, x % gridSize);
        }
    }

    // ── NormalizeExtent: below-floor candidate jumps straight to the floor's own ceiling multiple ────

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeExtent"/> jumps a below-floor nearest
    /// multiple straight to the smallest grid multiple at or above the minimum-extent floor.
    /// </summary>
    [Fact]
    public void NormalizeExtent_NearestBelowFloor_JumpsToSmallestMultipleAtOrAboveFloor()
    {
        // Nearest multiple of 48 to 64 is 48 (below the 120 floor) -> must be 144 (48*3), never 96 (48*2).
        Assert.Equal(144d, GridSizeNormalizer.NormalizeExtent(64, gridSize: 48, minExtent: 120));
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeExtent"/> uses the nearest grid multiple
    /// directly when it already meets the minimum-extent floor.
    /// </summary>
    [Fact]
    public void NormalizeExtent_NearestAlreadyMeetsFloor_UsesNearestDirectly()
    {
        Assert.Equal(144d, GridSizeNormalizer.NormalizeExtent(150, gridSize: 48, minExtent: 120));
    }

    /// <summary>
    /// Verifies that <see cref="GridSizeNormalizer.NormalizeExtent"/> returns the minimum-extent floor
    /// itself when that floor is already an exact grid multiple.
    /// </summary>
    [Fact]
    public void NormalizeExtent_FloorItselfIsAnExactMultiple_ReturnsTheFloor()
    {
        Assert.Equal(96d, GridSizeNormalizer.NormalizeExtent(10, gridSize: 48, minExtent: 96));
    }
}
