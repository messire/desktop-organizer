namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure static helpers for icon-grid geometry. No WPF or P/Invoke dependencies — fully unit-testable.
/// </summary>
public static class GridMetrics
{
    /// <summary>
    /// Fixed height of the name label (<c>TextBlock</c>/<c>TextBox</c> <c>Height="32"</c> in
    /// GridViewTemplate — see the doc comment on those elements in Templates.xaml, which points back
    /// here, the same "point at each other" pattern already used for <see cref="ItemMargin"/> ↔
    /// <c>Margin="5"</c>). Used by <see cref="RowHeight"/> to compute the cell's full vertical footprint.
    /// </summary>
    public const double LabelHeight = 32d;

    /// <summary>
    /// Total vertical chrome of FileListHost outside the icon grid itself
    /// (<c>FileListHost.Margin="0,0,0,4"</c> in ChildWindow.xaml). Analogous to
    /// <see cref="FileListHPadding"/> but for the vertical axis. As of the adaptive-icon-grid feature
    /// this is subtracted before quantising panel height (<c>Resize_DragDelta</c> in
    /// ChildWindow.xaml.cs) — previously only <c>TitleBarHeight</c> was subtracted, which left a
    /// systematic 4px off-by-four between the quantised height and the actual content height.
    /// </summary>
    public const double FileListVPadding = 4d;

    /// <summary>
    /// Per-side "gutter" around the icon box in GridViewTemplate: the total chrome contributed by one
    /// side of one item, used twice per <see cref="CellSize"/>/<see cref="RowHeight"/> (once for each
    /// of the two items sharing a seam) so that the seam between adjacent columns is exactly
    /// <c>ItemMargin * 2</c> wide. As of ui-polishing-1 §3.3 (B-F1) this 6px is split across two layers
    /// instead of coming purely from margin: <c>5px</c> is the item StackPanel's own <c>Margin</c> in
    /// GridViewTemplate, and the remaining <c>1px</c> is <c>PanelItemContainerStyle</c>'s
    /// <c>SelectionBorder</c> <c>BorderThickness</c> (now measured normally, no compensating negative
    /// Margin — see the doc comment on <c>PanelItemContainerStyle</c> in Templates.xaml). The constant's
    /// value and the <see cref="CellSize"/>/<see cref="RowHeight"/> formulas are unchanged by that
    /// split; only where the 6px chrome physically lives changed, so the selection border stroke no
    /// longer renders 1px outside the <c>ListBoxItem</c>'s arrange box (which used to get clipped for
    /// the first row/column).
    /// <para>
    /// As of the adaptive-icon-grid feature: this is chrome belonging to a single cell (part of its
    /// measured footprint), not the gap between two cells — the gap between cells is now a separate,
    /// independent concept owned by <c>AdaptiveIconGridPanel.MinGap</c>
    /// (<see cref="AdaptiveGridLayout.DefaultMinGap"/>). The two composed into the original 12px seam
    /// only while <c>MinGap == 0</c>; as of polishing/ui-1, <c>MinGap = 4</c>, so the visible seam
    /// between two icons in an incomplete row is <c>ItemMargin * 2 + DefaultMinGap</c> = ~16px on both
    /// axes (see <see cref="AdaptiveGridLayout"/>'s remarks on <c>DefaultMinGap</c>).
    /// </para>
    /// </summary>
    public const double ItemMargin = 6d;

    /// <summary>
    /// Total horizontal chrome of FileListHost outside the icon grid itself.
    /// As of ui-polishing-1 §3.5 this is <c>0</c> (<c>FileListHost.Margin="0,0,0,4"</c> — no extra
    /// left/right margin): the item's own per-side chrome (<see cref="ItemMargin"/> = 6px, see its
    /// doc comment) already reproduces exactly half of the 12px inter-column seam at the left/right
    /// edges of the list, which is the "uniform grid with half-gutter edges" rule that keeps the edge
    /// from looking twice as thick as the seam between two columns. Any extra host-level margin here
    /// would stack on top of that and reintroduce the asymmetry. Still added back to the quantised
    /// content width (see <c>Resize_DragDelta</c>/<c>SnapWidthToGrid</c> in ChildWindow.xaml.cs) so the
    /// formula stays correct if this ever needs to become non-zero again (e.g. a themed border around
    /// FileListHost).
    /// </summary>
    public const double FileListHPadding = 0d;

    /// <summary>
    /// Width of the item StackPanel (icon box) in GridViewTemplate.
    /// Formula: icon + 25 % padding on each side = <c>iconSize * 1.5</c>.
    /// Example: <c>BoxSize(48) == 72</c>.
    /// </summary>
    public static double BoxSize(double iconSize) => iconSize + (iconSize / 4d) * 2d;

    /// <summary>
    /// Full column footprint: <see cref="BoxSize"/> + two item margins.
    /// This is <c>x</c>, the cell width fed into <see cref="AdaptiveGridLayout"/>'s horizontal-axis
    /// calls (analytical twin of a <c>ListBoxItem</c>'s measured
    /// <see cref="System.Windows.UIElement.DesiredSize"/>.Width — see <see cref="AdaptiveGridLayout"/>'s
    /// remarks, "D-0"). No longer a standalone "quantisation step": on its own it doesn't account for
    /// <c>AdaptiveIconGridPanel.MinGap</c>/<c>EdgePaddingRatio</c>, which is why width snapping now goes
    /// through <see cref="AdaptiveGridLayout.SnapExtent"/> instead of a plain modulo against this value.
    /// Example: <c>CellSize(48) == 84</c> (72 box + 6 + 6 margins).
    /// </summary>
    public static double CellSize(double iconSize) => BoxSize(iconSize) + ItemMargin * 2d;

