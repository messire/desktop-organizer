using System.Windows;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure static AABB collision-response helpers for the "panels don't overlap while dragging"
/// feature (see docs/PLANS/panel-anti-overlap.md). No WPF <see cref="DependencyObject"/>, no
/// dispatcher, no P/Invoke — the only WPF dependency is the plain <see cref="Rect"/> struct
/// (WindowsBase), which is available in the test project via <c>UseWPF=true</c> in the root
/// <c>Directory.Build.props</c>. Third computational helper alongside <see cref="GridMetrics"/>
/// (ADR-005) and <see cref="GridSnap"/> (ADR-011); unrelated to either — the only relationship is
/// call order, fixed by <c>ChildWindow.xaml.cs</c> (plan §3.5): grid snap runs first, this runs last.
/// </summary>
public static class PanelCollision
{
    /// <summary>
    /// Overlap tolerance in DIP. Perfectly edge-to-edge rectangles (e.g. the result of a previous
    /// <see cref="ClampAgainstObstacles"/> call) must never register as overlapping — with an exact
    /// <c>&gt; 0</c> threshold, accumulated <see cref="double"/> rounding error could occasionally flip
    /// a truly-flush edge into a positive overlap and re-trigger the block, causing jitter. See plan
    /// §3.2, "Отсутствие дрожания".
    /// </summary>
    private const double Eps = 0.5;

    /// <summary>
    /// Overlap tolerance used ONLY for the R1 "ghost obstacle" amnesty check (an obstacle the panel
    /// was already overlapping before this frame/snap is not allowed to block the move). Deliberately
    /// a separate, much larger constant than <see cref="Eps"/>: <see cref="Eps"/> answers "is this a
    /// real collision" and must stay tight so genuinely-touching panels can still slide apart, while
    /// this threshold answers "was this pre-existing overlap clearly intentional/real, or just
    /// accumulated double-rounding noise around a flush edge". Sharing one constant for both questions
    /// meant a flush pair that drifted to a ~0.50001px overlap (independent <c>Window.Left</c>/
    /// <c>ActualWidth</c> reads across <c>MouseMove</c>, <see cref="ClampAgainstObstacles"/>,
    /// <c>LayoutSnapshotService.Restore</c>, or a DPI change) would silently satisfy the R1 amnesty
    /// test and permanently disable the wall between that pair until they separated completely — a
    /// silent, hard-to-repro regression (finalization MAJOR M-1). A 2px gap between "definitely still
    /// just touching" (&lt;= <see cref="Eps"/>) and "definitely a real pre-existing overlap" (&gt;
    /// <see cref="GhostEps"/>) leaves no borderline zone where rounding noise can flip the amnesty
    /// decision.
    /// </summary>
    private const double GhostEps = 2.0;

    /// <summary>
    /// Strict rectangle overlap with the <see cref="Eps"/> tolerance: edge-touching rectangles
    /// (<c>a.Right == b.X</c>) do NOT count as overlapping — otherwise a panel resting flush against
    /// an obstacle would immediately block its own next move away from it.
    /// </summary>
    /// <param name="a">The first rectangle.</param>
    /// <param name="b">The second rectangle.</param>
    /// <returns><see langword="true"/> if the rectangles overlap by more than <see cref="Eps"/> DIP on both axes.</returns>
    public static bool Overlaps(Rect a, Rect b) => Overlaps(a, b, Eps);

    /// <summary>
    /// Rectangle overlap test with an explicit tolerance. See <see cref="Overlaps(Rect, Rect)"/> (uses
    /// <see cref="Eps"/>) for the "is this a real collision" question, and <see cref="GhostEps"/>'s
    /// remarks for why the R1 amnesty check below uses a different, wider tolerance here instead.
    /// </summary>
    /// <param name="a">The first rectangle.</param>
    /// <param name="b">The second rectangle.</param>
    /// <param name="eps">The overlap tolerance, in DIP.</param>
    /// <returns><see langword="true"/> if the rectangles overlap by more than <paramref name="eps"/> DIP on both axes.</returns>
    private static bool Overlaps(Rect a, Rect b, double eps)
    {
        var overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var overlapY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return overlapX > eps && overlapY > eps;
    }

