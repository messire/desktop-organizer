using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.UI.Interaction.Native;

/// <summary>
/// Pure <c>WM_NCHITTEST</c> zone classifier for a native panel resize — zero WPF dependencies beyond
/// <see cref="NativeRectPx"/>/<see cref="NativePointPx"/> (both in Abstractions), zero mutable state.
/// Border/corner geometry mirrors <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> exactly: a
/// <c>cornerDip x cornerDip</c> square at each of the four corners, overlaid on top of an
/// <c>edgeDip</c>-wide straight-edge strip along the rest of each side (docs/PLANS/
/// phase-2-native-panel-sizing.md §1.4, widened to two independent thicknesses in docs/PLANS/
/// workspace-grid-07-visual.md fix-cycle 2, MAJOR-A). <c>edgeDip</c>/<c>cornerDip</c> are generic
/// caller-supplied values — <c>NativePanelResizeController</c>'s own constants (reading
/// <see cref="DesktopOrganizer.UI.Models.PanelMetrics.ResizeEdgeThickness"/>/
/// <see cref="DesktopOrganizer.UI.Models.PanelMetrics.ResizeCornerThickness"/>) are the single source of
/// truth for the actual production values; this type never re-hardcodes either one (docs/PLANS/
/// workspace-grid-07-visual.md fix-cycle 1, MAJOR-1 — a stale hardcoded value in this very doc comment
/// once drifted out of sync with a widening made elsewhere, do not repeat that here).
/// </summary>
public static class NativeHitTestMapper
{
    /// <summary>
    /// Classifies <paramref name="screenPoint"/> against <paramref name="windowRect"/>'s corner squares
    /// and straight-edge strips. Both are in physical screen pixels (D-3); <paramref name="edgeDip"/>/
    /// <paramref name="cornerDip"/> are DIP widths, converted to pixels via <paramref name="dpiScale"/>
    /// before comparison.
    /// </summary>
    /// <param name="screenPoint">The point to classify, in physical screen pixels (from <c>WM_NCHITTEST</c>'s
    /// <c>lParam</c>, already screen-space per the Win32 contract).</param>
    /// <param name="windowRect">The panel's current <c>GetWindowRect</c> result, in physical screen pixels.</param>
    /// <param name="dpiScale">The panel's current DPI scale (1.0 = 96 DPI), used to convert
    /// <paramref name="edgeDip"/>/<paramref name="cornerDip"/> to pixels.</param>
    /// <param name="edgeDip">The straight-edge strip thickness, in DIP —
    /// <see cref="DesktopOrganizer.UI.Models.PanelMetrics.ResizeEdgeThickness"/> in production, to match
    /// <c>ResizeOverlay</c>'s Grid columns/rows.</param>
    /// <param name="cornerDip">
    /// The corner square width/height, in DIP — <see cref="DesktopOrganizer.UI.Models.PanelMetrics.ResizeCornerThickness"/>
    /// in production, to match <c>ResizeOverlay</c>'s corner <c>Thumb</c>s. Must be <c>&gt;= edgeDip</c>
    /// for the corner-priority classification below to behave sensibly (production always has
    /// <c>cornerDip &gt; edgeDip</c> — see <c>PanelMetricsTests</c>); a caller passing a SMALLER
    /// <paramref name="cornerDip"/> than <paramref name="edgeDip"/> gets a mathematically well-defined but
    /// visually nonsensical result (a corner zone narrower than the edge strip it is meant to widen),
    /// which is not a case any production call site constructs.
    /// </param>
    /// <param name="enabled">When <see langword="false"/> (panel rolled up), resizing is disabled and this
    /// always returns <see cref="NativeSizingEdge.None"/>, regardless of where <paramref name="screenPoint"/>
    /// falls.</param>
    /// <returns>
    /// The edge/corner under <paramref name="screenPoint"/>, or <see cref="NativeSizingEdge.None"/> when the
    /// point is outside <paramref name="windowRect"/>, in the non-resizable center area, or resizing is
    /// disabled. A point within <paramref name="cornerDip"/> of BOTH edges meeting at a corner always
    /// resolves to that corner, even where it is also within <paramref name="edgeDip"/> of just one of
    /// them — this mirrors <c>ResizeOverlay</c>'s own corner <c>Thumb</c>s being declared after (so
    /// visually/hit-test on top of) the straight-edge ones in <c>ChildWindow.xaml</c>. This also
    /// naturally covers windows narrower/shorter than twice <paramref name="cornerDip"/>, where the
    /// left/right (or top/bottom) corner bands overlap across the whole window.
    /// </returns>
    public static NativeSizingEdge Map(
        NativePointPx screenPoint,
        NativeRectPx windowRect,
        double dpiScale,
        double edgeDip,
        double cornerDip,
        bool enabled)
    {
        if (!enabled)
            return NativeSizingEdge.None;

        if (screenPoint.X < windowRect.Left || screenPoint.X >= windowRect.Right ||
            screenPoint.Y < windowRect.Top || screenPoint.Y >= windowRect.Bottom)
            return NativeSizingEdge.None;

        var edgePx = edgeDip * dpiScale;
        var cornerPx = cornerDip * dpiScale;

        // Corner bands (wider) take priority — checked first, against cornerPx on BOTH axes.
        var nearLeftCorner = screenPoint.X < windowRect.Left + cornerPx;
        var nearRightCorner = screenPoint.X >= windowRect.Right - cornerPx;
        var nearTopCorner = screenPoint.Y < windowRect.Top + cornerPx;
        var nearBottomCorner = screenPoint.Y >= windowRect.Bottom - cornerPx;

        if (nearLeftCorner && nearTopCorner)
            return NativeSizingEdge.TopLeft;
        if (nearRightCorner && nearTopCorner)
            return NativeSizingEdge.TopRight;
        if (nearLeftCorner && nearBottomCorner)
            return NativeSizingEdge.BottomLeft;
        if (nearRightCorner && nearBottomCorner)
            return NativeSizingEdge.BottomRight;

        // Straight-edge bands (narrower) — only reached once every corner combination above has missed.
        var nearLeftEdge = screenPoint.X < windowRect.Left + edgePx;
        var nearRightEdge = screenPoint.X >= windowRect.Right - edgePx;
        var nearTopEdge = screenPoint.Y < windowRect.Top + edgePx;
        var nearBottomEdge = screenPoint.Y >= windowRect.Bottom - edgePx;

        if (nearLeftEdge)
            return NativeSizingEdge.Left;
        if (nearRightEdge)
            return NativeSizingEdge.Right;
        if (nearTopEdge)
            return NativeSizingEdge.Top;
        if (nearBottomEdge)
            return NativeSizingEdge.Bottom;

        return NativeSizingEdge.None;
    }
}
