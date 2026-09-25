using System.Windows.Controls;
using System.Windows.Interop;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.ViewModels;

using static DesktopOrganizer.UI.Views.VisualTreeWalker;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// System icons panel window. Chromeless, top-level, and deliberately <b>not</b> <c>Topmost</c>:
/// <c>SystemIconsPanelManager</c> keeps re-asserting, on a timer, the Z-order slot directly above the
/// shell desktop window (<c>Progman</c>), so the panel sinks back behind ordinary application windows
/// instead of floating over them, while still staying above the wallpaper and the desktop icons (this
/// revises ADR-019's original <c>Topmost="True"</c> trade-off). It is no longer re-parented into the
/// desktop's <c>WorkerW</c> layer — that mechanism made the window invisible to
/// <see cref="DesktopOrganizer.Services.Abstractions.IVirtualDesktopService"/>, which broke the
/// "show on all virtual desktops" setting. <c>SystemIconsPanelManager.OpenPanel</c> applies the
/// <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c> styles itself, strictly AFTER <see cref="Window.Show"/>
/// — applying them earlier (e.g. here, in <see cref="OnSourceInitialized"/>) would exclude the
/// window from virtual-desktop tracking forever (ADR-019 §R0.5).
/// </summary>
public partial class SystemIconsPanelWindow : Window
{
    private readonly SystemIconsPanelViewModel _viewModel;

    // Drag-reorder gesture detection (press/threshold/DoDragDrop) is shared with ChildWindow via
    // ListReorderGestureController (REFACTORING_PLAN.md §5.2/§6.5 dedup) — this window's own DragOver/
    // Drop/insertion-indicator hit-testing stays local (a plain vertical StackPanel walk, meaningfully
    // different from ChildWindow's view-mode-aware ListBoxItem-generator hit-test), so only Down/Move
    // are wired through the shared controller.
    private readonly ListReorderGestureController<SystemIconItemModel> _reorderController;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemIconsPanelWindow"/> class.
    /// </summary>
    /// <param name="viewModel">The panel's ViewModel, bound as this window's <see cref="FrameworkElement.DataContext"/>.</param>
    public SystemIconsPanelWindow(SystemIconsPanelViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        _reorderController = new ListReorderGestureController<SystemIconItemModel>(
            dragSource: IconsListBox,
            dataFormat: "SystemIconKind",
            resolveDragCandidate: ResolveReorderDragCandidate,
            createPayload: item => item.Kind,
            onGestureEnded: () => { });
    }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Load icons now that we have a window handle.
        _viewModel.LoadIcons();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the zero-based insertion index inside a vertical StackPanel given a drop position.
    /// </summary>
    private static int GetInsertionIndex(StackPanel panel, Point dropPos)
    {
        int index = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement fe)
            {
                index++;
                continue;
            }

