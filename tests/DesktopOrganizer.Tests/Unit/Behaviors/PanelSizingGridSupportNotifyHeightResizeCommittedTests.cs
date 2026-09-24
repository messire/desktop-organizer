using DesktopOrganizer.UI.Behaviors;
using DesktopOrganizer.UI.Interaction;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Behaviors;

/// <summary>
/// Unit tests for <see cref="PanelSizingGridSupport.NotifyHeightResizeCommitted"/> (P06 fix-cycle
/// MAJOR-4, adr/ADR-029-icon-layout-policy.md "Fix-цикл P06") — the single shared helper both
/// <see cref="PanelResizeController"/> (Thumb path) and <see cref="NativePanelResizeController"/>
/// (native <c>WM_SIZING</c> path) call at their respective end-of-gesture points, so neither controller
/// needs its own copy of the "was this a vertical resize" check.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class PanelSizingGridSupportNotifyHeightResizeCommittedTests
{
    [Theory]
    [InlineData(ResizeEdge.Top)]
    [InlineData(ResizeEdge.Bottom)]
    [InlineData(ResizeEdge.TopLeft)]
    [InlineData(ResizeEdge.TopRight)]
    [InlineData(ResizeEdge.BottomLeft)]
    [InlineData(ResizeEdge.BottomRight)]
    public void NotifyHeightResizeCommitted_HeightAffectingEdge_CallsHost(ResizeEdge edge)
    {
        var host = new Mock<IPanelSizingGridHost>();

        PanelSizingGridSupport.NotifyHeightResizeCommitted(host.Object, edge);

        host.Verify(h => h.NotifyHeightResizeCommitted(), Times.Once);
    }

    [Theory]
    [InlineData(ResizeEdge.Left)]
    [InlineData(ResizeEdge.Right)]
    public void NotifyHeightResizeCommitted_WidthOnlyEdge_NeverCallsHost(ResizeEdge edge)
    {
        var host = new Mock<IPanelSizingGridHost>();

        PanelSizingGridSupport.NotifyHeightResizeCommitted(host.Object, edge);

        host.Verify(h => h.NotifyHeightResizeCommitted(), Times.Never);
    }

    [Fact]
    public void NotifyHeightResizeCommitted_NullHost_NeverThrows()
    {
        var exception = Record.Exception(() => PanelSizingGridSupport.NotifyHeightResizeCommitted(null, ResizeEdge.Bottom));

        Assert.Null(exception);
    }
}
