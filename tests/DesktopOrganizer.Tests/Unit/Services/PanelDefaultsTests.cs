using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Services;
using DesktopOrganizer.UI.Theming;
using Moq;
using DrawingSize = System.Drawing.Size;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PanelDefaults"/> (fix-cycle 1, docs/PLANS/workspace-grid-migration.md
/// §10.5): the shared appearance/workspace-grid seeding logic used by both
/// <c>App.xaml.cs</c>'s lasso-gesture panel creation and <c>MainWindowViewModel.AddPanel</c> — neither
/// call site is practically unit-testable end-to-end (WPF <c>Application</c>/full DI graph), so this
/// exercises the shared logic itself directly, covering both the "explicit Size already set" shape
/// (the lasso path) and the "Size left at CLR default" shape (the plain "Add Panel" action).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelDefaultsTests
{
    private static Mock<IAppSettings> BuildSettings(
        string backgroundColor = "#112233",
        string textColor = "#EEEEEE",
        double opacity = 0.7,
        string surfaceStyle = "Ambient",
        double iconSize = 48,
        string geometryMode = "LegacyFreeform",
        string iconLayoutPolicy = "LegacyStretch")
    {
        var settings = new Mock<IAppSettings>();
        settings.SetupProperty(s => s.DefaultBackgroundColor, backgroundColor);
        settings.SetupProperty(s => s.DefaultTextColor, textColor);
        settings.SetupProperty(s => s.DefaultOpacity, opacity);
        settings.SetupProperty(s => s.DefaultSurfaceStyle, surfaceStyle);
        settings.SetupProperty(s => s.DefaultIconSize, iconSize);
        settings.SetupProperty(s => s.DefaultPanelGeometryMode, geometryMode);
        settings.SetupProperty(s => s.DefaultIconLayoutPolicy, iconLayoutPolicy);
        return settings;
    }

    // ── Apply ────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Apply_SeedsAppearanceFromSettings()
    {
        var model = new ChildWindowModel();
        var settings = BuildSettings(opacity: 0.55, textColor: "#AAAAAA", surfaceStyle: "Glass", iconSize: 40);

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal("#112233", model.AccentColor);
        Assert.Equal("#AAAAAA", model.TextColor);
        Assert.Equal(0.55, model.SurfaceOpacity);
        Assert.Equal(PanelSurfaceStyle.Glass, model.SurfaceStyle);
        Assert.Equal(40, model.IconSize);
    }

    [Fact]
    public void Apply_LegacyNearBlackBackgroundColor_ResolvesToNeutralAccentInstead()
    {
        // CRITICAL-1 regression (ADR-025 D-10, DO-014) preserved by the shared helper.
        var model = new ChildWindowModel();
        var settings = BuildSettings(backgroundColor: "#1E1E1E");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(AccentPalette.Neutral, model.AccentColor);
    }

    [Fact]
    public void Apply_GeometryModeGridAligned_SeedsGridAligned()
    {
        var model = new ChildWindowModel();
        var settings = BuildSettings(geometryMode: "GridAligned");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(PanelGeometryMode.GridAligned, model.GeometryMode);
    }

    [Fact]
    public void Apply_UnrecognisedGeometryMode_FallsBackToLegacyFreeform()
    {
        var model = new ChildWindowModel();
        var settings = BuildSettings(geometryMode: "not-a-real-value");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
    }

    [Fact]
    public void Apply_IconLayoutPolicyFixedGap_SeedsFixedGap()
    {
        var model = new ChildWindowModel();
        var settings = BuildSettings(iconLayoutPolicy: "FixedGap");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(IconLayoutPolicy.FixedGap, model.IconLayoutPolicy);
    }

    [Fact]
    public void Apply_NeverTouchesPositionOrSize()
    {
        var model = new ChildWindowModel { Position = new System.Drawing.Point(7, 9), Size = new DrawingSize(111, 222) };
        var settings = BuildSettings(geometryMode: "GridAligned");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(new System.Drawing.Point(7, 9), model.Position);
        Assert.Equal(new DrawingSize(111, 222), model.Size);
    }

    // ── ReconcileGridAlignedSpans — MAJOR-1(b) ──────────────────────────────────────────────────────
    [Fact]
    public void ReconcileGridAlignedSpans_LegacyFreeform_IsNoOp()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.LegacyFreeform, Size = new DrawingSize(300, 200) };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        Assert.Null(model.WidthSpanUnits);
        Assert.Null(model.HeightSpanUnits);
        Assert.Equal(new DrawingSize(300, 200), model.Size);
    }

    [Fact]
    public void ReconcileGridAlignedSpans_GridAlignedWithExplicitSize_SeedsNonNullPositiveSpans()
    {
        // The lasso-gesture shape: Position/Size are already assigned to the drawn rectangle before
        // this call (App.xaml.cs's CreateLassoPanel).
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned, Size = new DrawingSize(237, 173) };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        Assert.NotNull(model.WidthSpanUnits);
        Assert.NotNull(model.HeightSpanUnits);
        Assert.True(model.WidthSpanUnits >= 1);
        Assert.True(model.HeightSpanUnits >= 1);
    }

    [Fact]
    public void ReconcileGridAlignedSpans_GridAlignedWithExplicitSize_NeverShrinksBelowRequestedSize()
    {
        // ADR-028 "px<->DIP": BoundsToSpans uses Ceiling, never Round — content must never be clipped.
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned, Size = new DrawingSize(237, 173) };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        Assert.True(model.Size.Width >= 237);
        Assert.True(model.Size.Height >= 173);
    }

    [Fact]
    public void ReconcileGridAlignedSpans_GridAlignedWithExplicitSize_SizeExactlyMatchesSpans()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned, Size = new DrawingSize(237, 173) };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        var expected = WorkspaceLayoutPolicy.SpansToBounds(
            new GridSpan(model.WidthSpanUnits!.Value, model.HeightSpanUnits!.Value),
            WorkspaceGridMetrics.UnitSize,
            WorkspaceGridMetrics.DefaultGap);

        Assert.Equal((int)Math.Round(expected.Width), model.Size.Width);
        Assert.Equal((int)Math.Round(expected.Height), model.Size.Height);
    }

    [Fact]
    public void ReconcileGridAlignedSpans_GridAlignedWithDegenerateSize_SeedsDefaultPanelSizeInstead()
    {
        // N-6 fix cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8): the plain "Add Panel" shape
        // (MainWindowViewModel.AddPanel never assigns Position/Size, so Size is still the CLR default
        // (0,0) at this point) previously seeded PanelSizePreset.Medium (248x232 DIP) here — a
        // DIFFERENT, smaller size than the 260x300 DIP a LegacyFreeform "Add Panel" gets from
        // ChildWindow.xaml. Must seed PanelMetrics.DefaultPanelSize instead, so both geometry modes
        // agree on what "Add Panel"'s default size is, and must not quantise to a degenerate 1x1 span
        // (which BoundsToSpans would otherwise floor a bare (0,0) input to).
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        var expectedSpan = WorkspaceLayoutPolicy.BoundsToSpans(PanelMetrics.DefaultPanelSize, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap);
        Assert.Equal(expectedSpan.WidthUnits, model.WidthSpanUnits);
        Assert.Equal(expectedSpan.HeightUnits, model.HeightSpanUnits);

        // Pinned regression (ADR-028 "px<->DIP" Ceiling rule): 260x300 DIP -> ceil((260+8)/8 - eps)=34,
        // ceil((300+8)/8 - eps)=39 -> SpansToBounds: 34*8-8=264, 39*8-8=304.
        Assert.Equal(34, model.WidthSpanUnits);
        Assert.Equal(39, model.HeightSpanUnits);
        Assert.Equal(264, model.Size.Width);
        Assert.Equal(304, model.Size.Height);
    }

    [Fact]
    public void ReconcileGridAlignedSpans_TinyExplicitSize_ClampsToMinSpanExpanded()
    {
        // N-5 fix cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8): BoundsToSpans alone never
        // enforces WorkspaceGridMetrics.MinSpanExpanded — only WorkspaceLayoutPolicy.SnapBounds (the
        // "Align to workspace grid" command's own math) composes the two. This creation-time seed path
        // must clamp too, or a tiny explicit Size (e.g. a very small lasso rectangle) could create a new
        // GridAligned panel below the documented minimum.
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned, Size = new DrawingSize(10, 10) };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        Assert.Equal(WorkspaceGridMetrics.MinWidthSpanExpanded, model.WidthSpanUnits);
        Assert.Equal(WorkspaceGridMetrics.MinHeightSpanExpanded, model.HeightSpanUnits);

        var expectedSize = WorkspaceLayoutPolicy.SpansToBounds(WorkspaceGridMetrics.MinSpanExpanded, WorkspaceGridMetrics.UnitSize, WorkspaceGridMetrics.DefaultGap);
        Assert.Equal((int)Math.Round(expectedSize.Width), model.Size.Width);
        Assert.Equal((int)Math.Round(expectedSize.Height), model.Size.Height);
    }

    [Fact]
    public void ReconcileGridAlignedSpans_NeverTouchesPosition()
    {
        var model = new ChildWindowModel
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            Position = new System.Drawing.Point(413, 291),
            Size = new DrawingSize(237, 173),
        };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        Assert.Equal(new System.Drawing.Point(413, 291), model.Position);
    }
}
