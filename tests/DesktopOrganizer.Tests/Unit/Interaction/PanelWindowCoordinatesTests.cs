using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Unit tests for release-hardening F4 (docs/PLANS/release-hardening-1.0-F-dpi.md §7
/// "PanelWindowCoordinatesTests", ADR-037): <see cref="PanelWindowCoordinates"/> is the single px↔DIP
/// boundary between a live fence panel <see cref="Window"/> and desktop-DIP. Embedded mode is identity
/// regardless of the window's own (live-queried, environment-bound — see
/// <see cref="DesktopOrganizer.Tests.Unit.Behaviors.PersistedGeometryInvarianceTests"/>'s remarks) DPI, so
/// it is exercised at several scales here; TopLevel is exercised at this test host's own real window scale
/// (self-consistent, <c>s_w == s</c>).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelWindowCoordinatesTests
{
    /// <summary>Representative Embedded-mode scales (identity holds regardless — see this class's remarks).</summary>
    public static TheoryData<double> Scales => new() { 1d, 1.25d, 1.5d, 2d };

    // ── ReadDesktopBounds ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.ReadDesktopBounds"/> returns the window's raw
    /// <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> verbatim in Embedded mode, at every scale.
    /// </summary>
    /// <param name="scale">The uniform DPI scale under test.</param>
    [Theory]
    [MemberData(nameof(Scales))]
    public void ReadDesktopBounds_EmbeddedMode_ReturnsRawWindowBounds(double scale)
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window { Left = 203, Top = -147, Width = 320, Height = 240 };

            var bounds = PanelWindowCoordinates.ReadDesktopBounds(window, EmbeddedScale(scale));

            Assert.Equal(new Rect(203, -147, 320, 240), bounds);
        });
    }

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.ReadDesktopBounds"/> treats a never-assigned
    /// (<c>NaN</c>) <c>Width</c>/<c>Height</c> as <c>0</c> rather than propagating <c>NaN</c>, while
    /// leaving <c>Left</c>/<c>Top</c> untouched.
    /// </summary>
    [Fact]
    public void ReadDesktopBounds_NeverSizedWindow_TreatsNaNWidthHeightAsZero()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window { Left = 10, Top = 20 };

            var bounds = PanelWindowCoordinates.ReadDesktopBounds(window, EmbeddedScale(1d));

            Assert.Equal(new Rect(10, 20, 0, 0), bounds);
        });
    }

    // ── ToDesktopBounds ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.ToDesktopBounds"/> converts a caller-supplied
    /// window-space rectangle (not necessarily the window's own live bounds — e.g. a rolled-up panel's
    /// collapsed header) verbatim in Embedded mode.
    /// </summary>
    [Fact]
    public void ToDesktopBounds_EmbeddedMode_ConvertsCallerSuppliedRectangleVerbatim()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();

            var bounds = PanelWindowCoordinates.ToDesktopBounds(window, new Rect(5, 15, 100, 32), EmbeddedScale(1.5d));

            Assert.Equal(new Rect(5, 15, 100, 32), bounds);
        });
    }

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.ToDesktopBounds"/> on a TopLevel uniform snapshot
    /// built at exactly this test host's own real window DPI (self-consistent — see this class's remarks)
    /// converts a window-space rectangle to desktop-DIP through the model, matching
    /// <see cref="DesktopScaleModel.WindowToDesktop"/> called directly with the same scale.
    /// </summary>
    [Fact]
    public void ToDesktopBounds_TopLevelMode_AtThisEnvironmentsOwnRealScale_MatchesTheModelDirectly()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();
            var scale = LiveEnvironmentScale(window);
            var model = UniformScale(DesktopLayerMode.TopLevel, scale);
            var rect = new Rect(40, 60, 100, 50);

            var viaAdapter = PanelWindowCoordinates.ToDesktopBounds(window, rect, model);
            var viaModelDirectly = model.WindowToDesktop(rect, scale);

            Assert.Equal(viaModelDirectly, viaAdapter);
        });
    }

    // ── WriteDesktopBounds / WriteDesktopPosition ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.WriteDesktopBounds"/> writes
    /// <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> onto the window verbatim in Embedded mode.
    /// </summary>
    [Fact]
    public void WriteDesktopBounds_EmbeddedMode_WritesLeftTopWidthHeightVerbatim()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();

            PanelWindowCoordinates.WriteDesktopBounds(window, new Rect(11, 22, 333, 444), EmbeddedScale(1.25d));

            Assert.Equal(11d, window.Left);
            Assert.Equal(22d, window.Top);
            Assert.Equal(333d, window.Width);
            Assert.Equal(444d, window.Height);
        });
    }

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.WriteDesktopPosition"/> writes only
    /// <c>Left</c>/<c>Top</c>, leaving <c>Width</c>/<c>Height</c> untouched.
    /// </summary>
    [Fact]
    public void WriteDesktopPosition_EmbeddedMode_WritesOnlyLeftTop_LeavesSizeUntouched()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window { Width = 300, Height = 200 };

            PanelWindowCoordinates.WriteDesktopPosition(window, new Point(50, 60), EmbeddedScale(1.5d));

            Assert.Equal(50d, window.Left);
            Assert.Equal(60d, window.Top);
            Assert.Equal(300d, window.Width);
            Assert.Equal(200d, window.Height);
        });
    }

    // ── ToWindowPosition ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.ToWindowPosition"/> computes the target
    /// <c>Left</c>/<c>Top</c> without writing it onto the window.
    /// </summary>
    [Fact]
    public void ToWindowPosition_EmbeddedMode_ReturnsThePointWithoutWriting()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window { Left = 999, Top = 999 };

            var position = PanelWindowCoordinates.ToWindowPosition(window, new Point(12, 34), EmbeddedScale(2d));

            Assert.Equal(new Point(12, 34), position);
            Assert.Equal(999d, window.Left);
            Assert.Equal(999d, window.Top);
        });
    }

    // ── GetWindowScale ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="PanelWindowCoordinates.GetWindowScale"/> never throws and always returns a
    /// positive, finite value for a window with no <see cref="PresentationSource"/> yet.
    /// </summary>
    [Fact]
    public void GetWindowScale_WindowWithoutPresentationSource_ReturnsAPositiveFiniteValue()
    {
        StaTestRunner.Run(() =>
        {
            var window = new Window();

            var scale = PanelWindowCoordinates.GetWindowScale(window, EmbeddedScale(1.5d));

            Assert.True(scale > 0 && double.IsFinite(scale));
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static DesktopScaleModel EmbeddedScale(double scale) => UniformScale(DesktopLayerMode.Embedded, scale);

    private static DesktopScaleModel UniformScale(DesktopLayerMode mode, double scale)
        => DesktopScaleModel.Create(mode, scale, scale, []);

    /// <summary>This test host's own real, live-queried window DPI scale (see this class's remarks).</summary>
    private static double LiveEnvironmentScale(Window window)
        => PanelWindowCoordinates.GetWindowScale(window, DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, []));
}
