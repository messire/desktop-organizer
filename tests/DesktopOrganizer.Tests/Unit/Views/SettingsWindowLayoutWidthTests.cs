using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.Localization;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// T-9 (docs/PLANS/design-system-1x.md §5/§3 phase 6.2b, H-4): best-effort regression coverage for
/// <c>MainWindow.xaml</c>'s sidebar nav labels and <c>PanelsSection</c>'s Variant A command row (§2.7 D-8)
/// at the window's own default size, in both <c>en</c> and <c>ru-RU</c> — the two dimensions §1.1's own H-4
/// finding flagged ("сайдбар 190 DIP и ряд кнопок Panels переносятся/обрезаются при Body ≥ 13"). Measures
/// real, styled elements built from the SAME catalogue/RESX data the app itself uses
/// (<see cref="SettingsSectionCatalog"/>, <see cref="LocalizationResources"/>) rather than re-parsing
/// <c>MainWindow.xaml</c>/<c>PanelsSection.xaml</c> wholesale — those already have their own construction
/// coverage (<c>SettingsSectionsInitializeTests</c>); this class only pins LAYOUT, at the phase 6.2b sizes
/// (sidebar 220 DIP column, 760 DIP window, Font.Size.Body sidebar/command-row text).
/// </summary>
/// <remarks>
/// H-4 finding: the sidebar column (220 DIP) is NOT wide enough for every ru-RU label at Font.Size.Body
/// without any mitigation (e.g. "Расположение и привязка" alone measures ≈200 DIP against ≈184 DIP
/// available) — the plan's own accepted fallback for that case is "TextTrimming + тултип" (§3 6.2b), not a
/// wider column for every label. <see cref="SidebarItemTemplate_LabelColumn_TrimsWithEllipsisAndCarriesToolTip"/>
/// pins that mitigation is actually wired into <c>Settings.SidebarItemTemplate</c>, rather than re-asserting
/// a "never overflows" bound this phase deliberately does not guarantee for every possible label.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SettingsWindowLayoutWidthTests
{
    // MainWindow.xaml: Width="760", sidebar column 220; ContentControl Margin="20" (both sides);
    // Settings.CardBorder-equivalent SettingsCard Padding Theme.Card.Padding = 16,12 (both sides).
    private const double WindowWidth = 760;
    private const double SidebarColumnWidth = 220;
    private const double ContentMargin = 20 * 2;
    private const double CardHorizontalPadding = 16 * 2;
    private const double AvailablePanelsRowWidth = WindowWidth - SidebarColumnWidth - ContentMargin - CardHorizontalPadding;

    /// <summary>The two shipped UI cultures (docs/PLANS/localization.md) — <c>[MemberData]</c> source for both theories below.</summary>
    public static TheoryData<string> Cultures => ["en", "ru"];

    /// <summary>
    /// H-4 mitigation (design-system-1x.md §3 6.2b): <c>Settings.SidebarItemTemplate</c>'s label lives in a
    /// Grid "*" column (never a Horizontal StackPanel, which gives a child unlimited measure width and so
    /// never actually constrains it) — <see cref="TextBlock.TextTrimming"/> is
    /// <see cref="TextTrimming.CharacterEllipsis"/> and a <see cref="FrameworkElement.ToolTip"/> carries the
    /// un-truncated text, so a ru-RU label too long for the 220 DIP column degrades to "…" instead of
    /// silently drawing past the column.
    /// </summary>
    [Fact]
    public void SidebarItemTemplate_LabelColumn_TrimsWithEllipsisAndCarriesToolTip()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var template = (DataTemplate)app.Resources["Settings.SidebarItemTemplate"];

            var entry = SettingsSectionCatalog.Primary[0];
            var root = (FrameworkElement)template.LoadContent();
            root.DataContext = entry;

            var window = new Window { Content = root, ShowActivated = false, Left = -5000, Top = -5000, Width = SidebarColumnWidth, Height = 40 };
            window.Show();
            window.UpdateLayout();

            try
            {
                var grid = Assert.IsType<Grid>(root);
                Assert.Equal(2, grid.ColumnDefinitions.Count);
                Assert.Equal(GridLength.Auto, grid.ColumnDefinitions[0].Width);
                Assert.True(grid.ColumnDefinitions[1].Width.IsStar);

                var label = FindLabel(grid);
                Assert.NotNull(label);
                Assert.Equal(TextTrimming.CharacterEllipsis, label!.TextTrimming);
                Assert.False(string.IsNullOrEmpty(label.ToolTip as string));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>PanelsSection's command row (Add panel [primary] · Customize…) fits one line at the window's own default content width, in both cultures — the pre-6.2b 5-button row wrapped to two lines in ru. The former "Ещё" drop-down's items now live on each row's right-click context menu, so the row is two controls.</summary>
    /// <param name="cultureName">"en" or "ru".</param>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void PanelsCommandRow_NaturalWidth_FitsOneLine(string cultureName)
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var culture = CultureInfo.GetCultureInfo(cultureName);

            var addButton = new Button
            {
                Content = LocalizationResources.GetString("Settings_Panels_AddPanel_Button", culture),
                Style = (Style)app.Resources["Theme.Button.Primary"],
                Margin = new Thickness(0, 0, 8, 0),
            };
            var customizeButton = new Button
            {
                Content = LocalizationResources.GetString("Settings_Panels_Customize_Button", culture),
                Style = (Style)app.Resources["Theme.Button"],
            };

            // Measured directly (no hosting Window needed): a StackPanel's OWN natural DesiredSize is what
            // matters here — a Window.Content StackPanel defaults to HorizontalAlignment.Stretch and would
            // report its ARRANGED size (the window's own width) as ActualWidth, not its natural content
            // width, which is the actual "does this overflow?" question T-9/H-4 asks.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { addButton, customizeButton } };
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var message = $"Panels command row ({cultureName}) natural width {row.DesiredSize.Width:F1} exceeds " +
                $"the available {AvailablePanelsRowWidth:F1} DIP content width at the window's own 760 DIP " +
                "default — T-9/H-4 (Variant A, §2.7 D-8).";

            Assert.True(row.DesiredSize.Width <= AvailablePanelsRowWidth, message);
        });
    }

    private static TextBlock? FindLabel(Grid grid)
    {
        foreach (var child in grid.Children)
        {
            if (child is TextBlock { TextTrimming: TextTrimming.CharacterEllipsis } label) return label;
        }

        return null;
    }
}
