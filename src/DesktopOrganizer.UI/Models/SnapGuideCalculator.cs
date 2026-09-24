using System.Windows;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The six ways the moving panel's own perimeter/centreline can magnetically align with another live
/// panel while in Edit Layout (docs/PLANS/phase-6-edit-layout-ux.md §1 D-7, ADR-026). Each kind aligns
/// the MOVING panel's edge/centre with the SAME edge/centre of the obstacle — e.g. <see cref="LeftEdge"/>
/// lines up <c>moving.Left</c> with <c>obstacle.Left</c>, never with <c>obstacle.Right</c>. "Touching"
/// alignment (e.g. <c>moving.Right</c> flush against <c>obstacle.Left</c>) is deliberately not a guide
/// kind here — that is what <see cref="PanelCollision.ClampAgainstObstacles"/> already enforces as a hard
/// wall, not a soft magnet.
/// </summary>
public enum SnapGuideKind
{
    /// <summary><c>moving.Left</c> aligns with the obstacle's <c>Left</c>.</summary>
    LeftEdge,

    /// <summary><c>moving.Right</c> aligns with the obstacle's <c>Right</c>.</summary>
    RightEdge,

    /// <summary><c>moving.Top</c> aligns with the obstacle's <c>Top</c>.</summary>
    TopEdge,

    /// <summary><c>moving.Bottom</c> aligns with the obstacle's <c>Bottom</c>.</summary>
    BottomEdge,

    /// <summary>The moving panel's horizontal centre aligns with the obstacle's horizontal centre.</summary>
    CenterX,

    /// <summary>The moving panel's vertical centre aligns with the obstacle's vertical centre.</summary>
    CenterY,
}

/// <summary>
/// One active alignment guide. <paramref name="Coordinate"/> is the world-space line the moving panel
/// snapped onto — the screen X for the three vertical-line kinds (<see cref="SnapGuideKind.LeftEdge"/>/
/// <see cref="SnapGuideKind.RightEdge"/>/<see cref="SnapGuideKind.CenterX"/>), or the screen Y for the
/// three horizontal-line kinds. <paramref name="Against"/> is the obstacle panel's rectangle the guide is
/// measured against — kept for a future overlay renderer (docs/PLANS/phase-6-edit-layout-ux.md §1 D-10,
/// P6-6) to draw the guide line spanning from the moving panel to this rectangle; not consumed by this
/// phase's pipeline (P6-5), which only reads the corrected position.
/// </summary>
public readonly record struct SnapGuide(SnapGuideKind Kind, double Coordinate, Rect Against);

/// <summary>
/// Result of <see cref="SnapGuideCalculator.Snap"/>: the (possibly guide-corrected) top-left position of
/// the moving panel, plus the guides that produced it — at most one per axis (two total), per the plan's
/// "one active guide per axis" rule.
/// </summary>
public readonly record struct SnapGuideResult(Point Position, IReadOnlyList<SnapGuide> Guides);

/// <summary>
/// Pure static helper for inter-panel alignment ("snap guide") magnetism — the Fences-parity Edit Layout
/// feature (docs/PLANS/phase-6-edit-layout-ux.md §1 D-7). No WPF <see cref="System.Windows.DependencyObject"/>
/// and no P/Invoke — the only WPF dependency is the plain <see cref="Rect"/>/<see cref="Point"/> structs
/// (WindowsBase), exactly like <see cref="GridSnap"/> and <see cref="PanelCollision"/>. Fifth
/// computational helper alongside <see cref="GridMetrics"/> (ADR-005), <see cref="GridSnap"/> (ADR-011),
/// <see cref="PanelCollision"/> (ADR-017), and <see cref="AdaptiveGridLayout"/> (ADR-018) — deliberately
/// separate from all four (the plan's §0 forbids editing any of them); the only relationship is call
/// order, fixed by <c>Behaviors.PanelDragMoveBehavior.OnTitleBarMouseMove</c>: <see cref="GridSnap"/> runs
/// first, this runs second (and ONLY while <c>ILayoutEditModeService.IsEditing</c> is
/// <see langword="true"/> — the Normal-mode pipeline never calls this type at all), and
/// <see cref="PanelCollision.ClampAgainstObstacles"/> always runs last (R-8 — the anti-overlap "hard
/// wall" must never be pushed through by a guide correction).
/// </summary>
public static class SnapGuideCalculator
{
    /// <summary>
    /// Magnetic pull distance, in DIU, for entering a guide's alignment zone. Deliberately a SEPARATE
    /// constant from <see cref="IAppSettings.GridSnapDistance"/> — a different magnet (a neighbouring
    /// panel's edge/centre, not the icon grid) with different semantics; conflating the two behind one
    /// user-facing slider would make that setting's meaning unclear (see the plan's Q-3). Not currently
    /// exposed as a user setting.
    /// </summary>
    public const double DefaultThreshold = 8.0;

