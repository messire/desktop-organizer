using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Interaction.Native;
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
/// (<see cref="PanelResizeController"/>), roll-up (<see cref="RollUpBehavior"/>), title inline-rename
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

    // P06 (docs/fix-prompts/06-fit-content.md): backs ChildWindow.ContentSizing.cs's "Apply to all
    // panels…" confirmation dialogs. IConfirmationService is already a DI singleton (Configuration/DiConfig.cs)
    // used by MainWindowViewModel for the same purpose — MessageBox.Show has its own top-level HWND, so
    // it receives input/activation normally regardless of this panel's own WS_EX_NOACTIVATE style.
    private readonly IConfirmationService _confirmation;

    private readonly PanelDragMoveBehavior _dragMoveBehavior;

    // docs/PLANS/phase-6-edit-layout-ux.md §1 D-5: the gate wraps whichever IResizeAffordance this
    // panel's resize path uses (ThumbOverlayAffordance or NativeSizingAffordance) and is what actually
    // gets handed to RollUpBehavior — RollUpBehavior keeps calling SetEnabled(!collapsed) exactly as
    // before, unaware a second, independent policy input now also gates the same affordance.
    private readonly ResizeAffordanceGate _resizeGate;

    // Phase 2 (docs/PLANS/phase-2-native-panel-sizing.md §1.6): _resizeController is the common
    // IPanelResizeController surface (AbortResize/SnapWidthToGrid) used by every call site that does not
    // care which path is active (Initialize, OnClosing, OnModelPropertyChanged, ChildWindow_Unloaded).
    // Exactly one of _thumbResizeController/_nativeResizeController is non-null — set once, in the
    // constructor, by NativeSizingSwitch.IsEnabled — and _resizeController points at whichever one that
    // is. The Thumb-specific event handlers below (Resize_DragStarted/Delta/Completed,
    // ResizeOverlay_LostMouseCapture, ResizeOverlay_PreviewMouseLeftButtonDown) go through
    // _thumbResizeController directly (null-conditional) since those XAML events have no native-path
    // equivalent — NativePanelResizeController is driven entirely by INativePanelSizingService callbacks
    // instead, and the ResizeOverlay Thumbs are disabled up front on that path (R-9) so those handlers
    // are never actually invoked when native sizing is active.
    private readonly IPanelResizeController _resizeController;
    private readonly PanelResizeController? _thumbResizeController;
    private readonly NativePanelResizeController? _nativeResizeController;
    private readonly RollUpBehavior _rollUpBehavior;
    private readonly PanelTitleRenameBehavior _titleRename;
    private readonly PanelVisualStateBehavior _panelVisualState;

    // docs/PLANS/phase-6-edit-layout-ux.md §1 D-6: the direct sibling of _panelVisualState above — writes
    // PanelTheme.IsLayoutEditingProperty onto the same PanelRoot instead of PanelTheme.VisualStateProperty.
    private readonly LayoutEditModeBehavior _layoutEditModeVisual;
    private readonly ListReorderGestureController<FileItem> _reorderController;
    private readonly HintPopupController _hintPopups;

    // Stage S (docs/PLANS/workspace-grid-03-04-integration.md §1.5): the single point P03/P04 write
    // Left/Top/Width/Height + the model's Position/Size (+ span/MonitorDeviceId) through together, so
    // OnLocationChanged/OnRenderSizeChanged below never fight a P03/P04-driven geometry write with their
    // own redundant model write (baseline risk 17). Constructed once, unconditionally — used by both the
    // Thumb and native resize paths.
    private readonly PanelGeometryWriter _geometryWriter;

    // ADR-021: one PanelInteractionTracker per panel, shared by every gesture behaviour below so "only
    // one gesture owns capture at a time" is enforced across drag AND resize. Constructed before both
    // behaviours (docs/PLANS/phase-1-resize-stabilization.md §3.5).
    private readonly PanelInteractionTracker _interactionTracker = new();

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
    public ChildWindow(
        ChildWindowViewModel viewModel,
        IWindowManager windowManager,
        IDesktopIntegrationService desktopIntegration,
        IAppSettings appSettings,
        IGestureDiagnostics diagnostics,
        INativePanelSizingService nativeSizingService,
        ILayoutEditModeService layoutEditMode,
        IMonitorService monitorService,
        IConfirmationService confirmation)
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
        DataContext = _viewModel;

        // Phase 2 (docs/PLANS/phase-2-native-panel-sizing.md §1.6): decide once, here, which resize
        // path this panel uses. NativeSizingSwitch itself needs no HWND — only the persisted setting
        // and the process's CLI args/environment — so the decision does not have to wait for
        // OnSourceInitialized. It is made this early specifically because RollUpBehavior (which needs
        // the resulting IResizeAffordance) must itself be constructed before _dragMoveBehavior below
        // (its onDoubleClick callback captures _rollUpBehavior). Only the native hook's actual
        // installation (NativePanelResizeController.AttachTo, which DOES need a real HWND) is deferred
        // to OnSourceInitialized, after EmbedInDesktop — see that method.
        var nativeSizingEnabled = NativeSizingSwitch.IsEnabled(
            Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable, _appSettings.NativePanelSizing);

        // R-9: the Thumb overlay is wrapped by this affordance regardless of which path ends up active.
        // On the native path it is disabled immediately below, before the panel is even shown, so the 8
        // Thumbs can never intercept a click — RollUpBehavior only re-asserts overlay state on later
        // roll-up/roll-down toggles, it does not run at construction time.
        var thumbOverlayAffordance = new ThumbOverlayAffordance(ResizeOverlay);

        // docs/PLANS/phase-6-edit-layout-ux.md §1 D-2/D-5: read fresh on every title-bar press by
        // PanelDragMoveBehavior — never snapshotted — so a mode/lock/preference change made while no
        // gesture is in flight is picked up by the very next press without this panel needing to do
        // anything else.
        bool CanMove() => LayoutGesturePolicy.Resolve(
            _layoutEditMode.Mode,
            _layoutEditMode.IsLayoutLocked,
            NormalModeLayoutGesturesParser.ParseOrMoveOnly(_appSettings.NormalModeLayoutGestures)).CanMove;

        // Constructed before _dragMoveBehavior/_resizeController (P03): both branches below hand it to
        // PanelDragMoveBehavior, which commits every GridAligned drop through it (plan §1.5).
        _geometryWriter = new PanelGeometryWriter(this, () => _model);

        // Constructed before _dragMoveBehavior: its onDoubleClick callback below captures
        // _rollUpBehavior, and assigning it first keeps that a real (non-nullable) reference
        // rather than a forward reference nullable analysis can't see through.
        if (nativeSizingEnabled)
        {
            thumbOverlayAffordance.SetEnabled(false);
            var nativeAffordance = new NativeSizingAffordance();
            _resizeGate = new ResizeAffordanceGate(nativeAffordance);
            _rollUpBehavior = new RollUpBehavior(
                this, _windowManager, ContentRow, FileListHost, _resizeGate, TitleBarHeight, () => _model);

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
                geometryWriter: _geometryWriter);

            _nativeResizeController = new NativePanelResizeController(
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
                isResizeEnabled: () => nativeAffordance.IsEnabled);
            _resizeController = _nativeResizeController;
        }
        else
        {
            _resizeGate = new ResizeAffordanceGate(thumbOverlayAffordance);
            _rollUpBehavior = new RollUpBehavior(
                this, _windowManager, ContentRow, FileListHost, _resizeGate, TitleBarHeight, () => _model);

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
                geometryWriter: _geometryWriter);

            _thumbResizeController = new PanelResizeController(
                this,
                _appSettings,
                _windowManager,
                getModel: () => _model,
                onResizeGestureStarting: OnPanelGestureStarting,
                clearDragObstacles: _dragMoveBehavior.ClearDragObstacles,
                snapToGrid: _dragMoveBehavior.SnapToGrid,
                titleBarHeight: TitleBarHeight,
                diagnostics: _diagnostics,
                tracker: _interactionTracker);
            _resizeController = _thumbResizeController;
        }

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
            performReorder: (item, index) => _viewModel.ReorderItem(item, index));

        _hintPopups = new HintPopupController(
            Dispatcher, MoveHintPopup, RenameErrorPopup, RenameErrorText, FileListHost, () => _viewModel.EditingItem);

        // Persist whenever the user changes the file list from the panel (e.g. removes an item).
        _viewModel.FilesChanged += (_, _) => _windowManager.SaveWindowsState();

        // One-time, non-blocking hint the first time a live drag-drop actually moves a file/folder
        // into app-managed storage — see ChildWindowViewModel.MoveHintRequested.
        _viewModel.MoveHintRequested += (_, _) => _hintPopups.ShowMoveHint();

        // Non-blocking hint whenever an inline-rename attempt fails (see ChildWindowViewModel.RenameError).
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

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

        Left = model.Position.X;
        Top = model.Position.Y;

        if (model.Size.Width > 0) Width = model.Size.Width;
        if (model.Size.Height > 0) Height = model.Size.Height;

        WindowState = model.WindowState;
        _rollUpBehavior.ExpandedHeight = Height;

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

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _desktopIntegration.EmbedInDesktop(hwnd);

        // Phase 2 (docs/PLANS/phase-2-native-panel-sizing.md §1.6): the native WM_SIZING hook is
        // installed here — after EmbedInDesktop, once the panel's HWND is both valid and already
        // reparented into WorkerW — even though which controller to use was already decided in the
        // constructor (see its remarks). No-op when the Thumb path is active (_nativeResizeController
        // is null).
        _nativeResizeController?.AttachTo(hwnd);
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        _diagnostics.PanelClosing(_model?.Id, (Mouse.Captured as Thumb)?.IsDragging ?? false);

        // ADR-021 "window disposal forces state to Idle": end whatever resize gesture might still be
        // active before the window (and its Thumbs) go away — mirrors ResizeOverlay_LostMouseCapture's
        // "diagnostics call first, behavior second" ordering above (plan §3.5).
        _resizeController.AbortResize(ResizeEndReason.Closing);

        // R-10: deterministically remove the native WM_SIZING hook right next to the AbortResize call
        // above — a no-op (and idempotent) when the Thumb path is active or AttachTo was never reached.
        _nativeResizeController?.Dispose();

        if (_model is not null)
            _model.PropertyChanged -= OnModelPropertyChanged;

        // P06 hook (docs/fix-prompts/06-fit-content.md) — see ChildWindow.ContentSizing.cs.
        DetachContentSizing();

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _layoutEditMode.ModeChanged -= OnLayoutModeChanged;
        _layoutEditMode.PropertyChanged -= OnLayoutEditModePropertyChanged;
        _panelVisualState.Dispose();
        _layoutEditModeVisual.Dispose();
        _hintPopups.StopTimers();
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
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    /// <inheritdoc/>
    protected override void OnDrop(DragEventArgs e)
    {
        // Folder Portals are read-only — ignore drops.
        if (_viewModel.IsPortal) return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        // Mirror OnDragEnter's effect here too: some drag sources (e.g. Explorer) inspect the final
        // e.Effects after the drop completes to decide their own post-drop behavior (cursor feedback,
        // whether to show a "move" vs "copy" animation). AddDroppedFiles always moves, so make that
        // explicit rather than relying on whatever effect happened to still be set from drag-over.
        e.Effects = DragDropEffects.Move;

        _viewModel.AddDroppedFiles(files);
        _windowManager.SaveWindowsState();
    }

    /// <inheritdoc/>
    protected override void OnLocationChanged(EventArgs e)
    {
        // Stage S guard (docs/PLANS/workspace-grid-03-04-integration.md §1.5): a PanelGeometryWriter.Apply
        // call already wrote model.Position directly — this handler firing synchronously as a side
        // effect of that same Left/Top assignment must not write it a second, redundant time.
        if (_model is null || _geometryWriter.IsApplying) return;
        _model.Position = new Point((int)Left, (int)Top);
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
    /// Handles the global inline-rename shortcut: <c>F2</c> begins renaming the currently selected
    /// file item (Feature B, DoD 8). No-op if nothing is selected, the panel title is itself being
    /// renamed (<see cref="PanelTitleRenameBehavior.IsEditingTitle"/>) — F2 should not fight the title
    /// editor for focus — or a file item rename is already in progress (repeated F2 would otherwise
    /// re-run <see cref="ChildWindowViewModel.BeginRenameCommand"/> and overwrite the text the user
    /// already typed with <see cref="FileItem.DisplayName"/> again).
    /// <para>
    /// Also handles <c>Escape</c>: exits <see cref="ILayoutEditModeService"/>'s Edit Layout mode
    /// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #4). Best-effort only — this panel carries
    /// <c>WS_EX_NOACTIVATE</c> and has never received keyboard input reliably in this state (same
    /// unverified risk class as ADR-013 R1's F2/inline-rename) — the tray icon and this panel's own
    /// <c>HeaderContextMenu</c> "Edit layout" item remain the guaranteed exit paths; <c>Escape</c> is
    /// never the sole way out (R-6).
    /// </para>
    /// </summary>
    private void ChildWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            if (_titleRename.IsEditingTitle) return;
            if (_viewModel.EditingItem is not null) return;
            if (_viewModel.SelectedFile is null) return;

            _viewModel.BeginRenameCommand.Execute(_viewModel.SelectedFile);
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
    /// (title-bar press) and <see cref="PanelResizeController"/> (resize-thumb press): commits any
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

    private void Resize_DragStarted(object sender, DragStartedEventArgs e)
        => _thumbResizeController?.OnResizeDragStarted(sender, e);

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
        => _thumbResizeController?.OnResizeDragDelta(sender, e);

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e)
        => _thumbResizeController?.OnResizeDragCompleted(sender, e);

    /// <summary>
    /// Log-only observer of the <c>ResizeOverlay</c>'s bubbling <c>Mouse.GotMouseCapture</c> (one
    /// handler for all 8 <c>Thumb</c>s — docs/Refactoring/ §3.4). Never sets <c>e.Handled</c>.
    /// </summary>
    private void ResizeOverlay_GotMouseCapture(object sender, MouseEventArgs e)
        => _diagnostics.CaptureGot(_model?.Id, e.OriginalSource?.GetType().Name ?? "null", Mouse.Captured?.GetType().Name);

    /// <summary>
    /// Observer of the <c>ResizeOverlay</c>'s bubbling <c>Mouse.LostMouseCapture</c> — see
    /// <see cref="ResizeOverlay_GotMouseCapture"/>. Never sets <c>e.Handled</c>. As of Phase 1 resize
    /// stabilization (commit C5), this is no longer purely log-only: the existing
    /// <see cref="IGestureDiagnostics.CaptureLost"/> call stays first (unchanged order/position — the
    /// diagnostics-log-events.md contract), and is now followed by the REAL cleanup path
    /// (<see cref="PanelResizeController.OnCaptureLost"/>), which ends any gesture still active for this
    /// panel — an involuntary capture loss (Alt+Tab, system dialog, WorkerW/native capture theft) is
    /// exactly the scenario H2/H4 describe (plan §6).
    /// </summary>
    private void ResizeOverlay_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _diagnostics.CaptureLost(
            _model?.Id,
            e.OriginalSource?.GetType().Name ?? "null",
            Mouse.Captured?.GetType().Name,
            (e.OriginalSource as Thumb)?.IsDragging ?? false);

        _thumbResizeController?.OnCaptureLost(sender, e);
    }

    /// <summary>
    /// Stale-drag self-heal entry point (plan §3.4/§6 H4) — runs on the tunneling <c>Preview</c> pass,
    /// BEFORE the individual <c>Thumb</c>'s own <c>MouseLeftButtonDown</c> handler (which silently
    /// no-ops while <see cref="Thumb.IsDragging"/> is already <see langword="true"/>). Never sets
    /// <c>e.Handled</c> — the <c>Thumb</c> underneath must still receive this same click normally
    /// afterwards to start its own (now un-stuck) drag.
    /// </summary>
    private void ResizeOverlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _thumbResizeController?.RecoverStaleDrag(e.OriginalSource);

    /// <summary>
    /// ADR-021 "window disposal forces state to Idle": ends any resize gesture still active when this
    /// panel unloads, and forces the shared <see cref="PanelInteractionTracker"/> back to
    /// <see cref="PanelInteractionState.Idle"/> as a final backstop (in case some future gesture type
    /// besides resize/drag ever fails to release it itself).
    /// </summary>
    private void ChildWindow_Unloaded(object sender, RoutedEventArgs e)
    {
        // IPanelResizeController.AbortResize(Unloaded) is the shared-contract equivalent of
        // PanelResizeController.OnUnloaded() (which itself just calls AbortResize(Unloaded)) — routed
        // through the common interface so this works for both resize paths.
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
    /// Explorer-like "right-click selects the item before the context menu opens" behavior (Feature A).
    /// Runs on the Preview (tunneling) pass so the selection is already updated by the time the
    /// bubbling <c>MouseRightButtonUp</c> shows <c>FileContextMenu</c>.
    /// </summary>
    private void FileListHost_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        if (FindAncestorOrSelf<ListBoxItem>(source)?.DataContext is FileItem item)
            _viewModel.SelectedFile = item;
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
    /// this handler re-snaps here too. <see cref="IPanelResizeController.SnapWidthToGrid"/> itself already
    /// gates on Grid mode and <c>IAppSettings.SnapResizeToIconGrid</c> — no extra guard is needed here,
    /// mirroring exactly how the pre-existing <c>IconSize</c>/<c>ViewMode</c> triggers rely on that same
    /// internal gate rather than duplicating it.
    /// <para>
    /// <b>Known, pre-existing, unchanged scope gap (not introduced or fixed by this addition):</b>
    /// <see cref="IPanelResizeController.SnapWidthToGrid"/> does not special-case
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
    /// inputs, so they are ignored here.
    /// </summary>
    private void OnLayoutEditModePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ILayoutEditModeService.IsLayoutLocked)) return;

        AbortGesturesAndApplyLayoutGate();
    }

    /// <summary>
    /// D-4's abort sequence, run in this exact order before the resize gate (D-5) is re-applied with the
    /// freshly-resolved <see cref="LayoutGesturePolicy"/> allowance:
    /// <list type="number">
    /// <item><description><see cref="_resizeController"/>.<c>AbortResize(ResizeEndReason.SourceChanged)</c> — idempotent on both resize paths (ADR-022 §1.6).</description></item>
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
