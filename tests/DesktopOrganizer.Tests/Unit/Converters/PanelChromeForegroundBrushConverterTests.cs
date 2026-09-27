using System.Globalization;
using System.Windows.Media;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="PanelChromeForegroundBrushConverter"/> (docs/PLANS/color-presets-and-size-step.md
/// WP-B "ADDITIONAL"; MAJOR-2 review-round fix adds the <c>text</c> value). Same defensive contract as the
/// other Ambient Accent converters (<see cref="HoverOverlayBrushConverterTests"/>): never throws, frozen
/// bounded-cache brushes, garbage input degrades gracefully, and a High Contrast flag of
/// <see langword="true"/> returns the supplied fallback brush verbatim.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelChromeForegroundBrushConverterTests
{
    private static readonly PanelChromeForegroundBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;
    private static readonly SolidColorBrush FallbackBrush = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush DefaultTextBrush = AccentBrush("#DDDDDD");
    private static readonly SolidColorBrush SkyTextBrush = AccentBrush("#17324D");

    /// <summary>
    /// Every accent this app shipped before Q-1 variant B (<see cref="AccentPalette.Neutral"/> + its six
    /// <see cref="AccentPalette.Presets"/>) must resolve to the exact byte-identical DARK token
    /// <see cref="PanelChromeForegroundPolicy"/> defines for every <see cref="PanelChromeForegroundKind"/> —
    /// i.e. Q-1 variant B changes nothing about how any pre-existing panel's chrome renders.
    /// </summary>
    /// <param name="kind">The chrome-foreground kind to resolve (the theory's own <c>ConverterParameter</c> value).</param>
    [Theory]
    [InlineData(PanelChromeForegroundKind.Primary)]
    [InlineData(PanelChromeForegroundKind.Secondary)]
    [InlineData(PanelChromeForegroundKind.Muted)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumb)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbBorder)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbHover)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbPressed)]
    [InlineData(PanelChromeForegroundKind.Border)]
    public void Convert_EveryLegacyAccent_ReturnsByteIdenticalDarkToken(PanelChromeForegroundKind kind)
    {
        var expected = PanelChromeForegroundPolicy.Resolve(AccentBrush(AccentPalette.Neutral).Color, DefaultTextBrush.Color, kind);

        foreach (var accentHex in AllLegacyAccents())
        {
            var result = Converter.Convert(
                [AccentBrush(accentHex), DefaultTextBrush, false, FallbackBrush],
                typeof(Brush),
                kind.ToString(),
                AnyCulture);

            var brush = Assert.IsType<SolidColorBrush>(result);
            Assert.Equal(expected, brush.Color);
            Assert.True(brush.IsFrozen);
        }
    }

    /// <summary>A light accent (Sky, <c>#B9DCFF</c>) PAIRED WITH ITS OWN DARK TEXT resolves to the LIGHT token set, not the dark one.</summary>
    [Fact]
    public void Convert_LightAccentPairedWithOwnDarkText_ReturnsLightToken_NotDarkToken()
    {
        var result = Converter.Convert(
            [AccentBrush("#B9DCFF"), SkyTextBrush, false, FallbackBrush],
            typeof(Brush),
            "Primary",
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(PanelChromeForegroundPolicy.LightPrimary, brush.Color);
        Assert.NotEqual(PanelChromeForegroundPolicy.DarkPrimary, brush.Color);
    }

    /// <summary>
    /// MAJOR-2 fix (review round, orchestrator decision "variant A"): the SAME light accent, paired with the
    /// app's own LIGHT default text instead of its own dark text, must resolve to the DARK token set — a
    /// legacy panel with a custom light accent and default light text must not go unreadable.
    /// </summary>
    [Fact]
    public void Convert_LightAccentPairedWithLightText_ReturnsDarkToken()
    {
        var result = Converter.Convert(
            [AccentBrush("#B9DCFF"), DefaultTextBrush, false, FallbackBrush],
            typeof(Brush),
            "Primary",
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(PanelChromeForegroundPolicy.DarkPrimary, brush.Color);
    }

    /// <summary>A High Contrast flag of <see langword="true"/> returns the supplied fallback brush verbatim, bypassing the accent-luminance rule entirely.</summary>
    [Fact]
    public void Convert_HighContrastTrue_ReturnsFallbackBrushVerbatim()
    {
        var result = Converter.Convert(
            [AccentBrush("#B9DCFF"), SkyTextBrush, true, FallbackBrush],
            typeof(Brush),
            "Primary",
            AnyCulture);

        Assert.Same(FallbackBrush, result);
    }

    /// <summary>A High Contrast flag of <see langword="true"/> with a missing fallback brush returns <see cref="Brushes.Transparent"/> instead of throwing.</summary>
    [Fact]
    public void Convert_HighContrastTrue_MissingFallback_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(
            [AccentBrush(AccentPalette.Neutral), DefaultTextBrush, true, null],
            typeof(Brush),
            "Primary",
            AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>Fewer values than the converter expects returns <see cref="Brushes.Transparent"/> instead of throwing.</summary>
    [Fact]
    public void Convert_TooFewValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert([AccentBrush(AccentPalette.Neutral), DefaultTextBrush, false], typeof(Brush), "Primary", AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>A <see langword="null"/> values array returns <see cref="Brushes.Transparent"/> instead of throwing.</summary>
    [Fact]
    public void Convert_NullValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(null!, typeof(Brush), "Primary", AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>A non-<see cref="SolidColorBrush"/> (or missing) first value falls back to the dark token set instead of throwing.</summary>
    [Fact]
    public void Convert_NonBrushFirstValue_DoesNotThrow_FallsBackToDarkToken()
    {
        var result = Converter.Convert(["not-a-brush", DefaultTextBrush, false, FallbackBrush], typeof(Brush), "Primary", AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(PanelChromeForegroundPolicy.DarkPrimary, brush.Color);
    }

    /// <summary>A non-<see cref="SolidColorBrush"/> (or missing) second (text) value falls back to the default text colour instead of throwing.</summary>
    [Fact]
    public void Convert_NonBrushSecondValue_DoesNotThrow_FallsBackToDefaultTextColor()
    {
        var result = Converter.Convert([AccentBrush(AccentPalette.Neutral), "not-a-brush", false, FallbackBrush], typeof(Brush), "Primary", AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(PanelChromeForegroundPolicy.DarkPrimary, brush.Color);
    }

    /// <summary>An unrecognized/missing <c>ConverterParameter</c> defaults to <see cref="PanelChromeForegroundKind.Primary"/>.</summary>
    [Fact]
    public void Convert_MissingParameter_DefaultsToPrimary()
    {
        var result = Converter.Convert([AccentBrush(AccentPalette.Neutral), DefaultTextBrush, false, FallbackBrush], typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(PanelChromeForegroundPolicy.DarkPrimary, brush.Color);
    }

    /// <summary>Converting identical inputs twice returns the same cached brush instance.</summary>
    [Fact]
    public void Convert_SameInputsTwice_ReturnsSameInstance()
    {
        var first = Converter.Convert([AccentBrush(AccentPalette.Neutral), DefaultTextBrush, false, FallbackBrush], typeof(Brush), "Secondary", AnyCulture);
        var second = Converter.Convert([AccentBrush(AccentPalette.Neutral), DefaultTextBrush, false, FallbackBrush], typeof(Brush), "Secondary", AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary><see cref="PanelChromeForegroundBrushConverter.ConvertBack"/> always throws <see cref="NotSupportedException"/> because the conversion is one-way.</summary>
    [Fact]
    public void ConvertBack_AlwaysThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(FallbackBrush, [typeof(Brush), typeof(Brush), typeof(bool), typeof(Brush)], null, AnyCulture));
    }

    private static SolidColorBrush AccentBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    private static IEnumerable<string> AllLegacyAccents()
    {
        yield return AccentPalette.Neutral;
        foreach (var preset in AccentPalette.Presets)
            yield return preset;
    }
}
