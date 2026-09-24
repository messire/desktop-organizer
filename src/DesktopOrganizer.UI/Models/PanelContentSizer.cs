using System.Windows;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// Pure, WPF-independent implementation of ADR-029 Решение 7 (<c>docs/fix-prompts/06-fit-content.md</c>,
/// P06): "how tall should a Grid-mode panel be to exactly fit its current icons". No
/// <see cref="System.Windows.DependencyObject"/>/<see cref="System.Windows.Media.Visual"/> dependencies
/// whatsoever — like <see cref="GridMetrics"/>/<see cref="AdaptiveGridLayout"/>/<see cref="WorkspaceLayoutPolicy"/>,
/// fully unit-testable without STA/WPF test infrastructure, and deliberately duplicates none of their
/// formulas: every intermediate value below is computed by calling straight into whichever of those
/// three types already owns it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single direction, no feedback loop (ADR-029 D-0/Решение 7 "Однонаправленность").</b>
/// <see cref="ResolveColumnCapacity"/> is computed from a WIDTH the caller already has (the panel's
/// current, unchanged <c>Window.Width</c>) — never the other way around, and this type never reads or
/// returns a width of its own. <see cref="Controls.AdaptiveIconGridPanel"/> itself is not involved in
/// this computation at all; like the rest of the snap-code family, this is an analytical DUPLICATE of
/// what that control would render for the same inputs, computed before any real layout pass runs (the
/// same reason <c>PanelResizeController.SnapWidthToGrid</c> exists as pure math rather than a
/// measure/arrange round-trip).
/// </para>
/// <para>
/// <b>Column capacity uses the SAME chrome accounting as the live panel (ADR-029 "Реализация P06").</b>
/// The original Решение 7 formula (written before the P05 fix-cycle's <c>CapacityWidth</c> mechanism
/// existed) used the bare <see cref="PanelMetrics.ContentLeftInset"/> to convert <c>Window.Width</c> to
/// content width. This type instead uses <see cref="GridMetrics.ContentHorizontalChrome"/> — the
/// policy-aware chrome that ALSO reserves <see cref="GridMetrics.ReservedVerticalScrollBarWidth"/> for
/// <see cref="IconLayoutPolicy.FixedGap"/> — so a <see cref="IconLayoutPolicy.FixedGap"/> panel being
/// auto-sized computes the same column count (and therefore row count/height) that
/// <see cref="Controls.AdaptiveIconGridPanel"/> will actually render, rather than re-introducing the
/// exact "lost column" class of drift ADR-029's F-3 fix already closed once for the resize-snap path.
/// </para>
/// <para>
/// <b>Vertical axis never stretches, under either <see cref="IconLayoutPolicy"/> (ADR-029 Решение 1/
/// Решение 3, <see cref="AdaptiveGridLayout.Offset"/>'s own remarks).</b> The row-to-row gap fed into
/// <see cref="AdaptiveGridLayout.Extent"/> is always <see cref="AdaptiveGridLayout.DefaultMinGap"/> —
/// never <see cref="AdaptiveGridLayout.RowGap(int, int, double, double, double, double, IconLayoutPolicy)"/> —
/// exactly mirroring <see cref="Controls.AdaptiveIconGridPanel.ArrangeOverride"/>'s own vertical
/// <see cref="AdaptiveGridLayout.Offset"/> call, which never goes through <c>RowGap</c> at all.
/// </para>
/// </remarks>
public static class PanelContentSizer
{
    /// <summary>
    /// Computes <c>k_max</c> — the number of icon columns that fit across <paramref name="windowWidth"/>
    /// under <paramref name="layoutPolicy"/> — the same way <see cref="Controls.AdaptiveIconGridPanel"/>
    /// itself does, but analytically, from a WINDOW-width value instead of a live measured content width.
    /// </summary>
    /// <param name="windowWidth">The panel's current <see cref="Window.Width"/>, in DIP. Never mutated
    /// or re-derived by this type — see the class remarks on "Однонаправленность".</param>
    /// <param name="iconSize">The panel's current <c>IconSize</c>, in DIP.</param>
    /// <param name="layoutPolicy">
    /// The panel's current <see cref="IconLayoutPolicy"/> — callers must resolve a List-mode panel to
    /// <see cref="IconLayoutPolicy.LegacyStretch"/> themselves before calling (ADR-029 Решение 5: the
    /// policy — and, transitively, this whole formula — is meaningless outside Grid mode; Q3 = B limits
    /// P06 to Grid mode, see the ADR's "Открытые вопросы").
    /// </param>
    /// <param name="verticalScrollBarWidth">
    /// The real <see cref="SystemParameters.VerticalScrollBarWidth"/>, read by the caller — this type
    /// stays WPF-runtime-free the same way <see cref="GridMetrics.ReservedVerticalScrollBarWidth"/> does.
    /// </param>
    /// <returns>The column capacity (always &gt;= 1 — see <see cref="AdaptiveGridLayout.RowCapacity"/>).</returns>
    public static int ResolveColumnCapacity(double windowWidth, double iconSize, IconLayoutPolicy layoutPolicy, double verticalScrollBarWidth)
    {
        var cell = GridMetrics.CellSize(iconSize);
        var chrome = GridMetrics.ContentHorizontalChrome(layoutPolicy, verticalScrollBarWidth);
        var contentWidth = Math.Max(0d, windowWidth - chrome);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

        return AdaptiveGridLayout.RowCapacity(contentWidth, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding);
    }