    /// <summary>
    /// Deterministic tolerance band applied on top of the caller-supplied threshold, exactly mirroring
    /// <c>PanelCollision.Eps</c> (0.5 DIU) — see that constant's remarks for the underlying rationale.
    /// Widens the "is this candidate within the magnet" comparison just enough that accumulated
    /// <see cref="double"/> rounding noise between two calls with an (almost) identical cursor position
    /// can never flip a candidate in or out of range purely from floating-point jitter — the actual
    /// magnet radius users perceive is still governed by <paramref name="threshold"/> passed to
    /// <see cref="Snap"/>, this only stabilises the boundary.
    /// </summary>
    private const double Eps = 0.5;

    /// <summary>
    /// One axis's winning candidate: the obstacle it was measured against, which of the three per-axis
    /// <see cref="SnapGuideKind"/> values it represents, the corrected moving-rectangle coordinate
    /// (<see cref="NewCoordinate"/> — a <c>Left</c> or <c>Top</c> value), the world-space guide line
    /// itself (<see cref="LineCoordinate"/>), and the absolute distance that made it a candidate.
    /// </summary>
    private readonly record struct Candidate(SnapGuideKind Kind, double NewCoordinate, double LineCoordinate, Rect Obstacle, double AbsDelta);

    /// <summary>
    /// Computes the guide-corrected top-left position for <paramref name="moving"/> and the (up to two)
    /// guides that produced it. Deterministic: for each axis independently, obstacles are scanned in
    /// list order and the FIRST obstacle with any qualifying candidate wins outright — a later obstacle
    /// with a numerically smaller delta never overrides an earlier one. Within that single obstacle, if
    /// more than one of its three per-axis kinds also qualifies, the smallest <c>|delta|</c> wins (ties
    /// broken by kind declaration order: <c>LeftEdge</c>/<c>TopEdge</c> before <c>RightEdge</c>/
    /// <c>BottomEdge</c> before <c>CenterX</c>/<c>CenterY</c>). This index-first rule is what keeps the
    /// result stable frame-to-frame — comparing deltas ACROSS obstacles (instead of only within one) is
    /// exactly the kind of comparison that flickers under floating-point noise when two obstacles are
    /// nearly equidistant.
    /// </summary>
    /// <param name="moving">The panel's rectangle after <see cref="GridSnap"/> has already run (proposed position, real size).</param>
    /// <param name="obstacles">The other live panels' current screen rectangles (same snapshot <see cref="PanelCollision.ClampAgainstObstacles"/> will use afterwards).</param>
    /// <param name="workArea">
    /// The current monitor's work area. Used only as a soft guard: a guide correction that would push the
    /// moving panel's whole rectangle outside <paramref name="workArea"/> on that axis is discarded (that
    /// axis falls back to its pre-guide coordinate) rather than applied. A degenerate/unknown work area
    /// (<c>Width</c> or <c>Height</c> &lt;= 0, e.g. <see cref="Rect.Empty"/>) disables this guard entirely
    /// rather than blocking every guide, so callers that do not care about it can pass a default value.
    /// </param>
    /// <param name="threshold">The magnetic pull distance, in DIU — typically <see cref="DefaultThreshold"/>. Negative values disable guide-snapping entirely.</param>
    /// <returns>The corrected position (unchanged from <paramref name="moving"/>'s origin on any axis with no qualifying guide) and the active guides.</returns>
    public static SnapGuideResult Snap(Rect moving, IReadOnlyList<Rect> obstacles, Rect workArea, double threshold)
    {
        if (obstacles.Count == 0 || threshold < 0
            || moving.Width <= 0 || moving.Height <= 0
            || !double.IsFinite(moving.X) || !double.IsFinite(moving.Y))
        {
            return new SnapGuideResult(new Point(moving.X, moving.Y), []);
        }

        var left = moving.X;
        var top = moving.Y;
        var guides = new List<SnapGuide>(2);

        var xBest = BestXCandidate(moving, obstacles, threshold);
        if (xBest is { } x && StaysOnScreenX(x.NewCoordinate, moving.Width, workArea))
        {
            left = x.NewCoordinate;
            guides.Add(new SnapGuide(x.Kind, x.LineCoordinate, x.Obstacle));
        }

        var yBest = BestYCandidate(moving, obstacles, threshold);
        if (yBest is { } y && StaysOnScreenY(y.NewCoordinate, moving.Height, workArea))
        {
            top = y.NewCoordinate;
            guides.Add(new SnapGuide(y.Kind, y.LineCoordinate, y.Obstacle));
        }

        return new SnapGuideResult(new Point(left, top), guides);
    }

