using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopOrganizer.Tests.Helpers;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// Focused unit tests for <see cref="SteppedSlider"/>'s direction logic (docs/PLANS/color-presets-and-size-step.md
/// §5.2, WP-B) — every keyboard/Home/End path routes through <see cref="SteppedValue"/> (already exhaustively
/// covered by <c>SteppedValueTests</c>), so this class only pins that <see cref="SteppedSlider"/> itself calls
/// the right <see cref="SteppedValue"/> direction for the right WPF input mechanism, and that its constructor
/// wires the fixed <see cref="System.Windows.Controls.Primitives.RangeBase.SmallChange"/>/
/// <see cref="System.Windows.Controls.Primitives.RangeBase.LargeChange"/>/<see cref="System.Windows.Controls.Slider.TickFrequency"/>/
/// <see cref="System.Windows.Controls.Slider.IsSnapToTickEnabled"/> values §5.2 specifies. Drag/wheel/track-part
/// coverage is WP-C's (a separate package, per the plan) — this file only exercises the protected
/// <c>On*</c> overrides a real <see cref="System.Windows.Controls.Primitives.Track"/>/<see cref="System.Windows.Input.Keyboard"/>
/// gesture would otherwise call, via a same-assembly-independent subclass that exposes them (STA-agnostic:
/// none of these overrides touch layout/rendering, but every test still runs on an STA thread — the
/// project-wide convention for any live <see cref="System.Windows.DependencyObject"/> instance).
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class SteppedSliderTests
{
    /// <summary>Verifies the constructor wires the fixed, non-configurable stepping metadata §5.2 specifies.</summary>
    [Fact]
    public void Constructor_WiresFixedSteppingMetadata()
    {
        StaTestRunner.Run(() =>
        {
            var slider = new TestableSteppedSlider();

            Assert.False(slider.IsSnapToTickEnabled);
            Assert.Equal(SizeStepPolicy.Step, slider.Step);
            Assert.Equal(SizeStepPolicy.Step, slider.TickFrequency);
            Assert.Equal((double)SizeStepPolicy.Step, slider.SmallChange);
            Assert.Equal((double)SizeStepPolicy.Step, slider.LargeChange);
        });
    }

    /// <summary>Icon-size range: 34 up (small or large — both are "one step toward the click/key") lands on 40.</summary>
    /// <param name="start">The slider's starting value.</param>
    /// <param name="small">Invoke the "small" (keyboard arrow) override when <see langword="true"/>, the "large" (PageUp/track-click) override otherwise.</param>
    [Theory]
    [InlineData(34.0, true)]
    [InlineData(34.0, false)]
    public void IncreaseFromThirtyFour_LandsOnForty(double start, bool small)
    {
        StaTestRunner.Run(() =>
        {
            var slider = BuildIconSizeSlider(start);

            if (small) slider.InvokeIncreaseSmall();
            else slider.InvokeIncreaseLarge();

            Assert.Equal(40, slider.Value);
        });
    }

    /// <summary>Icon-size range: 34 down (small or large) lands on 32.</summary>
    /// <param name="start">The slider's starting value.</param>
    /// <param name="small">Invoke the "small" (keyboard arrow) override when <see langword="true"/>, the "large" (PageDown/track-click) override otherwise.</param>
    [Theory]
    [InlineData(34.0, true)]
    [InlineData(34.0, false)]
    public void DecreaseFromThirtyFour_LandsOnThirtyTwo(double start, bool small)
    {
        StaTestRunner.Run(() =>
        {
            var slider = BuildIconSizeSlider(start);

            if (small) slider.InvokeDecreaseSmall();
            else slider.InvokeDecreaseLarge();

            Assert.Equal(32, slider.Value);
        });
    }

    /// <summary>At the maximum (128), increasing further stays clamped at 128.</summary>
    [Fact]
    public void IncreaseAtMaximum_StaysAtMaximum()
    {
        StaTestRunner.Run(() =>
        {
            var slider = BuildIconSizeSlider(SizeStepPolicy.IconSizeMaximum);

            slider.InvokeIncreaseSmall();

            Assert.Equal(SizeStepPolicy.IconSizeMaximum, slider.Value);
        });
    }

    /// <summary><see cref="SteppedSlider"/>'s Home/End map to <see cref="RangeBase.Minimum"/>/<see cref="RangeBase.Maximum"/> exactly, not a step.</summary>
    [Fact]
    public void MinimizeAndMaximizeValue_JumpToMinimumAndMaximum()
    {
        StaTestRunner.Run(() =>
        {
            var slider = BuildIconSizeSlider(48);

            slider.InvokeMinimizeValue();
            Assert.Equal(SizeStepPolicy.IconSizeMinimum, slider.Value);

            slider.InvokeMaximizeValue();
            Assert.Equal(SizeStepPolicy.IconSizeMaximum, slider.Value);
        });
    }

    /// <summary>Grid-step range (min 2, not a multiple of Step 8): 2 up lands on 8, 8 down lands back on 2.</summary>
    [Fact]
    public void GridStepRange_TwoUpToEightAndBackDown()
    {
        StaTestRunner.Run(() =>
        {
            var slider = new TestableSteppedSlider
            {
                Minimum = SizeStepPolicy.GridStepMinimum,
                Maximum = SizeStepPolicy.GridStepMaximum,
                Value = SizeStepPolicy.GridStepMinimum,
            };

            slider.InvokeIncreaseSmall();
            Assert.Equal(8, slider.Value);

            slider.InvokeDecreaseSmall();
            Assert.Equal(SizeStepPolicy.GridStepMinimum, slider.Value);
        });
    }

    private static TestableSteppedSlider BuildIconSizeSlider(double value) => new()
    {
        Minimum = SizeStepPolicy.IconSizeMinimum,
        Maximum = SizeStepPolicy.IconSizeMaximum,
        Value = value,
    };

    /// <summary>
    /// Exposes <see cref="SteppedSlider"/>'s protected step-decision overrides as public methods a
    /// same-assembly-independent test can call directly, without needing a real
    /// <see cref="System.Windows.Controls.Primitives.Track"/>/<see cref="System.Windows.Input.Keyboard"/>
    /// gesture to trigger them.
    /// </summary>
    private sealed class TestableSteppedSlider : SteppedSlider
    {
        public void InvokeIncreaseSmall() => OnIncreaseSmall();

        public void InvokeDecreaseSmall() => OnDecreaseSmall();

        public void InvokeIncreaseLarge() => OnIncreaseLarge();

        public void InvokeDecreaseLarge() => OnDecreaseLarge();

        public void InvokeMinimizeValue() => OnMinimizeValue();

        public void InvokeMaximizeValue() => OnMaximizeValue();
    }
}
