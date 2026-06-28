using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="GridMetrics"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class GridMetricsTests
{
    // ----- BoxSize -----

    /// <summary>
    /// BoxSize(48) == 72 (StackPanel width for 48-px icons, i.e. icon × 1.5).
    /// </summary>
    [Fact]
    public void BoxSize_48_Returns72()
    {
        Assert.Equal(72d, GridMetrics.BoxSize(48));
    }

    /// <summary>
    /// BoxSize(32) == 48.
    /// </summary>
    [Fact]
    public void BoxSize_32_Returns48()
    {
        Assert.Equal(48d, GridMetrics.BoxSize(32));
    }

    // ----- CellSize -----

    /// <summary>
    /// CellSize(48) == 84 (BoxSize 72 + ItemMargin 6 × 2).
    /// This is the full column footprint used for wrap-mode width quantisation.
    /// </summary>
    [Fact]
    public void CellSize_48_Returns84()
    {
        Assert.Equal(84d, GridMetrics.CellSize(48));
    }

    /// <summary>
    /// CellSize(32) == 60 (BoxSize 48 + 12).
    /// </summary>
    [Fact]
    public void CellSize_32_Returns60()
    {
        Assert.Equal(60d, GridMetrics.CellSize(32));
    }

    /// <summary>
    /// CellSize(0) must not throw.
    /// </summary>
    [Fact]
    public void CellSize_Zero_DoesNotThrow()
    {
        var result = GridMetrics.CellSize(0);
        Assert.Equal(GridMetrics.ItemMargin * 2d, result);
    }

    // ----- Quantize -----

    /// <summary>
    /// 100 snaps down to 84 (nearest multiple of 84).
    /// </summary>
    [Fact]
    public void Quantize_100_Cell84_Returns84()
    {
        Assert.Equal(84d, GridMetrics.Quantize(100, 84));
    }

    /// <summary>
    /// 130 snaps up to 168 (84 * 1.5 + 1 — past mid-point).
    /// </summary>
    [Fact]
    public void Quantize_130_Cell84_Returns168()
    {
        Assert.Equal(168d, GridMetrics.Quantize(130, 84));
    }

    /// <summary>
    /// 84 is already on a boundary — stays 84.
    /// </summary>
    [Fact]
    public void Quantize_84_Cell84_Returns84()
    {
        Assert.Equal(84d, GridMetrics.Quantize(84, 84));
    }

    /// <summary>
    /// 0 with cell 84: implementation enforces minimum of one cell, so result is 84.
    /// </summary>
    [Fact]
    public void Quantize_Zero_Cell84_ReturnsOneCell()
    {
        Assert.Equal(84d, GridMetrics.Quantize(0, 84));
    }

    /// <summary>
    /// 42 is exactly half a cell (84). Math.Round uses banker's rounding (round-half-to-even),
    /// so Math.Round(0.5) == 0 → result is Math.Max(84, 0) == 84.
    /// </summary>
    [Fact]
    public void Quantize_42_Cell84_Returns84_BankersRounding()
    {
        Assert.Equal(84d, GridMetrics.Quantize(42, 84));
    }
}
