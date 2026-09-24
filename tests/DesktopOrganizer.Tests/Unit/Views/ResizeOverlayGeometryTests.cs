using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

// ChildWindowModel.Position/Size are System.Drawing.Point/Size (matching Win32 window geometry), NOT
// System.Windows.Point/Size — same aliasing ChildWindowInitializeTests/ChildWindowSizingGridTests use.
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// P09 defect-list regression (release-gate MAJOR/P1): <c>ResizeOverlay</c>'s (<c>Views/ChildWindow.xaml</c>)
/// four corner <see cref="Thumb"/>s are documented (<c>PanelMetrics.ResizeCornerThickness</c>'s own XML doc,
/// docs/PLANS/workspace-grid-07-visual.md fix-cycle 2 MAJOR-A) to be 8x8 DIP, matching the native
/// <c>WM_NCHITTEST</c> path's <c>NativeHitTestMapper.Map</c> corner band — but the fix-cycle 2 XAML gave
/// each corner <see cref="Thumb"/> an explicit <c>Width</c>/<c>Height</c> of 8 while leaving it inside a
/// Grid cell sized to <c>PanelMetrics.ResizeEdgeThickness</c> (6), relying on <c>HorizontalAlignment</c>/
/// <c>VerticalAlignment</c> to "overflow" the extra 2 DIP. WPF's automatic layout-clip
/// (<see cref="FrameworkElement"/>'s internal <c>NeedsClipBounds</c> mechanism — entirely separate from
/// <see cref="Panel.ClipToBounds"/>, and NOT bypassed by alignment) clips any child whose resolved
/// <see cref="FrameworkElement.RenderSize"/> exceeds the arrange slot the Grid actually gave it, regardless
/// of alignment — so the "overflow" was silently clipped straight back down to 6x6 on this WPF path, while
/// the native path (which has no such clip) genuinely used 8x8. This class is a real STA regression pin
/// (not a description) for the actual measured/arranged geometry, exercised through a REAL
/// <see cref="ChildWindow"/> (the same harness convention <c>ChildWindowInitializeTests</c>/
/// <c>ChildWindowSizingGridTests</c> establish) rather than a hand-simulated stand-in Grid, so a future
/// regression in either direction (clip reappearing, or the corner/edge cells silently drifting apart from
/// <see cref="PanelMetrics"/> again) fails here instead of only being described in a doc comment.
/// </summary>
/// <remarks>
/// Deliberately does NOT call <see cref="Window.Show"/> (unlike, e.g.,
/// <c>AdaptiveIconGridPanelLayoutPolicyBindingTests</c>) — a real, on-Windows full-suite run confirmed
/// empirically that showing this class' <see cref="ChildWindow"/> (a genuine native <c>HWND</c>, unlike
/// every OTHER <c>ChildWindow</c> harness in this project) crashes the ENTIRE `dotnet test` HOST PROCESS
/// later in the run: some unrelated, pre-existing test's own leaked (never-disposed)
/// <see cref="ChildWindowViewModel"/> existence-check <see cref="System.Threading.Timer"/> throws an
/// unhandled exception trying to <see cref="Dispatcher.Invoke(Delegate)"/> back onto
/// <see cref="Application.Current"/>'s own <see cref="Dispatcher"/> once this class' real, shown
/// <see cref="ChildWindow"/> makes that dispatcher's owning thread reachable through
/// <see cref="Application.Windows"/>-driven <see cref="Window.Close"/>/<c>ShutdownMode</c> machinery in a
/// way none of this project's PRE-EXISTING (never-shown) <c>ChildWindow</c> harnesses ever do (they have
/// safely tolerated the SAME kind of leaked timer for many fix cycles, precisely because no real
/// <c>HWND</c>/<see cref="Application.Windows"/> registration is ever involved). Sidestepped entirely by
/// never showing the window: instead, this class reaches directly for the extracted <c>ResizeOverlay</c>
/// <see cref="Grid"/> (via <see cref="FrameworkElement.FindName(string)"/>, which resolves against the
/// LOGICAL tree registered during <c>InitializeComponent()</c> regardless of whether the window is ever
/// shown) and calls <see cref="UIElement.Measure(Size)"/>/<see cref="UIElement.Arrange(Rect)"/> on THAT
/// element directly — the same "measure/arrange a loose <see cref="FrameworkElement"/> with no live
/// <see cref="PresentationSource"/> at all" pattern <c>Unit.Resources.ResourceDictionaryMergeTests</c>'
/// own List-mode label tests already use successfully (a <see cref="Grid"/> does not care whether it
/// happens to be reachable from a shown <see cref="Window"/> — <c>Measure</c>/<c>Arrange</c> are pure
/// layout math, not <c>HWND</c>-dependent). A bare <see cref="ChildWindow.Measure(Size)"/>/
/// <see cref="ChildWindow.Arrange(Rect)"/> pair on the WHOLE, unshown <see cref="Window"/> itself was tried
/// first and did NOT work (confirmed empirically — every descendant's <c>ActualWidth</c>/<c>ActualHeight</c>
/// stayed 0); a <see cref="Window"/>'s OWN top-level <c>MeasureOverride</c>/<c>ArrangeOverride</c>
/// participation appears to require a live <see cref="PresentationSource"/>/layout manager in a way a
/// plain descendant <see cref="Grid"/> simply does not.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeOverlayGeometryTests
{
    private const double WindowExtent = 200d;

    [Fact]
    public void ResizeOverlay_AfterMeasureArrange200x200_CornerThumbsAre8x8Unclipped_AndAtTheirGeometricCorner()
    {
        var snapshot = RunOnStaThread(() => MeasureResizeOverlay(WindowExtent, WindowExtent));

        foreach (var tag in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
        {
            var corner = snapshot.Corners[tag];

            Assert.True(corner.HasClip == false, $"{tag}: corner Thumb must not be auto-clipped by its Grid cell (LayoutInformation.GetLayoutClip should be null).");
            Assert.Equal(PanelMetrics.ResizeCornerThickness, corner.ActualWidth);
            Assert.Equal(PanelMetrics.ResizeCornerThickness, corner.ActualHeight);
        }

        // Each corner Thumb's own top-left, translated into the ResizeOverlay Grid's coordinate space,
        // must sit exactly at the panel's geometric corner (0,0 / W-8,0 / 0,H-8 / W-8,H-8) — not offset
        // inward by the pre-fix "effectively 6x6, positioned at the edge cell" clip artifact.
        var corner8 = PanelMetrics.ResizeCornerThickness;
        AssertTopLeft(snapshot.Corners["TopLeft"], new Point(0, 0));
        AssertTopLeft(snapshot.Corners["TopRight"], new Point(WindowExtent - corner8, 0));
        AssertTopLeft(snapshot.Corners["BottomLeft"], new Point(0, WindowExtent - corner8));
        AssertTopLeft(snapshot.Corners["BottomRight"], new Point(WindowExtent - corner8, WindowExtent - corner8));
    }

    [Fact]
    public void ResizeOverlay_AfterMeasureArrange200x200_StraightEdgeThumbsStayResizeEdgeThicknessAndUnclipped()
    {
        var snapshot = RunOnStaThread(() => MeasureResizeOverlay(WindowExtent, WindowExtent));

        foreach (var tag in new[] { "Top", "Bottom" })
        {
            var edge = snapshot.Edges[tag];
            Assert.False(edge.HasClip, $"{tag}: straight-edge Thumb must not be auto-clipped by its Grid cell.");
            Assert.Equal(PanelMetrics.ResizeEdgeThickness, edge.ActualHeight);
        }

        foreach (var tag in new[] { "Left", "Right" })
        {
            var edge = snapshot.Edges[tag];
            Assert.False(edge.HasClip, $"{tag}: straight-edge Thumb must not be auto-clipped by its Grid cell.");
            Assert.Equal(PanelMetrics.ResizeEdgeThickness, edge.ActualWidth);
        }

        // The Top/Bottom edges must reach the window's own left/right edges via the corner Thumbs'
        // adjacent coverage (i.e. they start no later than ResizeCornerThickness from either side) —
        // no gap between "where a corner Thumb's own hit area ends" and "where the straight edge begins".
        var topLeft = snapshot.Edges["Top"].TopLeft;
        Assert.True(topLeft.X <= PanelMetrics.ResizeCornerThickness + 0.01, $"Top edge Thumb must begin at/before the corner boundary (X={topLeft.X}).");
    }

    private static ResizeOverlaySnapshot MeasureResizeOverlay(double width, double height)
    {
        using var harness = ResizeOverlayHarness.Build();

        var model = new ChildWindowModel
        {
            ViewMode = PanelViewMode.Grid,
            IconSize = 48d,
            GeometryMode = PanelGeometryMode.LegacyFreeform,
            SizingMode = PanelSizingMode.Manual,
            Position = new DrawingPoint(0, 0),
            Size = new DrawingSize((int)width, (int)height),
        };
        harness.Window.Initialize(model);

        var overlay = (Grid)harness.Window.FindName("ResizeOverlay")!;

        // Measure/Arrange the extracted Grid directly — see this class' own remarks for why the whole
        // Window is never Measured/Arranged/Shown here.
        overlay.Measure(new Size(width, height));
        overlay.Arrange(new Rect(0, 0, width, height));
        overlay.UpdateLayout();

        var thumbsByTag = overlay.Children
            .OfType<Thumb>()
            .ToDictionary(t => (string)t.Tag);

        var corners = new Dictionary<string, ThumbSnapshot>();
        foreach (var tag in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
            corners[tag] = Snapshot(thumbsByTag[tag], overlay);

        var edges = new Dictionary<string, ThumbSnapshot>();
        foreach (var tag in new[] { "Top", "Bottom", "Left", "Right" })
            edges[tag] = Snapshot(thumbsByTag[tag], overlay);

        return new ResizeOverlaySnapshot(corners, edges);
    }

    private static ThumbSnapshot Snapshot(Thumb thumb, Grid overlay)
    {
        var topLeft = thumb.TranslatePoint(new Point(0, 0), overlay);
        var hasClip = LayoutInformation.GetLayoutClip(thumb) is not null;
        return new ThumbSnapshot(thumb.ActualWidth, thumb.ActualHeight, topLeft, hasClip);
    }

    private static void AssertTopLeft(ThumbSnapshot snapshot, Point expected)
    {
        Assert.True(
            Math.Abs(snapshot.TopLeft.X - expected.X) < 0.01 && Math.Abs(snapshot.TopLeft.Y - expected.Y) < 0.01,
            $"Expected top-left {expected}, got {snapshot.TopLeft}.");
    }

    private sealed record ThumbSnapshot(double ActualWidth, double ActualHeight, Point TopLeft, bool HasClip);

    private sealed record ResizeOverlaySnapshot(
        Dictionary<string, ThumbSnapshot> Corners,
        Dictionary<string, ThumbSnapshot> Edges);

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // STA/resource-chain infrastructure — duplicated from Unit.Views.ChildWindowInitializeTests (this
    // codebase's established convention of duplicating small STA/chrome harnesses per test class rather
    // than sharing one; see that class's own remarks for why). Never calls Window.Show()/Close() — see
    // this class' own remarks for why.
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    private sealed class ResizeOverlayHarness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private ResizeOverlayHarness(Application app, ResourceDictionary? originalResources, bool ownsApplication, ChildWindow window)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Window = window;
        }

        public ChildWindow Window { get; }

        public static ResizeOverlayHarness Build()
        {
            // M-2 fix (P08 fix cycle 2): ownership decided by reference-comparing
            // WpfApplicationHost.EnsureApplication()'s return value against the pre-call ambient state,
            // not by a separate "was it null" probe — see ResourceDictionaryMergeTests's own remarks.
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            MergeResourceDictionaries(app);

            app.Resources["LayoutEditMode"] = Mock.Of<ILayoutEditModeService>();

            var iconProvider = new Mock<IIconProvider>();
            iconProvider.Setup(p => p.GetIcon(It.IsAny<string>())).Returns((ImageSource)null!);

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

            // MoveAndResize (not the default MoveOnly/empty-string fallback, ADR-026 phase-6 D-2) — this
            // test measures ResizeOverlay's own Thumb geometry, which only matters while resize gestures
            // are actually allowed in Normal mode; ResizeAffordanceGate collapses the whole overlay
            // (Visibility.Collapsed, ActualWidth/Height 0) otherwise, which is real, deliberate product
            // behavior (see ResizeAffordanceGate.cs's own remarks), not something this test should fight.
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

            return new ResizeOverlayHarness(app, originalResources, ownsApplication, window);
        }

        public void Dispose()
        {
            if (!_ownsApplication)
                _app.Resources = _originalResources!;

            // If _ownsApplication, deliberately leave Application.Current/Resources as-is — matches
            // ChildWindowInitializeTests/ChildWindowSizingGridTests' own convention (see
            // RunOnStaThread's remarks for why the owning thread keeps pumping afterward instead of
            // exiting). This class' Window is never Shown/Closed, so there is no ShutdownMode/
            // Application.Windows concern to guard against here at all.
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
    /// Runs <paramref name="func"/> to completion on a brand-new STA thread and returns its result.
    /// Safe here (unlike an earlier draft of this class that also called <see cref="Window.Show"/> — see
    /// this class' own remarks): this harness never shows a real <c>HWND</c>, so there is no cross-thread
    /// <see cref="Application.Windows"/>/dispatcher-ownership concern this pattern would run into.
    /// </summary>
    /// <remarks>
    /// M-2 fix (P08 fix cycle 2): delegates to <see cref="StaTestRunner.Run{T}"/> instead of this
    /// class's own former hand-rolled duplicate of the STA-thread/ownership machinery (itself copied from
    /// <c>ChildWindowInitializeTests.RunOnStaThread{T}</c>, which kept an owning thread pumping
    /// (<see cref="Dispatcher.Run()"/>) forever with NO <see cref="Dispatcher.UnhandledException"/>
    /// absorber — a leaked callback throwing on that pumped-forever thread was an unguarded process kill.
    /// <see cref="StaTestRunner.Run{T}"/> already closes that gap.
    /// </remarks>
    private static T RunOnStaThread<T>(Func<T> func) => StaTestRunner.Run(func);
}