    /// <summary>
    /// Full row footprint: icon slot height + <see cref="LabelHeight"/> + two item margins. This is
    /// <c>x_v</c>, the cell height fed into <see cref="AdaptiveGridLayout"/>'s vertical-axis calls
    /// (analytical twin of a <c>ListBoxItem</c>'s measured
    /// <see cref="System.Windows.UIElement.DesiredSize"/>.Height). Deliberately NOT equal to
    /// <see cref="CellSize"/> — the cell is <c>84 x 92</c> at <c>iconSize = 48</c>, not square; using
    /// <see cref="CellSize"/> for the vertical axis would clip or overlap the name label.
    /// Example: <c>RowHeight(48) == 92</c> (48 icon + 32 label + 6 + 6 margins).
    /// </summary>
    public static double RowHeight(double iconSize) => iconSize + LabelHeight + ItemMargin * 2d;

    /// <summary>
    /// Hard floor for a panel's overall WINDOW width/height in pixels — the single source of truth
    /// for what used to be three independently-maintained copies of the literal <c>120</c>
    /// (finalization MAJOR M-1/m-2): <c>ChildWindow.xaml</c>'s hard <c>MinWidth="120"</c> WPF
    /// coercion on <c>Window.Width</c> (cannot be data-bound to this constant — see the comment
    /// beside it in the XAML, which must be kept in sync by hand), <c>ChildWindow.xaml.cs</c>'s own
    /// flat <c>MinPanelSize</c> constant (used directly as the height floor in BOTH view modes, as
    /// the flat width floor in List mode — which has no columns at all — AND as the clamp floor for
    /// <see cref="MinPanelWidth"/>'s icon-grid-derived Grid-mode width minimum below; genuinely
    /// axis-agnostic, hence the name), and <c>App.xaml.cs</c>'s minimum size for a panel drawn via
    /// the desktop lasso gesture. Keeping three independent copies in sync by hand is exactly what
    /// let the icon-grid-derived Grid-mode minimum silently drift below this hard floor at small
    /// <c>IconSize</c> values in the first place — see <see cref="MinPanelWidth"/>'s remarks.
    /// </summary>
    public const double MinPanelSize = 120d;

    /// <summary>
    /// Minimum panel WINDOW width for a Grid-mode panel at the given <paramref name="iconSize"/>:
    /// <c>max(MinPanelSize, one column's Extent + FileListHPadding)</c> (finalization MAJOR M-1).
    /// <para>
    /// The "one column" term is <see cref="AdaptiveGridLayout.Extent(int, double, double, double)"/>
    /// for a single cell (<c>n = 1</c>) — expressed that way, rather than duplicating the resulting
    /// <c>2 * cell</c> formula by hand, so <see cref="AdaptiveGridLayout"/> stays the single source of
    /// truth for grid geometry (adr/ADR-018-adaptive-icon-grid-layout.md, "D-0"). Because
    /// <see cref="AdaptiveGridLayout.Extent(int, double, double, double)"/>'s gap term is multiplied
    /// by <c>(n - 1) == 0</c> when <c>n == 1</c>, this is exactly "one column plus its half-cell edge
    /// padding on each side" — which happens to measure <c>2 * CellSize(iconSize)</c>, independent of
    /// <see cref="AdaptiveGridLayout.DefaultMinGap"/> — NOT "two columns' worth of cells":
    /// <c>Extent(2, ...)</c> would be roughly <c>3 * cell + gap</c>, nearly double this value. Do not
    /// "simplify" this to <c>Extent(2, ...)</c> — that would double every Grid panel's minimum width.
    /// </para>
    /// <para>
    /// <c>+ FileListHPadding</c> promotes the content-only Extent to a WINDOW-width value, matching
    /// the contract every caller of this method compares it against (e.g.
    /// <c>ChildWindow.Resize_DragDelta</c>/<c>SnapWidthToGrid</c> compare it directly against
    /// <c>SnapExtent(...) + FileListHPadding</c>, itself a window-width value) — currently a no-op
    /// numerically since <see cref="FileListHPadding"/> is <c>0</c>, but keeps the contract correct if
    /// that ever changes (finalization m-3).
    /// </para>
    /// <para>
    /// Clamped up to <see cref="MinPanelSize"/> because the unclamped icon-grid minimum (72/84/96/108px
    /// for <c>IconSize ∈ {16,20,24,28}</c>, the low end of the slider) is smaller than that hard
    /// <c>Window.MinWidth</c> floor. Without this clamp, <c>ChildWindow.Resize_DragDelta</c> could
    /// compute a <see cref="System.Windows.Window.Width"/> below what WPF will actually coerce the
    /// window to, desynchronizing the code's own <see cref="System.Windows.Window.Left"/> math (set
    /// from the uncoerced value) from the window's real, coerced <c>Width</c> — visibly jumping the
    /// panel's right edge when dragging the LEFT resize handle inward (finalization MAJOR M-1).
    /// </para>
    /// </summary>
    public static double MinPanelWidth(double iconSize)
    {
        var cell = CellSize(iconSize);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var oneColumnExtent = AdaptiveGridLayout.Extent(1, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);

        return Math.Max(MinPanelSize, oneColumnExtent + FileListHPadding);
    }
}
