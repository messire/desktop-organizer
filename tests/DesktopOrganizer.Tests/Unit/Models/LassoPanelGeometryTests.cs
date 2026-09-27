using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;
using DrawingPoint = System.Drawing.Point;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="LassoPanelGeometry"/> — F-7 (ADR-028 «Lasso-создание панели: px vs DIP»,
/// D3-8): the desktop lasso reports PHYSICAL pixels, <see cref="ChildWindowModel"/> stores DIP.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class LassoPanelGeometryTests
{
    private const double U = WorkspaceGridMetrics.UnitSize;
    private const double MinSize = 120d; // GridMetrics.MinPanelSize, mirrored by App.MinPanelSize

    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    private static IReadOnlyList<(string DeviceName, Rect Bounds, Rect WorkArea)> Monitors =>
    [
        (Display1, new Rect(0, 0, 1920, 1080), new Rect(0, 40, 1920, 1040)),
        (Display2, new Rect(-1366, -200, 1366, 768), new Rect(-1366, -160, 1366, 728)),
    ];

    // ── ToDip (D3-8; release-hardening F, ADR-037) ───────────────────────────────────────────────────
    //
    // ToDip now takes a DesktopScaleModel instead of a bare scalar (F3: the conversion routes through
    // the per-monitor snapshot's PxToDip, not a flat division). Every case below builds a UNIFORM
    // snapshot (Embedded mode, no monitors) at the scale under test — DesktopScaleModel's own uniform
    // fast path is `px / s` component-by-component, bit-identical to the pre-F scalar formula, so these
    // are the exact same numbers this test file asserted before F3. Multi-monitor/anchored lasso
    // conversion is additionally covered below (release-hardening F4).

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ToDip"/> divides every physical-pixel component by
    /// the DPI scale at 150%.
    /// </summary>
    [Fact]
    public void ToDip_At150Percent_DividesEveryComponentByTheScale()
    {
        var dip = LassoPanelGeometry.ToDip(new Rect(300, 300, 600, 600), UniformScale(1.5d), minSize: MinSize);

        Assert.Equal(new Rect(200, 200, 400, 400), dip);
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ToDip"/> divides every component by the DPI scale
    /// correctly across a table of scale factors, including a non-integral 175% scale.
    /// </summary>
    /// <param name="scale">The DPI scale factor under test.</param>
    /// <param name="x">The expected DIP x-coordinate.</param>
    /// <param name="y">The expected DIP y-coordinate.</param>
    /// <param name="width">The expected DIP width.</param>
    /// <param name="height">The expected DIP height.</param>
    [Theory]
    [InlineData(1.0, 400, 400, 800, 600)]
    [InlineData(1.25, 320, 320, 640, 480)]
    [InlineData(1.75, 1600d / 7d, 1600d / 7d, 3200d / 7d, 2400d / 7d)]
    public void ToDip_ScaleTable(double scale, double x, double y, double width, double height)
    {
        var dip = LassoPanelGeometry.ToDip(new Rect(400, 400, 800, 600), UniformScale(scale), MinSize);

        Assert.Equal(x, dip.X, 9);
        Assert.Equal(y, dip.Y, 9);
        Assert.Equal(width, dip.Width, 9);
        Assert.Equal(height, dip.Height, 9);
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ToDip"/> applies the minimum-size floor AFTER
    /// converting to DIP, not before — comparing the floor against physical pixels would let an
    /// undersized result through at scales above 100%.
    /// </summary>
    [Fact]
    public void ToDip_MinimumSizeIsAppliedAfterTheConversion_SoItMeansTheSameAtEveryDpi()
    {
        // 150 physical px at 150% is 100 DIP -- below the 120 DIP floor. Applying the floor BEFORE the
        // conversion (the pre-P03 order) would have compared 150 px against 120 DIP and let it through.
        var dip = LassoPanelGeometry.ToDip(new Rect(0, 0, 150, 150), UniformScale(1.5d), minSize: MinSize);

        Assert.Equal(MinSize, dip.Width);
        Assert.Equal(MinSize, dip.Height);
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ToDip"/> falls back to a 1:1 (no-op) scale for
    /// degenerate scale values (zero, negative, NaN, or infinite) rather than producing garbage output —
    /// <see cref="DesktopScaleModel.Create"/> itself sanitizes a degenerate reference/layer scale to
    /// <c>1.0</c> (see its remarks).
    /// </summary>
    /// <param name="scale">The degenerate DPI scale factor under test.</param>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ToDip_DegenerateScale_FallsBackToOneToOne(double scale)
    {
        var dip = LassoPanelGeometry.ToDip(new Rect(300, 300, 600, 600), UniformScale(scale), MinSize);

        Assert.Equal(new Rect(300, 300, 600, 600), dip);
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ToDip"/> scales negative monitor coordinates (a
    /// monitor left of/above the primary) with the same formula, no sign-sensitive special case.
    /// </summary>
    [Fact]
    public void ToDip_NegativeMonitorCoordinates_ScaleWithoutSignSpecialCasing()
    {
        var dip = LassoPanelGeometry.ToDip(new Rect(-1200, -240, 600, 600), UniformScale(1.5d), minSize: MinSize);

        Assert.Equal(-800d, dip.X);
        Assert.Equal(-160d, dip.Y);
    }

    // ── ToDip: multi-monitor, anchored (mixed-DPI) snapshots (release-hardening F4) ──────────────────

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ToDip"/> on an anchored (mixed-DPI, two-monitor)
    /// snapshot converts a lasso drawn entirely on the 150% secondary monitor through THAT monitor's own
    /// scale — a lassoed panel on a non-primary, non-reference-scale monitor comes out the right size.
    /// </summary>
    [Fact]
    public void ToDip_LassoOnSecondaryMonitorOfAnchoredLayout_UsesThatMonitorsOwnScale()
    {
        var dip = LassoPanelGeometry.ToDip(new Rect(2400, 150, 300, 300), Mixed100Plus150Right(), minSize: MinSize);

        // Secondary's DIP origin is anchored at its own left edge (1920, facing the primary); size divided
        // by its own 1.5 scale, not the primary's 1.0 reference scale.
        Assert.Equal(new Rect(1920 + ((2400 - 1920) / 1.5), 150 / 1.5, 200, 200), dip);
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ToDip"/> on an anchored (mixed-DPI, two-monitor)
    /// snapshot converts a lasso drawn on the 100% primary through the primary's own scale, unaffected by
    /// the secondary's different scale.
    /// </summary>
    [Fact]
    public void ToDip_LassoOnPrimaryMonitorOfAnchoredLayout_UsesThePrimarysOwnScale()
    {
        var dip = LassoPanelGeometry.ToDip(new Rect(100, 50, 300, 150), Mixed100Plus150Right(), minSize: MinSize);

        Assert.Equal(new Rect(100, 50, 300, 150), dip);
    }

    /// <summary>
    /// Verifies that a lasso drawn across the seam between two differently-scaled monitors converts
    /// through whichever monitor it intersects MOST — the same max-intersection rule
    /// <see cref="DesktopScaleModel.PxToDip(Rect)"/> itself documents.
    /// </summary>
    [Fact]
    public void ToDip_LassoAcrossTheSeamOfAnchoredLayout_UsesTheMaxIntersectionMonitor()
    {
        // 120 px on the primary (1800..1920), 180 px on the 150% secondary (1920..2100) -> the secondary
        // wins the intersection regardless of the (equal, 300 px tall) height on both sides.
        var dip = LassoPanelGeometry.ToDip(new Rect(1800, 100, 300, 300), Mixed100Plus150Right(), minSize: MinSize);

        Assert.Equal(new Rect(1920 + ((1800 - 1920) / 1.5), 100 / 1.5, 200, 200), dip);
    }

    /// <summary>
    /// Verifies that the minimum-size floor still applies AFTER the anchored, per-monitor conversion, on
    /// the secondary monitor of a mixed-DPI layout.
    /// </summary>
    [Fact]
    public void ToDip_LassoOnSecondaryMonitorOfAnchoredLayout_AppliesMinimumSizeFloorAfterConversion()
    {
        // 150 physical px at 150% is 100 DIP -- below the 120 DIP floor.
        var dip = LassoPanelGeometry.ToDip(new Rect(2400, 150, 150, 150), Mixed100Plus150Right(), minSize: MinSize);

        Assert.Equal(MinSize, dip.Width);
        Assert.Equal(MinSize, dip.Height);
    }

    // ── ApplyTo ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ApplyTo"/> writes the DIP rectangle verbatim (no
    /// grid alignment) onto a <see cref="PanelGeometryMode.LegacyFreeform"/> panel, leaving
    /// <see cref="ChildWindowModel.MonitorDeviceId"/> untouched.
    /// </summary>
    [Fact]
    public void ApplyTo_LegacyFreeformPanel_WritesTheDipRectangleVerbatim()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.LegacyFreeform };

        LassoPanelGeometry.ApplyTo(model, new Rect(203, 147, 400, 400), Monitors);

        Assert.Equal(new DrawingPoint(203, 147), model.Position);
        Assert.Equal(new System.Drawing.Size(400, 400), model.Size);
        Assert.Null(model.MonitorDeviceId); // untouched for the legacy mode
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ApplyTo"/> snaps a
    /// <see cref="PanelGeometryMode.GridAligned"/> panel's position onto the lassoed monitor's own grid
    /// origin and records that monitor's device id.
    /// </summary>
    [Fact]
    public void ApplyTo_GridAlignedPanel_SnapsThePositionToTheLassoedMonitorsOrigin()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };

        LassoPanelGeometry.ApplyTo(model, new Rect(203, 147, 400, 400), Monitors);

        Assert.Equal(0, (model.Position.X - 0) % (int)U);   // origin X = 0
        Assert.Equal(0, (model.Position.Y - 40) % (int)U);  // origin Y = 40 (taskbar)
        Assert.True(Math.Abs(model.Position.X - 203) <= U / 2d);
        Assert.True(Math.Abs(model.Position.Y - 147) <= U / 2d);
        Assert.Equal(Display1, model.MonitorDeviceId);
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ApplyTo"/> picks the correct monitor and grid origin
    /// even when the lassoed rectangle is on a monitor with negative screen coordinates.
    /// </summary>
    [Fact]
    public void ApplyTo_GridAlignedPanelOnANegativeCoordinateMonitor_UsesThatMonitorsOrigin()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.GridAligned };

        LassoPanelGeometry.ApplyTo(model, new Rect(-800, -100, 400, 400), Monitors);

        Assert.Equal(Display2, model.MonitorDeviceId);
        Assert.Equal(0, (model.Position.X - (-1366)) % (int)U);
        Assert.Equal(0, (model.Position.Y - (-160)) % (int)U);
    }

    /// <summary>
    /// Verifies that <see cref="LassoPanelGeometry.ApplyTo"/> rounds fractional DIP coordinates
    /// instead of truncating them, so negative coordinates are not biased toward zero.
    /// </summary>
    [Fact]
    public void ApplyTo_RoundsInsteadOfTruncating_SoNegativeCoordinatesAreNotBiasedTowardZero()
    {
        var model = new ChildWindowModel { GeometryMode = PanelGeometryMode.LegacyFreeform };

        LassoPanelGeometry.ApplyTo(model, new Rect(-100.7, -200.7, 333.6, 444.6), Monitors);

        Assert.Equal(new DrawingPoint(-101, -201), model.Position);
        Assert.Equal(new System.Drawing.Size(334, 445), model.Size);
    }

    /// <summary>Builds a uniform (single-scale) desktop-scale snapshot for the <c>ToDip</c> cases above.</summary>
    private static DesktopScaleModel UniformScale(double scale)
        => DesktopScaleModel.Create(DesktopLayerMode.Embedded, scale, scale, []);

    /// <summary>
    /// Builds an anchored, TopLevel, two-monitor snapshot (100% primary + 150% to the right) for the
    /// multi-monitor <c>ToDip</c> cases above (same layout as <c>DesktopScaleModelTests</c>' own).
    /// </summary>
    private static DesktopScaleModel Mixed100Plus150Right()
    {
        MonitorScale[] monitors =
        [
            new(Display1, new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(0, 0, 1920, 1040), true, 1d),
            new(Display2, new System.Drawing.Rectangle(1920, 0, 3840, 2160), new System.Drawing.Rectangle(1920, 0, 3840, 2100), false, 1.5d),
        ];
        return DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, monitors);
    }
}
