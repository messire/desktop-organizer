using System.Windows;
using System.Windows.Controls.Primitives;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns the panel's edge/corner resize gesture, extracted from <c>ChildWindow.xaml.cs</c>'s
/// <c>Resize_DragStarted/Delta/Completed</c> and <c>SnapWidthToGrid</c> (REFACTORING_PLAN.md §5.3). The
/// pixel math itself (<see cref="Models.GridMetrics"/>/<see cref="Models.AdaptiveGridLayout"/> calls) was
/// already side-effect-free before this extraction; this class wraps it with the small amount of
/// per-gesture mutable state (raw accumulators, the drag-gesture start snapshot) that the original
/// code-behind fields held.
/// </summary>
public sealed class PanelResizeController
{
    private readonly Window _window;
    private readonly IAppSettings _appSettings;
    private readonly IWindowManager _windowManager;
    private readonly Func<ChildWindowModel?> _getModel;
    private readonly Action _onResizeGestureStarting;
    private readonly Action _clearDragObstacles;
    private readonly Action _snapToGrid;
    private readonly double _titleBarHeight;

    // Raw (unquantised) accumulators for the current resize drag gesture.
    private double _rawWidth;
    private double _rawHeight;

    // Fixed opposite-edge anchors set at DragStarted; used to reposition the panel
    // when the Left or Top thumb is dragged so the right/bottom edge stays put.
    private double _rawRight;
    private double _rawBottom;

    // Cell size used to snap the current resize gesture, computed once in OnResizeDragStarted.
    // 0 means snapping is disabled for that axis (free-form/pixel-accurate resize); see
    // AdaptiveGridLayout.SnapExtent, used by OnResizeDragDelta/SnapWidthToGrid below
    // (docs/PLANS/adaptive-icon-grid.md §4, "D-2").
    private double _snapCellWidth;
    private double _snapCellHeight;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelResizeController"/> class.
    /// </summary>
    /// <param name="window">The owning panel window (its <see cref="Window.Width"/>/<see cref="Window.Height"/>/<see cref="Window.Left"/>/<see cref="Window.Top"/> are resized).</param>
    /// <param name="appSettings">Snap-to-grid settings driving whether/how the resize quantises.</param>
    /// <param name="windowManager">Used to persist layout state once a resize completes.</param>
    /// <param name="getModel">Accessor for the current <see cref="ChildWindowModel"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="onResizeGestureStarting">
    /// Invoked at the start of every resize gesture — commits any in-progress file-item rename and
    /// clears the panel's own file selection (mirrors the original <c>CommitActiveItemRenameIfAny</c> +
    /// <c>SelectedFile = null</c> pair also used by the title-bar drag gesture).
    /// </param>
    /// <param name="clearDragObstacles">Clears <see cref="PanelDragMoveBehavior"/>'s obstacle snapshot so a resize never inherits a stale one.</param>
    /// <param name="snapToGrid">Shared drop-time grid snap — see <see cref="PanelDragMoveBehavior.SnapToGrid"/>.</param>
    /// <param name="titleBarHeight">The panel's fixed title-bar height, used to strip chrome before quantising the resize gesture's height.</param>
    public PanelResizeController(
        Window window,
        IAppSettings appSettings,
        IWindowManager windowManager,
        Func<ChildWindowModel?> getModel,
        Action onResizeGestureStarting,
        Action clearDragObstacles,
        Action snapToGrid,
        double titleBarHeight)
    {
        _window = window;
        _appSettings = appSettings;
        _windowManager = windowManager;
        _getModel = getModel;
        _onResizeGestureStarting = onResizeGestureStarting;
        _clearDragObstacles = clearDragObstacles;
        _snapToGrid = snapToGrid;
        _titleBarHeight = titleBarHeight;
    }

