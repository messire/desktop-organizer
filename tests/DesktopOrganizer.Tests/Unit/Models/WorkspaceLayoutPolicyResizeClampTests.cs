using System.Windows;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// P04 fix-cycle 1, MAJOR-1: <see cref="WorkspaceLayoutPolicy.ClampResizeToWorkArea"/> — the pure half of
/// "a resize shrinks the moving edge at the wall instead of being TRANSLATED back inside on mouse-up".
/// The session/controller-level half lives in
/// <c>ResizeGestureSessionWorkAreaClampTests</c>/<c>PanelSizingGridSupportCommitTests</c>.
/// </summary>
[Trait(UnitTests, "UI")]
public class WorkspaceLayoutPolicyResizeClampTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    // A single 1080p monitor with a bottom taskbar — the same work area FakePanelSizingGridHost uses.
    private static readonly Rect WorkArea = new(0d, 0d, 1920d, 1040d);

    private static readonly GridSpan MinSpan = WorkspaceGridMetrics.MinSpanExpanded;

    // ── The exact review repro: work area 1920x1040, panel Left=1600 W=280, drag Right +200. ────────
    // Pre-fix the frame stayed 480 wide (Right = 2080, 160 DIP past the wall) and mouse-up's SafeClamp
    // "fixed" it by moving Left from 1600 to 1440. Post-fix the frame itself stops at the wall.

    /// <summary>
    /// Verifies the exact MAJOR-1 review repro: dragging the right edge past the work area shrinks
    /// the resulting span to stop exactly at the wall, leaving the anchor (left edge) untouched,
    /// rather than the old post-fix "translate the whole rectangle back inside" behavior.
    /// </summary>
    [Fact]
    public void RightEdge_GrowingPastTheWorkArea_ShrinksTheSpan_AndNeverMovesTheAnchor()
    {
        var candidate = new Rect(1600d, 200d, 480d, 240d); // what SnapBounds produces for span 61

        var clamped = WorkspaceLayoutPolicy.ClampResizeToWorkArea(candidate, ResizeEdge.Right, WorkArea, U, G, MinSpan);

        Assert.Equal(1600d, clamped.Left);   // the anchor the user never touched
        Assert.Equal(1920d, clamped.Right);  // exactly the wall
        Assert.Equal(320d, clamped.Width);   // 41*8-8 — a whole span, not a partial slot
        Assert.Equal(240d, clamped.Height);  // the untouched axis is bit-for-bit unchanged
        Assert.Equal(200d, clamped.Top);
    }

    // ── All 8 edges, each pushed against the wall(s) it can actually reach. ──────────────────────────

    /// <summary>Supplies every <see cref="ResizeEdge"/> value for theories exercised across all eight resize edges.</summary>
    public static TheoryData<ResizeEdge> EightEdges => new()
    {
        ResizeEdge.Left, ResizeEdge.Right, ResizeEdge.Top, ResizeEdge.Bottom,
        ResizeEdge.TopLeft, ResizeEdge.TopRight, ResizeEdge.BottomLeft, ResizeEdge.BottomRight,
    };

    /// <summary>
    /// Verifies, for every one of the eight resize edges, that an absurd overshoot past the work area
    /// is clamped fully inside it, the anchor edge never moves, and the result is still a whole grid
    /// span at or above the minimum.
    /// </summary>
    /// <param name="edge">The resize edge being dragged, drawn from <see cref="EightEdges"/>.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void EveryEdge_IsKeptInsideTheWorkArea_WithTheAnchorFixed(ResizeEdge edge)
    {
        // 8 DIP of room on the wall(s) this edge moves toward, then an absurd overshoot.
        var start = StartAgainstTheWall(edge, spanWidth: 36, spanHeight: 31);
        var overgrown = Overgrow(start, edge, 400d);

        var clamped = WorkspaceLayoutPolicy.ClampResizeToWorkArea(overgrown, edge, WorkArea, U, G, MinSpan);

        // 1. Inside the work area on every edge the gesture moved.
        if (edge.AffectsWidth())
        {
            Assert.True(clamped.Left >= WorkArea.Left, $"{edge}: left {clamped.Left} escaped the work area");
            Assert.True(clamped.Right <= WorkArea.Right, $"{edge}: right {clamped.Right} escaped the work area");
        }

        if (edge.AffectsHeight())
        {
            Assert.True(clamped.Top >= WorkArea.Top, $"{edge}: top {clamped.Top} escaped the work area");
            Assert.True(clamped.Bottom <= WorkArea.Bottom, $"{edge}: bottom {clamped.Bottom} escaped the work area");
        }

        // 2. The anchor edge never moved — the invariant SafeClamp's translation used to break.
        if (edge.AffectsWidth())
        {
            if (edge.AnchorsRight()) Assert.Equal(overgrown.Right, clamped.Right);
            else Assert.Equal(overgrown.Left, clamped.Left);
        }
        else
        {
            Assert.Equal(overgrown.Left, clamped.Left);
            Assert.Equal(overgrown.Width, clamped.Width);
        }

        if (edge.AffectsHeight())
        {
            if (edge.AnchorsBottom()) Assert.Equal(overgrown.Bottom, clamped.Bottom);
            else Assert.Equal(overgrown.Top, clamped.Top);
        }
        else
        {
            Assert.Equal(overgrown.Top, clamped.Top);
            Assert.Equal(overgrown.Height, clamped.Height);
        }

        // 3. Still a whole span, still above the floor.
        var span = WorkspaceLayoutPolicy.BoundsToSpans(clamped.Size, U, G);
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(span, U, G), clamped.Size);
        Assert.True(span.WidthUnits >= MinSpan.WidthUnits);
        Assert.True(span.HeightUnits >= MinSpan.HeightUnits);
    }

    // ── And the whole point: after the clamp, the mouse-up SafeClamp has nothing left to translate. ──

    /// <summary>
    /// Verifies that once <see cref="WorkspaceLayoutPolicy.ClampResizeToWorkArea"/> has run, the
    /// drop-time <see cref="WorkspaceLayoutPolicy.SafeClamp"/> is a no-op — there is nothing left for
    /// it to translate.
    /// </summary>
    /// <param name="edge">The resize edge being dragged, drawn from <see cref="EightEdges"/>.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void AfterTheClamp_TheDropTimeSafeClamp_IsANoOp(ResizeEdge edge)
    {
        var start = StartAgainstTheWall(edge, spanWidth: 36, spanHeight: 31);
        var clamped = WorkspaceLayoutPolicy.ClampResizeToWorkArea(Overgrow(start, edge, 400d), edge, WorkArea, U, G, MinSpan);

        var dropped = WorkspaceLayoutPolicy.SafeClamp(clamped, WorkArea, U, G);

        Assert.Equal(clamped.X, dropped.X);
        Assert.Equal(clamped.Y, dropped.Y);
    }

    // ── A rectangle that already fits is returned bit-for-bit unchanged (no gratuitous re-quantising). ──

    /// <summary>
    /// Verifies that a rectangle already fully inside the work area is returned bit-for-bit unchanged
    /// by <see cref="WorkspaceLayoutPolicy.ClampResizeToWorkArea"/>, for every resize edge — no
    /// gratuitous re-quantising.
    /// </summary>
    /// <param name="edge">The resize edge passed in, drawn from <see cref="EightEdges"/>.</param>
    [Theory]
    [MemberData(nameof(EightEdges))]
    public void ARectangleThatAlreadyFits_IsReturnedUnchanged(ResizeEdge edge)
    {
        var candidate = new Rect(400d, 304d, 280d, 240d);

        Assert.Equal(candidate, WorkspaceLayoutPolicy.ClampResizeToWorkArea(candidate, edge, WorkArea, U, G, MinSpan));
    }

    // ── "Floor wins": a work area narrower than MinSpan keeps the minimum rather than going below it. ──

    /// <summary>
    /// Verifies that when the work area itself is narrower than the minimum span, the clamp keeps the
    /// minimum span's width rather than shrinking below it to fit the work area.
    /// </summary>
    [Fact]
    public void AWorkAreaNarrowerThanTheMinimumSpan_KeepsTheMinimum()
    {
        var tiny = new Rect(0d, 0d, 64d, 1040d); // 64 DIP wide — far below the 120 DIP floor
        var candidate = new Rect(0d, 0d, 480d, 240d);

        var clamped = WorkspaceLayoutPolicy.ClampResizeToWorkArea(candidate, ResizeEdge.Right, tiny, U, G, MinSpan);

        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(MinSpan, U, G).Width, clamped.Width);
    }

    // ── Negative-origin monitor (secondary left of/above the primary) — nothing here is sign-sensitive. ──

    /// <summary>
    /// Verifies that a work area with negative-coordinate bounds clamps a resize against its own
    /// (negative-coordinate) edges correctly, with nothing sign-sensitive breaking.
    /// </summary>
    [Fact]
    public void ANegativeOriginWorkArea_ClampsAgainstItsOwnEdges()
    {
        var workArea = new Rect(-1366d, -37d, 1366d, 731d);
        var candidate = new Rect(-400d, 0d, 480d, 240d); // Right = 80, well past workArea.Right = 0

        var clamped = WorkspaceLayoutPolicy.ClampResizeToWorkArea(candidate, ResizeEdge.Right, workArea, U, G, MinSpan);

        Assert.Equal(-400d, clamped.Left);
        Assert.True(clamped.Right <= workArea.Right);
        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(WorkspaceLayoutPolicy.BoundsToSpans(clamped.Size, U, G), U, G), clamped.Size);
    }

    // ── Fail-safe: an unusable work area must never resize the panel. ────────────────────────────────

    /// <summary>
    /// Verifies that an empty (unusable) work area disables the clamp entirely — the candidate
    /// rectangle is returned unchanged rather than being resized against a meaningless work area.
    /// </summary>
    [Fact]
    public void AnEmptyWorkArea_DisablesTheClampEntirely()
    {
        var candidate = new Rect(1600d, 200d, 480d, 240d);

        Assert.Equal(candidate, WorkspaceLayoutPolicy.ClampResizeToWorkArea(candidate, ResizeEdge.Right, Rect.Empty, U, G, MinSpan));
    }

    /// <summary>A whole-span rectangle sitting 8 DIP away from every wall <paramref name="edge"/> moves toward.</summary>
    private static Rect StartAgainstTheWall(ResizeEdge edge, int spanWidth, int spanHeight)
    {
        var size = WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(spanWidth, spanHeight), U, G);

        var left = !edge.AffectsWidth() ? 400d
            : edge.AnchorsRight() ? WorkArea.Left + U
            : WorkArea.Right - U - size.Width;

        var top = !edge.AffectsHeight() ? 304d
            : edge.AnchorsBottom() ? WorkArea.Top + U
            : WorkArea.Bottom - U - size.Height;

        return new Rect(left, top, size.Width, size.Height);
    }

    /// <summary>What <c>SnapBounds</c> produces after dragging <paramref name="edge"/> outward by <paramref name="by"/> DIP.</summary>
    private static Rect Overgrow(Rect start, ResizeEdge edge, double by)
    {
        var width = edge.AffectsWidth() ? start.Width + by : start.Width;
        var height = edge.AffectsHeight() ? start.Height + by : start.Height;
        var left = edge.AffectsWidth() && edge.AnchorsRight() ? start.Right - width : start.Left;
        var top = edge.AffectsHeight() && edge.AnchorsBottom() ? start.Bottom - height : start.Top;

        return new Rect(left, top, width, height);
    }
}
