using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;

using static DesktopOrganizer.UI.Views.VisualTreeWalker;

namespace DesktopOrganizer.UI.Views;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>
/// Desktop panel window. Chromeless, embedded into the desktop layer via WorkerW.
/// </summary>
/// <remarks>
/// Owns only window lifecycle (<see cref="Initialize"/>/<see cref="SyncToModel"/>/<see cref="OnClosing"/>),
/// external file drag/drop (<see cref="OnDragEnter"/>/<see cref="OnDrop"/>), and thin XAML event wiring.
/// Everything else has been extracted into <see cref="DesktopOrganizer.UI.Behaviors"/> classes
/// (REFACTORING_PLAN.md §5.3): title-bar drag (<see cref="PanelDragMoveBehavior"/>), resize
/// (<see cref="NativePanelResizeController"/>), roll-up (<see cref="RollUpBehavior"/>), title inline-rename
/// (<see cref="PanelTitleRenameBehavior"/>), drag-to-reorder (<see cref="ListReorderGestureController{TItem}"/>),
/// and the auto-hiding hint popups (<see cref="HintPopupController"/>).
/// </remarks>
public partial class ChildWindow : Window
{
    /// <summary>
    /// The panel's fixed title-bar/header height, in DIP. Internal (rather than private) so
    /// <c>WindowManager.CollectOtherPanelBounds</c> can use it as the target/final height of a
    /// rolled-up panel, instead of reading a mid-animation <c>ActualHeight</c> (finalization MAJOR
    /// M-2) — see the constant's use there for details. Sourced from
    /// <see cref="PanelMetrics.HeaderHeight"/> (D-11, docs/PLANS/phase-5-ambient-accent-ui.md §1
    /// D-11) rather than repeating its own literal — <c>PanelMetrics.cs</c> is the single source of
    /// truth for this value, also consumed directly by <c>ChildWindow.xaml</c>'s
    /// <c>PanelChrome</c>/<c>PanelRoot</c> row heights and by <see cref="RollUpBehavior"/>'s
    /// collapsed target height (via the <c>titleBarHeight</c> parameter passed below, unchanged from
    /// before).
    /// </summary>
    internal const double TitleBarHeight = PanelMetrics.HeaderHeight;

    /// <summary>
    /// Custom <see cref="DataObject"/> format used to identify an in-panel drag-to-reorder gesture
    /// (ADR-014 Decision 5, Block C), as opposed to an incoming <see cref="DataFormats.FileDrop"/>
    /// from the desktop/Explorer. The payload is the dragged item's <see cref="FileItem.Path"/> (a
    /// string), not the <see cref="FileItem"/> instance itself — a string survives crossing the OLE
    /// data-object boundary safely, and resolving it back to a <see cref="FileItem"/> by looking it
    /// up in the target panel's own <see cref="BaseViewModel.FileItems"/> doubles as a free
    /// guard against a drag started from a different panel (path not found there ⇒ rejected).
    /// </summary>
    private const string PanelItemReorderFormat = "DesktopOrganizer.PanelItemReorder";

    private readonly ChildWindowViewModel _viewModel;
    private readonly IWindowManager _windowManager;
    private readonly IDesktopIntegrationService _desktopIntegration;
    private readonly IAppSettings _appSettings;
    private readonly IGestureDiagnostics _diagnostics;
    private readonly ILayoutEditModeService _layoutEditMode;
    private readonly IMonitorService _monitorService;
    private readonly IDesktopScaleProvider _desktopScale;

    // P06 (docs/fix-prompts/06-fit-content.md): backs ChildWindow.ContentSizing.cs's "Apply to all
    // panels…" confirmation dialogs. IConfirmationService is already a DI singleton (Configuration/DiConfig.cs)
    // used by MainWindowViewModel for the same purpose — MessageBox.Show has its own top-level HWND, so
    // it receives input/activation normally regardless of this panel's own WS_EX_NOACTIVATE style.
    private readonly IConfirmationService _confirmation;

    // docs/prompts/settings-window-redesign.md D-3/WP-B: backs CustomizePanelMenu_Click. Already a DI
    // singleton (Configuration/DiConfig.cs) — the same instance MainWindowViewModel's own
    // "Customize…" button (WP-A) resolves, so both entry points share the one open dialog instance.
    private readonly IPanelCustomizeService _panelCustomize;

    private readonly PanelDragMoveBehavior _dragMoveBehavior;

    // docs/PLANS/phase-6-edit-layout-ux.md §1 D-5: the gate wraps this panel's NativeSizingAffordance and is
    // what actually gets handed to RollUpBehavior — RollUpBehavior keeps calling SetEnabled(!collapsed)
    // exactly as before, unaware a second, independent policy input now also gates the same affordance.
    private readonly ResizeAffordanceGate _resizeGate;

    // ADR-022 (docs/PLANS/native-resize-single-path.md): the native WM_SIZING sizing loop is the only resize
    // path. NativePanelResizeController is driven entirely by INativePanelSizingService callbacks — there are
    // no XAML resize event handlers.
    private readonly NativePanelResizeController _resizeController;
    private readonly RollUpBehavior _rollUpBehavior;
    private readonly PanelTitleRenameBehavior _titleRename;
    private readonly PanelVisualStateBehavior _panelVisualState;

    // docs/PLANS/phase-6-edit-layout-ux.md §1 D-6: the direct sibling of _panelVisualState above — writes
    // PanelTheme.IsLayoutEditingProperty onto the same PanelRoot instead of PanelTheme.VisualStateProperty.
    private readonly LayoutEditModeBehavior _layoutEditModeVisual;
    private readonly ListReorderGestureController<FileItem> _reorderController;
    private readonly HintPopupController _hintPopups;

    // ADR-036 (release-hardening-1.0.md §E): coordinates a panel-item drag-out gesture across panels —
    // process-wide singleton (Configuration/DiConfig.cs), UI-thread-only. See IPanelItemDragOutSession.
    private readonly IPanelItemDragOutSession _dragOutSession;

    // ADR-036 §E: confirms an external (Explorer/desktop) drag-out actually moved the file before this
    // panel removes its own entry — see CreatePanelItemDragData/OnPanelDragCompleted below. Lazily
    // constructed (see its use) so a panel that never drags an item out never pays for a DispatcherTimer.
    private readonly MovedOutVerifier _movedOutVerifier;

    // Stage S (docs/PLANS/workspace-grid-03-04-integration.md §1.5): the single point P03/P04 write
    // Left/Top/Width/Height + the model's Position/Size (+ span/MonitorDeviceId) through together, so
    // OnLocationChanged/OnRenderSizeChanged below never fight a P03/P04-driven geometry write with their
    // own redundant model write (baseline risk 17). Constructed once, unconditionally.
    private readonly PanelGeometryWriter _geometryWriter;

    // ADR-021: one PanelInteractionTracker per panel, shared by every gesture behaviour below so "only
    // one gesture owns capture at a time" is enforced across drag AND resize. Constructed before both
    // behaviours (docs/PLANS/phase-1-resize-stabilization.md §3.5).
    private readonly PanelInteractionTracker _interactionTracker = new();

    // M3 (release-hardening 1.0 review): guards OnLocationChanged for the whole span of a per-monitor DPI
    // change (WM_DPICHANGED) — Set at the very start of OnDpiChanged, Clear()ed only once a
    // Dispatcher.BeginInvoke(Background) callback actually runs, i.e. strictly after every Left/Top nudge
    // WPF's own base.OnDpiChanged rescale (and any further internal adjustment still queued for the same
    // message) has already produced. Without this, a TRANSIENT Left/Top value synchronously raised
    // OnLocationChanged mid-transition, writing a position that was neither the pre-DPI-change one nor
    // the final, adapter-resolved one into ChildWindowModel.Position — see OnDpiChanged/OnLocationChanged's
    // own remarks. See TransitionLatch's own remarks for why this is a small pure/testable type rather
    // than a bare bool field.
    private readonly TransitionLatch _dpiTransition = new();

    private ChildWindowModel? _model;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindow"/> class.
    /// </summary>
    /// <param name="viewModel">The panel's ViewModel, bound as this window's <see cref="FrameworkElement.DataContext"/>.</param>
    /// <param name="windowManager">Creates/removes/persists panel windows; used to save layout state after gestures.</param>
    /// <param name="desktopIntegration">Embeds/positions this window on the desktop layer (WorkerW).</param>
    /// <param name="appSettings">Global application settings driving snap-to-grid and other panel behavior.</param>
    /// <param name="diagnostics">Log-only observer of the resize/drag gesture lifecycle.</param>
    /// <param name="nativeSizingService">
    /// Installs the window-message hook (<c>WM_NCHITTEST</c>/<c>WM_ENTERSIZEMOVE</c>/<c>WM_SIZING</c>/
    /// <c>WM_EXITSIZEMOVE</c>, among others) that drives native panel sizing when it is enabled.
    /// </param>
    /// <param name="layoutEditMode">The application-global Edit Layout mode service.</param>
    /// <param name="monitorService">Provides monitor topology for placement/anti-overlap/grid resolution.</param>
    /// <param name="confirmation">Gates destructive "apply to all panels" actions behind a user confirmation prompt.</param>
    /// <param name="panelCustomize">Opens the modeless "Customize panel…" dialog for this panel.</param>
    /// <param name="dragOutSession">Coordinates a panel-item drag-out gesture across panels (ADR-036, release-hardening-1.0.md §E).</param>
    /// <param name="desktopScale">
    /// The per-monitor DPI snapshot provider (release-hardening F, ADR-037) — supplies every px↔desktop-DIP
    /// conversion this window and its behaviors need (<see cref="PanelGeometryWriter"/>,
    /// <see cref="PanelDragMoveBehavior"/>, the resize controllers, <see cref="OnDpiChanged"/>).
    /// </param>
    public ChildWindow(
        ChildWindowViewModel viewModel,
        IWindowManager windowManager,
        IDesktopIntegrationService desktopIntegration,
        IAppSettings appSettings,
        IGestureDiagnostics diagnostics,
        INativePanelSizingService nativeSizingService,
        ILayoutEditModeService layoutEditMode,
        IMonitorService monitorService,
        IConfirmationService confirmation,
        IPanelCustomizeService panelCustomize,
        IPanelItemDragOutSession dragOutSession,
        IDesktopScaleProvider desktopScale)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windowManager = windowManager;
        _desktopIntegration = desktopIntegration;
        _appSettings = appSettings;
        _diagnostics = diagnostics;
        _layoutEditMode = layoutEditMode;
        _monitorService = monitorService;
        _confirmation = confirmation;
        _panelCustomize = panelCustomize;
        _dragOutSession = dragOutSession;
        _desktopScale = desktopScale;