    public void OnResizeDragStarted(object sender, DragStartedEventArgs e)
    {
        // Resize anti-overlap is explicitly out of scope (plan §3.6) — see PanelDragMoveBehavior.SnapToGrid's
        // remarks. Clear the drag-obstacle snapshot explicitly here too so a resize can never silently
        // inherit a stale, non-empty snapshot left behind by a drag gesture that ended through some path
        // other than PanelDragMoveBehavior.EndDrag (finalization MINOR m-1).
        _clearDragObstacles();

        _onResizeGestureStarting();

        _rawWidth = _window.Width;
        _rawHeight = _window.Height;
        _rawRight = _window.Left + _window.Width;
        _rawBottom = _window.Top + _window.Height;

        var model = _getModel();

        if (_appSettings.SnapResizeToIconGrid && model is not null && model.ViewMode == PanelViewMode.Grid)
        {
            // Grid mode: width snaps to whole columns (GridMetrics.CellSize == x) and height snaps to
            // whole rows (GridMetrics.RowHeight == x_v) — deliberately NOT the same cell size on both
            // axes (docs/PLANS/adaptive-icon-grid.md §5, "D-3"): the cell is 84x92 at IconSize=48, not
            // square, so reusing CellSize for height would quantise height a few pixels short of an
            // actual row.
            _snapCellWidth = GridMetrics.CellSize(model.IconSize);
            _snapCellHeight = GridMetrics.RowHeight(model.IconSize);
        }
        else
        {
            // List mode, or snapping disabled: BOTH axes are free-form (pixel-accurate). List mode is
            // always free on both axes — GridMetrics.CellSize/RowHeight describe icon-grid columns/rows,
            // which have nothing to do with a list row's height or a list panel's width.
            _snapCellWidth = 0d;
            _snapCellHeight = 0d;
        }
    }

