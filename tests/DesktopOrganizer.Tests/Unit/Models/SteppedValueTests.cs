using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="SteppedValue"/> (docs/PLANS/color-presets-and-size-step.md §5.1) — pins the
/// exact worked tables the plan specifies for <see cref="SteppedValue.Next"/> and
/// <see cref="SteppedValue.SnapForDrag"/>, plus the argument-validation and non-finite-input contracts
/// from §3.2.
/// </summary>
[Trait(UnitTests, "UI")]
public class SteppedValueTests
{
    // ----- Next: icon-size range (16..128, step 8) -----

    /// <summary>Verifies <see cref="SteppedValue.Next"/> against the icon-size worked table (§5.1).</summary>
    /// <param name="current">The current (possibly fractional or out-of-range) value.</param>
    /// <param name="direction">Which way to step.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData(34, StepDirection.Up, 40)]
    [InlineData(34, StepDirection.Down, 32)]
    [InlineData(40, StepDirection.Up, 48)]
    [InlineData(40, StepDirection.Down, 32)]
    [InlineData(32.0101, StepDirection.Up, 40)]
    [InlineData(32.0101, StepDirection.Down, 32)]
    [InlineData(31.9999999, StepDirection.Up, 40)] // within Tolerance of 32 -> treated as 32, then Up -> 40
    [InlineData(47.5, StepDirection.Up, 48)]
    [InlineData(47.5, StepDirection.Down, 40)]
    [InlineData(124, StepDirection.Up, 128)]
    [InlineData(128, StepDirection.Up, 128)]
    [InlineData(200, StepDirection.Down, 120)] // out of range, clamped to 128 first, then stepped down
    public void Next_IconSizeRange_MatchesWorkedTable(double current, StepDirection direction, int expected)
    {
        Assert.Equal(expected, SteppedValue.Next(current, 8, direction, 16, 128));
    }

    /// <summary>Verifies stepping Down from the minimum stays at the minimum.</summary>
    [Fact]
    public void Next_16_Down_IconSizeRange_ReturnsMinimum()
    {
        Assert.Equal(16, SteppedValue.Next(16, 8, StepDirection.Down, 16, 128));
    }

    // ----- Next: grid-step range (2..128, step 8) -----

    /// <summary>Verifies <see cref="SteppedValue.Next"/> against the grid-step worked table (§5.1), where the minimum (2) is not itself a step multiple.</summary>
    /// <param name="current">The current value.</param>
    /// <param name="direction">Which way to step.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData(2, StepDirection.Up, 8)]
    [InlineData(8, StepDirection.Down, 2)]
    [InlineData(5, StepDirection.Down, 2)]
    [InlineData(5, StepDirection.Up, 8)]
    public void Next_GridStepRange_MatchesWorkedTable(double current, StepDirection direction, int expected)
    {
        Assert.Equal(expected, SteppedValue.Next(current, 8, direction, 2, 128));
    }

    /// <summary>Verifies a <c>NaN</c> current value is treated as the minimum (§3.2).</summary>
    [Fact]
    public void Next_NaN_Up_GridStepRange_TreatsCurrentAsMinimum()
    {
        Assert.Equal(8, SteppedValue.Next(double.NaN, 8, StepDirection.Up, 2, 128));
    }

    /// <summary>Verifies non-finite <c>current</c> values never throw and are treated as the minimum (§3.2).</summary>
    /// <param name="current">A non-finite value: <c>NaN</c> or ±<c>Infinity</c>.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Next_NonFiniteCurrent_NeverThrows_AndTreatsAsMinimum(double current)
    {
        var exception = Record.Exception(() => SteppedValue.Next(current, 8, StepDirection.Down, 16, 128));

        Assert.Null(exception);
        Assert.Equal(16, SteppedValue.Next(current, 8, StepDirection.Down, 16, 128));
    }

