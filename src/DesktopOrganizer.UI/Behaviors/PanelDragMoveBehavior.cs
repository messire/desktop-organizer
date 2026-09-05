using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Owns manual title-bar drag-to-move for a <see cref="Views.ChildWindow"/>, extracted from
/// <c>ChildWindow.xaml.cs</c>'s <c>TitleBar_MouseLeftButtonDown/Move/Up</c>/<c>LostMouseCapture</c>/
/// <c>EndDrag</c> (REFACTORING_PLAN.md §5.3). Replaces <see cref="Window.DragMove()"/>, which blocks the
/// calling thread and hands movement off to the native OS move loop until mouse-up — incompatible with
/// per-mouse-move magnetic grid snapping (see docs/PLANS/grid-snap-during-drag.md). Also owns the
/// drop-time grid-snap (<see cref="SnapToGrid"/>), which is shared with <see cref="PanelResizeController"/>'s
/// resize-completed handler (see its remarks).
/// </summary>
public sealed class PanelDragMoveBehavior
{
    private readonly Window _window;
    private readonly IAppSettings _appSettings;
    private readonly IWindowManager _windowManager;
    private readonly Func<Guid?> _getPanelId;
    private readonly Action _onDragGestureStarting;
    private readonly Action _onDoubleClick;

    // Snapshot of every OTHER live panel's screen rectangle, taken once per drag gesture (see
    // docs/PLANS/panel-anti-overlap.md §3.3) — safe to take only once because the mouse is captured
    // for the whole gesture, so no other panel can move/resize/appear/disappear meanwhile. A reusable
    // list (not reallocated per-gesture) so TitleBar_MouseMove never allocates on the per-frame hot
    // path; repopulated in OnTitleBarMouseLeftButtonDown, cleared in EndDrag.
    private readonly List<Rect> _dragObstacles = [];

    private bool _isDragging;
    private Point _dragStartScreenCursor;
    private double _dragStartLeft;
    private double _dragStartTop;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelDragMoveBehavior"/> class.
    /// </summary>
    /// <param name="window">The owning panel window (its <see cref="Window.Left"/>/<see cref="Window.Top"/> are moved).</param>
    /// <param name="appSettings">Grid size/snap-distance settings driving the live drag-time magnet.</param>
    /// <param name="windowManager">Used to snapshot obstacle panels and persist layout state.</param>
    /// <param name="getPanelId">Accessor for the current panel's <see cref="ChildWindowModel.Id"/> (may return <see langword="null"/> before <c>Initialize</c>).</param>
    /// <param name="onDragGestureStarting">
    /// Invoked at the very start of every title-bar press, before the roll-up/drag branch is decided —
    /// commits any in-progress file-item rename and clears the panel's own file selection (mirrors the
    /// original <c>CommitActiveItemRenameIfAny</c> + <c>SelectedFile = null</c> pair).
    /// </param>
    /// <param name="onDoubleClick">Invoked instead of starting a drag when the title bar receives a double click (toggles roll-up).</param>
    public PanelDragMoveBehavior(
        Window window,
        IAppSettings appSettings,
        IWindowManager windowManager,
        Func<Guid?> getPanelId,
        Action onDragGestureStarting,
        Action onDoubleClick)
    {
        _window = window;
        _appSettings = appSettings;
        _windowManager = windowManager;
        _getPanelId = getPanelId;
        _onDragGestureStarting = onDragGestureStarting;
        _onDoubleClick = onDoubleClick;
    }

    /// <summary>
    /// Clears the drag-obstacle snapshot without ending a drag. Used by <see cref="PanelResizeController"/>'s
    /// <c>Resize_DragStarted</c> so a resize gesture can never inherit a stale snapshot left behind by a
    /// drag that ended through some path other than <see cref="EndDrag"/> (finalization MINOR m-1).
    /// </summary>
    public void ClearDragObstacles() => _dragObstacles.Clear();

