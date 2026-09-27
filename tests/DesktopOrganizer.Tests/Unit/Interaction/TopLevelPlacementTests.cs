using System.Windows;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction;
using Rectangle = System.Drawing.Rectangle;

namespace DesktopOrganizer.Tests.Unit.Interaction;

/// <summary>
/// Unit tests for release-hardening F4 (docs/PLANS/release-hardening-1.0-F-dpi.md §7
/// "TopLevelPlacementTests", ADR-037): <see cref="TopLevelPlacement"/> converts a physical-pixel rectangle
/// (the sidebar/"Customize panel…" dialog's own monitor rectangle) into DIP by that monitor's OWN effective
/// scale (<see cref="DesktopScaleModel.GetTopLevelScale"/>) — always per-monitor, in both desktop layer
/// modes (plan §2 "Window.Left/Top top-level окна").
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class TopLevelPlacementTests
{
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";

    /// <summary>
    /// Verifies that <see cref="TopLevelPlacement.ToDip"/> divides by the NAMED monitor's own effective
    /// scale, not the reference/primary scale — a top-level window on the 150% secondary is converted at
    /// 150%, even though the snapshot's reference scale is 100%.
    /// </summary>
    [Fact]
    public void ToDip_NamedSecondaryMonitor_DividesByThatMonitorsOwnScale()
    {
        var scale = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        var dip = TopLevelPlacement.ToDip(new Rect(2400, 150, 300, 150), Display2, scale);

        Assert.Equal(new Rect(1600, 100, 200, 100), dip);
    }

    /// <summary>
    /// Verifies that <see cref="TopLevelPlacement.ToDip"/> on the primary monitor divides by its own (100%)
    /// scale, unaffected by the secondary monitor's different scale.
    /// </summary>
    [Fact]
    public void ToDip_NamedPrimaryMonitor_DividesByThatMonitorsOwnScale()
    {
        var scale = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        var dip = TopLevelPlacement.ToDip(new Rect(100, 50, 300, 150), Display1, scale);

        Assert.Equal(new Rect(100, 50, 300, 150), dip);
    }

    /// <summary>
    /// Verifies that <see cref="TopLevelPlacement.ToDip"/> falls back to
    /// <see cref="DesktopScaleModel.ReferenceScale"/> — the pre-F, single-scale behavior — when
    /// <paramref name="deviceName"/> is <see langword="null"/> or empty.
    /// </summary>
    /// <param name="deviceName">The null/empty device name under test.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToDip_NullOrEmptyDeviceName_FallsBackToReferenceScale(string? deviceName)
    {
        var scale = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1.5d, 1.5d, []);

        var dip = TopLevelPlacement.ToDip(new Rect(300, 300, 600, 600), deviceName, scale);

        Assert.Equal(new Rect(200, 200, 400, 400), dip);
    }

    /// <summary>
    /// Verifies that <see cref="TopLevelPlacement.ToDip"/> falls back to
    /// <see cref="DesktopScaleModel.ReferenceScale"/> for an unrecognized device name (e.g. a monitor that
    /// was disconnected between enumeration and use).
    /// </summary>
    [Fact]
    public void ToDip_UnknownDeviceName_FallsBackToReferenceScale()
    {
        var scale = DesktopScaleModel.Create(DesktopLayerMode.TopLevel, 1d, 1d, Mixed100Plus150Right());

        var dip = TopLevelPlacement.ToDip(new Rect(100, 100, 200, 200), @"\\.\DISPLAY9", scale);

        Assert.Equal(new Rect(100, 100, 200, 200), dip);
    }

    /// <summary>
    /// Verifies that <see cref="TopLevelPlacement.ToDip"/> in Embedded mode still divides by the named
    /// monitor's own effective scale — <see cref="DesktopScaleModel.GetTopLevelScale"/> is always
    /// per-monitor, in every desktop layer mode (plan §2).
    /// </summary>
    [Fact]
    public void ToDip_EmbeddedMode_StillUsesTheNamedMonitorsOwnScale()
    {
        var scale = DesktopScaleModel.Create(DesktopLayerMode.Embedded, 1d, 1d, Mixed100Plus150Right());

        var dip = TopLevelPlacement.ToDip(new Rect(2400, 150, 300, 150), Display2, scale);

        Assert.Equal(new Rect(1600, 100, 200, 100), dip);
    }

    private static MonitorScale[] Mixed100Plus150Right() =>
    [
        new(Display1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, 1d),
        new(Display2, new Rectangle(1920, 0, 3840, 2160), new Rectangle(1920, 0, 3840, 2100), false, 1.5d),
    ];
}
