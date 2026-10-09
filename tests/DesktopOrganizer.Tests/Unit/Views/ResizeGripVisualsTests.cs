using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// ADR-022 (docs/PLANS/native-resize-single-path.md, ADR-026 D-6): <c>ChildWindow.xaml</c>'s
/// <c>ResizeGripVisuals</c> — the visual-only accent grips of Edit Layout for the native resize path
/// (the only resize path; hit-testing is <c>WM_NCHITTEST</c>). Pins: it is never a hit-test target, it
/// follows the <c>ResizeAffordanceGate</c> (Normal + MoveOnly hides it, Edit Layout shows it), and its
/// geometry equals the <c>NativeHitTestMapper</c> bands (8x8 unclipped corners, 6 DIP straight edges
/// anchored to the outer edge — also the regression pin for WPF's automatic layout clip of children
/// larger than their Grid cell, P09 defect list; see <c>PanelMetrics.ResizeCornerThickness</c> remarks).
/// </summary>
/// <remarks>
/// The window is never shown: a real HWND on a <see cref="ChildWindow"/> in the test host can crash the
/// whole <c>dotnet test</c> process via other tests' leaked existence-check timers marshalling onto
/// <see cref="Application.Current"/>'s dispatcher. Geometry is measured by arranging the extracted grid
/// directly (<see cref="FrameworkElement.FindName(string)"/> resolves against the logical tree registered
/// by <c>InitializeComponent()</c>, shown or not).
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ResizeGripVisualsTests
{
    private const double WindowExtent = 200d;

    private static readonly string[] AllTags =
        ["Top", "Bottom", "Left", "Right", "TopLeft", "TopRight", "BottomLeft", "BottomRight"];

    /// <summary>Verifies the grips are visual-only: the grid and all 8 grips are not hit-test targets.</summary>
    [Fact]
    public void ResizeGripVisuals_IsNeverAHitTestTarget_AndHasTheEightGrips()
    {
        var snapshot = RunOnStaThread(() =>
        {
            using var harness = Harness.Build(NormalModeLayoutGestures.MoveAndResize);
            var grips = harness.Grips;
            return (
                GridHitTestVisible: grips.IsHitTestVisible,
                Tags: grips.Children.OfType<Border>().Select(b => (string)b.Tag).ToList(),
                AnyBorderHitTestVisible: grips.Children.OfType<Border>().Any(b => b.IsHitTestVisible));
        });

        Assert.False(snapshot.GridHitTestVisible);
        Assert.False(snapshot.AnyBorderHitTestVisible);
        Assert.Equal(AllTags.OrderBy(t => t), snapshot.Tags.OrderBy(t => t));
    }

    /// <summary>Verifies Normal mode with the default move-only gesture policy hides the grips.</summary>
    [Fact]
    public void NormalMode_MoveOnly_GripsAreCollapsed()
    {
        var (visibility, hitTestVisible) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build(NormalModeLayoutGestures.MoveOnly);
            return (harness.Grips.Visibility, harness.Grips.IsHitTestVisible);
        });

        Assert.Equal(Visibility.Collapsed, visibility);
        Assert.False(hitTestVisible);
    }

    /// <summary>Verifies Normal mode with "Move and resize" shows the grips (transparent until Edit Layout) but still not hit-testable.</summary>
    [Fact]
    public void NormalMode_MoveAndResize_GripsAreVisible_ButNotHitTestVisible()
    {
        var (visibility, hitTestVisible, isEditing) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build(NormalModeLayoutGestures.MoveAndResize);
            return (harness.Grips.Visibility, harness.Grips.IsHitTestVisible, PanelTheme.GetIsLayoutEditing(harness.Grips));
        });

        Assert.Equal(Visibility.Visible, visibility);
        Assert.False(hitTestVisible);
        Assert.False(isEditing);
    }

    /// <summary>Verifies entering Edit Layout shows the grips (even under move-only) and they inherit the editing flag that tints them.</summary>
    [Fact]
    public void EnteringEditLayout_ShowsTheGrips_AndTheyInheritIsLayoutEditing()
    {
        var snapshot = RunOnStaThread(() =>
        {
            using var harness = Harness.Build(NormalModeLayoutGestures.MoveOnly);
            var before = harness.Grips.Visibility;

            harness.EnterEditLayout();

            var corner = harness.Grips.Children.OfType<Border>().First(b => (string)b.Tag == "TopLeft");
            return (
                Before: before,
                After: harness.Grips.Visibility,
                HitTestVisible: harness.Grips.IsHitTestVisible,
                CornerEditing: PanelTheme.GetIsLayoutEditing(corner));
        });

        Assert.Equal(Visibility.Collapsed, snapshot.Before);
        Assert.Equal(Visibility.Visible, snapshot.After);
        Assert.False(snapshot.HitTestVisible);
        Assert.True(snapshot.CornerEditing);
    }

    /// <summary>Verifies leaving Edit Layout under move-only hides the grips again.</summary>
    [Fact]
    public void LeavingEditLayout_UnderMoveOnly_HidesTheGripsAgain()
    {
        var visibility = RunOnStaThread(() =>
        {
            using var harness = Harness.Build(NormalModeLayoutGestures.MoveOnly);
            harness.EnterEditLayout();
            harness.LeaveEditLayout();
            return harness.Grips.Visibility;
        });

        Assert.Equal(Visibility.Collapsed, visibility);
    }

    /// <summary>Verifies each corner grip is an unclipped 8x8 DIP square at its geometric corner (the NativeHitTestMapper corner band).</summary>
    [Fact]
    public void Geometry_CornerGripsAre8x8Unclipped_AtTheirGeometricCorner()
    {
        var snapshot = RunOnStaThread(MeasureGrips);

        var corner = PanelMetrics.ResizeCornerThickness;
        foreach (var tag in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
        {
            var grip = snapshot[tag];
            Assert.False(grip.HasClip, $"{tag}: corner grip must not be auto-clipped by its Grid cell.");
            Assert.Equal(corner, grip.ActualWidth);
            Assert.Equal(corner, grip.ActualHeight);
        }

        AssertTopLeft(snapshot["TopLeft"], new Point(0, 0));
        AssertTopLeft(snapshot["TopRight"], new Point(WindowExtent - corner, 0));
        AssertTopLeft(snapshot["BottomLeft"], new Point(0, WindowExtent - corner));
        AssertTopLeft(snapshot["BottomRight"], new Point(WindowExtent - corner, WindowExtent - corner));
    }

    /// <summary>Verifies each straight-edge grip is ResizeEdgeThickness thick, unclipped, anchored to its own outer edge.</summary>
    [Fact]
    public void Geometry_EdgeGripsAreResizeEdgeThickness_AnchoredToTheOuterEdge()
    {
        var snapshot = RunOnStaThread(MeasureGrips);

        var edge = PanelMetrics.ResizeEdgeThickness;
        var corner = PanelMetrics.ResizeCornerThickness;

        Assert.False(snapshot["Top"].HasClip);
        Assert.Equal(edge, snapshot["Top"].ActualHeight);
        AssertTopLeft(snapshot["Top"], new Point(corner, 0));

        Assert.False(snapshot["Bottom"].HasClip);
        Assert.Equal(edge, snapshot["Bottom"].ActualHeight);
        AssertTopLeft(snapshot["Bottom"], new Point(corner, WindowExtent - edge));

        Assert.False(snapshot["Left"].HasClip);
        Assert.Equal(edge, snapshot["Left"].ActualWidth);
        AssertTopLeft(snapshot["Left"], new Point(0, corner));

        Assert.False(snapshot["Right"].HasClip);
        Assert.Equal(edge, snapshot["Right"].ActualWidth);
        AssertTopLeft(snapshot["Right"], new Point(WindowExtent - edge, corner));
    }

    /// <summary>
    /// Verifies the four straight-edge strips are square (no CornerRadius) so they meet the corner squares
    /// flush, while the corner squares keep the rounding (decisions review cycle 1, U6).
    /// </summary>
    [Fact]
    public void Visuals_EdgeStripsAreSquare_CornersKeepTheirRounding()
    {
        var radii = RunOnStaThread(() =>
        {
            using var harness = Harness.Build(NormalModeLayoutGestures.MoveAndResize);
            return harness.Grips.Children.OfType<Border>().ToDictionary(b => (string)b.Tag, b => b.CornerRadius);
        });

        foreach (var tag in new[] { "Top", "Bottom", "Left", "Right" })
            Assert.Equal(new CornerRadius(0), radii[tag]);

        foreach (var tag in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
            Assert.NotEqual(new CornerRadius(0), radii[tag]);
    }

    private static Dictionary<string, GripSnapshot> MeasureGrips()
    {
        using var harness = Harness.Build(NormalModeLayoutGestures.MoveAndResize);
        var grips = harness.Grips;

        grips.Measure(new Size(WindowExtent, WindowExtent));
        grips.Arrange(new Rect(0, 0, WindowExtent, WindowExtent));
        grips.UpdateLayout();

        return grips.Children.OfType<Border>().ToDictionary(
            b => (string)b.Tag,
            b => new GripSnapshot(
                b.ActualWidth,
                b.ActualHeight,
                b.TranslatePoint(new Point(0, 0), grips),
                LayoutInformation.GetLayoutClip(b) is not null));
    }

    private static void AssertTopLeft(GripSnapshot snapshot, Point expected)
    {
        Assert.True(
            Math.Abs(snapshot.TopLeft.X - expected.X) < 0.01 && Math.Abs(snapshot.TopLeft.Y - expected.Y) < 0.01,
            $"Expected top-left {expected}, got {snapshot.TopLeft}.");
    }

    private sealed record GripSnapshot(double ActualWidth, double ActualHeight, Point TopLeft, bool HasClip);

    private static T RunOnStaThread<T>(Func<T> func) => StaTestRunner.Run(func);

    // STA/resource-chain harness — the repo's per-class duplicated convention (see ChildWindowInitializeTests).
    // Never shows or closes the window.
    private sealed class Harness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;
        private readonly Mock<ILayoutEditModeService> _layoutEditMode;

        private Harness(
            Application app,
            ResourceDictionary? originalResources,
            bool ownsApplication,
            ChildWindow window,
            Mock<ILayoutEditModeService> layoutEditMode)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            _layoutEditMode = layoutEditMode;
            Window = window;
        }

        public ChildWindow Window { get; }

        public Grid Grips => (Grid)Window.FindName("ResizeGripVisuals")!;

        public static Harness Build(NormalModeLayoutGestures gestures)
        {
            var beforeEnsure = Application.Current;
            var app = WpfApplicationHost.EnsureApplication();
            var ownsApplication = !ReferenceEquals(app, beforeEnsure);
            var originalResources = ownsApplication ? null : app.Resources;

            app.Resources = new ResourceDictionary();
            TestThemeResources.MergeReduced(app);

            var layoutEditMode = new Mock<ILayoutEditModeService>();
            layoutEditMode.SetupGet(s => s.Mode).Returns(LayoutInteractionMode.Normal);
            layoutEditMode.SetupGet(s => s.IsEditing).Returns(false);
            app.Resources["LayoutEditMode"] = layoutEditMode.Object;

            var iconProvider = new Mock<IIconProvider>();
            iconProvider.Setup(p => p.GetIcon(It.IsAny<string>(), It.IsAny<int>())).Returns((System.Windows.Media.ImageSource)null!);

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
            appSettings.SetupProperty(s => s.SnapResizeToIconGrid, false);
            appSettings.SetupProperty(s => s.NormalModeLayoutGestures, gestures.ToString());

            var window = new ChildWindow(
                viewModel,
                Mock.Of<IWindowManager>(),
                Mock.Of<IDesktopIntegrationService>(),
                appSettings.Object,
                Mock.Of<IGestureDiagnostics>(),
                Mock.Of<INativePanelSizingService>(),
                layoutEditMode.Object,
                Mock.Of<IMonitorService>(),
                Mock.Of<IConfirmationService>(),
                Mock.Of<IPanelCustomizeService>(),
                Mock.Of<IPanelItemDragOutSession>(),
                FakeDesktopScale.Uniform().Object);

            window.Initialize(new ChildWindowModel
            {
                ViewMode = PanelViewMode.Grid,
                IconSize = 48d,
                GeometryMode = PanelGeometryMode.LegacyFreeform,
                SizingMode = PanelSizingMode.Manual,
                Position = new DrawingPoint(0, 0),
                Size = new DrawingSize((int)WindowExtent, (int)WindowExtent),
            });

            return new Harness(app, originalResources, ownsApplication, window, layoutEditMode);
        }

        public void EnterEditLayout() => RaiseMode(LayoutInteractionMode.Normal, LayoutInteractionMode.EditLayout);

        public void LeaveEditLayout() => RaiseMode(LayoutInteractionMode.EditLayout, LayoutInteractionMode.Normal);

        public void Dispose()
        {
            if (!_ownsApplication)
                _app.Resources = _originalResources!;
        }

        private void RaiseMode(LayoutInteractionMode from, LayoutInteractionMode to)
        {
            _layoutEditMode.SetupGet(s => s.Mode).Returns(to);
            _layoutEditMode.SetupGet(s => s.IsEditing).Returns(to == LayoutInteractionMode.EditLayout);
            _layoutEditMode.Raise(
                l => l.ModeChanged += null,
                _layoutEditMode.Object,
                new LayoutModeChangedEventArgs(from, to, LayoutModeChangeSource.Programmatic));
        }
    }
}
