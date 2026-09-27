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
    /// Total horizontal chrome, as a WINDOW-width value, that sits between
    /// <see cref="System.Windows.FrameworkElement.Width"/> and the icon grid's own content width — i.e. what every
    /// caller of this constant (<c>MinPanelWidth</c>,
    /// <c>PanelResizeController</c>/<c>NativePanelResizeController</c>'s <c>SnapWidthToGrid</c>,
    /// <c>ResizeConstraintsFactory.BuildConstraints</c>'s <c>HorizontalChrome</c>) adds back after
    /// quantising CONTENT width, or subtracts before quantising it, to convert between the two.
    /// <para>
    /// <b>FileListHost's OWN chrome is still <c>0</c></b> (ui-polishing-1 §3.5,
    /// <c>FileListHost.Margin="0,0,0,4"</c> — no extra left/right margin): the item's own per-side
    /// chrome (<see cref="ItemMargin"/> = 6px, see its doc comment) already reproduces exactly half of
    /// the 12px inter-column seam at the left/right edges of the list, the "uniform grid with
    /// half-gutter edges" rule from ADR-015. That part of this constant's original justification is
    /// unchanged.
    /// </para>
    /// <para>
    /// As of ADR-029 (F-3 fix, docs/PLANS/workspace-grid-00-baseline.md finding F-3, "Решение 2") this
    /// constant is NO LONGER the independent <c>0d</c> literal ADR-015 gave it — it is
    /// <see cref="PanelMetrics.ContentLeftInset"/> (8 DIP), accounting for a DIFFERENT, OUTER layer of
    /// chrome ADR-015 never knew about: <c>PanelChrome</c>'s fixed 8-DIP accent-indicator column
    /// (<c>Resources/Styles/Panels.xaml</c>, <c>PART_AccentIndicator</c>'s <c>ColumnDefinition</c>,
    /// ADR-025 MAJOR-1), which sits between <see cref="System.Windows.FrameworkElement.Width"/> and the
    /// <c>*</c>-column <c>PART_ContentHost</c> that actually hosts <c>FileListHost</c> /
    /// <c>AdaptiveIconGridPanel</c>. Before this fix, every ANALYTICAL snap-time computation of "how
    /// many columns fit" assumed the FULL window width was available to the icon grid, while
    /// <c>AdaptiveIconGridPanel</c> itself — driven by real WPF layout, not this constant — only ever
    /// saw <c>Window.Width - 8</c>; the two systematically disagreed by exactly one column at every
    /// snap point. Applies identically to BOTH <see cref="AdaptiveGridLayout"/>-facing
    /// <see cref="IconLayoutPolicy"/> values (ADR-029 Open Question Q2 = A: this is a chrome-accounting
    /// fix, orthogonal to the stretch-vs-fixed-gap contract, so it is not gated by
    /// <see cref="IconLayoutPolicy"/> at all).
    /// </para>
    /// </summary>
    public const double FileListHPadding = PanelMetrics.ContentLeftInset;

    /// <summary>
    /// M-2 (adr/ADR-018-adaptive-icon-grid-layout.md's MAJOR-finding, variant (a) — accepted by the P05
    /// fix-cycle for <see cref="IconLayoutPolicy.FixedGap"/> only, see adr/ADR-029-icon-layout-policy.md
    /// Решение 4 amendment): the extra width, ON TOP of <see cref="FileListHPadding"/>, reserved for a
    /// vertical scrollbar that MIGHT appear — <paramref name="verticalScrollBarWidth"/> for
    /// <see cref="IconLayoutPolicy.FixedGap"/>, <c>0</c> for <see cref="IconLayoutPolicy.LegacyStretch"/>
    /// (unchanged behavior).
    /// <para>
    /// <b>Why this exists.</b> <c>ScrollViewer.VerticalScrollBarVisibility="Auto"</c>
    /// (<c>GridViewTemplate</c>) means the vertical scrollbar's own appearance/disappearance changes the
    /// viewport width <see cref="Controls.AdaptiveIconGridPanel"/> measures against, which can flip
    /// <see cref="AdaptiveGridLayout.RowCapacity"/> by exactly one column right at the threshold where a
    /// panel's content stops/starts fitting vertically. Under <see cref="IconLayoutPolicy.LegacyStretch"/>
    /// this is absorbed by the stretch term (the same columns just redistribute their gap slightly) —
    /// ADR-018 accepted it as-is (variant (c)). Under <see cref="IconLayoutPolicy.FixedGap"/> there is no
    /// stretch to absorb it: the column flip instead surfaces as a sudden jump in the right-edge surplus
    /// space (ADR-029's Решение 4 UX-compromise section), which reads as more jarring — so <c>FixedGap</c>
    /// reserves the scrollbar's width UNCONDITIONALLY (independent of whether a scrollbar is actually
    /// visible right now), trading a small amount of always-unused width for a column count that never
    /// changes as items are added/removed.
    /// </para>
    /// <para>
    /// <paramref name="verticalScrollBarWidth"/> is a parameter, not a direct
    /// <see cref="System.Windows.SystemParameters.VerticalScrollBarWidth"/> read inside this method, so
    /// this class (and <see cref="AdaptiveGridLayout"/>) stay free of any WPF runtime dependency and
    /// fully unit-testable with an arbitrary injected value — callers (<see cref="Controls.AdaptiveIconGridPanel"/>,
    /// <c>PanelResizeController</c>/<c>NativePanelResizeController</c>'s <c>SnapWidthToGrid</c>,
    /// <c>ResizeConstraintsFactory.BuildConstraints</c>) are the ones that read the real system metric.
    /// </para>
    /// </summary>
    /// <param name="policy">Which of the two ADR-029 gap-distribution policies is in effect.</param>
    /// <param name="verticalScrollBarWidth">The real system vertical scrollbar width, read by the caller.</param>
    public static double ReservedVerticalScrollBarWidth(IconLayoutPolicy policy, double verticalScrollBarWidth)
        => policy == IconLayoutPolicy.FixedGap ? verticalScrollBarWidth : 0d;

    /// <summary>
    /// Total horizontal chrome, as a WINDOW-width value, between <see cref="System.Windows.FrameworkElement.Width"/>
    /// and the CONTENT width actually available to lay out icon-grid columns in — i.e. the
    /// policy-aware generalisation of <see cref="FileListHPadding"/> that also accounts for M-2's
    /// <see cref="ReservedVerticalScrollBarWidth"/> reservation
    /// (<c>= FileListHPadding + ReservedVerticalScrollBarWidth(policy, verticalScrollBarWidth)</c>).
    /// This is the single function every window-width&lt;-&gt;content-width conversion in the snap-code
    /// path (<c>PanelResizeController</c>/<c>NativePanelResizeController</c>'s <c>SnapWidthToGrid</c>,
    /// <see cref="MinPanelWidth(double, IconLayoutPolicy, double)"/>,
    /// <c>ResizeConstraintsFactory.BuildConstraints</c>'s <c>HorizontalChrome</c>) must use once
    /// <see cref="IconLayoutPolicy"/> is in scope — using bare <see cref="FileListHPadding"/> in any of
    /// those call sites would silently re-introduce the exact class of drift F-3 already fixed once (a
    /// snap-time formula assuming a narrower/wider content area than what
    /// <see cref="Controls.AdaptiveIconGridPanel"/> actually reserves), this time specific to
    /// <see cref="IconLayoutPolicy.FixedGap"/> panels.
    /// </summary>
    /// <param name="policy">Which of the two ADR-029 gap-distribution policies is in effect.</param>
    /// <param name="verticalScrollBarWidth">The real system vertical scrollbar width, read by the caller.</param>
    public static double ContentHorizontalChrome(IconLayoutPolicy policy, double verticalScrollBarWidth)
        => FileListHPadding + ReservedVerticalScrollBarWidth(policy, verticalScrollBarWidth);

    /// <summary>
    /// Width of the item StackPanel (icon box) in GridViewTemplate.
    /// Formula: icon + 25 % padding on each side = <c>iconSize * 1.5</c>.
    /// Example: <c>BoxSize(48) == 72</c>.
    /// </summary>
    /// <param name="iconSize">The panel's current icon size.</param>
    public static double BoxSize(double iconSize) => iconSize + ((iconSize / 4d) * 2d);

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
    /// <param name="iconSize">The panel's current icon size.</param>
    public static double CellSize(double iconSize) => BoxSize(iconSize) + (ItemMargin * 2d);

    /// <summary>
    /// Full row footprint: icon slot height + <see cref="LabelHeight"/> + two item margins. This is
    /// <c>x_v</c>, the cell height fed into <see cref="AdaptiveGridLayout"/>'s vertical-axis calls
    /// (analytical twin of a <c>ListBoxItem</c>'s measured
    /// <see cref="System.Windows.UIElement.DesiredSize"/>.Height). Deliberately NOT equal to
    /// <see cref="CellSize"/> — the cell is <c>84 x 92</c> at <c>iconSize = 48</c>, not square; using
    /// <see cref="CellSize"/> for the vertical axis would clip or overlap the name label.
    /// Example: <c>RowHeight(48) == 92</c> (48 icon + 32 label + 6 + 6 margins).
    /// </summary>
    /// <param name="iconSize">The panel's current icon size.</param>
    public static double RowHeight(double iconSize) => iconSize + LabelHeight + (ItemMargin * 2d);

    /// <summary>
    /// Hard floor for a panel's overall WINDOW width/height in pixels — the single source of truth
    /// for what used to be three independently-maintained copies of the literal <c>120</c>
    /// (finalization MAJOR M-1/m-2): <c>ChildWindow.xaml</c>'s hard <c>MinWidth="120"</c> WPF
    /// coercion on <c>Window.Width</c> (cannot be data-bound to this constant — see the comment
    /// beside it in the XAML, which must be kept in sync by hand), <c>ChildWindow.xaml.cs</c>'s own
    /// flat <c>MinPanelSize</c> constant (used directly as the height floor in BOTH view modes, as
    /// the flat width floor in List mode — which has no columns at all — AND as the clamp floor for
    /// <see cref="MinPanelWidth(double)"/>'s icon-grid-derived Grid-mode width minimum below; genuinely
    /// axis-agnostic, hence the name), and <c>App.xaml.cs</c>'s minimum size for a panel drawn via
    /// the desktop lasso gesture. Keeping three independent copies in sync by hand is exactly what
    /// let the icon-grid-derived Grid-mode minimum silently drift below this hard floor at small
    /// <c>IconSize</c> values in the first place — see <see cref="MinPanelWidth(double)"/>'s remarks.
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
    /// <c>PanelResizeController</c>/<c>NativePanelResizeController</c>'s <c>SnapWidthToGrid</c> compare
    /// it directly against <c>SnapExtent(...) + FileListHPadding</c>, itself a window-width value). As
    /// of ADR-029's F-3 fix this is NO LONGER a numeric no-op — <see cref="FileListHPadding"/> is now
    /// <see cref="PanelMetrics.ContentLeftInset"/> (8), not <c>0</c> — this term is exactly what keeps
    /// this method a genuine window-width value now that <see cref="FileListHPadding"/> stopped being
    /// zero (finalization m-3's own prediction, realized by ADR-029).
    /// </para>
    /// <para>
    /// Clamped up to <see cref="MinPanelSize"/> because the unclamped icon-grid minimum (72/84/96/108px
    /// for legacy <c>IconSize</c> values at the old tick-4 slider's low end) is smaller than that hard
    /// <c>Window.MinWidth</c> floor. As of docs/PLANS/color-presets-and-size-step.md (§2.1 D-S1), the
    /// slider's step is 8, so the low end it can actually reach is now only
    /// <c>IconSize ∈ {16, 24}</c> (<see cref="SizeStepPolicy.Step"/>) — the same clamp still applies to
    /// both. Without this clamp, <c>ChildWindow.Resize_DragDelta</c> could
    /// compute a <see cref="System.Windows.FrameworkElement.Width"/> below what WPF will actually coerce the
    /// window to, desynchronizing the code's own <see cref="System.Windows.Window.Left"/> math (set
    /// from the uncoerced value) from the window's real, coerced <c>Width</c> — visibly jumping the
    /// panel's right edge when dragging the LEFT resize handle inward (finalization MAJOR M-1).
    /// </para>
    /// </summary>
    /// <param name="iconSize">The panel's current icon size.</param>
    public static double MinPanelWidth(double iconSize)
        => MinPanelWidth(iconSize, IconLayoutPolicy.LegacyStretch, verticalScrollBarWidth: 0d);

    /// <summary>
    /// Policy-aware overload (M-2, adr/ADR-029-icon-layout-policy.md Решение 4 amendment): identical to
    /// <see cref="MinPanelWidth(double)"/> except it converts the one-column content extent to a
    /// window-width value via <see cref="ContentHorizontalChrome"/> instead of the bare
    /// <see cref="FileListHPadding"/>, so a <see cref="IconLayoutPolicy.FixedGap"/> panel's minimum
    /// window width also reserves M-2's scrollbar width — keeping this floor consistent with what
    /// <c>PanelResizeController</c>/<c>NativePanelResizeController</c>'s <c>SnapWidthToGrid</c> and
    /// <c>ResizeConstraintsFactory.BuildConstraints</c> now compute for the same policy.
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> passed here is numerically identical to the
    /// three-argument overload above (<see cref="ContentHorizontalChrome"/> reduces to
    /// <see cref="FileListHPadding"/> for that policy, regardless of <paramref name="verticalScrollBarWidth"/>).
    /// </summary>
    /// <param name="iconSize">The panel's current icon size.</param>
    /// <param name="policy">The panel's <see cref="IconLayoutPolicy"/> (Grid mode only — callers must
    /// resolve List-mode panels to <see cref="IconLayoutPolicy.LegacyStretch"/> themselves, mirroring
    /// ADR-029 Решение 5: the policy is meaningless outside Grid mode).</param>
    /// <param name="verticalScrollBarWidth">
    /// The real <see cref="System.Windows.SystemParameters.VerticalScrollBarWidth"/>, read by the
    /// caller — see <see cref="ReservedVerticalScrollBarWidth"/>'s remarks for why this class never
    /// reads it directly.
    /// </param>
    public static double MinPanelWidth(double iconSize, IconLayoutPolicy policy, double verticalScrollBarWidth)
    {
        var cell = CellSize(iconSize);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var oneColumnExtent = AdaptiveGridLayout.Extent(1, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);

        return Math.Max(MinPanelSize, oneColumnExtent + ContentHorizontalChrome(policy, verticalScrollBarWidth));
    }
}
