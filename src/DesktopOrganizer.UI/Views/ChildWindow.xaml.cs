using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Core.Models;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Views;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>
/// Desktop panel window. Chromeless, embedded into the desktop layer via WorkerW.
/// </summary>
public partial class ChildWindow : Window
{
    private readonly ChildWindowViewModel _viewModel;
    private readonly IWindowManager _windowManager;
    private readonly IDesktopIntegrationService _desktopIntegration;
    private readonly IAppSettings _appSettings;
    private ChildWindowModel? _model;

    // Internal (rather than private) so WindowManager.CollectOtherPanelBounds can use it as the
    // target/final height of a rolled-up panel, instead of reading a mid-animation ActualHeight
    // (finalization MAJOR M-2) — see the constant's use there for details.
    internal const double TitleBarHeight = 32;

    // Flat pixel floor used directly as the height minimum in BOTH view modes, as the width minimum
    // in List mode (which has no columns at all), AND as the clamp floor inside
    // GridMetrics.MinPanelWidth's icon-grid-derived Grid-mode width minimum below — genuinely
    // axis-agnostic, hence the name (reverted from the briefly-tried "MinPanelHeight", which became
    // inaccurate the moment it started backing that clamp too — finalization MAJOR M-1/m-2 review).
    // Single source of truth is GridMetrics.MinPanelSize, which also backs the hard
    // Window.MinWidth="120" WPF coercion in ChildWindow.xaml and App.xaml.cs's lasso-drawn panel
    // minimum — keep all three in sync (the XAML literal cannot be bound to this constant; see the
    // comment beside it).
    private const double MinPanelSize = GridMetrics.MinPanelSize;

    /// <summary>
    /// Custom <see cref="DataObject"/> format used to identify an in-panel drag-to-reorder gesture
    /// (ADR-014 Decision 5, Block C), as opposed to an incoming <see cref="DataFormats.FileDrop"/>
    /// from the desktop/Explorer. The payload is the dragged item's <see cref="FileItem.Path"/> (a
    /// string), not the <see cref="FileItem"/> instance itself — a string survives crossing the OLE
    /// data-object boundary safely, and resolving it back to a <see cref="FileItem"/> by looking it
    /// up in the target panel's own <see cref="ChildWindowViewModel.FileItems"/> doubles as a free
    /// guard against a drag started from a different panel (path not found there ⇒ rejected).
    /// </summary>
    private const string PanelItemReorderFormat = "DesktopOrganizer.PanelItemReorder";

    private double _expandedHeight = 300;
    private bool _isEditingTitle;
    private string _titleBeforeEdit = string.Empty;

    // Raw (unquantised) accumulators for the current resize drag gesture.
    private double _rawWidth;
    private double _rawHeight;

    // Fixed opposite-edge anchors set at DragStarted; used to reposition the panel
    // when the Left or Top thumb is dragged so the right/bottom edge stays put.
    private double _rawRight;
    private double _rawBottom;

    // Cell size used to snap the current resize gesture, computed once in Resize_DragStarted.
    // 0 means snapping is disabled for that axis (free-form/pixel-accurate resize); see
    // AdaptiveGridLayout.SnapExtent, used by Resize_DragDelta/SnapWidthToGrid below
    // (docs/PLANS/adaptive-icon-grid.md §4, "D-2"). Not called "_step*" any more: the old
    // "round to a multiple" ("step") semantics died with GridMetrics.Quantize — the new snap target is
    // AdaptiveGridLayout.Extent(k)/Extent(R), which is not a plain multiple of the cell size in general
    // (it includes the 2*edgePadding term).
    private double _snapCellWidth;
    private double _snapCellHeight;

    // One-shot timer that auto-dismisses the Move-semantics hint popup; recreated per show.
    private DispatcherTimer? _moveHintTimer;

    // One-shot timer that auto-dismisses the rename-error hint popup; recreated per show.
    private DispatcherTimer? _renameErrorTimer;

    // Manual title-bar drag state (replaces Window.DragMove(), which blocks the calling thread
    // and hands movement off to the native OS move loop until mouse-up — incompatible with
    // per-mouse-move magnetic grid snapping). See docs/PLANS/grid-snap-during-drag.md.
    private bool _isDragging;
    private System.Windows.Point _dragStartScreenCursor;
    private double _dragStartLeft;
    private double _dragStartTop;

    // Snapshot of every OTHER live panel's screen rectangle, taken once per drag gesture (see
    // docs/PLANS/panel-anti-overlap.md §3.3) — safe to take only once because the mouse is captured
    // for the whole gesture, so no other panel can move/resize/appear/disappear meanwhile. A
    // reusable field (not a local list) so TitleBar_MouseMove never allocates on the per-frame hot
    // path; repopulated in TitleBar_MouseLeftButtonDown, cleared in EndDrag.
    private readonly List<Rect> _dragObstacles = [];

