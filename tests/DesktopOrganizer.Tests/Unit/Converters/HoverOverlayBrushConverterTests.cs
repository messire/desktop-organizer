using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="HoverOverlayBrushConverter"/> (MAJOR-3, docs/PLANS/workspace-grid-07-visual.md
/// fix-cycle 1). Same defensive contract as the other Ambient Accent converters
/// (<see cref="PanelSurfaceBrushConverterTests"/>): never throws, frozen bounded-cache brushes, garbage
/// input degrades gracefully.
/// </summary>
[Trait(UnitTests, "UI")]
public class HoverOverlayBrushConverterTests
{
    private static readonly HoverOverlayBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;
    private static readonly SolidColorBrush FallbackHoverBrush = new(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    [Fact]
    public void Convert_ValidInputs_ReturnsFrozenBrush()
    {
        var result = Converter.Convert(
            new object?[] { AccentPalette.Neutral, PanelSurfaceStyle.Ambient, false, FallbackHoverBrush },
            typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_EveryShippedSurfaceToken_ReturnsDarkenWash()
    {
        // Every accent preset mixed at SurfaceComposer.AmbientAccentRatio into the neutral #1E1E1E base
        // stays well below HoverOverlayPolicy.DarkSurfaceLuminanceThreshold today (see
        // HoverOverlayPolicy's own remarks) — this pins that the wiring end-to-end (converter ->
        // SurfaceComposer -> ContrastCalculator -> HoverOverlayPolicy) actually reaches that conclusion
        // for every real token, not just a hand-picked one.
        foreach (var style in new[] { PanelSurfaceStyle.Solid, PanelSurfaceStyle.Ambient, PanelSurfaceStyle.Glass })
        {
            foreach (var accent in AllAccents())
            {
                var result = Converter.Convert(
                    new object?[] { accent, style, false, FallbackHoverBrush },
                    typeof(Brush), null, AnyCulture);

                var brush = Assert.IsType<SolidColorBrush>(result);
                Assert.Equal(HoverOverlayPolicy.DarkenWash, brush.Color);
            }
        }
    }

    [Fact]
    public void Convert_HighContrastTrue_ReturnsFallbackBrushVerbatim()
    {
        var result = Converter.Convert(
            new object?[] { AccentPalette.Neutral, PanelSurfaceStyle.Glass, true, FallbackHoverBrush },
            typeof(Brush), null, AnyCulture);

        Assert.Same(FallbackHoverBrush, result);
    }

    [Fact]
    public void Convert_HighContrastTrue_MissingFallback_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(
            new object?[] { AccentPalette.Neutral, PanelSurfaceStyle.Glass, true, null },
            typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    [Fact]
    public void Convert_TooFewValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { AccentPalette.Neutral, PanelSurfaceStyle.Ambient }, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    [Fact]
    public void Convert_GarbageAccentHex_DoesNotThrow_StillReturnsFrozenBrush()
    {
        var result = Converter.Convert(
            new object?[] { "not-a-colour", PanelSurfaceStyle.Ambient, false, FallbackHoverBrush },
            typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_NonStyleSecondValue_DoesNotThrow()
    {
        var result = Converter.Convert(
            new object?[] { AccentPalette.Neutral, "not-a-style", false, FallbackHoverBrush },
            typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void Convert_NullValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(null!, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    [Fact]
    public void Convert_SameInputsTwice_ReturnsSameInstance()
    {
        var first = Converter.Convert(new object?[] { AccentPalette.Neutral, PanelSurfaceStyle.Ambient, false, FallbackHoverBrush }, typeof(Brush), null, AnyCulture);
        var second = Converter.Convert(new object?[] { AccentPalette.Neutral, PanelSurfaceStyle.Ambient, false, FallbackHoverBrush }, typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    [Fact]
    public void ConvertBack_AlwaysThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(FallbackHoverBrush, [typeof(string), typeof(PanelSurfaceStyle), typeof(bool), typeof(Brush)], null, AnyCulture));
    }

    private static IEnumerable<string> AllAccents()
    {
        yield return AccentPalette.Neutral;
        foreach (var preset in AccentPalette.Presets)
            yield return preset;
    }
}
