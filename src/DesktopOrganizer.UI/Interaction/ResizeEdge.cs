namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// One of the 8 resize handles a panel exposes (<c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c>, one
/// <c>Thumb</c> per member, <c>Thumb.Tag</c> set to the member's exact name). Extracted as part of
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
/// Parses/classifies <see cref="ResizeEdge"/> from the exact <c>Thumb.Tag</c> strings used in
/// <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c> (8 <c>Thumb</c> elements, one per
/// <see cref="ResizeEdge"/> member). The classification helpers below are deliberately expressed as
/// the same substring tests the original code used
/// (<c>direction.Contains("Left")</c>/<c>"Right"</c>/<c>"Top"</c>/<c>"Bottom"</c>,
/// <c>PanelResizeController.cs:136-205</c> pre-extraction) rather than a hand-written <c>switch</c> —
/// a <c>switch</c> "as convenient" risks silently diverging on a corner case (plan §2.3).
/// </summary>
public static class ResizeEdgeParser
{
    /// <summary>
    /// Parses <paramref name="tag"/> into a <see cref="ResizeEdge"/>. Only the exact 8 strings used as
    /// <c>Thumb.Tag</c> in <c>ChildWindow.xaml:231-238</c> are recognized — comparison is
    /// <see cref="StringComparison.Ordinal"/> (these values come from XAML markup, never from locale-
    /// sensitive user input). Keep this switch and the XAML <c>Tag</c> values in sync by hand; they are
    /// intentionally duplicated (see the remarks on <c>ChildWindow.xaml</c>'s <c>ResizeOverlay</c>).
    /// </summary>
    /// <param name="tag">The candidate <c>Thumb.Tag</c> value (may be <see langword="null"/>).</param>
    /// <param name="edge">The parsed edge, valid only when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="tag"/> is one of the 8 known strings.</returns>
    public static bool TryParse(string? tag, out ResizeEdge edge)
    {
        switch (tag)
        {
            case "Left": edge = ResizeEdge.Left; return true;
            case "Top": edge = ResizeEdge.Top; return true;
            case "Right": edge = ResizeEdge.Right; return true;
            case "Bottom": edge = ResizeEdge.Bottom; return true;
            case "TopLeft": edge = ResizeEdge.TopLeft; return true;
            case "TopRight": edge = ResizeEdge.TopRight; return true;
            case "BottomLeft": edge = ResizeEdge.BottomLeft; return true;
            case "BottomRight": edge = ResizeEdge.BottomRight; return true;
            default:
                edge = default;
                return false;
        }
    }

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
