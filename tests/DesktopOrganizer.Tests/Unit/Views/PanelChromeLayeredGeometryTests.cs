using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

// ChildWindowModel.Position/Size are System.Drawing.Point/Size (matching Win32 window geometry), NOT
// System.Windows.Point/Size — same aliasing Unit.Views.ResizeOverlayGeometryTests/ChildWindowInitializeTests use.
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.3, T-V1/T-V2/T-V3)
/// — real-window geometry pins for the layered chrome viewed ALONGSIDE its real window siblings
/// (<c>ResizeOverlay</c>, <c>TitleText</c>, the header button group) — as opposed to
/// <c>Unit.Resources.ResourceDictionaryMergeTests</c>' own isolated-<see cref="PanelChrome"/> pins (T-R*),
/// which construct a bare <see cref="PanelChrome"/> directly against its style and never touch a real
/// <see cref="ChildWindow"/> at all. Same harness convention as <c>Unit.Views.ResizeOverlayGeometryTests</c>
/// (real <see cref="ChildWindow"/>, <see cref="Window.Show"/> never called — see that class's own remarks
/// for why a shown, real <c>HWND</c> here crashes the whole `dotnet test` host process later in the run)
/// — duplicated per this codebase's established "one small STA/chrome harness per test class" convention
/// (see <c>ResizeOverlayGeometryTests</c>' own remarks on that point) rather than shared.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelChromeLayeredGeometryTests
{
    private const double WindowExtent = 200d;

    /// <summary>
    /// T-V1: in a real, unshown <see cref="ChildWindow"/> (200x200) — <c>ResizeOverlay</c> still occupies
    /// the WHOLE window rect (unchanged, plan §0 "0 изменённых строк в ChildWindow.xaml"), while
    /// <c>PART_SurfaceLayer</c> (the layered chrome's own inset content root, plan D-1/D-2) is shrunk by
    /// <see cref="PanelMetrics.ChromeOuterInset"/> on every side — i.e. the 2-DIP ring between the window
    /// edge and <c>PART_SurfaceLayer</c>'s own edge, made nearly transparent by the inset, must lie ENTIRELY
    /// inside both the straight-edge (<see cref="PanelMetrics.ResizeEdgeThickness"/>) and corner
    /// (<see cref="PanelMetrics.ResizeCornerThickness"/>) resize strips (plan D-5's hit-catcher rationale:
    /// the resize gesture must still start immediately at the outer ring, not partway in).
    /// </summary>
    [Fact]
    public void ResizeOverlay_SpansWholeWindow_AndFullyCoversTheChromeInsetRing()
    {
        var snapshot = RunOnStaThread(() => MeasureLayeredChrome(WindowExtent, WindowExtent));

        Assert.Equal(WindowExtent, snapshot.OverlayWidth);
        Assert.Equal(WindowExtent, snapshot.OverlayHeight);

        var inset = PanelMetrics.ChromeOuterInset;
        Assert.Equal(
            new Rect(inset, inset, WindowExtent - (2 * inset), WindowExtent - (2 * inset)),
            snapshot.SurfaceLayerRect);

        Assert.True(
            inset <= PanelMetrics.ResizeEdgeThickness,
            $"ChromeOuterInset ({inset}) must be <= ResizeEdgeThickness ({PanelMetrics.ResizeEdgeThickness}) for the straight-edge resize strips to fully cover the inset ring.");
        Assert.True(
            inset <= PanelMetrics.ResizeCornerThickness,
            $"ChromeOuterInset ({inset}) must be <= ResizeCornerThickness ({PanelMetrics.ResizeCornerThickness}) for the corner resize strips to fully cover the inset ring.");
    }

    /// <summary>
    /// T-V2: at maximum accent-marker emphasis (<see cref="PanelVisualState.Resizing"/> while
    /// <c>IsLayoutEditing</c>, plan D-4's own collision-analysis combination), none of the curved marker's
    /// three segments — the vertical stem, the horizontal tail (each inflated by half the marker's own
    /// real, state-driven <see cref="Shape.StrokeThickness"/>), and the corner arc (inflated by half the
    /// marker's own maximum possible <see cref="Shape.StrokeThickness"/>, see remarks) — intersects the
    /// real <c>TitleText</c> element's own rendered bounding rect.
    /// </summary>
    /// <remarks>
    /// Replaces a prior version of this test that compared <c>TitleText</c> against the marker's single
    /// COMBINED axis-aligned bounding box (<see cref="Geometry.Bounds"/> of the WHOLE
    /// <see cref="Path.Data"/> — stem + arc + tail together, inflated as one rectangle). That combined box
    /// has an empty corner: the marker's real ink never fills the box's own far corner (the diagonal arc
    /// cuts across it), yet the combined-box intersection test flagged that empty corner as if it were
    /// painted, producing a false red. This version checks the marker's real geometry SEGMENT BY SEGMENT
    /// instead — the stem's own local rect is <c>x ∈ [-s/2, s/2]</c>, <c>y ∈ [R, R+stem]</c> inflated by
    /// <c>s/2</c>; the tail's own local rect is <c>y ∈ [-s/2, s/2]</c>, <c>x ∈ [R, R+tail]</c> inflated by
    /// <c>s/2</c> — where <c>R</c>/<c>stem</c>/<c>tail</c>/<c>s</c> are all read from the REAL
    /// <c>Radius.Panel</c> resource, <see cref="PanelMetrics.AccentMarkerStemLength"/>/
    /// <see cref="PanelMetrics.AccentMarkerTailLength"/>, and the marker's own real
    /// <see cref="Shape.StrokeThickness"/> (matching <c>AccentMarkerGeometryConverter</c>'s own segment
    /// layout — see that converter's remarks), never hand-copied literals.
    /// <para>
    /// The corner arc (<c>AccentMarkerGeometryConverter</c>'s <see cref="System.Windows.Media.ArcSegment"/>
    /// from <c>(0, R)</c> to <c>(R, 0)</c>, radius <c>R</c>, centered at <c>(R, R)</c> — the usual rounded-
    /// corner construction, bulging TOWARD the true corner <c>(0, 0)</c>, never past it) has EXACT stroked
    /// bounds of <c>x ∈ [-h, R]</c>, <c>y ∈ [-h, R]</c>, where <c>h</c> is half the stroke thickness — NOT
    /// the naive <c>[-h, R+h]</c> a "just inflate the raw path's own <c>[0,R]x[0,R]</c> bounds by h on every
    /// side" shortcut would suggest (that shortcut overshoots by exactly <c>h</c> on the far/outer edge: the
    /// arc's outward normal only ever reaches full magnitude <c>h</c> at its own two ENDPOINTS, where the
    /// normal is axis-aligned and <c>x</c>/<c>y</c> individually already cap out at <c>R</c> — everywhere
    /// else along the arc the normal is off-axis, so its per-axis contribution is strictly less than
    /// <c>h</c>; the corner point <c>(R, R)</c> itself is the arc's own CENTER, never touched by ink at
    /// all). <c>h</c> here is half <see cref="PanelMetrics.AccentMarkerMaxStrokeThickness"/> — the marker's
    /// own worst-case stroke width across every <see cref="PanelVisualState"/> — rather than this call's own
    /// (already-maximum, at Resizing+editing) <see cref="Shape.StrokeThickness"/>, so the pin stays valid
    /// even if a future state ever exceeds today's Resizing+editing combination. The arc's own quadrant
    /// therefore stays entirely within its own <c>[0, R]</c> square (never crossing into where
    /// <c>TitleText</c> lives, well to the right of <c>R</c>) — the exact bounds are asserted directly
    /// below, with ≈4 DIP of margin to <c>TitleText</c>'s own left edge (the header's own
    /// <c>ContentSidePadding</c> + the title editor's own 10-DIP <c>Margin</c>,
    /// <c>Views/ChildWindow.xaml</c>) — enough margin to also catch a future growth of <c>Radius.Panel</c>
    /// itself, not just today's value.
    /// </para>
    /// </remarks>
    [Fact]
    public void AccentMarker_AtMaximumEmphasis_DoesNotIntersectTitleText()
    {
        var snapshot = RunOnStaThread(() => MeasureLayeredChrome(
            WindowExtent,
            300d,
            visualState: PanelVisualState.Resizing,
            isLayoutEditing: true));

        Assert.False(
            snapshot.MarkerStemRect.IntersectsWith(snapshot.TitleRect),
            $"Marker stem's own inflated bounding rect ({snapshot.MarkerStemRect}) must not intersect TitleText's own bounding rect ({snapshot.TitleRect}) at maximum emphasis.");
        Assert.False(
            snapshot.MarkerTailRect.IntersectsWith(snapshot.TitleRect),
            $"Marker tail's own inflated bounding rect ({snapshot.MarkerTailRect}) must not intersect TitleText's own bounding rect ({snapshot.TitleRect}) at maximum emphasis.");
        Assert.False(
            snapshot.MarkerArcRect.IntersectsWith(snapshot.TitleRect),
            $"Marker arc's own exact-bounds rect ({snapshot.MarkerArcRect}) must not intersect TitleText's own bounding rect ({snapshot.TitleRect}) at maximum emphasis.");
    }

    /// <summary>
    /// T-V3: the real, rendered header button group (<c>SortModeToggleButton</c>, sharing its row with
    /// View/RollUp/Overflow) stays within <c>y ∈ [2, 30]</c> in <see cref="PanelChrome"/>-local (= window)
    /// coordinates — unchanged from before ADR-030 (plan D-3's own table: "Кнопки заголовка (экран)
    /// y∈[2,30] нет [изменений]") — and the real overlap between the top straight-edge resize strip
    /// (<c>y ∈ [0, ResizeEdgeThickness)</c>) and the button's own real top margin never exceeds the value
    /// <c>Unit.Models.PanelMetricsTests.ResizeEdgeThickness_TopStrip_OverlapWithHeaderButtonTopMargin_IsReducedFromFixCycle1</c>
    /// already pins analytically (4 DIP) — confirming that pin still matches the REAL, rendered layout after
    /// the ADR-030 layered-chrome template change, not just the pure-math model.
    /// </summary>
    [Fact]
    public void HeaderButton_StaysWithinOriginalScreenYRange_AndTopStripOverlapDoesNotGrow()
    {
        var snapshot = RunOnStaThread(() => MeasureLayeredChrome(WindowExtent, 300d));

        Assert.True(
            snapshot.HeaderButtonRect.Top >= 2d - 0.01,
            $"Header button top ({snapshot.HeaderButtonRect.Top}) must stay at/after y=2.");
        Assert.True(
            snapshot.HeaderButtonRect.Bottom <= 30d + 0.01,
            $"Header button bottom ({snapshot.HeaderButtonRect.Bottom}) must stay at/before y=30.");

        var realTopStripOverlap = Math.Max(0d, PanelMetrics.ResizeEdgeThickness - snapshot.HeaderButtonRect.Top);
        var pinnedTopStripOverlap = PanelMetrics.ResizeEdgeThickness
            - ((PanelMetrics.HeaderHeight - PanelMetrics.HeaderButtonSize) / 2d);

        Assert.True(
            realTopStripOverlap <= pinnedTopStripOverlap + 0.01,
            $"Real top-strip/header-button overlap ({realTopStripOverlap}) must not exceed PanelMetricsTests' own pinned value ({pinnedTopStripOverlap}) after the ADR-030 chrome inset.");
    }

    private static LayeredChromeSnapshot MeasureLayeredChrome(
        double width,
        double height,
        PanelVisualState visualState = PanelVisualState.Idle,
        bool isLayoutEditing = false)
    {
        using var harness = LayeredChromeHarness.Build();

        var model = new ChildWindowModel
        {
            Title = "Sample Panel Title",
            ViewMode = PanelViewMode.Grid,
            IconSize = 48d,
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            SizingMode = PanelSizingMode.Manual,
            Position = new DrawingPoint(0, 0),
            Size = new DrawingSize((int)width, (int)height),
        };
        harness.Window.Initialize(model);

        var panelRoot = (Grid)harness.Window.FindName("PanelRoot")!;
        PanelTheme.SetVisualState(panelRoot, visualState);
        PanelTheme.SetIsLayoutEditing(panelRoot, isLayoutEditing);

        var overlay = (Grid)harness.Window.FindName("ResizeOverlay")!;
        overlay.Measure(new Size(width, height));
        overlay.Arrange(new Rect(0, 0, width, height));
        overlay.UpdateLayout();

        var chrome = panelRoot.Children.OfType<PanelChrome>().Single();
        chrome.ApplyTemplate();
        chrome.Measure(new Size(width, height));
        chrome.Arrange(new Rect(0, 0, width, height));
        chrome.UpdateLayout();

        var surfaceLayer = (FrameworkElement)chrome.Template.FindName("PART_SurfaceLayer", chrome)!;
        var surfaceLayerTopLeft = surfaceLayer.TranslatePoint(new Point(0, 0), chrome);
        var surfaceLayerRect = new Rect(surfaceLayerTopLeft, new Size(surfaceLayer.ActualWidth, surfaceLayer.ActualHeight));

        // M-4 fix: the marker HOST (PART_AccentIndicatorHost), not the Path itself, is what is anchored
        // top-left within PART_SurfaceLayer with no Margin — its own TranslatePoint(0,0) is therefore the
        // real origin of the marker's LOCAL coordinate space (the same (0,0) AccentMarkerGeometryConverter
        // builds Path.Data against), read from the real, already-arranged visual tree rather than hand-
        // derived.
        var markerHost = (FrameworkElement)chrome.Template.FindName(PanelChrome.PartAccentIndicatorHost, chrome)!;
        var marker = (Path)chrome.Template.FindName("PART_AccentIndicator", chrome)!;
        var markerOrigin = markerHost.TranslatePoint(new Point(0, 0), chrome);

        var radiusPanel = ((CornerRadius)Application.Current!.Resources["Radius.Panel"]).TopLeft;
        var stem = PanelMetrics.AccentMarkerStemLength;
        var tail = PanelMetrics.AccentMarkerTailLength;
        var halfStroke = marker.StrokeThickness / 2d;

        // Segment-by-segment local rects matching AccentMarkerGeometryConverter's own path layout (see
        // that converter's remarks): the vertical stem runs (0, R+stem) -> (0, R); the horizontal tail runs
        // (R, 0) -> (R+tail, 0). Each is inflated by half the marker's own real StrokeThickness (round
        // end-caps/joins extend a stroke's visual bounds that far beyond its geometric path).
        var stemLocalRect = new Rect(0, radiusPanel, 0, stem);
        stemLocalRect.Inflate(halfStroke, halfStroke);
        var markerStemRect = new Rect(
            markerOrigin.X + stemLocalRect.X,
            markerOrigin.Y + stemLocalRect.Y,
            stemLocalRect.Width,
            stemLocalRect.Height);

        var tailLocalRect = new Rect(radiusPanel, 0, tail, 0);
        tailLocalRect.Inflate(halfStroke, halfStroke);
        var markerTailRect = new Rect(
            markerOrigin.X + tailLocalRect.X,
            markerOrigin.Y + tailLocalRect.Y,
            tailLocalRect.Width,
            tailLocalRect.Height);

        // The corner arc's own EXACT stroked bounds (this test's own remarks derive why) — x ∈ [-h, R],
        // y ∈ [-h, R], never the naive [-h, R+h] a blind "inflate the raw path bounds by h" shortcut would
        // give. h reads PanelMetrics.AccentMarkerMaxStrokeThickness (the marker's own worst-case width
        // across every PanelVisualState), not this call's own halfStroke, so the pin stays a true upper
        // bound even if a future state ever exceeds today's Resizing+editing maximum.
        var halfMaxStroke = PanelMetrics.AccentMarkerMaxStrokeThickness / 2d;
        var arcLocalRect = new Rect(-halfMaxStroke, -halfMaxStroke, radiusPanel + halfMaxStroke, radiusPanel + halfMaxStroke);
        var markerArcRect = new Rect(
            markerOrigin.X + arcLocalRect.X,
            markerOrigin.Y + arcLocalRect.Y,
            arcLocalRect.Width,
            arcLocalRect.Height);

        var titleText = (FrameworkElement)harness.Window.FindName("TitleText")!;
        var titleOrigin = titleText.TranslatePoint(new Point(0, 0), chrome);
        var titleRect = new Rect(titleOrigin, new Size(titleText.ActualWidth, titleText.ActualHeight));

        var headerButton = (FrameworkElement)harness.Window.FindName("SortModeToggleButton")!;
        var headerButtonOrigin = headerButton.TranslatePoint(new Point(0, 0), chrome);
        var headerButtonRect = new Rect(headerButtonOrigin, new Size(headerButton.ActualWidth, headerButton.ActualHeight));

        return new LayeredChromeSnapshot(
            overlay.ActualWidth,
            overlay.ActualHeight,
            surfaceLayerRect,
            markerStemRect,
            markerTailRect,
            markerArcRect,
            titleRect,
            headerButtonRect);
    }

    private sealed record LayeredChromeSnapshot(
        double OverlayWidth,
        double OverlayHeight,
        Rect SurfaceLayerRect,
        Rect MarkerStemRect,
        Rect MarkerTailRect,
        Rect MarkerArcRect,
        Rect TitleRect,
        Rect HeaderButtonRect);

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // STA/resource-chain infrastructure — duplicated from Unit.Views.ResizeOverlayGeometryTests (this
    // codebase's established convention of duplicating small STA/chrome harnesses per test class rather
    // than sharing one; see that class's own remarks for why). Never calls Window.Show()/Close().
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    private sealed class LayeredChromeHarness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private LayeredChromeHarness(Application app, ResourceDictionary? originalResources, bool ownsApplication, ChildWindow window)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Window = window;
        }

        public ChildWindow Window { get; }

        public static LayeredChromeHarness Build()
        {
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            MergeResourceDictionaries(app);

            app.Resources["LayoutEditMode"] = Mock.Of<ILayoutEditModeService>();

            var iconProvider = new Mock<IIconProvider>();
            iconProvider.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((System.Windows.Media.ImageSource)null!);

            var viewModel = new ChildWindowViewModel(
                iconProvider.Object,
                Mock.Of<IFileLauncher>(),
                Mock.Of<IFolderWatcherFactory>(),
                Mock.Of<IPanelItemStorageService>(),
                Mock.Of<IFileItemRenameService>(),
                Mock.Of<IAppSettings>(),
                Mock.Of<IPanelSelectionCoordinator>(),
                Mock.Of<IGestureDiagnostics>());

            var appSettings = new Mock<IAppSettings>();
            appSettings.SetupProperty(s => s.NativePanelSizing, false);
            appSettings.SetupProperty(s => s.SnapResizeToIconGrid, false);
            appSettings.SetupProperty(s => s.NormalModeLayoutGestures, nameof(NormalModeLayoutGestures.MoveAndResize));

            var window = new ChildWindow(
                viewModel,
                Mock.Of<IWindowManager>(),
                Mock.Of<IDesktopIntegrationService>(),
                appSettings.Object,
                Mock.Of<IGestureDiagnostics>(),
                Mock.Of<INativePanelSizingService>(),
                (ILayoutEditModeService)app.Resources["LayoutEditMode"],
                Mock.Of<IMonitorService>(),
                Mock.Of<IConfirmationService>());

            return new LayeredChromeHarness(app, originalResources, ownsApplication, window);
        }

        public void Dispose()
        {
            if (!_ownsApplication)
                _app.Resources = _originalResources!;
        }

        private static void MergeResourceDictionaries(Application app)
        {
            var merged = app.Resources.MergedDictionaries;
            foreach (var relativePath in new[]
                     {
                         "/Resources/Styles/Colors.xaml",
                         "/Resources/Styles/Spacing.xaml",
                         "/Resources/Styles/Brushes.xaml",
                         "/Resources/Styles/Typography.xaml",
                         "/Resources/Styles/Icons.xaml",
                         "/Resources/Styles/Controls.xaml",
                         "/Resources/Styles/Panels.xaml",
                         "/Resources/Templates.xaml",
                     })
            {
                merged.Add(new ResourceDictionary { Source = new Uri($"/DesktopOrganizer.UI;component{relativePath}", UriKind.Relative) });
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="func"/> to completion on a brand-new STA thread and returns its result — see
    /// <see cref="LayeredChromeHarness"/>'s own remarks and <c>ResizeOverlayGeometryTests</c>' class-level
    /// remarks for why STA is required and <see cref="Window.Show"/> is never called here.
    /// </summary>
    private static T RunOnStaThread<T>(Func<T> func) => StaTestRunner.Run(func);
}
