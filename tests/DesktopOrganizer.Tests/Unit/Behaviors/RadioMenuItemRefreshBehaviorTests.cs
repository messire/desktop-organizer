using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Commands;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// M-1 (docs/PLANS/design-system-1x.md phase 6.5 review round): <see cref="RadioMenuItemRefreshBehavior"/>
/// against a real, off-screen <see cref="ContextMenu"/>-hosted <see cref="MenuItem"/> — the same shape
/// <c>ChildWindow.xaml</c>'s IconLayoutPolicy/ContentFilter/SortMode radio groups use (<c>IsCheckable</c> +
/// <c>OneWay</c> <c>IsChecked</c> binding + a <c>Command</c> that is a no-op for an already-selected value).
/// A real <see cref="Window"/> host is needed for <see cref="MenuItem"/>'s own <c>OnClick</c> internals
/// (submenu-closing bookkeeping etc.) to run without throwing — the same reason
/// <c>AppThemeServiceTests</c>/<c>ThemedWindowBehaviorTests</c> host their own subjects in a real,
/// never-activated <see cref="Window"/> instead of a bare, disconnected element.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class RadioMenuItemRefreshBehaviorTests
{
    /// <summary>
    /// Without <see cref="RadioMenuItemRefreshBehavior"/> attached: clicking the ALREADY selected radio
    /// item flips <see cref="MenuItem.IsChecked"/> to <see langword="false"/> and leaves it there — WPF's
    /// own <c>MenuItem.OnClick</c> toggles it locally via <c>SetCurrentValue</c> before the <c>Command</c>
    /// runs, and the command is a genuine no-op here (setting an already-current value raises no
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/>), so nothing ever re-pulls the <c>OneWay</c>
    /// binding back to its real, still-selected value. This is the bug M-1 fixes — recorded here as a
    /// regression baseline, not a desired behavior.
    /// </summary>
    [Fact]
    public void Click_OnAlreadySelectedItem_WithoutBehavior_LeavesIsCheckedFalse_DemonstratesTheBug()
    {
        StaTestRunner.Run(() =>
        {
            var source = new RadioSource { Selected = true };
            using var harness = MenuItemHarness.Build(source, attachBehavior: false);

            Assert.True(harness.MenuItem.IsChecked);

            InvokeOnClick(harness.MenuItem);

            Assert.False(harness.MenuItem.IsChecked);
            Assert.True(source.Selected); // the source itself is untouched — only the visual glyph desynced.
        });
    }

    /// <summary>
    /// M-1 fix: with <see cref="RadioMenuItemRefreshBehavior.IsRadioItemProperty"/> attached, the SAME click
    /// re-pulls the <c>OneWay</c> binding right after WPF's own toggle — <see cref="MenuItem.IsChecked"/>
    /// stays <see langword="true"/>, and the bound <c>Command</c> still ran exactly once.
    /// </summary>
    [Fact]
    public void Click_OnAlreadySelectedItem_WithBehavior_KeepsIsCheckedTrue()
    {
        StaTestRunner.Run(() =>
        {
            var source = new RadioSource { Selected = true };
            using var harness = MenuItemHarness.Build(source, attachBehavior: true);

            Assert.True(harness.MenuItem.IsChecked);

            InvokeOnClick(harness.MenuItem);

            Assert.True(harness.MenuItem.IsChecked);
            Assert.Equal(1, harness.CommandExecutionCount);
        });
    }

    /// <summary>
    /// A genuine selection CHANGE (the source really does flip) still ends up checked, with the behavior
    /// attached — the fix must not interfere with the ordinary, already-correct case.
    /// </summary>
    [Fact]
    public void Click_SelectingAPreviouslyUnselectedItem_WithBehavior_EndsUpChecked()
    {
        StaTestRunner.Run(() =>
        {
            var source = new RadioSource { Selected = false };
            using var harness = MenuItemHarness.Build(source, attachBehavior: true);

            Assert.False(harness.MenuItem.IsChecked);

            InvokeOnClick(harness.MenuItem);

            Assert.True(harness.MenuItem.IsChecked);
            Assert.True(source.Selected);
            Assert.Equal(1, harness.CommandExecutionCount);
        });
    }

    /// <summary>
    /// Invokes <see cref="MenuItem"/>'s protected <c>OnClick</c> override directly — the same reflection
    /// approach <c>ChromeStylesStaTests.InvokeOnClick</c> already uses for
    /// <see cref="System.Windows.Controls.Primitives.ButtonBase"/> — then pumps the dispatcher down to
    /// <see cref="System.Windows.Threading.DispatcherPriority.ContextIdle"/> (below
    /// <see cref="System.Windows.Threading.DispatcherPriority.Render"/>). <c>MenuItem.OnClick</c>'s real
    /// internals (<c>OnClickImpl</c>) toggle <c>IsChecked</c> SYNCHRONOUSLY but queue the actual
    /// <see cref="MenuItem.Click"/> raise (and the bound <c>Command</c>'s execution, right after it) on a
    /// <c>Render</c>-priority <c>Dispatcher.BeginInvoke</c> — without pumping past that priority here,
    /// <see cref="RadioMenuItemRefreshBehavior"/>'s own <see cref="MenuItem.Click"/> handler would never run
    /// inside the test at all.
    /// </summary>
    private static void InvokeOnClick(MenuItem menuItem)
    {
        typeof(MenuItem).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(menuItem, null);
        menuItem.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>Minimal <see cref="INotifyPropertyChanged"/> source — only raises when the value actually changes, exactly like <c>ChildWindowModel</c>'s <c>SetField</c>-backed properties.</summary>
    private sealed class RadioSource : INotifyPropertyChanged
    {
        private bool _selected;

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Real, off-screen <see cref="Window"/> hosting a single checkable <see cref="MenuItem"/> inside a <see cref="ContextMenu"/> — see this class's own remarks for why a real window is needed.</summary>
    private sealed class MenuItemHarness : IDisposable
    {
        private readonly Window _window;

        private MenuItemHarness(Window window, MenuItem menuItem, RelayCommand command)
        {
            _window = window;
            MenuItem = menuItem;
            _command = command;
        }

        private readonly RelayCommand _command;

        public MenuItem MenuItem { get; }

        public int CommandExecutionCount { get; private set; }

        public static MenuItemHarness Build(RadioSource source, bool attachBehavior)
        {
            var menuItem = new MenuItem { IsCheckable = true };
            menuItem.SetBinding(
                MenuItem.IsCheckedProperty,
                new Binding(nameof(RadioSource.Selected)) { Source = source, Mode = BindingMode.OneWay });

            if (attachBehavior)
                RadioMenuItemRefreshBehavior.SetIsRadioItem(menuItem, true);

            var harnessBox = new MenuItemHarness?[1];
            var command = new RelayCommand(() =>
            {
                source.Selected = true;
                if (harnessBox[0] is { } h) h.CommandExecutionCount++;
            });
            menuItem.Command = command;

            var contextMenu = new ContextMenu();
            contextMenu.Items.Add(menuItem);

            var border = new Border { ContextMenu = contextMenu };
            var window = new Window { Content = border, ShowActivated = false, Left = -5000, Top = -5000, Width = 50, Height = 50 };
            window.Show();

            contextMenu.PlacementTarget = border;
            contextMenu.IsOpen = true;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            var harness = new MenuItemHarness(window, menuItem, command);
            harnessBox[0] = harness;
            return harness;
        }

        public void Dispose() => _window.Close();
    }
}
