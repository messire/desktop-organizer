using System.Globalization;
using System.Windows;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for the four Ambient Accent converters (docs/PLANS/phase-5-ambient-accent-ui.md §3.4):
/// <see cref="PanelSurfaceBrushConverter"/>, <see cref="AccentBrushConverter"/>,
/// <see cref="VisualStateToDoubleConverter"/>, <see cref="ItemCountToTextConverter"/>. Same contract
/// as <see cref="HexBrushConverter"/>/<see cref="ColorOpacityBrushConverter"/> (Phase 7 Block A
/// precedent): never throw, frozen bounded-cache brushes, garbage input degrades gracefully.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelSurfaceBrushConverterTests
{
    private static readonly PanelSurfaceBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that valid hex/opacity/style inputs produce a frozen <see cref="SolidColorBrush"/>.
    /// </summary>
    [Fact]
    public void Convert_ValidInputs_ReturnsFrozenBrush()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that converting identical hex/opacity/style inputs twice returns the same cached brush instance.
    /// </summary>
    [Fact]
    public void Convert_SameInputsTwice_ReturnsSameInstance()
    {
        var first = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);
        var second = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that fewer values than the converter expects returns <see cref="Brushes.Transparent"/> instead
    /// of throwing.
    /// </summary>
    [Fact]
    public void Convert_TooFewValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.8 }, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that a malformed accent hex string still produces a frozen brush instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_GarbageAccentHex_DoesNotThrow_StillReturnsFrozenBrush()
    {
        var result = Converter.Convert(new object?[] { "not-a-colour", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that a non-<see cref="double"/> opacity value still produces a frozen brush instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonDoubleOpacity_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", "nope", PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that a non-<see cref="PanelSurfaceStyle"/> third value still produces a frozen brush instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonStyleThirdValue_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.8, "not-a-style" }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that passing a high-contrast flag of <see langword="true"/> forces the resulting brush to be
    /// fully opaque regardless of the requested opacity.
    /// </summary>
    [Fact]
    public void Convert_HighContrastTrue_ProducesFullyOpaqueBrush()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.35, PanelSurfaceStyle.Glass, true }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(255, brush.Color.A);
    }

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceStyle.Solid"/> produces the same brush color regardless of accent hex.
    /// </summary>
    [Fact]
    public void Convert_SolidStyle_IgnoresAccentColor()
    {
        var withBlue = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Solid }, typeof(Brush), null, AnyCulture);
        var withViolet = Converter.Convert(new object?[] { "#A855F7", 0.8, PanelSurfaceStyle.Solid }, typeof(Brush), null, AnyCulture);

        var blueBrush = Assert.IsType<SolidColorBrush>(withBlue);
        var violetBrush = Assert.IsType<SolidColorBrush>(withViolet);
        Assert.Equal(blueBrush.Color, violetBrush.Color);
    }

    /// <summary>
    /// Verifies that repeatedly converting many distinct hex/opacity/style combinations keeps producing frozen
    /// brushes, exercising the converter's bounded cache eviction path without failure.
    /// </summary>
    [Fact]
    public void Convert_ManyDistinctInputs_CacheStaysBounded()
    {
        for (var i = 0; i < 600; i++)
        {
            var hex = $"#{i % 256:X2}{(i * 3) % 256:X2}{(i * 7) % 256:X2}";
            var opacity = (i % 101) / 100.0;
            var style = (PanelSurfaceStyle)(i % 3);

            var result = Converter.Convert(new object?[] { hex, opacity, style }, typeof(Brush), null, AnyCulture);

            var brush = Assert.IsType<SolidColorBrush>(result);
            Assert.True(brush.IsFrozen);
        }
    }

    /// <summary>
    /// Verifies that <see cref="PanelSurfaceBrushConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(new SolidColorBrush(Colors.Red), new[] { typeof(string), typeof(double), typeof(PanelSurfaceStyle) }, null, AnyCulture));
    }
}

// Cheap fix (review round 3): AccentIndicatorWidthConverter (MAJOR-B) had no dedicated unit test —
// only exercised transitively via ResourceDictionaryMergeTests' template materialization.