    /// <summary>Verifies a step less than 1 throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    /// <param name="step">An invalid (non-positive) step.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-8)]
    public void Next_StepLessThan1_Throws(int step)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SteppedValue.Next(34, step, StepDirection.Up, 16, 128));
    }

    /// <summary>Verifies a minimum greater than the maximum throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    [Fact]
    public void Next_MinimumGreaterThanMaximum_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SteppedValue.Next(34, 8, StepDirection.Up, 128, 16));
    }

    /// <summary>
    /// Property check across the full icon-size range: stepping Up never decreases the value, and
    /// stepping Down never increases it — regardless of whether <c>current</c> sits exactly on a step
    /// multiple.
    /// </summary>
    [Fact]
    public void Next_Up_NeverDecreases_AndDown_NeverIncreases_AcrossRange()
    {
        for (var current = 16d; current <= 128d; current += 0.5)
        {
            var up = SteppedValue.Next(current, 8, StepDirection.Up, 16, 128);
            var down = SteppedValue.Next(current, 8, StepDirection.Down, 16, 128);

            Assert.True(up >= current - SteppedValue.Tolerance, $"Up({current}) = {up} decreased.");
            Assert.True(down <= current + SteppedValue.Tolerance, $"Down({current}) = {down} increased.");
        }
    }

    // ----- SnapForDrag -----

    /// <summary>Verifies <see cref="SteppedValue.SnapForDrag"/> against the icon-size worked table (§5.1).</summary>
    /// <param name="raw">The raw, unsnapped pointer-derived value.</param>
    /// <param name="dragStart">The value the drag started from.</param>
    /// <param name="expected">The expected snapped result.</param>
    [Theory]
    [InlineData(34.2, 34, 40)]
    [InlineData(33.8, 34, 32)]
    [InlineData(60, 34, 64)]
    [InlineData(44.1, 34, 48)]
    [InlineData(127, 120, 128)]
    public void SnapForDrag_IconSizeRange_MatchesWorkedTable(double raw, double dragStart, int expected)
    {
        Assert.Equal(expected, SteppedValue.SnapForDrag(raw, dragStart, 8, 16, 128));
    }

    /// <summary>Verifies a raw value that rounds below the minimum clamps to the minimum.</summary>
    [Fact]
    public void SnapForDrag_GridStepRange_RoundsToZero_ClampsToMinimum()
    {
        Assert.Equal(2, SteppedValue.SnapForDrag(3, 8, 8, 2, 128));
    }

    /// <summary>Verifies no meaningful movement (raw ≈ dragStart) returns the rounded drag start, not a stepped value.</summary>
    [Fact]
    public void SnapForDrag_NoMeaningfulMovement_ReturnsRoundedDragStart()
    {
        Assert.Equal(32, SteppedValue.SnapForDrag(32.0101, 32.0101, 8, 16, 128));
    }

    /// <summary>Verifies a step less than 1 throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    [Fact]
    public void SnapForDrag_StepLessThan1_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SteppedValue.SnapForDrag(34, 34, 0, 16, 128));
    }

    /// <summary>Verifies a minimum greater than the maximum throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    [Fact]
    public void SnapForDrag_MinimumGreaterThanMaximum_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SteppedValue.SnapForDrag(34, 34, 8, 128, 16));
    }

    /// <summary>Verifies non-finite <c>raw</c>/<c>dragStart</c> values never throw (§3.2).</summary>
    /// <param name="nonFinite">A non-finite value: <c>NaN</c> or +<c>Infinity</c>.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SnapForDrag_NonFiniteRawOrDragStart_NeverThrows(double nonFinite)
    {
        var exception1 = Record.Exception(() => SteppedValue.SnapForDrag(nonFinite, 34, 8, 16, 128));
        var exception2 = Record.Exception(() => SteppedValue.SnapForDrag(34, nonFinite, 8, 16, 128));

        Assert.Null(exception1);
        Assert.Null(exception2);
    }

    // ----- RoundAndClamp -----

    /// <summary>Verifies <see cref="SteppedValue.RoundAndClamp"/> against the worked table (§5.1): ties away from zero, then clamp.</summary>
    /// <param name="value">The value to round and clamp.</param>
    /// <param name="expected">The expected result.</param>
    [Theory]
    [InlineData(47.5, 48)]
    [InlineData(200, 128)]
    [InlineData(-5, 16)]
    public void RoundAndClamp_IconSizeRange_MatchesWorkedTable(double value, int expected)
    {
        Assert.Equal(expected, SteppedValue.RoundAndClamp(value, 16, 128));
    }

    /// <summary>Verifies non-finite input maps to the minimum (§3.2).</summary>
    /// <param name="value">A non-finite value: <c>NaN</c> or ±<c>Infinity</c>.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RoundAndClamp_NonFinite_ReturnsMinimum(double value)
    {
        Assert.Equal(16, SteppedValue.RoundAndClamp(value, 16, 128));
    }

    /// <summary>Verifies a minimum greater than the maximum throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    [Fact]
    public void RoundAndClamp_MinimumGreaterThanMaximum_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SteppedValue.RoundAndClamp(34, 128, 16));
    }
}