    /// <summary>
    /// AABB collision response for a single drag frame: clamps the proposed move from
    /// <paramref name="current"/> to (<paramref name="proposedLeft"/>, <paramref name="proposedTop"/>)
    /// so the panel's new position never creates a new overlap with any rectangle in
    /// <paramref name="obstacles"/>. X and Y are resolved independently (plan §3.2) so a panel that
    /// stops against a neighbour on one axis keeps sliding freely along the other ("sliding along the
    /// wall"). Each axis uses a swept rectangle spanning the old-to-new movement range to catch
    /// obstacles that a single large per-frame delta would otherwise tunnel straight through.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="current"/> (not just the proposed rectangle) for two reasons documented
    /// in the plan (§3.1): (1) it fixes the sign of the movement, so a fast frame that plunges deep
    /// into a neighbour is clamped back to the edge it entered from, never "popped out" the opposite
    /// side; and (2) it's the only way to tell "was already overlapping this obstacle" (R1 — an
    /// already-overlapped obstacle is amnestied and does not block movement) from "just overlapped it
    /// this frame" (blocked). <paramref name="obstacles"/> is <see cref="IReadOnlyList{Rect}"/>, walked
    /// with a plain indexed <c>for</c> loop rather than <c>foreach</c>/LINQ, so this never allocates or
    /// boxes an enumerator on the per-mouse-move hot path (plan §5).
    /// </remarks>
    /// <param name="current">The panel's rectangle before this frame's move.</param>
    /// <param name="proposedLeft">The proposed new <c>Left</c> (already grid-snapped by the caller).</param>
    /// <param name="proposedTop">The proposed new <c>Top</c> (already grid-snapped by the caller).</param>
    /// <param name="obstacles">The other live panels' current screen rectangles.</param>
    /// <returns>The clamped rectangle to actually apply to the window.</returns>
    public static Rect ClampAgainstObstacles(Rect current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles)
    {
        var width = current.Width;
        var height = current.Height;

        if (width <= 0 || height <= 0 || obstacles.Count == 0
            || !double.IsFinite(current.X) || !double.IsFinite(current.Y))
            return new Rect(proposedLeft, proposedTop, width, height);

        // ---------- X axis ----------
        var newLeft = proposedLeft;
        var dx = proposedLeft - current.X;
        if (dx != 0)
        {
            var sweepX = new Rect(Math.Min(current.X, proposedLeft), current.Y, Math.Abs(dx) + width, height);

            for (var i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i];
                if (Overlaps(current, o, GhostEps)) continue; // R1: already-overlapped obstacle is a "ghost" this frame.
                if (!Overlaps(sweepX, o)) continue; // Not in the swept path.

                newLeft = dx > 0 ? Math.Min(newLeft, o.X - width) : Math.Max(newLeft, o.Right);
            }

            // Never move backwards past the starting position. Empirically DOES trigger in practice
            // (finalization review): it restores the pre-move position whenever accumulated double
            // rounding pushes an obstacle-clamped `newLeft` a sub-epsilon distance past `current.X` in
            // the wrong direction — without it, that rounding noise would read back as a tiny move in
            // the opposite direction from the one the user is actually dragging (see plan §3.2).
            newLeft = dx > 0 ? Math.Max(newLeft, current.X) : Math.Min(newLeft, current.X);
        }

        // ---------- Y axis (resolved against the already-clamped X) ----------
        var afterX = new Rect(newLeft, current.Y, width, height);
        var newTop = proposedTop;
        var dy = proposedTop - current.Y;
        if (dy != 0)
        {
            var sweepY = new Rect(newLeft, Math.Min(current.Y, proposedTop), width, Math.Abs(dy) + height);

            for (var i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i];
                if (Overlaps(afterX, o, GhostEps)) continue; // R1, evaluated against the post-X-clamp rectangle.
                if (!Overlaps(sweepY, o)) continue;

                newTop = dy > 0 ? Math.Min(newTop, o.Y - height) : Math.Max(newTop, o.Bottom);
            }

            newTop = dy > 0 ? Math.Max(newTop, current.Y) : Math.Min(newTop, current.Y);
        }

        return new Rect(newLeft, newTop, width, height);
    }

    /// <summary>
    /// Used by <c>ChildWindow.SnapToGrid</c> on mouse-up (plan §3.5) to decide, per axis, whether
    /// applying <see cref="GridSnap"/>'s quantised position would create a NEW overlap that the
    /// pre-snap position did not already have. Deliberately checks for a <em>new</em> overlap, not
    /// "any" overlap, so R1 (pre-existing overlaps, e.g. restored from a pre-feature save) never
    /// blocks the grid snap from being applied. The "was <paramref name="before"/> already overlapping
    /// this obstacle" half of that check uses <see cref="GhostEps"/>, not <see cref="Eps"/>, for the
    /// same reason as the R1 checks in <see cref="ClampAgainstObstacles"/> (finalization MAJOR M-1):
    /// otherwise a flush pre-existing pair nudged by rounding noise to a ~0.50001px overlap could read
    /// as "already overlapping" here too and amnesty a snap that actually creates a real new overlap.
    /// </summary>
    /// <param name="before">The panel's rectangle before applying the grid snap to this axis.</param>
    /// <param name="after">The panel's rectangle after applying the grid snap to this axis.</param>
    /// <param name="obstacles">The other live panels' current screen rectangles.</param>
    /// <returns>
    /// <see langword="true"/> if some obstacle overlaps <paramref name="after"/> but did not already
    /// overlap <paramref name="before"/>.
    /// </returns>
    public static bool CreatesNewOverlap(Rect before, Rect after, IReadOnlyList<Rect> obstacles)
    {
        for (var i = 0; i < obstacles.Count; i++)
        {
            var o = obstacles[i];
            if (Overlaps(after, o) && !Overlaps(before, o, GhostEps)) return true;
        }

        return false;
    }
}
