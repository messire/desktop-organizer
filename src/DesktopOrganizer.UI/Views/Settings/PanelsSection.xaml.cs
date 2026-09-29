using System.Windows.Controls;

namespace DesktopOrganizer.UI.Views.Settings;

/// <summary>
/// "Panels" settings section: the current page's panel list, plus Add/Customize and Align-all. The
/// per-panel actions (Customize…, Move to page ▸, Copy to page ▸, Remove) live on each row's right-click
/// context menu — contextual to the panel, the way the panel's own header menu is — rather than in a
/// separate "More" drop-down next to the list. The page selector replaces the old top page-tab strip.
/// </summary>
/// <remarks>
/// Phase 6.3 (design-system-1x.md §2.8, ADR-047 D-9): this class used to subscribe to
/// <c>MainWindowViewModel.PropertyChanged</c> for its own <c>PanelPageStatusMessage</c> live-region
/// announcement (<c>PanelPageStatusText</c>, since removed from <c>PanelsSection.xaml</c>) — that status
/// now lives on the app-wide <c>MainWindow.xaml</c> InfoBar host instead, which raises its own
/// <see cref="System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged"/> via
/// <c>InfoBar.AutomationProperties.LiveSetting="Polite"</c> without this section needing any subscription
/// of its own.
/// </remarks>
public partial class PanelsSection : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="PanelsSection"/> class.</summary>
    public PanelsSection() => InitializeComponent();

    /// <summary>
    /// Selects the row whose context menu is about to open. The row menu's Move/Copy/Remove commands act
    /// on <c>MainWindowViewModel.SelectedPanel</c>, not on the row — and the row that owns the menu is
    /// not always the selected one: a right-click does select it (<see cref="ListBoxItem"/>'s own
    /// right-button handling), but Shift+F10 / the Menu key open the menu of the <i>focused</i> row, which
    /// Tab-into-list and Ctrl+Arrow leave unselected. <c>ContextMenuOpening</c> fires for every one of
    /// those routes, so this is the single place that keeps "the row I opened the menu on" and "the panel
    /// the menu acts on" the same panel.
    /// </summary>
    /// <param name="sender">The <see cref="ListBoxItem"/> whose menu is opening.</param>
    /// <param name="e">Not marked handled: the menu still has to open.</param>
    private void PanelRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBoxItem row) row.IsSelected = true;
    }
}
