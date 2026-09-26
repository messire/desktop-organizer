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
    // ── Apply ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelDefaults.Apply"/> seeds the model's accent, text color, opacity,
    /// surface style, and icon size from the given <see cref="IAppSettings"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies the ADR-025 D-10/DO-014 regression guard: a legacy near-black background color resolves
    /// to <see cref="AccentPalette.Neutral"/> instead of that near-black value.
    /// </summary>
    [Fact]
    public void Apply_LegacyNearBlackBackgroundColor_ResolvesToNeutralAccentInstead()
    {
        // CRITICAL-1 regression (ADR-025 D-10, DO-014) preserved by the shared helper.
        var model = new ChildWindowModel();
        var settings = BuildSettings(backgroundColor: "#1E1E1E");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(AccentPalette.Neutral, model.AccentColor);
    }

    /// <summary>
    /// Verifies that a <c>"GridAligned"</c> default geometry mode setting seeds
    /// <see cref="PanelGeometryMode.GridAligned"/> on the model.
    /// </summary>
    [Fact]
    public void Apply_GeometryModeGridAligned_SeedsGridAligned()
    {
        var model = new ChildWindowModel();
        var settings = BuildSettings(geometryMode: "GridAligned");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(PanelGeometryMode.GridAligned, model.GeometryMode);
    }

    /// <summary>
    /// Verifies that an unrecognized default geometry mode setting falls back to
    /// <see cref="PanelGeometryMode.LegacyFreeform"/>.
    /// </summary>
    [Fact]
    public void Apply_UnrecognisedGeometryMode_FallsBackToLegacyFreeform()
    {
        var model = new ChildWindowModel();
        var settings = BuildSettings(geometryMode: "not-a-real-value");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(PanelGeometryMode.LegacyFreeform, model.GeometryMode);
    }

    /// <summary>
    /// Verifies that a <c>"FixedGap"</c> default icon-layout-policy setting seeds
    /// <see cref="IconLayoutPolicy.FixedGap"/> on the model.
    /// </summary>
    [Fact]
    public void Apply_IconLayoutPolicyFixedGap_SeedsFixedGap()
    {
        var model = new ChildWindowModel();
        var settings = BuildSettings(iconLayoutPolicy: "FixedGap");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(IconLayoutPolicy.FixedGap, model.IconLayoutPolicy);
    }

    /// <summary>
    /// Verifies that <see cref="PanelDefaults.Apply"/> never modifies the model's existing
    /// <c>Position</c>/<c>Size</c> — appearance and geometry-mode seeding only.
    /// </summary>
    [Fact]
    public void Apply_NeverTouchesPositionOrSize()
    {
        var model = new ChildWindowModel { Position = new System.Drawing.Point(7, 9), Size = new DrawingSize(111, 222) };
        var settings = BuildSettings(geometryMode: "GridAligned");

        PanelDefaults.Apply(model, settings.Object);

        Assert.Equal(new System.Drawing.Point(7, 9), model.Position);
        Assert.Equal(new DrawingSize(111, 222), model.Size);
    }

    // ── CreateNew — fix-cycle finding 11 (docs/prompts/settings-window-redesign.md) ─────────────────

    /// <summary>
    /// Verifies that <see cref="PanelDefaults.CreateNew"/> sets the new panel's title and page
    /// assignment, and applies every appearance default from <see cref="PanelDefaults.Apply"/>
    /// (spot-checked via the accent colour) — the single-call replacement for the two call sites that used
    /// to construct the model and call <see cref="PanelDefaults.Apply"/> separately.
    /// </summary>
    [Fact]
    public void CreateNew_SetsTitleAndPageIdAndAppliesDefaults()
    {
        var settings = BuildSettings(backgroundColor: "#445566");

        var model = PanelDefaults.CreateNew(settings.Object, "work");

        Assert.Equal("New Panel", model.Title);
        Assert.Equal("work", model.PageId);
        Assert.Equal("#445566", model.AccentColor);
    }

    /// <summary>Verifies that <see cref="PanelDefaults.CreateNew"/> rejects a <see langword="null"/> settings instance.</summary>
    [Fact]
    public void CreateNew_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PanelDefaults.CreateNew(null!, "default"));
    }

    // ── ReconcileGridAlignedSpans — MAJOR-1(b) ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelDefaults.ReconcileGridAlignedSpans"/> is a no-op for a
    /// <see cref="PanelGeometryMode.LegacyFreeform"/> panel: no spans are seeded and size is untouched.
    /// </summary>
    [Fact]
    public void ReconcileGridAlignedSpans_LegacyFreeform_IsNoOp()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.LegacyFreeform, Size = new DrawingSize(300, 200) };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        Assert.Null(model.WidthSpanUnits);
        Assert.Null(model.HeightSpanUnits);
        Assert.Equal(new DrawingSize(300, 200), model.Size);
    }

    /// <summary>
    /// Verifies that reconciling a <see cref="PanelGeometryMode.GridAligned"/> panel that already has an
    /// explicit size (the lasso-gesture shape) seeds non-null, positive width/height span units.
    /// </summary>
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

    /// <summary>
    /// Verifies the ADR-028 px/DIP ceiling rule: reconciling never shrinks the panel's size below the
    /// explicitly requested size, since span quantization must round up, not down.
    /// </summary>
    [Fact]
    public void ReconcileGridAlignedSpans_GridAlignedWithExplicitSize_NeverShrinksBelowRequestedSize()
    {
        // ADR-028 "px<->DIP": BoundsToSpans uses Ceiling, never Round — content must never be clipped.
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned, Size = new DrawingSize(237, 173) };

        PanelDefaults.ReconcileGridAlignedSpans(model);

        Assert.True(model.Size.Width >= 237);
        Assert.True(model.Size.Height >= 173);
    }

    /// <summary>
    /// Verifies that the size written back after reconciliation exactly matches
    /// <see cref="WorkspaceLayoutPolicy.SpansToBounds"/> for the spans it just seeded.
    /// </summary>
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

    /// <summary>
    /// Verifies the N-6 fix-cycle-2 regression: a <see cref="PanelGeometryMode.GridAligned"/> panel with
    /// a degenerate (CLR-default, 0x0) size seeds <c>PanelMetrics.DefaultPanelSize</c> instead of the
    /// smaller <c>PanelSizePreset.Medium</c>, and never quantises to a degenerate 1x1 span.
    /// </summary>
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

    /// <summary>
    /// Verifies the N-5 fix-cycle-2 regression: a tiny explicit size (e.g. a very small lasso rectangle)
    /// is clamped to <see cref="WorkspaceGridMetrics.MinSpanExpanded"/> rather than left below the
    /// documented minimum.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="PanelDefaults.ReconcileGridAlignedSpans"/> never modifies the model's
    /// existing <c>Position</c> — span/size reconciliation only.
    /// </summary>
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

    // ── helpers ──────────────────────────────────────────────────────────────
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
}