    public void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // An active file-item inline rename never sees this click as a LostKeyboardFocus (the panel
        // carries WS_EX_NOACTIVATE) — commit it explicitly first, and clear this panel's own selection
        // (clicking the title bar is not clicking a file item) — see the constructor doc comment.
        _onDragGestureStarting();

        // Double click on the title bar toggles roll-up; single click drags the panel.
        // Clicks that land on child controls (rollup/rename/menu buttons) never reach here:
        // ButtonBase marks MouseLeftButtonDown as handled before it bubbles to this Border.
        if (e.ClickCount == 2)
        {
            _onDoubleClick();
            return;
        }

        // Start the drag optimistically, but only if mouse capture actually succeeds — otherwise
        // MouseMove/MouseLeftButtonUp may never arrive (e.g. capture denied while another element
        // already holds it) and _isDragging would get stuck true with no way to clear it.
        var captured = sender is UIElement element && element.CaptureMouse();
        if (!captured) return;

        _isDragging = true;
        _dragStartScreenCursor = _window.PointToScreen(Mouse.GetPosition(_window));
        _dragStartLeft = _window.Left;
        _dragStartTop = _window.Top;

        // Snapshot every other live panel's rectangle exactly once for this gesture (plan §3.3):
        // correct because the mouse is captured for the whole gesture, so nothing else on screen can
        // move/resize/appear/disappear in the meantime. The panel id can be null in tests that never
        // call Initialize — guard the same way the rest of this class does, and clear rather than leave
        // a stale snapshot from a previous gesture in that case.
        if (_getPanelId() is { } panelId)
            _windowManager.CollectOtherPanelBounds(panelId, _dragObstacles);
        else
            _dragObstacles.Clear();
    }

    /// <summary>
    /// Live drag: on every mouse move while dragging, re-derive the raw (unsnapped) window position
    /// from the cursor delta since the drag started, magnetically snap it to the icon grid via
    /// <see cref="GridSnap"/>, then clamp it against the drag-obstacle snapshot taken in
    /// <see cref="OnTitleBarMouseLeftButtonDown"/> so the panel can never overlap another live panel
    /// (docs/PLANS/panel-anti-overlap.md §3.5).
    /// </summary>
    public void OnTitleBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        // PointToScreen returns physical device pixels, but Window.Left/Top are device-independent
        // units (96 DPI baseline). At any DPI scale other than 100% (e.g. 125%/150%, common on
        // laptops) applying a raw device-pixel delta to a DIU coordinate would move the panel faster
        // than the cursor. Convert the delta to DIU using this window's DPI scale before applying it.
        var dpi = VisualTreeHelper.GetDpi(_window);
        var currentScreenCursor = _window.PointToScreen(Mouse.GetPosition(_window));
        var deltaX = (currentScreenCursor.X - _dragStartScreenCursor.X) / dpi.DpiScaleX;
        var deltaY = (currentScreenCursor.Y - _dragStartScreenCursor.Y) / dpi.DpiScaleY;
        var rawLeft = _dragStartLeft + deltaX;
        var rawTop  = _dragStartTop  + deltaY;

        var (snappedLeft, snappedTop) = GridSnap.Snap(rawLeft, rawTop, _appSettings.GridSize, _appSettings.GridSnapDistance);

        var current = new Rect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
        var clamped = PanelCollision.ClampAgainstObstacles(current, snappedLeft, snappedTop, _dragObstacles);

        _window.Left = clamped.X;
        _window.Top  = clamped.Y;
    }

    public void OnTitleBarMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndDrag(sender as UIElement);

    /// <summary>
    /// Handles involuntary loss of mouse capture (Alt+Tab, system dialog, window deactivation, etc.) so
    /// a drag never gets "stuck" with the dragging flag left <see langword="true"/>. Safe to call
    /// redundantly: <see cref="EndDrag"/> is a no-op once the drag has already ended.
    /// </summary>
    public void OnTitleBarLostMouseCapture(object sender, MouseEventArgs e)
        => EndDrag(sender as UIElement);

    private void EndDrag(UIElement? element)
    {
        if (!_isDragging) return;

        _isDragging = false;
        element?.ReleaseMouseCapture();
        SnapToGrid();

        // Drop the drag-gesture obstacle snapshot (docs/PLANS/panel-anti-overlap.md §3.3, R5): never
        // leave a stale snapshot lying around between gestures (e.g. after Alt+Tab/system-dialog
        // involuntary capture loss via OnTitleBarLostMouseCapture).
        _dragObstacles.Clear();

        _windowManager.SaveWindowsState();
    }

    /// <summary>
    /// Rounds Left and Top to the nearest grid cell after a drag or resize operation, using the same
    /// <see cref="IAppSettings.GridSnapDistance"/> threshold as the live drag-time magnet (rather than an
    /// unconditional quantise with <c>threshold = grid</c>). Keeping the same threshold for both the live
    /// magnet and the drop-time snap avoids a visible "jump" on mouse-up: with an unconditional drop
    /// snap, a panel released just outside the live-drag magnet radius (but still off-grid) would jerk
    /// onto the nearest line instead of staying where the user visually left it. GridSize&lt;=1 means no
    /// effective snapping (every pixel is a valid position).
    /// </summary>
    /// <remarks>
    /// Anti-overlap on mouse-up (docs/PLANS/panel-anti-overlap.md §3.5): this is NOT a full
    /// <see cref="PanelCollision.ClampAgainstObstacles"/> pass — the live drag already clamped the panel
    /// against <see cref="_dragObstacles"/> on every frame, so it cannot already be inside a neighbour
    /// when this runs. The only way the grid snap below could create a NEW overlap is by nudging the
    /// already-clamped position by up to <see cref="IAppSettings.GridSnapDistance"/> pixels back toward a
    /// grid line. Each axis is therefore snapped independently and rolled back (kept at its pre-snap
    /// value) if — and only if — <see cref="PanelCollision.CreatesNewOverlap"/> says that specific axis's
    /// snap would introduce a NEW overlap; an overlap that already existed before this drag (R1 — a
    /// pre-feature saved layout) never blocks the snap, because <c>CreatesNewOverlap</c> only reports
    /// overlaps that are new relative to <c>before</c>.
    /// <para>
    /// Also called by <see cref="PanelResizeController"/>'s resize-completed handler, but no drag gesture
    /// (and therefore no <see cref="OnTitleBarMouseLeftButtonDown"/>) runs before a resize, so
    /// <see cref="_dragObstacles"/> is empty there and <c>CreatesNewOverlap</c> always returns
    /// <see langword="false"/> for both axes — the snap behaves exactly as it did before this feature for
    /// a resize. This is intentional (plan §3.6 — resize anti-overlap is explicitly out of scope), not an
    /// oversight: do not "fix" it by populating <see cref="_dragObstacles"/> from resize handlers.
    /// </para>
    /// </remarks>
    public void SnapToGrid()
    {
        var grid = _appSettings.GridSize;
        if (grid <= 1) return;

        var threshold = _appSettings.GridSnapDistance;
        var width = _window.ActualWidth;
        var height = _window.ActualHeight;

        var snappedLeft = GridSnap.SnapAxis(_window.Left, grid, threshold);
        var beforeX = new Rect(_window.Left, _window.Top, width, height);
        var afterX = new Rect(snappedLeft, _window.Top, width, height);
        if (!PanelCollision.CreatesNewOverlap(beforeX, afterX, _dragObstacles))
            _window.Left = snappedLeft;

        var snappedTop = GridSnap.SnapAxis(_window.Top, grid, threshold);
        var beforeY = new Rect(_window.Left, _window.Top, width, height);
        var afterY = new Rect(_window.Left, snappedTop, width, height);
        if (!PanelCollision.CreatesNewOverlap(beforeY, afterY, _dragObstacles))
            _window.Top = snappedTop;
    }
}
