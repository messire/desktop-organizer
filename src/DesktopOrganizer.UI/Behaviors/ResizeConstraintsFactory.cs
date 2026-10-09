using System.Diagnostics.CodeAnalysis;
using System.Windows;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Builds the fixed-for-the-gesture snap/chrome/floor configuration passed to
/// <see cref="ResizeGestureSession.TryBegin"/> — mirrors the pre-C4 <c>OnResizeDragStarted</c>/
/// <c>OnResizeDragDelta</c> branch decisions (docs/PLANS/phase-1-resize-stabilization.md §2.4).
/// </summary>
/// <remarks>
/// Extracted out of the former Thumb-driven resize controller as part of Phase 2 commit C4
/// (docs/PLANS/phase-2-native-panel-sizing.md §1.5); the native <c>WM_SIZING</c> path
/// (<see cref="NativePanelResizeController"/>) builds <see cref="ResizeConstraints"/> from this ONE
/// implementation (ADR-022: the Thumb path is removed) — a second copy of this logic would silently diverge the next
/// time either snap metric changes, breaking the regression-golden suite (plan §3, "жёсткое требование").
/// </remarks>
internal static class ResizeConstraintsFactory
{
    /// <summary>
    /// Builds the snap/chrome/floor configuration for one resize gesture.
    /// </summary>
    /// <param name="model">
    /// The panel's current model (may be <see langword="null"/> before <c>Initialize</c> — treated the
    /// same as List mode).
    /// </param>
    /// <param name="snapEnabled"><c>IAppSettings.SnapResizeToIconGrid</c> at the moment the gesture starts.</param>
    /// <param name="titleBarHeight"><c>ChildWindow.TitleBarHeight</c> — chrome stripped before quantising height.</param>
    /// <param name="gridState">
    /// The panel's workspace-grid state for this gesture (<see cref="PanelGridStateResolver.Resolve"/>,
    /// docs/PLANS/workspace-grid-03-04-integration.md §3.3/P04-C1). Defaults to
    /// <see cref="PanelGridState.Legacy"/>, so every pre-P04 call site keeps producing exactly the
    /// values it produced before. This is the ONE point where <see cref="PanelGeometryMode.GridAligned"/>
    /// enters the resize pipeline's configuration (ADR-028 Amendment MAJOR-4 п.1).
    /// </param>
    /// <param name="gridSize">
    /// ADR-011 Amendment 2026-09-24 (resize extension): <c>IAppSettings.GridSize</c> at the moment the
    /// gesture starts — resolves <see cref="ResizeConstraints.SizeGridStep"/>. Defaults to <c>1</c>
    /// ("no grid", <see cref="GridSnap.SnapAxis"/>'s own convention), so every pre-existing call site
    /// keeps producing <see cref="ResizeConstraints.SizeGridStep"/> <c>== 0</c>, i.e. bit-for-bit its
    /// pre-amendment output.
    /// </param>
    public static ResizeConstraints BuildConstraints(
        ChildWindowModel? model,
        bool snapEnabled,
        double titleBarHeight,
        PanelGridState gridState = default,
        int gridSize = 1)
    {
        // Defensive: a GridAligned state with a degenerate unit size could only come from a
        // hand-constructed PanelGridState (PanelGridStateResolver always fills it from
        // WorkspaceGridMetrics' constants) — fall back to the legacy shape rather than computing a
        // zero/NaN floor from it.
        var isGridAligned = gridState.IsGridAligned && double.IsFinite(gridState.UnitSize) && gridState.UnitSize > 0d;

        // ADR-011 Amendment 2026-09-24 (resize extension): IAppSettings.GridSize, when active, takes
        // priority over the icon-grid snap below — see ResolveSizeGridStep's remarks for the GridAligned
        // commensurability gate.
        var sizeGridStep = ResolveSizeGridStep(gridSize, isGridAligned, gridState.UnitSize);

        double snapCellWidth = 0d, snapCellHeight = 0d;

        // P04-C2: GridAligned takes the FREE branch of ResizeGeometryCalculator.Apply on BOTH axes —
        // AdaptiveGridLayout.SnapExtent (System C, the icon grid) must never quantise a System-B panel
        // extent (ADR-028 Amendment MAJOR-4 п.1: "система B != система C"). Quantisation to the
        // workspace grid happens instead as the second step of the same frame, inside
        // ResizeGestureSession.TryUpdate (§3.4/P04-G2).
        //
        // sizeGridStep > 0 overrides the icon-grid snap outright (both semantically — GridSize has
        // priority over SnapResizeToIconGrid, per the ADR-011 amendment — and so the diagnostics log
        // (IGestureDiagnostics.ResizeStarted) never reports icon-grid snap cells for a gesture that is
        // actually being quantised to GridSize).
        if (IsIconGridSnapActive(model, snapEnabled, isGridAligned, sizeGridStep))
        {
            // Grid mode: width snaps to whole columns (GridMetrics.CellSize == x) and height snaps to
            // whole rows (GridMetrics.RowHeight == x_v) — deliberately NOT the same cell size on both
            // axes (docs/PLANS/adaptive-icon-grid.md §5, "D-3"): the cell is 84x92 at IconSize=48, not
            // square, so reusing CellSize for height would quantise height a few pixels short of an
            // actual row.
            snapCellWidth = GridMetrics.CellSize(model.IconSize);
            snapCellHeight = GridMetrics.RowHeight(model.IconSize);
        }

        // List mode, or snapping disabled: BOTH axes stay free-form (pixel-accurate, snapCellWidth/
        // Height left at 0) — List mode is always free on both axes, GridMetrics.CellSize/RowHeight
        // describe icon-grid columns/rows, which have nothing to do with a list row's height or a list
        // panel's width.
        //
        // M-2 fix-cycle amendment (adr/ADR-029-icon-layout-policy.md Решение 4): IconLayoutPolicy is
        // Grid-mode-only by construction (ADR-029 Решение 5) — a List-mode/null model is resolved to
        // LegacyStretch here so GridMetrics.ContentHorizontalChrome never reserves M-2's scrollbar width
        // for a mode where AdaptiveIconGridPanel/AdaptiveGridLayout aren't even in play.
        var effectivePolicy = model?.ViewMode == PanelViewMode.Grid ? model.IconLayoutPolicy : IconLayoutPolicy.LegacyStretch;
        var horizontalChrome = GridMetrics.ContentHorizontalChrome(effectivePolicy, SystemParameters.VerticalScrollBarWidth);

        // P04-C3: a GridAligned panel's floor is its MINIMUM SPAN expressed in DIP — never
        // GridMetrics.MinPanelWidth(IconSize)/MinPanelSize (System C). Deriving the floor from IconSize
        // is the "IconSize changes panel bounds" bug (baseline F-2) at the level of the minimum itself
        // (ADR-028 Amendment MAJOR-4 п.3); WorkspaceGridMetrics.MinSpanExpanded/MinSpanCollapsed are
        // chosen for us by PanelGridStateResolver, which already knows whether the panel is rolled up.
        var gridMinSize = isGridAligned
            ? WorkspaceLayoutPolicy.SpansToBounds(gridState.MinSpan, gridState.UnitSize, gridState.Gap)
            : default;

        var minWidth = isGridAligned
            ? gridMinSize.Width
            : model?.ViewMode == PanelViewMode.Grid
                ? GridMetrics.MinPanelWidth(model.IconSize, effectivePolicy, SystemParameters.VerticalScrollBarWidth)
                : GridMetrics.MinPanelSize;

        return new ResizeConstraints(
            MinWidth: minWidth,
            MinHeight: isGridAligned ? gridMinSize.Height : GridMetrics.MinPanelSize,
            SnapCellWidth: snapCellWidth,
            SnapCellHeight: snapCellHeight,
            TitleBarHeight: titleBarHeight,
            HorizontalChrome: horizontalChrome,
            VerticalChrome: GridMetrics.FileListVPadding,
            SizeGridStep: sizeGridStep);
    }

