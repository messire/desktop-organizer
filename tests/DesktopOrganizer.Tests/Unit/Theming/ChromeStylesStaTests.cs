using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// T-7 (docs/PLANS/design-system-1x.md §3 phase 6.2/§5): real, applied-template regression coverage for the
/// phase 6.2a "Palette T" control/style catalogue (<c>Resources/Styles/Theme.Controls.xaml</c>,
/// <c>Resources/Styles/Controls.xaml</c>'s new <c>Theme.ContextMenu</c>/<c>Theme.MenuItem</c>/
/// <c>Theme.Popup.Chrome</c> additions). Scoped to what 6.2a itself owns (library, not screens) — the
/// remaining T-7 items the plan lists (e.g. <c>TitleEditBox</c> not picking up <c>Theme.TextBox</c>, the
/// panel header menu's real <c>HeaderContextMenu</c> instance) apply only once 6.2c wires
/// <c>ChildWindow.xaml</c> onto these styles, and are left for that phase's own test pass.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChromeStylesStaTests
{
    private static readonly AppTheme[] AllThemes = [AppTheme.Light, AppTheme.Dark, AppTheme.HighContrast];

    /// <summary><c>[MemberData]</c> source — one <see cref="AppTheme"/> case per entry of <see cref="AllThemes"/>.</summary>
    public static IEnumerable<object[]> ThemeCases() => AllThemes.Select(t => new object[] { t });

    // ── H-1 regression (closes docs/PLANS/design-system-1x.md §1.1 S-18) ──────────────────────────────

    /// <summary>
    /// H-1 fix: a real <see cref="ContextMenu"/> styled with <c>Theme.ContextMenu</c>/<c>Theme.MenuItem</c>,
    /// with a nested header+child item (the exact shape <c>ChromeContrastAuditTests</c>'
    /// <c>KnownGap_H1_...</c> test builds against the OLD <c>Control.ContextMenu</c>/<c>Control.MenuItem</c>
    /// pair) — the header item's own <see cref="Popup"/>'s <see cref="Border"/> child ("SubmenuBorder")
    /// now resolves <c>Theme.Surface.Flyout</c>, the SAME background <c>Theme.ContextMenu</c> itself uses,
    /// in every one of <see cref="AllThemes"/> — never the stock system-colour background the pre-6.2a
    /// Aero2 template silently fell back to.
    /// </summary>
    /// <param name="theme">The Palette T theme to materialize the whole merge chain against.</param>
    [Theory]
    [MemberData(nameof(ThemeCases))]
    public void MenuItem_SubmenuHeaderRole_PopupBackgroundIsThemeSurfaceFlyout_WithReadableTextContrast(AppTheme theme)
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(theme);
            var app = isolation.App;

            var placementTarget = new Button();
            var window = new Window { Content = placementTarget, ShowActivated = false, Left = -5000, Top = -5000, Width = 50, Height = 50 };
            window.Show();
            window.UpdateLayout();

            try
            {
                var childItem = new MenuItem { Header = "Child item" };
                var headerItem = new MenuItem { Header = "Parent header" };
                headerItem.Items.Add(childItem);

                var contextMenu = new ContextMenu
                {
                    Style = (Style)app.Resources["Theme.ContextMenu"],
                    PlacementTarget = placementTarget,
                };
                contextMenu.Resources.Add(typeof(MenuItem), new Style(typeof(MenuItem)) { BasedOn = (Style)app.Resources["Theme.MenuItem"] });
                contextMenu.Items.Add(headerItem);

                contextMenu.IsOpen = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                Assert.Equal(MenuItemRole.SubmenuHeader, headerItem.Role);

                headerItem.IsSubmenuOpen = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                var popup = FindVisualDescendant<Popup>(headerItem);
                Assert.NotNull(popup);
                var submenuBorder = Assert.IsType<Border>(popup!.Child);

                var expectedFlyout = ((SolidColorBrush)app.Resources[ThemeKeys.SurfaceFlyout]).Color;
                var actualBackground = Assert.IsType<SolidColorBrush>(submenuBorder.Background).Color;
                Assert.Equal(expectedFlyout, actualBackground);

                if (theme != AppTheme.HighContrast)
                {
                    var textPrimary = ((SolidColorBrush)app.Resources[ThemeKeys.TextPrimary]).Color;
                    var contrast = ContrastCalculator.ContrastRatio(textPrimary, actualBackground);
                    Assert.True(contrast >= ContrastCalculator.MinimumTextContrast, $"Submenu popup text contrast {contrast:F2} < {ContrastCalculator.MinimumTextContrast} ({theme}).");
                }

                contextMenu.IsOpen = false;
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Built inside one real <see cref="ContextMenu"/> — a leaf and a header-with-a-submenu added directly
    /// to <see cref="ItemsControl.Items"/>, and — inside that submenu — a leaf and a nested header — every
    /// one of the 4 items resolves to <c>Theme.MenuItem</c>'s matching real <see cref="ControlTemplate"/>
    /// (<c>Theme.MenuItem.ItemTemplate</c> for the two leaves, <c>Theme.MenuItem.HeaderTemplate</c> for the
    /// two headers) — never the WPF stock/Aero2 default.
    /// </summary>
    /// <remarks>
    /// <see cref="MenuItemRole.TopLevelItem"/>/<see cref="MenuItemRole.TopLevelHeader"/> never actually
    /// occur anywhere in this test (confirmed empirically, matching WPF's own documented behaviour): those
    /// two roles are reserved for a <see cref="Menu"/> control's own horizontal bar — every item hosted by
    /// a <see cref="ContextMenu"/> (including ones added directly to its own <see cref="ItemsControl.Items"/>,
    /// not just nested ones) gets <see cref="MenuItemRole.SubmenuItem"/>/<see cref="MenuItemRole.SubmenuHeader"/>
    /// instead — exactly what <c>ChromeContrastAuditTests</c>' own H-1 characterization test already
    /// asserted for its directly-hosted header item. Since this app has no <see cref="Menu"/> bar anywhere
    /// (only <see cref="ContextMenu"/>s, §2.6), <c>Theme.MenuItem</c>'s Style.Triggers for the TopLevel* Role
    /// values (kept for a hypothetical future <see cref="Menu"/> consumer) are provably never reached by any
    /// REAL menu in this app — this test's own name/assertions reflect that reality rather than asserting an
    /// unreachable role.
    /// </remarks>
    [Fact]
    public void MenuItem_DirectAndNestedContextMenuItems_ResolveToTheExpectedThemeTemplate()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var app = isolation.App;

            var placementTarget = new Button();
            var window = new Window { Content = placementTarget, ShowActivated = false, Left = -5000, Top = -5000, Width = 50, Height = 50 };
            window.Show();
            window.UpdateLayout();

            try
            {
                var submenuItem = new MenuItem { Header = "Submenu item" };
                var submenuHeader = new MenuItem { Header = "Submenu header" };
                submenuHeader.Items.Add(new MenuItem { Header = "Grandchild" });

                var directItem = new MenuItem { Header = "Direct item" };
                var directHeader = new MenuItem { Header = "Direct header" };
                directHeader.Items.Add(submenuItem);
                directHeader.Items.Add(submenuHeader);

                var contextMenu = new ContextMenu { Style = (Style)app.Resources["Theme.ContextMenu"], PlacementTarget = placementTarget };
                contextMenu.Resources.Add(typeof(MenuItem), new Style(typeof(MenuItem)) { BasedOn = (Style)app.Resources["Theme.MenuItem"] });
                contextMenu.Items.Add(directItem);
                contextMenu.Items.Add(directHeader);

                contextMenu.IsOpen = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                directHeader.IsSubmenuOpen = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                var actual = (directItem.Role, directHeader.Role, submenuItem.Role, submenuHeader.Role);
                var expected = (MenuItemRole.SubmenuItem, MenuItemRole.SubmenuHeader, MenuItemRole.SubmenuItem, MenuItemRole.SubmenuHeader);
                Assert.Equal(expected, actual);

                var itemTemplate = (ControlTemplate)app.Resources["Theme.MenuItem.ItemTemplate"];
                var headerTemplate = (ControlTemplate)app.Resources["Theme.MenuItem.HeaderTemplate"];

                Assert.Same(itemTemplate, directItem.Template);
                Assert.Same(headerTemplate, directHeader.Template);
                Assert.Same(itemTemplate, submenuItem.Template);
                Assert.Same(headerTemplate, submenuHeader.Template);

                contextMenu.IsOpen = false;
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ── DropDownButton keyboard/UIA contract ───────────────────────────────────────────────────────────

    /// <summary><see cref="DropDownButton.OnClick"/> (the choke point a real click, Space, Enter, and Alt+Down keyboard trigger all funnel through) toggles <see cref="ToggleButton.IsChecked"/> and syncs <see cref="DropDownButton.Menu"/>'s <see cref="ContextMenu.IsOpen"/> — twice, to also cover the close-on-second-click path.</summary>
    [Fact]
    public void OnClick_TogglesCheckedAndSyncsMenuIsOpen_BothDirections()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var (window, button) = BuildDropDownButtonHarness(isolation.App);
            try
            {
                InvokeOnClick(button);
                Assert.True(button.IsChecked);
                Assert.True(button.Menu!.IsOpen);

                InvokeOnClick(button);
                Assert.False(button.IsChecked);
                Assert.False(button.Menu.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Enter (design-system-1x.md §2.5: "Space/Enter/Alt+↓ открывает") opens the menu via <see cref="DropDownButton.OnKeyDown"/>.</summary>
    [Fact]
    public void OnKeyDown_Enter_OpensMenuAndChecksButton()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var (window, button) = BuildDropDownButtonHarness(isolation.App);
            try
            {
                var args = BuildKeyEventArgs(button, Key.Enter);
                InvokeOnKeyDown(button, args);

                Assert.True(button.IsChecked);
                Assert.True(button.Menu!.IsOpen);
                Assert.True(args.Handled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Closing the menu (however it closes) un-checks the button and returns keyboard focus to it (§2.5:
    /// "Esc закрывает и возвращает фокус") — the private <c>OnMenuClosed</c> handler
    /// <see cref="DropDownButton.Menu"/>'s <see cref="ContextMenu.Closed"/> event is wired to.
    /// </summary>
    /// <remarks>
    /// Invokes the handler directly (reflection) rather than relying on a real, headless-hosted
    /// <see cref="Popup"/> to actually raise <see cref="ContextMenu.Closed"/> — an off-screen
    /// (<c>Left/Top = -5000</c>) <see cref="Window"/> with no real screen presence does not reliably
    /// complete a <see cref="ContextMenu"/>'s own open/close popup lifecycle in this test host (confirmed
    /// empirically: setting <see cref="ContextMenu.IsOpen"/> back to <see langword="false"/> after opening it
    /// via <see cref="ToggleButton.IsChecked"/> did not raise <c>Closed</c> here). The wiring itself
    /// (<c>Menu</c> DP changed → <c>Closed += OnMenuClosed</c>) is a one-line, code-reading-obvious
    /// subscription; what actually matters — and is what a Fences-parity/manual-verify pass (M-6.6) would
    /// catch if it silently stopped firing — is the HANDLER's own behaviour, which this pins directly.
    /// </remarks>
    [Fact]
    public void OnMenuClosed_UnchecksButtonAndRestoresFocus()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var (window, button) = BuildDropDownButtonHarness(isolation.App);
            try
            {
                InvokeOnClick(button);
                Assert.True(button.IsChecked);

                InvokeOnMenuClosed(button);

                Assert.False(button.IsChecked);
                Assert.True(button.IsKeyboardFocused);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary><see cref="DropDownButton"/>'s UIA automation peer exposes the Toggle pattern (§2.5: "UIA Toggle-pattern") — inherited for free from <see cref="ToggleButton"/>.</summary>
    [Fact]
    public void AutomationPeer_SupportsTogglePattern()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var (window, button) = BuildDropDownButtonHarness(isolation.App);
            try
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(button);
                Assert.NotNull(peer);
                Assert.NotNull(peer!.GetPattern(PatternInterface.Toggle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ── DropDownButton.ShowChevron / Theme.Button.Overflow (UX-MEDIUM, 6.5 review round) ──────────────────

    /// <summary>The default <see cref="DropDownButton"/> style draws its chevron glyph (<see cref="DropDownButton.ShowChevron"/> defaults to <see langword="true"/>).</summary>
    [Fact]
    public void DropDownButton_DefaultStyle_ShowsChevron()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var (window, button) = BuildDropDownButtonHarness(isolation.App);
            try
            {
                Assert.True(button.ShowChevron);

                var chevron = FindVisualDescendant<TextBlock>(button, tb => tb.Name == "Chevron")!;
                Assert.Equal(Visibility.Visible, chevron.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// UX-MEDIUM fix: a <see cref="DropDownButton"/> styled <c>Theme.Button.Overflow</c> (the per-row "⋯"
    /// overflow buttons in <c>PagesSection</c>/<c>BackupSection</c>) collapses the chevron entirely — its own
    /// <c>Content</c> is already the conventional overflow glyph, so a second chevron next to it would be a
    /// redundant, confusing "opens something else" affordance.
    /// </summary>
    [Fact]
    public void DropDownButton_ThemeButtonOverflowStyle_CollapsesChevron()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var app = isolation.App;

            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Item" });
            var button = new DropDownButton { Style = (Style)app.Resources["Theme.Button.Overflow"], Content = "", Menu = menu };
            var window = new Window { Content = button, ShowActivated = false, Left = -5000, Top = -5000, Width = 100, Height = 60 };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.False(button.ShowChevron);

                var chevron = FindVisualDescendant<TextBlock>(button, tb => tb.Name == "Chevron")!;
                Assert.Equal(Visibility.Collapsed, chevron.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ── SettingsCard ────────────────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="SettingsCard"/>'s ActionContent presenter is <c>AutomationProperties.LabeledBy</c>-linked to the header (§2.5).</summary>
    [Fact]
    public void SettingsCard_ActionContentPresenter_IsLabeledByTheHeaderPresenter()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);

            var actionContent = new CheckBox();
            var card = new SettingsCard { Header = "Title", Description = "Description", ActionContent = actionContent };
            var window = new Window { Content = card, ShowActivated = false, Left = -5000, Top = -5000, Width = 300, Height = 100 };
            window.Show();
            window.UpdateLayout();

            try
            {
                var headerHost = (FrameworkElement)card.Template.FindName("HeaderHost", card)!;
                var actionHost = FindVisualDescendant<ContentPresenter>(card, cp => ReferenceEquals(cp.Content, actionContent));

                Assert.NotNull(actionHost);
                Assert.Same(headerHost, AutomationProperties.GetLabeledBy(actionHost!));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ── Heading levels (§2.12) ─────────────────────────────────────────────────────────────────────────

    /// <summary><c>Settings.PageHeader</c> (L1) and <c>Settings.GroupHeader</c> (L2) both expose an explicit <see cref="AutomationProperties.HeadingLevelProperty"/>.</summary>
    [Fact]
    public void PageHeader_AndGroupHeader_ExposeExpectedHeadingLevels()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var app = isolation.App;

            var pageHeader = new TextBlock { Style = (Style)app.Resources["Settings.PageHeader"], Text = "Page" };
            var groupHeader = new TextBlock { Style = (Style)app.Resources["Settings.GroupHeader"], Text = "Group" };

            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(pageHeader));
            Assert.Equal(AutomationHeadingLevel.Level2, AutomationProperties.GetHeadingLevel(groupHeader));
        });
    }

    // ── Whole-catalogue materialization sweep, 3 themes (T-7 "каждый стиль загружается в Light/Dark/HC") ──

    /// <summary>Every explicit key <c>Theme.Controls.xaml</c>/the new <c>Controls.xaml</c> additions declare materializes without throwing, in Light, Dark, and High Contrast, including a real Measure/Arrange pass over one instance of every new control.</summary>
    /// <param name="theme">The Palette T theme to materialize the whole merge chain against.</param>
    [Theory]
    [MemberData(nameof(ThemeCases))]
    public void EntireCatalogue_MaterializesWithoutThrowing(AppTheme theme)
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(theme);
            var app = isolation.App;

            string[] keys =
            [
                "Theme.FocusVisual", "Theme.Button", "Theme.Button.Primary", "Theme.Button.Subtle", "Theme.Button.Link",
                "Theme.ToggleSwitch", "Theme.CheckBox", "Theme.RadioButton",
                "Theme.ComboBox", "Theme.ComboBoxItem", "Theme.TextBox", "Theme.Slider",
                "Theme.ScrollBar", "Theme.ScrollBar.Thumb", "Theme.ListBox", "Theme.ListBoxItem",
                "Theme.ListView", "Theme.GridViewColumnHeader",
                "Theme.ContextMenu", "Theme.MenuItem", "Theme.MenuItem.GroupHeader", "Theme.Separator",
                "Theme.Popup.Chrome", "Settings.GroupHeader",
            ];

            foreach (var key in keys)
                Assert.NotNull(app.Resources[key]);

            // Force real ControlTemplate content (not just the Style object) to parse and lay out, the way
            // ResourceDictionaryMergeTests' own CRITICAL-1 regression coverage does for the rest of the
            // catalogue — this is what would catch a typo'd StaticResource inside a Setter.Value.
            var button = new Button { Style = (Style)app.Resources["Theme.Button.Primary"], Content = "OK" };
            var dropDown = new DropDownButton { Content = "More", Menu = new ContextMenu() };
            var card = new SettingsCard { Header = "H", Description = "D", ActionContent = new CheckBox() };
            var infoBar = new InfoBar { Title = "T", Message = "M" };
            var toggle = new CheckBox { Style = (Style)app.Resources["Theme.ToggleSwitch"] };
            var combo = new ComboBox { Style = (Style)app.Resources["Theme.ComboBox"], ItemsSource = new[] { "A", "B" } };
            var textBox = new TextBox { Style = (Style)app.Resources["Theme.TextBox"] };
            var slider = new Slider { Style = (Style)app.Resources["Theme.Slider"] };

            var window = new Window
            {
                Content = new StackPanel { Children = { button, dropDown, card, infoBar, toggle, combo, textBox, slider } },
                ShowActivated = false,
                Left = -5000,
                Top = -5000,
                Width = 400,
                Height = 400,
            };
            var exception = Record.Exception(() =>
            {
                window.Show();
                window.UpdateLayout();
            });
            window.Close();

            Assert.Null(exception);
        });
    }

    // ── InfoBar severity glyph/title colour/live-region (wave-4 review round, UX MINOR) ───────────────────

    /// <summary>Each <see cref="InfoBarSeverity"/> resolves its own leading glyph and TITLE colour — the body <c>Message</c> line always stays <c>Theme.Text.Primary</c> regardless (never asserted equal to a severity colour here — see the class remark on the style itself).</summary>
    /// <param name="severity">The severity under test.</param>
    /// <param name="expectedGlyphKey">The <c>Icon.Glyph.*</c> key <see cref="InfoBar"/>'s template should resolve its glyph <see cref="TextBlock"/>'s <c>Text</c> to.</param>
    /// <param name="expectedTitleColorKey">The <c>Theme.Text.*</c> key the title should be painted with, or <see langword="null"/> for <see cref="ThemeKeys.TextPrimary"/> (Informational's own default, no trigger).</param>
    [Theory]
    [InlineData(InfoBarSeverity.Informational, "Icon.Glyph.Info", null)]
    [InlineData(InfoBarSeverity.Success, "Icon.Glyph.Success", ThemeKeys.TextSuccess)]
    [InlineData(InfoBarSeverity.Caution, "Icon.Glyph.Warning", ThemeKeys.TextCaution)]
    [InlineData(InfoBarSeverity.Critical, "Icon.Glyph.Error", ThemeKeys.TextCritical)]
    public void InfoBar_Severity_SetsGlyphAndTitleColour(InfoBarSeverity severity, string expectedGlyphKey, string? expectedTitleColorKey)
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);
            var app = isolation.App;

            var infoBar = new InfoBar { Title = "T", Message = "M", Severity = severity };
            var window = new Window { Content = infoBar, ShowActivated = false, Left = -5000, Top = -5000, Width = 400, Height = 200 };
            try
            {
                window.Show();
                window.UpdateLayout();

                var glyph = FindVisualDescendant<TextBlock>(infoBar, tb => tb.Name == "GlyphText")!;
                var title = FindVisualDescendant<TextBlock>(infoBar, tb => tb.Name == "TitleText")!;

                Assert.Equal((string)app.Resources[expectedGlyphKey], glyph.Text);

                var expectedTitleColorKey2 = expectedTitleColorKey ?? ThemeKeys.TextPrimary;
                Assert.Equal(
                    ((SolidColorBrush)app.Resources[expectedTitleColorKey2]).Color,
                    ((SolidColorBrush)title.Foreground).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Only <see cref="InfoBarSeverity.Critical"/> raises <see cref="AutomationProperties.LiveSettingProperty"/> from the style's own base <c>Polite</c> to <c>Assertive</c> — a failure the user must notice should interrupt Narrator, unlike every other severity.</summary>
    /// <param name="severity">The severity under test.</param>
    /// <param name="expected">The <see cref="AutomationLiveSetting"/> that severity should resolve to.</param>
    [Theory]
    [InlineData(InfoBarSeverity.Informational, AutomationLiveSetting.Polite)]
    [InlineData(InfoBarSeverity.Success, AutomationLiveSetting.Polite)]
    [InlineData(InfoBarSeverity.Caution, AutomationLiveSetting.Polite)]
    [InlineData(InfoBarSeverity.Critical, AutomationLiveSetting.Assertive)]
    public void InfoBar_Severity_SetsExpectedLiveSetting(InfoBarSeverity severity, AutomationLiveSetting expected)
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.Light);

            var infoBar = new InfoBar { Title = "T", Message = "M", Severity = severity };
            var window = new Window { Content = infoBar, ShowActivated = false, Left = -5000, Top = -5000, Width = 400, Height = 200 };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal(expected, AutomationProperties.GetLiveSetting(infoBar));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ── TextBox selection brushes (wave-4 review round, UX MINOR) ──────────────────────────────────────

    /// <summary><c>Theme.TextBox</c>'s <see cref="TextBoxBase.SelectionBrush"/>/<see cref="TextBoxBase.SelectionTextBrush"/> resolve <c>Theme.Selection.Fill</c>/<c>Theme.Selection.Text</c> — the same pairing <c>ThemePaletteContrastTests.SelectionText_MeetsTextContrast_OnSelectionFill</c> already holds to the 4.5:1 text floor — rather than falling back to the unthemed OS accent highlight.</summary>
    /// <param name="theme">The Palette T theme under test.</param>
    [Theory]
    [MemberData(nameof(ThemeCases))]
    public void TextBox_SelectionBrushes_ResolveThemeSelectionTokens(AppTheme theme)
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(theme);
            var app = isolation.App;

            var textBox = new TextBox { Style = (Style)app.Resources["Theme.TextBox"] };
            var window = new Window { Content = textBox, ShowActivated = false, Left = -5000, Top = -5000, Width = 200, Height = 60 };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal(((SolidColorBrush)app.Resources[ThemeKeys.SelectionFill]).Color, ((SolidColorBrush)textBox.SelectionBrush).Color);
                Assert.Equal(((SolidColorBrush)app.Resources[ThemeKeys.SelectionText]).Color, ((SolidColorBrush)textBox.SelectionTextBrush).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ── Settings sidebar glyph, High Contrast (M-2, 6.5 review round) ─────────────────────────────────────

    /// <summary>
    /// M-2 fix: <c>Settings.SidebarItemTemplate</c>'s glyph <see cref="TextBlock"/> — normally painted
    /// <c>Theme.Text.Secondary</c> — resolves to <c>Theme.Selection.Text</c> (in High Contrast,
    /// <see cref="SystemColors.HighlightTextBrush"/>) once its hosting <see cref="ListBoxItem"/> is selected,
    /// instead of staying <c>Theme.Text.Secondary</c> (≈ <see cref="SystemColors.WindowTextBrush"/>) against
    /// the now-<see cref="SystemColors.HighlightBrush"/> selected background — the ≈1.4:1 WCAG 1.4.11 gap
    /// the review round found.
    /// </summary>
    [Fact]
    public void SidebarItemTemplate_Glyph_SelectedListBoxItem_HighContrast_ResolvesSelectionText()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.HighContrast);
            var app = isolation.App;

            var listBox = new ListBox
            {
                Style = (Style)app.Resources["Settings.SidebarListBox"],
                ItemsSource = DesktopOrganizer.UI.Models.SettingsSectionCatalog.Primary,
            };
            var window = new Window { Content = listBox, ShowActivated = false, Left = -5000, Top = -5000, Width = 260, Height = 400 };
            try
            {
                window.Show();
                window.UpdateLayout();

                var container = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0)!;
                var glyph = FindVisualDescendant<TextBlock>(container, tb => tb.Name == "Glyph")!;

                Assert.Equal(((SolidColorBrush)app.Resources[ThemeKeys.TextSecondary]).Color, ((SolidColorBrush)glyph.Foreground).Color);

                listBox.SelectedIndex = 0;
                window.UpdateLayout();

                Assert.Equal(((SolidColorBrush)app.Resources[ThemeKeys.SelectionText]).Color, ((SolidColorBrush)glyph.Foreground).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// M-2 fix: the same glyph also resolves <c>Theme.Selection.Text</c> when only <see cref="UIElement.IsMouseOver"/>
    /// is <see langword="true"/> (not selected) — the hover state uses the SAME <c>Theme.Control.Subtle.Hover</c>
    /// (= <see cref="SystemColors.HighlightBrush"/> in High Contrast) fill as selection, so it needs the same
    /// glyph-contrast fix.
    /// </summary>
    [Fact]
    public void SidebarItemTemplate_Glyph_MouseOverListBoxItem_HighContrast_ResolvesSelectionText()
    {
        StaTestRunner.Run(() =>
        {
            using var isolation = new IsolatedAppResources(AppTheme.HighContrast);
            var app = isolation.App;

            var listBox = new ListBox
            {
                Style = (Style)app.Resources["Settings.SidebarListBox"],
                ItemsSource = DesktopOrganizer.UI.Models.SettingsSectionCatalog.Primary,
            };
            var window = new Window { Content = listBox, ShowActivated = false, Left = -5000, Top = -5000, Width = 260, Height = 400 };
            try
            {
                window.Show();
                window.UpdateLayout();

                var container = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0)!;
                var glyph = FindVisualDescendant<TextBlock>(container, tb => tb.Name == "Glyph")!;

                // UIElement.IsMouseOver is read-only (no real mouse-input simulation on an off-screen
                // window) — set it through its own DependencyPropertyKey, the same reflection-based
                // technique this class already uses to invoke protected members directly (InvokeOnClick et
                // al. below).
                SetIsMouseOver(container, true);
                window.UpdateLayout();

                Assert.Equal(((SolidColorBrush)app.Resources[ThemeKeys.SelectionText]).Color, ((SolidColorBrush)glyph.Foreground).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────
    private static (Window Window, DropDownButton Button) BuildDropDownButtonHarness(Application app)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "First" });
        menu.Items.Add(new MenuItem { Header = "Second" });

        var button = new DropDownButton { Content = "More", Menu = menu };
        var window = new Window { Content = button, ShowActivated = true, Left = -5000, Top = -5000, Width = 200, Height = 100 };
        window.Show();
        window.UpdateLayout();
        button.Focus();
        return (window, button);
    }

    /// <summary>Invokes <see cref="DropDownButton"/>'s protected <c>OnClick</c> override directly (no WPF input-stack simulation needed) — the same "expose the protected member via a runtime-type-preserving virtual call" technique <c>IntegerInputBoxTests</c> already uses for <c>OnPreviewKeyDown</c>, done here via reflection since <see cref="DropDownButton"/> itself has no test-only subclass.</summary>
    private static void InvokeOnClick(DropDownButton button) =>
        typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(button, null);

    /// <summary>Invokes <see cref="DropDownButton"/>'s protected <c>OnKeyDown</c> override directly — see <see cref="InvokeOnClick"/>'s own remarks.</summary>
    private static void InvokeOnKeyDown(DropDownButton button, KeyEventArgs args) =>
        typeof(UIElement).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(button, [args]);

    /// <summary>Invokes <see cref="DropDownButton"/>'s own private <c>OnMenuClosed</c> handler directly — see <see cref="InvokeOnClick"/>'s own remarks, and <c>OnMenuClosed_UnchecksButtonAndRestoresFocus</c>'s remarks for why this test does not go through a real <see cref="ContextMenu.Closed"/> event.</summary>
    private static void InvokeOnMenuClosed(DropDownButton button) =>
        typeof(DropDownButton).GetMethod("OnMenuClosed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(button, [null, new RoutedEventArgs()]);

    /// <summary>Constructs a real <see cref="KeyEventArgs"/> for <paramref name="key"/> against <paramref name="element"/>'s own <see cref="PresentationSource"/> (needs a real, shown <see cref="Window"/> — the constructor throws on a <see langword="null"/> source).</summary>
    private static KeyEventArgs BuildKeyEventArgs(UIElement element, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) { RoutedEvent = Keyboard.KeyDownEvent };

    /// <summary>
    /// Sets <see cref="UIElement.IsMouseOver"/> directly through its own private
    /// <c>IsMouseOverPropertyKey</c> — the property is read-only (computed from real hit-testing), and an
    /// off-screen, never-activated <see cref="Window"/> has nothing to hit-test a simulated mouse move
    /// against.
    /// </summary>
    private static void SetIsMouseOver(UIElement element, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement)
            .GetField("IsMouseOverPropertyKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        element.SetValue(key, value);
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }

        return null;
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

    /// <summary>Swaps in a fresh, full (Colors→Templates, Light/Dark/HC theme appended) <see cref="Application.Resources"/> chain for the duration of the test, restoring whatever was there before on <see cref="Dispose"/> — the same convention <c>AppThemeServiceTests.IsolatedAppResources</c>/<c>ResourceDictionaryMergeTests</c> already establish.</summary>
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
