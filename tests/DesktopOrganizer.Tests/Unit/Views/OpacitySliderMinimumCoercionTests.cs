using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// UX#2 (release-polish-1.0 visual review): verifies the WPF-behaviour assumption behind raising the
/// opacity <see cref="Slider"/>'s <c>Minimum</c> to <see cref="SurfaceComposer.MinSurfaceOpacity"/> (0.35) in
/// <c>PanelCustomizeWindow.xaml</c>/<c>NewPanelDefaultsSection.xaml</c> — that <see cref="RangeBase"/>'s own
/// <c>Value</c> coercion (clamping the DISPLAYED value to <c>Minimum</c> when a bound source starts out
/// below it, e.g. a legacy panel/default persisted at <c>0.2</c>) does NOT write the coerced value back
/// through a <c>TwoWay</c> binding to the source. If it did, merely opening the Customize window (or the
/// Settings window) for such a panel would silently bump its persisted opacity to 0.35 with no user action
/// — exactly the kind of silent persistence mutation CLAUDE.md's persistence-freeze section warns against.
/// This is empirically true (verified by running this test): the source keeps its original, sub-floor value;
/// only the slider's own visual position is clamped.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class OpacitySliderMinimumCoercionTests
{
    /// <summary>
    /// A <see cref="Slider"/> whose <see cref="RangeBase.Minimum"/> (0.35) is ABOVE a <c>TwoWay</c>-bound
    /// source's starting value (0.2) coerces its own displayed <see cref="RangeBase.Value"/> up to
    /// <see cref="RangeBase.Minimum"/>, but the bound source is left UNCHANGED — WPF's coercion here is a
    /// display-only clamp, not a source-mutating one.
    /// </summary>
    [Fact]
    public void SliderMinimumAboveBoundSourceValue_CoercesDisplayOnly_NeverWritesBackToSource()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var holder = new OpacityHolder { Opacity = 0.2 };
            var slider = new Slider { Minimum = SurfaceComposer.MinSurfaceOpacity, Maximum = 1.0 };
            slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(OpacityHolder.Opacity))
            {
                Source = holder,
                Mode = BindingMode.TwoWay,
            });

            var window = new Window { Content = slider, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();
            window.Close();

            Assert.Equal(SurfaceComposer.MinSurfaceOpacity, slider.Value);
            Assert.Equal(0.2, holder.Opacity);
        });
    }

    private sealed class OpacityHolder : INotifyPropertyChanged
    {
        private double _opacity;

        public event PropertyChangedEventHandler? PropertyChanged;

        public double Opacity
        {
            get => _opacity;
            set
            {
                _opacity = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Opacity)));
            }
        }
    }
}
