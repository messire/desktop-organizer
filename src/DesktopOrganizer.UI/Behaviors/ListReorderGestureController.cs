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
    private readonly Func<TItem, DataObject?>? _createDataObject;
    private readonly Action<TItem>? _onDragCompleted;
    private readonly Func<bool>? _isForeignPanelDrag;

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
    /// <param name="createDataObject">
    /// Optional (ADR-036, package E — panel-item drag-out): builds the full <see cref="DataObject"/> for
    /// a candidate once the drag threshold is exceeded, replacing the default
    /// <c>new DataObject(dataFormat, createPayload(candidate))</c>. Used by <c>ChildWindow</c> to also
    /// attach <see cref="DataFormats.FileDrop"/> and the shell's preferred-drop-effect format, so the
    /// SAME gesture that reorders within a panel also works as a drag-out to another panel or
    /// Explorer/the desktop. Returning <see langword="null"/> aborts the drag entirely, before
    /// <see cref="DragDrop.DoDragDrop"/> is ever called (e.g. a read-only source with nothing to drag
    /// out) — <paramref name="onDragCompleted"/> is not invoked in that case, since no drag started.
    /// </param>
    /// <param name="onDragCompleted">
    /// Optional (ADR-036, package E): invoked once per gesture, right before
    /// <paramref name="onGestureEnded"/>, in the same <c>finally</c> block — i.e. exactly when a drag
    /// that actually started (<see cref="DragDrop.DoDragDrop"/> was called) has fully finished,
    /// regardless of the resulting effect (a caller that needs the outcome reads it from its own
    /// session/state object instead — see <c>ChildWindow.OnPanelDragCompleted</c> and
    /// <c>IPanelItemDragOutSession</c> — since the OLE drop effect itself is not trustworthy, e.g.
    /// Explorer's own optimized move often reports <see cref="DragDropEffects.None"/>).
    /// </param>
    /// <param name="isForeignPanelDrag">
    /// Optional (ADR-036, package E): checked, with no arguments (purely ambient/session state), right
    /// after the <paramref name="dataFormat"/> presence check in <see cref="OnDragOver"/>/<see cref="OnDrop"/> —
    /// when it returns <see langword="true"/>, the event is left COMPLETELY untouched (mirroring the
    /// foreign-format early return, risk R1) even though <paramref name="dataFormat"/> IS present, so a
    /// drag that originated on a DIFFERENT panel (which still carries this SAME private reorder format,
    /// since the format name is shared by every panel) is not mistakenly treated as a same-panel reorder
    /// attempt — instead it keeps bubbling up to the owning <c>Window</c>'s own
    /// <see cref="DataFormats.FileDrop"/>-based drop handling (cross-panel transfer / external drop).
    /// </param>
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
        Action<TItem, int>? performReorder = null,
        Func<TItem, DataObject?>? createDataObject = null,
        Action<TItem>? onDragCompleted = null,
        Func<bool>? isForeignPanelDrag = null)
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
        _createDataObject = createDataObject;
        _onDragCompleted = onDragCompleted;
        _isForeignPanelDrag = isForeignPanelDrag;
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

        var candidate = _candidate;
        var data = ResolveDragData(candidate, _createDataObject, _createPayload, _dataFormat);
        if (data is null)
        {
            // ADR-036: createDataObject refused to build a payload (e.g. a read-only source with
            // nothing to drag out) — abort before DoDragDrop ever runs, so no gesture-ended/drag-
            // completed callback fires for a drag that never actually started.
            _candidate = null;
            return;
        }

        _isReordering = true;
        try
        {
            DragDrop.DoDragDrop(_dragSource, data, DragDropEffects.Move);
        }
        finally
        {
            _onDragCompleted?.Invoke(candidate);
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
        if (_isForeignPanelDrag?.Invoke() == true) return; // another panel's own drag-out — leave e untouched (see isForeignPanelDrag remarks).

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
        if (_isForeignPanelDrag?.Invoke() == true) return; // another panel's own drag-out — leave e untouched (see isForeignPanelDrag remarks).

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

    /// <summary>
    /// Resolves the <see cref="DataObject"/> for a drag gesture that just crossed the system drag
    /// threshold (<see cref="OnPreviewMouseMove"/>). Extracted as an internal static helper (M1 fix
    /// package, release-hardening 1.0 review) purely so its "return null to abort" contract is directly
    /// unit-testable without needing a live <see cref="DragDrop.DoDragDrop"/> gesture.
    /// </summary>
    /// <remarks>
    /// M2 (release-hardening 1.0 review): a prior version wrote this as
    /// <c>createDataObject?.Invoke(candidate) ?? new DataObject(...)</c> — which made the "return
    /// <see langword="null"/> to abort" contract documented on the constructor's <c>createDataObject</c>
    /// parameter dead code. <c>??</c> replaces a null RESULT of a non-null delegate with the default
    /// <see cref="DataObject"/> just as readily as it replaces a null DELEGATE, so <c>createDataObject</c>
    /// returning <see langword="null"/> (e.g. a read-only source with nothing to drag out) silently fell
    /// through to dragging the default payload anyway instead of aborting. Branching explicitly on
    /// whether the delegate itself was supplied preserves that contract: only "no <c>createDataObject</c>
    /// was ever passed in" falls back to the default <see cref="DataObject"/>; a supplied
    /// <c>createDataObject</c> that returns <see langword="null"/> is honored as an abort.
    /// </remarks>
    /// <param name="candidate">The drag candidate resolved at press time.</param>
    /// <param name="createDataObject">The optional <c>createDataObject</c> delegate (see the constructor's remarks).</param>
    /// <param name="createPayload">The <c>createPayload</c> delegate, used only when <paramref name="createDataObject"/> is <see langword="null"/>.</param>
    /// <param name="dataFormat">The reorder <see cref="DataObject"/> format, used only when <paramref name="createDataObject"/> is <see langword="null"/>.</param>
    /// <returns>The resolved <see cref="DataObject"/>, or <see langword="null"/> to abort the gesture before <see cref="DragDrop.DoDragDrop"/> is ever called.</returns>
    internal static DataObject? ResolveDragData(
        TItem candidate,
        Func<TItem, DataObject?>? createDataObject,
        Func<TItem, object> createPayload,
        string dataFormat)
        => createDataObject is not null
            ? createDataObject(candidate)
            : new DataObject(dataFormat, createPayload(candidate));
}
