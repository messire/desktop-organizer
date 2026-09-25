using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction.Native;

namespace DesktopOrganizer.Tests.Unit.Interaction.Native;

/// <summary>
/// Phase 2 (commit C2, docs/PLANS/phase-2-native-panel-sizing.md §1.4/§5, T-HIT-01..10) tests for
/// <see cref="NativeHitTestMapper"/>. Rewritten in docs/PLANS/workspace-grid-07-visual.md fix-cycle 2
/// (MAJOR-A) for the new two-thickness <c>Map</c> signature: a <c>CornerDip</c>-square (8 DIP) at each
/// corner, overlaid on an independent, narrower <c>EdgeDip</c>-wide (6 DIP) straight-edge strip —
/// production's actual values (<c>NativePanelResizeController.EdgeDip</c>/<c>CornerDip</c>, reading
/// <c>PanelMetrics.ResizeEdgeThickness</c>/<c>ResizeCornerThickness</c>). Fix-cycle 1's version of this
/// file used a single shared 6 DIP for both roles (pre-MAJOR-1) then a single shared 8 DIP (post-MAJOR-1,
/// pre-MAJOR-A) — neither shape exists in production anymore, so probe points below are chosen so no
/// single probe would pass under BOTH the old single-value scheme and a mis-implemented two-value one:
/// edge probes always sit within <c>EdgeDip</c> of exactly one side AND outside <c>CornerDip</c> of every
/// side (so they could only ever resolve as a straight edge, never a corner), and corner probes sit within
/// <c>CornerDip</c> — but OUTSIDE <c>EdgeDip</c> — of both sides meeting at that corner (so they could only
/// ever resolve as a corner via the WIDER threshold, never coincidentally via the narrower edge one).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeHitTestMapperTests
{
    private const double EdgeDip = 6.0;
    private const double CornerDip = 8.0;

    // 400x300 px window at an arbitrary screen offset (not at the origin, so tests can't accidentally
    // pass via a bug that ignores windowRect.Left/Top).
    private static readonly NativeRectPx WindowRect = new(100, 200, 500, 500);

    /// <summary>Verifies that all eight corner/edge zones plus the dead center resolve to the correct <see cref="NativeSizingEdge"/>, at a given DPI <paramref name="scale"/>.</summary>
    /// <param name="scale">The DPI scale factor applied to the edge/corner thresholds.</param>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Map_AllEightZonesPlusCenter_ResolveCorrectly_AtGivenDpiScale(double scale)
    {
        var edgePx = EdgeDip * scale;
        var cornerPx = CornerDip * scale;

        // Corner probes: strictly between edgePx and cornerPx on BOTH axes from the corner they target —
        // outside the (narrower) edge band entirely, so only the (wider) corner band can catch them.
        var cornerOffset = (edgePx + cornerPx) / 2.0;

        // Edge probes: within edgePx of exactly one side, at the window's midline on the other axis — far
        // from every corner band (midline is always >= cornerPx from either end for this 400x300 window).
        var edgeOffset = edgePx / 2.0;

        NativeSizingEdge At(double dx, double dy) => NativeHitTestMapper.Map(
            new NativePointPx((int)Math.Round(WindowRect.Left + dx), (int)Math.Round(WindowRect.Top + dy)),
            WindowRect,
            scale,
            EdgeDip,
            CornerDip,
            enabled: true);

        Assert.Equal(NativeSizingEdge.TopLeft, At(cornerOffset, cornerOffset));
        Assert.Equal(NativeSizingEdge.TopRight, At(WindowRect.Width - cornerOffset, cornerOffset));
        Assert.Equal(NativeSizingEdge.BottomLeft, At(cornerOffset, WindowRect.Height - cornerOffset));
        Assert.Equal(NativeSizingEdge.BottomRight, At(WindowRect.Width - cornerOffset, WindowRect.Height - cornerOffset));
        Assert.Equal(NativeSizingEdge.Left, At(edgeOffset, WindowRect.Height / 2.0));
        Assert.Equal(NativeSizingEdge.Right, At(WindowRect.Width - edgeOffset, WindowRect.Height / 2.0));
        Assert.Equal(NativeSizingEdge.Top, At(WindowRect.Width / 2.0, edgeOffset));
        Assert.Equal(NativeSizingEdge.Bottom, At(WindowRect.Width / 2.0, WindowRect.Height - edgeOffset));
        Assert.Equal(NativeSizingEdge.None, At(WindowRect.Width / 2.0, WindowRect.Height / 2.0));
    }

    /// <summary>
    /// MAJOR-A regression pin: a point within <see cref="CornerDip"/> but OUTSIDE <see cref="EdgeDip"/> of
    /// BOTH sides meeting at a corner must still resolve to that corner — this is exactly the "wider
    /// corner, narrower edge" shape's whole point (docs/PLANS/workspace-grid-07-visual.md fix-cycle 2), and
    /// is the one probe shape a naive single-threshold implementation (either fix-cycle 1's uniform 8, or a
    /// straight revert to a uniform 6) would get wrong: a uniform-8 mapper would ALSO classify this as a
    /// corner (coincidentally correct here), but a uniform-6 mapper would return <see cref="NativeSizingEdge.None"/>
    /// (both axes exceed 6, its only threshold) instead of a corner.
    /// </summary>
    [Fact]
    public void Map_PointWithinCornerDipButOutsideEdgeDipOfBothSides_ResolvesToCorner()
    {
        // 7 DIP from the top and from the left: > EdgeDip (6) on both axes, < CornerDip (8) on both axes.
        var point = new NativePointPx(WindowRect.Left + 7, WindowRect.Top + 7);

        var edge = NativeHitTestMapper.Map(point, WindowRect, 1.0, EdgeDip, CornerDip, enabled: true);

        Assert.Equal(NativeSizingEdge.TopLeft, edge);
    }

    /// <summary>
    /// The symmetric counterpart to the corner case above: a point within <see cref="CornerDip"/> of only
    /// ONE side (not both) must resolve to the plain straight edge for that one side, via the (narrower)
    /// <see cref="EdgeDip"/> band — not <see cref="NativeSizingEdge.None"/>, and not the corner (which
    /// requires BOTH axes).
    /// </summary>
    [Fact]
    public void Map_PointWithinEdgeDipOfOneSideOnly_ResolvesToStraightEdge_NotCorner()
    {
        // 3 DIP from the left (within EdgeDip), but at the window's vertical midline — nowhere near
        // CornerDip of the top or bottom.
        var point = new NativePointPx(WindowRect.Left + 3, WindowRect.Top + (WindowRect.Height / 2));

        var edge = NativeHitTestMapper.Map(point, WindowRect, 1.0, EdgeDip, CornerDip, enabled: true);

        Assert.Equal(NativeSizingEdge.Left, edge);
    }

    /// <summary>Verifies that a point exactly at the edge boundary, away from every corner, resolves to <see cref="NativeSizingEdge.None"/> rather than an edge.</summary>
    /// <param name="scale">The DPI scale factor applied to the edge threshold.</param>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Map_PointExactlyAtEdgeBoundary_AwayFromCorners_IsCenterNotEdge(double scale)
    {
        var edgePx = (int)(EdgeDip * scale);
        var rect = new NativeRectPx(0, 0, 400, 300);
        var point = new NativePointPx(rect.Left + edgePx, rect.Height / 2);

        var edge = NativeHitTestMapper.Map(point, rect, scale, EdgeDip, CornerDip, enabled: true);

        Assert.Equal(NativeSizingEdge.None, edge);
    }

    /// <summary>Verifies that a point one pixel inside the edge boundary, away from every corner, resolves to the straight edge.</summary>
    /// <param name="scale">The DPI scale factor applied to the edge threshold.</param>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Map_PointOnePixelInsideEdgeBoundary_AwayFromCorners_IsEdge(double scale)
    {
        var edgePx = (int)(EdgeDip * scale);
        var rect = new NativeRectPx(0, 0, 400, 300);
        var point = new NativePointPx(rect.Left + edgePx - 1, rect.Height / 2);

        var edge = NativeHitTestMapper.Map(point, rect, scale, EdgeDip, CornerDip, enabled: true);

        Assert.Equal(NativeSizingEdge.Left, edge);
    }

    /// <summary>Verifies that a point exactly at the corner boundary on one axis, but still within the edge band on the other axis, resolves to the straight edge, not the corner.</summary>
    /// <param name="scale">The DPI scale factor applied to the edge/corner thresholds.</param>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Map_PointExactlyAtCornerBoundary_IsEdgeNotCorner(double scale)
    {
        // Exactly CornerDip from the left, but still within EdgeDip of the top (2 DIP) — the corner
        // band's own boundary excludes this point (">=" is "outside"), but the edge band's Top strip
        // still claims it via the vertical axis.
        var cornerPx = (int)(CornerDip * scale);
        var rect = new NativeRectPx(0, 0, 400, 300);
        var point = new NativePointPx(rect.Left + cornerPx, rect.Top + 2);

        var edge = NativeHitTestMapper.Map(point, rect, scale, EdgeDip, CornerDip, enabled: true);

        Assert.Equal(NativeSizingEdge.Top, edge);
    }

    /// <summary>Verifies that a point outside the window rect entirely resolves to <see cref="NativeSizingEdge.None"/>.</summary>
    /// <param name="x">The probe point's X coordinate, in screen pixels.</param>
    /// <param name="y">The probe point's Y coordinate, in screen pixels.</param>
    [Theory]
    [InlineData(50, 50)] // left of and above the window
    [InlineData(600, 300)] // right of the window
    [InlineData(300, 100)] // above the window
    [InlineData(300, 550)] // below the window
    public void Map_PointOutsideWindowRect_ReturnsNone(int x, int y)
    {
        var edge = NativeHitTestMapper.Map(new NativePointPx(x, y), WindowRect, 1.0, EdgeDip, CornerDip, enabled: true);

        Assert.Equal(NativeSizingEdge.None, edge);
    }

    /// <summary>Verifies that with resize disabled, every one of the eight zones resolves to <see cref="NativeSizingEdge.None"/>.</summary>
    [Fact]
    public void Map_Disabled_AllEightZonesReturnNone()
    {
        var rect = new NativeRectPx(0, 0, 400, 300);
        var points = new[]
        {
            new NativePointPx(3, 3),     // TopLeft
            new NativePointPx(200, 3),   // Top
            new NativePointPx(397, 3),   // TopRight
            new NativePointPx(3, 150),   // Left
            new NativePointPx(397, 150), // Right
            new NativePointPx(3, 297),   // BottomLeft
            new NativePointPx(200, 297), // Bottom
            new NativePointPx(397, 297), // BottomRight
        };

        foreach (var point in points)
        {
            Assert.Equal(NativeSizingEdge.None, NativeHitTestMapper.Map(point, rect, 1.0, EdgeDip, CornerDip, enabled: false));
        }
    }

    /// <summary>Verifies that when the window is smaller than twice the corner threshold, so the corner bands overlap across the whole window, every probe still resolves to a corner.</summary>
    [Fact]
    public void Map_WindowSmallerThanTwiceCorner_OverlappingBands_PrioritizesCorner()
    {
        // 10x10 px window; CornerDip is 8 DIP @ scale 1.0 = 8px, so 2*corner (16) > window size (10) —
        // the left/right (and top/bottom) corner bands overlap across the whole window.
        var rect = new NativeRectPx(0, 0, 10, 10);

        Assert.Equal(NativeSizingEdge.TopLeft, NativeHitTestMapper.Map(new NativePointPx(2, 2), rect, 1.0, EdgeDip, CornerDip, enabled: true));
        Assert.Equal(NativeSizingEdge.TopRight, NativeHitTestMapper.Map(new NativePointPx(8, 2), rect, 1.0, EdgeDip, CornerDip, enabled: true));
        Assert.Equal(NativeSizingEdge.BottomLeft, NativeHitTestMapper.Map(new NativePointPx(2, 8), rect, 1.0, EdgeDip, CornerDip, enabled: true));
        Assert.Equal(NativeSizingEdge.BottomRight, NativeHitTestMapper.Map(new NativePointPx(8, 8), rect, 1.0, EdgeDip, CornerDip, enabled: true));
    }
}
