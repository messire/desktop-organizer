using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.Localization;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// T-9 (docs/PLANS/design-system-1x.md §3 phase 6.2c, R-1): best-effort regression coverage that
/// <c>PanelCustomizeWindow.xaml</c>'s single-line elements — segmented-control items (View/Sort order) and
/// the two fixed-width <see cref="ComboBox"/>es (Content filter/Icon layout) — fit this window's own 420 DIP
/// width, in both <c>en</c> and <c>ru</c>. Mirrors <c>SettingsWindowLayoutWidthTests</c>' own technique:
/// resolves label text directly via <see cref="LocalizationResources.GetString(string, CultureInfo)"/> (what
/// <c>LocKeyConverter</c>/<c>EnumDisplayNameConverter</c> themselves read) and measures a plain
/// <see cref="TextBlock"/>'s natural width, rather than mutating the process-wide
/// <c>DesktopOrganizer.UI.Localization.LocalizationSource.Current</c> singleton (which — once read by any
/// earlier test in this same process — cannot be safely re-initialized to a different culture; see that
/// type's own remarks).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class CustomizeLayoutWidthTests
{
    // PanelCustomizeWindow.xaml: Width="420"; ScrollViewer Padding="16,12,16,12"; SettingsCard's own
    // Theme.Card.Padding = Thickness(16,12,16,12) (ThemeResourceBuilder.cs) — both horizontal-only here.
    private const double WindowWidth = 420;
    private const double ScrollViewerHorizontalPadding = 16 * 2;
    private const double CardHorizontalPadding = 16 * 2;
    private const double CardInteriorWidth = WindowWidth - ScrollViewerHorizontalPadding - CardHorizontalPadding;

    // Settings.SegmentedGroup: BorderThickness Thickness.Hairline (1, both sides = 2) + Padding="2" (both
    // sides = 4). Settings.SegmentedItem: Padding="10,5" (10 each side = 20 per segment).
    private const double SegmentedGroupChrome = 2 + 4;
    private const double SegmentedItemHorizontalPadding = 10 * 2;

    // Theme.ComboBox: Padding="10,5" (10 each side = 20) reserved for the selection text, PLUS the
    // chevron column's own Margin="0,0,10,0" (chevron glyph itself ~10 DIP at Font.Size.Caption 10pt) —
    // ~20 DIP more. A single approximate constant, generous on purpose (best-effort budget, not a pixel-
    // exact template re-implementation).
    private const double ComboBoxChrome = 20 + 20;

    /// <summary>The two shipped UI cultures (docs/PLANS/localization.md).</summary>
    public static TheoryData<string> Cultures => ["en", "ru"];

    /// <summary>
    /// "View" (List/Grid) and "Sort order" (Manual/Name) — each segmented group's two items' combined
    /// natural width (label + per-segment padding + group chrome) fits the card's own 356 DIP interior in
    /// both cultures (List/Grid/Manual/По имени are short; this is a real, non-vacuous ratchet against a
    /// future longer enum label or RESX edit).
    /// </summary>
    /// <param name="cultureName">"en" or "ru".</param>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void SegmentedRows_ViewAndSortOrder_NaturalWidth_FitsCardInteriorWidth(string cultureName)
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var culture = CultureInfo.GetCultureInfo(cultureName);

            AssertSegmentedGroupFits(app, culture, "View", ["Enum_PanelViewMode_List", "Enum_PanelViewMode_Grid"]);
            AssertSegmentedGroupFits(app, culture, "Sort order", ["Enum_PanelSortMode_Manual", "Enum_PanelSortMode_Name"]);
        });
    }

    /// <summary>Content filter's 4-value, 180 DIP <see cref="ComboBox"/>: the widest label's natural width (plus chrome) fits the box in both cultures.</summary>
    /// <param name="cultureName">"en" or "ru".</param>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void ContentFilterComboBox_WidestLabelNaturalWidth_FitsThe180DipBox(string cultureName)
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var culture = CultureInfo.GetCultureInfo(cultureName);

            string[] keys =
            [
                "Enum_PanelContentFilter_ShowAll", "Enum_PanelContentFilter_HideAll",
                "Enum_PanelContentFilter_HideShortcuts", "Enum_PanelContentFilter_FoldersOnly",
            ];
            var widest = keys.Max(key => MeasureLabelNaturalWidth(app, culture, key));
            var message = $"Content filter's widest label ({cultureName}) natural width {widest:F1} + chrome " +
                $"{ComboBoxChrome} exceeds the 180 DIP ComboBox — T-9.";

            Assert.True(widest + ComboBoxChrome <= 180, message);
        });
    }

    /// <summary>
    /// Icon layout's 2-value, 180 DIP <see cref="ComboBox"/>: <c>Enum_IconLayoutPolicy_LegacyStretch</c>'s own
    /// display text ("Adaptive stretch (legacy)" en / "Адаптивное растяжение (устар.)" ru) is characterized
    /// here as a KNOWN, DOCUMENTED T-9 exception (design-system-1x.md §3's own "allowlist допускается с
    /// причиной") — it does NOT fit 180 DIP in either culture — rather than either silently accepting a hard
    /// visual clip or blocking on an unsolvable text-length problem (the window itself is a fixed 420 DIP;
    /// the enum's own display text is frozen RESX content, and a segmented control does not fit either two
    /// half-width cells — see that card's own XAML remarks). The mitigation actually shipped
    /// (<c>PanelCustomizeWindow.xaml</c>: <c>TextTrimming="CharacterEllipsis"</c> on the item template, plus a
    /// <c>ToolTip</c> that always shows the untrimmed text) is asserted here structurally, so a future edit
    /// that drops either one regresses this test instead of silently reintroducing a hard clip.
    /// </summary>
    /// <param name="cultureName">"en" or "ru".</param>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void IconLayoutComboBox_LongestLabelDoesNotFit_MitigatedByTrimmingAndTooltip(string cultureName)
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var culture = CultureInfo.GetCultureInfo(cultureName);

            var longest = MeasureLabelNaturalWidth(app, culture, "Enum_IconLayoutPolicy_LegacyStretch");
            var message = $"Enum_IconLayoutPolicy_LegacyStretch ({cultureName}) natural width {longest:F1} + chrome " +
                $"{ComboBoxChrome} no longer exceeds 180 DIP — this test's own allowlist reasoning is stale; " +
                "consider asserting a hard fit instead (T-9).";

            Assert.True(longest + ComboBoxChrome > 180, message);
        });

        var repoRoot = RepoRootLocator.Find();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DesktopOrganizer.UI", "Views", "PanelCustomizeWindow.xaml"));

        var iconLayoutComboBoxBlock = System.Text.RegularExpressions.Regex.Match(
            xaml,
            "<ComboBox Width=\"180\" ItemsSource=\"\\{Binding IconLayoutPolicies\\}\".*?</ComboBox>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.True(iconLayoutComboBoxBlock.Success, "Icon layout ComboBox not found (or restructured) in PanelCustomizeWindow.xaml — T-9 mitigation check is stale.");
        Assert.Contains("ToolTip=", iconLayoutComboBoxBlock.Value);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", iconLayoutComboBoxBlock.Value);
    }

    private static void AssertSegmentedGroupFits(Application app, CultureInfo culture, string groupLabel, string[] optionKeys)
    {
        var segmentsWidth = optionKeys.Sum(key => MeasureLabelNaturalWidth(app, culture, key) + SegmentedItemHorizontalPadding);
        var totalWidth = segmentsWidth + SegmentedGroupChrome;
        var message = $"{groupLabel} segmented group ({culture.Name}) natural width {totalWidth:F1} exceeds the " +
            $"{CardInteriorWidth:F1} DIP card interior — T-9/R-1.";

        Assert.True(totalWidth <= CardInteriorWidth, message);
    }

    /// <summary>Measures <paramref name="resxKey"/>'s own string (resolved directly via <see cref="LocalizationResources"/>, not a live <c>{loc:Loc}</c> binding) at <c>Font.Size.Body</c> — the same effective size <c>Settings.SegmentedItem</c>/<c>Theme.ComboBox</c> render at.</summary>
    private static double MeasureLabelNaturalWidth(Application app, CultureInfo culture, string resxKey)
    {
        var text = LocalizationResources.GetString(resxKey, culture);
        var block = new TextBlock { Text = text, FontSize = (double)app.Resources["Font.Size.Body"] };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }
}
