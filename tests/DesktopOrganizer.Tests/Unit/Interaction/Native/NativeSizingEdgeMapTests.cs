using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Interaction;
using DesktopOrganizer.UI.Interaction.Native;

namespace DesktopOrganizer.Tests.Unit.Interaction.Native;

/// <summary>
/// Phase 2 (commit C2, docs/PLANS/phase-2-native-panel-sizing.md §1.4/§5, T-MAP-05) tests for
/// <see cref="NativeSizingEdgeMap"/> — <see cref="NativeSizingEdge"/> &lt;-&gt; <see cref="ResizeEdge"/>
/// and <c>WMSZ_*</c> (WinUser.h, 1..8) -&gt; <see cref="NativeSizingEdge"/>.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class NativeSizingEdgeMapTests
{
    /// <summary>Verifies that <see cref="NativeSizingEdgeMap.ToResizeEdge"/> maps each of the eight <see cref="NativeSizingEdge"/> values to its matching <see cref="ResizeEdge"/>.</summary>
    /// <param name="native">The native edge value to convert.</param>
    /// <param name="expected">The expected <see cref="ResizeEdge"/> result.</param>
    [Theory]
    [InlineData(NativeSizingEdge.Left, ResizeEdge.Left)]
    [InlineData(NativeSizingEdge.Top, ResizeEdge.Top)]
    [InlineData(NativeSizingEdge.Right, ResizeEdge.Right)]
    [InlineData(NativeSizingEdge.Bottom, ResizeEdge.Bottom)]
    [InlineData(NativeSizingEdge.TopLeft, ResizeEdge.TopLeft)]
    [InlineData(NativeSizingEdge.TopRight, ResizeEdge.TopRight)]
    [InlineData(NativeSizingEdge.BottomLeft, ResizeEdge.BottomLeft)]
    [InlineData(NativeSizingEdge.BottomRight, ResizeEdge.BottomRight)]
    public void ToResizeEdge_AllEightEdges_MapsToMatchingResizeEdge(NativeSizingEdge native, ResizeEdge expected)
    {
        Assert.Equal(expected, NativeSizingEdgeMap.ToResizeEdge(native));
    }

    /// <summary>Verifies that <see cref="NativeSizingEdgeMap.ToResizeEdge"/> maps <see cref="NativeSizingEdge.None"/> to <see langword="null"/>.</summary>
    [Fact]
    public void ToResizeEdge_None_ReturnsNull()
    {
        Assert.Null(NativeSizingEdgeMap.ToResizeEdge(NativeSizingEdge.None));
    }

    /// <summary>Verifies that <see cref="NativeSizingEdgeMap.FromResizeEdge"/> maps each of the eight <see cref="ResizeEdge"/> values to its matching <see cref="NativeSizingEdge"/>.</summary>
    /// <param name="edge">The <see cref="ResizeEdge"/> value to convert.</param>
    /// <param name="expected">The expected <see cref="NativeSizingEdge"/> result.</param>
    [Theory]
    [InlineData(ResizeEdge.Left, NativeSizingEdge.Left)]
    [InlineData(ResizeEdge.Top, NativeSizingEdge.Top)]
    [InlineData(ResizeEdge.Right, NativeSizingEdge.Right)]
    [InlineData(ResizeEdge.Bottom, NativeSizingEdge.Bottom)]
    [InlineData(ResizeEdge.TopLeft, NativeSizingEdge.TopLeft)]
    [InlineData(ResizeEdge.TopRight, NativeSizingEdge.TopRight)]
    [InlineData(ResizeEdge.BottomLeft, NativeSizingEdge.BottomLeft)]
    [InlineData(ResizeEdge.BottomRight, NativeSizingEdge.BottomRight)]
    public void FromResizeEdge_AllEightEdges_MapsToMatchingNativeSizingEdge(ResizeEdge edge, NativeSizingEdge expected)
    {
        Assert.Equal(expected, NativeSizingEdgeMap.FromResizeEdge(edge));
    }

    /// <summary>Verifies that <see cref="NativeSizingEdgeMap.FromWmsz"/> maps each <c>WMSZ_*</c> constant (WinUser.h, 1..9) to its matching <see cref="NativeSizingEdge"/>.</summary>
    /// <param name="wmsz">The raw <c>WMSZ_*</c> value.</param>
    /// <param name="expected">The expected <see cref="NativeSizingEdge"/> result.</param>
    [Theory]
    [InlineData(1, NativeSizingEdge.Left)]
    [InlineData(2, NativeSizingEdge.Right)]
    [InlineData(3, NativeSizingEdge.Top)]
    [InlineData(4, NativeSizingEdge.TopLeft)]
    [InlineData(5, NativeSizingEdge.TopRight)]
    [InlineData(6, NativeSizingEdge.Bottom)]
    [InlineData(7, NativeSizingEdge.BottomLeft)]
    [InlineData(8, NativeSizingEdge.BottomRight)]
    [InlineData(9, NativeSizingEdge.None)]
    public void FromWmsz_NineValues_MapsAccordingToWinUserHConstants(int wmsz, NativeSizingEdge expected)
    {
        Assert.Equal(expected, NativeSizingEdgeMap.FromWmsz(wmsz));
    }

    /// <summary>Verifies that <see cref="NativeSizingEdgeMap.FromWmsz"/> returns <see cref="NativeSizingEdge.None"/> for out-of-range <paramref name="wmsz"/> values.</summary>
    /// <param name="wmsz">The out-of-range raw <c>WMSZ_*</c> value.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void FromWmsz_OutOfRangeValues_ReturnNone(int wmsz)
    {
        Assert.Equal(NativeSizingEdge.None, NativeSizingEdgeMap.FromWmsz(wmsz));
    }
}