    /// <summary>
    /// Computes <c>displayRows</c> (ADR-029 Решение 7): the number of rows to actually reserve height
    /// for, which is <see cref="AdaptiveGridLayout.RowCount"/> EXCEPT for an empty panel, which still
    /// reserves exactly one row — a visible drag-and-drop "drop zone" rather than collapsing to a
    /// height indistinguishable from <c>IsRolledUp</c>.
    /// </summary>
    /// <param name="itemCount">The panel's current, VISIBLE item count (i.e. after any content filter —
    /// callers should pass the same count <see cref="Controls.AdaptiveIconGridPanel"/> itself lays out,
    /// not an unfiltered total).</param>
    /// <param name="columnCapacity"><see cref="ResolveColumnCapacity"/>'s result.</param>
    /// <returns>
    /// <c>1</c> when <paramref name="itemCount"/> &lt;= <c>0</c> (the drop-zone floor — deliberately NOT
    /// a reuse of <see cref="AdaptiveGridLayout.RowCount"/>'s own <c>itemCount == 0 -&gt; 0</c> contract,
    /// which remains unchanged everywhere else it's used for scrolling/measure); otherwise
    /// <see cref="AdaptiveGridLayout.RowCount(int, int)"/> unchanged.
    /// </returns>
    public static int ResolveDisplayRowCount(int itemCount, int columnCapacity)
        => itemCount <= 0 ? 1 : AdaptiveGridLayout.RowCount(itemCount, columnCapacity);

    /// <summary>
    /// Computes the raw (unrounded-to-workspace-grid) fit height, in DIP: <c>HeaderHeight +
    /// FileListVPadding + Extent(displayRows, RowHeight(iconSize), DefaultMinGap, verticalEdgePadding)</c>
    /// (ADR-029 Решение 7's formula, verbatim). Because <see cref="GridMetrics.RowHeight"/> already
    /// embeds the full label height, a single row (<paramref name="itemCount"/> in
    /// <c>[1, columnCapacity]</c>) never clips its name label — there is no separate, smaller "just the
    /// icon" term this formula could accidentally use instead.
    /// </summary>
    /// <param name="itemCount">See <see cref="ResolveDisplayRowCount"/>.</param>
    /// <param name="iconSize">See <see cref="ResolveColumnCapacity"/>.</param>
    /// <param name="windowWidth">See <see cref="ResolveColumnCapacity"/>.</param>
    /// <param name="layoutPolicy">See <see cref="ResolveColumnCapacity"/>.</param>
    /// <param name="verticalScrollBarWidth">See <see cref="ResolveColumnCapacity"/>.</param>
    public static double ComputeRawContentHeight(
        int itemCount,
        double iconSize,
        double windowWidth,
        IconLayoutPolicy layoutPolicy,
        double verticalScrollBarWidth)
    {
        var columnCapacity = ResolveColumnCapacity(windowWidth, iconSize, layoutPolicy, verticalScrollBarWidth);
        var displayRows = ResolveDisplayRowCount(itemCount, columnCapacity);

        var rowHeight = GridMetrics.RowHeight(iconSize);
        var verticalEdgePadding = AdaptiveGridLayout.EdgePadding(rowHeight, AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio);

        // Vertical gaps are ALWAYS DefaultMinGap, under either IconLayoutPolicy — see class remarks.
        var contentHeight = AdaptiveGridLayout.Extent(displayRows, rowHeight, AdaptiveGridLayout.DefaultMinGap, verticalEdgePadding);

        return PanelMetrics.HeaderHeight + GridMetrics.FileListVPadding + contentHeight;
    }

    /// <summary>
    /// Rounds a raw fit height up to a whole DIP (ADR-028's M-1 convention, the same one the resize-snap
    /// code already applies) for a <see cref="PanelGeometryMode.LegacyFreeform"/> panel — no workspace
    /// row-span concept applies there.
    /// </summary>
    public static double RoundForLegacyFreeform(double rawHeight) => Math.Ceiling(Math.Max(0d, rawHeight));

