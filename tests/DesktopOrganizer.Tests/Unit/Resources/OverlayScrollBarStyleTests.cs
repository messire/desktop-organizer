using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Resources;

/// <summary>
/// Pins the custom overlay scrollbar (<c>Resources/Styles/Controls.xaml</c>'s
/// <c>Control.ScrollBar.Overlay</c> / <c>Control.ScrollBar.Thumb</c> / <c>Control.ScrollBar.TrackButton</c>,
/// docs/prompts/scrollbar-pack) against its own spec: geometry tokens that are hand-written derivations of
/// each other stay in sync (the same drift-guard convention <c>PanelMetricsTests</c> uses for
/// <c>Radius.Panel.Inner</c>), a REAL <see cref="ScrollBar"/> styled with it materializes a stock
/// <see cref="Track"/> (<c>PART_Track</c>) whose own arrange math agrees with the Thumb's minimum length,
/// the mirrored horizontal template is what an <see cref="Orientation.Horizontal"/> bar gets, no render
/// effect sneaks into the tree, and — the mechanism both panel windows rely on — a Grid-scoped implicit
/// style reaches the <see cref="ScrollBar"/> a <see cref="ListBox"/>'s own default template creates
/// across the template boundary.
/// </summary>
public class OverlayScrollBarStyleTests
{
    private const string SpacingUri = "/DesktopOrganizer.UI;component/Resources/Styles/Spacing.xaml";