        // M1 (release-hardening 1.0 review): a FRESH DispatcherDebounceTimer per Schedule() call — never
        // a single cached instance — so an item dragged out while an earlier drag-out's own 500/2000 ms
        // recheck is still pending gets its own independent timer instead of silently cancelling the
        // first item's still-in-flight check (see MovedOutVerifier's own remarks). Every timer this
        // factory hands out is tracked by MovedOutVerifier itself until its chain finishes; OnClosing
        // below stops whichever are still pending via _movedOutVerifier.CancelAll().
        _movedOutVerifier = new MovedOutVerifier(() => new DispatcherDebounceTimer(Dispatcher));
        DataContext = _viewModel;

        // ADR-022: the resize controller needs no HWND at construction — it is created here, early, because
        // RollUpBehavior (which needs the resulting IResizeAffordance) must itself be constructed before
        // _dragMoveBehavior below (its onDoubleClick callback captures _rollUpBehavior). Only the native hook's
        // actual installation (NativePanelResizeController.AttachTo, which DOES need a real HWND) is deferred
        // to OnSourceInitialized, after EmbedInDesktop — see that method.

        // docs/PLANS/phase-6-edit-layout-ux.md §1 D-2/D-5: read fresh on every title-bar press by
        // PanelDragMoveBehavior — never snapshotted — so a mode/lock/preference change made while no
        // gesture is in flight is picked up by the very next press without this panel needing to do
        // anything else.
        bool CanMove() => LayoutGesturePolicy.Resolve(
            _layoutEditMode.Mode,
            _layoutEditMode.IsLayoutLocked,
            NormalModeLayoutGesturesParser.ParseOrMoveOnly(_appSettings.NormalModeLayoutGestures)).CanMove;

        // Constructed before _dragMoveBehavior/_resizeController (P03): it is handed to
        // PanelDragMoveBehavior, which commits every GridAligned drop through it (plan §1.5).
        _geometryWriter = new PanelGeometryWriter(this, () => _model, _desktopScale);

        // Constructed before _dragMoveBehavior: its onDoubleClick callback below captures
        // _rollUpBehavior, and assigning it first keeps that a real (non-nullable) reference
        // rather than a forward reference nullable analysis can't see through.
        // ADR-022 G-3: the visual-only ResizeGripVisuals follows the same gated flag, so the Edit Layout
        // accent grips are shown/hidden together with the (hook-side) resize enable flag.
        var nativeAffordance = new NativeSizingAffordance(ResizeGripVisuals);
        _resizeGate = new ResizeAffordanceGate(nativeAffordance);
        _rollUpBehavior = new RollUpBehavior(
            this, _windowManager, ContentRow, FileListHost, _resizeGate, TitleBarHeight, () => _model, desktopScale: _desktopScale);

        _dragMoveBehavior = new PanelDragMoveBehavior(
            this,
            _appSettings,
            _windowManager,
            getPanelId: () => _model?.Id,
            getModel: () => _model,
            onDragGestureStarting: OnPanelGestureStarting,
            onDoubleClick: () => _rollUpBehavior.ToggleRollUp(),
            diagnostics: _diagnostics,
            tracker: _interactionTracker,
            canMove: CanMove,
            layoutEditMode: _layoutEditMode,
            monitorService: _monitorService,
            geometryWriter: _geometryWriter,
            desktopScale: _desktopScale);

        _resizeController = new NativePanelResizeController(
            this,
            _appSettings,
            _windowManager,
            getModel: () => _model,
            onResizeGestureStarting: OnPanelGestureStarting,
            clearDragObstacles: _dragMoveBehavior.ClearDragObstacles,
            snapToGrid: _dragMoveBehavior.SnapToGrid,
            titleBarHeight: TitleBarHeight,
            diagnostics: _diagnostics,
            tracker: _interactionTracker,
            nativeSizingService: nativeSizingService,
            isResizeEnabled: () => nativeAffordance.IsEnabled,
            desktopScale: _desktopScale);

        // D-5: apply the real policy value immediately — before the panel is ever shown — rather than
        // leaving the gate at its true/true construction default (see ResizeAffordanceGate's remarks).
        _resizeGate.SetAllowedByPolicy(LayoutGesturePolicy.Resolve(
            _layoutEditMode.Mode,
            _layoutEditMode.IsLayoutLocked,
            NormalModeLayoutGesturesParser.ParseOrMoveOnly(_appSettings.NormalModeLayoutGestures)).CanResize);

        // D-4: subscribed here (constructor) / unsubscribed in OnClosing, mirroring every other
        // long-lived subscription this window makes to something that outlives it (_viewModel.PropertyChanged
        // below). PropertyChanged is also subscribed (not just ModeChanged) so an IsLayoutLocked change
        // that does NOT also transition Mode — e.g. locking while already in Normal — still re-applies
        // the resize gate; see AbortGesturesAndApplyLayoutGate's remarks.
        _layoutEditMode.ModeChanged += OnLayoutModeChanged;
        _layoutEditMode.PropertyChanged += OnLayoutEditModePropertyChanged;

        _titleRename = new PanelTitleRenameBehavior(
            _windowManager, TitleText, TitleEditBox, () => _model, SyncToModel);

        // D-6 (docs/PLANS/phase-5-ambient-accent-ui.md §1 D-6): the first real subscriber to
        // PanelInteractionTracker.StateChanged, resolving PanelRoot/PanelTheme.VisualState from the
        // tracker's gesture state plus PanelRoot's own IsMouseOver and _titleRename.IsEditingTitle.
        // Constructed after _titleRename (its isTitleEditing callback reads it) and after
        // _interactionTracker (field initializer above, already assigned).
        _panelVisualState = new PanelVisualStateBehavior(PanelRoot, _interactionTracker, () => _titleRename.IsEditingTitle);

        // D-6: the direct sibling of _panelVisualState above — writes PanelTheme.IsLayoutEditingProperty
        // onto the same PanelRoot from ILayoutEditModeService.ModeChanged instead of PanelInteractionTracker.StateChanged.
        _layoutEditModeVisual = new LayoutEditModeBehavior(PanelRoot, _layoutEditMode);

        _reorderController = new ListReorderGestureController<FileItem>(
            dragSource: FileListHost,
            dataFormat: PanelItemReorderFormat,
            resolveDragCandidate: ResolveReorderDragCandidate,
            createPayload: item => item.Path,
            onGestureEnded: HideReorderInsertionIndicator,
            resolveDropTarget: TryResolveReorderItem,
            showInsertionIndicator: ShowReorderInsertionIndicator,
            hideInsertionIndicator: HideReorderInsertionIndicator,
            computeInsertionIndex: ComputeDropInsertionIndex,
            performReorder: PerformReorder,
            createDataObject: CreatePanelItemDragData,
            onDragCompleted: OnPanelDragCompleted,
            isForeignPanelDrag: () => _dragOutSession.IsActive && _dragOutSession.SourcePanelId != _model?.Id);

        _hintPopups = new HintPopupController(
            Dispatcher, MoveHintPopup, RenameErrorPopup, RenameErrorText, FileListHost, () => _viewModel.EditingItem, MoveHintText);

        // Persist whenever the user changes the file list from the panel (e.g. removes an item).
        _viewModel.FilesChanged += (_, _) => _windowManager.SaveWindowsState();

        // One-time, non-blocking hint the first time a live drag-drop actually moves a file/folder
        // into app-managed storage — see ChildWindowViewModel.MoveHintRequested.
        _viewModel.MoveHintRequested += (_, _) => _hintPopups.ShowMoveHint();

        // Read-only session (F5): the first drop onto this panel leaves the file in place - say so, once.
        _viewModel.ReadOnlyDropHintRequested += (_, _) => _hintPopups.ShowReadOnlyDropHint();

        // Non-blocking hint whenever an inline-rename attempt fails (see ChildWindowViewModel.RenameError).
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // ADR-046 D-6: a page switch hides/shows this window (WindowManager) without going back through
        // ChildWindowViewModel.LoadFromModel, so the panel's background icon-load priority is kept in
        // sync here instead — Visible (current page) always dequeues ahead of Background (hidden pages).
        IsVisibleChanged += OnIsVisibleChanged;

