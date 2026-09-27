using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;

// ChildWindowModel.Position/Size are System.Drawing.Point/Size (matching Win32 window geometry), NOT
// System.Windows.Point/Size — same aliasing ChildWindow.xaml.cs itself uses.
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// TDD regression coverage for the P05 fix-cycle's MAJOR-1 finding: <see cref="ChildWindow.Initialize"/>
/// must NEVER mutate a loaded panel's persisted <see cref="FrameworkElement.Width"/> — loading a panel is not a
/// user gesture, and ADR-028's "no jump on first launch" invariant applies to EVERY
/// <see cref="PanelGeometryMode"/>, not just <see cref="PanelGeometryMode.GridAligned"/> (see this
/// fix-cycle's ADR-028/ADR-029 amendments). Before the fix, <c>Initialize</c> unconditionally called
/// <c>IPanelResizeController.SnapWidthToGrid()</c> "so persisted sizes stay aligned even after the icon
/// size was changed in a previous session" — harmless before ADR-029's F-3 chrome fix (a numeric no-op,
/// <c>GridMetrics.FileListHPadding</c> was <c>0</c>), but a silent one-time width jump afterwards
/// (<c>FileListHPadding</c> became 8): a persisted 3-column width of 344 DIP became 352, and a persisted
/// 1-column width of 168 DIP became 176, the very first time a panel opened after upgrading.
/// </summary>
/// <remarks>
/// Constructs a REAL <see cref="ChildWindow"/> — no test in this project previously did this (every
/// other View-layer test exercises extracted <see cref="DesktopOrganizer.UI.Behaviors"/> classes
/// instead, see e.g. <c>NativePanelResizeControllerTests</c>'s remarks). Reuses
/// <c>Unit.Resources.ResourceDictionaryMergeTests</c>'s proven "fresh STA thread, swap in a throwaway
/// <see cref="ResourceDictionary"/>, restore the original in <see langword="finally"/>" pattern (see that
/// class's own remarks for WHY: WPF resource objects are thread-affine to whichever STA thread first
/// materializes them, so a naive "merge once, reuse across every STA-thread test" approach — the simpler
/// pattern <c>ChildWindowViewModelTests.EnsureWpfApplication</c> gets away with only because it never
/// leaves the ORIGINAL, non-STA xUnit thread — would throw
/// "the calling thread cannot access this object because a different thread owns it" the moment a SECOND
/// <see cref="System.Threading.Thread"/> in this class touches whatever <see cref="Application.Current"/>
/// already exists from an earlier test). <see cref="RunOnStaThread{T}"/>/<c>MergeResourceDictionaries</c>
/// below are duplicated from that class rather than shared/refactored out (matches this codebase's own
/// established precedent of duplicating small STA/chrome-accounting helpers across
/// <c>PanelResizeController</c>/<c>NativePanelResizeController</c> rather than reaching for a shared
/// utility neither one has a reference to).
/// <see cref="DesktopOrganizer.UI.Interaction.Native.NativeSizingSwitch"/> is steered onto the Thumb-based
/// <see cref="DesktopOrganizer.UI.Behaviors.PanelResizeController"/> path
/// (<c>IAppSettings.NativePanelSizing = false</c>) so construction never touches any native/P-Invoke code
/// — the whole point of this test is exercised entirely in managed WPF layout.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowInitializeTests
{
    /// <summary>
    /// The core MAJOR-1 regression: a persisted 3-column width (344 DIP at IconSize 48 — the EXACT
    /// pre-F3 <c>SnapWidthToGrid</c> snap point for 3 columns, see
    /// <c>ContentLeftInsetRowCapacityRegressionTests</c>) must load back as 344, not jump to 352.
    /// </summary>
    [Fact]
    public void Initialize_GridPanel_ThreeColumnPersistedWidth_344_StaysAt344_NotSnappedTo352()
    {
        var width = RunOnStaThread(() =>
        {
            using var harness = ChildWindowHarness.Build();
            harness.Window.Initialize(BuildGridModel(344d));
            return harness.Window.Width;
        });

        Assert.Equal(344d, width);
    }

    /// <summary>
    /// The second MAJOR-1 regression case named in the fix-cycle review: a persisted single-column width
    /// (168 DIP at IconSize 48) must load back as 168, not jump to 176.
    /// </summary>
    [Fact]
    public void Initialize_GridPanel_OneColumnPersistedWidth_168_StaysAt168_NotSnappedTo176()
    {
        var width = RunOnStaThread(() =>
        {
            using var harness = ChildWindowHarness.Build();
            harness.Window.Initialize(BuildGridModel(168d));
            return harness.Window.Width;
        });

        Assert.Equal(168d, width);
    }

    /// <summary>
    /// Characterizes the exact bug MAJOR-1 avoids, at the same seam <c>Initialize</c> used to call
    /// (<c>PanelResizeController.SnapWidthToGrid</c>, reached here via the STILL-SUPPORTED
    /// <c>OnModelPropertyChanged</c> IconSize-change path — MAJOR-1 removes only the LOAD-time call, not
    /// this user-driven one) — confirms the removed call-site really would have produced the jump
    /// described above, so the two tests above are pinned against a REAL mechanism, not a coincidence.
    /// Permanent regression pin (project precedent:
    /// <c>ContentLeftInsetRowCapacityRegressionTests.PreF3Bug_Characterization_...</c>), not deleted once
    /// the fix landed.
    /// </summary>
    [Fact]
    public void Initialize_IfItStillCalledSnapWidthToGrid_WouldJumpTo352_CharacterizationOfTheAvoidedBug()
    {
        var (afterInit, afterIconSizeRoundTrip) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowHarness.Build();
            var model = BuildGridModel(344d);
            harness.Window.Initialize(model);
            var afterInit = harness.Window.Width;

            // Toggling IconSize away and back raises PropertyChanged(IconSize) twice; only the shape of
            // the event matters to OnModelPropertyChanged (it re-snaps unconditionally on that property
            // name), so one real change is enough — set to a different value first so the setter doesn't
            // no-op, matching the model's already-subscribed OnModelPropertyChanged from Initialize above.
            model.IconSize += 1d;
            model.IconSize = 48d;

            return (afterInit, harness.Window.Width);
        });

        Assert.Equal(344d, afterInit); // Initialize itself did NOT move it (the fix)...
        Assert.Equal(352d, afterIconSizeRoundTrip); // ...but the still-supported user-driven re-snap does.
    }

    /// <summary>
    /// List-mode panels never snap width at all (<c>SnapWidthToGrid</c>'s own early-return) — Initialize
    /// must leave a List-mode panel's width untouched too, for completeness/symmetry with the Grid-mode
    /// cases above.
    /// </summary>
    [Fact]
    public void Initialize_ListPanel_PersistedWidth_IsNeverTouched()
    {
        var width = RunOnStaThread(() =>
        {
            using var harness = ChildWindowHarness.Build();
            var model = new ChildWindowModel
            {
                ViewMode = PanelViewMode.List,
                Position = new DrawingPoint(0, 0),
                Size = new DrawingSize(200, 300),
            };

            harness.Window.Initialize(model);
            return harness.Window.Width;
        });

        Assert.Equal(200d, width);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // Fix-cycle 2 MAJOR-2 (adr/ADR-029-icon-layout-policy.md "Fix-цикл P05"): OnModelPropertyChanged
    // only re-snapped on IconSize/ViewMode — switching a live panel's IconLayoutPolicy (which changes
    // GridMetrics.ContentHorizontalChrome/MinPanelWidth's M-2 scrollbar reservation for the SAME
    // persisted width) silently left the persisted width matching the OLD policy's chrome, not the new
    // one, until some UNRELATED IconSize/ViewMode change happened to also fire. These tests pin the fix:
    // IconLayoutPolicy now joins the same re-snap trigger.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A persisted 3-column LegacyStretch width (352 DIP — <c>Extent(3, 84, 4, 42) + ContentLeftInset ==
    /// 344 + 8</c>, i.e. F-3-correct and requiring no snap of its own) must re-snap to a WIDER width the
    /// moment the SAME live panel switches to <c>FixedGap</c> — because <c>FixedGap</c>'s M-2 reservation
    /// (<see cref="SystemParameters.VerticalScrollBarWidth"/>) makes the SAME 352 DIP window no longer
    /// wide enough to fit 3 columns once that extra chrome is accounted for. The expected value is
    /// computed via the exact same formula <see cref="DesktopOrganizer.UI.Behaviors.PanelResizeController.SnapWidthToGrid"/>
    /// itself uses (not a hard-coded number) — this is the same "characterize the real mechanism, not a
    /// coincidence" style already used by this class's MAJOR-1 tests above.
    /// </summary>
    [Fact]
    public void Initialize_ThenTogglingIconLayoutPolicyToFixedGap_TriggersReSnap_ReservingScrollBarWidth()
    {
        var (afterInit, afterPolicyChange) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowHarness.Build();
            var model = BuildGridModel(352d, policy: IconLayoutPolicy.LegacyStretch);
            harness.Window.Initialize(model);
            var afterInit = harness.Window.Width;

            model.IconLayoutPolicy = IconLayoutPolicy.FixedGap;

            return (afterInit, harness.Window.Width);
        });

        Assert.Equal(352d, afterInit); // Initialize itself never snaps (MAJOR-1, unaffected by this fix).

        var expected = ExpectedSnappedWidth(352d, IconLayoutPolicy.FixedGap, iconSize: 48d);
        Assert.Equal(expected, afterPolicyChange);
        Assert.True(expected > afterInit, "Test assumes a real, nonzero SystemParameters.VerticalScrollBarWidth — otherwise this assertion would be vacuous.");
    }

    /// <summary>
    /// Symmetric round trip: switching a <c>FixedGap</c> panel back to <c>LegacyStretch</c> re-snaps back
    /// down to the narrower, non-reserving width — proving the trigger fires on EVERY
    /// <c>IconLayoutPolicy</c> change, not just the LegacyStretch-&gt;FixedGap direction. Both expected
    /// values are computed by chaining <see cref="ExpectedSnappedWidth"/> (the same formula
    /// <c>SnapWidthToGrid</c> itself uses) rather than hard-coded numbers, so this test's correctness
    /// does not depend on assuming any particular <see cref="SystemParameters.VerticalScrollBarWidth"/>
    /// value/rounding-boundary behavior on the machine running it.
    /// </summary>
    [Fact]
    public void Initialize_ThenTogglingIconLayoutPolicyBackToLegacyStretch_ReSnapsToNarrowerWidth()
    {
        var (afterFixedGap, afterBackToLegacy) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowHarness.Build();
            var model = BuildGridModel(352d, policy: IconLayoutPolicy.FixedGap);
            harness.Window.Initialize(model);

            // Move to FixedGap's own snap point first (Initialize doesn't snap — MAJOR-1), then back.
            model.IconLayoutPolicy = IconLayoutPolicy.LegacyStretch;
            model.IconLayoutPolicy = IconLayoutPolicy.FixedGap;
            var afterFixedGap = harness.Window.Width;

            model.IconLayoutPolicy = IconLayoutPolicy.LegacyStretch;
            return (afterFixedGap, harness.Window.Width);
        });

        var expectedAfterFixedGap = ExpectedSnappedWidth(352d, IconLayoutPolicy.FixedGap, iconSize: 48d);
        Assert.Equal(expectedAfterFixedGap, afterFixedGap);

        var expectedAfterBackToLegacy = ExpectedSnappedWidth(afterFixedGap, IconLayoutPolicy.LegacyStretch, iconSize: 48d);
        Assert.Equal(expectedAfterBackToLegacy, afterBackToLegacy);
        Assert.True(afterFixedGap > afterBackToLegacy);
    }

    /// <summary>
    /// When <c>IAppSettings.SnapResizeToIconGrid</c> is disabled, <c>SnapWidthToGrid</c>'s own early
    /// return means an <c>IconLayoutPolicy</c> change (like an <c>IconSize</c>/<c>ViewMode</c> change)
    /// must NOT touch the persisted width at all — the new trigger relies entirely on
    /// <c>SnapWidthToGrid</c>'s existing internal gate, not a duplicate one in <c>OnModelPropertyChanged</c>.
    /// </summary>
    [Fact]
    public void Initialize_ThenTogglingIconLayoutPolicy_WhenSnapDisabled_LeavesWidthUntouched()
    {
        var (afterInit, afterPolicyChange) = RunOnStaThread(() =>
        {
            using var harness = ChildWindowHarness.Build(snapResizeToIconGrid: false);
            var model = BuildGridModel(352d, policy: IconLayoutPolicy.LegacyStretch);
            harness.Window.Initialize(model);
            var afterInit = harness.Window.Width;

            model.IconLayoutPolicy = IconLayoutPolicy.FixedGap;

            return (afterInit, harness.Window.Width);
        });

        Assert.Equal(352d, afterInit);
        Assert.Equal(352d, afterPolicyChange);
    }

    private static ChildWindowModel BuildGridModel(double width, double iconSize = 48d, IconLayoutPolicy policy = IconLayoutPolicy.LegacyStretch) => new()
    {
        ViewMode = PanelViewMode.Grid,
        IconSize = iconSize,
        IconLayoutPolicy = policy,
        Position = new DrawingPoint(0, 0),
        Size = new DrawingSize((int)width, 300),
    };

    /// <summary>
    /// Pure re-implementation of <see cref="DesktopOrganizer.UI.Behaviors.PanelResizeController.SnapWidthToGrid"/>'s formula
    /// (Grid mode, snap enabled) — used by the two tests above so their expectations are derived from the
    /// SAME mechanism the production code uses, not independently hard-coded numbers that could drift
    /// from it, or that could be wrong at a <see cref="SystemParameters.VerticalScrollBarWidth"/>
    /// rounding boundary on some machine other than the one these numbers were manually checked on.
    /// </summary>
    private static double ExpectedSnappedWidth(double windowWidth, IconLayoutPolicy policy, double iconSize)
    {
        var scrollBarWidth = SystemParameters.VerticalScrollBarWidth;
        var cell = GridMetrics.CellSize(iconSize);
        var chrome = GridMetrics.ContentHorizontalChrome(policy, scrollBarWidth);
        var edgePadding = AdaptiveGridLayout.EdgePadding(cell, AdaptiveGridLayout.DefaultEdgePaddingRatio);
        var content = Math.Max(0d, windowWidth - chrome);

        return Math.Ceiling(Math.Max(
            GridMetrics.MinPanelWidth(iconSize, policy, scrollBarWidth),
            AdaptiveGridLayout.SnapExtent(content, cell, AdaptiveGridLayout.DefaultMinGap, edgePadding) + chrome));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // STA/resource-chain infrastructure — duplicated from Unit.Resources.ResourceDictionaryMergeTests,
    // see this class's own remarks for why.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Owns the swapped-in <see cref="ResourceDictionary"/> for one <see cref="RunOnStaThread{T}"/> call
    /// and a real <see cref="ChildWindow"/> built against it; <see cref="Dispose"/> restores the
    /// <see cref="Application.Current"/>'s original <see cref="Application.Resources"/> reference exactly
    /// like <c>ResourceDictionaryMergeTests</c>'s inline <c>finally</c> blocks do.
    /// </summary>
    private sealed class ChildWindowHarness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private ChildWindowHarness(Application app, ResourceDictionary? originalResources, bool ownsApplication, ChildWindow window)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Window = window;
        }

        public ChildWindow Window { get; }

        public static ChildWindowHarness Build(bool snapResizeToIconGrid = true)
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

            // ChildWindow.xaml's HeaderContextMenu binds three MenuItems directly to
            // {StaticResource LayoutEditMode} (App.xaml.cs's StartApp publishes the real
            // ILayoutEditModeService there before any window is ever constructed in production) — without
            // this key InitializeComponent() throws XamlParseException immediately.
            app.Resources["LayoutEditMode"] = Mock.Of<ILayoutEditModeService>();

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
            appSettings.SetupProperty(s => s.NativePanelSizing, false);
            appSettings.SetupProperty(s => s.SnapResizeToIconGrid, snapResizeToIconGrid); // must be true (the default) for the avoided-bug characterization test / MAJOR-2 re-snap tests to actually reach SnapWidthToGrid's body
            appSettings.SetupProperty(s => s.NormalModeLayoutGestures, string.Empty);

            var window = new ChildWindow(
                viewModel,
                Mock.Of<IWindowManager>(),
                Mock.Of<IDesktopIntegrationService>(),
                appSettings.Object,
                Mock.Of<IGestureDiagnostics>(),
                Mock.Of<INativePanelSizingService>(),
                (ILayoutEditModeService)app.Resources["LayoutEditMode"],
                Mock.Of<IMonitorService>(),
                Mock.Of<IConfirmationService>(),
                Mock.Of<IPanelCustomizeService>(),
                Mock.Of<IPanelItemDragOutSession>(),
                FakeDesktopScale.Uniform().Object);

            return new ChildWindowHarness(app, originalResources, ownsApplication, window);
        }

        public void Dispose()
        {
            if (!_ownsApplication)
                _app.Resources = _originalResources!;

            // If _ownsApplication, deliberately leave Application.Current/Resources as-is (see
            // RunOnStaThread's remarks for why this thread keeps pumping afterward instead of exiting).
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
    /// </summary>
    /// <remarks>
    /// M-2 fix (P08 fix cycle 2): delegates to <see cref="StaTestRunner.Run{T}"/> instead of this
    /// class's own former hand-rolled duplicate of the STA-thread/ownership machinery, which kept an
    /// owning thread pumping (<see cref="Dispatcher.Run()"/>) forever with NO
    /// <see cref="Dispatcher.UnhandledException"/> absorber — a leaked callback throwing on that
    /// pumped-forever thread was an unguarded process kill. <see cref="StaTestRunner.Run{T}"/> already
    /// closes that gap.
    /// </remarks>
    private static T RunOnStaThread<T>(Func<T> func) => StaTestRunner.Run(func);
}