    /// <summary>
    /// Rounds a raw fit height UP to the smallest whole workspace-grid row span (ADR-028) that covers
    /// it, for a <see cref="PanelGeometryMode.GridAligned"/> panel — reusing
    /// <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/> exactly as ADR-029 Решение 7 specifies ("округление
    /// вверх до workspace row span"), never a hand-rolled <c>Math.Ceiling(height / unitSize)</c>.
    /// The <see cref="Size.Width"/> component passed to <see cref="WorkspaceLayoutPolicy.BoundsToSpans"/>
    /// is an unused placeholder (<c>0</c>): <see cref="WorkspaceLayoutPolicy.SpansToBounds"/>'s height
    /// output depends only on <see cref="GridSpan.HeightUnits"/>, never on <see cref="GridSpan.WidthUnits"/>
    /// (the two axes are fully separable — see that method's own formula), so this never needs — and
    /// must never be tempted to duplicate — the panel's actual, unrelated width span.
    /// </summary>
    /// <param name="rawHeight"><see cref="ComputeRawContentHeight"/>'s result.</param>
    /// <param name="unitSize"><see cref="WorkspaceGridMetrics.UnitSize"/> — parametrized (not read
    /// internally) for the same "stay a pure function of its inputs" reason every other type in this
    /// family follows.</param>
    /// <param name="gap"><see cref="WorkspaceGridMetrics.DefaultGap"/>.</param>
    /// <returns>
    /// The height span, in whole <paramref name="unitSize"/> units, floored at
    /// <see cref="WorkspaceGridMetrics.MinHeightSpanExpanded"/> — the same component-wise minimum-span
    /// floor <see cref="Services.PanelDefaults.ReconcileGridAlignedSpans"/> already applies when seeding
    /// a brand new <see cref="PanelGeometryMode.GridAligned"/> panel, so <c>Fit panel to content"</c>/
    /// <see cref="PanelSizingMode.AutoContent"/> can never quantise a panel below the documented minimum
    /// expanded span even for a degenerate (0-item, tiny-<c>IconSize</c>) input.
    /// </returns>
    public static int RoundHeightUnitsForGridAligned(double rawHeight, double unitSize = WorkspaceGridMetrics.UnitSize, double gap = WorkspaceGridMetrics.DefaultGap)
    {
        var span = WorkspaceLayoutPolicy.BoundsToSpans(new Size(0d, Math.Max(0d, rawHeight)), unitSize, gap);
        return Math.Max(span.HeightUnits, WorkspaceGridMetrics.MinHeightSpanExpanded);
    }

    /// <summary>
    /// Full pipeline (ADR-029 Решение 7): computes the raw fit height, then rounds it per
    /// <paramref name="geometryMode"/> — <see cref="RoundForLegacyFreeform"/> for
    /// <see cref="PanelGeometryMode.LegacyFreeform"/>, or <see cref="RoundHeightUnitsForGridAligned"/>
    /// converted back to DIP via <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> for
    /// <see cref="PanelGeometryMode.GridAligned"/>. The single entry point both the explicit "Fit panel
    /// to content" action and the <see cref="PanelSizingMode.AutoContent"/> passive recompute call
    /// (<c>Views/ChildWindow.ContentSizing.cs</c>) — see this type's own class remarks.
    /// </summary>
    /// <param name="itemCount">See <see cref="ResolveDisplayRowCount"/>.</param>
    /// <param name="iconSize">See <see cref="ResolveColumnCapacity"/>.</param>
    /// <param name="windowWidth">See <see cref="ResolveColumnCapacity"/>.</param>
    /// <param name="layoutPolicy">See <see cref="ResolveColumnCapacity"/>.</param>
    /// <param name="verticalScrollBarWidth">See <see cref="ResolveColumnCapacity"/>.</param>
    /// <param name="geometryMode">Selects the rounding rule (see above).</param>
    /// <param name="unitSize">See <see cref="RoundHeightUnitsForGridAligned"/>.</param>
    /// <param name="gap">See <see cref="RoundHeightUnitsForGridAligned"/>.</param>
    /// <returns>The final, ready-to-apply <c>Window.Height</c> value, in DIP.</returns>
    public static double FitHeight(
        int itemCount,
        double iconSize,
        double windowWidth,
        IconLayoutPolicy layoutPolicy,
        double verticalScrollBarWidth,
        PanelGeometryMode geometryMode,
        double unitSize = WorkspaceGridMetrics.UnitSize,
        double gap = WorkspaceGridMetrics.DefaultGap)
    {
        var raw = ComputeRawContentHeight(itemCount, iconSize, windowWidth, layoutPolicy, verticalScrollBarWidth);

        if (geometryMode != PanelGeometryMode.GridAligned)
            return RoundForLegacyFreeform(raw);

        var heightUnits = RoundHeightUnitsForGridAligned(raw, unitSize, gap);
        return WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, heightUnits), unitSize, gap).Height;
    }
}
