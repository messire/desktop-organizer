using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Behaviors;
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
    private readonly ChildWindowViewModel _viewModel;
    private readonly IWindowManager _windowManager;
    private readonly IDesktopIntegrationService _desktopIntegration;
    private readonly IAppSettings _appSettings;
    private ChildWindowModel? _model;

    private readonly PanelDragMoveBehavior _dragMoveBehavior;
    private readonly PanelResizeController _resizeController;
    private readonly RollUpBehavior _rollUpBehavior;
    private readonly PanelTitleRenameBehavior _titleRename;
    private readonly ListReorderGestureController<FileItem> _reorderController;
    private readonly HintPopupController _hintPopups;

    // Internal (rather than private) so WindowManager.CollectOtherPanelBounds can use it as the
    // target/final height of a rolled-up panel, instead of reading a mid-animation ActualHeight
    // (finalization MAJOR M-2) — see the constant's use there for details.
    internal const double TitleBarHeight = 32;

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

        // Constructed before _dragMoveBehavior: its onDoubleClick callback below captures
        // _rollUpBehavior, and assigning it first keeps that a real (non-nullable) reference
        // rather than a forward reference nullable analysis can't see through.
        _rollUpBehavior = new RollUpBehavior(
            this, _windowManager, ContentRow, FileListHost, ResizeOverlay, TitleBarHeight, () => _model);

        _dragMoveBehavior = new PanelDragMoveBehavior(
            this,
            _appSettings,
            _windowManager,
            getPanelId: () => _model?.Id,
            onDragGestureStarting: OnPanelGestureStarting,
            onDoubleClick: () => _rollUpBehavior.ToggleRollUp());

        _resizeController = new PanelResizeController(
            this,
            _appSettings,
            _windowManager,
            getModel: () => _model,
            onResizeGestureStarting: OnPanelGestureStarting,
            clearDragObstacles: _dragMoveBehavior.ClearDragObstacles,
            snapToGrid: _dragMoveBehavior.SnapToGrid,
            titleBarHeight: TitleBarHeight);

        _titleRename = new PanelTitleRenameBehavior(
            _windowManager, TitleText, TitleEditBox, () => _model, SyncToModel);

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
        _rollUpBehavior.ExpandedHeight = Height;

        _viewModel.LoadFromModel(model);

        // Re-snap width to the icon grid on load so persisted sizes stay aligned even after
        // the icon size was changed in a previous session.
        _resizeController.SnapWidthToGrid();

        model.PropertyChanged += OnModelPropertyChanged;

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
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelPropertyChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
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

    /// <summary>
    /// Shows the non-modal, auto-dismissing rename-error hint popup for <paramref name="outcome"/>, the
    /// moment <see cref="ChildWindowViewModel.RenameError"/> is set to a non-null value.
    /// </summary>
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
    /// </summary>
    private void ChildWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        if (_titleRename.IsEditingTitle) return;
        if (_viewModel.EditingItem is not null) return;
        if (_viewModel.SelectedFile is null) return;

        _viewModel.BeginRenameCommand.Execute(_viewModel.SelectedFile);
        e.Handled = true;
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
        => _dragMoveBehavior.OnTitleBarMouseLeftButtonDown(sender, e);

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        => _dragMoveBehavior.OnTitleBarMouseMove(sender, e);

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _dragMoveBehavior.OnTitleBarMouseLeftButtonUp(sender, e);

    private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
        => _dragMoveBehavior.OnTitleBarLostMouseCapture(sender, e);

    private void RollUpMenu_Click(object sender, RoutedEventArgs e) => _rollUpBehavior.ToggleRollUp();

    private void RenameMenu_Click(object sender, RoutedEventArgs e) => _titleRename.BeginRename();

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
        => _titleRename.OnTitleEditBoxKeyDown(sender, e);

    private void TitleEditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _titleRename.OnTitleEditBoxLostKeyboardFocus(sender, e);

    private void Resize_DragStarted(object sender, DragStartedEventArgs e)
        => _resizeController.OnResizeDragStarted(sender, e);

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
        => _resizeController.OnResizeDragDelta(sender, e);

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e)
        => _resizeController.OnResizeDragCompleted(sender, e);

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
    /// <see cref="ChildWindowViewModel.FileItems"/>.
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
    /// and survives virtualization, unlike walking <see cref="ListBox.Items"/>/children directly.
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

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChildWindowModel.IconSize) or nameof(ChildWindowModel.ViewMode))
            _resizeController.SnapWidthToGrid();
    }
}
