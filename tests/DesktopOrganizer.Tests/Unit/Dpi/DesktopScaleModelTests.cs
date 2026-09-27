using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using Rectangle = System.Drawing.Rectangle;

namespace DesktopOrganizer.Tests.Unit.Dpi;

/// <summary>
/// Unit tests for <see cref="DesktopScaleModel"/> (release-hardening F1, plan §2/§7): the uniform fast path
/// must be bit-identical to 0.6.1's <c>px / scale</c>, anchored top-level snapshots keep the edge facing
/// the primary monitor, overlaps fall back to the uniform mapping, and the window adapters are identity
/// wherever the plan says so.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DesktopScaleModelTests
{
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";
    private const string Display3 = @"\\.\DISPLAY3";

    /// <summary>Layout names for the uniform theory.</summary>
    public enum Layout
    {
        /// <summary>One monitor.</summary>
        Single,

        /// <summary>Primary plus a second monitor to its right.</summary>
        TwoRight,

        /// <summary>Primary plus a second monitor to its left (negative px) and above it.</summary>
        TwoLeftNegative,
    }

    /// <summary>
    /// Scales × modes × layouts for <see cref="UniformFastPath_IsBitIdenticalTo061Formula"/>.
    /// </summary>
    public static TheoryData<double, DesktopLayerMode, Layout> UniformCases
    {
        get
        {
            var data = new TheoryData<double, DesktopLayerMode, Layout>();
            foreach (var s in new[] { 1d, 1.25d, 1.5d, 1.75d, 2d })
            {
                foreach (var mode in new[] { DesktopLayerMode.Embedded, DesktopLayerMode.TopLevel })
                {
                    foreach (var layout in Enum.GetValues<Layout>())
                        data.Add(s, mode, layout);
                }
            }

            return data;
        }
    }

    // ── ScaleFromDpi ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Verifies the DPI → scale conversion and its fallback for 0.</summary>
    /// <param name="dpi">Input DPI.</param>
    /// <param name="fallback">Fallback scale.</param>
    /// <param name="expected">Expected scale.</param>
    [Theory]
    [InlineData(96u, 1d, 1d)]
    [InlineData(120u, 1d, 1.25d)]
    [InlineData(144u, 1d, 1.5d)]
    [InlineData(192u, 1d, 2d)]
    [InlineData(0u, 1d, 1d)]
    [InlineData(0u, 1.75d, 1.75d)]
    public void ScaleFromDpi_DividesByBaseDpi_AndFallsBackForZero(uint dpi, double fallback, double expected)
        => Assert.Equal(expected, DesktopScaleModel.ScaleFromDpi(dpi, fallback));

    // ── Uniform fast path (persistence invariance) ───────────────────────────────────────────────

    /// <summary>
    /// For every single-scale configuration, in both modes, bounds/work area and every conversion equal the
    /// 0.6.1 formula <c>px / s</c> EXACTLY (no tolerance) — the proof that persisted geometry does not move.
    /// </summary>
    /// <param name="s">The common scale.</param>
    /// <param name="mode">The desktop layer mode.</param>
    /// <param name="layout">The monitor layout.</param>
    [Theory]
    [MemberData(nameof(UniformCases))]
    public void UniformFastPath_IsBitIdenticalTo061Formula(double s, DesktopLayerMode mode, Layout layout)
    {
        var monitors = BuildLayout(layout, s);
        var model = DesktopScaleModel.Create(mode, s, s, monitors);

        Assert.True(model.IsUniform);
        Assert.False(model.IsAnchorOverlapFallback);
        Assert.Equal(s, model.UniformScale);

        foreach (var m in monitors)
        {
            Assert.Equal(Golden061(m.BoundsPx, s), model.GetBoundsDip(m.DeviceName));
            Assert.Equal(Golden061(m.WorkAreaPx, s), model.GetWorkAreaDip(m.DeviceName));
            Assert.Equal(s, model.GetGeometryScale(m.DeviceName));
        }

        foreach (var (x, y) in new[] { (0d, 0d), (1d, 1d), (333d, 777d), (-1367d, -201d), (1919d, 1079d), (4001d, 17d) })
        {
            Assert.Equal(new Point(x / s, y / s), model.PxToDip(x, y));
            Assert.Equal(new Point(x * s, y * s), model.DipToPx(new Point(x, y)));
        }

        var px = new Rect(-1301, 257, 333, 187);
        Assert.Equal(new Rect(px.X / s, px.Y / s, px.Width / s, px.Height / s), model.PxToDip(px));
        Assert.Equal(new Rect(px.X * s, px.Y * s, px.Width * s, px.Height * s), model.DipToPx(px));
    }

    /// <summary>
    /// Uniform snapshots with the window at the same scale: both window adapters are the identity, bit
    /// for bit — <c>Window.Left</c> equals the model position exactly as in 0.6.1.
    /// </summary>
    /// <param name="s">The common scale.</param>
    /// <param name="mode">The desktop layer mode.</param>
    /// <param name="layout">The monitor layout.</param>
    [Theory]
    [MemberData(nameof(UniformCases))]
    public void UniformFastPath_WindowAdaptersAreIdentity(double s, DesktopLayerMode mode, Layout layout)
    {
        var model = DesktopScaleModel.Create(mode, s, s, BuildLayout(layout, s));
        var bounds = new Rect(0.1 + (1d / 3d), -123.456, 286, 132.5);

        Assert.Equal(bounds, model.WindowToDesktop(bounds, s));
        Assert.Equal(bounds.TopLeft, model.DesktopToWindow(bounds, s));
    }

    /// <summary>
    /// Embedded mode is uniform with the layer scale even when the monitors have different scales
    /// (children of WorkerW share its single DPI) — top-level windows still get the per-monitor scale.
    /// </summary>
    [Fact]
    public void Embedded_WithMixedMonitorScales_IsUniformAtLayerScale()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.Embedded, 1d, 1d, Mixed100Plus150Right());

        Assert.True(model.IsUniform);
        Assert.Equal(1d, model.LayerScale);
        Assert.Equal(new Rect(1920, 0, 3840, 2160), model.GetBoundsDip(Display2));
        Assert.Equal(1d, model.GetGeometryScale(Display2));
        Assert.Equal(1.5d, model.GetTopLevelScale(Display2));
        Assert.Equal(1d, model.GetTopLevelScale(Display1));
    }

    /// <summary>
    /// A top-level snapshot whose monitors all share a scale different from the system scale (scale changed
    /// without signing out) uses the common monitor scale, matching what PerMonitorV2 windows get.
    /// </summary>
    [Fact]
    public void TopLevel_AllMonitorsSameScale_DifferentFromReference_UsesCommonScale()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, BuildLayout(Layout.TwoRight, 1.5));

        Assert.True(model.IsUniform);
        Assert.Equal(1.5d, model.UniformScale);
        Assert.Equal(1d, model.LayerScale);
        Assert.Equal(new Rect(0, 0, 2560 / 1.5, 1440 / 1.5), model.GetBoundsDip(Display1));
    }

    // ── Anchored top-level snapshots ─────────────────────────────────────────────────────────────

    /// <summary>100 % primary + 150 % to the right: the secondary's left edge stays at the primary's right edge.</summary>
    [Fact]
    public void Anchored_150PercentRight_KeepsLeftEdgeFacingPrimary()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        Assert.False(model.IsUniform);
        Assert.Equal(new Rect(0, 0, 1920, 1080), model.GetBoundsDip(Display1));
        Assert.Equal(new Rect(1920, 0, 2560, 1440), model.GetBoundsDip(Display2));
        Assert.Equal(new Rect(1920, 0, 2560, 1400), model.GetWorkAreaDip(Display2));
        Assert.Equal(1.5d, model.GetGeometryScale(Display2));
        Assert.Equal(1d, model.GetGeometryScale(Display1));
        AssertNoOverlap(model);
    }

    /// <summary>150 % primary + 100 % to the left (negative px): the left monitor keeps its RIGHT edge at 0.</summary>
    [Fact]
    public void Anchored_100PercentLeftOf150Primary_KeepsRightEdgeFacingPrimary()
    {
        var monitors = new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 2880, 1620), true, 1.5),
            Monitor(Display2, new Rectangle(-1920, 0, 1920, 1080), false, 1d),
        };

        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1.5, 1.5, monitors);

        Assert.False(model.IsUniform);
        Assert.Equal(new Rect(0, 0, 1920, 1080), model.GetBoundsDip(Display1));
        Assert.Equal(new Rect(-1920, 0, 1920, 1080), model.GetBoundsDip(Display2));
        AssertNoOverlap(model);
    }

    /// <summary>100 % primary + 150 % above: the upper monitor keeps its BOTTOM edge at the primary's top.</summary>
    [Fact]
    public void Anchored_150PercentAbove_KeepsBottomEdgeFacingPrimary()
    {
        var monitors = new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 1920, 1080), true, 1d),
            Monitor(Display2, new Rectangle(0, -2160, 3840, 2160), false, 1.5),
        };

        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, monitors);

        Assert.Equal(new Rect(0, -1440, 2560, 1440), model.GetBoundsDip(Display2));
        AssertNoOverlap(model);
    }

    /// <summary>
    /// Three monitors whose anchored DIP rectangles overlap degrade to the uniform <c>px / s_ref</c>
    /// mapping (0.6.1 geometry) and flag <see cref="DesktopScaleModel.IsAnchorOverlapFallback"/>.
    /// </summary>
    [Fact]
    public void Anchored_OverlappingLayout_FallsBackToUniformReferenceScale()
    {
        var monitors = new[]
        {
            Monitor(Display1, new Rectangle(0, 0, 2880, 1620), true, 1.5),
            Monitor(Display2, new Rectangle(2880, 0, 1920, 1080), false, 1d),
            Monitor(Display3, new Rectangle(4800, 0, 1920, 1080), false, 1.5),
        };

        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1.5, 1.5, monitors);

        Assert.True(model.IsUniform);
        Assert.True(model.IsAnchorOverlapFallback);
        Assert.Equal(1.5d, model.UniformScale);
        Assert.Equal(new Rect(4800 / 1.5, 0, 1920 / 1.5, 1080 / 1.5), model.GetBoundsDip(Display3));
        Assert.Equal(1.5d, model.GetGeometryScale(Display2));
    }

    /// <summary>PxToDip ∘ DipToPx round-trips within half a pixel on every monitor of an anchored layout.</summary>
    /// <param name="x">Physical x.</param>
    /// <param name="y">Physical y.</param>
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(1919d, 1079d)]
    [InlineData(1920d, 0d)]
    [InlineData(3001d, 777d)]
    [InlineData(5759d, 2159d)]
    public void Anchored_PxDipRoundTrip_WithinHalfPixel(double x, double y)
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        var back = model.DipToPx(model.PxToDip(x, y));

        Assert.InRange(back.X, x - 0.5, x + 0.5);
        Assert.InRange(back.Y, y - 0.5, y + 0.5);
    }

    /// <summary>A point outside every monitor maps through the nearest monitor.</summary>
    [Fact]
    public void Anchored_PointOutsideMonitors_UsesNearestMonitor()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        Assert.Equal(new Point(1920 + ((6000 - 1920) / 1.5), 100 / 1.5), model.PxToDip(6000, 100));
        Assert.Equal(new Point(-50, 100), model.PxToDip(-50, 100));
    }

    /// <summary>A rectangle across the seam maps through the monitor it intersects most.</summary>
    [Fact]
    public void Anchored_RectOnSeam_UsesMaxIntersectionMonitor()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        // 120 px on the primary, 180 px on the 150 % secondary → secondary.
        var dip = model.PxToDip(new Rect(1800, 150, 300, 150));

        Assert.Equal(new Rect(1920 + ((1800 - 1920) / 1.5), 100, 200, 100), dip);
    }

    /// <summary>An exact intersection tie goes to the primary monitor.</summary>
    [Fact]
    public void Anchored_RectOnSeam_TieGoesToPrimary()
    {
        var monitors = Mixed100Plus150Right().Reverse().ToArray(); // primary listed last on purpose
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, monitors);

        var dip = model.PxToDip(new Rect(1820, 100, 200, 100));

        Assert.Equal(new Rect(1820, 100, 200, 100), dip);
    }

    // ── Window adapters ──────────────────────────────────────────────────────────────────────────

    /// <summary>Embedded: both adapters are the identity regardless of the window scale.</summary>
    [Fact]
    public void Embedded_WindowAdapters_AreIdentity()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.Embedded, 1d, 1d, Mixed100Plus150Right());
        var bounds = new Rect(2500.5, 300.25, 286, 132);

        Assert.Equal(bounds, model.WindowToDesktop(bounds, 1.5));
        Assert.Equal(bounds.TopLeft, model.DesktopToWindow(bounds, 1.5));
    }

    /// <summary>
    /// Anchored top-level: a window on the 150 % secondary at px (3000, 150) has Left/Top (2000, 100) in its
    /// own DIPs; it maps to desktop-DIP (2640, 100) with its size unchanged, and back.
    /// </summary>
    [Fact]
    public void Anchored_WindowAdapters_ArePerMonitorAndRoundTrip()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        var desktop = model.WindowToDesktop(new Rect(2000, 100, 200, 100), 1.5);
        Assert.Equal(new Rect(2640, 100, 200, 100), desktop);

        var window = model.DesktopToWindow(desktop, 1.5);
        Assert.Equal(2000, window.X, 9);
        Assert.Equal(100, window.Y, 9);
    }

    /// <summary>
    /// Uniform top-level snapshot with a window transiently at another scale (before its
    /// <c>WM_DPICHANGED</c>): the adapters convert through px instead of passing the value through.
    /// </summary>
    [Fact]
    public void UniformTopLevel_WindowAtOtherScale_ConvertsThroughPx()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, BuildLayout(Layout.Single, 1d));

        Assert.Equal(new Rect(300, 150, 200, 100), model.WindowToDesktop(new Rect(200, 100, 200, 100), 1.5));
        Assert.Equal(new Point(200, 100), model.DesktopToWindow(new Rect(300, 150, 200, 100), 1.5));
    }

    // ── Sanitizing, lookups, equivalence ─────────────────────────────────────────────────────────

    /// <summary>Invalid reference/layer/monitor scales are replaced instead of producing NaN/∞ geometry.</summary>
    [Fact]
    public void Create_SanitizesInvalidScales()
    {
        var model = DesktopScaleModel.Create(
            DesktopLayerMode.Embedded,
            layerScale: -1,
            referenceScale: 0,
            [Monitor(Display1, new Rectangle(0, 0, 1920, 1080), true, double.NaN)]);

        Assert.Equal(1d, model.ReferenceScale);
        Assert.Equal(1d, model.LayerScale);
        Assert.Equal(1d, model.Monitors[0].Scale);
        Assert.Equal(new Rect(0, 0, 1920, 1080), model.GetBoundsDip(Display1));
    }

    /// <summary>No monitors: uniform at the reference scale (the provider's last-resort snapshot).</summary>
    [Fact]
    public void Create_WithoutMonitors_IsUniformAtReferenceScale()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1.25, 1.25, []);

        Assert.True(model.IsUniform);
        Assert.Equal(1.25, model.UniformScale);
        Assert.Equal(new Point(100, 200), model.PxToDip(125, 250));
    }

    /// <summary>Unknown device names answer empty rectangles and the fallback scales.</summary>
    [Fact]
    public void UnknownDevice_AnswersEmptyAndFallbackScales()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        Assert.Equal(Rect.Empty, model.GetBoundsDip(@"\\.\DISPLAY9"));
        Assert.Equal(Rect.Empty, model.GetWorkAreaDip(@"\\.\DISPLAY9"));
        Assert.Equal(1d, model.GetTopLevelScale(@"\\.\DISPLAY9"));
    }

    /// <summary>Equivalence compares mode, scales and monitors — not instance identity.</summary>
    [Fact]
    public void IsEquivalentTo_ComparesValues()
    {
        var a = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());
        var b = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());
        var otherMode = DesktopScaleModel.Create(DesktopLayerMode.Embedded, 1d, 1d, Mixed100Plus150Right());
        var otherDpi = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, BuildLayout(Layout.TwoRight, 1d));

        Assert.True(a.IsEquivalentTo(b));
        Assert.False(a.IsEquivalentTo(otherMode));
        Assert.False(a.IsEquivalentTo(otherDpi));
        Assert.False(a.IsEquivalentTo(null));
    }

    private static Rect Golden061(Rectangle r, double scale)
    {
        // Verbatim 0.6.1 MonitorService.ToDipRect.
        var safeScale = scale > 0 ? scale : 1.0;
        return new Rect(r.Left / safeScale, r.Top / safeScale, r.Width / safeScale, r.Height / safeScale);
    }

    private static MonitorScale Monitor(string name, Rectangle bounds, bool primary, double scale)
        => new(name, bounds, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - (int)(40 * (double.IsNaN(scale) ? 1 : scale))), primary, scale);

    private static MonitorScale[] Mixed100Plus150Right() =>
    [
        Monitor(Display1, new Rectangle(0, 0, 1920, 1080), true, 1d),
        Monitor(Display2, new Rectangle(1920, 0, 3840, 2160), false, 1.5),
    ];

    private static MonitorScale[] BuildLayout(Layout layout, double s)
    {
        var primary = Monitor(Display1, new Rectangle(0, 0, 2560, 1440), true, s);
        return layout switch
        {
            Layout.Single => [primary],
            Layout.TwoRight => [primary, Monitor(Display2, new Rectangle(2560, 0, 1920, 1080), false, s)],
            _ => [primary, Monitor(Display2, new Rectangle(-1366, -200, 1366, 768), false, s)],
        };
    }

    private static void AssertNoOverlap(DesktopScaleModel model)
    {
        var rects = model.Monitors.Select(m => model.GetBoundsDip(m.DeviceName)).ToArray();
        for (var i = 0; i < rects.Length; i++)
        {
            for (var j = i + 1; j < rects.Length; j++)
            {
                var w = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].Left, rects[j].Left);
                var h = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Top, rects[j].Top);
                Assert.False(w > 1e-9 && h > 1e-9, $"DIP rectangles {rects[i]} and {rects[j]} overlap.");
            }
        }
    }
}
