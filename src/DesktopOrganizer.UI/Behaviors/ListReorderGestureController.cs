using System.Windows;
using System.Windows.Input;

namespace DesktopOrganizer.UI.Behaviors;

/// <summary>
/// Shared drag-to-reorder gesture controller (ADR-014 Decision 5/6, Block C). Owns the drag-candidate
/// bookkeeping, the system drag-threshold check, and the <see cref="DragDrop.DoDragDrop"/> call
/// (including its try/finally cleanup) that used to be duplicated almost verbatim between
/// <c>ChildWindow</c> (file items — <c>_reorderCandidate</c>/<c>_isReordering</c>/<c>_reorderStartPoint</c>)
/// and <c>SystemIconsPanelWindow</c> (system icon items — <c>_dragItem</c>/<c>_isDragging</c>/
/// <c>_dragStartPoint</c>) — see REFACTORING_PLAN.md §5.2/§6.5. Everything that genuinely differs between
/// the two hosts (candidate hit-testing/gating, payload shape, insertion-indicator geometry) is supplied
/// by the owning window via delegates, so this class itself carries no window-specific layout knowledge.
/// <para>
/// <c>ChildWindow</c> wires all six gesture methods (Down/Up/Move/DragOver/DragLeave/Drop); the DragOver/
/// DragLeave/Drop overloads are optional (only used when the owning window supplies the corresponding
/// delegates) because <c>SystemIconsPanelWindow</c>'s own DragOver/Drop hit-testing (a plain vertical
/// <c>StackPanel</c> walk) differs enough from <c>ChildWindow</c>'s (view-mode-aware
/// <see cref="ListReorderGeometry"/> hit-test) that unifying it risked a behavior change nobody could
/// verify visually in this environment — so it stays local to that window, and only Down/Move are shared
/// there.
/// </para>
/// </summary>
/// <typeparam name="TItem">The reorderable item type (<c>FileItem</c> or <c>SystemIconItemModel</c>).</typeparam>
public sealed class ListReorderGestureController<TItem>
    where TItem : class
{
    private readonly UIElement _dragSource;
    private readonly string _dataFormat;
    private readonly Func<DependencyObject, TItem?> _resolveDragCandidate;
    private readonly Func<TItem, object> _createPayload;
    private readonly Action _onGestureEnded;

    private readonly Func<DragEventArgs, TItem?>? _resolveDropTarget;
    private readonly Action<DragEventArgs>? _showInsertionIndicator;
    private readonly Action? _hideInsertionIndicator;
    private readonly Func<DragEventArgs, int>? _computeInsertionIndex;
    private readonly Action<TItem, int>? _performReorder;

    private Point _startPoint;
    private TItem? _candidate;
    private bool _isReordering;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListReorderGestureController{TItem}"/> class.
    /// </summary>
    /// <param name="dragSource">The element passed to <see cref="DragDrop.DoDragDrop"/> as the drag source.</param>
    /// <param name="dataFormat">The <see cref="DataObject"/> format identifying this reorder gesture.</param>
    /// <param name="resolveDragCandidate">
    /// Resolves the press-time drag candidate from the hit-tested source element. Fully owns any
    /// window-specific side effects (e.g. clearing the host's own selection on an empty-space click) —
    /// this controller never touches selection state itself.
    /// </param>
    /// <param name="createPayload">Builds the <see cref="DataObject"/> payload for a candidate once the drag threshold is exceeded.</param>
    /// <param name="onGestureEnded">
    /// Invoked after every <see cref="DragDrop.DoDragDrop"/> call returns (success, cancel, or a thrown
    /// exception — always runs via <c>finally</c>). Typically hides the insertion indicator.
    /// </param>
    /// <param name="resolveDropTarget">Optional: resolves a live item from a <see cref="DragEventArgs"/> for DragOver/Drop. Required to use <see cref="OnDragOver"/>/<see cref="OnDrop"/>.</param>
    /// <param name="showInsertionIndicator">Optional: shows/positions the insertion indicator for a DragOver. Required to use <see cref="OnDragOver"/>.</param>
    /// <param name="hideInsertionIndicator">Optional: hides the insertion indicator. Required to use <see cref="OnDragOver"/>/<see cref="OnDragLeave"/>/<see cref="OnDrop"/>.</param>
    /// <param name="computeInsertionIndex">Optional: computes the drop insertion index. Required to use <see cref="OnDrop"/>.</param>
    /// <param name="performReorder">Optional: applies the reorder to the owning collection. Required to use <see cref="OnDrop"/>.</param>
    public ListReorderGestureController(
        UIElement dragSource,
        string dataFormat,
        Func<DependencyObject, TItem?> resolveDragCandidate,
        Func<TItem, object> createPayload,
        Action onGestureEnded,
        Func<DragEventArgs, TItem?>? resolveDropTarget = null,
        Action<DragEventArgs>? showInsertionIndicator = null,
        Action? hideInsertionIndicator = null,
        Func<DragEventArgs, int>? computeInsertionIndex = null,
        Action<TItem, int>? performReorder = null)
    {
        _dragSource = dragSource;
        _dataFormat = dataFormat;
        _resolveDragCandidate = resolveDragCandidate;
        _createPayload = createPayload;
        _onGestureEnded = onGestureEnded;
        _resolveDropTarget = resolveDropTarget;
        _showInsertionIndicator = showInsertionIndicator;
        _hideInsertionIndicator = hideInsertionIndicator;
        _computeInsertionIndex = computeInsertionIndex;
        _performReorder = performReorder;
    }

    /// <summary>True while an OLE drag-reorder gesture (<see cref="DragDrop.DoDragDrop"/>) is in flight.</summary>
    public bool IsReordering => _isReordering;

    /// <summary>
    /// Records the drag start point and resolves the drag candidate from the press. Mirrors the original
    /// <c>*_PreviewMouseLeftButtonDown</c> handlers: the start point is always recorded, even when
    /// <c>resolveDragCandidate</c> ultimately returns <see langword="null"/> — harmless,
    /// since <see cref="OnPreviewMouseMove"/> immediately no-ops while there is no candidate.
    /// </summary>
    /// <param name="sender">Unused; present to match the WPF mouse-event handler signature.</param>
    /// <param name="e">The mouse-button event; its cursor position becomes the recorded drag start point.</param>
    public void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _candidate = null;
        if (e.OriginalSource is not DependencyObject source) return;

        _startPoint = e.GetPosition(null);
        _candidate = _resolveDragCandidate(source);
    }

    /// <summary>
    /// Clears a leftover drag-reorder candidate from a plain click that never moved far enough to start
    /// a drag. Not wired by every host — a host that never had this problem in the original code
    /// (e.g. <c>SystemIconsPanelWindow</c>) simply never calls it.
    /// </summary>
    /// <param name="sender">The event source; unused.</param>
    /// <param name="e">The mouse-button event data; unused, present only to match the WPF handler signature.</param>
    public void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isReordering) return;
        _candidate = null;
    }

    /// <summary>
    /// Promotes a remembered drag candidate into an actual <see cref="DragDrop.DoDragDrop"/> gesture once
    /// the mouse has moved past the system drag threshold, so a short click never starts a drag.
    /// </summary>
    /// <param name="sender">Unused; present to match the WPF mouse-event handler signature.</param>
    /// <param name="e">The mouse-move event; its current position is compared against the recorded drag start point.</param>
    public void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _candidate is null || _isReordering) return;

        var delta = e.GetPosition(null) - _startPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _isReordering = true;
        try
        {
            var data = new DataObject(_dataFormat, _createPayload(_candidate));
            DragDrop.DoDragDrop(_dragSource, data, DragDropEffects.Move);
        }
        finally
        {
            _isReordering = false;
            _candidate = null;
            _onGestureEnded();
        }
    }

    /// <summary>
    /// Accepts or rejects a drag-reorder hover. For a foreign data format this returns immediately
    /// WITHOUT touching <see cref="DragEventArgs.Effects"/>/<see cref="RoutedEventArgs.Handled"/> at all —
    /// required so an incoming <see cref="DataFormats.FileDrop"/> from the desktop/Explorer keeps
    /// bubbling to the window's own <c>OnDragEnter</c> (critical risk R1, see <c>ChildWindow</c>'s
    /// original remarks).
    /// </summary>
    /// <param name="e">
    /// The drag-over event. Left untouched (including <see cref="System.Windows.RoutedEventArgs.Handled"/>)
    /// for a foreign data format; otherwise its <see cref="DragEventArgs.Effects"/> and
    /// <see cref="System.Windows.RoutedEventArgs.Handled"/> are set to reflect the drop decision.
    /// </param>
    public void OnDragOver(DragEventArgs e)
    {
        if (_resolveDropTarget is null || _showInsertionIndicator is null || _hideInsertionIndicator is null)
            throw new InvalidOperationException($"{nameof(OnDragOver)} requires resolveDropTarget/showInsertionIndicator/hideInsertionIndicator to be supplied.");

        if (!e.Data.GetDataPresent(_dataFormat)) return; // foreign format — leave e untouched (risk R1).

        if (_resolveDropTarget(e) is null)
        {
            e.Effects = DragDropEffects.None;
            _hideInsertionIndicator();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        _showInsertionIndicator(e);
        e.Handled = true;
    }

    /// <summary>Hides the insertion indicator on drag-leave.</summary>
    /// <param name="e">Unused; present to match the WPF drag-event handler signature.</param>
    public void OnDragLeave(DragEventArgs e) => _hideInsertionIndicator?.Invoke();

    /// <summary>
    /// Completes a drag-reorder drop. Mirrors <see cref="OnDragOver"/>'s accept/reject split exactly, for
    /// the same reason: a foreign-format drop must be left completely untouched.
    /// </summary>
    /// <param name="e">
    /// The drop event. Left untouched for a foreign data format; otherwise the reorder is applied and
    /// <see cref="System.Windows.RoutedEventArgs.Handled"/> is set.
    /// </param>
    public void OnDrop(DragEventArgs e)
    {
        if (_resolveDropTarget is null || _hideInsertionIndicator is null ||
            _computeInsertionIndex is null || _performReorder is null)
            throw new InvalidOperationException($"{nameof(OnDrop)} requires resolveDropTarget/hideInsertionIndicator/computeInsertionIndex/performReorder to be supplied.");

        if (!e.Data.GetDataPresent(_dataFormat)) return; // foreign format — leave e untouched (risk R1).

        if (_resolveDropTarget(e) is not { } item)
        {
            _hideInsertionIndicator();
            e.Handled = true;
            return;
        }

        var insertIndex = _computeInsertionIndex(e);
        _performReorder(item, insertIndex);

        _hideInsertionIndicator();
        e.Handled = true;
    }
}
