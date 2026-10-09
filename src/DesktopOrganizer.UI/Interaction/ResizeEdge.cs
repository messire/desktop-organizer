namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// One of the 8 resize edges/corners a panel exposes (the native hit-test zones, see
/// <c>NativeHitTestMapper</c>; the <c>ResizeGripVisuals</c> borders in <c>ChildWindow.xaml</c> carry the
/// member's exact name in their <c>Tag</c>). Extracted as part of
/// Phase 1 resize stabilization (docs/PLANS/phase-1-resize-stabilization.md §2.3) so the resize
/// gesture pipeline can be typed instead of comparing raw strings.
/// </summary>
public enum ResizeEdge
{
    /// <summary>The left edge — moves the panel's left boundary; the right edge stays anchored.</summary>
    Left,

    /// <summary>The top edge — moves the panel's top boundary; the bottom edge stays anchored.</summary>
    Top,

    /// <summary>The right edge — moves the panel's right boundary; the left edge stays anchored.</summary>
    Right,

    /// <summary>The bottom edge — moves the panel's bottom boundary; the top edge stays anchored.</summary>
    Bottom,

    /// <summary>The top-left corner — moves both the top and left boundaries.</summary>
    TopLeft,

    /// <summary>The top-right corner — moves both the top and right boundaries.</summary>
    TopRight,

    /// <summary>The bottom-left corner — moves both the bottom and left boundaries.</summary>
    BottomLeft,

    /// <summary>The bottom-right corner — moves both the bottom and right boundaries.</summary>
    BottomRight,
}

/// <summary>
/// Classifies <see cref="ResizeEdge"/> members by the axes they move and the boundary they anchor. The
/// helpers below are deliberately expressed as the same substring tests the original code used
/// (<c>direction.Contains("Left")</c>/<c>"Right"</c>/<c>"Top"</c>/<c>"Bottom"</c>) rather than a hand-written <c>switch</c> —
/// a <c>switch</c> "as convenient" risks silently diverging on a corner case (plan §2.3).
/// </summary>
public static class ResizeEdgeParser
{
    /// <summary>
    /// <see langword="true"/> for every edge that moves the panel's WIDTH — i.e. every edge whose tag
    /// contained <c>"Left"</c> or <c>"Right"</c> in the original string-based code. <c>Top</c>/<c>Bottom</c>
    /// are the only edges that do NOT affect width.
    /// </summary>
    /// <param name="edge">The edge to test.</param>
    public static bool AffectsWidth(this ResizeEdge edge) =>
        edge is ResizeEdge.Left or ResizeEdge.Right or ResizeEdge.TopLeft or ResizeEdge.TopRight
            or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;

    /// <summary>
    /// <see langword="true"/> for every edge that moves the panel's HEIGHT — mirrors
    /// <see cref="AffectsWidth"/> for the vertical axis. <c>Left</c>/<c>Right</c> are the only edges
    /// that do NOT affect height.
    /// </summary>
    /// <param name="edge">The edge to test.</param>
    public static bool AffectsHeight(this ResizeEdge edge) =>
        edge is ResizeEdge.Top or ResizeEdge.Bottom or ResizeEdge.TopLeft or ResizeEdge.TopRight
            or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;

    /// <summary>
    /// <see langword="true"/> when dragging this edge moves the panel's LEFT edge, which means the
    /// RIGHT edge is the fixed anchor (<c>Window.Left = anchorRight - newWidth</c>) — i.e. every edge
    /// whose tag contains <c>"Left"</c>.
    /// </summary>
    /// <param name="edge">The edge to test.</param>
    public static bool AnchorsRight(this ResizeEdge edge) =>
        edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft;

    /// <summary>
    /// <see langword="true"/> when dragging this edge moves the panel's TOP edge, which means the
    /// BOTTOM edge is the fixed anchor (<c>Window.Top = anchorBottom - newHeight</c>) — i.e. every edge
    /// whose tag contains <c>"Top"</c>.
    /// </summary>
    /// <param name="edge">The edge to test.</param>
    public static bool AnchorsBottom(this ResizeEdge edge) =>
        edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight;
}
