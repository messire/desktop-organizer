using System.Windows;
using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure static geometry for the workspace panel grid (ADR-028, System B — WHERE a
/// <see cref="PanelGeometryMode.GridAligned"/> panel is and HOW BIG it is). No
/// <see cref="System.Windows.DependencyObject"/>, no dispatcher, no P/Invoke — the only WPF dependency
/// is the plain <see cref="Point"/>/<see cref="Size"/>/<see cref="Rect"/> structs (WindowsBase), same
/// pattern as <see cref="GridSnap"/>/<see cref="PanelCollision"/>/<see cref="AdaptiveGridLayout"/>, fully
/// unit-testable without STA/WPF test infrastructure.
/// <para>
/// Units, everywhere in this type: all <see cref="double"/> parameters/returns are DIP (device-independent
/// pixels) — never physical px, never <see cref="GridMetrics.CellSize"/>/<see cref="AdaptiveGridLayout.SnapExtent"/>
/// (those belong exclusively to System C, the icon layout — see ADR-028 "Три независимые системы
/// координат"). <see cref="GridSpan"/> values are whole workspace grid units
/// (<see cref="WorkspaceGridMetrics.UnitSize"/> DIP each). Device-pixel rounding is delegated entirely to
/// the existing <c>NativeSizingCoordinateMapper</c> (ADR-022 D-3) at the point <c>Window.Left/Top/Width/
/// Height</c> is actually written — this type never rounds to px and never needs to know the DPI scale.
/// </para>
/// <para>
/// <b>No <c>IconSize</c>/<c>ViewMode</c> parameter anywhere in this type, by design</b> (ADR-028 "Правило
/// IconSize/ViewMode не меняют panel bounds"): every method's signature is closed over position/size/grid
/// parameters only, so a <see cref="PanelGeometryMode.GridAligned"/> panel's bounds are structurally
/// incapable of depending on icon content — the exact class of bug (baseline finding F-2) this ADR closes.
/// </para>
/// </summary>
public static class WorkspaceLayoutPolicy
{
    /// <summary>
    /// Quantises one coordinate to the nearest workspace grid line relative to <paramref name="origin"/>
    /// (ADR-028 "px↔DIP": <c>origin + Round((raw - origin) / unitSize) * unitSize</c>). Uses
    /// <see cref="Math.Round(double)"/>'s default banker's (round-to-even) rounding — the same rounding
    /// mode <see cref="GridSnap.SnapAxis"/> already uses for the unrelated legacy magnet-snap, kept
    /// consistent across the "position snap" family per the ADR. Unconditional (no threshold/magnet
    /// distance) — unlike <see cref="GridSnap.SnapAxis"/>, a <see cref="PanelGeometryMode.GridAligned"/>
    /// coordinate is always exactly on the grid, never "close enough".
    /// </summary>
    /// <param name="raw">The raw (unsnapped) coordinate, in DIP, in the same coordinate space as <paramref name="origin"/>.</param>
    /// <param name="origin">
    /// The workspace grid origin along this axis, in DIP — the top-left of the panel's monitor's
    /// <c>WorkArea</c> (ADR-028 "Origin для системы B"), NOT monitor bounds and NOT the global
    /// <c>(0,0)</c>. May be negative (a monitor left of or above the primary monitor) — the formula
    /// requires no special-casing for sign; see the class remarks on why this type never rounds to px.
    /// </param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP. Values &lt;= 0 disable snapping
    /// (returns <paramref name="raw"/> unchanged) — the same "degenerate input is a no-op, never a divide
    /// by zero" contract as <see cref="GridSnap.SnapAxis"/>/<see cref="AdaptiveGridLayout.RowCapacity"/>.</param>
    /// <returns>
    /// The snapped coordinate — always exactly <paramref name="origin"/> plus a whole multiple of
    /// <paramref name="unitSize"/>. Idempotent: <c>SnapCoordinate(SnapCoordinate(x, o, u), o, u) ==
    /// SnapCoordinate(x, o, u)</c>, because the result is always already an exact multiple, and rounding
    /// an exact multiple to itself is the identity.
    /// </returns>
    public static double SnapCoordinate(double raw, double origin, double unitSize)
    {
        if (!double.IsFinite(raw) || !double.IsFinite(origin)) return raw;
        if (!double.IsFinite(unitSize) || unitSize <= 0d) return raw;

        return origin + (Math.Round((raw - origin) / unitSize) * unitSize);
    }

    /// <summary>
    /// Snaps both axes of a point to the workspace grid via <see cref="SnapCoordinate"/>, independently
    /// per axis (mirrors <see cref="GridSnap.Snap"/>'s per-axis shape for the legacy grid).
    /// </summary>
    /// <param name="raw">The raw (unsnapped) point, in DIP.</param>
    /// <param name="origin">
    /// The panel's monitor's work-area origin, in DIP (ADR-028 "Origin для системы B"). Both components
    /// may be negative.
    /// </param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <returns>The snapped point. See <see cref="SnapCoordinate"/> for the idempotency guarantee.</returns>
    public static Point SnapPosition(Point raw, Point origin, double unitSize)
        => new(SnapCoordinate(raw.X, origin.X, unitSize), SnapCoordinate(raw.Y, origin.Y, unitSize));

    /// <summary>
    /// Converts an already-grid-aligned DIP position into its integer (column, row) SLOT indices relative
    /// to <paramref name="origin"/> — the POSITION-side counterpart of <see cref="BoundsToSpans"/> (which
    /// converts a SIZE into a span). Fix-cycle 1, MAJOR-5(a): added for stage 03
    /// (docs/fix-prompts/03-positioning.md, drag/monitor tracking) to persist a
    /// <see cref="PanelGeometryMode.GridAligned"/> panel's POSITION as whole slot coordinates, so it
    /// survives a workspace-grid origin change (taskbar resize, DPI change, monitor hotplug) the exact same
    /// way <see cref="GridSpan"/> already lets a panel's SIZE survive one (ADR-028 "Origin для системы B")
    /// — re-deriving <c>origin + (Col, Row) * unitSize</c> via <see cref="SlotToPosition"/> against the NEW
    /// origin needs no migration step, the same property <see cref="GridSpan"/>'s own doc comment
    /// describes for size.
    /// </summary>
    /// <param name="raw">The DIP position, typically already the output of <see cref="SnapPosition"/>.</param>
    /// <param name="origin">The workspace grid origin, in DIP (ADR-028 "Origin для системы B"). May be negative.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP. Values &lt;= 0 (or non-finite
    /// components anywhere) degrade to slot <c>(0, 0)</c> rather than dividing by zero.</param>
    /// <returns>
    /// The (Col, Row) slot indices — <c>Round((raw - origin) / unitSize)</c> per axis. Exactly on-grid
    /// input round-trips exactly (<see cref="SlotToPosition"/> is its inverse); off-grid input rounds to
    /// the nearest slot, same tie-break as <see cref="SnapCoordinate"/> (banker's rounding).
    /// </returns>
    public static (int Col, int Row) PositionToSlot(Point raw, Point origin, double unitSize)
        => (SlotIndexForCoordinate(raw.X, origin.X, unitSize), SlotIndexForCoordinate(raw.Y, origin.Y, unitSize));

    /// <summary>
    /// Inverse of <see cref="PositionToSlot"/>: resolves a whole (column, row) slot pair back to its DIP
    /// position relative to <paramref name="origin"/>.
    /// </summary>
    /// <param name="slot">The (Col, Row) slot indices. May be negative (a slot left of/above <paramref name="origin"/>).</param>
    /// <param name="origin">The workspace grid origin, in DIP (ADR-028 "Origin для системы B"). May be negative.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <returns><c>origin + slot * unitSize</c>, per axis.</returns>
    public static Point SlotToPosition((int Col, int Row) slot, Point origin, double unitSize)
    {
        var safeUnitSize = double.IsFinite(unitSize) ? unitSize : 0d;
        return new Point(origin.X + (slot.Col * safeUnitSize), origin.Y + (slot.Row * safeUnitSize));
    }

    /// <summary>
    /// Converts a raw DIP size into the smallest integer <see cref="GridSpan"/> whose
    /// <see cref="SpansToBounds"/> covers it (ADR-028 "px↔DIP": <see cref="Math.Ceiling(double)"/>, never
    /// <see cref="Math.Round(double)"/> — a span must never be smaller than the content that asked for
    /// it, the same "never lose a whole unit to a sub-DIP shortfall" reasoning as
    /// <see cref="AdaptiveGridLayout.SnapExtent"/>/ADR-018 M-1). The grid's cell-plus-gap model
    /// (ADR-028 "Модуль U, зазор G") is <c>Extent(n) = n*unitSize - gap</c>, so its inverse is
    /// <c>n = Ceiling((extent + gap) / unitSize)</c>.
    /// </summary>
    /// <param name="size">The raw (unsnapped) width/height, in DIP. Non-finite or non-positive components clamp to <c>0</c> before quantising.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP. Must be &gt; 0 (values &lt;= 0 clamp the result to a 1x1 span, the same defensive floor <see cref="AdaptiveGridLayout.RowCapacity"/> uses for a degenerate cell size).</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP (see <see cref="WorkspaceGridMetrics.DefaultGap"/>).</param>
    /// <returns>
    /// The smallest <see cref="GridSpan"/> (each dimension &gt;= 1) whose <see cref="SpansToBounds"/> is
    /// <c>&gt;=</c> <paramref name="size"/> on both axes. Idempotent together with
    /// <see cref="SpansToBounds"/>: for any span <c>s</c>, <c>BoundsToSpans(SpansToBounds(s, u, g), u, g)
    /// == s</c>, because <see cref="Math.Ceiling(double)"/> of an already-whole number is the identity.
    /// </returns>
    public static GridSpan BoundsToSpans(Size size, double unitSize, double gap)
    {
        var width = double.IsFinite(size.Width) ? Math.Max(0d, size.Width) : 0d;
        var height = double.IsFinite(size.Height) ? Math.Max(0d, size.Height) : 0d;
        var safeGap = double.IsFinite(gap) ? Math.Max(0d, gap) : 0d; // Fix-cycle 2, NIT-F: ADR-028 requires gap >= 0; only IsFinite was enforced before.

        return new GridSpan(SpanCountForExtent(width, unitSize, safeGap), SpanCountForExtent(height, unitSize, safeGap));
    }

    /// <summary>
    /// Converts an integer <see cref="GridSpan"/> into its DIP width/height (ADR-028 "cell + gap" model:
    /// <c>n*unitSize - gap</c> — NOT <c>n*unitSize</c> — so that two panels occupying adjacent slots leave
    /// exactly <paramref name="gap"/> DIP of empty space between them).
    /// </summary>
    /// <param name="span">The span, in whole workspace grid units. Non-positive components clamp to <c>1</c>.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP.</param>
    /// <returns>
    /// The DIP <see cref="Size"/> for <paramref name="span"/>, floored at <c>0</c> on each axis (a span
    /// whose extent would otherwise go negative — only possible with a pathological <paramref name="gap"/>
    /// larger than the span itself — never produces a negative window size).
    /// </returns>
    public static Size SpansToBounds(GridSpan span, double unitSize, double gap)
    {
        var safeUnitSize = double.IsFinite(unitSize) ? unitSize : 0d;
        var safeGap = double.IsFinite(gap) ? Math.Max(0d, gap) : 0d; // Fix-cycle 2, NIT-F: a negative gap must not silently INFLATE the extent beyond n*unitSize.
        var width = (Math.Max(1, span.WidthUnits) * safeUnitSize) - safeGap;
        var height = (Math.Max(1, span.HeightUnits) * safeUnitSize) - safeGap;

        return new Size(Math.Max(0d, width), Math.Max(0d, height));
    }

    /// <summary>
    /// Interactive-resize counterpart of <see cref="BoundsToSpans"/> (fix-cycle 1, MAJOR-3/orchestrator
    /// decision 2026-09-22): rounds to the NEAREST whole span rather than always ceiling upward.
    /// <see cref="BoundsToSpans"/>'s "never lose space" ceiling is the right choice for a one-shot
    /// operation that must never clip existing content (Align to workspace grid / Fit-to-content /
    /// migrating a legacy size) — but wrong for a LIVE resize drag, where the panel edge should track the
    /// cursor symmetrically with no directional bias, matching the legacy
    /// <c>ResizeGeometryCalculator.Apply</c>/<see cref="AdaptiveGridLayout.CountForExtent"/> "nearest"
    /// family.
    /// </summary>
    /// <remarks>
    /// <b>P04 fix-cycle 1, MAJOR-2.</b> Made public. <see cref="BoundsToSpans"/>'s ceiling is also wrong
    /// for the COMMIT step of a resize (<c>PanelSizingGridSupport.PersistSpans</c>), for a different
    /// reason than the live drag: the bounds read back there have been through
    /// <c>NativeSizingCoordinateMapper.ToPx</c>/<c>ToDip</c>, which at 125/150/175% can return up to
    /// <c>1 px</c> (0.8/0.67/0.57 DIP) MORE than the whole-span value the gesture actually applied — more
    /// than <see cref="SpanQuantizationToleranceDip"/> absorbs — so a ceiling ratchets the stored span up
    /// by one unit on every single gesture (312 DIP -&gt; 312.667 -&gt; span 41 -&gt; 320 DIP on restore).
    /// Rounding to the nearest span is immune to that: half a unit (4 DIP) is an order of magnitude more
    /// than any px round-trip error, and a GENUINE half-unit resize is still captured.
    /// </remarks>
    /// <param name="size">The raw (unsnapped) width/height, in DIP.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP.</param>
    /// <returns>The NEAREST <see cref="GridSpan"/> (each dimension &gt;= 1) to <paramref name="size"/>.</returns>
    public static GridSpan RoundBoundsToSpans(Size size, double unitSize, double gap)
    {
        var width = double.IsFinite(size.Width) ? Math.Max(0d, size.Width) : 0d;
        var height = double.IsFinite(size.Height) ? Math.Max(0d, size.Height) : 0d;
        var safeGap = double.IsFinite(gap) ? Math.Max(0d, gap) : 0d; // Fix-cycle 2, NIT-F.

        return new GridSpan(RoundSpanCountForExtent(width, unitSize, safeGap), RoundSpanCountForExtent(height, unitSize, safeGap));
    }

    /// <summary>
    /// Component-wise <c>max</c> of a candidate span against a minimum span.
    /// </summary>
    /// <remarks>
    /// <b>P04 fix-cycle 1, MAJOR-3.</b> Made public: <see cref="PanelGridStateResolver.Resolve"/>'s lazy
    /// span migration derives a span from the panel's LIVE bounds, which can legitimately be smaller than
    /// <see cref="WorkspaceGridMetrics.MinSpanExpanded"/> (a 56 DIP-tall panel restored from a pre-span
    /// document), and must clamp it with the same rule the two <see cref="SnapBounds(Rect,Point,double,double,GridSpan)"/>
    /// overloads already use — otherwise it writes back a span BELOW the floor, which
    /// <c>HasValidPersistedSpan</c> then rejects again on the very next resolve, re-deriving the same
    /// too-small value forever.
    /// </remarks>
    /// <param name="span">The candidate span.</param>
    /// <param name="minSpan">The per-axis floor.</param>
    /// <returns>A span whose every dimension is <c>&gt;=</c> <paramref name="minSpan"/>'s.</returns>
    public static GridSpan ClampToMinimum(GridSpan span, GridSpan minSpan)
        => new(Math.Max(span.WidthUnits, minSpan.WidthUnits), Math.Max(span.HeightUnits, minSpan.HeightUnits));

    /// <summary>
    /// Composes <see cref="SnapPosition"/> and <see cref="BoundsToSpans"/>/<see cref="SpansToBounds"/> to
    /// quantise a whole proposed rectangle to the workspace grid — the "Align to workspace grid"
    /// command's core math (ADR-028): position snaps to the nearest grid line, size rounds UP to the
    /// nearest whole span (clamped to at least <paramref name="minSpan"/>), so content is never clipped
    /// by rounding.
    /// </summary>
    /// <param name="raw">The proposed rectangle, in DIP (e.g. the panel's current on-screen bounds before alignment).</param>
    /// <param name="origin">The panel's monitor's work-area origin, in DIP (ADR-028 "Origin для системы B").</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP.</param>
    /// <param name="minSpan">The minimum span the result must not go below (e.g. <see cref="WorkspaceGridMetrics.MinSpanExpanded"/>/<see cref="WorkspaceGridMetrics.MinSpanCollapsed"/>).</param>
    /// <returns>
    /// A rectangle whose top-left is on the workspace grid and whose size is
    /// <see cref="SpansToBounds"/> of a whole span &gt;= <paramref name="minSpan"/>. Anti-overlap
    /// (<see cref="PanelCollision"/>) is NOT applied here — the caller composes that afterwards (ADR-028
    /// "Align to workspace grid", step 4), same layering as the existing drag pipeline.
    /// </returns>
    public static Rect SnapBounds(Rect raw, Point origin, double unitSize, double gap, GridSpan minSpan)
    {
        WorkspaceGridMetrics.ValidateGap(unitSize, gap); // Fix-cycle 2, MINOR-E: cheap entry validation -- without it, a caller passing a `gap` that isn't a whole multiple of `unitSize` silently violates the "both edges land on the grid" guarantee this whole type promises.

        var position = SnapPosition(raw.TopLeft, origin, unitSize);
        var span = ClampToMinimum(BoundsToSpans(raw.Size, unitSize, gap), minSpan);
        var size = SpansToBounds(span, unitSize, gap);

        return new Rect(position, size);
    }

    /// <summary>
    /// Resize-gesture overload of <see cref="SnapBounds(Rect,Point,double,double,GridSpan)"/>: quantises
    /// <paramref name="raw"/>'s size to a whole span (both axes, per ADR-028 "Resize: quantization" — a
    /// <see cref="PanelGeometryMode.GridAligned"/> panel's bounds are always a whole span on both axes,
    /// regardless of which single edge is being dragged), reusing the existing <see cref="ResizeEdge"/>
    /// (ADR-028 explicitly rejects a second, parallel edge enum — see the ADR's Alternative 5).
    /// <para>
    /// <b>Fix-cycle 1, MAJOR-3 (ADR-028 Amendment 2026-09-22).</b> Two behavior changes from the original
    /// P02 version: (1) this overload now takes <paramref name="origin"/> and quantises the size via
    /// nearest-span ROUNDING (orchestrator decision: interactive resize rounds to the nearest span,
    /// symmetric like the legacy <c>ResizeGeometryCalculator.Apply</c>/<c>AdaptiveGridLayout.CountForExtent</c>
    /// "nearest" family — <see cref="Math.Ceiling(double)"/>, as used by <see cref="BoundsToSpans"/>, is
    /// reserved for the one-shot "never lose space" operations: Align to workspace grid / Fit-to-content /
    /// migrating a legacy size); (2) the edge OPPOSITE <paramref name="edge"/> is kept fixed ONLY when it
    /// is already exactly on the workspace grid relative to <paramref name="origin"/> — otherwise it is
    /// SNAPPED (aligned) as part of this same call (ADR rule: "противоположный край неподвижен, если он на
    /// сетке; если нет — сначала выравнивается"). Without <paramref name="origin"/>, the original
    /// implementation copied <paramref name="raw"/>'s anchor coordinate verbatim, so a panel resized while
    /// its anchor edge was off-grid (e.g. its very first <see cref="PanelGeometryMode.GridAligned"/> resize
    /// right after switching from <see cref="PanelGeometryMode.LegacyFreeform"/>, before an explicit "Align
    /// to workspace grid") stayed off-grid on that edge FOREVER, no matter how many further resizes it went
    /// through — every subsequent call kept re-copying the same off-grid anchor. Because
    /// <see cref="WorkspaceGridMetrics.ValidateGap"/> requires <c>gap</c> to be a whole multiple of
    /// <paramref name="unitSize"/>, <see cref="SpansToBounds"/>'s extent is ALSO always a whole multiple of
    /// <paramref name="unitSize"/> — so snapping just the anchor coordinate (this method) and snapping just
    /// the moving coordinate (also this method, transitively via the quantised <c>size</c>) are sufficient
    /// to guarantee BOTH resulting edges land exactly on the grid, not only the anchor.
    /// </para>
    /// </summary>
    /// <param name="raw">
    /// The gesture's proposed rectangle for this frame, in DIP — same shape as
    /// <c>ResizeGeometryCalculator.Apply</c>'s output: the edge named by <paramref name="edge"/> has
    /// already moved, the opposite edge is still at its gesture-start position (which may or may not
    /// already be on-grid — see the remarks above).
    /// </param>
    /// <param name="origin">
    /// The panel's monitor's work-area origin, in DIP (ADR-028 "Origin для системы B"). May be negative —
    /// see <see cref="SnapCoordinate"/>.
    /// </param>
    /// <param name="edge">The edge/corner being dragged.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP.</param>
    /// <param name="minSpan">The minimum span the result must not go below.</param>
    /// <returns>
    /// A rectangle whose size is a whole span &gt;= <paramref name="minSpan"/>, both of whose edges on
    /// each resized axis are exactly on the workspace grid relative to <paramref name="origin"/>. The edge
    /// opposite <paramref name="edge"/> equals that same edge of <paramref name="raw"/> when it was
    /// already on-grid, or the nearest grid line to it otherwise (see remarks). Panel-vs-panel
    /// anti-overlap clamping (ADR-028 "Resize: ... Anti-overlap", <see cref="ClampResizeToSlots"/>) is NOT
    /// applied here — that is the caller's next pipeline stage.
    /// </returns>
    public static Rect SnapBounds(Rect raw, Point origin, ResizeEdge edge, double unitSize, double gap, GridSpan minSpan)
    {
        WorkspaceGridMetrics.ValidateGap(unitSize, gap); // Fix-cycle 2, MINOR-E: see the position-variant overload's identical note.

        var span = ClampToMinimum(RoundBoundsToSpans(raw.Size, unitSize, gap), minSpan);
        var size = SpansToBounds(span, unitSize, gap);

        var anchorX = edge.AnchorsRight() ? raw.Right : raw.Left;
        var anchorY = edge.AnchorsBottom() ? raw.Bottom : raw.Top;
        var snappedAnchorX = SnapCoordinate(anchorX, origin.X, unitSize);
        var snappedAnchorY = SnapCoordinate(anchorY, origin.Y, unitSize);

        var left = edge.AnchorsRight() ? snappedAnchorX - size.Width : snappedAnchorX;
        var top = edge.AnchorsBottom() ? snappedAnchorY - size.Height : snappedAnchorY;

        return new Rect(left, top, size.Width, size.Height);
    }

    /// <summary>
    /// P04 fix-cycle 1, MAJOR-1: keeps the MOVING edge of a
    /// <see cref="PanelGeometryMode.GridAligned"/> resize inside <paramref name="workAreaDip"/> by
    /// shrinking the span one whole unit at a time (floored at <paramref name="minSpan"/>) — the
    /// "clamp on the frame" counterpart of <see cref="SafeClamp(Rect,Rect,double,double)"/>'s
    /// "never shrink, move instead" rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug this closes.</b> <see cref="SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/>
    /// has no notion of a work area, so a resize could grow the panel straight past the monitor's edge for
    /// as long as the button was held. Nothing objected mid-gesture; the correction arrived only at
    /// mouse-up, where <c>PanelDragMoveBehavior.SnapToGrid</c> -&gt; <c>PanelDragPipeline.ResolveGridDrop</c>
    /// -&gt; <see cref="SafeClamp(Rect,Rect,double,double)"/> pulls an oversized panel back inside by
    /// TRANSLATING it (that method's documented "never shrink size" contract, which is right for a DRAG
    /// and wrong for a resize): work area <c>1920x1040</c>, panel <c>Left=1600 W=280</c>, drag the right
    /// edge <c>+200</c> -&gt; live width 480 -&gt; on release <c>maxSlot = Floor((1920-480)/8) = 180</c>
    /// -&gt; <c>Left = 1440</c>. The user dragged the RIGHT edge and the LEFT edge jumped 160 DIP, which
    /// is precisely the "resize keeps the opposite edge fixed" invariant (DoD D4-3) inverted.
    /// </para>
    /// <para>
    /// <b>Why shrink rather than translate.</b> The anchor edge is, by definition of a resize, the edge
    /// the user is NOT moving; it must survive the whole gesture. So the only degree of freedom left when
    /// the moving edge hits the wall is the span itself. Shrinking also composes correctly with the next
    /// pipeline stage (<see cref="ClampResizeToSlots"/>), which shrinks from the same edge, and makes the
    /// mouse-up <see cref="SafeClamp(Rect,Rect,double,double)"/> a guaranteed no-op: a rectangle that
    /// already fits inside the work area on a whole slot has nothing left to clamp.
    /// </para>
    /// <para>
    /// <b>Floor wins over the wall.</b> When even <paramref name="minSpan"/> does not fit (a monitor
    /// narrower than the minimum panel), the minimum is kept and the overflow is accepted — the same
    /// "floor wins" precedent as <see cref="ClampResizeToSlots"/>'s own <c>minSpan</c> branch. That
    /// residual case is the only one where the mouse-up clamp can still translate the panel.
    /// </para>
    /// </remarks>
    /// <param name="candidate">The proposed, already grid-quantised resize rectangle (the output of <see cref="SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/>).</param>
    /// <param name="edge">The edge/corner being dragged — determines which edge(s) may not leave the work area, and which stay anchored.</param>
    /// <param name="workAreaDip">The panel's monitor's work area, in the same DIP space as <paramref name="candidate"/>. <see cref="Rect.Empty"/>/non-finite/degenerate disables the clamp entirely (fail-safe: a frame with no work area must never resize the panel).</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP.</param>
    /// <param name="minSpan">The span floor the clamp will not go below.</param>
    /// <returns><paramref name="candidate"/> unchanged when it already fits, otherwise the same rectangle with the moving axis/axes shrunk to the largest whole span that fits, anchor edge(s) untouched.</returns>
    public static Rect ClampResizeToWorkArea(Rect candidate, ResizeEdge edge, Rect workAreaDip, double unitSize, double gap, GridSpan minSpan)
    {
        WorkspaceGridMetrics.ValidateGap(unitSize, gap); // Same "validate on entry" rule as every other grid-aware overload here.

        if (!IsFiniteRect(candidate) || workAreaDip.IsEmpty || !IsFiniteRect(workAreaDip)) return candidate;
        if (workAreaDip.Width <= 0d || workAreaDip.Height <= 0d) return candidate;

        var span = BoundsToSpans(candidate.Size, unitSize, gap);
        var widthUnits = span.WidthUnits;
        var heightUnits = span.HeightUnits;

        if (edge.AffectsWidth())
        {
            // The anchor never moves, so the room available to the moving edge is measured FROM it.
            var available = edge.AnchorsRight() ? candidate.Right - workAreaDip.Left : workAreaDip.Right - candidate.Left;
            widthUnits = Math.Max(minSpan.WidthUnits, Math.Min(widthUnits, MaxSpanForExtent(available, unitSize, gap)));
        }

        if (edge.AffectsHeight())
        {
            var available = edge.AnchorsBottom() ? candidate.Bottom - workAreaDip.Top : workAreaDip.Bottom - candidate.Top;
            heightUnits = Math.Max(minSpan.HeightUnits, Math.Min(heightUnits, MaxSpanForExtent(available, unitSize, gap)));
        }

        if (widthUnits == span.WidthUnits && heightUnits == span.HeightUnits) return candidate;

        var shrunk = SpansToBounds(new GridSpan(widthUnits, heightUnits), unitSize, gap);

        // Only the axis that actually changed is recomputed — an untouched axis keeps candidate's own
        // Left/Top/Width/Height bit-for-bit rather than round-tripping through Right - Width.
        var width = widthUnits == span.WidthUnits ? candidate.Width : shrunk.Width;
        var height = heightUnits == span.HeightUnits ? candidate.Height : shrunk.Height;
        var left = widthUnits == span.WidthUnits || !edge.AnchorsRight() ? candidate.Left : candidate.Right - width;
        var top = heightUnits == span.HeightUnits || !edge.AnchorsBottom() ? candidate.Top : candidate.Bottom - height;

        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// Resolves a named <see cref="PanelSizePreset"/> to its DIP size (ADR-028 "Presets"). A thin wrapper
    /// over <see cref="WorkspaceGridMetrics.SpanForPreset"/> + <see cref="SpansToBounds"/> so callers
    /// creating a new panel from a preset never need to touch <see cref="GridSpan"/> directly.
    /// </summary>
    /// <param name="preset">The named preset.</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP.</param>
    /// <returns>The preset's DIP <see cref="Size"/>.</returns>
    public static Size PreferredSize(PanelSizePreset preset, double unitSize, double gap)
        => SpansToBounds(WorkspaceGridMetrics.SpanForPreset(preset), unitSize, gap);

    /// <summary>
    /// <see cref="WorkspaceGridOptions"/>-aware overload of <see cref="PreferredSize(PanelSizePreset,double,double)"/>
    /// (fix-cycle 1, MAJOR-6): the 3-<see langword="double"/> overload above always resolves
    /// <paramref name="preset"/> through <see cref="WorkspaceGridMetrics.SpanForPreset"/> — i.e. the FIXED
    /// default preset table — no matter what <c>unitSize</c>/<c>gap</c> a caller passed in, so a caller
    /// holding a customized <see cref="WorkspaceGridOptions.PresetSpans"/> (the whole reason that field is
    /// a settable field on a configurable options record, per its own doc comment: "a future
    /// <c>WorkspaceGridOptions</c> consumer... could substitute an overridden table") had no way to make
    /// <see cref="PreferredSize(PanelSizePreset,double,double)"/> actually use it. This overload resolves
    /// <paramref name="preset"/> through <paramref name="options"/>'s OWN <see cref="WorkspaceGridOptions.PresetSpans"/>
    /// table first, falling back to <see cref="WorkspaceGridMetrics.SpanForPreset"/> only if
    /// <paramref name="options"/> genuinely has no entry for <paramref name="preset"/> (defense in depth —
    /// <see cref="WorkspaceGridOptions.Validate"/> already rejects an incomplete table, but this method
    /// does not require the caller to have called <see cref="WorkspaceGridOptions.Validate"/> first).
    /// </summary>
    /// <param name="preset">The named preset.</param>
    /// <param name="options">The options whose <see cref="WorkspaceGridOptions.PresetSpans"/>/<see cref="WorkspaceGridOptions.UnitSize"/>/<see cref="WorkspaceGridOptions.Gap"/> resolve the result.</param>
    /// <returns>The preset's DIP <see cref="Size"/>, per <paramref name="options"/>.</returns>
    public static Size PreferredSize(PanelSizePreset preset, WorkspaceGridOptions options)
    {
        var span = options.PresetSpans is not null && options.PresetSpans.TryGetValue(preset, out var optionSpan)
            ? optionSpan
            : WorkspaceGridMetrics.SpanForPreset(preset);

        return SpansToBounds(span, options.UnitSize, options.Gap);
    }

    /// <summary>
    /// Clamps a proposed rectangle's POSITION so it stays inside <paramref name="workAreaDip"/> as far as
    /// possible — mirrors the existing <c>WindowManager.ClampIntoMonitor</c> (M3) clamp, but as a pure
    /// function operating directly on DIP rectangles (ADR-028 table: "SafeClamp — позиционный clamp,
    /// размер не трогает"). Works unmodified for a work area with a negative origin (a monitor left of or
    /// above the primary monitor) — every comparison below is a plain <see cref="double"/> min/max/clamp,
    /// none of them sign-sensitive.
    /// </summary>
    /// <remarks>
    /// <b>Fix-cycle 2, MINOR-D.</b> Renamed from <c>SafeClamp(Rect, Rect)</c>: the original name did not say
    /// which <see cref="PanelGeometryMode"/> it belongs to, unlike its sibling
    /// <see cref="SafeClamp(Rect,Rect,double,double)"/> (whose own name/doc are explicit about being the
    /// <see cref="PanelGeometryMode.GridAligned"/> counterpart) — a future reader had no textual cue that
    /// THIS overload is the one <see cref="PanelGeometryMode.LegacyFreeform"/>/<c>WindowManager.
    /// ClampIntoMonitor</c> actually owns. Safe to rename outright (not add as a second overload) because,
    /// as of fix-cycle 1/2, this method has zero callers anywhere in the runtime pipeline yet (still
    /// architecture-only/pure-math — stage 03/04 wires it up) — only this file's own doc comments and this
    /// type's unit tests referenced the old name, both updated in the same commit.
    /// </remarks>
    /// <param name="proposed">The proposed rectangle, in DIP. Size is preserved exactly — see remarks on <see cref="ClampAxis"/> for what happens when the panel doesn't fit.</param>
    /// <param name="workAreaDip">The target monitor's work area, in DIP (ADR-028 "Origin для системы B" — the same rectangle whose top-left is the workspace grid origin).</param>
    /// <returns>
    /// A rectangle with <paramref name="proposed"/>'s exact <c>Width</c>/<c>Height</c> and a position
    /// clamped so the panel is fully inside <paramref name="workAreaDip"/> when it fits, or pinned to the
    /// work area's top-left (never further clamped/shrunk) when it is larger than the work area on that
    /// axis — the panel stays at least partially visible rather than the size silently shrinking (ADR-028
    /// "Доступность всех окон в multi-monitor", hotplug/work-area-shrink case).
    /// </returns>
    public static Rect SafeClampFreeform(Rect proposed, Rect workAreaDip)
    {
        if (!double.IsFinite(proposed.X) || !double.IsFinite(proposed.Y)
            || !double.IsFinite(proposed.Width) || !double.IsFinite(proposed.Height))
        {
            return proposed;
        }

        var left = ClampAxis(proposed.X, proposed.Width, workAreaDip.X, workAreaDip.Width);
        var top = ClampAxis(proposed.Y, proposed.Height, workAreaDip.Y, workAreaDip.Height);

        return new Rect(left, top, proposed.Width, proposed.Height);
    }

    /// <summary>
    /// Grid-aware counterpart of <see cref="SafeClampFreeform"/> for a
    /// <see cref="PanelGeometryMode.GridAligned"/> panel (fix-cycle 1, MAJOR-2, ADR-028 Amendment
    /// 2026-09-22). The plain continuous overload above can return a position that is NOT itself a
    /// multiple of <paramref name="unitSize"/> from <paramref name="workAreaDip"/>'s top-left (which IS
    /// the workspace grid origin, ADR-028 "Origin для системы B") — e.g. work area width 1366 DIP, panel
    /// width 248 DIP (a Medium preset) gives a continuous clamp of <c>X=1118</c>, which is off-grid. The
    /// very next <see cref="SnapCoordinate"/> then rounds that off-grid position UP to the nearest grid
    /// line (<c>1120</c>), pushing the panel's right edge (<c>1368</c>) PAST the work area's right edge
    /// (<c>1366</c>) — the plain clamp and the position snap fight each other, and the panel ends up
    /// poking outside the monitor it was just clamped into. This overload instead picks the LARGEST grid
    /// slot whose ACTUAL width/height still fits entirely inside <paramref name="workAreaDip"/>
    /// (<c>X=1112</c> in the example above), so the result is simultaneously on-grid AND inside the work
    /// area — no follow-up <see cref="SnapCoordinate"/> call can ever push it back out.
    /// </summary>
    /// <remarks>
    /// <b>Fix-cycle 2, NIT-G.</b> Fix-cycle 1's implementation computed the "does it fit" test from
    /// <c>BoundsToSpans(proposed.Size, ...)</c>'s <see cref="GridSpan"/> — i.e. from the CEILING-quantised
    /// span's own <see cref="SpansToBounds"/> extent — rather than from <paramref name="proposed"/>'s own
    /// actual, un-quantised <c>Width</c>/<c>Height</c> (which is what the returned <see cref="Rect"/>
    /// actually uses — this method never changes size). For most widths that span-derived extent is a few
    /// DIP LARGER than the true width (Ceiling rounds up), which only makes the clamp more conservative —
    /// harmless. But <see cref="SpanCountForExtent"/>'s own MAJOR-1 tolerance (0.5 DIP, deliberately
    /// absorbing DPI round-trip noise) means a width just inside that tolerance window — e.g. 248.5 DIP,
    /// which still quantises to span 32 (extent 248 DIP) rather than bumping to 33 — can make the
    /// span-derived extent up to 0.5 DIP SMALLER than the real width being placed. At a work area sized so
    /// the span-based slot is an exact fit (no floor-rounding slack to absorb the difference), the OLD
    /// formula placed the panel's real (larger) width up to 0.5 DIP PAST <paramref name="workAreaDip"/>'s
    /// far edge — see <c>SafeClamp_GridAware_WidthInsideToleranceWindow_NeverExceedsWorkAreaByMoreThanRounding</c>
    /// for the exact repro (a 248.5 DIP-wide panel in a 1360 DIP work area). Fixed by computing the "does it
    /// fit" test directly from <paramref name="proposed"/>'s own <c>Width</c>/<c>Height</c> — the exact
    /// value actually being placed — instead of round-tripping through <see cref="GridSpan"/> at all;
    /// <paramref name="unitSize"/>/<paramref name="gap"/> are still validated (<paramref name="gap"/> is no
    /// longer read by the position-fit arithmetic itself, only by <see cref="WorkspaceGridMetrics.ValidateGap"/>
    /// below — kept as a parameter for symmetry with every other grid-aware overload in this type, and
    /// because rejecting a malformed <c>(unitSize, gap)</c> pair here, at the point a caller is about to
    /// place a panel, is cheap and catches a misconfiguration close to its source).
    /// </remarks>
    /// <param name="proposed">
    /// The proposed rectangle, in DIP. Size is preserved exactly, same contract as
    /// <see cref="SafeClampFreeform"/> — including the "panel larger than the work area on an axis is
    /// pinned to that axis's start (slot 0, i.e. exactly <paramref name="workAreaDip"/>'s origin), never
    /// shrunk" rule (ADR-028 "Доступность всех окон в multi-monitor").
    /// </param>
    /// <param name="workAreaDip">
    /// The target monitor's work area, in DIP — its top-left doubles as the workspace grid origin
    /// (ADR-028 "Origin для системы B"; unlike the resize-edge <see cref="SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/>
    /// overload, no separate <c>origin</c> parameter is needed here, because a <see cref="SafeClamp(Rect,Rect,double,double)"/>
    /// call only ever makes sense against the panel's OWN monitor's own work area).
    /// </param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP. Validated (see remarks) but not otherwise consumed by this overload.</param>
    /// <returns>
    /// A rectangle whose position is <paramref name="workAreaDip"/>'s top-left plus a whole multiple of
    /// <paramref name="unitSize"/> on each axis, entirely inside <paramref name="workAreaDip"/> when the
    /// panel's ACTUAL extent fits (the largest such slot not exceeding <paramref name="proposed"/>'s own
    /// slot), or pinned to slot <c>0</c> on an axis where the panel is larger than the work area — same
    /// "never shrink size" rule as <see cref="SafeClampFreeform"/>.
    /// </returns>
    public static Rect SafeClamp(Rect proposed, Rect workAreaDip, double unitSize, double gap)
    {
        WorkspaceGridMetrics.ValidateGap(unitSize, gap); // Fix-cycle 2: extends MINOR-E's "validate on entry" to this overload too.

        if (!double.IsFinite(proposed.X) || !double.IsFinite(proposed.Y)
            || !double.IsFinite(proposed.Width) || !double.IsFinite(proposed.Height))
        {
            return proposed;
        }

        var left = ClampAxisToGrid(proposed.X, workAreaDip.X, proposed.Width, unitSize, workAreaDip.Width);
        var top = ClampAxisToGrid(proposed.Y, workAreaDip.Y, proposed.Height, unitSize, workAreaDip.Height);

        return new Rect(left, top, proposed.Width, proposed.Height);
    }

    /// <summary>
    /// Anti-overlap clamp for a <see cref="PanelGeometryMode.GridAligned"/> RESIZE gesture, expressed in
    /// whole grid slots (fix-cycle 1, MAJOR-5(c); ADR-028 "Resize: ... Anti-overlap при resize — закрывает
    /// открытый пункт ADR-017"). Because every <see cref="PanelGeometryMode.GridAligned"/> panel's edges
    /// land on the SAME shared grid (ADR-028's key structural guarantee for this mode), shrinking one grid
    /// unit at a time from the dragged edge can never stop "half inside" a neighbour's cell — the
    /// continuous-AABB case ADR-017 left as an open backlog item for resize. Intended to run immediately
    /// after <see cref="SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/>, before the result is
    /// applied to the window (same "anti-overlap always last" layering as the existing drag pipeline).
    /// </summary>
    /// <param name="candidate">The proposed, already grid-quantised resize rectangle (e.g. the output of <see cref="SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/>).</param>
    /// <param name="edge">The edge/corner being dragged — determines which side(s) may shrink.</param>
    /// <param name="obstacles">The other live panels' current rectangles (same shape as <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect})"/>'s <c>obstacles</c> parameter).</param>
    /// <param name="unitSize">The workspace grid unit <c>U</c>, in DIP.</param>
    /// <param name="gap">The panel gap <c>G</c>, in DIP — also the minimum clearance enforced against every obstacle (same semantics as <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect},double)"/>).</param>
    /// <param name="minSpan">
    /// The minimum span the result must never go below, even if that means it still overlaps an obstacle
    /// — a resize can never make the panel disappear below the floor size just to satisfy anti-overlap
    /// (same "floor wins" precedent as this type's own minimum-span clamps elsewhere).
    /// </param>
    /// <returns>
    /// <paramref name="candidate"/> unchanged if it does not come within <paramref name="gap"/> DIP of any
    /// obstacle that shrinking can ever actually clear (see remarks), or if none of
    /// <paramref name="obstacles"/> is; otherwise the dragged edge is pulled back one whole
    /// <paramref name="unitSize"/> slot at a time (recomputing the opposite edge's fixed position exactly
    /// as <see cref="SnapBounds(Rect,Point,ResizeEdge,double,double,GridSpan)"/> would) until either no
    /// obstacle is within <paramref name="gap"/> DIP or <paramref name="minSpan"/> is reached.
    /// </returns>
    /// <remarks>
    /// <b>Fix-cycle 2, MAJOR-B.</b> An obstacle on the ANCHOR side of <paramref name="candidate"/> — i.e.
    /// one this resize's fixed (non-moving) edge already overlaps or precedes, before a single unit is ever
    /// shrunk — can never be cleared by moving the OPPOSITE edge, because the anchor itself never moves. The
    /// original implementation did not distinguish this from a genuinely resolvable obstacle: it just kept
    /// shrinking every iteration regardless, destroying the panel's size down to <paramref name="minSpan"/>
    /// for zero actual benefit (repro: <c>candidate=(100,100,248,248)</c>, <paramref name="edge"/>=<c>Right</c>
    /// (anchor = <c>Left</c> = 100), obstacle=<c>(50,100,100,248)</c> (X∈[50,150], straddling the anchor) —
    /// 20 iterations later the result was <c>(100,100,88,248)</c>, still overlapping, width destroyed for
    /// nothing). Fixed with a one-time pre-filter (<see cref="CanEverClear"/>), evaluated against
    /// <paramref name="candidate"/>'s ORIGINAL anchor position (the anchor never moves during the loop, so
    /// this only needs to run once, not per iteration): an obstacle that the moving edge(s) can never place
    /// on the far side of, on ANY axis <paramref name="edge"/> actually moves, is dropped from consideration
    /// entirely — it is neither shrunk toward nor allowed to trip the <paramref name="minSpan"/> floor
    /// return; the panel's original <paramref name="candidate"/> size is preserved for it, the same
    /// "pre-existing state is not auto-corrected" spirit as ADR-017's R1, applied here to a resize gesture
    /// instead of a drag. If <see cref="CanEverClear"/> drops every obstacle, <paramref name="candidate"/>
    /// is returned immediately, unshrunk. An obstacle that legitimately sits ahead of the moving edge (the
    /// "обратная ситуация" case) is unaffected by this filter and still shrinks exactly to
    /// <paramref name="gap"/> DIP of clearance as before.
    /// <para>
    /// <b>Fix-cycle 2, MINOR-C.</b> The floor check below now tests <c>shrunkWidth</c>/<c>shrunkHeight</c>
    /// against <paramref name="minSpan"/> only on the axis/axes <paramref name="edge"/> actually moves
    /// (<c>edge.AffectsWidth()</c>/<c>AffectsHeight()</c>). The original code checked BOTH unconditionally,
    /// so a collapsed (rolled-up) panel — whose HEIGHT span (<see cref="WorkspaceGridMetrics.CollapsedHeaderSpan"/>,
    /// 5 as of P04's OQ-4 = A fix; this comment said 4, the pre-fix P02 value, until P04 fix-cycle 1) is
    /// legitimately below the EXPANDED floor (<see cref="WorkspaceGridMetrics.MinHeightSpanExpanded"/>,
    /// 12) — silently no-opped on the very first iteration of a WIDTH-only resize (<paramref name="edge"/> =
    /// <c>Left</c>/<c>Right</c>, which never touches height at all), because the untouched height span
    /// already read as "below minSpan.HeightUnits (12)" even though nothing was ever asking to shrink it.
    /// </para>
    /// </remarks>
    public static Rect ClampResizeToSlots(Rect candidate, ResizeEdge edge, IReadOnlyList<Rect> obstacles, double unitSize, double gap, GridSpan minSpan)
    {
        WorkspaceGridMetrics.ValidateGap(unitSize, gap); // Fix-cycle 2, MINOR-E (also enforces unitSize > 0 and finite).

        if (obstacles.Count == 0)
            return candidate;

        var relevant = FilterObstaclesResolvableByThisEdge(candidate, edge, obstacles);
        if (relevant.Count == 0)
            return candidate; // MAJOR-B: every obstacle is anchor-side / otherwise unresolvable by this edge -- nothing to do.

        var span = BoundsToSpans(candidate.Size, unitSize, gap);
        var current = candidate;

        while (OverlapsAnyWithGap(current, relevant, gap))
        {
            var widthMoves = edge.AffectsWidth();
            var heightMoves = edge.AffectsHeight();
            var shrunkWidth = widthMoves ? span.WidthUnits - 1 : span.WidthUnits;
            var shrunkHeight = heightMoves ? span.HeightUnits - 1 : span.HeightUnits;

            // MINOR-C: only the axis/axes this edge actually moves are checked against the floor.
            if ((widthMoves && shrunkWidth < minSpan.WidthUnits) || (heightMoves && shrunkHeight < minSpan.HeightUnits))
                return current; // MinSpan floor reached -- accept the residual overlap rather than violate it.

            span = new GridSpan(shrunkWidth, shrunkHeight);
            var size = SpansToBounds(span, unitSize, gap);

            var left = edge.AnchorsRight() ? current.Right - size.Width : current.Left;
            var top = edge.AnchorsBottom() ? current.Bottom - size.Height : current.Top;
            current = new Rect(left, top, size.Width, size.Height);
        }

        return current;
    }

    /// <summary>
    /// One-axis half of <see cref="SafeClamp(Rect,Rect,double,double)"/>: picks the largest slot index
    /// <c>k &gt;= 0</c> such that <c>origin + k*unitSize + extent &lt;= origin + areaExtent</c> — solving
    /// for <c>k</c> gives <c>maxSlot = Floor((areaExtent - extent) / unitSize)</c>. Clamped to <c>&gt;= 0</c>:
    /// a negative <c>maxSlot</c> means the panel does not fit on this axis at all — pin to slot <c>0</c>
    /// (i.e. exactly <paramref name="origin"/>), mirroring <see cref="ClampAxis"/>'s "never shrink, pin to
    /// start" rule. A tiny epsilon guards the <see cref="Math.Floor(double)"/> call from an exact-fit case
    /// landing one slot short purely from binary floating-point representation error.
    /// </summary>
    /// <remarks>
    /// Fix-cycle 2, NIT-G: <paramref name="extent"/> is <paramref name="position"/>'s panel's ACTUAL,
    /// un-quantised <c>Width</c>/<c>Height</c> — deliberately NOT re-derived from a <see cref="GridSpan"/>
    /// (as fix-cycle 1's version did via a <c>spanUnits</c>/<c>gap</c> pair). See
    /// <see cref="SafeClamp(Rect,Rect,double,double)"/>'s remarks for why that round trip could place the
    /// real rectangle up to 0.5 DIP past <paramref name="areaExtent"/>.
    /// </remarks>
    private static double ClampAxisToGrid(double position, double origin, double extent, double unitSize, double areaExtent)
    {
        if (!double.IsFinite(unitSize) || unitSize <= 0d || !double.IsFinite(origin) || !double.IsFinite(areaExtent) || !double.IsFinite(extent))
            return position;

        const double epsilon = 1e-9;

        var maxSlotRaw = (areaExtent - extent) / unitSize;
        var maxSlot = double.IsFinite(maxSlotRaw) ? Math.Floor(maxSlotRaw + epsilon) : 0d;
        if (maxSlot < 0d) maxSlot = 0d;

        var proposedSlot = Math.Round((position - origin) / unitSize);
        var slot = Math.Clamp(proposedSlot, 0d, maxSlot);

        return origin + (slot * unitSize);
    }

    /// <summary>
    /// One-axis half of <see cref="SafeClampFreeform"/>: clamps <paramref name="position"/> into
    /// <c>[areaStart, areaStart + areaExtent - extent]</c> when the panel fits on this axis
    /// (<paramref name="extent"/> &lt;= <paramref name="areaExtent"/>), or pins it to
    /// <paramref name="areaStart"/> when it doesn't — never shrinks <paramref name="extent"/> itself.
    /// </summary>
    private static double ClampAxis(double position, double extent, double areaStart, double areaExtent)
    {
        if (!double.IsFinite(areaStart) || !double.IsFinite(areaExtent) || areaExtent <= 0d) return position;

        var maxStart = areaStart + areaExtent - extent;
        return maxStart < areaStart ? areaStart : Math.Clamp(position, areaStart, maxStart);
    }

    /// <summary>
    /// The largest whole span whose <see cref="SpansToBounds"/> extent still fits into
    /// <paramref name="available"/> DIP — the inverse of the "cell + gap" model solved for <c>n</c> with
    /// <see cref="Math.Floor(double)"/> instead of <see cref="Math.Ceiling(double)"/>, floored at
    /// <c>1</c>. A non-finite/negative <paramref name="available"/> (an anchor that already sits outside
    /// the work area) yields <c>1</c>, i.e. "nothing fits" — the caller then falls back to its
    /// <c>minSpan</c> floor.
    /// </summary>
    private static int MaxSpanForExtent(double available, double unitSize, double gap)
    {
        if (!double.IsFinite(unitSize) || unitSize <= 0d || !double.IsFinite(available)) return int.MaxValue;

        var safeGap = double.IsFinite(gap) ? Math.Max(0d, gap) : 0d;
        var raw = (available + safeGap) / unitSize;
        if (!double.IsFinite(raw)) return int.MaxValue;

        // Epsilon: an EXACT fit (available == n*unitSize - gap) must yield n, not n-1, when the division
        // lands a hair below the whole number in binary floating point. Same guard as ClampAxisToGrid.
        const double epsilon = 1e-9;
        return (int)Math.Clamp(Math.Floor(raw + epsilon), 1d, int.MaxValue);
    }

    /// <summary>Every component of <paramref name="rect"/> is finite.</summary>
    private static bool IsFiniteRect(Rect rect)
        => double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    /// <summary>
    /// Tolerance (fix-cycle 1, MAJOR-1), in DIP, subtracted from the raw unit count before
    /// <see cref="Math.Ceiling(double)"/> in <see cref="SpanCountForExtent"/>. Without it, a whole
    /// <see cref="GridSpan"/> converted to DIP (<see cref="SpansToBounds"/>), rounded to physical device
    /// pixels by the existing <c>NativeSizingCoordinateMapper.ToPx</c> (ADR-022 D-3) at a non-"nice" DPI
    /// scale (110%/175%, not just 125%/150%/200%), and converted back via <c>ToDip</c> can come back a
    /// fraction of a DIP LARGER than the exact <c>n*UnitSize-Gap</c> value it started from — e.g. span 12
    /// (88 DIP) -> 97px @110% -> 88.1818 DIP. Plain <see cref="Math.Ceiling(double)"/> would then quantise
    /// that back to span 13, and the NEXT such round-trip would ratchet it to 14, and so on — a panel that
    /// never actually grows on screen slowly inflating every time it round-trips through such a scale.
    /// <c>0.5</c> DIP is deliberately: (1) large enough to absorb this class of error (a single
    /// <see cref="Math.Round(double)"/> to the nearest device pixel is off by at most 0.5 physical px,
    /// which is at most 0.5 DIP at 100% scale and strictly less at any scale &gt;= 100%, the only range
    /// this application's DPI-awareness needs to support — see ADR-028 "DPI-awareness"); and (2) small
    /// enough that a GENUINE size increase of a whole DIP or more (the actual case <see cref="BoundsToSpans"/>
    /// exists to detect, e.g. "Fit panel to content" needing one more row) is never masked. See
    /// <c>SpansToBounds_BoundsToSpans_RoundTrip_PreservesOriginalSpan_AcrossDpiScales...</c> in
    /// <c>WorkspaceLayoutPolicyTests</c> for the regression coverage (100/110/125/150/175/200%).
    /// </summary>
    private const double SpanQuantizationToleranceDip = 0.5d;

    /// <summary>
    /// Inverse of the "cell + gap" extent formula for one axis: <c>n = Ceiling((extent + gap) / unitSize
    /// - eps)</c> (eps = <see cref="SpanQuantizationToleranceDip"/> expressed in raw/unitSize-relative
    /// terms — see that constant's remarks, MAJOR-1), floored at <c>1</c> and clamped to
    /// <see cref="int.MaxValue"/> before the cast to <see langword="int"/> (fix-cycle 1 NIT: an
    /// astronomically large but still <see cref="double.IsFinite(double)"/> <paramref name="extent"/>
    /// could otherwise produce a <see cref="Math.Ceiling(double)"/> result outside <see langword="int"/>'s
    /// range, which is an unchecked, silently-wrong narrowing conversion in C#, not an
    /// <see cref="OverflowException"/> — see <see cref="BoundsToSpans"/>).
    /// </summary>
    private static int SpanCountForExtent(double extent, double unitSize, double gap)
    {
        if (!double.IsFinite(unitSize) || unitSize <= 0d) return 1;

        var raw = (extent + gap) / unitSize;
        if (!double.IsFinite(raw)) return 1;

        var toleranceRaw = SpanQuantizationToleranceDip / unitSize;
        var adjusted = raw - toleranceRaw;
        if (!double.IsFinite(adjusted)) return 1;

        var ceilValue = Math.Ceiling(adjusted);
        var clamped = Math.Clamp(ceilValue, 1d, int.MaxValue);
        return (int)clamped;
    }

    /// <summary>
    /// Nearest-span counterpart of <see cref="SpanCountForExtent"/> (see <see cref="RoundBoundsToSpans"/>).
    /// </summary>
    private static int RoundSpanCountForExtent(double extent, double unitSize, double gap)
    {
        if (!double.IsFinite(unitSize) || unitSize <= 0d) return 1;

        var raw = (extent + gap) / unitSize;
        if (!double.IsFinite(raw)) return 1;

        // Round HALF UP (Math.Floor(t + 0.5)), deliberately NOT Math.Round's banker's (round-to-even)
        // default (fix-cycle 1 orchestrator decision, 2026-09-22): Floor(t + 0.5) gives every integer an
        // EQUAL-WIDTH capture bucket [n-0.5, n+0.5), whereas round-to-even alternates which side of a
        // .5 tie wins from one integer to the next -- undesirable for a live, continuously-dragged resize
        // gesture, where every half-unit boundary should behave the same way, not just the even ones.
        // Agrees with AdaptiveGridLayout.CountForExtent's own MidpointRounding.AwayFromZero choice for
        // every non-negative `raw` (which it always is here -- extent/gap/unitSize are all clamped
        // non-negative above).
        var count = (int)Math.Clamp(Math.Floor(raw + 0.5d), 1d, int.MaxValue);
        return count;
    }

    /// <summary>
    /// Fix-cycle 2, MAJOR-B: filters <paramref name="obstacles"/> down to the ones a
    /// <paramref name="edge"/>-resize of <paramref name="original"/> could EVER clear, evaluated once
    /// against <paramref name="original"/>'s own (never-moving) anchor position/positions. See
    /// <see cref="CanEverClear"/> for the per-obstacle test and <see cref="ClampResizeToSlots"/>'s remarks
    /// for the repro this closes.
    /// </summary>
    private static IReadOnlyList<Rect> FilterObstaclesResolvableByThisEdge(Rect original, ResizeEdge edge, IReadOnlyList<Rect> obstacles)
    {
        List<Rect>? relevant = null;
        for (var i = 0; i < obstacles.Count; i++)
        {
            var o = obstacles[i];
            if (!CanEverClear(original, edge, o)) continue;

            relevant ??= new List<Rect>(obstacles.Count);
            relevant.Add(o);
        }

        return relevant ?? (IReadOnlyList<Rect>)Array.Empty<Rect>();
    }

    /// <summary>
    /// <see langword="true"/> if shrinking <paramref name="original"/> from <paramref name="edge"/> could
    /// EVER put daylight between it and <paramref name="obstacle"/> on at least one of the axes
    /// <paramref name="edge"/> actually moves. For a single axis, the anchor (the edge OPPOSITE the one
    /// being dragged) never moves, so an obstacle already at-or-before the anchor on that axis is
    /// unresolvable BY THIS EDGE, forever, regardless of how far the moving edge retreats — it is not "in
    /// the way", it is already past the wall the resize can build. For a corner edge (both axes move), the
    /// obstacle only needs to be resolvable on ONE of the two axes: two rectangles stop overlapping the
    /// moment EITHER axis's ranges become disjoint, so an obstacle that straddles the anchor on X but is
    /// cleanly resolvable on Y (or vice versa) is still kept.
    /// </summary>
    private static bool CanEverClear(Rect original, ResizeEdge edge, Rect obstacle)
    {
        var affectsWidth = edge.AffectsWidth();
        var affectsHeight = edge.AffectsHeight();

        var widthResolvable = !affectsWidth || (edge.AnchorsRight()
            ? obstacle.Right <= original.Right
            : obstacle.Left >= original.Left);

        var heightResolvable = !affectsHeight || (edge.AnchorsBottom()
            ? obstacle.Bottom <= original.Bottom
            : obstacle.Top >= original.Top);

        return affectsWidth && affectsHeight
            ? widthResolvable || heightResolvable // corner: clearing EITHER axis is enough.
            : widthResolvable && heightResolvable; // pure edge: the untouched axis's own check is trivially true.
    }

    /// <summary>
    /// <see langword="true"/> if <paramref name="rect"/> comes within <paramref name="gap"/> DIP of any
    /// <paramref name="obstacles"/> entry — i.e. <see cref="PanelCollision.Overlaps(Rect,Rect)"/> against
    /// each obstacle inflated outward by <paramref name="gap"/> on all four sides, the same "inflate, then
    /// reuse the existing overlap test" technique as
    /// <see cref="PanelCollision.ClampAgainstObstacles(Rect,double,double,IReadOnlyList{Rect},double)"/>.
    /// </summary>
    private static bool OverlapsAnyWithGap(Rect rect, IReadOnlyList<Rect> obstacles, double gap)
    {
        var safeGap = double.IsFinite(gap) ? Math.Max(0d, gap) : 0d;

        for (var i = 0; i < obstacles.Count; i++)
        {
            var o = obstacles[i];
            var inflated = new Rect(o.X - safeGap, o.Y - safeGap, Math.Max(0d, o.Width + (2d * safeGap)), Math.Max(0d, o.Height + (2d * safeGap)));
            if (PanelCollision.Overlaps(rect, inflated)) return true;
        }

        return false;
    }

    /// <summary>Single-axis half of <see cref="PositionToSlot"/>.</summary>
    private static int SlotIndexForCoordinate(double raw, double origin, double unitSize)
    {
        if (!double.IsFinite(raw) || !double.IsFinite(origin) || !double.IsFinite(unitSize) || unitSize <= 0d) return 0;

        return (int)Math.Round((raw - origin) / unitSize);
    }
}
