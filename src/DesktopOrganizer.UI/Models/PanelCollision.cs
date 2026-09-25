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
    /// <see cref="ClampAgainstObstacles(Rect, double, double, IReadOnlyList{Rect})"/> call) must never register as overlapping — with an exact
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
    /// <c>ActualWidth</c> reads across <c>MouseMove</c>, <see cref="ClampAgainstObstacles(Rect, double, double, IReadOnlyList{Rect})"/>,
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
        => ClampAgainstObstaclesCore(current, proposedLeft, proposedTop, obstacles, 0d);

    /// <summary>
    /// Gap-aware overload of <see cref="ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect})"/>
    /// (fix-cycle 1, MAJOR-5(b), additive — the zero-gap overload above is UNCHANGED, byte-for-byte, and
    /// keeps serving <see cref="PanelGeometryMode.LegacyFreeform"/>'s existing flush-touching behavior).
    /// A <see cref="PanelGeometryMode.GridAligned"/> panel must stop <paramref name="gap"/> DIP away from a
    /// neighbour (ADR-028's "cell + gap" model — the same visible seam a properly grid-aligned pair of
    /// panels already has by construction once both are on the shared workspace grid), not flush against
    /// it.
    /// </summary>
    /// <remarks>
    /// <b>Fix-cycle 2, MAJOR-A.</b> No longer inflates the obstacle list up front and delegates to the
    /// zero-gap overload verbatim — that made the R1 ghost/amnesty check fire against the INFLATED
    /// rectangle, which let a panel already sitting closer than <paramref name="gap"/> DIP to a real
    /// obstacle (but not actually intersecting it) tunnel straight through on approach. See
    /// <see cref="ClampAgainstObstaclesCore"/>'s remarks for the full repro and the fix (ghost check and a
    /// new "behind the direction of travel" guard both run against the REAL obstacle; only the final wall
    /// position is offset by <paramref name="gap"/>). Zero array allocation either way —
    /// <see cref="InflateX"/>/<see cref="InflateY"/> operate on <see cref="Rect"/> value types.
    /// <para>
    /// <b>P03 carry-over fix.</b> The remaining inflation is now per-axis (<see cref="InflateX"/> for the
    /// horizontal pass, <see cref="InflateY"/> for the vertical one) so an obstacle that is within
    /// <paramref name="gap"/> DIP of the panel on the PERPENDICULAR axis, without overlapping it there, no
    /// longer becomes a wall and no longer blocks sliding along it — see <see cref="InflateX"/>'s remarks.
    /// </para>
    /// </remarks>
    /// <param name="current">The panel's rectangle before this frame's move/resize.</param>
    /// <param name="proposedLeft">The proposed new <c>Left</c>.</param>
    /// <param name="proposedTop">The proposed new <c>Top</c>.</param>
    /// <param name="obstacles">The other live panels' current screen rectangles.</param>
    /// <param name="gap">
    /// The minimum clearance to keep from every obstacle, in DIP. Non-positive/non-finite values
    /// reproduce the zero-gap overload's exact behavior (flush-touching allowed).
    /// </param>
    /// <returns>The clamped rectangle to actually apply to the window, never closer than <paramref name="gap"/> DIP to any obstacle it stopped against.</returns>
    public static Rect ClampAgainstObstacles(Rect current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles, double gap)
    {
        var safeGap = double.IsFinite(gap) && gap > 0d ? gap : 0d;
        return ClampAgainstObstaclesCore(current, proposedLeft, proposedTop, obstacles, safeGap);
    }

    /// <summary>
    /// Used by <c>ChildWindow.SnapToGrid</c> on mouse-up (plan §3.5) to decide, per axis, whether
    /// applying <see cref="GridSnap"/>'s quantised position would create a NEW overlap that the
    /// pre-snap position did not already have. Deliberately checks for a <em>new</em> overlap, not
    /// "any" overlap, so R1 (pre-existing overlaps, e.g. restored from a pre-feature save) never
    /// blocks the grid snap from being applied. The "was <paramref name="before"/> already overlapping
    /// this obstacle" half of that check uses <see cref="GhostEps"/>, not <see cref="Eps"/>, for the
    /// same reason as the R1 checks in <see cref="ClampAgainstObstacles(Rect, double, double, IReadOnlyList{Rect})"/> (finalization MAJOR M-1):
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
    /// Shared implementation behind both the zero-gap overload above and the gap-aware overload below
    /// (fix-cycle 2, MAJOR-A: replaces fix-cycle 1's "inflate every obstacle, then delegate to the zero-gap
    /// overload verbatim" approach, which had a real bug — see remarks). <paramref name="gap"/> is always
    /// exactly <c>0d</c> for the zero-gap public overload, which makes every formula below collapse to
    /// byte-for-byte the same arithmetic the zero-gap overload used before this fix-cycle (proven per-line
    /// in the remarks), so its behavior and its existing tests are unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fix-cycle 2, MAJOR-A: the bug in fix-cycle 1's inflate-then-delegate design.</b> Inflating every
    /// obstacle by <paramref name="gap"/> and then calling the zero-gap overload meant the R1 ghost/amnesty
    /// check (<c>Overlaps(current, o, GhostEps)</c>) ran against the INFLATED rectangle too. A panel already
    /// sitting closer than <paramref name="gap"/> DIP to a REAL obstacle (e.g. 5 DIP away when <c>gap</c> is
    /// 8) — a legitimate, common state that is not a real physical intersection at all — registered as
    /// "already overlapping" the inflated ghost and was amnestied, i.e. let through with NO blocking in
    /// either direction. Concretely: obstacle real X∈[150,250], panel real X∈[45,145] (5 DIP short of the 8
    /// DIP gap), proposed X=200 — inflated obstacle became [142,258], whose overlap with the (unmoved)
    /// <paramref name="current"/> was 3 DIP, comfortably past <c>GhostEps</c> (2.0) — so the entire obstacle
    /// was skipped and the panel jumped straight to X=200, ending up 50 DIP INSIDE the real, physical
    /// obstacle. Zero-gap drags never had this failure mode because <c>GhostEps</c> is compared against the
    /// same (non-inflated) rectangle used for the real collision, so "closer than the wall" and "physically
    /// inside the wall" could never be confused.
    /// </para>
    /// <para>
    /// <b>The fix: real rectangles decide "ghost or blocked", the arithmetic wall position alone shifts by
    /// <paramref name="gap"/>.</b> The R1 ghost check (line 1 of the loop body) always runs against the REAL,
    /// non-inflated <paramref name="obstacles"/>[i] — a genuine pre-existing physical intersection is
    /// amnestied exactly as ADR-017 always required, completely independent of <paramref name="gap"/>. A
    /// second guard — new in fix-cycle 2 — skips any obstacle that is <em>behind</em> the current position in
    /// the direction of travel (<c>o.Right &lt;= current.X</c> for <c>dx &gt; 0</c>, mirrored for the other
    /// three directions): retreating from an obstacle (or from inside its gap zone) is never blocked, no
    /// matter how close <paramref name="current"/> already is to it — this is what keeps
    /// <c>ClampAgainstObstacles_WithGap_MovingAway_Unrestricted</c> passing (an obstacle flush with
    /// <paramref name="current"/>'s trailing edge must never block departure, even though it sits well
    /// within the inflated/gap zone). Only once BOTH guards let an obstacle through does the swept-overlap
    /// test run — against the obstacle inflated by <paramref name="gap"/> (so approaching to within
    /// <paramref name="gap"/> DIP, not just physically touching, is what triggers the wall) — and the actual
    /// stop position is computed directly as <c>o.X - gap - width</c> / <c>o.Right + gap</c> (the real edge
    /// plus/minus the gap offset), not by re-deriving it from an already-inflated rectangle. For the MAJOR-A
    /// repro above, this now yields a wall at X=42 (<c>150-8-100</c>), which the existing "never move past
    /// <paramref name="current"/>" line then holds at the ALREADY-CORRECT X=45 (the pre-existing 5 DIP gap is
    /// not itself corrected/pulled back — ADR-017 R1's "pre-existing states aren't auto-corrected" principle,
    /// extended to the new gap concept — but no further approach is allowed either).
    /// </para>
    /// <para>
    /// This shared core also drops fix-cycle 1's <c>Rect[]</c> allocation entirely — <see cref="InflateX"/>/<see cref="InflateY"/> are
    /// called per-obstacle-per-candidate-block (a <see cref="Rect"/> is a value type; no heap allocation),
    /// so the gap-aware overload is now exactly as allocation-free as the zero-gap one.
    /// </para>
    /// </remarks>
    private static Rect ClampAgainstObstaclesCore(Rect current, double proposedLeft, double proposedTop, IReadOnlyList<Rect> obstacles, double gap)
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
                if (Overlaps(current, o, GhostEps)) continue; // R1: real pre-existing overlap is a "ghost" this frame.
                if (dx > 0 ? o.Right <= current.X : o.X >= current.Right) continue; // MAJOR-A: obstacle is behind the direction of travel — retreating (even from within its gap zone) is never blocked.
                if (!Overlaps(sweepX, InflateX(o, gap))) continue; // Not within `gap` DIP of the swept path ALONG X (Y is compared against the obstacle's real extent -- see InflateX).

                newLeft = dx > 0 ? Math.Min(newLeft, o.X - gap - width) : Math.Max(newLeft, o.Right + gap);
            }

            // Never move backwards past the starting position. Empirically DOES trigger in practice
            // (finalization review): it restores the pre-move position whenever accumulated double
            // rounding pushes an obstacle-clamped `newLeft` a sub-epsilon distance past `current.X` in
            // the wrong direction — without it, that rounding noise would read back as a tiny move in
            // the opposite direction from the one the user is actually dragging (see plan §3.2). Fix-cycle
            // 2: this SAME line is also what correctly holds a pre-existing sub-gap position exactly where
            // it was (see MAJOR-A remarks above) instead of snapping it back to a full `gap` DIP of
            // clearance — "not pulled back, but no closer either".
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
                if (dy > 0 ? o.Bottom <= afterX.Y : o.Y >= afterX.Bottom) continue; // MAJOR-A, vertical mirror.
                if (!Overlaps(sweepY, InflateY(o, gap))) continue; // Horizontal mirror of the X pass: X is compared against the obstacle's real extent.

                newTop = dy > 0 ? Math.Min(newTop, o.Y - gap - height) : Math.Max(newTop, o.Bottom + gap);
            }

            newTop = dy > 0 ? Math.Max(newTop, current.Y) : Math.Min(newTop, current.Y);
        }

        return new Rect(newLeft, newTop, width, height);
    }

    /// <summary>
    /// Inflates <paramref name="rect"/> outward by <paramref name="gap"/> DIP along the X axis ONLY, for
    /// the horizontal pass of <see cref="ClampAgainstObstaclesCore"/>. Returns <paramref name="rect"/>
    /// itself, unmodified, when <paramref name="gap"/> is non-positive (the zero-gap call path —
    /// <see cref="Rect"/> is a value type, so this is never a heap allocation either way).
    /// </summary>
    /// <remarks>
    /// <b>P03 carry-over fix (fix-cycle 2 final verification, MAJOR): the gap widens the wall's POSITION,
    /// never the SET of obstacles that are walls.</b> The previous version inflated all four sides, so the
    /// perpendicular (Y) comparison ran against an obstacle stretched by <paramref name="gap"/> vertically
    /// too. A neighbour that merely sits closer than <paramref name="gap"/> ABOVE the panel — vertically
    /// disjoint from it, therefore never in the way of a purely horizontal move — then registered as
    /// overlapping the swept path and blocked horizontal travel in BOTH directions (the pre-existing
    /// sub-gap proximity is amnesty-exempt by design, R1, so it could not be "corrected" either), while the
    /// exact same move was free at <c>gap = 0</c>. That breaks the "sliding along the wall" invariant this
    /// type's own remarks state (X and Y resolved independently). Comparing the perpendicular axis against
    /// the obstacle's REAL extent restores it: an obstacle is a wall for horizontal movement only when it
    /// genuinely overlaps the panel vertically, and the gap then decides only WHERE that wall stands
    /// (<c>o.X - gap - width</c> / <c>o.Right + gap</c>, unchanged below).
    /// </remarks>
    private static Rect InflateX(Rect rect, double gap)
        => gap <= 0d
            ? rect
            : new Rect(rect.X - gap, rect.Y, Math.Max(0d, rect.Width + (2d * gap)), rect.Height);

    /// <summary>Vertical mirror of <see cref="InflateX"/>, for the Y pass of <see cref="ClampAgainstObstaclesCore"/>.</summary>
    private static Rect InflateY(Rect rect, double gap)
        => gap <= 0d
            ? rect
            : new Rect(rect.X, rect.Y - gap, rect.Width, Math.Max(0d, rect.Height + (2d * gap)));
}
