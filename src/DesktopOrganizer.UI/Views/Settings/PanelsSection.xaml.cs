using System.Windows;
using System.Windows.Controls;

namespace DesktopOrganizer.UI.Views.Settings;

/// <summary>
/// "Panels" settings section: the current page's panel list, plus Add/Customize/Move-to-page/Remove/
/// Align-all. The page selector replaces the old top page-tab strip.
/// </summary>
public partial class PanelsSection : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="PanelsSection"/> class.</summary>
    public PanelsSection() => InitializeComponent();

    /// <summary>
    /// Opens the "Move to page" button's <see cref="FrameworkElement.ContextMenu"/> on left-click — a pure view
    /// mechanic (a <see cref="ContextMenu"/> otherwise only opens on right-click), not app logic.
    /// </summary>
    private void MoveToPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
