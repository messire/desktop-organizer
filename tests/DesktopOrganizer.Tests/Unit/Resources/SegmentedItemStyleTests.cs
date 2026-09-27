using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Resources;

/// <summary>
/// Pins <c>Resources/Styles/Settings.xaml</c>'s <c>Settings.SegmentedItem</c> style (release-polish-1.0
/// review round M6): a disabled segment (e.g. the Sort segmented group while a panel is a Folder Portal,
/// <c>PanelCustomizeWindow.xaml</c>) must render visibly dimmed — before M6 this template had no
/// <c>IsEnabled</c> trigger at all, so a disabled <see cref="RadioButton"/> looked identical to an enabled
/// one.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SegmentedItemStyleTests
{
    /// <summary>A disabled <see cref="RadioButton"/> styled with <c>Settings.SegmentedItem</c> renders at reduced opacity.</summary>
    [Fact]
    public void DisabledSegment_RendersAtReducedOpacity()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var style = (Style)app.Resources["Settings.SegmentedItem"];

            var radioButton = new RadioButton { Style = style, Content = "Manual", IsEnabled = false };
            var window = new Window { Content = radioButton, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            Assert.Equal(0.45, radioButton.Opacity);

            window.Close();
        });
    }

    /// <summary>An enabled <see cref="RadioButton"/> styled with <c>Settings.SegmentedItem</c> renders at full opacity.</summary>
    [Fact]
    public void EnabledSegment_RendersAtFullOpacity()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var style = (Style)app.Resources["Settings.SegmentedItem"];

            var radioButton = new RadioButton { Style = style, Content = "Manual", IsEnabled = true };
            var window = new Window { Content = radioButton, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            Assert.Equal(1.0, radioButton.Opacity);

            window.Close();
        });
    }
}
