using System.Windows;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// ADR-009 amendment 2026-10-10 (O1): connects <see cref="ViewModels.ChildWindowViewModel.CloudDropHintRequested"/>
/// to the panel's existing non-blocking hint popup (<see cref="Behaviors.HintPopupController.ShowCloudDropHint"/>).
/// A tiny partial of its own, hooked from XAML's <c>Loaded</c> instead of the constructor, so the cloud hint
/// does not touch the large, frequently-edited <c>ChildWindow.xaml.cs</c>.
/// </summary>
public partial class ChildWindow
{
    private bool _cloudHintWired;

    /// <summary>
    /// Subscribes once (the window can be loaded more than once, e.g. after a Z-order/desktop re-attach).
    /// A drop can only reach a window that is already loaded, so subscribing here never misses a hint.
    /// </summary>
    private void ChildWindow_CloudHintLoaded(object sender, RoutedEventArgs e)
    {
        if (_cloudHintWired) return;

        _cloudHintWired = true;
        _viewModel.CloudDropHintRequested += (_, _) => _hintPopups.ShowCloudDropHint();
    }
}
