using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// A button that opens an attached <see cref="ContextMenu"/> below itself instead of raising a normal
/// <see cref="ButtonBase.Click"/> command (design-system-1x.md §2.5/§2.7, ADR-047 D-7/D-8) — the
/// "<c>[Ещё ▾]</c>" control <c>PanelsSection</c>/<c>PagesSection</c>/<c>BackupSection</c> (6.2b) and the
/// panel header's own overflow menu will use instead of the pre-6.2a code-behind
/// <c>OpenMenuOnLeftClick_Click</c> pattern.
/// </summary>
/// <remarks>
/// A lookless <see cref="ToggleButton"/> (the default style lives in
/// <c>Resources/Styles/Theme.Controls.xaml</c>, same <see cref="FrameworkElement.DefaultStyleKeyProperty"/>
/// convention <see cref="PanelChrome"/> already establishes) rather than a plain <see cref="Button"/>:
/// <see cref="ToggleButton.IsChecked"/> mirrors <see cref="Menu"/>'s own <see cref="ContextMenu.IsOpen"/>
/// state, which is exactly the semantics a <see cref="ToggleButton"/>'s built-in
/// <see cref="System.Windows.Automation.Peers.ToggleButtonAutomationPeer"/> already expresses (§2.5
/// catalogue: "UIA Toggle-pattern") — a plain <see cref="Button"/> would need a hand-rolled
/// <c>AutomationProperties</c> shim for the same information.
/// </remarks>
public class DropDownButton : ToggleButton
{
    /// <summary>Identifies the <see cref="Menu"/> dependency property.</summary>
    public static readonly DependencyProperty MenuProperty = DependencyProperty.Register(
        nameof(Menu),
        typeof(ContextMenu),
        typeof(DropDownButton),
        new PropertyMetadata(null, OnMenuChanged));

    /// <summary>
    /// Identifies the <see cref="ShowChevron"/> dependency property (UX-MEDIUM, 6.5 review round): the
    /// default style's chevron <see cref="TextBlock"/> collapses when this is <see langword="false"/> — for
    /// the <c>"⋯"</c> per-row overflow buttons (<c>PagesSection</c>/<c>BackupSection</c>), whose own
    /// <c>Content</c> IS already the conventional "more actions" glyph, a second chevron glyph right next to
    /// it read as a redundant, confusing "opens something else" affordance rather than the single
    /// already-established Windows "⋯ opens a menu" convention.
    /// </summary>
    public static readonly DependencyProperty ShowChevronProperty = DependencyProperty.Register(
        nameof(ShowChevron),
        typeof(bool),
        typeof(DropDownButton),
        new PropertyMetadata(true));

    static DropDownButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DropDownButton), new FrameworkPropertyMetadata(typeof(DropDownButton)));
    }

    /// <summary>Gets or sets the menu this button opens/closes. Its <see cref="ContextMenu.PlacementTarget"/>/<see cref="ContextMenu.Placement"/> are set automatically.</summary>
    public ContextMenu? Menu
    {
        get => (ContextMenu?)GetValue(MenuProperty);
        set => SetValue(MenuProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the default style draws its trailing chevron glyph — <see langword="true"/>
    /// (the Win11 "<c>[Label ▾]</c>" convention) unless a consumer opts out via <c>Theme.Button.Overflow</c>
    /// (see <see cref="ShowChevronProperty"/>'s own remarks).
    /// </summary>
    public bool ShowChevron
    {
        get => (bool)GetValue(ShowChevronProperty);
        set => SetValue(ShowChevronProperty, value);
    }

    /// <summary>
    /// Toggles <see cref="ToggleButton.IsChecked"/> (via the base implementation) and keeps
    /// <see cref="Menu"/>'s <see cref="ContextMenu.IsOpen"/> in lock-step — reached both by a real
    /// mouse/touch click and by <see cref="OnKeyDown"/>'s Enter/Alt+Down handling below, since both funnel
    /// through <see cref="ButtonBase"/>'s own Space-key handling into this same override.
    /// </summary>
    protected override void OnClick()
    {
        base.OnClick();

        if (Menu is not null)
            Menu.IsOpen = IsChecked == true;
    }

    /// <summary>Enter and Alt+Down both act like a click (design-system-1x.md §2.5: "Space/Enter/Alt+↓ открывает").</summary>
    /// <param name="e">The key-press event args.</param>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter || (e.Key == Key.System && e.SystemKey == Key.Down))
        {
            OnClick();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void OnMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (DropDownButton)d;

        if (e.OldValue is ContextMenu oldMenu)
        {
            oldMenu.Opened -= button.OnMenuOpened;
            oldMenu.Closed -= button.OnMenuClosed;
        }

        if (e.NewValue is ContextMenu newMenu)
        {
            newMenu.PlacementTarget = button;
            newMenu.Placement = PlacementMode.Bottom;
            newMenu.Opened += button.OnMenuOpened;
            newMenu.Closed += button.OnMenuClosed;
        }
    }

    /// <summary>Moves keyboard focus onto the first item once the menu has actually opened (§2.5: "фокус на первый пункт") — deferred to <see cref="DispatcherPriority.Input"/> since the menu's items are not yet realized synchronously inside the <see cref="ContextMenu.Opened"/> handler itself.</summary>
    private void OnMenuOpened(object? sender, RoutedEventArgs e)
    {
        var menu = Menu;
        if (menu is null || menu.Items.Count == 0) return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (menu.ItemContainerGenerator.ContainerFromIndex(0) is MenuItem firstItem)
                    firstItem.Focus();
            }));
    }

    /// <summary>Un-checks this button and restores keyboard focus to it once the menu closes (§2.5: "Esc... возвращает фокус"), whether the close came from Esc, an item click, or a click-away.</summary>
    private void OnMenuClosed(object? sender, RoutedEventArgs e)
    {
        IsChecked = false;
        Focus();
    }
}