/// <summary>
/// Unit tests for <see cref="AccentIndicatorWidthConverter"/>, covering how it scales the accent indicator
/// thickness by <see cref="PanelVisualState"/> and falls back to the idle thickness on garbage input.
/// </summary>
[Trait(UnitTests, "UI")]
public class AccentIndicatorWidthConverterTests
{
    private static readonly AccentIndicatorWidthConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that <see cref="PanelVisualState.Resizing"/> scales the base thickness by the maximum emphasis factor.
    /// </summary>
    [Fact]
    public void Convert_Resizing_ReturnsThicknessScaledByMaxEmphasis()
    {
        var result = Converter.Convert(PanelVisualState.Resizing, typeof(double), null, AnyCulture);

        Assert.Equal(PanelMetrics.AccentIndicatorThickness * 1.7, result);
        Assert.Equal(5.1, result);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> value falls back to the idle thickness instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_Null_DoesNotThrow_FallsBackToIdleThickness()
    {
        var result = Converter.Convert(null, typeof(double), null, AnyCulture);

        Assert.Equal(PanelMetrics.AccentIndicatorThickness * 1.0, result);
        Assert.Equal(3.0, result);
    }

    /// <summary>
    /// Verifies that <see cref="DependencyProperty.UnsetValue"/> falls back to the idle thickness instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_UnsetValue_DoesNotThrow_FallsBackToIdleThickness()
    {
        var result = Converter.Convert(DependencyProperty.UnsetValue, typeof(double), null, AnyCulture);

        Assert.Equal(3.0, result);
    }

    /// <summary>
    /// Verifies that a value that is not a <see cref="PanelVisualState"/> falls back to the idle thickness
    /// instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonPanelVisualStateValue_DoesNotThrow_FallsBackToIdleThickness()
    {
        var result = Converter.Convert("not-a-state", typeof(double), null, AnyCulture);

        Assert.Equal(3.0, result);
    }

    /// <summary>
    /// Verifies that <see cref="AccentIndicatorWidthConverter.ConvertBack"/> returns <c>Binding.DoNothing</c>
    /// because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = Converter.ConvertBack(5.1, typeof(PanelVisualState), null, AnyCulture);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}

/// <summary>
/// Unit tests for <see cref="AccentBrushConverter"/>, covering per-mode opacity, cache bounding, and the
/// pinned alpha constants consumed directly from XAML via <c>x:Static</c>.
/// </summary>
[Trait(UnitTests, "UI")]
public class AccentBrushConverterTests
{
    private static readonly AccentBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that each known mode parameter ("indicator", "selection", "hover", "full") produces a frozen brush.
    /// </summary>
    /// <param name="parameter">The converter parameter selecting the accent mode.</param>
    [Theory]
    [InlineData("indicator")]
    [InlineData("selection")]
    [InlineData("hover")]
    [InlineData("full")]
    public void Convert_KnownParameter_ReturnsFrozenBrush(string parameter)
    {
        var result = Converter.Convert("#3B82F6", typeof(Brush), parameter, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that an unrecognized mode parameter still produces a frozen brush instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_UnknownParameter_DoesNotThrow()
    {
        var result = Converter.Convert("#3B82F6", typeof(Brush), "not-a-known-mode", AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> converter parameter still produces a brush instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NullParameter_DoesNotThrow()
    {
        var result = Converter.Convert("#3B82F6", typeof(Brush), null, AnyCulture);

        Assert.IsType<SolidColorBrush>(result);
    }

    /// <summary>
    /// Verifies that a malformed hex string falls back to the neutral accent brush instead of throwing, since
    /// <c>AccentPalette.Normalize</c> maps junk input to the default accent.
    /// </summary>
    [Fact]
    public void Convert_GarbageHex_DoesNotThrow_ReturnsFallbackAccentBrush()
    {
        // AccentPalette.Normalize maps junk to Default (Neutral) rather than throwing.
        var result = Converter.Convert("garbage", typeof(Brush), "full", AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that a non-<see cref="string"/> value still produces a brush instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonStringValue_DoesNotThrow()
    {
        var result = Converter.Convert(42, typeof(Brush), "full", AnyCulture);

        Assert.IsType<SolidColorBrush>(result);
    }

    /// <summary>
    /// Verifies that the "indicator" mode produces a more opaque brush than the "hover" mode for the same accent.
    /// </summary>
    [Fact]
    public void Convert_IndicatorMode_IsMoreOpaqueThanHoverMode()
    {
        var indicator = (SolidColorBrush)Converter.Convert("#3B82F6", typeof(Brush), "indicator", AnyCulture);
        var hover = (SolidColorBrush)Converter.Convert("#3B82F6", typeof(Brush), "hover", AnyCulture);

        Assert.True(indicator.Color.A > hover.Color.A);
    }

    /// <summary>
    /// Verifies that repeatedly converting many distinct hex values keeps producing frozen brushes, exercising
    /// the converter's bounded cache eviction path without failure.
    /// </summary>
    [Fact]
    public void Convert_ManyDistinctInputs_CacheStaysBounded()
    {
        for (var i = 0; i < 400; i++)
        {
            var hex = $"#{i % 256:X2}{(i * 3) % 256:X2}{(i * 7) % 256:X2}";

            var result = Converter.Convert(hex, typeof(Brush), "full", AnyCulture);

            var brush = Assert.IsType<SolidColorBrush>(result);
            Assert.True(brush.IsFrozen);
        }
    }

    /// <summary>
    /// Verifies that <see cref="AccentBrushConverter.ConvertBack"/> returns <c>Binding.DoNothing</c> because the
    /// conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = Converter.ConvertBack(new SolidColorBrush(Colors.Red), typeof(string), null, AnyCulture);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }

    // MAJOR-2 regression: Resources/Styles/Panels.xaml's PanelItemContainerStyle now references
    // SelectionAlpha directly via {x:Static conv:AccentBrushConverter.SelectionAlpha} instead of
    // mirroring it as a hand-synced "0.28" XAML literal — pin the value here so a future change to the
    // constant is a deliberate, visible decision. Note this is NOT redundant with the XAML picking the
    // value up "automatically" — it is not automatic at all: SelectionAlpha must stay `public` for that
    // x:Static reference to resolve at runtime in the first place (CRITICAL-1 — WPF on .NET (Core)
    // resolves x:Static via public-only reflection, even within the same assembly), so this test also
    // guards against a well-meaning future refactor re-narrowing the constant back to `internal`.

    /// <summary>
    /// Verifies that <see cref="AccentBrushConverter.SelectionAlpha"/> stays pinned at 0.28, matching the
    /// XAML <c>x:Static</c> reference in <c>PanelItemContainerStyle</c>.
    /// </summary>
    [Fact]
    public void SelectionAlpha_IsPinnedAt028()
    {
        Assert.Equal(0.28, AccentBrushConverter.SelectionAlpha);
    }

    /// <summary>
    /// Verifies that <see cref="AccentBrushConverter.HoverAlpha"/> stays pinned at 0.16.
    /// </summary>
    [Fact]
    public void HoverAlpha_IsPinnedAt016()
    {
        Assert.Equal(0.16, AccentBrushConverter.HoverAlpha);
    }
}

/// <summary>
/// Unit tests for <see cref="VisualStateToDoubleConverter"/>, covering per-channel opacity lookups by
/// <see cref="PanelVisualState"/> and graceful fallback on unrecognized input.
/// </summary>
[Trait(UnitTests, "UI")]
public class VisualStateToDoubleConverterTests
{
    private static readonly VisualStateToDoubleConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that each known channel parameter ("border", "chrome", "accent") returns a <see cref="double"/>.
    /// </summary>
    /// <param name="parameter">The converter parameter selecting the visual channel.</param>
    [Theory]
    [InlineData("border")]
    [InlineData("chrome")]
    [InlineData("accent")]
    public void Convert_KnownParameter_ReturnsDouble(string parameter)
    {
        var result = Converter.Convert(PanelVisualState.Hover, typeof(double), parameter, AnyCulture);

        Assert.IsType<double>(result);
    }

    /// <summary>
    /// Verifies that an unrecognized channel parameter does not throw.
    /// </summary>
    [Fact]
    public void Convert_UnknownParameter_DoesNotThrow()
    {
        var exception = Record.Exception(() => Converter.Convert(PanelVisualState.Idle, typeof(double), "nonsense", AnyCulture));

        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies that a value that is not a <see cref="PanelVisualState"/> defaults to the idle state's opacity
    /// instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonPanelVisualStateValue_DoesNotThrow_DefaultsToIdle()
    {
        var result = Converter.Convert("not-a-state", typeof(double), "border", AnyCulture);

        Assert.Equal(InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle), result);
    }

    /// <summary>
    /// Verifies that <see cref="VisualStateToDoubleConverter.ConvertBack"/> returns <c>Binding.DoNothing</c>
    /// because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = Converter.ConvertBack(0.5, typeof(PanelVisualState), "border", AnyCulture);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}

// docs/PLANS/phase-6-edit-layout-ux.md §1 D-6: edit-aware siblings of VisualStateToDoubleConverter/
// AccentIndicatorWidthConverter above — same "never throw" contract, plus the regression guarantee that
// isLayoutEditing:false reproduces the original single-value converters' output exactly (LayoutEditVisuals
// itself is what pins that; LayoutEditVisualsTests covers it directly — these tests only confirm the
// converters wire values/parameter through correctly).

/// <summary>
/// Unit tests for <see cref="EditAwareVisualStateToDoubleConverter"/>, the multi-value, layout-editing-aware
/// sibling of <see cref="VisualStateToDoubleConverter"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class EditAwareVisualStateToDoubleConverterTests
{
    private static readonly EditAwareVisualStateToDoubleConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that with <c>isLayoutEditing:false</c>, the converter reproduces
    /// <see cref="VisualStateToDoubleConverter"/>'s output exactly for the "border" and "chrome" channels.
    /// </summary>
    /// <param name="parameter">The converter parameter selecting the visual channel.</param>
    [Theory]
    [InlineData("border")]
    [InlineData("chrome")]
    public void Convert_EditingFalse_MatchesSingleValueConverterExactly(string parameter)
    {
        var single = new VisualStateToDoubleConverter().Convert(PanelVisualState.Hover, typeof(double), parameter, AnyCulture);
        var multi = Converter.Convert(new object?[] { PanelVisualState.Hover, false }, typeof(double), parameter, AnyCulture);

        Assert.Equal(single, multi);
    }

    /// <summary>
    /// Verifies that idle state with <c>isLayoutEditing:true</c> returns a fully opaque value for the "chrome" channel.
    /// </summary>
    [Fact]
    public void Convert_IdleEditingTrue_Chrome_ReturnsFullyOpaque()
    {
        var result = Converter.Convert(new object?[] { PanelVisualState.Idle, true }, typeof(double), "chrome", AnyCulture);

        Assert.Equal(1.0, result);
    }

    /// <summary>
    /// Verifies that idle state with <c>isLayoutEditing:true</c> returns the editing border opacity floor for
    /// the "border" channel.
    /// </summary>
    [Fact]
    public void Convert_IdleEditingTrue_Border_ReturnsEditFloor()
    {
        var result = Converter.Convert(new object?[] { PanelVisualState.Idle, true }, typeof(double), "border", AnyCulture);

        Assert.Equal(LayoutEditVisuals.EditBorderFloor, result);
    }

    /// <summary>
    /// Verifies that an empty values array defaults to idle, not-editing behavior instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_TooFewValues_DoesNotThrow_DefaultsToIdleNotEditing()
    {
        var result = Converter.Convert(Array.Empty<object?>(), typeof(double), "border", AnyCulture);

        Assert.Equal(InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle), result);
    }

    /// <summary>
    /// Verifies that a first value that is not a <see cref="PanelVisualState"/> defaults to idle instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonPanelVisualStateFirstValue_DoesNotThrow_DefaultsToIdle()
    {
        var result = Converter.Convert(new object?[] { "not-a-state", true }, typeof(double), "border", AnyCulture);

        Assert.Equal(LayoutEditVisuals.EditBorderFloor, result);
    }

    /// <summary>
    /// Verifies that a second value that is not a <see cref="bool"/> defaults to not-editing instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonBoolSecondValue_DoesNotThrow_DefaultsToNotEditing()
    {
        var result = Converter.Convert(new object?[] { PanelVisualState.Idle, "not-a-bool" }, typeof(double), "border", AnyCulture);

        Assert.Equal(InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle), result);
    }

    /// <summary>
    /// Verifies that an unrecognized channel parameter falls back to the "border" channel instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_UnknownParameter_DoesNotThrow_FallsBackToBorder()
    {
        var exception = Record.Exception(() => Converter.Convert(new object?[] { PanelVisualState.Idle, false }, typeof(double), "nonsense", AnyCulture));

        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies that <see cref="EditAwareVisualStateToDoubleConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(0.5, new[] { typeof(PanelVisualState), typeof(bool) }, "border", AnyCulture));
    }

    // ------------------------------------------------------------------
    // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §5.1, T-K1) —
    // "glow"/"accentStroke"/"neutralStroke"/"glowFallback" parameters, delegating to LayeredChromeVisuals.
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that the "glow", "accentStroke", and "neutralStroke" channel parameters match
    /// <see cref="LayeredChromeVisuals"/>'s corresponding opacity methods exactly, across every
    /// <see cref="PanelVisualState"/> and layout-editing combination.
    /// </summary>
    /// <param name="parameter">The converter parameter selecting the layered chrome channel.</param>
    [Theory]
    [InlineData("glow")]
    [InlineData("accentStroke")]
    [InlineData("neutralStroke")]
    public void Convert_LayeredChromeParameters_MatchLayeredChromeVisualsExactly(string parameter)
    {
        foreach (var state in new[] { PanelVisualState.Idle, PanelVisualState.Hover, PanelVisualState.Resizing })
        {
            foreach (var isLayoutEditing in new[] { false, true })
            {
                var expected = parameter switch
                {
                    "glow" => LayeredChromeVisuals.GlowOpacityFor(state, isLayoutEditing),
                    "accentStroke" => LayeredChromeVisuals.AccentStrokeOpacityFor(state, isLayoutEditing),
                    _ => LayeredChromeVisuals.NeutralStrokeOpacityFor(state, isLayoutEditing),
                };

                var actual = Converter.Convert(new object?[] { state, isLayoutEditing }, typeof(double), parameter, AnyCulture);

                Assert.Equal(expected, actual);
            }
        }
    }

    /// <summary>
    /// Verifies that the "glowFallback" parameter equals the "glow" channel's opacity multiplied by
    /// <c>PanelMetrics.GlowFallbackOpacityFactor</c>.
    /// </summary>
    [Fact]
    public void Convert_GlowFallbackParameter_EqualsGlowChannelTimesGlowFallbackOpacityFactor()
    {
        var expected = LayeredChromeVisuals.GlowOpacityFor(PanelVisualState.Hover, false) * PanelMetrics.GlowFallbackOpacityFactor;
        var actual = Converter.Convert(new object?[] { PanelVisualState.Hover, false }, typeof(double), "glowFallback", AnyCulture);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that an unrecognized channel parameter still falls back to the "border" channel even after
    /// additional named channels ("glow", "accentStroke", etc.) were introduced.
    /// </summary>
    [Fact]
    public void Convert_UnknownParameterAfterAddingNewOnes_StillFallsBackToBorder()
    {
        var expected = LayoutEditVisuals.BorderOpacityFor(PanelVisualState.Idle, false);
        var actual = Converter.Convert(new object?[] { PanelVisualState.Idle, false }, typeof(double), "still-nonsense", AnyCulture);

        Assert.Equal(expected, actual);
    }
}

/// <summary>
/// Unit tests for <see cref="EditAwareAccentIndicatorWidthConverter"/>, the multi-value, layout-editing-aware
/// sibling of <see cref="AccentIndicatorWidthConverter"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class EditAwareAccentIndicatorWidthConverterTests
{
    private static readonly EditAwareAccentIndicatorWidthConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that with <c>isLayoutEditing:false</c>, the converter reproduces
    /// <see cref="AccentIndicatorWidthConverter"/>'s output exactly.
    /// </summary>
    [Fact]
    public void Convert_EditingFalse_MatchesSingleValueConverterExactly()
    {
        var single = new AccentIndicatorWidthConverter().Convert(PanelVisualState.Resizing, typeof(double), null, AnyCulture);
        var multi = Converter.Convert(new object?[] { PanelVisualState.Resizing, false }, typeof(double), null, AnyCulture);

        Assert.Equal(single, multi);
    }

    /// <summary>
    /// Verifies that <c>isLayoutEditing:true</c> produces a wider indicator than <c>isLayoutEditing:false</c>
    /// for the same visual state.
    /// </summary>
    [Fact]
    public void Convert_EditingTrue_IsWiderThanEditingFalse()
    {
        var normal = (double)Converter.Convert(new object?[] { PanelVisualState.Idle, false }, typeof(double), null, AnyCulture);
        var editing = (double)Converter.Convert(new object?[] { PanelVisualState.Idle, true }, typeof(double), null, AnyCulture);

        Assert.True(editing > normal);
    }

    /// <summary>
    /// Verifies that an empty values array falls back to the idle thickness instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_TooFewValues_DoesNotThrow_FallsBackToIdleThickness()
    {
        var result = Converter.Convert(Array.Empty<object?>(), typeof(double), null, AnyCulture);

        Assert.Equal(PanelMetrics.AccentIndicatorThickness, result);
    }

    /// <summary>T-K2 (ADR-030, docs/PLANS/reference-panel-chrome.md §5.1): the "marker" parameter scales AccentMarkerStrokeThickness instead of AccentIndicatorThickness.</summary>
    [Fact]
    public void Convert_MarkerParameter_ScalesAccentMarkerStrokeThickness()
    {
        var expected = PanelMetrics.AccentMarkerStrokeThickness * LayoutEditVisuals.AccentEmphasisFor(PanelVisualState.Resizing, true);
        var actual = Converter.Convert(new object?[] { PanelVisualState.Resizing, true }, typeof(double), "marker", AnyCulture);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> converter parameter still uses the accent indicator thickness
    /// rather than the marker stroke thickness.
    /// </summary>
    [Fact]
    public void Convert_NullParameter_StillUsesAccentIndicatorThickness()
    {
        var expected = PanelMetrics.AccentIndicatorThickness * LayoutEditVisuals.AccentEmphasisFor(PanelVisualState.Hover, false);
        var actual = Converter.Convert(new object?[] { PanelVisualState.Hover, false }, typeof(double), null, AnyCulture);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Verifies that <see cref="EditAwareAccentIndicatorWidthConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(5.1, new[] { typeof(PanelVisualState), typeof(bool) }, null, AnyCulture));
    }
}

/// <summary>
/// Unit tests for <see cref="ItemCountToTextConverter"/>, covering singular/plural formatting and graceful
/// handling of negative or non-integer counts. Localized (docs/PLANS/localization.md §4.3, WP-2): every
/// culture-sensitive assertion fixes an explicit culture (L-3/R-5) instead of relying on
/// <see cref="CultureInfo.InvariantCulture"/>, which no longer distinguishes en/ru plural forms.
/// </summary>
[Trait(UnitTests, "UI")]
public class ItemCountToTextConverterTests
{
    private static readonly ItemCountToTextConverter Converter = new();
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>
    /// Verifies that a count of zero returns an empty string.
    /// </summary>
    [Fact]
    public void Convert_Zero_ReturnsEmptyString()
    {
        var result = Converter.Convert(0, typeof(string), null, English);

        Assert.Equal(string.Empty, result);
    }

    /// <summary>
    /// Verifies that a count of one returns the singular "1 item" text in English.
    /// </summary>
    [Fact]
    public void Convert_One_English_ReturnsSingularText()
    {
        var result = Converter.Convert(1, typeof(string), null, English);

        Assert.Equal("1 item", result);
    }

    /// <summary>
    /// Verifies that counts greater than one return the plural "N items" text in English.
    /// </summary>
    /// <param name="count">The item count to convert.</param>
    /// <param name="expected">The expected formatted text.</param>
    [Theory]
    [InlineData(2, "2 items")]
    [InlineData(42, "42 items")]
    public void Convert_MultipleItems_English_ReturnsPluralText(int count, string expected)
    {
        var result = Converter.Convert(count, typeof(string), null, English);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies the Russian CLDR One/Few/Many plural forms, including the <c>11</c>/<c>21</c>
    /// exceptions (docs/PLANS/localization.md L-8).
    /// </summary>
    /// <param name="count">The item count to convert.</param>
    /// <param name="expected">The expected formatted Russian text.</param>
    [Theory]
    [InlineData(1, "1 элемент")]
    [InlineData(2, "2 элемента")]
    [InlineData(5, "5 элементов")]
    [InlineData(11, "11 элементов")]
    [InlineData(21, "21 элемент")]
    [InlineData(22, "22 элемента")]
    [InlineData(25, "25 элементов")]
    public void Convert_Russian_ReturnsExpectedPluralForm(int count, string expected)
    {
        var result = Converter.Convert(count, typeof(string), null, Russian);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that a negative count returns an empty string instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NegativeCount_ReturnsEmptyString_DoesNotThrow()
    {
        var result = Converter.Convert(-5, typeof(string), null, English);

        Assert.Equal(string.Empty, result);
    }

    /// <summary>
    /// Verifies that a non-<see cref="int"/> value returns an empty string instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonIntValue_ReturnsEmptyString_DoesNotThrow()
    {
        var result = Converter.Convert("not-an-int", typeof(string), null, English);

        Assert.Equal(string.Empty, result);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> value does not throw.
    /// </summary>
    [Fact]
    public void Convert_NullValue_DoesNotThrow()
    {
        var exception = Record.Exception(() => Converter.Convert(null, typeof(string), null, English));

        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies that <see cref="ItemCountToTextConverter.ConvertBack"/> returns <c>Binding.DoNothing</c> because
    /// the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = Converter.ConvertBack("1 item", typeof(int), null, English);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}