    /// <summary>
    /// Resolves the winning X-axis candidate (<see cref="SnapGuideKind.LeftEdge"/>/<c>RightEdge</c>/
    /// <c>CenterX</c>) across every obstacle, per the index-first determinism rule documented on
    /// <see cref="Snap"/>. Returns <see langword="null"/> if no obstacle has a candidate within
    /// <paramref name="threshold"/> (widened by <see cref="Eps"/>).
    /// </summary>
    private static Candidate? BestXCandidate(Rect moving, IReadOnlyList<Rect> obstacles, double threshold)
    {
        for (var i = 0; i < obstacles.Count; i++)
        {
            var o = obstacles[i];
            Candidate? forThisObstacle = null;

            Consider(SnapGuideKind.LeftEdge, line: o.Left, movingFeature: moving.Left, newCoordinate: o.Left);
            Consider(SnapGuideKind.RightEdge, line: o.Right, movingFeature: moving.Right, newCoordinate: o.Right - moving.Width);
            Consider(SnapGuideKind.CenterX, line: o.Left + (o.Width / 2d), movingFeature: moving.Left + (moving.Width / 2d), newCoordinate: o.Left + (o.Width / 2d) - (moving.Width / 2d));

            // Lowest obstacle index with ANY qualifying candidate wins outright — never compare across
            // obstacles (see Snap's remarks on why that would flicker under floating-point noise).
            if (forThisObstacle is { } candidate) return candidate;

            void Consider(SnapGuideKind kind, double line, double movingFeature, double newCoordinate)
            {
                var absDelta = Math.Abs(movingFeature - line);
                if (absDelta > threshold + Eps) return;
                if (forThisObstacle is { } current && current.AbsDelta <= absDelta) return; // first-declared kind wins an exact tie.
                forThisObstacle = new Candidate(kind, newCoordinate, line, o, absDelta);
            }
        }

        return null;
    }

    /// <summary>
    /// Y-axis twin of <see cref="BestXCandidate"/> — <see cref="SnapGuideKind.TopEdge"/>/<c>BottomEdge</c>/
    /// <c>CenterY</c>, otherwise identical rule.
    /// </summary>
    private static Candidate? BestYCandidate(Rect moving, IReadOnlyList<Rect> obstacles, double threshold)
    {
        for (var i = 0; i < obstacles.Count; i++)
        {
            var o = obstacles[i];
            Candidate? forThisObstacle = null;

            Consider(SnapGuideKind.TopEdge, line: o.Top, movingFeature: moving.Top, newCoordinate: o.Top);
            Consider(SnapGuideKind.BottomEdge, line: o.Bottom, movingFeature: moving.Bottom, newCoordinate: o.Bottom - moving.Height);
            Consider(SnapGuideKind.CenterY, line: o.Top + (o.Height / 2d), movingFeature: moving.Top + (moving.Height / 2d), newCoordinate: o.Top + (o.Height / 2d) - (moving.Height / 2d));

            if (forThisObstacle is { } candidate) return candidate;

            void Consider(SnapGuideKind kind, double line, double movingFeature, double newCoordinate)
            {
                var absDelta = Math.Abs(movingFeature - line);
                if (absDelta > threshold + Eps) return;
                if (forThisObstacle is { } current && current.AbsDelta <= absDelta) return;
                forThisObstacle = new Candidate(kind, newCoordinate, line, o, absDelta);
            }
        }

        return null;
    }

    /// <summary>
    /// Soft guard used by <see cref="Snap"/>: rejects an X-axis guide correction that would leave the
    /// moving panel's whole rectangle outside <paramref name="workArea"/> (fully off the left/right side
    /// of the monitor). A degenerate <paramref name="workArea"/> (<c>Width &lt;= 0</c>, e.g.
    /// <see cref="Rect.Empty"/>) disables the guard (always returns <see langword="true"/>).
    /// </summary>
    private static bool StaysOnScreenX(double newLeft, double width, Rect workArea)
        => workArea.Width <= 0 || (newLeft + width > workArea.Left && newLeft < workArea.Right);

    /// <summary>Y-axis twin of <see cref="StaysOnScreenX"/>.</summary>
    private static bool StaysOnScreenY(double newTop, double height, Rect workArea)
        => workArea.Height <= 0 || (newTop + height > workArea.Top && newTop < workArea.Bottom);
}