    /// <summary>
    /// The ONE priority rule between the three width/height quantisers (ADR-011 amendment 2026-09-24 vs
    /// ADR-015/018): the icon-grid snap (<c>AdaptiveGridLayout</c> cell, System C) applies only in Grid mode
    /// with <c>IAppSettings.SnapResizeToIconGrid</c> on, never for a
    /// <see cref="PanelGeometryMode.GridAligned"/> panel (workspace grid, System B, owns its size) and never
    /// while <c>GridSize</c> is active (<paramref name="sizeGridStep"/> &gt; 0 — GridSize has priority and
    /// quantises instead). Shared by <see cref="BuildConstraints"/> (the resize gesture) and both
    /// controllers' <c>SnapWidthToGrid</c> (the post-<c>IconSize</c>/<c>ViewMode</c>/<c>IconLayoutPolicy</c>
    /// re-snap) so the two can never disagree.
    /// </summary>
    /// <param name="model">The panel's model (<see langword="null"/> before <c>Initialize</c> — never snaps).</param>
    /// <param name="snapEnabled"><c>IAppSettings.SnapResizeToIconGrid</c>.</param>
    /// <param name="isGridAligned">Whether the panel is on the workspace grid for this operation.</param>
    /// <param name="sizeGridStep">The step resolved by <see cref="ResolveSizeGridStep"/> (<c>0</c> = GridSize inactive).</param>
    /// <returns><see langword="true"/> iff the icon-grid snap is the active quantiser.</returns>
    internal static bool IsIconGridSnapActive([NotNullWhen(true)] ChildWindowModel? model, bool snapEnabled, bool isGridAligned, double sizeGridStep)
        => sizeGridStep <= 0d && !isGridAligned && snapEnabled && model is not null && model.ViewMode == PanelViewMode.Grid;