        // P06 hook (docs/fix-prompts/06-fit-content.md) — see ChildWindow.ContentSizing.cs.
        InitializeContentSizing();
    }

    /// <summary>
    /// Initializes the window state from a <see cref="ChildWindowModel"/>.
    /// </summary>
    /// <param name="model">The panel model to bind this window's geometry/behaviors to.</param>
    public void Initialize(ChildWindowModel model)
    {
        _model = model;

        // release-hardening F (ADR-037): model.Position is desktop-DIP; converted to this window's Left/
        // Top through the adapter. Best-effort at this point — the window has no PresentationSource yet
        // (before Show()), so PanelWindowCoordinates falls back to the snapshot's uniform scale; OnDpiChanged
        // re-applies once this window's real per-monitor DPI is known. Identity on any uniform snapshot
        // (every single-scale configuration), matching 0.6.1 bit-for-bit.
        var initialPosition = PanelWindowCoordinates.ToWindowPosition(
            this, new System.Windows.Point(model.Position.X, model.Position.Y), _desktopScale.Current);
        Left = initialPosition.X;
        Top = initialPosition.Y;

        if (model.Size.Width > 0) Width = model.Size.Width;
        if (model.Size.Height > 0) Height = model.Size.Height;

        WindowState = model.WindowState;
        _rollUpBehavior.ExpandedHeight = Height;

        // Best-effort DPI for the very first icon fetch (no PresentationSource yet — same caveat as the
        // position above); OnSourceInitialized/OnDpiChanged push the real per-monitor value, and the view
        // model only re-fetches if that moves the icon pixel size to another bucket.
        _viewModel.IconRenderScale = PanelWindowCoordinates.GetWindowScale(this, _desktopScale.Current);

        _viewModel.LoadFromModel(model);

        // finalization MAJOR-1 (P05 fix-cycle, ADR-028/ADR-029 amendments): deliberately NO
        // SnapWidthToGrid() call here. A prior version of this method re-snapped the persisted width on
        // every load "so persisted sizes stay aligned even after the icon size was changed in a previous
        // session" — but ADR-029's F-3 chrome fix (GridMetrics.FileListHPadding, 0 -> 8) changed WHAT
        // COUNTS AS a valid snap point, so re-running that snap on load turned a byte-for-byte replay of
        // a saved layout into a silent one-time width jump the very first time a panel was opened after
        // upgrading (e.g. a persisted 344 DIP 3-column width became 352; a persisted 168 DIP 1-column
        // width became 176) — a direct violation of ADR-028's "no jump on first launch" invariant, which
        // this method must uphold for EVERY panel geometry mode, not just PanelGeometryMode.GridAligned.
        // Loading a panel must NEVER mutate its saved bounds; only a user-driven action may
        // (OnModelPropertyChanged below, still snapping on an actual IconSize/ViewMode change, and the
        // live resize-drag path) — see ADR-029's Q2 amendment and ADR-028's LegacyFreeform section for
        // the full analysis of why this is now a hard invariant, not merely today's happenstance.
        model.PropertyChanged += OnModelPropertyChanged;

        // P06 hook (docs/fix-prompts/06-fit-content.md) — must run AFTER the resize-snap subscription
        // immediately above: this partial's own OnModelPropertyChangedForContentSizing relies on
        // subscription order to observe Window.Width AFTER any IconSize/ViewMode/IconLayoutPolicy-driven
        // SnapWidthToGrid() the line above already triggered for the same PropertyChanged event — see
        // ChildWindow.ContentSizing.cs's AttachContentSizing remarks. Deliberately does NOT force an
        // immediate recompute here (see the same remarks for why).
        AttachContentSizing(model);

        if (model.IsRolledUp) _rollUpBehavior.ApplyRollUp(true);
    }

    /// <summary>
    /// Syncs the current title and file list from the ViewModel back to the model for persistence.
    /// </summary>
    public void SyncToModel()
    {
        if (_model is null) return;

        // Title and appearance live directly on the model (bound two-way); only files need syncing.
        // Folder-portal panels mirror their folder, so their list is never persisted.
        if (string.IsNullOrWhiteSpace(_model.SourceFolderPath))
            _model.FilePaths = _viewModel.AllItems.Select(f => f.Path).ToList();
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="source"/> (a routed mouse event's
    /// <c>OriginalSource</c>) landed inside the inline-rename editor (<c>Templates.xaml</c>'s
    /// <c>FileNameEditBoxStyle</c> <see cref="TextBox"/>). Extracted as a pure, testable predicate — see
    /// <see cref="FileListHost_PreviewMouseRightButtonUp"/>'s own remarks.
    /// </summary>
    /// <param name="source">The visual-tree element the routed event reports as its source.</param>
    internal static bool IsInlineRenameEditorHit(DependencyObject source)
        => FindAncestorOrSelf<TextBox>(source) is not null;

    /// <summary>
    /// MINOR 9 (release-polish 1.0 final review): <see langword="true"/> when <paramref name="source"/> (a
    /// routed mouse event's <c>OriginalSource</c>) landed inside a <see cref="ScrollBar"/> — the list/grid
    /// view's own vertical scrollbar lives inside <see cref="FileListHost"/>, so without this check a
    /// right-click on it fell through to <see cref="FileListHost_PreviewMouseRightButtonUp"/>'s "empty
    /// panel body" branch and popped the fence's <c>HeaderContextMenu</c> instead of leaving the scrollbar
    /// alone. Extracted as a pure, testable predicate, same as <see cref="IsInlineRenameEditorHit"/>.
    /// </summary>
    /// <param name="source">The visual-tree element the routed event reports as its source.</param>
    internal static bool IsScrollBarHit(DependencyObject source)
        => FindAncestorOrSelf<ScrollBar>(source) is not null;

    /// <summary>
    /// Resolves the <see cref="FileItem"/> a right-click at <paramref name="source"/> (a routed mouse
    /// event's <c>OriginalSource</c>) landed on — the item's own <see cref="ListBoxItem"/> container
    /// (list and grid templates alike), or <see langword="null"/> when the click missed every item (the
    /// EMPTY panel body — UX parity fix, fences-ux-parity-reviewer, release-polish 1.0 review). Extracted
    /// as a pure, testable hit-test helper — see <see cref="FileListHost_PreviewMouseRightButtonUp"/>'s
    /// own remarks.
    /// </summary>
    /// <param name="source">The visual-tree element the routed event reports as its source.</param>
    internal static FileItem? ResolveRightClickedFileItem(DependencyObject source)
        => FindAncestorOrSelf<ListBoxItem>(source)?.DataContext as FileItem;

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _desktopIntegration.EmbedInDesktop(hwnd);

        // release-hardening F (ADR-037, plan §3.2): reports the fact of this panel's root window, so the
        // desktop-scale provider determines the layer mode (Embedded/TopLevel) from reality rather than
        // predicting it — and rebuilds the snapshot in case the prediction (or a stale snapshot from
        // before any panel existed) was wrong.
        _desktopScale.ReportPanelWindow(hwnd);

        // The panel's real rendering DPI is known from here on (after embedding): item icons are requested
        // at IconSize x this scale (see ChildWindowViewModel.IconRenderScale).
        _viewModel.IconRenderScale = PanelWindowCoordinates.GetWindowScale(this, _desktopScale.Current);

        // ADR-022: the native WM_SIZING hook is installed here — after EmbedInDesktop, once the panel's HWND
        // is both valid and already reparented into WorkerW — even though the controller itself was already
        // created in the constructor (see its remarks).
        _resizeController.AttachTo(hwnd);
    }

    /// <summary>
    /// Handles a real per-monitor DPI change of this window (release-hardening F §4.1/§5 #14, ADR-037):
    /// refreshes the desktop-scale snapshot, then re-applies the model's desktop-DIP position through the
    /// adapter exactly once — <c>Window.Left</c>/<c>Top</c> is interpreted by WPF against this window's
    /// CURRENT monitor DPI, so the value <see cref="Initialize"/> (or the last drag/resize frame) wrote
    /// under the OLD DPI can be off until this runs.
    /// </summary>
    /// <remarks>
    /// M3 (release-hardening 1.0 review): the re-apply below is DEFERRED to a
    /// <see cref="Dispatcher.BeginInvoke(DispatcherPriority, System.Delegate)"/> at
    /// <see cref="DispatcherPriority.Background"/> — never done synchronously, from inside this override
    /// itself, the way an earlier version did. The base <c>OnDpiChanged</c> implementation rescales
    /// <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> for the new DPI (and WM_DPICHANGED processing can
    /// still have further internal Left/Top adjustment queued after this override returns) — a
    /// synchronous re-apply here raced that, and the window's own
    /// <see cref="OnLocationChanged"/> override fires synchronously as a side effect of EVERY one of
    /// those Left/Top writes, TRANSIENT ones included, writing a position that was neither the
    /// pre-DPI-change one nor the final, adapter-resolved one into <c>ChildWindowModel.Position</c>
    /// (baseline bug, no loop guard). <see cref="_dpiTransition"/> is set for the whole span from here
    /// until the deferred callback actually runs — well past every transient Left/Top nudge WPF's own
    /// machinery still has queued — and <see cref="OnLocationChanged"/> skips its own model write for as
    /// long as it is set; the deferred callback then re-applies the position exactly once, letting that
    /// one (correct, final) <see cref="OnLocationChanged"/> write through normally.
    /// </remarks>
    /// <param name="oldDpiScale">The window's previous DPI scale. Unused — the model's desktop-DIP position is re-derived fresh, not incrementally adjusted.</param>
    /// <param name="newDpiScale">
    /// The window's new DPI scale, per WPF's own <c>WM_DPICHANGED</c> plumbing. Not used for the position
    /// (same reason as above), and, per MINOR-6 (release-polish 1.0 review), not used for
    /// <see cref="ChildWindowViewModel.IconRenderScale"/> either — <see cref="PanelWindowCoordinates.GetWindowScale"/>
    /// (below) is used there instead, the SAME single DPI source <see cref="Initialize"/>/
    /// <see cref="OnSourceInitialized"/> already push, so <see cref="ChildWindowViewModel.IconRenderScale"/>
    /// can never disagree with itself across those three call sites and trigger a spurious extra icon
    /// re-fetch for two values that are, in every normal case, identical anyway.
    /// </param>
    protected override void OnDpiChanged(DpiScale oldDpiScale, DpiScale newDpiScale)
    {
        _dpiTransition.Set();

        base.OnDpiChanged(oldDpiScale, newDpiScale);

        _desktopScale.Refresh();

        // Crisp icons on the new monitor: re-requests item icons only if the snapped pixel size changed
        // (and even then, debounced — see ChildWindowViewModel.ScheduleIconRefetch, M3). Single DPI
        // source (MINOR-6): PanelWindowCoordinates.GetWindowScale, same as Initialize/OnSourceInitialized.
        _viewModel.IconRenderScale = PanelWindowCoordinates.GetWindowScale(this, _desktopScale.Current);

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // Cleared first, unconditionally: even when the guards below skip the actual re-apply (the
            // model is gone, a gesture/geometry write started while this was queued, …), the DPI
            // transition itself is over by the time this deferred callback runs at all — OnLocationChanged
            // must resume its normal model write for whatever comes next (e.g. a real drag the user
            // started in the meantime).
            _dpiTransition.Clear();

            // Skipped entirely while a gesture or PanelGeometryWriter write is already in flight (plan
            // §4.1's "no petли за один цикл" guard) — that write already reflects the new DPI by
            // construction, and reapplying here would fight it.
            if (_model is null || _geometryWriter.IsApplying) return;
            if (_interactionTracker.State != PanelInteractionState.Idle) return;

            var position = PanelWindowCoordinates.ToWindowPosition(
                this, new System.Windows.Point(_model.Position.X, _model.Position.Y), _desktopScale.Current);
            Left = position.X;
            Top = position.Y;
        });
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        _diagnostics.PanelClosing(_model?.Id, _interactionTracker.State == PanelInteractionState.Resizing);

        // ADR-021 "window disposal forces state to Idle": end whatever resize gesture might still be
        // active before the window goes away ("diagnostics call first, behavior second" — plan §3.5).
        _resizeController.AbortResize(ResizeEndReason.Closing);

        // R-10: deterministically remove the native WM_SIZING hook right next to the AbortResize call
        // above — idempotent, and a no-op when AttachTo was never reached.
        _resizeController.Dispose();

        if (_model is not null)
            _model.PropertyChanged -= OnModelPropertyChanged;

        // P06 hook (docs/fix-prompts/06-fit-content.md) — see ChildWindow.ContentSizing.cs.
        DetachContentSizing();

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        IsVisibleChanged -= OnIsVisibleChanged;
        _layoutEditMode.ModeChanged -= OnLayoutModeChanged;
        _layoutEditMode.PropertyChanged -= OnLayoutEditModePropertyChanged;
        _panelVisualState.Dispose();
        _layoutEditModeVisual.Dispose();
        _hintPopups.StopTimers();

        // ADR-036 §E / M1 (release-hardening 1.0 review): cancels every still-pending MovedOutVerifier
        // re-check — across every drag-out gesture this panel has scheduled, not just the most recent
        // one (see MovedOutVerifier.CancelAll's own remarks) — so a delayed callback never fires against
        // this panel's ViewModel after it's about to be disposed below.
        _movedOutVerifier.CancelAll();

        SyncToModel();
        if (DataContext is IDisposable disposable) disposable.Dispose();
        base.OnClosing(e);
    }

    /// <inheritdoc/>
    protected override void OnDragEnter(DragEventArgs e)
    {
        // Folder Portals are read-only — reject drag-drop so the user gets visual feedback.
        if (_viewModel.IsPortal)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? ExternalDropEffect
            : DragDropEffects.None;
    }

    /// <inheritdoc/>
    protected override void OnDragOver(DragEventArgs e)
    {
        // Same rule as OnDragEnter, applied continuously: without it the OLE default (usually Move) applies
        // while hovering, so in a read-only session the cursor would promise a Move that never happens (F7).
        // FileListHost_DragOver handles in-panel reorder drags first (and marks them handled), so this only
        // sees the rest.
        if (_viewModel.IsPortal)
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? ExternalDropEffect
                : DragDropEffects.None;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The effect an external file drop reports: Move normally (the dropped items go into app-managed
    /// storage), but Copy in a read-only session (ADR-audit WP-B B1) — nothing is moved there, and a Move
    /// effect would let the drag source (Explorer) delete the originals after the drop.
    /// </summary>
    private DragDropEffects ExternalDropEffect
        => _viewModel.MovesDroppedItemsIntoStorage ? DragDropEffects.Move : DragDropEffects.Copy;

    /// <inheritdoc/>
    protected override void OnDrop(DragEventArgs e)
    {
        // Folder Portals are read-only — ignore drops.
        if (_viewModel.IsPortal) return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        // Mirror OnDragEnter's effect here too: some drag sources (e.g. Explorer) inspect the final
        // e.Effects after the drop completes to decide their own post-drop behavior (cursor feedback,
        // whether to show a "move" vs "copy" animation). AddDroppedFiles always moves, so make that
        // explicit rather than relying on whatever effect happened to still be set from drag-over
        // (except in a read-only session, where AddDroppedFiles moves nothing — see ExternalDropEffect).
        e.Effects = ExternalDropEffect;

        // ADR-036 §E: an in-app panel-item drag-out (see PanelItemDragDataFactory/CreatePanelItemDragData)
        // carries BOTH this panel's own drag-to-reorder format AND DataFormats.FileDrop — a drop that
        // lands on FileListHost's own items/empty area never reaches here at all: FileListHost_Drop routes
        // it to _reorderController first (see its isForeignPanelDrag wiring), which — for a same-panel
        // drop — calls PerformReorder, and IT (M1: not the reorder controller itself) is what claims the
        // drag via _dragOutSession.TryClaim. This method only runs for a drop elsewhere in the window: a
        // DIFFERENT panel entirely, or THIS panel's own drag landing outside FileListHost (e.g. its title
        // bar) — the TryClaim below is this panel's one remaining path to claiming ITS OWN drag (a
        // same-panel drop that missed FileListHost, e.g. dropped back on its own title bar).
        if (files.Length == 1 && _dragOutSession.IsActive)
        {
            if (_model is not null && _dragOutSession.TryClaim(_model.Id, files[0]))
            {
                // A genuine cross-panel transfer: the managed copy (if any) is shared storage, so this
                // only adds a reference — never MoveIn's the already-managed path again.
                _viewModel.AddTransferredItem(files[0]);
                _windowManager.SaveWindowsState();
            }

            // Either claimed as a transfer (handled above) or a self-claim that resolves to Reordered
            // (TryClaim's own remarks) — either way, an active in-app drag-out never falls through to
            // AddDroppedFiles, which would otherwise try to MoveIn a path that's already managed.
            return;
        }

        _viewModel.AddDroppedFiles(files);
        _windowManager.SaveWindowsState();
    }

    /// <inheritdoc/>
    protected override void OnLocationChanged(EventArgs e)
    {
        // Stage S guard (docs/PLANS/workspace-grid-03-04-integration.md §1.5): a PanelGeometryWriter.Apply
        // call already wrote model.Position directly — this handler firing synchronously as a side
        // effect of that same Left/Top assignment must not write it a second, redundant time.
        //
        // M3 (release-hardening 1.0 review): also skipped for the whole span of an in-flight per-monitor
        // DPI change (see OnDpiChanged's own remarks) — a Left/Top value WPF's own DPI rescale writes
        // mid-transition is transient, not a real move, and must never reach ChildWindowModel.Position.
        if (_model is null || _geometryWriter.IsApplying || _dpiTransition.IsSet) return;

        // release-hardening F (ADR-037): Left/Top through the adapter — desktop-DIP, not necessarily this
        // window's own space on a mixed-DPI TopLevel desktop. (int)-truncation is unchanged (pre-F
        // contract, see the finalization remarks elsewhere in this codebase on Math.Round vs (int)).
        var desktopPosition = PanelWindowCoordinates.ToDesktopBounds(this, new Rect(Left, Top, 0, 0), _desktopScale.Current).TopLeft;
        _model.Position = new Point((int)desktopPosition.X, (int)desktopPosition.Y);
    }

    /// <inheritdoc/>
    protected override void OnStateChanged(EventArgs e)
    {
        if (_model is null) return;
        _model.WindowState = WindowState;
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        // While rolled up, keep the persisted size at the expanded dimensions for restore. Stage S guard
        // (docs/PLANS/workspace-grid-03-04-integration.md §1.5): a PanelGeometryWriter.Apply call already
        // wrote model.Size directly — skip the redundant write this handler would otherwise perform as a
        // synchronous side effect of that same Width/Height assignment (baseline risk 17).
        if (_model is null || _model.IsRolledUp || _geometryWriter.IsApplying) return;
        _model.Size = new Size((int)Width, (int)Height);
    }

    /// <summary>
    /// Shows the non-modal, auto-dismissing rename-error hint popup for the failure reason, the
    /// moment <see cref="ChildWindowViewModel.RenameError"/> is set to a non-null value.
    /// </summary>
    /// <param name="sender">Unused; present to match the <see cref="INotifyPropertyChanged.PropertyChanged"/> handler signature.</param>
    /// <param name="e">The property-changed event; only <see cref="ChildWindowViewModel.RenameError"/> is acted on.</param>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChildWindowViewModel.RenameError)) return;
        if (_viewModel.RenameError is not { } outcome) return;

        _hintPopups.ShowRenameError(outcome);
    }

    /// <summary>
    /// Keeps <see cref="ChildWindowViewModel.SetIconLoadVisibility"/> in sync with this window's actual
    /// on-screen visibility (ADR-046 D-6) — <c>WindowManager</c> hides every panel not on the current page
    /// (and shows those that are) by toggling <see cref="UIElement.Visibility"/> directly, without going
    /// back through <see cref="ChildWindowViewModel.LoadFromModel"/>.
    /// </summary>
    /// <param name="sender">Unused; present to match the <see cref="DependencyPropertyChangedEventHandler"/> signature.</param>
    /// <param name="e">The visibility-changed event; <see cref="DependencyPropertyChangedEventArgs.NewValue"/> is the new <see cref="bool"/> visibility.</param>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _viewModel.SetIconLoadVisibility((bool)e.NewValue);

    /// <summary>
    /// Handles the global inline-rename shortcut: <c>F2</c> begins renaming the currently selected
    /// file item (Feature B, DoD 8), or — when nothing is selected — the panel's own title (same action
    /// as <c>HeaderContextMenu</c>'s "Rename" item, which shows this same <c>F2</c> gesture hint;
    /// design-system-1x.md §2.6 p.3). No-op if the panel title is already being renamed
    /// (<see cref="PanelTitleRenameBehavior.IsEditingTitle"/>) — F2 should not fight the title editor for
    /// focus — or a file item rename is already in progress (repeated F2 would otherwise re-run
    /// <see cref="ChildWindowViewModel.BeginRenameCommand"/> and overwrite the text the user already
    /// typed with <see cref="FileItem.DisplayName"/> again).
    /// <para>
    /// Also handles <c>Enter</c>: opens the selected file item (the same action as a double-click,
    /// <see cref="FileList_MouseDoubleClick"/>), through <see cref="ChildWindowViewModel.OpenSelectedFileCommand"/>.
    /// Deliberately skipped while either editor is open — the window's tunnelling Preview event fires
    /// BEFORE the title/rename <see cref="TextBox"/>es' own <c>PreviewKeyDown</c>, which is where their
    /// Enter means "commit" — and whenever the key originates in any text input, so a future editor
    /// cannot regress into "Enter launches the file mid-edit".
    /// </para>
    /// <para>
    /// Also handles <c>Escape</c>: exits <see cref="ILayoutEditModeService"/>'s Edit Layout mode
    /// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #4). Best-effort only — this panel carries
    /// <c>WS_EX_NOACTIVATE</c>; keyboard input reaching it was verified live for F2/inline-rename
    /// (ADR-013 R1, Windows 11, 2026-09-27) — the tray icon and this panel's own
    /// <c>HeaderContextMenu</c> "Edit layout" item remain the guaranteed exit paths; <c>Escape</c> is
    /// never the sole way out (R-6).
    /// </para>
    /// </summary>
    private void ChildWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_titleRename.IsEditingTitle) return;
            if (e.OriginalSource is TextBoxBase) return;
            if (!_viewModel.OpenSelectedFileCommand.CanExecute(null)) return;

            _viewModel.OpenSelectedFileCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            if (_titleRename.IsEditingTitle) return;
            if (_viewModel.EditingItem is not null) return;

            if (_viewModel.SelectedFile is null)
            {
                // Same call RenameMenu_Click makes — nothing selected inside the panel, so F2 (like the
                // menu's own "Rename" item) targets the panel's own title instead of a file item.
                _titleRename.BeginRename();
                _panelVisualState.Refresh();
            }
            else
            {
                _viewModel.BeginRenameCommand.Execute(_viewModel.SelectedFile);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _layoutEditMode.IsEditing)
        {
            _layoutEditMode.ExitEditLayout(LayoutModeChangeSource.Keyboard);
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragMoveBehavior.OnTitleBarMouseLeftButtonDown(sender, e);

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        => _dragMoveBehavior.OnTitleBarMouseMove(sender, e);

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _dragMoveBehavior.OnTitleBarMouseLeftButtonUp(sender, e);

    private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
        => _dragMoveBehavior.OnTitleBarLostMouseCapture(sender, e);

    private void RollUpMenu_Click(object sender, RoutedEventArgs e) => _rollUpBehavior.ToggleRollUp();

    /// <summary>
    /// <c>HeaderContextMenu</c>'s "Edit layout" item (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #2) —
    /// toggles the app-global <see cref="ILayoutEditModeService"/>. A no-op when
    /// <see cref="ILayoutEditModeService.IsLayoutLocked"/> is <see langword="true"/> (the item is also
    /// disabled then, via the <c>CanEnterEditLayout</c> binding in <c>ChildWindow.xaml</c>).
    /// </summary>
    private void EditLayoutMenu_Click(object sender, RoutedEventArgs e) =>
        _layoutEditMode.Toggle(LayoutModeChangeSource.PanelMenu);

    /// <summary>
    /// <c>HeaderContextMenu</c>'s "Lock layout" item (D-9 #2/D-3). Locking while
    /// <see cref="ILayoutEditModeService.Mode"/> is <see cref="LayoutInteractionMode.EditLayout"/> forces
    /// an immediate exit back to <see cref="LayoutInteractionMode.Normal"/> — handled inside the service
    /// itself (see its <c>IsLayoutLocked</c> setter remarks), not here.
    /// </summary>
    private void LockLayoutMenu_Click(object sender, RoutedEventArgs e) =>
        _layoutEditMode.IsLayoutLocked = !_layoutEditMode.IsLayoutLocked;

    /// <summary>
    /// Opens the header overflow menu (§5.4, docs/PLANS/phase-5-ambient-accent-ui.md C6). Reuses the
    /// <b>same</b> <see cref="ContextMenu"/> instance (<c>HeaderContextMenu</c>, declared once in
    /// <c>ChildWindow.xaml</c>) the title bar's right-click already opens successfully — never
    /// constructs a new <see cref="ContextMenu"/> (R-7): this panel carries <c>WS_EX_NOACTIVATE</c>
    /// (ADR-013 R1), so a brand-new input surface would be an unproven risk with no upside over reusing
    /// one already known to receive input correctly on this window. Anchors the menu under the button
    /// that opened it, rather than <see cref="PlacementMode.MousePoint"/> (the right-click path's
    /// effective default) — <see cref="HeaderContextMenu_Closed"/> restores that default afterward so a
    /// subsequent right-click is unaffected by this button-anchored placement.
    /// </summary>
    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        HeaderContextMenu.PlacementTarget = button;
        HeaderContextMenu.Placement = PlacementMode.Bottom;
        HeaderContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Restores <see cref="HeaderContextMenu"/>'s <see cref="ContextMenu.PlacementTarget"/>/
    /// <see cref="ContextMenu.Placement"/> to "unset" after every close, regardless of which path opened
    /// it. <see cref="OverflowButton_Click"/> sets both as local values so ClearValue is required (a
    /// plain re-assignment to null/default would itself count as a new local value); with them cleared,
    /// the next right-click invocation goes back through <c>ContextMenuService</c>'s own default
    /// (<see cref="PlacementMode.MousePoint"/>, anchored at the cursor) instead of silently inheriting
    /// the button-anchored placement <see cref="OverflowButton_Click"/> left behind.
    /// </summary>
    private void HeaderContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        HeaderContextMenu.ClearValue(ContextMenu.PlacementTargetProperty);
        HeaderContextMenu.ClearValue(ContextMenu.PlacementProperty);
    }

    /// <summary>
    /// Begins the title rename, then re-resolves <see cref="PanelVisualStateBehavior"/>'s state: unlike
    /// <see cref="_interactionTracker"/>'s gesture state or PanelRoot's own hover, "is the title being
    /// edited" has no change notification of its own to subscribe to (<see cref="PanelTitleRenameBehavior"/>
    /// is untouched by this phase — see its class remarks) — so every call site that changes it explicitly
    /// nudges the visual state afterward instead.
    /// </summary>
    private void RenameMenu_Click(object sender, RoutedEventArgs e)
    {
        _titleRename.BeginRename();
        _panelVisualState.Refresh();
    }

    /// <summary>
    /// <c>HeaderContextMenu</c>'s "Customize panel…" item (docs/prompts/settings-window-redesign.md
    /// D-3/WP-B): opens the modeless dialog for this panel's <see cref="ChildWindowModel"/>. Always
    /// enabled — every field the dialog edits already applies live regardless of Edit Layout mode.
    /// </summary>
    private void CustomizePanelMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        _panelCustomize.Show(_model);
    }

    /// <summary>
    /// Clears this panel's selection when any header button (Sort/View/RollUp, and — since C6 — Overflow)
    /// is clicked (ui-polishing-1 §2.4 Block A1) — a single handler on the buttons' shared parent
    /// <c>StackPanel</c>, rather than one per button. Runs on the tunneling Preview pass and
    /// deliberately never sets <c>e.Handled</c>, so each button's own <c>Command</c>/<c>Click</c> still
    /// fires normally afterwards on the bubbling pass — clearing the selection must never prevent the
    /// button's own action from executing.
    /// </summary>
    private void HeaderButtons_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // See TitleBar_MouseLeftButtonDown's remarks — same WS_EX_NOACTIVATE gap, same fix
        // (finalization MAJOR M-2).
        CommitActiveItemRenameIfAny();
        _viewModel.SelectedFile = null;
    }

    /// <summary>
    /// Shared "a new panel gesture is starting" hook used by both <see cref="PanelDragMoveBehavior"/>
    /// (title-bar press) and <see cref="NativePanelResizeController"/> (resize-gesture start): commits any
    /// in-progress file-item inline rename and clears this panel's own selection, since starting either
    /// gesture is not clicking a file item (ui-polishing-1 §2.4 Block A1). An active inline rename never
    /// sees the originating click as a <c>LostKeyboardFocus</c> (the panel carries <c>WS_EX_NOACTIVATE</c>)
    /// — commit it explicitly here instead (finalization MAJOR M-2).
    /// </summary>
    private void OnPanelGestureStarting()
    {
        CommitActiveItemRenameIfAny();
        _viewModel.SelectedFile = null;
    }

    /// <summary>
    /// Commits the current file-item inline rename, if one is in progress. Used by
    /// <see cref="OnPanelGestureStarting"/> and <see cref="HeaderButtons_PreviewMouseLeftButtonDown"/>,
    /// and by <see cref="ChildWindowViewModel.OnSelectionClaimed"/>'s cross-panel counterpart, all of
    /// which react to a gesture that a normal <c>LostKeyboardFocus</c> would otherwise have committed,
    /// were this window not <c>WS_EX_NOACTIVATE</c> (finalization MAJOR M-2). Mirrors
    /// <see cref="RenameEditBehavior"/>'s own "commit, not cancel" semantics for click-away — never
    /// calls <see cref="ChildWindowViewModel.CancelRenameCommand"/>. A no-op when nothing is being
    /// edited: <see cref="ChildWindowViewModel.CommitRenameCommand"/> itself already no-ops for that
    /// case, but checking here first avoids the command dispatch entirely on the (overwhelmingly
    /// common) path where no rename is in progress.
    /// </summary>
    private void CommitActiveItemRenameIfAny()
    {
        if (_viewModel.EditingItem is null) return;
        _viewModel.CommitRenameCommand.Execute(null);
    }

    private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        _titleRename.OnTitleEditBoxKeyDown(sender, e);
        _panelVisualState.Refresh();
    }

    private void TitleEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _titleRename.OnTitleEditBoxLostKeyboardFocus(sender, e);
        _panelVisualState.Refresh();
    }

    /// <summary>
    /// ADR-021 "window disposal forces state to Idle": ends any resize gesture still active when this
    /// panel unloads, and forces the shared <see cref="PanelInteractionTracker"/> back to
    /// <see cref="PanelInteractionState.Idle"/> as a final backstop (in case some future gesture type
    /// besides resize/drag ever fails to release it itself).
    /// </summary>
    private void ChildWindow_Unloaded(object sender, RoutedEventArgs e)
    {
        _resizeController.AbortResize(ResizeEndReason.Unloaded);
        _interactionTracker.ForceIdle();
    }

    /// <summary>
    /// Opens the double-clicked file. The original source's DataContext is the <see cref="FileItem"/>
    /// because the item templates inherit it. Guarded against an in-progress inline rename: the
    /// <c>Control.MouseDoubleClick</c> bubble still fires even when the second click landed on the
    /// active rename <see cref="TextBox"/> (word-select mouse-down there doesn't stop the event), and
    /// the TextBox shares its <see cref="FileItem"/> DataContext with the rest of the item — without
    /// this guard, double-clicking to select a word while renaming would open the file mid-edit.
    /// </summary>
    private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_viewModel.EditingItem is not null) return;
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItem item) return;

        _viewModel.OpenFile(item);
    }

    private void FileListHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _reorderController.OnPreviewMouseLeftButtonDown(sender, e);

    /// <summary>
    /// Clears a drag-to-reorder candidate left over from a plain click that never moved far enough to
    /// start a drag (MAJOR-1, panel-item-sorting review) — see
    /// <see cref="ListReorderGestureController{TItem}.OnPreviewMouseLeftButtonUp"/>.
    /// </summary>
    private void FileListHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _reorderController.OnPreviewMouseLeftButtonUp(sender, e);

    private void FileListHost_PreviewMouseMove(object sender, MouseEventArgs e)
        => _reorderController.OnPreviewMouseMove(sender, e);

    private void FileListHost_DragOver(object sender, DragEventArgs e)
        => _reorderController.OnDragOver(e);

    private void FileListHost_DragLeave(object sender, DragEventArgs e)
        => _reorderController.OnDragLeave(e);

    private void FileListHost_Drop(object sender, DragEventArgs e)
        => _reorderController.OnDrop(e);

    /// <summary>
    /// Resolves the press-time drag-to-reorder candidate for <see cref="_reorderController"/>
    /// (ADR-014 Block C2). No candidate — and therefore no drag ever starts — while another item is mid
    /// inline-rename, or the panel is a read-only Folder Portal (<see cref="ChildWindowViewModel.ReorderItem"/>
    /// is a no-op there anyway; rejecting here just avoids starting a doomed gesture). Also clears this
    /// panel's own selection when the click landed on empty space (not an item) — deliberately does not
    /// set <c>e.Handled</c> anywhere in this path — doing so would break the <c>ListBox</c>'s own
    /// built-in selection handling.
    /// </summary>
    private FileItem? ResolveReorderDragCandidate(DependencyObject source)
    {
        if (FindAncestorOrSelf<TextBox>(source) is not null) return null; // active inline-rename editor: let the click select text, never start a drag.

        if (FindAncestorOrSelf<ListBoxItem>(source) is null)
            _viewModel.SelectedFile = null;

        return _viewModel.EditingItem is null && !_viewModel.IsPortal
            ? FindAncestorOrSelf<ListBoxItem>(source)?.DataContext as FileItem
            : null;
    }

    /// <summary>
    /// Shared accept/reject gate for <see cref="FileListHost_DragOver"/> and <see cref="FileListHost_Drop"/>
    /// (ADR-014 Block C3, via <see cref="_reorderController"/>). Resolves the dragged item's path (the
    /// <see cref="DataObject"/> payload — see <see cref="PanelItemReorderFormat"/>) back to a live
    /// <see cref="FileItem"/> by looking it up in this panel's own currently-visible
    /// <see cref="BaseViewModel.FileItems"/>.
    /// </summary>
    private FileItem? TryResolveReorderItem(DragEventArgs e)
    {
        if (_viewModel.IsPortal) return null;
        if (!e.Data.GetDataPresent(PanelItemReorderFormat)) return null;
        if (e.Data.GetData(PanelItemReorderFormat) is not string path) return null;

        return _viewModel.FileItems.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves a drop position (from <see cref="FileListHost_Drop"/>) to a visible-index insertion
    /// gap in <c>[0, FileItems.Count]</c> (ADR-014 Block C4), by hit-testing the live <see cref="ListBox"/>
    /// hosted inside <see cref="FileListHost"/>. Works unchanged for both the List and Grid
    /// <c>DataTemplate</c>s in <c>Templates.xaml</c> — walking up from the hit-tested visual to the
    /// nearest <see cref="ListBoxItem"/> ancestor doesn't depend on the <c>ItemsPanel</c> type
    /// (<c>StackPanel</c> vs <c>WrapPanel</c> vs <see cref="DesktopOrganizer.UI.Controls.AdaptiveIconGridPanel"/>)
    /// and survives virtualization, unlike walking <see cref="ItemsControl.Items"/>/children directly.
    /// </summary>
    private int ComputeDropInsertionIndex(DragEventArgs e)
    {
        var itemCount = _viewModel.FileItems.Count;

        int? containerIndex = null;
        var positionInContainer = default(System.Windows.Point);
        var containerSize = default(System.Windows.Size);

        if (FindVisualChild<ListBox>(FileListHost) is { } listBox &&
            ListReorderGeometry.HitTestReorderContainer(listBox, e) is { } container)
        {
            var index = listBox.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0)
            {
                containerIndex = index;
                positionInContainer = e.GetPosition(container);
                containerSize = new System.Windows.Size(container.ActualWidth, container.ActualHeight);
            }
        }

        return ListReorderGeometry.ComputeInsertionGap(containerIndex, positionInContainer, containerSize, _viewModel.Model.ViewMode, itemCount);
    }

    /// <summary>
    /// Shows and positions <see cref="ReorderInsertionIndicator"/> for the current drag-over position
    /// (ADR-014 Block C5). A thin line — vertical between columns in <see cref="PanelViewMode.Grid"/>,
    /// horizontal between rows in <see cref="PanelViewMode.List"/> — anchored via <see cref="FrameworkElement.Margin"/>
    /// relative to the outer <see cref="Grid"/> both it and <see cref="FileListHost"/> live in
    /// (mirrors <c>SystemIconsPanelWindow</c>'s <c>InsertionIndicator</c> positioning pattern). When
    /// the cursor is past the last item (no hit-tested container), the indicator anchors to
    /// <see cref="FileListHost"/>'s own trailing edge instead of hiding — dropping at the end of the
    /// list is still a valid, visible target.
    /// </summary>
    private void ShowReorderInsertionIndicator(DragEventArgs e)
    {
        if (ReorderInsertionIndicator.Parent is not UIElement rootGrid)
        {
            HideReorderInsertionIndicator();
            return;
        }

        if (FindVisualChild<ListBox>(FileListHost) is not { } listBox)
        {
            HideReorderInsertionIndicator();
            return;
        }

        var isGrid = _viewModel.Model.ViewMode == PanelViewMode.Grid;
        var container = ListReorderGeometry.HitTestReorderContainer(listBox, e);

        if (container is null)
        {
            var hostOrigin = FileListHost.TranslatePoint(new System.Windows.Point(0, 0), rootGrid);

            if (isGrid)
            {
                // Cursor is over empty space past the last item — the drop always lands at the end of
                // the list (see ComputeInsertionGap: containerIndex == null -> itemCount). The exact
                // pixel position of "right after the last wrapped item" isn't cheaply available here
                // (it depends on how many items fit per row, which this method doesn't compute), so
                // this deliberately picks the simplest position that is not actively wrong: the
                // trailing (right) edge of FileListHost, rather than its leading (left) edge. Anchoring
                // at the left edge — as a prior version of this code did — visually implied the drop
                // would insert the item first, which contradicts where it actually lands (MAJOR-5,
                // panel-item-sorting review). The List-mode branch below already gets this exactly
                // right (bottom edge) because rows are the full host width, so there's no equivalent
                // ambiguity to resolve there.
                ReorderInsertionIndicator.Width = 2;
                ReorderInsertionIndicator.Height = FileListHost.ActualHeight;
                ReorderInsertionIndicator.Margin = new Thickness(hostOrigin.X + Math.Max(0, FileListHost.ActualWidth - 2), hostOrigin.Y, 0, 0);
            }
            else
            {
                ReorderInsertionIndicator.Width = FileListHost.ActualWidth;
                ReorderInsertionIndicator.Height = 2;
                ReorderInsertionIndicator.Margin = new Thickness(hostOrigin.X, hostOrigin.Y + Math.Max(0, FileListHost.ActualHeight - 2), 0, 0);
            }

            ReorderInsertionIndicator.Visibility = Visibility.Visible;
            return;
        }

        var containerOrigin = container.TranslatePoint(new System.Windows.Point(0, 0), rootGrid);
        var positionInContainer = e.GetPosition(container);

        if (isGrid)
        {
            var beforeMidpoint = positionInContainer.X < container.ActualWidth / 2.0;
            var lineX = beforeMidpoint ? containerOrigin.X : containerOrigin.X + container.ActualWidth;

            ReorderInsertionIndicator.Width = 2;
            ReorderInsertionIndicator.Height = container.ActualHeight;
            ReorderInsertionIndicator.Margin = new Thickness(lineX - 1, containerOrigin.Y, 0, 0);
        }
        else
        {
            var beforeMidpoint = positionInContainer.Y < container.ActualHeight / 2.0;
            var lineY = beforeMidpoint ? containerOrigin.Y : containerOrigin.Y + container.ActualHeight;

            ReorderInsertionIndicator.Width = FileListHost.ActualWidth;
            ReorderInsertionIndicator.Height = 2;
            ReorderInsertionIndicator.Margin = new Thickness(containerOrigin.X, lineY - 1, 0, 0);
        }

        ReorderInsertionIndicator.Visibility = Visibility.Visible;
    }

    private void HideReorderInsertionIndicator() => ReorderInsertionIndicator.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Applies a same-panel drag-to-reorder drop (ADR-014, <see cref="ListReorderGestureController{TItem}"/>'s
    /// <c>performReorder</c> callback). M1 (release-hardening 1.0 review): this is the ONLY place a
    /// same-panel reorder ever runs, so it must be the one to claim <see cref="_dragOutSession"/> on this
    /// panel's own behalf — before this fix, a plain in-panel reorder never called
    /// <see cref="IPanelItemDragOutSession.TryClaim"/> at all, so <see cref="OnPanelDragCompleted"/>'s
    /// later <see cref="IPanelItemDragOutSession.End"/> always resolved to
    /// <see cref="DragOutOutcomeKind.External"/> (nothing claimed the drag) instead of
    /// <see cref="DragOutOutcomeKind.Reordered"/> — starting a needless <see cref="_movedOutVerifier"/>
    /// existence-recheck for an item that never left this panel at all, on EVERY reorder. Claiming with
    /// this panel's own <c>Id</c> as <c>targetPanelId</c> is a self-claim (see <c>TryClaim</c>'s own
    /// remarks): it is deliberately never treated as an accepted transfer, but still resolves
    /// <see cref="IPanelItemDragOutSession.End"/> to <see cref="DragOutOutcomeKind.Reordered"/>.
    /// </summary>
    /// <param name="item">The dragged item, as resolved by <see cref="ResolveReorderDragCandidate"/>.</param>
    /// <param name="index">The target insertion index, as resolved by <see cref="ComputeDropInsertionIndex"/>.</param>
    private void PerformReorder(FileItem item, int index)
    {
        if (_model is not null) _dragOutSession.TryClaim(_model.Id, item.Path);
        _viewModel.ReorderItem(item, index);
    }

    /// <summary>
    /// Builds the drag-out payload for <paramref name="item"/> (ADR-036 §E) and starts
    /// <see cref="_dragOutSession"/> tracking it as this panel's own — a target panel (or
    /// Explorer/the desktop) decides the rest via <see cref="IPanelItemDragOutSession.TryClaim"/>.
    /// Returns <see langword="null"/> (aborting the drag before it starts — see
    /// <see cref="ListReorderGestureController{TItem}"/>'s own remarks) for a Folder Portal, mirroring
    /// <see cref="ResolveReorderDragCandidate"/>'s own portal gate (belt-and-suspenders: a portal never
    /// produces a candidate in the first place, so this is never actually reached for one).
    /// </summary>
    /// <param name="item">The drag candidate resolved by <see cref="ResolveReorderDragCandidate"/>.</param>
    private DataObject? CreatePanelItemDragData(FileItem item)
    {
        if (_model is null) return null;

        var data = PanelItemDragDataFactory.Create(PanelItemReorderFormat, item.Path, _viewModel.IsPortal);
        if (data is null) return null;

        _dragOutSession.Begin(_model.Id, item.Path);
        return data;
    }

    /// <summary>
    /// Resolves how <paramref name="item"/>'s drag-out gesture ended (ADR-036 §E), once
    /// <see cref="DragDrop.DoDragDrop"/> has fully returned, and applies the corresponding source-side
    /// cleanup: nothing for a same-panel <see cref="DragOutOutcomeKind.Reordered"/>, an immediate
    /// reference-only removal for <see cref="DragOutOutcomeKind.TransferredTo"/> a different panel, or
    /// a delayed existence-recheck (<see cref="_movedOutVerifier"/>) for
    /// <see cref="DragOutOutcomeKind.External"/> — since the OLE drop effect itself cannot be trusted
    /// (Explorer's own optimized move frequently reports <see cref="DragDropEffects.None"/> on success).
    /// </summary>
    /// <param name="item">The item whose drag-out gesture just finished.</param>
    private void OnPanelDragCompleted(FileItem item)
    {
        var outcome = _dragOutSession.End();
        switch (outcome.Kind)
        {
            case DragOutOutcomeKind.TransferredTo:
                _viewModel.RemoveTransferredItem(item);
                _windowManager.SaveWindowsState();
                break;

            case DragOutOutcomeKind.External:
                _movedOutVerifier.Schedule(item.Path, () =>
                {
                    _viewModel.RemoveMovedOutItem(item);
                    _windowManager.SaveWindowsState();
                });
                break;

            case DragOutOutcomeKind.Reordered:
            default:
                break; // the drag never left this panel — nothing else to do.
        }
    }

    /// <summary>
    /// Explorer-like "right-click selects the item before the context menu opens" behavior (Feature A).
    /// Runs on the Preview (tunneling) pass so the selection is already updated on press, before
    /// <see cref="FileListHost_PreviewMouseRightButtonUp"/> opens the native shell menu on release.
    /// </summary>
    private void FileListHost_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        if (ResolveRightClickedFileItem(source) is { } item)
            _viewModel.SelectedFile = item;
    }

    /// <summary>
    /// Right-click release inside <see cref="FileListHost"/>. On a file item (list and grid templates
    /// alike — both host items in <see cref="ListBoxItem"/> containers): shows the native Windows shell
    /// context menu for the item's file at the cursor, exactly like Explorer — the panel has no WPF menu
    /// for items; marking the event handled keeps WPF's <c>ContextMenuService</c> from looking for one.
    /// On the EMPTY panel body (UX parity fix, fences-ux-parity-reviewer, release-polish 1.0 review) —
    /// list/grid whitespace inside <see cref="FileListHost"/>, not on an item — opens
    /// <c>HeaderContextMenu</c> instead, the same fence menu the title bar and
    /// <see cref="OverflowButton_Click"/> already open: Stardock Fences opens its fence menu anywhere on
    /// the fence background, not just its title bar.
    /// <para>
    /// Coordinates: <see cref="Visual.PointToScreen"/> converts this window's DIP position into physical
    /// screen pixels through the window's own per-monitor DPI transform — what <c>TrackPopupMenuEx</c> expects
    /// (same conversion the System Icons panel uses, release-hardening F / ADR-037).
    /// </para>
    /// <para>
    /// A right-click INSIDE the inline-rename editor (MINOR 5, release-polish 1.0 review) is left entirely to the
    /// <see cref="TextBox"/>: it must show its own Cut/Copy/Paste menu, not commit the rename and open the shell
    /// menu (or the fence menu) over the half-typed name.
    /// </para>
    /// <para>
    /// A right-click on the list/grid's own <see cref="ScrollBar"/> (MINOR 9, release-polish 1.0 final
    /// review) is likewise left alone — it lives inside <see cref="FileListHost"/> but is neither a file
    /// item nor the empty panel body, and popping the fence menu over it every time would be surprising.
    /// </para>
    /// </summary>
    private void FileListHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsInlineRenameEditorHit(source)) return; // the inline-rename editor's own edit menu.
        if (IsScrollBarHit(source)) return; // let the scrollbar show its own (default: no) menu.

        if (ResolveRightClickedFileItem(source) is { } item)
        {
            e.Handled = true;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Same reasoning as the other gesture entry points: a WS_EX_NOACTIVATE panel never sees the
            // click as a LostKeyboardFocus, so an in-progress inline rename is committed explicitly first.
            CommitActiveItemRenameIfAny();

            var screen = PointToScreen(e.GetPosition(this));
            _viewModel.ShowShellContextMenu(item, hwnd, (int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            return;
        }

        // UX parity fix: the release landed on the EMPTY panel body, not on an item — open the fence menu
        // there too. Reuses the EXACT SAME <c>HeaderContextMenu</c> instance <see cref="OverflowButton_Click"/>
        // reuses (R-7, ADR-013): this panel carries WS_EX_NOACTIVATE, so a freshly-constructed ContextMenu
        // would be an unproven input surface with no upside over reusing one already known to receive
        // input correctly on this window. Anchored at the cursor (PlacementMode.MousePoint) — the same
        // effective default the title bar's own right-click already gets via ContextMenuService — unlike
        // OverflowButton_Click's button-anchored placement; HeaderContextMenu_Closed restores the
        // "unset"/ContextMenuService default afterward regardless of which of the three entry points opened
        // it. Every item inside is the SAME MenuItem instance, with the SAME CanEnterEditLayout/
        // IsLayoutLocked-gated bindings as the title-bar and overflow-button entry points, so Edit
        // Layout/lock-state gating is respected automatically — nothing extra to wire here.
        CommitActiveItemRenameIfAny();

        e.Handled = true;
        HeaderContextMenu.PlacementTarget = FileListHost;
        HeaderContextMenu.Placement = PlacementMode.MousePoint;
        HeaderContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Re-snaps the panel's width whenever a property that feeds <see cref="GridMetrics"/>/
    /// <see cref="AdaptiveGridLayout"/>'s icon-grid math changes on a user-driven gesture (never on
    /// passive load — see <c>Initialize</c>'s own remarks and ADR-029's Q2 amendment). Fix-cycle 2
    /// MAJOR-2 (adr/ADR-029-icon-layout-policy.md "Fix-цикл P05"): <see cref="ChildWindowModel.IconLayoutPolicy"/>
    /// joins <see cref="ChildWindowModel.IconSize"/>/<see cref="ChildWindowModel.ViewMode"/> here —
    /// switching a live panel from <see cref="IconLayoutPolicy.LegacyStretch"/> to
    /// <see cref="IconLayoutPolicy.FixedGap"/> (or back) changes <see cref="GridMetrics.ContentHorizontalChrome"/>/
    /// <see cref="GridMetrics.MinPanelWidth(double, IconLayoutPolicy, double)"/> (the M-2 scrollbar
    /// reservation) for the SAME persisted window width, so the persisted width can silently stop
    /// matching what <see cref="Controls.AdaptiveIconGridPanel"/> actually renders (the exact "lost column" class
    /// of bug ADR-029's F-3 fix already closed once for <c>IconSize</c>/<c>ViewMode</c> changes) unless
    /// this handler re-snaps here too. <see cref="NativePanelResizeController.SnapWidthToGrid"/> itself already
    /// gates on Grid mode and <c>IAppSettings.SnapResizeToIconGrid</c> — no extra guard is needed here,
    /// mirroring exactly how the pre-existing <c>IconSize</c>/<c>ViewMode</c> triggers rely on that same
    /// internal gate rather than duplicating it.
    /// <para>
    /// <b>Known, pre-existing, unchanged scope gap (not introduced or fixed by this addition):</b>
    /// <see cref="NativePanelResizeController.SnapWidthToGrid"/> does not special-case
    /// <see cref="ChildWindowModel.GeometryMode"/> — a <see cref="PanelGeometryMode.GridAligned"/> panel's
    /// width is snapped to the icon-grid cell size here exactly as a <see cref="PanelGeometryMode.LegacyFreeform"/>
    /// one is, same as it already was for <c>IconSize</c>/<c>ViewMode</c> before this fix-cycle. Owning
    /// that interaction is ADR-028/the workspace-grid geometry package's territory, explicitly out of
    /// this fix-cycle's zone (P02 fix-2) — adding <c>IconLayoutPolicy</c> to this list keeps it perfectly
    /// consistent with the two triggers that were already here, not better or worse with respect to
    /// <c>GeometryMode</c>.
    /// </para>
    /// </summary>
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChildWindowModel.IconSize)
            or nameof(ChildWindowModel.ViewMode)
            or nameof(ChildWindowModel.IconLayoutPolicy))
            _resizeController.SnapWidthToGrid();
    }

    /// <summary>
    /// <see cref="ILayoutEditModeService.ModeChanged"/> subscriber (docs/PLANS/phase-6-edit-layout-ux.md
    /// §1 D-4) — a real <see cref="LayoutInteractionMode"/> transition (entering or leaving Edit Layout).
    /// Delegates to <see cref="AbortGesturesAndApplyLayoutGate"/>, which every subscriber is required to
    /// run in full (abort THEN repaint) before returning — see that method's remarks for the exact order.
    /// </summary>
    private void OnLayoutModeChanged(object? sender, LayoutModeChangedEventArgs e) =>
        AbortGesturesAndApplyLayoutGate();

    /// <summary>
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> subscriber on <see cref="ILayoutEditModeService"/> — catches the one live-gate input
    /// <see cref="OnLayoutModeChanged"/> does NOT: <see cref="ILayoutEditModeService.IsLayoutLocked"/>
    /// toggled while <see cref="ILayoutEditModeService.Mode"/> stays <see cref="LayoutInteractionMode.Normal"/>
    /// throughout (locking while already editing instead forces an internal exit back to <c>Normal</c>,
    /// which raises <see cref="ILayoutEditModeService.ModeChanged"/> on its own and reaches
    /// <see cref="AbortGesturesAndApplyLayoutGate"/> that way — this handler is the second, narrower
    /// entry point for the case where <c>Mode</c> never actually changes). Every other property this
    /// service raises (<c>Mode</c>/<c>IsEditing</c>/<c>CanEnterEditLayout</c>) is either already covered
    /// by <see cref="OnLayoutModeChanged"/> or does not affect <see cref="LayoutGesturePolicy.Resolve"/>'s
    /// inputs, so they are ignored here. The third input — the persisted Normal-mode preference
    /// (<see cref="ILayoutEditModeService.NormalModeGesturesPropertyName"/>, relayed by
    /// <see cref="ILayoutEditModeService.NotifyNormalModeGesturesChanged"/>) — takes the same full
    /// abort-then-regate path: a gesture cannot normally be in flight while the settings window is used,
    /// the abort calls are idempotent no-ops then, and keeping a single path preserves D-4's ordering
    /// guarantee should a programmatic change ever race a live gesture.
    /// </summary>
    private void OnLayoutEditModePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var affectsResizeGate = e.PropertyName == nameof(ILayoutEditModeService.IsLayoutLocked)
            || e.PropertyName == ILayoutEditModeService.NormalModeGesturesPropertyName;
        if (!affectsResizeGate) return;

        AbortGesturesAndApplyLayoutGate();
    }

    /// <summary>
    /// D-4's abort sequence, run in this exact order before the resize gate (D-5) is re-applied with the
    /// freshly-resolved <see cref="LayoutGesturePolicy"/> allowance:
    /// <list type="number">
    /// <item><description><see cref="_resizeController"/>.<c>AbortResize(ResizeEndReason.SourceChanged)</c> — idempotent (ADR-022).</description></item>
    /// <item><description><see cref="_dragMoveBehavior"/>.<c>AbortDrag()</c> — idempotent (docs/PLANS/phase-6-edit-layout-ux.md §1 D-4).</description></item>
    /// <item><description><see cref="_interactionTracker"/>.<c>ForceIdle()</c> — backstop (ADR-021 escape hatch), in case some future gesture type fails to release itself via the two calls above.</description></item>
    /// <item><description><see cref="_resizeGate"/>.<c>SetAllowedByPolicy</c> — only NOW, after every active gesture has had a chance to cleanly end against the OLD policy, is the resize affordance re-gated for the new one.</description></item>
    /// </list>
    /// <see cref="_dragMoveBehavior"/>'s own <c>canMove</c> delegate needs no equivalent explicit refresh
    /// here — it is a live <c>Func&lt;bool&gt;</c> that re-reads <see cref="LayoutGesturePolicy.Resolve"/>
    /// fresh on every title-bar press (see its constructor doc comment), so the very next press already
    /// sees the new policy without this method touching it.
    /// </summary>
    private void AbortGesturesAndApplyLayoutGate()
    {
        _resizeController.AbortResize(ResizeEndReason.SourceChanged);
        _dragMoveBehavior.AbortDrag();
        _interactionTracker.ForceIdle();

        var allowance = LayoutGesturePolicy.Resolve(
            _layoutEditMode.Mode,
            _layoutEditMode.IsLayoutLocked,
            NormalModeLayoutGesturesParser.ParseOrMoveOnly(_appSettings.NormalModeLayoutGestures));

        _resizeGate.SetAllowedByPolicy(allowance.CanResize);
    }
}
