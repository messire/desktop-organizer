using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction;
using Rectangle = System.Drawing.Rectangle;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Unit tests for release-hardening F4 (docs/PLANS/release-hardening-1.0-F-dpi.md §7
/// "DragPositionCalculatorTests", ADR-037): <see cref="DragPositionCalculator.ResolveDesktopTopLeft"/>
/// anchors a title-bar drag gesture at its physical-pixel start position, applies the physical-pixel
/// cursor delta (no DPI conversion needed — both are already physical pixels), then converts the raw
/// physical-pixel top-left into desktop-DIP through whichever monitor the cursor now sits on.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class DragPositionCalculatorTests
{
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    // ── Uniform snapshot: identity on the delta, plain px/scale conversion ─────────────────────────

    /// <summary>
    /// Verifies that on a uniform (single-scale) snapshot, dragging by a physical-pixel delta moves the
    /// desktop-DIP top-left by exactly that delta divided by the scale.
    /// </summary>
    /// <param name="scale">The uniform DPI scale under test.</param>
    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void ResolveDesktopTopLeft_UniformSnapshot_MovesByDeltaDividedByScale(double scale)
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.Embedded, scale, scale, []);

        var result = DragPositionCalculator.ResolveDesktopTopLeft(
            startTopLeftPx: new Point(100 * scale, 200 * scale),
            startScreenPx: new Point(500, 500),
            currentScreenPx: new Point(560, 530),
            model);

        // 60/30 physical-pixel delta, divided by scale, added to the panel's own DIP start (100,200).
        Assert.Equal(new Point(100 + (60 / scale), 200 + (30 / scale)), result);
    }

    /// <summary>
    /// Verifies that a zero cursor delta (no movement yet — the first frame of a drag gesture) resolves
    /// back to exactly the panel's own starting position, in DIP.
    /// </summary>
    [Fact]
    public void ResolveDesktopTopLeft_ZeroDelta_ReturnsStartingPositionInDip()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.Embedded, 1.5d, 1.5d, []);

        var result = DragPositionCalculator.ResolveDesktopTopLeft(
            startTopLeftPx: new Point(300, 450),
            startScreenPx: new Point(500, 500),
            currentScreenPx: new Point(500, 500),
            model);

        Assert.Equal(new Point(200, 300), result);
    }

    // ── Anchored (mixed-DPI, two-monitor) snapshot: no jump crossing the seam ───────────────────────

    /// <summary>
    /// Verifies that dragging a panel from the 100% primary onto the 150% secondary resolves through the
    /// secondary's own scale once the cursor has actually crossed the seam — no jump, no residual use of
    /// the primary's scale.
    /// </summary>
    [Fact]
    public void ResolveDesktopTopLeft_DragCrossesTheMonitorSeam_UsesTheDestinationMonitorsOwnScale()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        // Panel's title bar starts at px (1800, 100) (on the primary, near the right edge) and is dragged
        // 200 px right, landing at px (2000, 100) -- now on the 150% secondary.
        var result = DragPositionCalculator.ResolveDesktopTopLeft(
            startTopLeftPx: new Point(1800, 100),
            startScreenPx: new Point(1800, 100),
            currentScreenPx: new Point(2000, 100),
            model);

        // Secondary's DIP origin is anchored at its own left edge (1920) since it lies to the right of
        // the primary; (2000 - 1920) / 1.5 = 53.33... added to that origin. Y never moved, but is still
        // divided by the secondary's own (1.5) scale once the cursor is on it.
        Assert.Equal(1920 + ((2000 - 1920) / 1.5), result.X, 9);
        Assert.Equal(100d / 1.5, result.Y, 9);
    }

    /// <summary>
    /// Verifies that a drag which stays entirely within one monitor of an anchored (mixed-DPI) layout never
    /// consults the other monitor's scale at all — the resolved DIP delta is exactly the physical-pixel
    /// delta divided by that one monitor's own scale.
    /// </summary>
    [Fact]
    public void ResolveDesktopTopLeft_DragStaysOnTheSecondaryMonitor_UsesOnlyItsOwnScale()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        var result = DragPositionCalculator.ResolveDesktopTopLeft(
            startTopLeftPx: new Point(3000, 300),
            startScreenPx: new Point(3000, 300),
            currentScreenPx: new Point(3150, 450),
            model);

        Assert.Equal(1920 + ((3150 - 1920) / 1.5), result.X, 9);
        Assert.Equal((450 - 0) / 1.5, result.Y, 9);
    }

    /// <summary>
    /// Verifies that a drag which stays entirely within the 100% primary of a mixed-DPI layout resolves as
    /// plain 1:1 desktop-DIP, unaffected by the secondary monitor's different scale existing at all.
    /// </summary>
    [Fact]
    public void ResolveDesktopTopLeft_DragStaysOnThePrimaryMonitor_UnaffectedBySecondarysScale()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        var result = DragPositionCalculator.ResolveDesktopTopLeft(
            startTopLeftPx: new Point(100, 200),
            startScreenPx: new Point(500, 500),
            currentScreenPx: new Point(530, 480),
            model);

        Assert.Equal(new Point(130, 180), result);
    }

    // ── H2 (release-hardening 1.0 review, MV-1): virtual-screen origin regression ─────────────────

    /// <summary>
    /// Regression for H2: <see cref="DesktopOrganizer.UI.Behaviors.PanelDragMoveBehavior"/> used to anchor
    /// <c>_dragStartTopLeftPx</c> at <c>Window.PointToScreen(0,0)</c> — TRUE physical screen pixels — while
    /// every per-frame result of this method is written back through
    /// <see cref="PanelWindowCoordinates.WriteDesktopPosition"/>, i.e. in desktop-DIP space, whose origin
    /// (release-hardening F, ADR-037's anchored/piecewise mapping) is generally offset from the physical
    /// screen origin whenever a monitor sits left of/above the primary (a non-zero "virtual-screen
    /// origin", DesktopCoordinateSpace). Anchoring the start point in a different space than the one every
    /// subsequent frame writes into produced a jump on the very first move.
    /// <para>
    /// This test builds exactly that layout — a monitor entirely to the LEFT of the primary, so its DIP
    /// origin (anchored on its right edge, see <see cref="DesktopScaleModel"/>'s remarks) sits at a
    /// negative offset from its own physical bounds — and verifies the FIXED anchor
    /// (<c>scale.DipToPx(PanelWindowCoordinates.ReadDesktopBounds(window, scale).TopLeft)</c>, the same
    /// expression <see cref="DesktopOrganizer.UI.Behaviors.PanelDragMoveBehavior"/>'s
    /// <c>OnTitleBarMouseLeftButtonDown</c> now uses) round-trips: with a zero cursor delta,
    /// <see cref="DragPositionCalculator.ResolveDesktopTopLeft"/> resolves back to EXACTLY the panel's own
    /// starting desktop-DIP position — i.e. no jump on the first frame of a drag that starts on a monitor
    /// with a non-zero virtual-screen origin.
    /// </para>
    /// </summary>
    [Fact]
    public void ResolveDesktopTopLeft_NonZeroVirtualScreenOrigin_ZeroDeltaAnchorRoundTripsWithNoJump()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, MonitorLeftOfPrimary());

        // The panel's own starting position, in desktop-DIP, entirely inside the left-of-primary
        // secondary's anchored DIP rectangle (see MonitorLeftOfPrimary's remarks: that rectangle is
        // [-1280, 0) x [0, 1080) once anchored at its right edge).
        var startDesktopDip = new Point(-640, 300);

        // The fix: derive the px anchor from the SAME desktop-DIP space every frame writes into, not a
        // raw PointToScreen(0,0) screen-pixel read.
        var dragStartTopLeftPx = model.DipToPx(startDesktopDip);

        var result = DragPositionCalculator.ResolveDesktopTopLeft(
            startTopLeftPx: dragStartTopLeftPx,
            startScreenPx: new Point(1000, 1000),
            currentScreenPx: new Point(1000, 1000), // zero delta: first frame, cursor hasn't moved yet
            model);

        Assert.Equal(startDesktopDip.X, result.X, 9);
        Assert.Equal(startDesktopDip.Y, result.Y, 9);
    }

    /// <summary>
    /// Same non-zero-virtual-screen-origin layout as the zero-delta test above, but with a real cursor
    /// movement that stays within the secondary monitor — verifies the resolved delta is exactly the
    /// physical-pixel movement divided by the secondary's own scale, unaffected by its DIP origin being a
    /// negative offset from its own physical bounds.
    /// </summary>
    [Fact]
    public void ResolveDesktopTopLeft_NonZeroVirtualScreenOrigin_RealMovementAppliesCorrectDelta()
    {
        var model = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, MonitorLeftOfPrimary());
        var startDesktopDip = new Point(-640, 300);
        var dragStartTopLeftPx = model.DipToPx(startDesktopDip);

        var result = DragPositionCalculator.ResolveDesktopTopLeft(
            startTopLeftPx: dragStartTopLeftPx,
            startScreenPx: new Point(1000, 1000),
            currentScreenPx: new Point(1030, 1015), // +30/+15 physical-pixel delta
            model);

        Assert.Equal(startDesktopDip.X + (30 / 1.5), result.X, 9);
        Assert.Equal(startDesktopDip.Y + (15 / 1.5), result.Y, 9);
    }

    private static MonitorScale[] Mixed100Plus150Right() =>
    [
        new(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 1d),
        new(Display2, new Rectangle(1920, 0, 3840, 2160), new Rectangle(1920, 0, 3840, 2100), false, 1.5d),
    ];

    /// <summary>
    /// A monitor entirely to the LEFT of a 100% primary at the origin, itself at 150% scale — the anchor
    /// rule (<see cref="DesktopScaleModel"/>'s remarks) keeps its RIGHT edge fixed (<c>dipRight = R /
    /// s_ref = 0</c>) and derives <c>dipLeft = dipRight - width/scale = 0 - 1920/1.5 = -1280</c>): its DIP
    /// rectangle is <c>[-1280, 0) x [0, 1080)</c> even though its physical bounds are <c>[-1920, 0) x [0,
    /// 1080)</c> — exactly the "virtual-screen origin != desktop-DIP origin" case H2 fixes.
    /// </summary>
    private static MonitorScale[] MonitorLeftOfPrimary() =>
    [
        new(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 1d),
        new(Display2, new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1080), false, 1.5d),
    ];
}
