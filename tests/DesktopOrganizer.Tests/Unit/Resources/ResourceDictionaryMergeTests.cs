using System.Collections;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Core;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Resources;

/// <summary>
/// Regression coverage for the CRITICAL-1 class of bug found in Ambient Accent UI review round 2: an
/// <c>internal</c> member referenced from XAML via <c>{x:Static ...}</c> — or any other resource that
/// fails to resolve (a missing/renamed <c>StaticResource</c>/<c>DynamicResource</c> key, a broken merge
/// order) — throws <see cref="System.Windows.Markup.XamlParseException"/> the moment the referencing
/// <c>Style</c>/<c>ControlTemplate</c>/<c>DataTemplate</c> is actually materialized. None of the other
/// ~1748 unit tests caught it: none of them build the real <see cref="Application.Resources"/> merge
/// chain <c>App.xaml.cs</c>'s <c>LoadResourceDictionaries()</c> builds and then force every deferred
/// resource to materialize. This test does exactly that, for both the normal and Windows-High-Contrast
/// merge orders, so this whole bug class is caught by every <c>dotnet test</c> run instead of only by a
/// one-off manual reviewer probe.
/// </summary>
/// <remarks>
/// Runs on a dedicated STA thread (there is no existing STA test helper in this project to reuse —
/// checked first, per the usual convention here, but none of the other WPF-touching fixtures, e.g.
/// <c>ChildWindowViewModelTests.EnsureWpfApplication</c>, needed one because they never construct a
/// <see cref="System.Windows.Controls.ContextMenu"/> or otherwise touch <c>InputManager</c>). Simply
/// merging/parsing dictionaries doesn't require it, but this test also has to force every deferred
/// resource to materialize — including <c>Panels.xaml</c>'s <c>FileContextMenu</c> — and constructing a
/// <see cref="System.Windows.Controls.ContextMenu"/> (a <see cref="System.Windows.FrameworkElement"/>)
/// requires <see cref="System.Windows.Input.InputManager"/> on the CURRENT thread, which throws
/// <see cref="System.InvalidOperationException"/> ("The calling thread must be STA") off the ordinary
/// (MTA) xUnit test thread.
///
/// <see cref="Application"/> only allows a single instance per process, and
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c> (AssemblyInfo.cs) means test
/// CLASSES never run concurrently within one <c>dotnet test</c> process — but their order is not
/// guaranteed, so <see cref="Application.Current"/> may already exist by the time this runs (several
/// other fixtures create one and never tear it down — see <c>ApplicationStateServiceTests
/// .RequestSave_InHeadlessTestHost_DegradesToImmediateSave</c>'s own remarks for the identical ambient-
/// state caveat). This test therefore reuses whatever <c>Application.Current</c> it finds rather than
/// assuming it owns the process, swaps in a throwaway <see cref="ResourceDictionary"/> for its own two
/// (normal / high-contrast) merge attempts, and in a <c>finally</c> restores whatever <c>Resources</c>
/// instance was there before if it found an existing <c>Application.Current</c>. If it constructed the
/// <c>Application</c> itself instead, it deliberately leaves it (and the last, high-contrast, merge)
/// alive rather than tearing it down — see <see cref="RunOnStaThread{T}"/>'s remarks for why.
/// <see cref="HighContrastState.IsActive"/> is likewise restored, and this class shares
/// <see cref="HighContrastStateTests"/>' collection so the two never race on that same static.
/// <para>
/// M-2 fix (P08 fix cycle 2): "did THIS call construct <c>Application.Current</c>" is decided by
/// reference-comparing <c>WpfApplicationHost.EnsureApplication()</c>'s return value against whatever
/// <c>Application.Current</c> was immediately beforehand, not by a separate "was it null" probe taken a
/// moment earlier — the two can never disagree with each other by construction. Delegating construction
/// itself to <c>WpfApplicationHost.EnsureApplication()</c> also means the (immediately-discarded, since
/// this test always overwrites <c>Resources</c> right after) initial merge — if this call happens to be
/// the one that constructs <c>Application.Current</c> — uses the SAME merge chain <c>App.xaml.cs</c>
/// builds in production, rather than a second, hand-rolled copy of it.
/// </para>
/// </remarks>
[Collection("HighContrastState")]
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResourceDictionaryMergeTests
{
    [Fact]
    public void LoadResourceDictionaries_NormalAndHighContrastModes_MaterializeEveryResourceWithoutThrowing()
    {
        var failures = RunOnStaThread(() =>
        {
            var results = new List<(bool HighContrast, Exception? Exception)>();
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                // Order is [true, false] (cheap fix, review round 3): the last iteration's merged
                // dictionaries are what survive in the shared Application.Resources for the rest of the
                // `dotnet test` process (this method only restores HighContrastState.IsActive/app.Resources
                // in `finally` below when it doesn't own the Application — see this class's own remarks —
                // it never re-merges a "normal" set afterward). Running high-contrast LAST used to leave
                // Colors.HighContrast.xaml's overrides merged into Application.Resources with
                // HighContrastState.IsActive correctly reset back to false — a resources/flag desync for
                // every test that runs afterward and happens to touch a themed resource. Normal-last
                // avoids that mismatch.
                foreach (var highContrast in new[] { true, false })
                {
                    app.Resources = new ResourceDictionary();
                    MergeResourceDictionaries(app, highContrast);

                    var exception = Record.Exception(() =>
                    {
                        TouchAllResourceKeys(app.Resources);
                        LoadTemplateContent(app, "ListViewTemplate");
                        LoadTemplateContent(app, "GridViewTemplate");
                        MaterializePanelChromeTemplate(app);
                    });

                    results.Add((highContrast, exception));
                }
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;

                // If ownsApplication, deliberately leave Application.Current/Resources as-is (the last
                // — high-contrast — merge) rather than tearing it down: see RunOnStaThread's remarks for
                // why this thread keeps pumping afterward instead of exiting.
            }

            return results;
        });

        foreach (var (highContrast, exception) in failures)
        {
            Assert.True(
                exception is null,
                $"Resource dictionary merge chain (HighContrast={highContrast}) threw while materializing resources: {exception}");
        }
    }

    /// <summary>
    /// Regression pin (fix-cycle round 1 CRITICAL, fixed in d16aab6): <c>Panels.xaml</c>'s
    /// <c>PART_AccentIndicator</c> host <see cref="ColumnDefinition"/> is a fixed literal
    /// (<c>Width="8"</c>) chosen because it comfortably exceeds the combined maximum the strip's own
    /// bound width can reach — <see cref="PanelMetrics.AccentIndicatorThickness"/> (3.0) *
    /// <see cref="InteractionVisualState.AccentEmphasisFor"/>'s maximum, <see cref="PanelVisualState.Resizing"/>
    /// (1.7) * <see cref="LayoutEditVisuals.AccentEmphasis"/> (1.35) = 6.885. Nothing machine-checks that
    /// relationship — a future change bumping any of those three constants without also widening the XAML
    /// literal would silently clip/overflow the strip at runtime, invisible to every other test (this is
    /// exactly how the original CRITICAL made it past `dotnet build`+`dotnet test`). Reuses this class's
    /// own STA/resource-merge infrastructure (<see cref="RunOnStaThread{T}"/>/<see cref="MergeResourceDictionaries"/>)
    /// to build a real <see cref="PanelChrome"/>, apply its actual template, and read the REAL
    /// <see cref="ColumnDefinition.Width"/> that ends up hosting <c>PART_AccentIndicator</c> — not a
    /// hand-copied literal — so this test fails the moment the XAML value and the three constants above
    /// drift out of the required relationship, regardless of which side changes.
    /// </summary>
    /// <summary>
    /// 2026-09-23 (Ambient Accent reference): the bright accent strip is HEADER-tall only — on the reference
    /// it reads at lum ~120 inside the header row and drops to the dim ~55 border below it. Pins that
    /// <c>PART_AccentIndicator</c> occupies row 0 with no row span (it used to span both rows, i.e. the
    /// full panel height), so a future template edit cannot silently bring the full-height strip back.
    /// </summary>
    [Fact]
    public void PanelChromeTemplate_AccentIndicator_SpansTheHeaderRowOnly()
    {
        var (row, rowSpan) = RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast: false);

                var style = (Style)app.Resources[typeof(PanelChrome)];
                var chrome = new PanelChrome { Style = style };
                chrome.ApplyTemplate();

                var accentIndicator = (FrameworkElement)chrome.Template.FindName("PART_AccentIndicator", chrome)!;
                return (Grid.GetRow(accentIndicator), Grid.GetRowSpan(accentIndicator));
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });

        Assert.Equal(0, row);      // the header row (PanelMetrics.HeaderRowHeight)
        Assert.Equal(1, rowSpan);  // header only — NOT the full panel height
    }

    [Fact]
    public void PanelChromeTemplate_AccentIndicatorColumn_WidthCoversMaxCombinedAccentEmphasis()
    {
        var columnWidth = RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast: false);

                var style = (Style)app.Resources[typeof(PanelChrome)];
                var chrome = new PanelChrome { Style = style };
                chrome.ApplyTemplate();

                var accentIndicator = (FrameworkElement)chrome.Template.FindName("PART_AccentIndicator", chrome)!;
                var columnIndex = Grid.GetColumn(accentIndicator);
                var hostGrid = (Grid)VisualTreeHelper.GetParent(accentIndicator);

                return hostGrid.ColumnDefinitions[columnIndex].Width.Value;
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });

        var maxCombinedAccentWidth = PanelMetrics.AccentIndicatorThickness
            * InteractionVisualState.AccentEmphasisFor(PanelVisualState.Resizing)
            * LayoutEditVisuals.AccentEmphasis;

        Assert.True(
            columnWidth >= maxCombinedAccentWidth,
            $"PART_AccentIndicator's host column width ({columnWidth}) must be >= the combined maximum " +
            $"accent-emphasis width ({maxCombinedAccentWidth} = AccentIndicatorThickness * " +
            "InteractionVisualState.AccentEmphasisFor(Resizing) * LayoutEditVisuals.AccentEmphasis), or the " +
            "strip will visually clip/overflow at the Resizing+IsLayoutEditing combination.");
    }

    /// <summary>
    /// UX-P2 fix regression pin (docs/PLANS/workspace-grid-07-visual.md fix-cycle 1): List mode's item
    /// root switched from a horizontal <see cref="StackPanel"/> to a <see cref="Grid"/> with a star
    /// column for the label specifically so <c>TextTrimming="CharacterEllipsis"</c> has a finite width to
    /// truncate against — a <see cref="StackPanel"/> always measures its orientation-axis children with
    /// an infinite available width, so the label previously rendered at its own full, un-clipped desired
    /// width regardless of the item's actual on-screen width. Reuses this class's own STA/resource-merge
    /// infrastructure to load the REAL <c>ListViewTemplate</c>/its real <c>ItemTemplate</c> (not a
    /// hand-reimplemented stand-in), gives it a very long <see cref="FileItem.DisplayName"/>, and
    /// Measures/Arranges it at a deliberately narrow width — this would have failed against the
    /// pre-fix <see cref="StackPanel"/> markup (the label's <see cref="FrameworkElement.ActualWidth"/>
    /// would have come out far wider than the available width, instead of clamped to it).
    /// </summary>
    [Fact]
    public void ListViewTemplate_LongDisplayName_LabelTextBlockStaysWithinAvailableWidth()
    {
        var (labelWidth, availableWidth) = RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast: false);

                var listTemplate = (DataTemplate)app.Resources["ListViewTemplate"];
                var listBox = (ListBox)listTemplate.LoadContent();
                var itemTemplate = listBox.ItemTemplate;

                var root = (FrameworkElement)itemTemplate.LoadContent();
                root.DataContext = new FileItem(
                    path: @"C:\workspace-grid-07-visual\" + new string('A', 200) + ".txt",
                    icon: null,
                    displayName: new string('A', 200));

                const double narrow = 150d;
                root.Measure(new Size(narrow, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, narrow, Math.Max(root.DesiredSize.Height, 20)));

                var label = (TextBlock)root.FindName("DisplayNameText")!;
                return (label.ActualWidth, narrow);
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });

        Assert.True(
            labelWidth <= availableWidth,
            $"List-mode label TextBlock ({labelWidth:F1}) must stay within the item's available width " +
            $"({availableWidth:F1}) even for a very long DisplayName.");
    }

    /// <summary>
    /// MAJOR-B fix-cycle 2 regression pin (docs/PLANS/workspace-grid-07-visual.md): the test above
    /// (<see cref="ListViewTemplate_LongDisplayName_LabelTextBlockStaysWithinAvailableWidth"/>) measures
    /// the item <see cref="DataTemplate"/>'s ROOT element directly (<c>itemTemplate.LoadContent()</c>,
    /// then <c>Measure</c>/<c>Arrange</c> on THAT), never going through the real <see cref="ListBox"/>/
    /// <see cref="ScrollViewer"/> that actually hosts it in the running app — it therefore stayed green
    /// even though <c>ListViewTemplate</c>'s <see cref="ListBox"/> never set
    /// <c>ScrollViewer.HorizontalScrollBarVisibility="Disabled"</c> (unlike <c>GridViewTemplate</c>, which
    /// always has), leaving <c>CanHorizontallyScroll=true</c> and a REAL horizontal scrollbar/overflow in
    /// the running app instead of the intended ellipsis. This test instead loads the REAL
    /// <c>ListViewTemplate</c> <see cref="ListBox"/> element (so it reads whatever value that XAML
    /// actually sets for the <c>ScrollViewer.HorizontalScrollBarVisibility</c> attached property — this
    /// test fails against the pre-fix-cycle-2 markup, confirmed by a temporary local revert before writing
    /// the fix), gives it a minimal-but-faithful <see cref="ControlTemplate"/> (a real
    /// <see cref="ScrollViewer"/>, <see cref="TemplateBindingExtension"/>-bound to that SAME attached
    /// property, hosting a real <see cref="ItemsPresenter"/>/<see cref="StackPanel"/> — the "give it the
    /// smallest real template it needs, skip the OS theme" convention this file's own
    /// <see cref="MaterializePanelChromeTemplate"/>/<c>ListReorderGeometryHitTestTests</c> already
    /// establish, since the OS/theme default <see cref="ListBox"/> template does not reliably realize
    /// containers in this headless test host), lays it out at a narrow width with a very long
    /// <see cref="FileItem.DisplayName"/>, and asserts against the REAL generated <see cref="ListBoxItem"/>
    /// container and its REAL descendant <see cref="TextBlock"/> — not a hand-simulated stand-in.
    /// </summary>
    [Fact]
    public void ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow()
    {
        var (containerWidth, labelWidth, availableWidth) = RunOnStaThread(() =>
        {
            var originalHighContrast = HighContrastState.IsActive;
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            try
            {
                app.Resources = new ResourceDictionary();
                MergeResourceDictionaries(app, highContrast: false);

                var listTemplate = (DataTemplate)app.Resources["ListViewTemplate"];
                var listBox = (ListBox)listTemplate.LoadContent();

                // Minimal-but-faithful template: a real ScrollViewer, TemplateBound to the SAME
                // ScrollViewer.HorizontalScrollBarVisibility attached property ListViewTemplate's own XAML
                // sets on `listBox` above, hosting a real ItemsPresenter — see this test's own remarks for
                // why the OS default ListBox template is not used here instead.
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

                // Plain (non-virtualizing) vertical StackPanel — the same simplification
                // ListReorderGeometryHitTestTests/AdaptiveIconGridPanelTests already use for their own
                // ItemsPanel in this headless host, and irrelevant to the mechanism under test: a
                // VirtualizingStackPanel's own IScrollInfo machinery is not what CanHorizontallyScroll
                // drives here — ScrollContentPresenter's physical-scrolling Measure behavior (infinite vs.
                // constrained width) applies identically to any Panel content.
                listBox.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));

                listBox.DataContext = new List<FileItem>
                {
                    new(
                        path: @"C:\workspace-grid-07-visual\" + new string('A', 200) + ".txt",
                        icon: null,
                        displayName: new string('A', 200)),
                };

                const double narrow = 150d;
                listBox.Measure(new Size(narrow, double.PositiveInfinity));
                listBox.Arrange(new Rect(0, 0, narrow, listBox.DesiredSize.Height));
                listBox.UpdateLayout();

                var container = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(0)!;
                var label = FindVisualChildByName<TextBlock>(container, "DisplayNameText")!;

                return (container.ActualWidth, label.ActualWidth, narrow);
            }
            finally
            {
                HighContrastState.IsActive = originalHighContrast;

                if (!ownsApplication)
                    app.Resources = originalResources!;
            }
        });

        Assert.True(
            containerWidth <= availableWidth + 0.5,
            $"The real generated ListBoxItem container ({containerWidth:F1}) must not overflow the " +
            $"ListBox's own available width ({availableWidth:F1}) — a wider container means a horizontal " +
            "scrollbar/clip instead of the intended ellipsis.");
        Assert.True(
            labelWidth <= availableWidth,
            $"List-mode label TextBlock ({labelWidth:F1}) must stay within the item's available width " +
            $"({availableWidth:F1}) even for a very long DisplayName, through the REAL ListBox/ScrollViewer pipeline.");
    }

    /// <summary>
    /// Depth-first visual-tree search for a descendant of type <typeparamref name="T"/> named
    /// <paramref name="name"/> — used only by
    /// <see cref="ListViewTemplate_NarrowWidth_RealListBoxClampsLabelAndHasNoHorizontalOverflow"/> to reach
    /// the generated item template's <c>DisplayNameText</c> <see cref="TextBlock"/> through a real
    /// <see cref="ListBoxItem"/> container's own <see cref="ContentPresenter"/>.
    /// </summary>
    private static T? FindVisualChildByName<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name)
                return typed;

            var descendant = FindVisualChildByName<T>(child, name);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    /// <summary>
    /// Runs <paramref name="func"/> to completion on a brand-new STA thread and returns its result —
    /// see this class's own remarks for why STA is required here.
    /// </summary>
    /// <remarks>
    /// M-2 fix (P08 fix cycle 2): delegates to <see cref="StaTestRunner.Run{T}"/> instead of a
    /// hand-rolled duplicate of its STA-thread/ownership machinery — this class's own former copy kept
    /// the owning thread pumping forever (exactly like <see cref="StaTestRunner"/> does — see its own
    /// remarks for why that is required, not optional), but with NO
    /// <see cref="Dispatcher.UnhandledException"/> absorber, so a leaked callback throwing on that
    /// pumped-forever thread was an unguarded process kill. <see cref="StaTestRunner.Run{T}"/> already
    /// closes that gap.
    /// </remarks>
    private static T RunOnStaThread<T>(Func<T> func) => StaTestRunner.Run(func);

    /// <summary>
    /// Mirrors <c>DesktopOrganizer.UI.App</c>'s private <c>LoadResourceDictionaries()</c>
    /// (src/DesktopOrganizer.UI/App.xaml.cs) — same 9 dictionaries, same relative pack URIs, same
    /// conditional <c>Colors.HighContrast.xaml</c> insertion point between <c>Colors.xaml</c> and
    /// <c>Spacing.xaml</c>. Keep this in sync if that method's list/order ever changes. Driven by an
    /// explicit <paramref name="highContrast"/> bool instead of the real
    /// <see cref="SystemParameters.HighContrast"/> (a live OS accessibility setting a test can't flip)
    /// so both merge orders are exercised deterministically regardless of the CI machine's actual
    /// accessibility settings.
    /// </summary>
    private static void MergeResourceDictionaries(Application app, bool highContrast)
    {
        HighContrastState.IsActive = highContrast;

        var merged = app.Resources.MergedDictionaries;
        merged.Add(LoadResourceDictionary("/Resources/Styles/Colors.xaml"));

        if (highContrast)
            merged.Add(LoadResourceDictionary("/Resources/Styles/Colors.HighContrast.xaml"));

        merged.Add(LoadResourceDictionary("/Resources/Styles/Spacing.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Brushes.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Typography.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Icons.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Controls.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Styles/Panels.xaml"));
        merged.Add(LoadResourceDictionary("/Resources/Templates.xaml"));
    }

    private static ResourceDictionary LoadResourceDictionary(string relativePackUri)
        => new() { Source = new Uri($"/DesktopOrganizer.UI;component{relativePackUri}", UriKind.Relative) };

    /// <summary>
    /// Walks every merged dictionary in the chain (recursively, in case any of them nest their own
    /// <see cref="ResourceDictionary.MergedDictionaries"/>) and touches every key via the indexer — this
    /// forces a deferred <c>Style</c>/<c>ControlTemplate</c> (and any <c>StaticResource</c>/<c>x:Static</c>
    /// it references) to actually resolve, instead of merely sitting unparsed in the dictionary.
    /// </summary>
    private static void TouchAllResourceKeys(ResourceDictionary dictionary)
    {
        foreach (ResourceDictionary child in dictionary.MergedDictionaries)
            TouchAllResourceKeys(child);

        foreach (var key in new ArrayList(dictionary.Keys))
            _ = dictionary[key];
    }

    /// <summary>
    /// <see cref="DataTemplate.LoadContent"/> is what actually materializes
    /// <c>PanelItemContainerStyle</c>'s <c>ControlTemplate</c> (reached via <c>ListViewTemplate</c>/
    /// <c>GridViewTemplate</c>'s <c>ItemContainerStyle="{StaticResource PanelItemContainerStyle}"</c>) —
    /// a plain key lookup (<see cref="TouchAllResourceKeys"/>) alone is not enough to reproduce
    /// CRITICAL-1; only loading the template's content is.
    /// </summary>
    private static void LoadTemplateContent(Application app, string templateKey)
    {
        var template = (DataTemplate)app.Resources[templateKey];
        template.LoadContent();
    }

    /// <summary>
    /// MAJOR-D fix (review round 3): <see cref="TouchAllResourceKeys"/> plus <see cref="LoadTemplateContent"/>
    /// above touch every dictionary KEY and materialize <c>PanelItemContainerStyle</c>'s
    /// <see cref="ControlTemplate"/> content (via <c>ListViewTemplate</c>/<c>GridViewTemplate</c>'s
    /// <c>ItemContainerStyle</c>), but neither one ever parses <c>Panels.xaml</c>'s <c>PanelChrome</c>
    /// implicit-<see cref="Style"/> <see cref="ControlTemplate"/> BODY — a plain key lookup only returns
    /// the still-deferred <see cref="Style"/> object; its <c>ControlTemplate</c> setter's content is only
    /// actually parsed when a real <see cref="PanelChrome"/> instance applies that template. Proven
    /// empirically by a reviewer temporarily typo-ing the <c>AccentIndicatorWidth</c>
    /// <c>StaticResource</c> reference inside that template in a prior round: <c>dotnet build</c> stayed
    /// clean and every other test (including the two above) stayed green, while a real render would have
    /// thrown <see cref="System.Windows.Markup.XamlParseException"/>. Constructing a
    /// <see cref="PanelChrome"/>, applying its (implicit, type-keyed) default style, and forcing
    /// Measure/Arrange is what actually reproduces that failure mode here.
    /// </summary>
    private static void MaterializePanelChromeTemplate(Application app)
    {
        var style = (Style)app.Resources[typeof(PanelChrome)];
        var chrome = new PanelChrome { Style = style };

        chrome.ApplyTemplate();
        chrome.Measure(new Size(400, 300));
        chrome.Arrange(new Rect(0, 0, 400, 300));
    }
}