            var childPos = fe.TranslatePoint(new Point(0, 0), panel);
            double midY = childPos.Y + (fe.ActualHeight / 2.0);
            if (dropPos.Y < midY) break;
            index++;
        }

        return index;
    }

    /// <summary>
    /// Returns the Y offset (relative to <paramref name="panel"/>) where the insertion
    /// line should be drawn. Matches the top edge of the item the cursor is above, or
    /// the bottom edge of the last item when the cursor is below all items.
    /// </summary>
    private static double GetInsertionYOffset(StackPanel panel, Point dropPos)
    {
        double lastBottom = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            var childPos = fe.TranslatePoint(new Point(0, 0), panel);
            double midY = childPos.Y + (fe.ActualHeight / 2.0);
            if (dropPos.Y < midY)
                return childPos.Y;
            lastBottom = childPos.Y + fe.ActualHeight;
        }

        return lastBottom;
    }

    // -------------------------------------------------------------------------
    // Right-click → native shell context menu
    // -------------------------------------------------------------------------
    private void IconButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SystemIconItemModel item)
        {
            // Convert WPF logical coordinates to physical screen pixels for the native menu.
            var source = PresentationSource.FromVisual(this);
            var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            var logicalPos = e.GetPosition(this);
            var screenX = (int)((Left + logicalPos.X) * dpiX);
            var screenY = (int)((Top + logicalPos.Y) * dpiY);

            var hwnd = new WindowInteropHelper(this).Handle;

            // Suppress the default WPF context menu on the button.
            e.Handled = true;

            _viewModel.ShowContextMenu(item.Kind, hwnd, screenX, screenY);
        }
    }

    // -------------------------------------------------------------------------
    // Drag-reorder: initiated from PreviewMouseLeftButtonDown + PreviewMouseMove
    // Also owns click-to-select / click-empty-space-to-deselect (ui-polishing-1 §5.4/§5.5, Block D).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Records the drag-reorder candidate for the upcoming <see cref="IconsListBox_PreviewMouseMove"/>,
    /// and clears the sidebar's own selection when the click lands on the panel's empty space (its own
    /// padding, past the last item) rather than on an item (ui-polishing-1 §5.5) — the sidebar
    /// equivalent of <c>ChildWindow.FileListHost_PreviewMouseLeftButtonDown</c>'s deselect-on-miss
    /// behavior. Deliberately does not set <c>e.Handled</c> anywhere: doing so would break the
    /// <see cref="ListBox"/>'s own built-in click-to-select handling, which still needs to run on the
    /// bubbling phase right after this <c>Preview</c> handler.
    /// </summary>
    private void IconsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _reorderController.OnPreviewMouseLeftButtonDown(sender, e);

    private void IconsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        => _reorderController.OnPreviewMouseMove(sender, e);

    /// <summary>
    /// Resolves the press-time drag-to-reorder candidate for <see cref="_reorderController"/>. Walks up
    /// to the ListBoxItem container, not a specific content element type — before the ItemsControl ->
    /// ListBox migration (§5.3) this looked for an ancestor Button, but the item template stopped
    /// containing one when it moved to a Border + MouseBinding (FIX 5, see IconBorder below), which
    /// silently made the drag candidate always null and drag-reorder dead code (ui-polishing-1 §5.2 —
    /// the bug this fixes). ListBoxItem is the right ancestor type post-migration, matching the same
    /// pattern already used in ChildWindow.xaml.cs.
    /// </summary>
    private SystemIconItemModel? ResolveReorderDragCandidate(DependencyObject source)
    {
        var container = FindAncestorOrSelf<ListBoxItem>(source);
        if (container is null)
        {
            _viewModel.SelectedItem = null;
            return null;
        }

        return container.DataContext as SystemIconItemModel;
    }

    private void IconsListBox_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SystemIconKind"))
        {
            e.Effects = DragDropEffects.None;
            HideInsertionIndicator();
        }
        else
        {
            e.Effects = DragDropEffects.Move;

            // Show insertion indicator at the computed insertion position.
            var panel = FindVisualChild<StackPanel>(IconsListBox);
            if (panel is not null)
            {
                var dropPos = e.GetPosition(panel);
                double yPos = GetInsertionYOffset(panel, dropPos);

                // Convert panel-relative Y to PanelGrid-relative Y.
                var panelOrigin = panel.TranslatePoint(new Point(0, 0), PanelGrid);
                InsertionIndicator.Margin = new Thickness(0, panelOrigin.Y + yPos - 1, 0, 0);
                InsertionIndicator.Visibility = Visibility.Visible;
            }
        }

        e.Handled = true;
    }

    private void IconsListBox_DragLeave(object sender, DragEventArgs e)
    {
        HideInsertionIndicator();
    }

    private void IconsListBox_Drop(object sender, DragEventArgs e)
    {
        HideInsertionIndicator();

        if (!e.Data.GetDataPresent("SystemIconKind")) return;

        var sourceKind = (SystemIconKind)e.Data.GetData("SystemIconKind");

        // Determine target index from mouse position within the ListBox.
        var panel = FindVisualChild<StackPanel>(IconsListBox);
        if (panel is null) return;

        var dropPos = e.GetPosition(panel);
        int newIndex = GetInsertionIndex(panel, dropPos);

        _viewModel.Reorder(sourceKind, newIndex);
        e.Handled = true;
    }

    private void HideInsertionIndicator()
    {
        InsertionIndicator.Visibility = Visibility.Collapsed;
    }

    // -------------------------------------------------------------------------
    // Per-item drag-over / drop for Recycle Bin file drop
    // -------------------------------------------------------------------------
    private void IconButton_DragOver(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe &&
            fe.DataContext is SystemIconItemModel item &&
            item.Kind == SystemIconKind.RecycleBin &&
            e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            // Allow reorder drag to fall through to the parent ListBox.
            e.Effects = e.Data.GetDataPresent("SystemIconKind")
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void IconButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not SystemIconItemModel item) return;

        if (item.Kind == SystemIconKind.RecycleBin &&
            e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _viewModel.SendToRecycleBin(paths, hwnd);
            e.Handled = true;
        }

        // else: reorder drops bubble to the parent ListBox handler
    }
}
