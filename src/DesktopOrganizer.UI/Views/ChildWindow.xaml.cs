using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
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
            _model.FilePaths = _viewModel.FileItems.Select(f => f.Path).ToList();
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
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <inheritdoc/>
    protected override void OnDrop(DragEventArgs e)
    {
        // Folder Portals are read-only — ignore drops.
        if (_viewModel.IsPortal) return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _viewModel.AddFiles(files);
        _windowManager.SaveWindowsState();
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
        if (e.ClickCount == 2)
        {
            ToggleRollUp();
            return;
        }

        DragMove();
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
        Height = collapsed ? TitleBarHeight : _expandedHeight;
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

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string direction }) return;

        if (direction.Contains("Right"))
            Width = Math.Max(MinPanelSize, Width + e.HorizontalChange);

        if (direction.Contains("Bottom"))
            Height = Math.Max(MinPanelSize, Height + e.VerticalChange);

        if (direction.Contains("Left"))
        {
            var newWidth = Math.Max(MinPanelSize, Width - e.HorizontalChange);
            Left += Width - newWidth;
            Width = newWidth;
        }

        if (direction.Contains("Top"))
        {
            var newHeight = Math.Max(MinPanelSize, Height - e.VerticalChange);
            Top += Height - newHeight;
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
    /// Rounds Left and Top to the nearest grid cell after a drag operation.
    /// GridSize=1 means no effective snapping (every pixel is a valid position).
    /// </summary>
    private void SnapToGrid()
    {
        var grid = _appSettings.GridSize;
        if (grid <= 1) return;

        Left = Math.Round(Left / grid) * grid;
        Top  = Math.Round(Top  / grid) * grid;
    }
}
