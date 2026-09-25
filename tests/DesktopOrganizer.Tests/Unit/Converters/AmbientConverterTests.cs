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

    [Fact]
    public void Convert_ValidInputs_ReturnsFrozenBrush()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_SameInputsTwice_ReturnsSameInstance()
    {
        var first = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);
        var second = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    [Fact]
    public void Convert_TooFewValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.8 }, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    [Fact]
    public void Convert_GarbageAccentHex_DoesNotThrow_StillReturnsFrozenBrush()
    {
        var result = Converter.Convert(new object?[] { "not-a-colour", 0.8, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_NonDoubleOpacity_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", "nope", PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_NonStyleThirdValue_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.8, "not-a-style" }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_HighContrastTrue_ProducesFullyOpaqueBrush()
    {
        var result = Converter.Convert(new object?[] { "#3B82F6", 0.35, PanelSurfaceStyle.Glass, true }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(255, brush.Color.A);
    }

    [Fact]
    public void Convert_SolidStyle_IgnoresAccentColor()
    {
        var withBlue = Converter.Convert(new object?[] { "#3B82F6", 0.8, PanelSurfaceStyle.Solid }, typeof(Brush), null, AnyCulture);
        var withViolet = Converter.Convert(new object?[] { "#A855F7", 0.8, PanelSurfaceStyle.Solid }, typeof(Brush), null, AnyCulture);

        var blueBrush = Assert.IsType<SolidColorBrush>(withBlue);
        var violetBrush = Assert.IsType<SolidColorBrush>(withViolet);
        Assert.Equal(blueBrush.Color, violetBrush.Color);
    }

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

    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(new SolidColorBrush(Colors.Red), new[] { typeof(string), typeof(double), typeof(PanelSurfaceStyle) }, null, AnyCulture));
    }
}

// Cheap fix (review round 3): AccentIndicatorWidthConverter (MAJOR-B) had no dedicated unit test —
// only exercised transitively via ResourceDictionaryMergeTests' template materialization.
[Trait(UnitTests, "UI")]
public class AccentIndicatorWidthConverterTests
{
    private static readonly AccentIndicatorWidthConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_Resizing_ReturnsThicknessScaledByMaxEmphasis()
    {
        var result = Converter.Convert(PanelVisualState.Resizing, typeof(double), null, AnyCulture);

        Assert.Equal(PanelMetrics.AccentIndicatorThickness * 1.7, result);
        Assert.Equal(5.1, result);
    }

    [Fact]
    public void Convert_Null_DoesNotThrow_FallsBackToIdleThickness()
    {
        var result = Converter.Convert(null, typeof(double), null, AnyCulture);

        Assert.Equal(PanelMetrics.AccentIndicatorThickness * 1.0, result);
        Assert.Equal(3.0, result);
    }

    [Fact]
    public void Convert_UnsetValue_DoesNotThrow_FallsBackToIdleThickness()
    {
        var result = Converter.Convert(DependencyProperty.UnsetValue, typeof(double), null, AnyCulture);

        Assert.Equal(3.0, result);
    }

    [Fact]
    public void Convert_NonPanelVisualStateValue_DoesNotThrow_FallsBackToIdleThickness()
    {
        var result = Converter.Convert("not-a-state", typeof(double), null, AnyCulture);

        Assert.Equal(3.0, result);
    }

    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = Converter.ConvertBack(5.1, typeof(PanelVisualState), null, AnyCulture);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}

[Trait(UnitTests, "UI")]
public class AccentBrushConverterTests
{
    private static readonly AccentBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

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

    [Fact]
    public void Convert_UnknownParameter_DoesNotThrow()
    {
        var result = Converter.Convert("#3B82F6", typeof(Brush), "not-a-known-mode", AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_NullParameter_DoesNotThrow()
    {
        var result = Converter.Convert("#3B82F6", typeof(Brush), null, AnyCulture);

        Assert.IsType<SolidColorBrush>(result);
    }

    [Fact]
    public void Convert_GarbageHex_DoesNotThrow_ReturnsFallbackAccentBrush()
    {
        // AccentPalette.Normalize maps junk to Default (Neutral) rather than throwing.
        var result = Converter.Convert("garbage", typeof(Brush), "full", AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_NonStringValue_DoesNotThrow()
    {
        var result = Converter.Convert(42, typeof(Brush), "full", AnyCulture);

        Assert.IsType<SolidColorBrush>(result);
    }

    [Fact]
    public void Convert_IndicatorMode_IsMoreOpaqueThanHoverMode()
    {
        var indicator = (SolidColorBrush)Converter.Convert("#3B82F6", typeof(Brush), "indicator", AnyCulture);
        var hover = (SolidColorBrush)Converter.Convert("#3B82F6", typeof(Brush), "hover", AnyCulture);

        Assert.True(indicator.Color.A > hover.Color.A);
    }

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
    [Fact]
    public void SelectionAlpha_IsPinnedAt028()
    {
        Assert.Equal(0.28, AccentBrushConverter.SelectionAlpha);
    }

    [Fact]
    public void HoverAlpha_IsPinnedAt016()
    {
        Assert.Equal(0.16, AccentBrushConverter.HoverAlpha);
    }
}

[Trait(UnitTests, "UI")]
public class VisualStateToDoubleConverterTests
{
    private static readonly VisualStateToDoubleConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("border")]
    [InlineData("chrome")]
    [InlineData("accent")]
    public void Convert_KnownParameter_ReturnsDouble(string parameter)
    {
        var result = Converter.Convert(PanelVisualState.Hover, typeof(double), parameter, AnyCulture);

        Assert.IsType<double>(result);
    }

    [Fact]
    public void Convert_UnknownParameter_DoesNotThrow()
    {
        var exception = Record.Exception(() => Converter.Convert(PanelVisualState.Idle, typeof(double), "nonsense", AnyCulture));

        Assert.Null(exception);
    }

    [Fact]
    public void Convert_NonPanelVisualStateValue_DoesNotThrow_DefaultsToIdle()
    {
        var result = Converter.Convert("not-a-state", typeof(double), "border", AnyCulture);

        Assert.Equal(InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle), result);
    }

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
[Trait(UnitTests, "UI")]
public class EditAwareVisualStateToDoubleConverterTests
{
    private static readonly EditAwareVisualStateToDoubleConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("border")]
    [InlineData("chrome")]
    public void Convert_EditingFalse_MatchesSingleValueConverterExactly(string parameter)
    {
        var single = new VisualStateToDoubleConverter().Convert(PanelVisualState.Hover, typeof(double), parameter, AnyCulture);
        var multi = Converter.Convert(new object?[] { PanelVisualState.Hover, false }, typeof(double), parameter, AnyCulture);

        Assert.Equal(single, multi);
    }

    [Fact]
    public void Convert_IdleEditingTrue_Chrome_ReturnsFullyOpaque()
    {
        var result = Converter.Convert(new object?[] { PanelVisualState.Idle, true }, typeof(double), "chrome", AnyCulture);

        Assert.Equal(1.0, result);
    }

    [Fact]
    public void Convert_IdleEditingTrue_Border_ReturnsEditFloor()
    {
        var result = Converter.Convert(new object?[] { PanelVisualState.Idle, true }, typeof(double), "border", AnyCulture);

        Assert.Equal(LayoutEditVisuals.EditBorderFloor, result);
    }

    [Fact]
    public void Convert_TooFewValues_DoesNotThrow_DefaultsToIdleNotEditing()
    {
        var result = Converter.Convert(Array.Empty<object?>(), typeof(double), "border", AnyCulture);

        Assert.Equal(InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle), result);
    }

    [Fact]
    public void Convert_NonPanelVisualStateFirstValue_DoesNotThrow_DefaultsToIdle()
    {
        var result = Converter.Convert(new object?[] { "not-a-state", true }, typeof(double), "border", AnyCulture);

        Assert.Equal(LayoutEditVisuals.EditBorderFloor, result);
    }

    [Fact]
    public void Convert_NonBoolSecondValue_DoesNotThrow_DefaultsToNotEditing()
    {
        var result = Converter.Convert(new object?[] { PanelVisualState.Idle, "not-a-bool" }, typeof(double), "border", AnyCulture);

        Assert.Equal(InteractionVisualState.BorderOpacityFor(PanelVisualState.Idle), result);
    }

    [Fact]
    public void Convert_UnknownParameter_DoesNotThrow_FallsBackToBorder()
    {
        var exception = Record.Exception(() => Converter.Convert(new object?[] { PanelVisualState.Idle, false }, typeof(double), "nonsense", AnyCulture));

        Assert.Null(exception);
    }

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

    [Fact]
    public void Convert_GlowFallbackParameter_EqualsGlowChannelTimesGlowFallbackOpacityFactor()
    {
        var expected = LayeredChromeVisuals.GlowOpacityFor(PanelVisualState.Hover, false) * PanelMetrics.GlowFallbackOpacityFactor;
        var actual = Converter.Convert(new object?[] { PanelVisualState.Hover, false }, typeof(double), "glowFallback", AnyCulture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Convert_UnknownParameterAfterAddingNewOnes_StillFallsBackToBorder()
    {
        var expected = LayoutEditVisuals.BorderOpacityFor(PanelVisualState.Idle, false);
        var actual = Converter.Convert(new object?[] { PanelVisualState.Idle, false }, typeof(double), "still-nonsense", AnyCulture);

        Assert.Equal(expected, actual);
    }
}

[Trait(UnitTests, "UI")]
public class EditAwareAccentIndicatorWidthConverterTests
{
    private static readonly EditAwareAccentIndicatorWidthConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_EditingFalse_MatchesSingleValueConverterExactly()
    {
        var single = new AccentIndicatorWidthConverter().Convert(PanelVisualState.Resizing, typeof(double), null, AnyCulture);
        var multi = Converter.Convert(new object?[] { PanelVisualState.Resizing, false }, typeof(double), null, AnyCulture);

        Assert.Equal(single, multi);
    }

    [Fact]
    public void Convert_EditingTrue_IsWiderThanEditingFalse()
    {
        var normal = (double)Converter.Convert(new object?[] { PanelVisualState.Idle, false }, typeof(double), null, AnyCulture);
        var editing = (double)Converter.Convert(new object?[] { PanelVisualState.Idle, true }, typeof(double), null, AnyCulture);

        Assert.True(editing > normal);
    }

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

    [Fact]
    public void Convert_NullParameter_StillUsesAccentIndicatorThickness()
    {
        var expected = PanelMetrics.AccentIndicatorThickness * LayoutEditVisuals.AccentEmphasisFor(PanelVisualState.Hover, false);
        var actual = Converter.Convert(new object?[] { PanelVisualState.Hover, false }, typeof(double), null, AnyCulture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(5.1, new[] { typeof(PanelVisualState), typeof(bool) }, null, AnyCulture));
    }
}

[Trait(UnitTests, "UI")]
public class ItemCountToTextConverterTests
{
    private static readonly ItemCountToTextConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_Zero_ReturnsEmptyString()
    {
        var result = Converter.Convert(0, typeof(string), null, AnyCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_One_ReturnsSingularText()
    {
        var result = Converter.Convert(1, typeof(string), null, AnyCulture);

        Assert.Equal("1 item", result);
    }

    [Theory]
    [InlineData(2, "2 items")]
    [InlineData(42, "42 items")]
    public void Convert_MultipleItems_ReturnsPluralText(int count, string expected)
    {
        var result = Converter.Convert(count, typeof(string), null, AnyCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_NegativeCount_ReturnsEmptyString_DoesNotThrow()
    {
        var result = Converter.Convert(-5, typeof(string), null, AnyCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_NonIntValue_ReturnsEmptyString_DoesNotThrow()
    {
        var result = Converter.Convert("not-an-int", typeof(string), null, AnyCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_NullValue_DoesNotThrow()
    {
        var exception = Record.Exception(() => Converter.Convert(null, typeof(string), null, AnyCulture));

        Assert.Null(exception);
    }

    [Fact]
    public void ConvertBack_ReturnsDoNothing()
    {
        var result = Converter.ConvertBack("1 item", typeof(int), null, AnyCulture);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}
