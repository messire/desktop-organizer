using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// The ambient input environment the gesture state machine queries. Implemented by
/// <see cref="DesktopGestureService"/> over P/Invoke and faked in unit tests, so the pure gesture
/// logic can be covered without installing a real hook (same precedent as <c>GridSnap</c>, ADR-011).
/// </summary>
internal interface IDesktopGestureEnvironment
{
    /// <summary>Millisecond tick count used for double-click timing (<c>Environment.TickCount</c>).</summary>
    int TickCount { get; }

    /// <summary>System double-click time in milliseconds (<c>GetDoubleClickTime</c>).</summary>
    uint DoubleClickTime { get; }

    /// <summary>Whether the screen point is over the desktop (background or icon view), not over an app/panel.</summary>
    /// <param name="x">Screen X in pixels.</param>
    /// <param name="y">Screen Y in pixels.</param>
    /// <returns><see langword="true"/> when the point belongs to a desktop window class.</returns>
    bool IsDesktopPoint(int x, int y);

    /// <summary>Whether the screen point is over an empty part of the desktop (not on an icon).</summary>
    /// <param name="x">Screen X in pixels.</param>
    /// <param name="y">Screen Y in pixels.</param>
    /// <returns><see langword="true"/> when the point hits no desktop icon.</returns>
    bool IsEmptyDesktopPoint(int x, int y);
}

/// <summary>
/// The outcome of feeding one mouse message to <see cref="DesktopGestureStateMachine"/>.
/// A value type: the common "nothing happened" case allocates nothing on the hook hot path.
/// </summary>
/// <param name="Swallow">True when the hook must suppress the message (a completed lasso drag only).</param>
/// <param name="RaiseDoubleClick">True when <c>DesktopDoubleClicked</c> must be raised.</param>
/// <param name="Lasso">Non-null when <c>LassoCompleted</c> must be raised with these bounds.</param>
internal readonly record struct DesktopGestureDecision(bool Swallow, bool RaiseDoubleClick, DesktopLassoEventArgs? Lasso)
{
    /// <summary>Gets the "nothing happened" decision — the outcome for most mouse messages.</summary>
    public static DesktopGestureDecision None => default;

    /// <summary>Gets a value indicating whether any subscriber has to be notified for this decision.</summary>
    public bool RaisesEvent => RaiseDoubleClick || Lasso is not null;
}

/// <summary>
/// Pure gesture-recognition state machine for the desktop mouse hook: double-click on empty desktop
/// and right-button lasso drag (ADR-003). Contains no P/Invoke and no event plumbing — every OS query
/// goes through <see cref="IDesktopGestureEnvironment"/> — so the behaviour is unit-testable.
/// <para>
/// Not thread-safe by design: it is driven exclusively from the single WH_MOUSE_LL callback.
/// </para>
/// </summary>
/// <param name="environment">Provides tick/double-click timing and desktop hit-testing.</param>
internal sealed class DesktopGestureStateMachine(IDesktopGestureEnvironment environment)
{
    /// <summary>WM_MOUSEMOVE.</summary>
    internal const int WmMouseMove = 0x0200;

    /// <summary>WM_LBUTTONDOWN.</summary>
    internal const int WmLButtonDown = 0x0201;

    /// <summary>WM_RBUTTONDOWN.</summary>
    internal const int WmRButtonDown = 0x0204;

    /// <summary>WM_RBUTTONUP.</summary>
    internal const int WmRButtonUp = 0x0205;

    /// <summary>Chebyshev distance in pixels after which a right-button press counts as a drag.</summary>
    internal const int DragThreshold = 8;

    /// <summary>Chebyshev distance in pixels within which two clicks still count as a double click.</summary>
    internal const int DoubleClickSlop = 4;

    private int _lastLeftDownTime;
    private int _lastLeftDownX;
    private int _lastLeftDownY;

    private bool _rightDown;
    private bool _rightDragging;
    private int _rightStartX;
    private int _rightStartY;

    /// <summary>Gets a value indicating whether a right-button gesture is currently being tracked.</summary>
    internal bool IsRightDown => _rightDown;

    /// <summary>Gets a value indicating whether the tracked right-button gesture passed the drag threshold.</summary>
    internal bool IsRightDragging => _rightDragging;

    /// <summary>
    /// Feeds one mouse message to the state machine and returns what the hook should do.
    /// </summary>
    /// <param name="message">The window message (<c>wParam</c> of the low-level mouse hook).</param>
    /// <param name="x">Screen X of the message point, in pixels.</param>
    /// <param name="y">Screen Y of the message point, in pixels.</param>
    /// <returns>The decision for this message; <see cref="DesktopGestureDecision.None"/> for messages that change nothing.</returns>
    public DesktopGestureDecision Process(int message, int x, int y) => message switch
    {
        WmLButtonDown => HandleLeftDown(x, y),
        WmRButtonDown => HandleRightDown(x, y),
        WmMouseMove => HandleMouseMove(x, y),
        WmRButtonUp => HandleRightUp(x, y),
        _ => DesktopGestureDecision.None,
    };

    private static int Chebyshev(int ax, int ay, int bx, int by)
        => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    private DesktopGestureDecision HandleLeftDown(int x, int y)
    {
        var now = environment.TickCount;
        var isDouble = now - _lastLeftDownTime <= environment.DoubleClickTime
                       && Chebyshev(_lastLeftDownX, _lastLeftDownY, x, y) <= DoubleClickSlop;

        _lastLeftDownTime = now;
        _lastLeftDownX = x;
        _lastLeftDownY = y;

        // The (expensive, cross-process) hit-test is only reached for an actual double click.
        if (!isDouble || !environment.IsEmptyDesktopPoint(x, y)) return DesktopGestureDecision.None;

        _lastLeftDownTime = 0; // consume so a triple click isn't a second double
        return new DesktopGestureDecision(false, true, null);
    }

    private DesktopGestureDecision HandleRightDown(int x, int y)
    {
        if (!environment.IsDesktopPoint(x, y)) return DesktopGestureDecision.None;

        _rightDown = true;
        _rightDragging = false;
        _rightStartX = x;
        _rightStartY = y;
        return DesktopGestureDecision.None;
    }

    private DesktopGestureDecision HandleMouseMove(int x, int y)
    {
        if (_rightDown && !_rightDragging && Chebyshev(_rightStartX, _rightStartY, x, y) > DragThreshold)
            _rightDragging = true;

        return DesktopGestureDecision.None;
    }

    private DesktopGestureDecision HandleRightUp(int x, int y)
    {
        var completed = _rightDown && _rightDragging;
        var startX = _rightStartX;
        var startY = _rightStartY;

        // Reset BEFORE the caller notifies subscribers: a throwing subscriber must never leave the
        // machine latched in "right button down", which would corrupt the next right click.
        _rightDown = false;
        _rightDragging = false;

        if (!completed) return DesktopGestureDecision.None;

        var lasso = new DesktopLassoEventArgs(
            Math.Min(startX, x),
            Math.Min(startY, y),
            Math.Abs(x - startX),
            Math.Abs(y - startY));

        // Swallow the button-up so the desktop context menu does not appear for a completed drag.
        return new DesktopGestureDecision(true, false, lasso);
    }
}