    // Drag-to-reorder gesture state (ADR-014 Decision 5/6, Block C). Mirrors the pattern already
    // used by SystemIconsPanelWindow: PreviewMouseLeftButtonDown remembers a candidate + start
    // point, PreviewMouseMove promotes it to a live DoDragDrop once the system drag threshold is
    // exceeded.
    private System.Windows.Point _reorderStartPoint;
    private FileItem? _reorderCandidate;
    private bool _isReordering;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindow"/> class.
    /// </summary>
    public ChildWindow(
        ChildWindowViewModel viewModel,
        IWindowManager windowManager,
        IDesktopIntegrationService desktopIntegration,
        IAppSettings appSettings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _windowManager = windowManager;
        _desktopIntegration = desktopIntegration;
        _appSettings = appSettings;
        DataContext = _viewModel;

        // Persist whenever the user changes the file list from the panel (e.g. removes an item).
        _viewModel.FilesChanged += (_, _) => _windowManager.SaveWindowsState();

        // One-time, non-blocking hint the first time a live drag-drop actually moves a file/folder
        // into app-managed storage — see ChildWindowViewModel.MoveHintRequested.
        _viewModel.MoveHintRequested += (_, _) => ShowMoveHint();

        // Non-blocking hint whenever an inline-rename attempt fails (see ChildWindowViewModel.RenameError).
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Initializes the window state from a <see cref="ChildWindowModel"/>.
    /// </summary>
    public void Initialize(ChildWindowModel model)
    {
        _model = model;

        Left = model.Position.X;
        Top  = model.Position.Y;

        if (model.Size.Width  > 0) Width  = model.Size.Width;
        if (model.Size.Height > 0) Height = model.Size.Height;

        WindowState = model.WindowState;
        _expandedHeight = Height;

        _viewModel.LoadFromModel(model);

        // Re-snap width to the icon grid on load so persisted sizes stay aligned even after
        // the icon size was changed in a previous session.
        SnapWidthToGrid();

        model.PropertyChanged += OnModelPropertyChanged;

        if (model.IsRolledUp) ApplyRollUp(true);
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
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelPropertyChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _moveHintTimer?.Stop();
        _renameErrorTimer?.Stop();
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

    /// <summary>
    /// Shows the non-modal, auto-dismissing Move-semantics hint popup anchored to the title bar.
    /// Rendering only — the one-time decision (whether to show at all) already happened in the
    /// ViewModel before <see cref="ChildWindowViewModel.MoveHintRequested"/> was raised.
    /// </summary>
    private void ShowMoveHint()
    {
        _moveHintTimer?.Stop();

        MoveHintPopup.IsOpen = true;

        _moveHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _moveHintTimer.Tick += (_, _) =>
        {
            MoveHintPopup.IsOpen = false;
            _moveHintTimer?.Stop();
        };
        _moveHintTimer.Start();
    }

    /// <summary>
    /// Shows the non-modal, auto-dismissing rename-error hint popup, anchored to the file list, the
    /// moment <see cref="ChildWindowViewModel.RenameError"/> is set to a non-null value.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChildWindowViewModel.RenameError)) return;
        if (_viewModel.RenameError is not { } outcome) return;

        ShowRenameError(outcome);
        RefocusRenameEditor();
    }

    /// <summary>
    /// Handles the global inline-rename shortcut: <c>F2</c> begins renaming the currently selected
    /// file item (Feature B, DoD 8). No-op if nothing is selected, the panel title is itself being
    /// renamed (<see cref="_isEditingTitle"/>) — F2 should not fight the title editor for focus — or a
    /// file item rename is already in progress (repeated F2 would otherwise re-run
    /// <see cref="ChildWindowViewModel.BeginRenameCommand"/> and overwrite the text the user already
    /// typed with <see cref="FileItem.DisplayName"/> again).
    /// </summary>
    private void ChildWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        if (_isEditingTitle) return;
        if (_viewModel.EditingItem is not null) return;
        if (_viewModel.SelectedFile is null) return;

        _viewModel.BeginRenameCommand.Execute(_viewModel.SelectedFile);
        e.Handled = true;
    }

    /// <summary>
    /// Renders and shows <see cref="RenameErrorPopup"/> for <paramref name="outcome"/>. Rendering only
    /// — the ViewModel already decided the rename failed; this just picks a human-readable message.
    /// </summary>
    private void ShowRenameError(RenameOutcome outcome)
    {
        RenameErrorText.Text = outcome switch
        {
            RenameOutcome.InvalidName => "Invalid file name.",
            RenameOutcome.NameTaken => "A file or folder with that name already exists.",
            RenameOutcome.NotFound => "File not found — it may have been deleted or moved.",
            _ => "Failed to rename the file.",
        };

        _renameErrorTimer?.Stop();

        RenameErrorPopup.IsOpen = true;

        _renameErrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _renameErrorTimer.Tick += (_, _) =>
        {
            RenameErrorPopup.IsOpen = false;
            _renameErrorTimer?.Stop();
        };
        _renameErrorTimer.Start();
    }

