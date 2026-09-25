using System.Windows;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="PanelGridStateResolver"/> — the single point that reads
/// <see cref="ChildWindowModel.GeometryMode"/> (Инвариант В1,
/// docs/PLANS/workspace-grid-03-04-integration.md §1.3/§1.6). Pure: no WPF window, no
/// <c>IMonitorService</c> — monitor snapshots are plain tuples, matching the shape
/// <c>PanelDragMoveBehavior</c>/resize sessions pass in.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelGridStateResolverTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double G = WorkspaceGridMetrics.DefaultGap;

    private static readonly (string DeviceName, Rect Bounds, Rect WorkArea) Primary =
        ("\\\\.\\DISPLAY1", new Rect(0, 0, 1920, 1080), new Rect(0, 40, 1920, 1040));

    private static readonly (string DeviceName, Rect Bounds, Rect WorkArea) Secondary =
        ("\\\\.\\DISPLAY2", new Rect(1920, 0, 1920, 1080), new Rect(1920, 0, 1920, 1080));

    private static readonly (string DeviceName, Rect Bounds, Rect WorkArea) NegativeSecondary =
        ("\\\\.\\DISPLAY3", new Rect(-1920, -200, 1920, 1080), new Rect(-1920, -160, 1920, 1040));

    // ── Rule 1: Инвариант В1 — GeometryMode gate ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that resolving a null model returns <see cref="PanelGridState.Legacy"/>.
    /// </summary>
    [Fact]
    public void Resolve_NullModel_ReturnsLegacy()
    {
        var result = PanelGridStateResolver.Resolve(null, new Rect(0, 0, 260, 300), [Primary], isRolledUp: false);

        Assert.Equal(PanelGridState.Legacy, result);
        Assert.False(result.IsGridAligned);
    }

    /// <summary>
    /// Verifies that resolving a model in <see cref="PanelGeometryMode.LegacyFreeform"/> mode returns
    /// <see cref="PanelGridState.Legacy"/>, per Инвариант В1's GeometryMode gate.
    /// </summary>
    [Fact]
    public void Resolve_LegacyFreeformModel_ReturnsLegacy()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.LegacyFreeform };

        var result = PanelGridStateResolver.Resolve(model, new Rect(0, 0, 260, 300), [Primary], isRolledUp: false);

        Assert.Equal(PanelGridState.Legacy, result);
    }

    // ── Rule 2: no monitors -> fail-safe Legacy ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that resolving with an empty monitor list fails safe to
    /// <see cref="PanelGridState.Legacy"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Resolve_EmptyMonitorList_ReturnsLegacy()
    {
        var model = GridAlignedModel(widthSpanUnits: 20, heightSpanUnits: 20);

        var result = PanelGridStateResolver.Resolve(model, new Rect(0, 0, 152, 152), [], isRolledUp: false);

        Assert.Equal(PanelGridState.Legacy, result);
    }

    // ── Rule 2: monitor resolution order ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a persisted monitor device name takes priority over bounds-based containment —
    /// even when the panel's bounds sit inside a different monitor, the explicitly named one wins.
    /// </summary>
    [Fact]
    public void Resolve_MatchesMonitorByDeviceName_EvenWhenBoundsWouldSuggestAnotherMonitor()
    {
        var model = GridAlignedModel(monitorDeviceId: Secondary.DeviceName, widthSpanUnits: 20, heightSpanUnits: 20);

        // Bounds sit squarely inside Primary, but MonitorDeviceId names Secondary explicitly.
        var result = PanelGridStateResolver.Resolve(model, new Rect(100, 100, 152, 152), [Primary, Secondary], isRolledUp: false);

        Assert.True(result.IsGridAligned);
        Assert.Equal(Secondary.DeviceName, result.MonitorDeviceId);
        Assert.Equal(Secondary.WorkArea.TopLeft, result.Origin);
    }

    /// <summary>
    /// Verifies that matching the persisted monitor device name against the live monitor list is
    /// case-insensitive.
    /// </summary>
    [Fact]
    public void Resolve_DeviceNameMatch_IsCaseInsensitive()
    {
        var model = GridAlignedModel(monitorDeviceId: Secondary.DeviceName.ToLowerInvariant(), widthSpanUnits: 20, heightSpanUnits: 20);

        var result = PanelGridStateResolver.Resolve(model, new Rect(0, 0, 152, 152), [Primary, Secondary], isRolledUp: false);

        Assert.Equal(Secondary.DeviceName, result.MonitorDeviceId);
    }

    /// <summary>
    /// Verifies that when the persisted monitor device name matches nothing in the live monitor list,
    /// resolution falls back to finding the monitor whose bounds contain the panel's center.
    /// </summary>
    [Fact]
    public void Resolve_NoDeviceNameMatch_FallsBackToCenterContainment()
    {
        // Center of this rect lands on Secondary (1920..3840 x range).
        var bounds = new Rect(2000, 100, 200, 200);
        var model = GridAlignedModel(monitorDeviceId: "\\\\.\\DISPLAY-UNKNOWN", widthSpanUnits: 20, heightSpanUnits: 20);

        var result = PanelGridStateResolver.Resolve(model, bounds, [Primary, Secondary], isRolledUp: false);

        Assert.Equal(Secondary.DeviceName, result.MonitorDeviceId);
    }

    /// <summary>
    /// Verifies that the bounds-based monitor fallback uses the panel's center point, not its
    /// top-left corner, to find the containing monitor (ADR-028).
    /// </summary>
    [Fact]
    public void Resolve_UsesCenter_NotTopLeft_ToFindMonitor()
    {
        // Top-left is off every monitor (negative, outside NegativeSecondary's bounds too), but the
        // CENTER of this rect lands inside Primary — the resolver must use the center, not top-left
        // (ADR-028: top-left-outside-every-monitor is exactly the failure mode this rule avoids).
        var bounds = new Rect(-100, -100, 300, 300); // center = (50, 50) -> inside Primary.
        var model = GridAlignedModel(widthSpanUnits: 20, heightSpanUnits: 20);

        var result = PanelGridStateResolver.Resolve(model, bounds, [Primary, Secondary], isRolledUp: false);

        Assert.Equal(Primary.DeviceName, result.MonitorDeviceId);
    }

    /// <summary>
    /// Verifies that when neither the device name nor the center-containment check matches any
    /// monitor, resolution falls back to the first monitor in the supplied list.
    /// </summary>
    [Fact]
    public void Resolve_NoMatchAtAll_FallsBackToFirstMonitorInList()
    {
        // Center is off every monitor entirely.
        var bounds = new Rect(100_000, 100_000, 10, 10);
        var model = GridAlignedModel(widthSpanUnits: 20, heightSpanUnits: 20);

        var result = PanelGridStateResolver.Resolve(model, bounds, [Secondary, Primary], isRolledUp: false);

        Assert.Equal(Secondary.DeviceName, result.MonitorDeviceId);
    }

    /// <summary>
    /// Verifies that resolving against a monitor with a negative-coordinate work area correctly
    /// translates the resulting origin, work area, and device id to that monitor's values.
    /// </summary>
    [Fact]
    public void Resolve_NegativeMonitorOrigin_TranslatesCorrectly()
    {
        var bounds = new Rect(-1900, -150, 152, 152); // center inside NegativeSecondary.
        var model = GridAlignedModel(widthSpanUnits: 20, heightSpanUnits: 20);

        var result = PanelGridStateResolver.Resolve(model, bounds, [Primary, NegativeSecondary], isRolledUp: false);

        Assert.Equal(NegativeSecondary.DeviceName, result.MonitorDeviceId);
        Assert.Equal(NegativeSecondary.WorkArea.TopLeft, result.Origin);
        Assert.Equal(NegativeSecondary.WorkArea, result.WorkArea);
    }

    // ── Rule 4: constants + rolled-up-aware minimum span ──────────────────────────────────────────

    /// <summary>
    /// Verifies that resolving an expanded (not rolled-up) panel uses
    /// <see cref="WorkspaceGridMetrics.MinSpanExpanded"/> as the minimum span, plus the shared
    /// unit size and gap constants.
    /// </summary>
    [Fact]
    public void Resolve_Expanded_UsesMinSpanExpanded()
    {
        var model = GridAlignedModel(widthSpanUnits: 20, heightSpanUnits: 20);

        var result = PanelGridStateResolver.Resolve(model, new Rect(0, 0, 152, 152), [Primary], isRolledUp: false);

        Assert.Equal(WorkspaceGridMetrics.MinSpanExpanded, result.MinSpan);
        Assert.Equal(U, result.UnitSize);
        Assert.Equal(G, result.Gap);
    }

    /// <summary>
    /// Verifies that resolving a rolled-up panel uses
    /// <see cref="WorkspaceGridMetrics.MinSpanCollapsed"/> as the minimum span, instead of the
    /// expanded-panel minimum.
    /// </summary>
    [Fact]
    public void Resolve_RolledUp_UsesMinSpanCollapsed()
    {
        var model = GridAlignedModel(widthSpanUnits: 20, heightSpanUnits: WorkspaceGridMetrics.CollapsedHeaderSpan);

        var result = PanelGridStateResolver.Resolve(model, new Rect(0, 0, 152, 152), [Primary], isRolledUp: true);

        Assert.Equal(WorkspaceGridMetrics.MinSpanCollapsed, result.MinSpan);
    }

    // ── Rule 5: persisted spans, valid ────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that already-valid persisted width/height spans are used as-is, without the resolver
    /// rewriting the model's span properties.
    /// </summary>
    [Fact]
    public void Resolve_ValidPersistedSpans_AreUsedAsIs_AndModelIsNotRewritten()
    {
        var model = GridAlignedModel(widthSpanUnits: 32, heightSpanUnits: 30);

        var result = PanelGridStateResolver.Resolve(model, new Rect(0, 0, 9999, 9999), [Primary], isRolledUp: false);

        Assert.True(result.IsGridAligned);
        Assert.Equal(32, model.WidthSpanUnits);
        Assert.Equal(30, model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that missing or below-minimum persisted spans trigger lazy migration: the resolver
    /// derives fresh width/height spans from the panel's current bounds and writes them back to the
    /// model.
    /// </summary>
    /// <param name="width">The persisted width span, or null/below-minimum to force migration.</param>
    /// <param name="height">The persisted height span, or null/below-minimum to force migration.</param>
    [Theory]
    [InlineData(null, 20)]
    [InlineData(20, null)]
    [InlineData(1, 20)] // below MinSpanExpanded (12x12)
    [InlineData(20, 1)]
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    public void Resolve_MissingOrBelowMinimumSpans_TriggersLazyMigrationFromCurrentSize(int? width, int? height)
    {
        var model = GridAlignedModel(widthSpanUnits: width, heightSpanUnits: height);
        var bounds = new Rect(0, 0, 152, 152); // exactly 20x20 units: 20*8-8 = 152.

        var result = PanelGridStateResolver.Resolve(model, bounds, [Primary], isRolledUp: false);

        Assert.True(result.IsGridAligned);
        var expected = WorkspaceLayoutPolicy.BoundsToSpans(bounds.Size, U, G);
        Assert.Equal(expected.WidthUnits, model.WidthSpanUnits);
        Assert.Equal(expected.HeightUnits, model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that when there is no persisted span AND the current bounds are degenerate (zero or
    /// NaN in either dimension), the resolver returns <see cref="PanelGridState.Legacy"/> for this
    /// frame without writing any span onto the model.
    /// </summary>
    /// <param name="width">A degenerate (zero or NaN) width to resolve against.</param>
    /// <param name="height">A degenerate (zero or NaN) height to resolve against.</param>
    [Theory]
    [InlineData(0d, 100d)]
    [InlineData(100d, 0d)]
    [InlineData(double.NaN, 100d)]
    [InlineData(100d, double.NaN)]
    public void Resolve_MissingSpansAndDegenerateSize_ReturnsLegacyForThisFrame_WithoutTouchingModel(double width, double height)
    {
        var model = GridAlignedModel(); // no persisted spans at all -- e.g. AddPanel's (0,0) path.
        var bounds = new Rect(0, 0, width, height);

        var result = PanelGridStateResolver.Resolve(model, bounds, [Primary], isRolledUp: false);

        Assert.Equal(PanelGridState.Legacy, result);
        Assert.Null(model.WidthSpanUnits);
        Assert.Null(model.HeightSpanUnits);
    }

    /// <summary>
    /// Verifies that a model with no persisted spans and a zero-size rectangle (matching the state
    /// <c>AddPanel</c> is in before its first <c>SizeChanged</c>) resolves to
    /// <see cref="PanelGridState.Legacy"/>.
    /// </summary>
    [Fact]
    public void Resolve_MissingSpans_ZeroSize_ReturnsLegacy_MatchingAddPanelBeforeFirstRenderSizeChanged()
    {
        var model = GridAlignedModel();

        var result = PanelGridStateResolver.Resolve(model, new Rect(0, 0, 0, 0), [Primary], isRolledUp: false);

        Assert.Equal(PanelGridState.Legacy, result);
    }

    // ── ParticipatesInGrid (P03 fast path) ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelGridStateResolver.ParticipatesInGrid"/> returns false for both a
    /// null model and a <see cref="PanelGeometryMode.LegacyFreeform"/> model.
    /// </summary>
    [Fact]
    public void ParticipatesInGrid_NullOrLegacyFreeform_IsFalse()
    {
        Assert.False(PanelGridStateResolver.ParticipatesInGrid(null));
        Assert.False(PanelGridStateResolver.ParticipatesInGrid(
            new ChildWindowModel { GeometryMode = PanelGeometryMode.LegacyFreeform }));
    }

    /// <summary>
    /// Verifies that <see cref="PanelGridStateResolver.ParticipatesInGrid"/> returns true for a
    /// grid-aligned model even without any persisted spans — it is only a "could it" pre-check, not
    /// the authority on whether the panel actually resolves to a usable grid state.
    /// </summary>
    [Fact]
    public void ParticipatesInGrid_GridAligned_IsTrue_EvenWithoutSpans()
    {
        // Deliberately true here: this is only a "could it" pre-check, and Resolve remains the authority
        // on whether the panel actually resolves to a usable grid state this frame.
        Assert.True(PanelGridStateResolver.ParticipatesInGrid(GridAlignedModel()));
    }

    /// <summary>
    /// Verifies that when <see cref="PanelGridStateResolver.ParticipatesInGrid"/> returns false for a
    /// legacy model, <see cref="PanelGridStateResolver.Resolve"/> agrees by returning
    /// <see cref="PanelGridState.Legacy"/> for the same model.
    /// </summary>
    [Fact]
    public void ParticipatesInGrid_False_AgreesWithResolve()
    {
        var legacy = new ChildWindowModel { GeometryMode = PanelGeometryMode.LegacyFreeform };

        Assert.False(PanelGridStateResolver.ParticipatesInGrid(legacy));
        Assert.Equal(
            PanelGridState.Legacy,
            PanelGridStateResolver.Resolve(legacy, new Rect(0, 0, 260, 300), [Primary], isRolledUp: false));
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static ChildWindowModel GridAlignedModel(
        string? monitorDeviceId = null,
        int? widthSpanUnits = null,
        int? heightSpanUnits = null) =>
        new()
        {
            GeometryMode = PanelGeometryMode.GridAligned,
            MonitorDeviceId = monitorDeviceId,
            WidthSpanUnits = widthSpanUnits,
            HeightSpanUnits = heightSpanUnits,
        };
}
