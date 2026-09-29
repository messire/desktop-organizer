using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Resources;

/// <summary>
/// H-1 HC fix (6.1 review round, docs/PLANS/design-system-1x.md §1.1 H-1/§2.2): before this fix,
/// <c>Settings.SegmentedGroup</c>'s track used <c>Theme.Control.Subtle.Hover</c>, which
/// <see cref="ThemeResourceBuilder"/> maps to <see cref="SystemColors.HighlightBrush"/> in High Contrast —
/// the SAME brush <c>Settings.SegmentedItem</c>'s own <c>IsChecked</c> trigger uses for the selected
/// segment's fill (via <c>Theme.Selection.Fill</c>), making an unselected track visually IDENTICAL to the
/// selected segment in HC (≈1:1 contrast, no way to tell which segment is active). The fix moved the track
/// to <c>Theme.Control.Fill</c> (<see cref="SystemColors.ControlBrush"/> in HC) — a distinct brush from
/// <see cref="SystemColors.HighlightBrush"/> in every stock Windows High Contrast theme.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SegmentedGroupHighContrastTests
{
    /// <summary>In High Contrast, the track's own background differs from a selected segment's background — the H-1 regression this fix closes.</summary>
    [Fact]
    public void HighContrast_TrackBackground_DiffersFromSelectedSegmentBackground()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.HighContrast);
            var app = isolation.App;

            var track = new Border { Style = (Style)app.Resources["Settings.SegmentedGroup"] };
            var selected = new RadioButton { Style = (Style)app.Resources["Settings.SegmentedItem"], Content = "A", IsChecked = true };
            track.Child = selected;

            var window = new Window { Content = track, ShowActivated = false, Left = -5000, Top = -5000, Width = 200, Height = 60 };
            window.Show();
            window.UpdateLayout();

            try
            {
                var trackBackground = Assert.IsType<SolidColorBrush>(track.Background).Color;

                var segment = FindVisualDescendant<Border>(selected, b => b.Name == "Segment");
                Assert.NotNull(segment);
                var segmentBackground = Assert.IsType<SolidColorBrush>(segment!.Background).Color;

                Assert.NotEqual(trackBackground, segmentBackground);
                Assert.Equal(SystemColors.ControlColor, trackBackground);
                Assert.Equal(SystemColors.HighlightColor, segmentBackground);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// M-4 fix: even outside High Contrast (Light/Dark), where <c>Theme.Control.Fill</c> and
    /// <c>Theme.Selection.Fill</c> can coincide (both pure white in Light), the selected segment carries an
    /// outline (<c>Theme.Stroke.Control</c> BorderBrush) the unselected track never does — the plan's own
    /// documented "или обводка" fallback for when the two fills alone don't separate.
    /// </summary>
    /// <param name="theme">The non-High-Contrast theme (<see cref="AppTheme.Light"/>/<see cref="AppTheme.Dark"/>) under test.</param>
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void SelectedSegment_HasOutline_UnselectedTrackDoesNot(AppTheme theme)
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(theme);
            var app = isolation.App;

            var selected = new RadioButton { Style = (Style)app.Resources["Settings.SegmentedItem"], Content = "A", IsChecked = true };
            var window = new Window { Content = selected, ShowActivated = false, Left = -5000, Top = -5000, Width = 100, Height = 40 };
            window.Show();
            window.UpdateLayout();

            try
            {
                var segment = FindVisualDescendant<Border>(selected, b => b.Name == "Segment");
                Assert.NotNull(segment);

                var borderBrush = segment!.BorderBrush;
                Assert.NotNull(borderBrush);
                Assert.NotEqual(Brushes.Transparent, borderBrush);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;

            var nested = FindVisualDescendant(child, predicate);
            if (nested is not null) return nested;
        }

        return null;
    }

    /// <summary>Swaps in a fresh, full (Colors→Templates, theme appended) <see cref="Application.Resources"/> chain for the duration of the test — same convention <c>ChromeStylesStaTests</c>'s own harness uses.</summary>
    private sealed class IsolatedAppResources : IDisposable
    {
        private readonly ResourceDictionary _original;

        public IsolatedAppResources(AppTheme theme)
        {
            App = WpfApplicationHost.EnsureApplication();
            _original = App.Resources;
            App.Resources = new ResourceDictionary();

            var merged = App.Resources.MergedDictionaries;
            foreach (var relativePath in new[]
            {
                "/Resources/Styles/Colors.xaml",
                "/Resources/Styles/Spacing.xaml",
                "/Resources/Styles/Brushes.xaml",
                "/Resources/Styles/Typography.xaml",
                "/Resources/Styles/Icons.xaml",
                "/Resources/Styles/Controls.xaml",
                "/Resources/Styles/Settings.xaml",
                "/Resources/Styles/Theme.Controls.xaml",
                "/Resources/Styles/Panels.xaml",
                "/Resources/Templates.xaml",
            })
            {
                merged.Add(new ResourceDictionary { Source = new Uri($"/DesktopOrganizer.UI;component{relativePath}", UriKind.Relative) });
            }

            TestThemeResources.AppendTheme(App, theme);
        }

        public Application App { get; }

        public void Dispose() => App.Resources = _original;
    }
}