    /// <summary>
    /// <c>ScrollBar.ThumbInset.Vertical/Horizontal</c> are the hand-written literal
    /// <c>(ScrollBar.Width - ScrollBar.ThumbThickness) / 2</c> per side and <c>Radius.ScrollBarThumb</c> is half
    /// the pill thickness (a true capsule). XAML has no arithmetic markup extension, so this test is the
    /// only thing keeping those derivations honest. (The template-local Track basis literal in
    /// <c>Controls.xaml</c> — 2 x <c>ScrollBar.ThumbMinLength</c> — is pinned by the real-layout tests below.)
    /// </summary>
    [Fact]
    public void GeometryTokens_DerivedLiteralsStayInSync()
    {
        var (width, thickness, minLength, insetV, insetH, radius) = StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();
            var spacing = new ResourceDictionary { Source = new Uri(SpacingUri, UriKind.Relative) };
            return (
                (double)spacing["ScrollBar.Width"],
                (double)spacing["ScrollBar.ThumbThickness"],
                (double)spacing["ScrollBar.ThumbMinLength"],
                (Thickness)spacing["ScrollBar.ThumbInset.Vertical"],
                (Thickness)spacing["ScrollBar.ThumbInset.Horizontal"],
                (CornerRadius)spacing["Radius.ScrollBarThumb"]);
        });

        Assert.Equal(width, insetV.Left + thickness + insetV.Right);
        Assert.Equal(0d, insetV.Top + insetV.Bottom);
        Assert.Equal(width, insetH.Top + thickness + insetH.Bottom);
        Assert.Equal(0d, insetH.Left + insetH.Right);
        Assert.Equal(insetV.Left, insetV.Right);
        Assert.Equal(insetH.Top, insetH.Bottom);

        Assert.Equal(thickness / 2d, radius.TopLeft);
        Assert.Equal(radius.TopLeft, radius.TopRight);
        Assert.Equal(radius.TopLeft, radius.BottomLeft);
        Assert.Equal(radius.TopLeft, radius.BottomRight);
        Assert.InRange(minLength, 24d, 48d);
        Assert.InRange(width, 12d, 16d);
        Assert.InRange(thickness, 8d, 10d);
    }

    /// <summary>
    /// A vertical <see cref="ScrollBar"/> styled with <c>Control.ScrollBar.Overlay</c>, given an extreme
    /// extent (so the proportional thumb would be sub-pixel), lays out as: the whole bar is
    /// <c>ScrollBar.Width</c> wide, <c>PART_Track</c> is a real direction-reversed <see cref="Track"/>, both
    /// RepeatButtons carry no content (no arrow glyphs), and the Thumb is EXACTLY
    /// <c>ScrollBar.ThumbMinLength</c> tall by the Track's OWN arrange math (the two RepeatButtons plus the
    /// Thumb tile the full track length) — not merely a taller pill rendered over a shorter slot.
    /// The visible pill is <c>ScrollBar.ThumbThickness</c> wide, capsule-rounded, filled with a frozen
    /// brush colour-identical to <c>Brush.ScrollBar.Thumb</c> — docs/PLANS/color-presets-and-size-step.md
    /// WP-B "ADDITIONAL" made the Thumb's actual fill luminance-adaptive
    /// (<c>PanelChromeForegroundBrushConverter</c>, keyed off the inherited <c>PanelTheme.Accent</c>); a
    /// bare <see cref="ScrollBar"/> with no panel ancestor setting that attached property inherits
    /// <c>PanelTheme.Accent</c>'s own DARK default (<c>AccentPalette.Neutral</c>), which resolves to the
    /// exact same colour <c>Brush.ScrollBar.Thumb</c> always had — so this is no longer the SAME shared
    /// instance, but is still frozen and still byte-identical. Nothing in the tree has a render
    /// <see cref="UIElement.Effect"/>.
    /// </summary>
    [Fact]
    public void VerticalScrollBar_MaterializesTrackWithCapsuleThumbAtMinimumLength()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var width = (double)app.Resources["ScrollBar.Width"];
            var thickness = (double)app.Resources["ScrollBar.ThumbThickness"];
            var minLength = (double)app.Resources["ScrollBar.ThumbMinLength"];
            var radius = (CornerRadius)app.Resources["Radius.ScrollBarThumb"];
            var thumbColor = ((SolidColorBrush)app.Resources["Brush.ScrollBar.Thumb"]).Color;

            const double trackLength = 300d;
            var scrollBar = CreateStyledScrollBar(app, Orientation.Vertical);
            scrollBar.Measure(new Size(double.PositiveInfinity, trackLength));
            scrollBar.Arrange(new Rect(0, 0, scrollBar.DesiredSize.Width, trackLength));
            scrollBar.UpdateLayout();

            Assert.Equal(width, scrollBar.ActualWidth);
            Assert.Equal(trackLength, scrollBar.ActualHeight);

            var track = Assert.IsType<Track>(scrollBar.Template.FindName("PART_Track", scrollBar));
            Assert.True(track.IsDirectionReversed, "A vertical Track must be direction-reversed (offset 0 at the top).");
            Assert.NotNull(track.Thumb);
            Assert.NotNull(track.DecreaseRepeatButton);
            Assert.NotNull(track.IncreaseRepeatButton);
            Assert.Null(track.DecreaseRepeatButton.Content);
            Assert.Null(track.IncreaseRepeatButton.Content);
            Assert.Same(ScrollBar.PageUpCommand, track.DecreaseRepeatButton.Command);
            Assert.Same(ScrollBar.PageDownCommand, track.IncreaseRepeatButton.Command);

            Assert.Equal(minLength, track.Thumb.ActualHeight);
            Assert.Equal(
                track.ActualHeight,
                track.DecreaseRepeatButton.ActualHeight + track.Thumb.ActualHeight + track.IncreaseRepeatButton.ActualHeight,
                precision: 3);

            var pill = FindVisualChildByName<Border>(track.Thumb, "Pill");
            Assert.NotNull(pill);
            Assert.Equal(thickness, pill.ActualWidth);
            Assert.Equal(minLength, pill.ActualHeight);
            Assert.Equal(radius, pill.CornerRadius);
            var pillBrush = Assert.IsType<SolidColorBrush>(pill.Background);
            Assert.Equal(thumbColor, pillBrush.Color);
            Assert.True(pillBrush.IsFrozen, "The Thumb's resolved fill must be a frozen brush.");

            Assert.Empty(ElementsWithEffect(scrollBar));
        });
    }

    /// <summary>
    /// The <c>Orientation="Horizontal"</c> trigger swaps in the mirrored template: the bar is
    /// <c>ScrollBar.Width</c> TALL (width Auto), the Track is not direction-reversed, its RepeatButtons page
    /// left/right, and the Thumb is <c>ScrollBar.ThumbMinLength</c> WIDE with a <c>ScrollBar.ThumbThickness</c>
    /// tall pill — so a horizontal bar can never fall through to the vertical Track.
    /// </summary>
    [Fact]
    public void HorizontalScrollBar_UsesMirroredTemplate()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var width = (double)app.Resources["ScrollBar.Width"];
            var thickness = (double)app.Resources["ScrollBar.ThumbThickness"];
            var minLength = (double)app.Resources["ScrollBar.ThumbMinLength"];

            const double trackLength = 300d;
            var scrollBar = CreateStyledScrollBar(app, Orientation.Horizontal);
            scrollBar.Measure(new Size(trackLength, double.PositiveInfinity));
            scrollBar.Arrange(new Rect(0, 0, trackLength, scrollBar.DesiredSize.Height));
            scrollBar.UpdateLayout();

            Assert.Equal(width, scrollBar.ActualHeight);
            Assert.Equal(trackLength, scrollBar.ActualWidth);

            var track = Assert.IsType<Track>(scrollBar.Template.FindName("PART_Track", scrollBar));
            Assert.False(track.IsDirectionReversed, "A horizontal Track must not be direction-reversed.");
            Assert.Same(ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command);
            Assert.Same(ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);

            Assert.Equal(minLength, track.Thumb.ActualWidth);
            Assert.Equal(
                track.ActualWidth,
                track.DecreaseRepeatButton.ActualWidth + track.Thumb.ActualWidth + track.IncreaseRepeatButton.ActualWidth,
                precision: 3);

            var pill = FindVisualChildByName<Border>(track.Thumb, "Pill");
            Assert.NotNull(pill);
            Assert.Equal(thickness, pill.ActualHeight);
            Assert.Equal(minLength, pill.ActualWidth);
        });
    }

    /// <summary>
    /// The disabled state is a plain Style trigger on the <see cref="ScrollBar"/>'s own
    /// <see cref="UIElement.IsEnabled"/> (what a <see cref="ScrollViewer"/> flips when there is nothing to
    /// scroll), dimming the whole bar to <c>ScrollBar.DisabledOpacity</c> — not a Thumb-level trigger, which
    /// would misfire because the template's Track is disabled whenever the pointer is not over the bar.
    /// </summary>
    [Fact]
    public void DisabledScrollBar_DimsToDisabledOpacityToken()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var disabledOpacity = (double)app.Resources["ScrollBar.DisabledOpacity"];

            var scrollBar = CreateStyledScrollBar(app, Orientation.Vertical);
            scrollBar.Measure(new Size(double.PositiveInfinity, 100d));
            scrollBar.Arrange(new Rect(0, 0, scrollBar.DesiredSize.Width, 100d));
            Assert.Equal(1d, scrollBar.Opacity);

            scrollBar.IsEnabled = false;
            scrollBar.UpdateLayout();

            Assert.Equal(disabledOpacity, scrollBar.Opacity);
            Assert.InRange(disabledOpacity, 0.2, 0.5);
        });
    }

    /// <summary>
    /// The application mechanism itself: <c>ChildWindow.xaml</c>'s <c>PanelRoot</c> and
    /// <c>SystemIconsPanelWindow.xaml</c>'s root Border each declare an IMPLICIT <c>ScrollBar</c> style
    /// (<c>BasedOn="{StaticResource Control.ScrollBar.Overlay}"</c>) in their own Resources, trusting WPF to
    /// find an implicit <see cref="Control"/> style from inside the ListBox's default ControlTemplate's
    /// ScrollViewer's own template. This reproduces exactly that: a Grid whose Resources carry the same
    /// implicit style, hosting a stock-templated <see cref="ListBox"/> with enough items to scroll, must
    /// end up with a vertical <see cref="ScrollBar"/> that is <c>ScrollBar.Width</c> wide and templated with
    /// our <c>PART_Track</c> — i.e. the system scrollbar never leaks through.
    /// </summary>
    [Fact]
    public void ImplicitStyleScopedToAncestor_ReachesListBoxInternalScrollBar()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var width = (double)app.Resources["ScrollBar.Width"];
            var overlay = (Style)app.Resources["Control.ScrollBar.Overlay"];

            var host = new Grid();
            host.Resources.Add(typeof(ScrollBar), new Style(typeof(ScrollBar), overlay));

            var listBox = new ListBox
            {
                ItemsSource = Enumerable.Range(0, 200).Select(i => $"item {i}").ToList(),
                Width = 120d,
                Height = 100d,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);

            // Same "smallest real template it needs, skip the OS theme" convention as
            // ResourceDictionaryMergeTests.ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow:
            // a real, DEFAULT-templated ScrollViewer (the element that actually creates the ScrollBar under
            // test) hosting a real ItemsPresenter over a plain StackPanel — the OS/theme default ListBox
            // template does not reliably realize containers in this headless host.
            var itemsPresenterFactory = new FrameworkElementFactory(typeof(ItemsPresenter));
            var scrollViewerFactory = new FrameworkElementFactory(typeof(ScrollViewer));
            scrollViewerFactory.SetValue(
                ScrollViewer.HorizontalScrollBarVisibilityProperty,
                new TemplateBindingExtension(ScrollViewer.HorizontalScrollBarVisibilityProperty));
            scrollViewerFactory.SetValue(
                ScrollViewer.VerticalScrollBarVisibilityProperty,
                new TemplateBindingExtension(ScrollViewer.VerticalScrollBarVisibilityProperty));
            scrollViewerFactory.AppendChild(itemsPresenterFactory);
            listBox.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scrollViewerFactory };
            listBox.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
            host.Children.Add(listBox);

            host.Measure(new Size(120d, 100d));
            host.Arrange(new Rect(0, 0, 120d, 100d));
            host.UpdateLayout();

            var scrollBar = FindVisualChildren<ScrollBar>(listBox).SingleOrDefault(sb => sb.Orientation == Orientation.Vertical);
            Assert.NotNull(scrollBar);
            Assert.Equal(Visibility.Visible, scrollBar.Visibility);
            Assert.Same(overlay, scrollBar.Style.BasedOn);
            Assert.Equal(width, scrollBar.ActualWidth);
            Assert.IsType<Track>(scrollBar.Template.FindName("PART_Track", scrollBar));
            Assert.Empty(ElementsWithEffect(scrollBar));
        });
    }

    private static ScrollBar CreateStyledScrollBar(Application app, Orientation orientation)
        => new()
        {
            Style = (Style)app.Resources["Control.ScrollBar.Overlay"],
            Orientation = orientation,
            Minimum = 0d,
            Maximum = 100_000d,
            ViewportSize = 1d,
            Value = 0d,
        };

    private static IEnumerable<UIElement> ElementsWithEffect(DependencyObject root)
        => FindVisualChildren<UIElement>(root).Where(element => element.Effect is not null);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                yield return typed;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name)
        where T : FrameworkElement
        => FindVisualChildren<T>(parent).FirstOrDefault(element => element.Name == name);
}