    /// <summary>
    /// Re-focuses the inline-rename <see cref="TextBox"/> after a failed
    /// <see cref="ChildWindowViewModel.CommitRenameCommand"/>. <see cref="ChildWindowViewModel.CommitRename"/>
    /// deliberately leaves <see cref="ChildWindowViewModel.EditingItem"/> set on failure "so the user can
    /// correct the name" — but the commit itself was triggered by <see cref="RenameEditBehavior"/>'s
    /// <c>LostKeyboardFocus</c> handler, so by the time this runs the editor is visible again but no
    /// longer holds keyboard focus. Without re-focusing it here, a second click into empty space would
    /// never raise <c>LostKeyboardFocus</c> again (focus already left the TextBox) and the editor would
    /// appear permanently stuck. Mirrors the same focus+select-all path <see cref="RenameEditBehavior"/>
    /// uses when the editor first becomes visible.
    /// </summary>
    private void RefocusRenameEditor()
    {
        if (_viewModel.EditingItem is null) return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                // Re-check: by the time this runs (deferred), the edit may already have been
                // committed/cancelled (e.g. Escape pressed before the dispatcher callback ran).
                if (_viewModel.EditingItem is null) return;
                if (FindVisibleRenameEditor(FileListHost) is not { } textBox) return;

                textBox.Focus();
                textBox.SelectAll();
            }));
    }

    /// <summary>
    /// Walks the visual tree from <paramref name="root"/> looking for the currently visible inline-rename
    /// <see cref="TextBox"/> (see <see cref="RenameEditBehavior"/>). There is at most one at a time — only
    /// one <c>FileItem</c> can be <see cref="ChildWindowViewModel.EditingItem"/> — but every other panel
    /// item's (invisible) rename <see cref="TextBox"/> also carries the attached property, so both checks
    /// are required.
    /// </summary>
    private static TextBox? FindVisibleRenameEditor(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBox { IsVisible: true } textBox && RenameEditBehavior.GetIsRenameEditor(textBox))
                return textBox;

            if (FindVisibleRenameEditor(child) is { } found)
                return found;
        }

        return null;
    }

    /// <inheritdoc/>
    protected override void OnLocationChanged(EventArgs e)
    {
        if (_model is null) return;
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
        // While rolled up, keep the persisted size at the expanded dimensions for restore.
        if (_model is null || _model.IsRolledUp) return;
        _model.Size = new Size((int)Width, (int)Height);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // An active file-item inline rename never sees this click as a LostKeyboardFocus (the panel
        // carries WS_EX_NOACTIVATE) — commit it explicitly first, matching RenameEditBehavior's own
        // "click away commits" semantics (finalization MAJOR M-2).
        CommitActiveItemRenameIfAny();

        // Clicking the title bar is not clicking a file item — clear this panel's own selection
        // (ui-polishing-1 §2.4 Block A1). Unconditional and ahead of both branches below: it never
        // sets e.Handled, so it cannot interfere with either the roll-up toggle or the drag gesture.
        _viewModel.SelectedFile = null;

        // Double click on the title bar toggles roll-up; single click drags the panel.
        // Clicks that land on child controls (rollup/rename/menu buttons) never reach here:
        // ButtonBase marks MouseLeftButtonDown as handled before it bubbles to this Border.
        if (e.ClickCount == 2)
        {
            ToggleRollUp();
            return;
        }

        // Start the drag optimistically, but only if mouse capture actually succeeds — otherwise
        // MouseMove/MouseLeftButtonUp may never arrive (e.g. capture denied while another element
        // already holds it) and _isDragging would get stuck true with no way to clear it.
        var captured = sender is UIElement element && element.CaptureMouse();
        if (!captured) return;

        _isDragging = true;
        _dragStartScreenCursor = PointToScreen(Mouse.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;

        // Snapshot every other live panel's rectangle exactly once for this gesture (plan §3.3):
        // correct because the mouse is captured for the whole gesture, so nothing else on screen can
        // move/resize/appear/disappear in the meantime. _model can be null in tests that never call
        // Initialize — guard the same way the rest of this file does, and clear rather than leave a
        // stale snapshot from a previous gesture in that case.
        if (_model is not null)
            _windowManager.CollectOtherPanelBounds(_model.Id, _dragObstacles);
        else
            _dragObstacles.Clear();
    }

    /// <summary>
    /// Live drag: on every mouse move while dragging, re-derive the raw (unsnapped) window
    /// position from the cursor delta since the drag started, magnetically snap it to the icon grid
    /// via <see cref="GridSnap"/>, then clamp it against the drag-obstacle snapshot taken in
    /// <see cref="TitleBar_MouseLeftButtonDown"/> so the panel can never overlap another live panel
    /// (docs/PLANS/panel-anti-overlap.md §3.5 — the anti-overlap clamp runs last, AFTER the grid
    /// snap, and is never snapped back afterwards: clamping before the magnet would let the magnet
    /// pull the panel a few pixels into a neighbour, and re-snapping after clamping would defeat the
    /// whole point of resting flush against a neighbour's edge). Replaces <c>Window.DragMove()</c>,
    /// which hands control to the native move loop and only returns on mouse-up — incompatible with
    /// per-frame snapping/clamping.
    /// </summary>
    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        // PointToScreen returns physical device pixels, but Window.Left/Top are device-independent
        // units (96 DPI baseline). At any DPI scale other than 100% (e.g. 125%/150%, common on
        // laptops) applying a raw device-pixel delta to a DIU coordinate would move the panel faster
        // than the cursor. Convert the delta to DIU using this window's DPI scale before applying it.
        var dpi = VisualTreeHelper.GetDpi(this);
        var currentScreenCursor = PointToScreen(Mouse.GetPosition(this));
        var deltaX = (currentScreenCursor.X - _dragStartScreenCursor.X) / dpi.DpiScaleX;
        var deltaY = (currentScreenCursor.Y - _dragStartScreenCursor.Y) / dpi.DpiScaleY;
        var rawLeft = _dragStartLeft + deltaX;
        var rawTop  = _dragStartTop  + deltaY;

        var (snappedLeft, snappedTop) = GridSnap.Snap(rawLeft, rawTop, _appSettings.GridSize, _appSettings.GridSnapDistance);

        var current = new Rect(Left, Top, ActualWidth, ActualHeight);
        var clamped = PanelCollision.ClampAgainstObstacles(current, snappedLeft, snappedTop, _dragObstacles);

        Left = clamped.X;
        Top  = clamped.Y;
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndDrag(sender as UIElement);

    /// <summary>
    /// Handles involuntary loss of mouse capture (Alt+Tab, system dialog, window deactivation,
    /// etc.) so a drag never gets "stuck" with <see cref="_isDragging"/> left <see langword="true"/>.
    /// Safe to call redundantly: <see cref="EndDrag"/> is a no-op once the drag has already ended.
    /// </summary>
    private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
        => EndDrag(sender as UIElement);

    private void EndDrag(UIElement? element)
    {
        if (!_isDragging) return;

        _isDragging = false;
        element?.ReleaseMouseCapture();
        SnapToGrid();

        // Drop the drag-gesture obstacle snapshot (docs/PLANS/panel-anti-overlap.md §3.3, R5): never
        // leave a stale snapshot lying around between gestures (e.g. after Alt+Tab/system-dialog
        // involuntary capture loss via TitleBar_LostMouseCapture).
        _dragObstacles.Clear();

        _windowManager.SaveWindowsState();
    }

    private void RollUpMenu_Click(object sender, RoutedEventArgs e) => ToggleRollUp();

    private void RenameMenu_Click(object sender, RoutedEventArgs e) => BeginRename();

    /// <summary>
    /// Clears this panel's selection when any of the three header buttons (Sort/View/RollUp) is
    /// clicked (ui-polishing-1 §2.4 Block A1) — a single handler on the buttons' shared parent
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
    /// Commits the current file-item inline rename, if one is in progress. Used by the A1 header/
    /// title-bar/resize click hooks (<see cref="TitleBar_MouseLeftButtonDown"/>,
    /// <see cref="HeaderButtons_PreviewMouseLeftButtonDown"/>, <see cref="Resize_DragStarted"/>) and by
    /// <see cref="ChildWindowViewModel.OnSelectionClaimed"/>'s cross-panel counterpart, all of which
    /// react to a gesture that a normal <c>LostKeyboardFocus</c> would otherwise have committed, were
    /// this window not <c>WS_EX_NOACTIVATE</c> (finalization MAJOR M-2). Mirrors
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

    /// <summary>
    /// Collapses the panel to its title bar, or restores it to the previous height.
    /// </summary>
    private void ToggleRollUp()
    {
        if (_model is null) return;

        if (!_model.IsRolledUp) _expandedHeight = Height; // remember before collapsing

        _model.IsRolledUp = !_model.IsRolledUp;
        ApplyRollUp(_model.IsRolledUp);
        _windowManager.SaveWindowsState();
    }

    private void ApplyRollUp(bool collapsed)
    {
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FileListHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeOverlay.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        double fromHeight = collapsed ? _expandedHeight : TitleBarHeight;
        double toHeight   = collapsed ? TitleBarHeight  : _expandedHeight;

        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From           = fromHeight,
            To             = toHeight,
            Duration       = TimeSpan.FromMilliseconds(150),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };

        // Set Height to the start value synchronously so the first frame is correct.
        Height = fromHeight;
        BeginAnimation(HeightProperty, animation);
    }

    /// <summary>
    /// Switches the title into inline-edit mode.
    /// </summary>
    private void BeginRename()
    {
        if (_model is null) return;

        _isEditingTitle = true;
        _titleBeforeEdit = _model.Title;

        TitleText.Visibility = Visibility.Collapsed;
        TitleEditBox.Visibility = Visibility.Visible;
        TitleEditBox.Focus();
        TitleEditBox.SelectAll();
    }

    private void CommitRename()
    {
        if (!_isEditingTitle) return;
        _isEditingTitle = false;

        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;

        SyncToModel();
        _windowManager.SaveWindowsState();
    }

    private void CancelRename()
    {
        if (!_isEditingTitle) return;
        _isEditingTitle = false;

        if (_model is not null) _model.Title = _titleBeforeEdit;
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitRename();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelRename();
                e.Handled = true;
                break;
        }
    }

    private void TitleEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => CommitRename();

    private void Resize_DragStarted(object sender, DragStartedEventArgs e)
    {
        // Resize anti-overlap is explicitly out of scope (plan §3.6) — SnapToGrid relies on
        // _dragObstacles being empty during a resize so CreatesNewOverlap is always a no-op guard
        // here, same as before this feature existed. That invariant is only guaranteed by construction
        // today (a drag gesture always clears it in EndDrag) and isn't otherwise enforced — clear it
        // explicitly here too so a resize can never silently inherit a stale, non-empty snapshot left
        // behind by a drag gesture that ended through some path other than EndDrag (finalization MINOR
        // m-1).
        _dragObstacles.Clear();

        // See TitleBar_MouseLeftButtonDown's remarks — same WS_EX_NOACTIVATE gap, same fix
        // (finalization MAJOR M-2).
        CommitActiveItemRenameIfAny();

        // Starting a resize is not clicking a file item — clear this panel's own selection
        // (ui-polishing-1 §2.4 Block A1).
        _viewModel.SelectedFile = null;

        _rawWidth  = Width;
        _rawHeight = Height;
        _rawRight  = Left + Width;
        _rawBottom = Top  + Height;

        if (_appSettings.SnapResizeToIconGrid && _model is not null && _model.ViewMode == PanelViewMode.Grid)
        {
            // Grid mode: width snaps to whole columns (GridMetrics.CellSize == x) and height snaps to
            // whole rows (GridMetrics.RowHeight == x_v) — deliberately NOT the same cell size on both
            // axes (docs/PLANS/adaptive-icon-grid.md §5, "D-3"): the cell is 84x92 at IconSize=48, not
            // square, so reusing CellSize for height would quantise height a few pixels short of an
            // actual row.
            _snapCellWidth  = GridMetrics.CellSize(_model.IconSize);
            _snapCellHeight = GridMetrics.RowHeight(_model.IconSize);
        }
        else
        {
            // List mode, or snapping disabled: BOTH axes are free-form (pixel-accurate). List mode is
            // always free on both axes — GridMetrics.CellSize/RowHeight describe icon-grid columns/rows,
            // which have nothing to do with a list row's height or a list panel's width.
            _snapCellWidth  = 0d;
            _snapCellHeight = 0d;
        }
    }

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string direction }) return;

        if (direction.Contains("Right"))
            _rawWidth += e.HorizontalChange;

        if (direction.Contains("Bottom"))
            _rawHeight += e.VerticalChange;

        if (direction.Contains("Left"))
            _rawWidth -= e.HorizontalChange;

        if (direction.Contains("Top"))
            _rawHeight -= e.VerticalChange;

        // Snap content area (strip chrome before snapping, then re-add).
        // FileListHPadding (left+right margin of FileListHost) is chrome for width just as
        // TitleBarHeight + FileListVPadding is chrome for height — strip it before snapping, for both
        // view modes. FileListVPadding (FileListHost.Margin="0,0,0,4") used to be omitted here — see
        // its doc comment on GridMetrics for the resulting off-by-4 this fixes.
        var contentW = Math.Max(0d, _rawWidth  - GridMetrics.FileListHPadding);
        var contentH = Math.Max(0d, _rawHeight - TitleBarHeight - GridMetrics.FileListVPadding);

        // Snapped branches are rounded UP (Math.Ceiling) to a whole pixel, never down — same rationale
        // as SnapWidthToGrid above: the real Win32 window width/height is an integer device pixel, and
        // without rounding up a fractional IconSize or DPI/layout rounding can leave the live window a
        // hair short of the Extent(k)/Extent(R) it was just snapped to, making RowCapacity drop a whole
        // column mid-drag (finalization MAJOR M-1). The free-form (non-snapped) branches don't need this
        // — they pass the raw drag delta straight through, with no Extent(k) contract to honor.
        // The icon-grid-derived minimum width only makes sense in Grid mode — it's "one column plus
        // its half-cell edge padding on each side", which happens to measure 2 * CellSize(IconSize)
        // (see GridMetrics.MinPanelWidth's remarks; NOT "two columns' worth of cells" — that would be
        // roughly double) — apply it whether or not snapping is currently active for this gesture
        // (_snapCellWidth is 0 when snapping is disabled, but the panel is still a Grid panel). List
        // mode has no columns at all (ListViewTemplate ignores Model.IconSize for its own row height),
        // so it keeps the old flat floor instead of inheriting a Grid-cell-sized minimum.
        var minWidth = _model?.ViewMode == PanelViewMode.Grid
            ? GridMetrics.MinPanelWidth(_model.IconSize)
            : MinPanelSize;

        var newWidth = _snapCellWidth > 0d
            ? Math.Ceiling(Math.Max(minWidth, AdaptiveGridLayout.SnapExtent(contentW, _snapCellWidth, AdaptiveGridLayout.DefaultMinGap, ResizeSnapEdgePadding(_snapCellWidth)) + GridMetrics.FileListHPadding))
            : Math.Max(minWidth, contentW + GridMetrics.FileListHPadding);

        var newHeight = _snapCellHeight > 0d
            ? Math.Ceiling(Math.Max(MinPanelSize, TitleBarHeight + GridMetrics.FileListVPadding + AdaptiveGridLayout.SnapExtent(contentH, _snapCellHeight, AdaptiveGridLayout.DefaultMinGap, ResizeSnapEdgePadding(_snapCellHeight))))
            : Math.Max(MinPanelSize, _rawHeight);

        // Apply width.
        if (direction.Contains("Left"))
        {
            Left  = _rawRight - newWidth;
            Width = newWidth;
        }
        else if (direction.Contains("Right"))
        {
            Width = newWidth;
        }

        // Apply height.
        if (direction.Contains("Top"))
        {
            Top    = _rawBottom - newHeight;
            Height = newHeight;
        }
        else if (direction.Contains("Bottom"))
        {
            Height = newHeight;
        }
    }

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SnapToGrid();
        _windowManager.SaveWindowsState();
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

    /// <summary>
    /// Explorer-like "click on empty panel space deselects" behavior (Feature A). Only clears the
    /// selection when the click did not land on an item container — a click inside an item is left
    /// alone so the ListBox's own selection handling (and, later, inline-rename editing) applies.
    /// A click inside an in-progress inline-rename <see cref="TextBox"/> is never treated as "empty
    /// space", even before that editor visually sits inside an item container.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT cancel an in-progress inline rename here: this handler runs on the
    /// tunneling <c>PreviewMouseLeftButtonDown</c> pass, which fires before focus actually leaves the
    /// active rename <see cref="TextBox"/> — canceling unconditionally at this point would pre-empt
    /// <see cref="RenameEditBehavior"/>'s own <c>LostKeyboardFocus</c>-driven commit (C5 of
    /// docs/PLANS/icon-selection-and-rename.md) for every click-away, discarding even a valid typed
    /// name instead of committing it (regression against Explorer parity: click-away should commit,
    /// not cancel). A failed commit leaving the editor open with no focus is instead handled by
    /// <see cref="RefocusRenameEditor"/>, which re-focuses the <c>TextBox</c> after
    /// <see cref="ChildWindowViewModel.RenameError"/> is set, so the very next click-away or Escape
    /// works normally again — no separate cancel-on-click needed.
    /// </remarks>
    private void FileListHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Unconditionally clear any leftover candidate from a previous press first — before any of the
        // early returns below. Without this, a click that lands inside the active inline-rename
        // TextBox (e.g. to place the caret or select text) would leave a stale, unrelated candidate
        // set from an earlier press in place; FileListHost_PreviewMouseMove fires on this same visual
        // tree regardless of which element the TextBox itself handled the press on, so a subsequent
        // mouse move past the drag threshold — while the user is just selecting text — would wrongly
        // start a reorder drag using that stale candidate (MAJOR-1, panel-item-sorting review).
        _reorderCandidate = null;

        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestorOrSelf<TextBox>(source) is not null) return; // active inline-rename editor: let the click select text, never start a drag.

        if (FindAncestorOrSelf<ListBoxItem>(source) is null)
            _viewModel.SelectedFile = null;

        // Remember a drag-to-reorder candidate for FileListHost_PreviewMouseMove (ADR-014 Block C2).
        // No candidate — and therefore no drag ever starts — while another item is mid inline-rename,
        // or the panel is a read-only Folder Portal (ChildWindowViewModel.ReorderItem is a no-op there
        // anyway; rejecting here just avoids starting a doomed gesture). The TextBox guard above
        // already covers "click landed on the active rename editor" for the item being renamed itself.
        // Deliberately does not set e.Handled anywhere in this method — doing so would break the
        // ListBox's own built-in selection handling (see the deselection logic above, pre-existing).
        _reorderStartPoint = e.GetPosition(null);
        _reorderCandidate = _viewModel.EditingItem is null && !_viewModel.IsPortal
            ? FindAncestorOrSelf<ListBoxItem>(source)?.DataContext as FileItem
            : null;
    }

    /// <summary>
    /// Clears a drag-to-reorder candidate (see <see cref="FileListHost_PreviewMouseLeftButtonDown"/>)
    /// left over from a plain click that never moved far enough to start a drag. Without this, the
    /// candidate would otherwise live until the very next left-button press — mostly harmless in
    /// practice (that next press immediately overwrites or clears it), but it's an unbounded-lifetime
    /// piece of state with no reason to outlive the mouse gesture that created it (MAJOR-1,
    /// panel-item-sorting review). No-op while an actual reorder drag is in flight
    /// (<see cref="_isReordering"/>): <see cref="DragDrop.DoDragDrop"/> is blocking, so this handler
    /// cannot even run again until it returns, at which point <see cref="FileListHost_PreviewMouseMove"/>
    /// itself already clears the candidate.
    /// </summary>
    private void FileListHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isReordering) return;
        _reorderCandidate = null;
    }

    /// <summary>
    /// Promotes a remembered drag-to-reorder candidate (see <see cref="FileListHost_PreviewMouseLeftButtonDown"/>)
    /// into an actual <see cref="DragDrop.DoDragDrop"/> gesture once the mouse has moved past the
    /// system drag threshold (<see cref="SystemParameters.MinimumHorizontalDragDistance"/>/
    /// <see cref="SystemParameters.MinimumVerticalDragDistance"/>) — so a short click never starts a
    /// drag. <see cref="DragDrop.DoDragDrop"/> is blocking: it does not return until the gesture ends
    /// (drop, or cancel via Escape/lost capture), so all cleanup happens immediately after the call
    /// returns, regardless of how the gesture ended.
    /// </summary>
    private void FileListHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _reorderCandidate is null || _isReordering)
            return;

        var delta = e.GetPosition(null) - _reorderStartPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _isReordering = true;
        try
        {
            var data = new DataObject(PanelItemReorderFormat, _reorderCandidate.Path);
            DragDrop.DoDragDrop(FileListHost, data, DragDropEffects.Move);
        }
        finally
        {
            // Runs on every exit path — normal completion, cancellation, AND an exception thrown out
            // of DoDragDrop itself (a documented possibility for OLE drag&drop, e.g. a COM-level
            // failure in the drop target). Without this in a finally, an exception here would leave
            // _isReordering stuck true forever, permanently disabling every future reorder gesture in
            // this panel — no in-app way to recover short of restarting (MAJOR-2, panel-item-sorting
            // review) — and could also leave the insertion-line indicator visibly stuck on screen.
            _isReordering = false;
            _reorderCandidate = null;
            HideReorderInsertionIndicator();
        }
    }

    /// <summary>
    /// Accepts or rejects a drag-reorder hover (ADR-014 Block C3). For a foreign data format (most
    /// importantly an incoming <see cref="DataFormats.FileDrop"/> from the desktop/Explorer) this
    /// returns immediately WITHOUT touching <see cref="DragEventArgs.Effects"/> or
    /// <see cref="DragEventArgs.Handled"/> at all. That's not just cosmetic: per the OLE drag&amp;drop
    /// contract, whatever <see cref="DragEventArgs.Effects"/> the <i>last</i> <c>DragOver</c> left set
    /// before the mouse button is released decides whether the system calls <c>IDropTarget.Drop</c> or
    /// silently <c>IDropTarget.DragLeave</c> instead — if this handler had set <c>Effects = None</c> for
    /// a foreign format (even without setting <c>Handled</c>), a file dragged in from the desktop could
    /// stop being droppable at all, not just show the wrong cursor. <see cref="Window"/> overrides
    /// <see cref="OnDragEnter"/>/<see cref="OnDrop"/> but not <c>OnDragOver</c>, so nothing above this
    /// handler ever gets a chance to recompute <c>Effects</c> for the same tick (critical risk R1 — see
    /// docs/PLANS/panel-item-sorting.md).
    /// </summary>
    /// <remarks>
    /// Once the format is confirmed to be <see cref="PanelItemReorderFormat"/>, an invalid target
    /// (Folder Portal, wrong panel, item no longer present — see <see cref="TryResolveReorderItem"/>)
    /// is rejected by setting <c>Effects = None</c> AND <c>Handled = true</c>: this drag was never a
    /// <see cref="DataFormats.FileDrop"/> to begin with, so there is nothing useful for
    /// <see cref="OnDragEnter"/> to do with it further up the bubble.
    /// </remarks>
    private void FileListHost_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(PanelItemReorderFormat)) return; // foreign format — leave e untouched (risk R1).

        if (!TryResolveReorderItem(e, out _))
        {
            e.Effects = DragDropEffects.None;
            HideReorderInsertionIndicator();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        ShowReorderInsertionIndicator(e);
        e.Handled = true;
    }

    private void FileListHost_DragLeave(object sender, DragEventArgs e) => HideReorderInsertionIndicator();

    /// <summary>
    /// Completes a drag-reorder drop (ADR-014 Block C3). Mirrors <see cref="FileListHost_DragOver"/>'s
    /// accept/reject split exactly, for the same reason: a foreign-format drop (desktop/Explorer file
    /// drop) must be left completely untouched — not even <see cref="ReorderInsertionIndicator"/>
    /// hidden here, since it was never shown for a foreign format in the first place — so it bubbles to
    /// <see cref="OnDrop"/> on the <see cref="Window"/>, which still performs
    /// <see cref="ChildWindowViewModel.AddDroppedFiles"/> as before (risk R1).
    /// </summary>
    private void FileListHost_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(PanelItemReorderFormat)) return; // foreign format — leave e untouched (risk R1).

        if (!TryResolveReorderItem(e, out var item) || item is null)
        {
            HideReorderInsertionIndicator();
            e.Handled = true;
            return;
        }

        var visibleInsertIndex = ComputeDropInsertionIndex(e);
        _viewModel.ReorderItem(item, visibleInsertIndex);

        HideReorderInsertionIndicator();
        e.Handled = true;
    }

    /// <summary>
    /// Shared accept/reject gate for <see cref="FileListHost_DragOver"/> and <see cref="FileListHost_Drop"/>
    /// (ADR-014 Block C3). Resolves the dragged item's path (the <see cref="DataObject"/> payload —
    /// see <see cref="PanelItemReorderFormat"/>) back to a live <see cref="FileItem"/> by looking it up
    /// in this panel's own currently-visible <see cref="ChildWindowViewModel.FileItems"/>.
    /// </summary>
    /// <param name="e">The drag event to inspect.</param>
    /// <param name="item">The resolved item, or <see langword="null"/> when rejected.</param>
    /// <returns><see langword="true"/> if <paramref name="e"/> carries a reorder for an item this panel currently shows.</returns>
    private bool TryResolveReorderItem(DragEventArgs e, out FileItem? item)
    {
        item = null;

        if (_viewModel.IsPortal) return false;
        if (!e.Data.GetDataPresent(PanelItemReorderFormat)) return false;
        if (e.Data.GetData(PanelItemReorderFormat) is not string path) return false;

        item = _viewModel.FileItems.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        return item is not null;
    }

    /// <summary>
    /// Resolves a drop position (from <see cref="FileListHost_Drop"/>) to a visible-index insertion
    /// gap in <c>[0, FileItems.Count]</c> (ADR-014 Block C4), by hit-testing the live <see cref="ListBox"/>
    /// hosted inside <see cref="FileListHost"/>. Works unchanged for both the List and Grid
    /// <c>DataTemplate</c>s in <c>Templates.xaml</c> — walking up from the hit-tested visual to the
    /// nearest <see cref="ListBoxItem"/> ancestor doesn't depend on the <c>ItemsPanel</c> type
    /// (<c>StackPanel</c> vs <c>WrapPanel</c> vs <see cref="DesktopOrganizer.UI.Controls.AdaptiveIconGridPanel"/>)
    /// and survives virtualization, unlike walking
    /// <see cref="ListBox.Items"/>/children directly.
    /// </summary>
    /// <remarks>
    /// Every "can't resolve a concrete hit-tested item" case (no <see cref="ListBox"/> found, cursor
    /// over empty space with no hit-tested container, or a hit-tested container the generator can't
    /// resolve back to an index) collapses to <c>containerIndex = null</c> and always falls through to
    /// the single call to <see cref="ComputeInsertionGap"/> below, rather than returning
    /// <paramref name="e"/>-derived <c>itemCount</c> directly from three separate early-return branches.
    /// That used to leave <c>containerIndex == null</c> reachable only from unit tests, never from this
    /// production code path (MAJOR-4, panel-item-sorting review) — now the exact same method decides
    /// the end-of-list fallback for both.
    /// </remarks>
    private int ComputeDropInsertionIndex(DragEventArgs e)
    {
        var itemCount = _viewModel.FileItems.Count;

        int? containerIndex = null;
        var positionInContainer = default(System.Windows.Point);
        var containerSize = default(System.Windows.Size);

        if (FindVisualChild<ListBox>(FileListHost) is { } listBox &&
            HitTestReorderContainer(listBox, e) is { } container)
        {
            var index = listBox.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0)
            {
                containerIndex = index;
                positionInContainer = e.GetPosition(container);
                containerSize = new System.Windows.Size(container.ActualWidth, container.ActualHeight);
            }
        }

        return ComputeInsertionGap(containerIndex, positionInContainer, containerSize, _viewModel.Model.ViewMode, itemCount);
    }

    /// <summary>
    /// Pure, testable core of the drag-to-reorder insertion-gap math (ADR-014 Block C4 — "wrap in a
    /// form that accepts primitives, not live UI objects, so it's unit-testable without a real
    /// <see cref="ListBox"/>/hit-test"). Compares the cursor position within the hit-tested container
    /// against its midpoint, along the axis that matches the current layout: horizontal (X) in
    /// <see cref="PanelViewMode.Grid"/>, vertical (Y) in <see cref="PanelViewMode.List"/> — the two
    /// view modes lay items out on different axes, so "before" the cursor means a different axis in
    /// each.
    /// </summary>
    /// <param name="containerIndex">
    /// The visible index of the <see cref="ListBoxItem"/> container the cursor is over, or
    /// <see langword="null"/> when the cursor is over empty space past the last item — always
    /// resolves to the end-of-list gap (<paramref name="itemCount"/>) regardless of the other
    /// arguments in that case.
    /// </param>
    /// <param name="cursorPosition">The cursor's position relative to the hit-tested container's own origin.</param>
    /// <param name="containerSize">The hit-tested container's size.</param>
    /// <param name="viewMode">The panel's current view mode, selecting the comparison axis.</param>
    /// <param name="itemCount">The total number of currently visible items — the end-of-list fallback gap.</param>
    /// <returns>The insertion gap index, in <c>[0, itemCount]</c>.</returns>
    internal static int ComputeInsertionGap(
        int? containerIndex,
        System.Windows.Point cursorPosition,
        System.Windows.Size containerSize,
        PanelViewMode viewMode,
        int itemCount)
    {
        if (containerIndex is not { } index) return itemCount;

        var beforeMidpoint = viewMode == PanelViewMode.Grid
            ? cursorPosition.X < containerSize.Width / 2.0
            : cursorPosition.Y < containerSize.Height / 2.0;

        return beforeMidpoint ? index : index + 1;
    }

    /// <summary>
    /// Hit-tests <paramref name="listBox"/> at the drag position from <paramref name="e"/> and walks
    /// up to the nearest <see cref="ListBoxItem"/> ancestor (inclusive), or <see langword="null"/> when
    /// the cursor isn't over any item container (empty space below/after the last item).
    /// </summary>
    private static ListBoxItem? HitTestReorderContainer(ListBox listBox, DragEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(listBox, e.GetPosition(listBox));
        return hit is null ? null : FindAncestorOrSelf<ListBoxItem>(hit.VisualHit);
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
        var container = HitTestReorderContainer(listBox, e);

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
    /// Walks the visual tree looking for the first descendant of type <typeparamref name="T"/>
    /// (breadth doesn't matter here — there is exactly one <see cref="ListBox"/> per <c>DataTemplate</c>
    /// instance). Mirrors the identically-named helper in <c>SystemIconsPanelWindow.xaml.cs</c>.
    /// </summary>
    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            if (FindVisualChild<T>(child) is { } found) return found;
        }

        return null;
    }

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
    /// Walks up the visual tree from <paramref name="obj"/> (inclusive) looking for the nearest
    /// ancestor of type <typeparamref name="T"/>.
    /// </summary>
    private static T? FindAncestorOrSelf<T>(DependencyObject obj)
        where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T t) return t;
            obj = VisualTreeHelper.GetParent(obj);
        }

        return null;
    }

    /// <summary>
    /// Rounds Left and Top to the nearest grid cell after a drag operation, using the same
    /// <see cref="IAppSettings.GridSnapDistance"/> threshold as the live drag-time magnet (rather
    /// than an unconditional quantise with <c>threshold = grid</c>). Keeping the same threshold for
    /// both the live magnet and the drop-time snap avoids a visible "jump" on mouse-up: with an
    /// unconditional drop snap, a panel released just outside the live-drag magnet radius (but still
    /// off-grid) would jerk onto the nearest line instead of staying where the user visually left it.
    /// GridSize&lt;=1 means no effective snapping (every pixel is a valid position).
    /// </summary>
    /// <remarks>
    /// Anti-overlap on mouse-up (docs/PLANS/panel-anti-overlap.md §3.5): this is NOT a full
    /// <see cref="PanelCollision.ClampAgainstObstacles"/> pass — the live drag already clamped the
    /// panel against <see cref="_dragObstacles"/> on every frame, so it cannot already be inside a
    /// neighbour when this runs. The only way the grid snap below could create a NEW overlap is by
    /// nudging the already-clamped position by up to <see cref="IAppSettings.GridSnapDistance"/>
    /// pixels back toward a grid line. Each axis is therefore snapped independently and rolled back
    /// (kept at its pre-snap value) if — and only if — <see cref="PanelCollision.CreatesNewOverlap"/>
    /// says that specific axis's snap would introduce a NEW overlap; an overlap that already existed
    /// before this drag (R1 — a pre-feature saved layout) never blocks the snap, because
    /// <c>CreatesNewOverlap</c> only reports overlaps that are new relative to <c>before</c>.
    /// <para>
    /// <see cref="Resize_DragCompleted"/> also calls this method, but no drag gesture (and therefore
    /// no <see cref="TitleBar_MouseLeftButtonDown"/>) ran before it, so <see cref="_dragObstacles"/> is
    /// empty there and <c>CreatesNewOverlap</c> always returns <see langword="false"/> for both axes —
    /// the snap behaves exactly as it did before this feature for a resize. This is intentional (plan
    /// §3.6 — resize anti-overlap is explicitly out of scope for this batch, tracked in plan §9.1), not
    /// an oversight: do not "fix" it by populating <see cref="_dragObstacles"/> from resize handlers.
    /// </para>
    /// </remarks>
    private void SnapToGrid()
    {
        var grid = _appSettings.GridSize;
        if (grid <= 1) return;

        var threshold = _appSettings.GridSnapDistance;
        var width = ActualWidth;
        var height = ActualHeight;

        var snappedLeft = GridSnap.SnapAxis(Left, grid, threshold);
        var beforeX = new Rect(Left, Top, width, height);
        var afterX = new Rect(snappedLeft, Top, width, height);
        if (!PanelCollision.CreatesNewOverlap(beforeX, afterX, _dragObstacles))
            Left = snappedLeft;

        var snappedTop = GridSnap.SnapAxis(Top, grid, threshold);
        var beforeY = new Rect(Left, Top, width, height);
        var afterY = new Rect(Left, snappedTop, width, height);
        if (!PanelCollision.CreatesNewOverlap(beforeY, afterY, _dragObstacles))
            Top = snappedTop;
    }

    /// <summary>
    /// Snaps the window width to the nearest whole column in Grid mode.
    /// Accounts for <see cref="GridMetrics.FileListHPadding"/> so left/right margins are equal.
    /// No-op in List mode or when snap is disabled.
    /// </summary>
    private void SnapWidthToGrid()
    {
        if (_model is null || _model.ViewMode != PanelViewMode.Grid || !_appSettings.SnapResizeToIconGrid)
            return;

        var cell    = GridMetrics.CellSize(_model.IconSize);
        var content = Math.Max(0d, Width - GridMetrics.FileListHPadding);

        // Round the snapped width UP to a whole pixel (Math.Ceiling), never down: the actual Win32
        // window width is always an integer device pixel, and a fractional IconSize (sliders aren't
        // tick-snapped) or DPI/layout rounding can otherwise leave the real window a hair narrower than
        // the analytical Extent(k) it was just snapped to — which makes AdaptiveGridLayout.RowCapacity
        // see a shortfall and drop a whole column (finalization MAJOR M-1).
        Width = Math.Ceiling(Math.Max(GridMetrics.MinPanelWidth(_model.IconSize), AdaptiveGridLayout.SnapExtent(content, cell, AdaptiveGridLayout.DefaultMinGap, ResizeSnapEdgePadding(cell)) + GridMetrics.FileListHPadding));
    }

    /// <summary>
    /// Edge padding (<c>m</c>/<c>m_v</c>) used by resize-snap code (<see cref="Resize_DragDelta"/>,
    /// <see cref="SnapWidthToGrid"/>) to stay in agreement with <see cref="Controls.AdaptiveIconGridPanel"/>'s
    /// own layout math. Deliberately always uses <see cref="AdaptiveGridLayout.DefaultEdgePaddingRatio"/>
    /// rather than reading <c>AdaptiveIconGridPanel.EdgePaddingRatio</c> off the live panel — code-behind
    /// here has no reachable reference to the panel instance buried inside the <c>ListBox</c>'s
    /// <c>DataTemplate</c>. This is an intentional, documented compromise (plan §4, "R5"): if
    /// <c>MinGap</c>/<c>EdgePaddingRatio</c> are ever overridden in Templates.xaml's
    /// <c>ItemsPanelTemplate</c>, update this method (and the <see cref="AdaptiveGridLayout.DefaultMinGap"/>
    /// use above it) to match, or resize-snap will disagree with the actual on-screen column count.
    /// </summary>
    private static double ResizeSnapEdgePadding(double cell)
        => AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChildWindowModel.IconSize) or nameof(ChildWindowModel.ViewMode))
            SnapWidthToGrid();
    }
}
