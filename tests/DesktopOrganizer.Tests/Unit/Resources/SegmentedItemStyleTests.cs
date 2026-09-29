using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Resources;

/// <summary>
/// Pins <c>Resources/Styles/Settings.xaml</c>'s <c>Settings.SegmentedItem</c> style. Originally
/// (release-polish-1.0 review round M6) a disabled segment (e.g. the Sort segmented group while a panel is
/// a Folder Portal, <c>PanelCustomizeWindow.xaml</c>) dimmed via a blanket <see cref="UIElement.Opacity"/>
/// trigger; design-system-1x phase 6.2a (docs/PLANS/design-system-1x.md §2.2 D-6 "disabled ≠ Opacity")
/// replaced that with a <c>Theme.Text.Disabled</c> <see cref="Control.Foreground"/> token instead, and gave
/// the style its own <see cref="FrameworkElement.FocusVisualStyle"/> (H-3) rather than the pre-6.2a bespoke
/// 1px-border <c>IsKeyboardFocused</c> template trigger.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SegmentedItemStyleTests
{
    /// <summary>A disabled <see cref="RadioButton"/> styled with <c>Settings.SegmentedItem</c> renders at full opacity (no blanket-Opacity disable, D-6) with a dimmed <c>Theme.Text.Disabled</c> foreground.</summary>
    [Fact]
    public void DisabledSegment_RendersAtFullOpacity_WithDisabledForeground()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var style = (Style)app.Resources["Settings.SegmentedItem"];
            var disabledBrush = (Brush)app.Resources["Theme.Text.Disabled"];

            var radioButton = new RadioButton { Style = style, Content = "Manual", IsEnabled = false };
            var window = new Window { Content = radioButton, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            Assert.Equal(1.0, radioButton.Opacity);
            Assert.Equal(disabledBrush, radioButton.Foreground);

            window.Close();
        });
    }

    /// <summary>An enabled <see cref="RadioButton"/> styled with <c>Settings.SegmentedItem</c> renders at full opacity with its inherited (non-disabled) foreground.</summary>
    [Fact]
    public void EnabledSegment_RendersAtFullOpacity_WithoutDisabledForeground()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var style = (Style)app.Resources["Settings.SegmentedItem"];
            var disabledBrush = (Brush)app.Resources["Theme.Text.Disabled"];

            var radioButton = new RadioButton { Style = style, Content = "Manual", IsEnabled = true };
            var window = new Window { Content = radioButton, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            Assert.Equal(1.0, radioButton.Opacity);
            Assert.NotEqual(disabledBrush, radioButton.Foreground);

            window.Close();
        });
    }

    /// <summary>H-3: <c>Settings.SegmentedItem</c>/<c>.Compact</c> both set their own <c>Theme.FocusVisual</c> — no longer <see langword="null"/>/the stock pin-striped default.</summary>
    [Fact]
    public void SegmentedItem_AndCompactVariant_HaveOwnFocusVisualStyle()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var focusVisual = (Style)app.Resources["Theme.FocusVisual"];

            var segment = new RadioButton { Style = (Style)app.Resources["Settings.SegmentedItem"] };
            var compactSegment = new RadioButton { Style = (Style)app.Resources["Settings.SegmentedItem.Compact"] };
            var window = new Window
            {
                Content = new StackPanel { Children = { segment, compactSegment } },
                ShowActivated = false,
                Left = -5000,
                Top = -5000,
            };
            window.Show();
            window.UpdateLayout();

            Assert.Same(focusVisual, segment.FocusVisualStyle);
            Assert.Same(focusVisual, compactSegment.FocusVisualStyle);

            window.Close();
        });
    }
}
