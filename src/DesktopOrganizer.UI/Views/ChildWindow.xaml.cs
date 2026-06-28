using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Core.Models;
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

    private const double TitleBarHeight = 32;
    private const double MinPanelSize = 120;

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

    // Quantisation steps computed once per drag gesture in Resize_DragStarted.
    private double _stepX;
    private double _stepY;

    // One-shot timer that auto-dismisses the Move-semantics hint popup; recreated per show.
    private DispatcherTimer? _moveHintTimer;

    // Manual title-bar drag state (replaces Window.DragMove(), which blocks the calling thread
    // and hands movement off to the native OS move loop until mouse-up — incompatible with
    // per-mouse-move magnetic grid snapping). See docs/PLANS/grid-snap-during-drag.md.
    private bool _isDragging;
    private System.Windows.Point _dragStartScreenCursor;
    private double _dragStartLeft;
    private double _dragStartTop;

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

        // Re-snap wrap-mode width on load so persisted sizes stay aligned even after
        // the icon size was changed in a previous session.
        SnapWrapWidthToGrid();

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
        _moveHintTimer?.Stop();
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
    }

    /// <summary>
    /// Live drag: on every mouse move while dragging, re-derive the raw (unsnapped) window
    /// position from the cursor delta since the drag started, then magnetically snap it to the
    /// icon grid via <see cref="GridSnap"/>. Replaces <c>Window.DragMove()</c>, which hands control
    /// to the native move loop and only returns on mouse-up — incompatible with per-frame snapping.
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

        var (left, top) = GridSnap.Snap(rawLeft, rawTop, _appSettings.GridSize, _appSettings.GridSnapDistance);
        Left = left;
        Top  = top;
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
        _windowManager.SaveWindowsState();
    }

    private void RollUpMenu_Click(object sender, RoutedEventArgs e) => ToggleRollUp();

    private void RenameMenu_Click(object sender, RoutedEventArgs e) => BeginRename();

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
        _rawWidth  = Width;
        _rawHeight = Height;
        _rawRight  = Left + Width;
        _rawBottom = Top  + Height;

        if (_appSettings.SnapResizeToIconGrid && _model is not null)
        {
            var cell = GridMetrics.CellSize(_model.IconSize);

            // List mode: BOTH axes are free-form (pixel-accurate). CellSize is the wrap-mode icon
            // *column* footprint (box width + margins, e.g. 84px for a 48px icon) — a horizontal
            // metric that has nothing to do with a list row's height. Quantising list height to it
            // rounds ordinary vertical drags to the same multiple, so the panel appears frozen
            // vertically while width (already free-form) resizes smoothly. Keep height free too.
            // Wrap mode: both dimensions snap to the icon-grid cell.
            if (_model.ViewMode == PanelViewMode.List)
            {
                _stepX = 1d; // pixel-accurate width for list panels
                _stepY = 1d; // pixel-accurate height for list panels
            }
            else
            {
                _stepX = cell;
                _stepY = cell;
            }
        }
        else
        {
            _stepX = 1d;
            _stepY = 1d;
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

        // Quantise content area (strip chrome before quantising, then re-add).
        // In Wrap mode, FileListHPadding (left+right margin of FileListHost) is chrome for
        // width just as TitleBarHeight is chrome for height — strip it before snapping.
        var isWrap   = _model?.ViewMode == PanelViewMode.Wrap;
        var hPadding = isWrap ? GridMetrics.FileListHPadding : 0d;
        var contentW = Math.Max(0d, _rawWidth - hPadding);
        var contentH = Math.Max(0d, _rawHeight - TitleBarHeight);

        var newWidth  = _stepX > 1d
            ? Math.Max(MinPanelSize, GridMetrics.Quantize(contentW,  _stepX) + hPadding)
            : Math.Max(MinPanelSize, contentW + hPadding);

        var newHeight = _stepY > 1d
            ? Math.Max(MinPanelSize, TitleBarHeight + GridMetrics.Quantize(contentH, _stepY))
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
    /// because the item templates inherit it.
    /// </summary>
    private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not FileItem item) return;

        _viewModel.OpenFile(item);
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
    private void SnapToGrid()
    {
        var grid = _appSettings.GridSize;
        if (grid <= 1) return;

        var threshold = _appSettings.GridSnapDistance;
        Left = GridSnap.SnapAxis(Left, grid, threshold);
        Top  = GridSnap.SnapAxis(Top,  grid, threshold);
    }

    /// <summary>
    /// Snaps the window width to the nearest whole column in Wrap mode.
    /// Accounts for <see cref="GridMetrics.FileListHPadding"/> so left/right margins are equal.
    /// No-op in List mode or when snap is disabled.
    /// </summary>
    private void SnapWrapWidthToGrid()
    {
        if (_model is null || _model.ViewMode != PanelViewMode.Wrap || !_appSettings.SnapResizeToIconGrid)
            return;

        var cell    = GridMetrics.CellSize(_model.IconSize);
        var content = Math.Max(0d, Width - GridMetrics.FileListHPadding);
        Width = Math.Max(MinPanelSize, GridMetrics.Quantize(content, cell) + GridMetrics.FileListHPadding);
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChildWindowModel.IconSize) or nameof(ChildWindowModel.ViewMode))
            SnapWrapWidthToGrid();
    }
}
