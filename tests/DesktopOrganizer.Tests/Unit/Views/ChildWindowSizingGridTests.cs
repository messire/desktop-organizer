using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Diagnostics;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.ViewModels;
using DesktopOrganizer.UI.Views;
using Moq;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// P04 end-to-end through a REAL <see cref="ChildWindow"/> (docs/PLANS/workspace-grid-03-04-integration.md
/// §3.5/§3.7, DoD D4-2): the F-2 gate (<c>IconSize</c> never moves a
/// <see cref="PanelGeometryMode.GridAligned"/> panel's bounds), the "Size" preset command, and the
/// agreement between <c>ChildWindow.xaml</c>'s <c>MinWidth</c> coercion and
/// <see cref="WorkspaceGridMetrics.MinSpanExpanded"/>.
/// </summary>
/// <remarks>
/// The STA/resource-chain harness is duplicated from <c>ChildWindowInitializeTests</c> (which itself
/// duplicates it from <c>Unit.Resources.ResourceDictionaryMergeTests</c> — see that class's remarks for
/// why sharing it across STA threads does not work), with one addition P04 needs: a configurable
/// <see cref="IMonitorService"/>, because a grid-aligned panel that cannot resolve a monitor correctly
/// degrades to <see cref="PanelGridState.Legacy"/> by design.
/// </remarks>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ChildWindowSizingGridTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    // 264 x 304 DIP = spans (34, 39) — PanelDefaults' own quantisation of the 260x300 default size.
    private const int WidthSpanUnits = 34;
    private const int HeightSpanUnits = 39;
    private const double GridWidth = (WidthSpanUnits * U) - G;
    private const double GridHeight = (HeightSpanUnits * U) - G;

    /// <summary>
    /// P04 §3 item 6: the window's own hard <c>MinWidth</c> coercion and the workspace-grid minimum span
    /// must be the same number — read here off the REAL window, which is the only place the XAML literal
    /// is observable.
    /// </summary>
    [Fact]
    public void ChildWindowMinWidth_EqualsTheWorkspaceGridMinimumSpanWidth()
    {
        var (minWidth, minHeight) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            return (harness.Window.MinWidth, harness.Window.MinHeight);
        });

        Assert.Equal(WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, U, G).Width, minWidth);
        Assert.True(WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, U, G).Height >= minHeight);

        // The collapsed span is the window's MinHeight — a rolled-up panel is exactly as tall as the
        // floor the window itself enforces (DoD D4-4).
        Assert.Equal(minHeight, WorkspaceLayoutPolicy.SpansToBounds(new GridSpan(1, WorkspaceGridMetrics.CollapsedHeaderSpan), U, G).Height);
    }

    // ── DoD D4-2 / F-2: changing IconSize must not move a GridAligned panel's bounds. ────────────────
    [Fact]
    public void IconSizeChange_OnAGridAlignedPanel_LeavesTheWidthUntouched()
    {
        var (before, after) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            harness.Window.Initialize(model);
            var before = harness.Window.Width;

            model.IconSize = 96d;
            model.ViewMode = PanelViewMode.Grid;
            model.IconLayoutPolicy = IconLayoutPolicy.FixedGap;

            return (before, harness.Window.Width);
        });

        Assert.Equal(GridWidth, before);
        Assert.Equal(before, after);
    }

    // ── Control case (not vacuous): the very same property changes DO still re-snap a legacy panel. ──
    [Fact]
    public void IconSizeChange_OnALegacyPanel_StillReSnapsTheWidth()
    {
        var (before, after) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            model.GeometryMode = PanelGeometryMode.LegacyFreeform;
            harness.Window.Initialize(model);
            var before = harness.Window.Width;

            model.IconSize = 96d;

            return (before, harness.Window.Width);
        });

        Assert.NotEqual(before, after);
    }

    // ── §3.5: the preset command applies the preset's span, writes it onto the model and persists. ───
    [Theory]
    [InlineData(PanelSizePreset.Small)]
    [InlineData(PanelSizePreset.Medium)]
    [InlineData(PanelSizePreset.Large)]
    [InlineData(PanelSizePreset.ExtraLarge)]
    public void SizePreset_OnAGridAlignedPanel_AppliesThePresetSpan(PanelSizePreset preset)
    {
        var expected = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.SpanForPreset(preset), U, G);

        var (size, modelSpan, modelSize, geometryMode, saves) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            harness.Window.Initialize(model);

            harness.InvokeSizePreset(preset);

            return (
                new Size(harness.Window.Width, harness.Window.Height),
                new GridSpan(model.WidthSpanUnits ?? 0, model.HeightSpanUnits ?? 0),
                model.Size,
                model.GeometryMode,
                harness.SaveCount);
        });

        Assert.Equal(expected, size);
        Assert.Equal(WorkspaceGridMetrics.SpanForPreset(preset), modelSpan);

        // The model's Position/Size were written in the SAME act as the window's (PanelGeometryWriter),
        // so no OnRenderSizeChanged re-entry could leave them out of sync.
        Assert.Equal(new DrawingSize((int)expected.Width, (int)expected.Height), modelSize);
        Assert.Equal(PanelGeometryMode.GridAligned, geometryMode);
        Assert.Equal(1, saves);
    }

    // ── LegacyFreeform: the size changes, the MODE does not (see PanelSizePresetCalculator's remarks). ──
    [Fact]
    public void SizePreset_OnALegacyPanel_ResizesWithoutSwitchingGeometryMode()
    {
        var expected = WorkspaceLayoutPolicy.PreferredSize(PanelSizePreset.Medium, U, G);

        var (size, geometryMode, spans) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            model.GeometryMode = PanelGeometryMode.LegacyFreeform;
            model.WidthSpanUnits = null;
            model.HeightSpanUnits = null;
            harness.Window.Initialize(model);

            harness.InvokeSizePreset(PanelSizePreset.Medium);

            return (new Size(harness.Window.Width, harness.Window.Height), model.GeometryMode, (model.WidthSpanUnits, model.HeightSpanUnits));
        });

        Assert.Equal(expected, size);
        Assert.Equal(PanelGeometryMode.LegacyFreeform, geometryMode);
        Assert.Equal((null, null), spans);
    }

    // ── ADR-026 gating: an explicit command is blocked while the layout is locked. ───────────────────
    [Fact]
    public void SizePreset_WhenLayoutIsLocked_IsANoOp()
    {
        var (size, saves) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build(layoutLocked: true);
            var model = GridAlignedModel();
            harness.Window.Initialize(model);

            harness.InvokeSizePreset(PanelSizePreset.ExtraLarge);

            return (new Size(harness.Window.Width, harness.Window.Height), harness.SaveCount);
        });

        Assert.Equal(new Size(GridWidth, GridHeight), size);
        Assert.Equal(0, saves);
    }

    // ── A rolled-up panel is skipped (see the handler's remarks). ────────────────────────────────────
    [Fact]
    public void SizePreset_WhenRolledUp_IsANoOp()
    {
        var saves = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            model.IsRolledUp = true;
            harness.Window.Initialize(model);

            harness.InvokeSizePreset(PanelSizePreset.Large);

            return harness.SaveCount;
        });

        Assert.Equal(0, saves);
    }

    // ── P04 fix-cycle 1 (UX P0): a preset is an explicit MANUAL size — it turns AutoContent off, so the
    // content sizer's own width-change debounce (~300 ms, ChildWindow.ContentSizing.cs) cannot silently
    // undo the preset's HEIGHT a moment later. The debounce itself is not pumped here (the STA worker
    // returns before its timer could tick); what is pinned is the state that makes MaybeAutoFit a no-op
    // when it does. ─────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SizePreset_OnAnAutoContentPanel_SwitchesItToManual_AndKeepsThePresetHeight()
    {
        var expected = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.SpanForPreset(PanelSizePreset.Large), U, G);

        var (sizingMode, size) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            model.SizingMode = PanelSizingMode.AutoContent;
            harness.Window.Initialize(model);

            harness.InvokeSizePreset(PanelSizePreset.Large);

            return (model.SizingMode, new Size(harness.Window.Width, harness.Window.Height));
        });

        Assert.Equal(PanelSizingMode.Manual, sizingMode);
        Assert.Equal(expected, size);
    }

    // ── …and a panel that was already Manual is untouched (the assignment is idempotent, not a toggle). ──
    [Fact]
    public void SizePreset_OnAManualPanel_LeavesTheSizingModeAlone()
    {
        var sizingMode = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            harness.Window.Initialize(model);

            harness.InvokeSizePreset(PanelSizePreset.Medium);

            return model.SizingMode;
        });

        Assert.Equal(PanelSizingMode.Manual, sizingMode);
    }

    // ── MV-21: the "Size" submenu is DISABLED while the panel is rolled up (the Click handler's early
    // return stays as defence in depth, but the user now sees the rule). ────────────────────────────────
    [Fact]
    public void SizePresetMenu_IsDisabled_WhileThePanelIsRolledUp()
    {
        var (whileRolledUp, whileExpanded) = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            var model = GridAlignedModel();
            model.IsRolledUp = true;
            harness.Window.Initialize(model);

            var rolledUp = harness.IsSizeMenuEnabled();

            model.IsRolledUp = false;
            harness.Window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            return (rolledUp, harness.IsSizeMenuEnabled());
        });

        Assert.False(whileRolledUp);
        Assert.True(whileExpanded);
    }

    // ── A neighbouring panel stops the preset one gap short instead of overlapping it. ───────────────
    [Fact]
    public void SizePreset_WithANeighbour_StopsOneGapShortOfIt()
    {
        var width = RunOnStaThread(() =>
        {
            using var harness = Harness.Build();
            harness.Obstacles.Add(new Rect(400d, 0d, 200d, 600d));
            var model = GridAlignedModel();
            harness.Window.Initialize(model);

            harness.InvokeSizePreset(PanelSizePreset.ExtraLarge);

            return harness.Window.Width;
        });

        // The panel starts at Left = 0 (the model's Position), so the neighbour at X = 400 caps the
        // width at 400 - Gap = 392.
        Assert.Equal(400d - G, width);
    }

    private static ChildWindowModel GridAlignedModel() => new()
    {
        ViewMode = PanelViewMode.Grid,
        IconSize = 48d,
        GeometryMode = PanelGeometryMode.GridAligned,
        WidthSpanUnits = WidthSpanUnits,
        HeightSpanUnits = HeightSpanUnits,
        MonitorDeviceId = @"\\.\DISPLAY1",
        Position = new DrawingPoint(0, 0),
        Size = new DrawingSize((int)GridWidth, (int)GridHeight),
    };

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // STA/resource-chain infrastructure — duplicated from Unit.Views.ChildWindowInitializeTests,
    // see this class's own remarks.
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    private sealed class Harness : IDisposable
    {
        private readonly Application _app;
        private readonly ResourceDictionary? _originalResources;
        private readonly bool _ownsApplication;

        private Harness(Application app, ResourceDictionary? originalResources, bool ownsApplication, ChildWindow window)
        {
            _app = app;
            _originalResources = originalResources;
            _ownsApplication = ownsApplication;
            Window = window;
        }

        public ChildWindow Window { get; }

        public List<Rect> Obstacles { get; } = [];

        public int SaveCount { get; private set; }

        public static Harness Build(bool layoutLocked = false)
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

            var layoutEditMode = new Mock<ILayoutEditModeService>();
            layoutEditMode.SetupGet(s => s.IsLayoutLocked).Returns(layoutLocked);
            layoutEditMode.SetupGet(s => s.CanEnterEditLayout).Returns(!layoutLocked);
            app.Resources["LayoutEditMode"] = layoutEditMode.Object;

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
            appSettings.SetupProperty(s => s.SnapResizeToIconGrid, true);
            appSettings.SetupProperty(s => s.NormalModeLayoutGestures, string.Empty);

            var monitors = new Mock<IMonitorService>();
            monitors.Setup(m => m.GetMonitors()).Returns(
            [
                new MonitorInfo(@"\\.\DISPLAY1", new Rect(0d, 0d, 1920d, 1080d), new Rect(0d, 0d, 1920d, 1040d), IsPrimary: true),
            ]);

            var windowManager = new Mock<IWindowManager>();

            var harnessBox = new Harness[1];
            windowManager.Setup(w => w.SaveWindowsState()).Callback(() => harnessBox[0].SaveCount++);
            windowManager
                .Setup(w => w.CollectOtherPanelBounds(It.IsAny<Guid?>(), It.IsAny<IList<Rect>>()))
                .Callback<Guid?, IList<Rect>>((_, buffer) =>
                {
                    buffer.Clear();
                    foreach (var obstacle in harnessBox[0].Obstacles)
                        buffer.Add(obstacle);
                });

            var window = new ChildWindow(
                viewModel,
                windowManager.Object,
                Mock.Of<IDesktopIntegrationService>(),
                appSettings.Object,
                Mock.Of<IGestureDiagnostics>(),
                Mock.Of<INativePanelSizingService>(),
                layoutEditMode.Object,
                monitors.Object,
                Mock.Of<IConfirmationService>());

            var harness = new Harness(app, originalResources, ownsApplication, window);
            harnessBox[0] = harness;

            return harness;
        }

        /// <summary>
        /// Invokes the private <c>SizePresetMenu_Click</c> handler exactly as the XAML
        /// <c>Click="SizePresetMenu_Click"</c> wiring would (same <c>Tag</c>-carrying
        /// <see cref="MenuItem"/> sender) — the same reflection approach
        /// <c>ChildWindowWorkspaceGridPartialTests</c> already uses for these partial handlers.
        /// </summary>
        /// <summary>
        /// Opens the header context menu (so its <c>PlacementTarget</c>-relative bindings actually
        /// evaluate) and reads the "Size" submenu's resolved <see cref="UIElement.IsEnabled"/> — MV-21.
        /// </summary>
        public bool IsSizeMenuEnabled()
        {
            var menu = (System.Windows.Controls.ContextMenu)Window.FindName("HeaderContextMenu")!;
            var item = (MenuItem)Window.FindName("SizePresetMenu")!;

            menu.PlacementTarget = Window;
            menu.DataContext = Window.DataContext;
            menu.IsOpen = true;
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            var enabled = item.IsEnabled;
            menu.IsOpen = false;

            return enabled;
        }

        public void InvokeSizePreset(PanelSizePreset preset)
        {
            var method = typeof(ChildWindow).GetMethod("SizePresetMenu_Click", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            method!.Invoke(Window, [new MenuItem { Tag = preset.ToString() }, new RoutedEventArgs()]);
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
