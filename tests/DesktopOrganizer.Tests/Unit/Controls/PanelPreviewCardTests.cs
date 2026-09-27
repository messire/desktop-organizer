using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// Unit tests for <see cref="PanelPreviewCard"/> (ADR-042): construction/layout never throws, and every
/// dependency property round-trips the value assigned to it — the shared "Preview" card used by both
/// <c>PanelCustomizeWindow</c> and Settings' "New panel defaults" section.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelPreviewCardTests
{
    /// <summary>Verifies that constructing and laying out the control with its default property values never throws.</summary>
    [Fact]
    public void Construction_WithDefaults_DoesNotThrow()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var exception = Record.Exception(() =>
            {
                var card = new PanelPreviewCard();
                var window = new System.Windows.Window
                {
                    Content = card,
                    ShowActivated = false,
                    Left = -5000,
                    Top = -5000,
                };
                window.Show();
                window.UpdateLayout();
                window.Close();
            });

            Assert.Null(exception);
        });
    }

    /// <summary>Verifies that every previewed property round-trips the assigned value, including at the edges of the surface-style/mode space.</summary>
    [Fact]
    public void Properties_RoundTrip()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var card = new PanelPreviewCard
            {
                AccentHex = "#3F7FD9",
                TextHex = "#172F4B",
                SurfaceStyle = PanelSurfaceStyle.Glass,
                SurfaceOpacity = 0.65,
                IconSize = 64,
                PreviewTitle = "Downloads",
            };

            Assert.Equal("#3F7FD9", card.AccentHex);
            Assert.Equal("#172F4B", card.TextHex);
            Assert.Equal(PanelSurfaceStyle.Glass, card.SurfaceStyle);
            Assert.Equal(0.65, card.SurfaceOpacity);
            Assert.Equal(64, card.IconSize);
            Assert.Equal("Downloads", card.PreviewTitle);
        });
    }

    /// <summary>Verifies that laying out the control with every <see cref="PanelSurfaceStyle"/> value never throws — the full <c>PanelSurfaceBrush</c>/<c>PanelChromeForegroundBrush</c> MultiBinding path actually runs.</summary>
    /// <param name="style">The surface style under test.</param>
    [Theory]
    [InlineData(PanelSurfaceStyle.Solid)]
    [InlineData(PanelSurfaceStyle.Ambient)]
    [InlineData(PanelSurfaceStyle.Glass)]
    public void Layout_EverySurfaceStyle_DoesNotThrow(PanelSurfaceStyle style)
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var exception = Record.Exception(() =>
            {
                var card = new PanelPreviewCard { SurfaceStyle = style, AccentHex = "#7A5BE7", TextHex = "#2B2350" };
                var window = new System.Windows.Window
                {
                    Content = card,
                    ShowActivated = false,
                    Left = -5000,
                    Top = -5000,
                };
                window.Show();
                window.UpdateLayout();
                window.Close();
            });

            Assert.Null(exception);
        });
    }

    /// <summary>
    /// M4 (release-polish-1.0 review round): the title reads the panel's own <see cref="PanelPreviewCard.TextHex"/>
    /// directly (the same <c>HexBrush</c> converter <c>ChildWindow.xaml</c>'s real title uses) — changing it
    /// changes the title's <see cref="TextBlock.Foreground"/> to the matching <see cref="SolidColorBrush"/>,
    /// rather than leaving it pinned to a fixed chrome token.
    /// </summary>
    [Fact]
    public void TitleForeground_FollowsTextHex()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var card = new PanelPreviewCard { TextHex = "#172F4B" };
            var window = new Window { Content = card, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            var titleText = FindLogicalDescendants<TextBlock>(card).Single(tb => tb.Name == "TitleText");
            var foreground = Assert.IsType<SolidColorBrush>(titleText.Foreground);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#172F4B")!, foreground.Color);

            card.TextHex = "#FFFFFF";
            window.UpdateLayout();

            var updatedForeground = Assert.IsType<SolidColorBrush>(titleText.Foreground);
            Assert.Equal(Colors.White, updatedForeground.Color);

            window.Close();
        });
    }

    /// <summary>
    /// M4: the header glyph row shows all FOUR of the real panel's header buttons (Sort, View, RollUp,
    /// Overflow) — the preview used to be missing the RollUp glyph.
    /// </summary>
    [Fact]
    public void HeaderGlyphRow_ShowsAllFourGlyphs()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var card = new PanelPreviewCard();
            var window = new Window { Content = card, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            var glyphTexts = FindLogicalDescendants<TextBlock>(card)
                .Where(tb => tb.FontFamily?.Source?.Contains("Segoe MDL2 Assets", StringComparison.Ordinal) == true)
                .Select(tb => tb.Text)
                .ToList();

            Assert.Contains(Application.Current.FindResource("Icon.Glyph.Sort") as string, glyphTexts);
            Assert.Contains(Application.Current.FindResource("Icon.Glyph.ViewGrid") as string, glyphTexts);
            Assert.Contains(Application.Current.FindResource("Icon.Glyph.RollUp") as string, glyphTexts);
            Assert.Contains(Application.Current.FindResource("Icon.Glyph.Overflow") as string, glyphTexts);

            window.Close();
        });
    }

    /// <summary>
    /// UX#4 (release-polish-1.0 visual review): the card shows a VISIBLE "Preview" header — before UX#4
    /// only <c>AutomationProperties.Name</c> named this card, with no on-screen label at all.
    /// </summary>
    [Fact]
    public void Card_ShowsVisiblePreviewHeader()
    {
        StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            var card = new PanelPreviewCard();
            var window = new Window { Content = card, ShowActivated = false, Left = -5000, Top = -5000 };
            window.Show();
            window.UpdateLayout();

            // "Preview" is Common_Preview_Header's default-culture (English) value — same literal-English-
            // string convention LocExtensionTests uses for {loc:Loc ...}-driven Text content.
            var headerBlock = FindLogicalDescendants<TextBlock>(card)
                .FirstOrDefault(tb => tb.Text == "Preview" && tb.Name != "TitleText");

            Assert.NotNull(headerBlock);

            window.Close();
        });
    }

    /// <summary>
    /// Recursively enumerates every logical-tree descendant of <paramref name="root"/> assignable to
    /// <typeparamref name="T"/> — same "walk the logical tree without requiring a full visual pass" shape
    /// <c>SizeEditorsWiringTests</c> already establishes.
    /// </summary>
    /// <typeparam name="T">The descendant type to find.</typeparam>
    /// <param name="root">The logical-tree root to search from (exclusive of itself).</param>
    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;

            if (node is T match) yield return match;

            foreach (var nested in FindLogicalDescendants<T>(node))
                yield return nested;
        }
    }
}