    public void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string direction }) return;

        if (direction.Contains("Right"))
        {
            _rawWidth += e.HorizontalChange;
        }

        if (direction.Contains("Bottom"))
        {
            _rawHeight += e.VerticalChange;
        }

        if (direction.Contains("Left"))
        {
            _rawWidth -= e.HorizontalChange;
        }

        if (direction.Contains("Top"))
        {
            _rawHeight -= e.VerticalChange;
        }

        var model = _getModel();

        // Snap content area (strip chrome before snapping, then re-add).
        // FileListHPadding (left+right margin of FileListHost) is chrome for width just as
        // TitleBarHeight + FileListVPadding is chrome for height — strip it before snapping, for both
        // view modes.
        var contentW = Math.Max(0d, _rawWidth - GridMetrics.FileListHPadding);
        var contentH = Math.Max(0d, _rawHeight - _titleBarHeight - GridMetrics.FileListVPadding);

        // Snapped branches are rounded UP (Math.Ceiling) to a whole pixel, never down — the real Win32
        // window width/height is an integer device pixel, and without rounding up a fractional IconSize
        // or DPI/layout rounding can leave the live window a hair short of the Extent(k)/Extent(R) it was
        // just snapped to, making RowCapacity drop a whole column mid-drag (finalization MAJOR M-1). The
        // free-form (non-snapped) branches don't need this — they pass the raw drag delta straight
        // through, with no Extent(k) contract to honor. The icon-grid-derived minimum width only makes
        // sense in Grid mode; List mode has no columns at all, so it keeps the old flat floor instead of
        // inheriting a Grid-cell-sized minimum.
        var minWidth = model?.ViewMode == PanelViewMode.Grid
            ? GridMetrics.MinPanelWidth(model.IconSize)
            : GridMetrics.MinPanelSize;

        var newWidth = _snapCellWidth > 0d
            ? Math.Ceiling(Math.Max(minWidth, AdaptiveGridLayout.SnapExtent(contentW, _snapCellWidth, AdaptiveGridLayout.DefaultMinGap, ResizeSnapEdgePadding(_snapCellWidth)) + GridMetrics.FileListHPadding))
            : Math.Max(minWidth, contentW + GridMetrics.FileListHPadding);

        var newHeight = _snapCellHeight > 0d
            ? Math.Ceiling(Math.Max(GridMetrics.MinPanelSize, _titleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, _snapCellHeight, AdaptiveGridLayout.DefaultMinGap, ResizeSnapVerticalEdgePadding(_snapCellHeight))))
            : Math.Max(GridMetrics.MinPanelSize, _rawHeight);

        // Apply width.
        if (direction.Contains("Left"))
        {
            _window.Left = _rawRight - newWidth;
            _window.Width = newWidth;
        }
        else if (direction.Contains("Right"))
        {
            _window.Width = newWidth;
        }

        // Apply height.
        if (direction.Contains("Top"))
        {
            _window.Top = _rawBottom - newHeight;
            _window.Height = newHeight;
        }
        else if (direction.Contains("Bottom"))
        {
            _window.Height = newHeight;
        }
    }

    public void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _snapToGrid();
        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// Snaps the window width to the nearest whole column in Grid mode.
    /// Accounts for <see cref="GridMetrics.FileListHPadding"/> so left/right margins are equal.
    /// No-op in List mode or when snap is disabled.
    /// </summary>
    public void SnapWidthToGrid()
    {
        var model = _getModel();
        if (model is null || model.ViewMode != PanelViewMode.Grid || !_appSettings.SnapResizeToIconGrid) return;

        var cell = GridMetrics.CellSize(model.IconSize);
        var content = Math.Max(0d, _window.Width - GridMetrics.FileListHPadding);

        // Round the snapped width UP to a whole pixel (Math.Ceiling), never down: the actual Win32
        // window width is always an integer device pixel, and a fractional IconSize (sliders aren't
        // tick-snapped) or DPI/layout rounding can otherwise leave the real window a hair narrower than
        // the analytical Extent(k) it was just snapped to — which makes AdaptiveGridLayout.RowCapacity
        // see a shortfall and drop a whole column (finalization MAJOR M-1).
        _window.Width = Math.Ceiling(Math.Max(GridMetrics.MinPanelWidth(model.IconSize),
            AdaptiveGridLayout.SnapExtent(content, cell, AdaptiveGridLayout.DefaultMinGap, ResizeSnapEdgePadding(cell)) + GridMetrics.FileListHPadding));
    }

    /// <summary>
    /// HORIZONTAL edge padding (<c>m</c>) used by width resize-snap code (<see cref="OnResizeDragDelta"/>'s
    /// width branch, <see cref="SnapWidthToGrid"/>) to stay in agreement with
    /// <see cref="Controls.AdaptiveIconGridPanel"/>'s own layout math. Deliberately always uses
    /// <see cref="AdaptiveGridLayout.DefaultEdgePaddingRatio"/> rather than reading
    /// <c>AdaptiveIconGridPanel.EdgePaddingRatio</c> off the live panel — this class has no reachable
    /// reference to the panel instance buried inside the <c>ListBox</c>'s <c>DataTemplate</c>. This is an
    /// intentional, documented compromise (plan §4, "R5"): if <c>MinGap</c>/<c>EdgePaddingRatio</c> are
    /// ever overridden in Templates.xaml's <c>ItemsPanelTemplate</c>, update this method (and the
    /// <see cref="AdaptiveGridLayout.DefaultMinGap"/> use above it) to match, or resize-snap will
    /// disagree with the actual on-screen column count. See <see cref="ResizeSnapVerticalEdgePadding"/>
    /// for the VERTICAL (height) counterpart — the two axes no longer share one ratio/constant (bug fix:
    /// a single row of icons was reporting double its actual height, tripping a spurious scrollbar).
    /// </summary>
    private static double ResizeSnapEdgePadding(double cell)
        => AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

    /// <summary>
    /// VERTICAL edge padding (<c>m_v</c>) used by height resize-snap code (<see cref="OnResizeDragDelta"/>'s
    /// height branch) to stay in agreement with <see cref="Controls.AdaptiveIconGridPanel"/>'s own layout
    /// math — specifically its <c>VerticalEdgePaddingRatio</c> DP, whose default is
    /// <see cref="AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio"/> (<c>0</c>, deliberately NOT the
    /// same as <see cref="AdaptiveGridLayout.DefaultEdgePaddingRatio"/> used by
    /// <see cref="ResizeSnapEdgePadding"/> — see that constant's doc comment for the bug this fixes). Same
    /// "no reachable panel reference" compromise as <see cref="ResizeSnapEdgePadding"/> applies here too:
    /// if <c>AdaptiveIconGridPanel.VerticalEdgePaddingRatio</c> is ever overridden in Templates.xaml, update
    /// this method to match, or height resize-snap will silently desync from what the panel actually
    /// renders.
    /// </summary>
    private static double ResizeSnapVerticalEdgePadding(double cell)
        => AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultVerticalEdgePaddingRatio);
}