    /// <summary>
    /// ADR-011 Amendment 2026-09-24 (resize extension): resolves <see cref="ResizeConstraints.SizeGridStep"/>
    /// from the raw <paramref name="gridSize"/> setting.
    /// </summary>
    /// <param name="gridSize">
    /// <c>IAppSettings.GridSize</c>. Values <c>&lt;= 1</c> disable the feature entirely (returns <c>0</c>),
    /// matching <see cref="GridSnap.SnapAxis"/>'s "GridSize=1 means no grid" convention.
    /// </param>
    /// <param name="isGridAligned">
    /// Whether this gesture's <see cref="PanelGridState"/> is grid-aligned (already validated by the
    /// caller — see <see cref="BuildConstraints"/>'s own <c>isGridAligned</c> computation).
    /// </param>
    /// <param name="unitSize">
    /// <see cref="PanelGridState.UnitSize"/> for this gesture — only consulted when
    /// <paramref name="isGridAligned"/> is <see langword="true"/>.
    /// </param>
    /// <returns>
    /// <c>0</c> when <paramref name="gridSize"/> is <c>&lt;= 1</c>; <paramref name="gridSize"/> unchanged
    /// for a non-grid-aligned gesture (LegacyFreeform is not on any grid to begin with, so there is
    /// nothing for GridSize to be incommensurate WITH); for a grid-aligned gesture, <paramref name="gridSize"/>
    /// only when it is a whole multiple of <paramref name="unitSize"/> (ADR-028 "две сетки намеренно
    /// несоизмеримы" — the same commensurability gate <c>WindowManager.NormalizeGridAlignedToGridSize</c>
    /// already applies to restore-time normalization; forcing an incommensurate GridSize onto a
    /// GridAligned panel would pull it OFF the workspace grid it is locked to), otherwise <c>0</c> (the
    /// panel is left on its ordinary workspace-grid quantisation, undisturbed by GridSize).
    /// </returns>
    internal static double ResolveSizeGridStep(int gridSize, bool isGridAligned, double unitSize)
    {
        if (gridSize <= 1) return 0d;
        if (!isGridAligned) return gridSize;

        var unit = (int)unitSize;
        return unit > 0 && gridSize % unit == 0 ? gridSize : 0d;
    }
}
