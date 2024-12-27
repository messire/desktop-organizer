using System.ComponentModel;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.ViewModels;

namespace DesktopOrganizer.UI.Views;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>
/// Interaction logic for ChildWindow.xaml.
/// </summary>
public partial class ChildWindow : Window
{
    private readonly ChildWindowViewModel _viewModel;

    private ChildWindowModel Model => DataContext as ChildWindowModel ?? throw new InvalidOperationException();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildWindow"/> class.
    /// </summary>
    /// <param name="model">The model for the child window.</param>
    /// <param name="iconProvider">Provider for fetching file icons.</param>
    public ChildWindow(ChildWindowModel model, IIconProvider iconProvider)
    {
        InitializeComponent();
        _viewModel = new ChildWindowViewModel(iconProvider);
        DataContext = model;
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.SaveFileList();
        base.OnClosing(e);
    }

    /// <inheritdoc/>
    protected override void OnDragEnter(DragEventArgs e)
        => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    /// <inheritdoc/>
    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _viewModel.AddFiles(files);
    }

    /// <inheritdoc/>
    protected override void OnLocationChanged(EventArgs e)
        => Model.Position = new Point((int)Left, (int)Top);

    /// <inheritdoc/>
    protected override void OnStateChanged(EventArgs e)
        => Model.WindowState = WindowState;

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        => Model.Size = new Size((int)Width, (int)Height);
}
