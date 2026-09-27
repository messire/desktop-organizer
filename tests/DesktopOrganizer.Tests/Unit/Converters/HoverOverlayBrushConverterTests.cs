using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="HoverOverlayBrushConverter"/> (MAJOR-3, docs/PLANS/workspace-grid-07-visual.md
/// fix-cycle 1; MAJOR-2/MAJOR-3 review-round fix, docs/PLANS/color-presets-and-size-step.md — the (accent,
/// text) PAIR now decides the neutral base, and a light surface gets a darken, not a lighten, wash; NEW-1
/// fix, docs/PLANS/release-polish-1.0.md — <c>ConverterParameter="ItemTile"</c> routes through
/// <see cref="HoverOverlayPolicy.ForItemTile"/> instead of <see cref="HoverOverlayPolicy.For"/> so a dark
/// panel's icon-tile hover stays <see cref="HoverOverlayPolicy.LightenWash"/>, not the near-invisible
/// <see cref="HoverOverlayPolicy.DarkenWash"/>). Same defensive contract as the other Ambient Accent
/// converters (<see cref="PanelSurfaceBrushConverterTests"/>):
/// never throws, frozen bounded-cache brushes, garbage input degrades gracefully. Inputs are
/// <c>(theming:PanelTheme.Accent)</c>/<c>(theming:PanelTheme.Text)</c>-shaped <see cref="SolidColorBrush"/>
/// values (values[0]/values[2]), not raw hex strings — see the converter's own remarks.
/// </summary>
[Trait(UnitTests, "UI")]
public class HoverOverlayBrushConverterTests
{
    private static readonly HoverOverlayBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;
    private static readonly SolidColorBrush FallbackHoverBrush = new(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush DefaultTextBrush = AccentBrush("#DDDDDD");

    /// <summary>
    /// Verifies that valid accent/style/text/high-contrast/fallback inputs produce a frozen
    /// <see cref="SolidColorBrush"/>.
    /// </summary>
    [Fact]
    public void Convert_ValidInputs_ReturnsFrozenBrush()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that every shipped surface style/accent combination (paired with the app's own default
    /// text) resolves to <see cref="HoverOverlayPolicy.DarkenWash"/>, pinning the full
    /// converter-to-policy wiring rather than a single hand-picked token.
    /// </summary>
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
                    new object?[] { AccentBrush(accent), style, DefaultTextBrush, false, FallbackHoverBrush },
                    typeof(Brush),
                    null,
                    AnyCulture);

                var brush = Assert.IsType<SolidColorBrush>(result);
                Assert.Equal(HoverOverlayPolicy.DarkenWash, brush.Color);
            }
        }
    }

    /// <summary>
    /// Verifies that a high-contrast flag of <see langword="true"/> returns the supplied fallback brush verbatim.
    /// </summary>
    [Fact]
    public void Convert_HighContrastTrue_ReturnsFallbackBrushVerbatim()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Glass, DefaultTextBrush, true, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);

        Assert.Same(FallbackHoverBrush, result);
    }

    /// <summary>
    /// Verifies that a high-contrast flag of <see langword="true"/> with a missing fallback brush returns
    /// <see cref="Brushes.Transparent"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_HighContrastTrue_MissingFallback_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Glass, DefaultTextBrush, true, null },
            typeof(Brush),
            null,
            AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that fewer values than the converter expects returns <see cref="Brushes.Transparent"/> instead
    /// of throwing.
    /// </summary>
    [Fact]
    public void Convert_TooFewValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false },
            typeof(Brush),
            null,
            AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that a non-<see cref="SolidColorBrush"/> (or missing) accent value falls back to
    /// <see cref="AccentPalette.Neutral"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonBrushAccentValue_DoesNotThrow_FallsBackToNeutral()
    {
        var result = Converter.Convert(
            new object?[] { "not-a-brush", PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that a non-<see cref="PanelSurfaceStyle"/> second value still produces a frozen brush instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonStyleSecondValue_DoesNotThrow()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), "not-a-style", DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that a non-<see cref="SolidColorBrush"/> (or missing) text value falls back to
    /// <see cref="PanelSurfaceBrushConverter.DefaultTextColor"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonBrushTextValue_DoesNotThrow_FallsBackToDefaultTextColor()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, "not-a-brush", false, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> values array returns <see cref="Brushes.Transparent"/> instead
    /// of throwing.
    /// </summary>
    [Fact]
    public void Convert_NullValues_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(null!, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that converting identical inputs twice returns the same cached brush instance.
    /// </summary>
    [Fact]
    public void Convert_SameInputsTwice_ReturnsSameInstance()
    {
        var first = Converter.Convert(new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush }, typeof(Brush), null, AnyCulture);
        var second = Converter.Convert(new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush }, typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that <see cref="HoverOverlayBrushConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_AlwaysThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(FallbackHoverBrush, [typeof(Brush), typeof(PanelSurfaceStyle), typeof(Brush), typeof(bool), typeof(Brush)], null, AnyCulture));
    }

    // --- Q-1 variant B / MAJOR-2 / MAJOR-3 fixes (docs/PLANS/color-presets-and-size-step.md) -------------

    /// <summary>
    /// MAJOR-3 fix (review round): verifies that a light accent PAIRED WITH ITS OWN DARK TEXT
    /// (docs/PLANS/color-presets-and-size-step.md §0.5's Sky preset, <c>#B9DCFF</c>/<c>#17324D</c>) — which
    /// now resolves through <see cref="SurfaceComposer.LightNeutralBase"/> via
    /// <see cref="SurfaceComposer.ResolveNeutralBase"/> — produces <see cref="HoverOverlayPolicy.LightSurfaceDarkenWash"/>,
    /// NOT <see cref="HoverOverlayPolicy.LightenWash"/> (flips the pre-fix pinned expectation: lightening an
    /// already near-white surface is imperceptible).
    /// </summary>
    [Fact]
    public void Convert_LightAccentPairedWithOwnDarkText_ReturnsLightSurfaceDarkenWash_NotLightenWash()
    {
        var textBrush = AccentBrush("#17324D");

        foreach (var style in new[] { PanelSurfaceStyle.Solid, PanelSurfaceStyle.Ambient, PanelSurfaceStyle.Glass })
        {
            var result = Converter.Convert(
                new object?[] { AccentBrush("#B9DCFF"), style, textBrush, false, FallbackHoverBrush },
                typeof(Brush),
                null,
                AnyCulture);

            var brush = Assert.IsType<SolidColorBrush>(result);
            Assert.Equal(HoverOverlayPolicy.LightSurfaceDarkenWash, brush.Color);
            Assert.NotEqual(HoverOverlayPolicy.LightenWash, brush.Color);
        }
    }

    /// <summary>
    /// MAJOR-2 fix (review round, orchestrator decision "variant A"): verifies that the SAME light accent,
    /// paired with the app's own LIGHT default text instead of its own dark text, stays on the DARK base —
    /// producing <see cref="HoverOverlayPolicy.DarkenWash"/>, the SAME wash every legacy dark panel gets.
    /// </summary>
    [Fact]
    public void Convert_LightAccentPairedWithLightText_ReturnsDarkenWash()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush("#B9DCFF"), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(HoverOverlayPolicy.DarkenWash, brush.Color);
    }

    // --- NEW-1 fix (docs/PLANS/release-polish-1.0.md) — ConverterParameter="ItemTile" routing -----------

    /// <summary>
    /// NEW-1 fix: verifies that a dark accent (paired with the app's own default light text) routed
    /// through <c>ConverterParameter="ItemTile"</c> (<c>PanelItemContainerStyle</c>'s own icon-tile hover,
    /// Panels.xaml) resolves to <see cref="HoverOverlayPolicy.LightenWash"/> (<c>#1AFFFFFF</c>) — the
    /// ORIGINAL, clearly-visible dark-surface hover — NOT <see cref="HoverOverlayPolicy.DarkenWash"/>, which
    /// the SAME inputs resolve to on the header buttons' unparameterized path
    /// (<see cref="Convert_EveryShippedSurfaceToken_ReturnsDarkenWash"/>).
    /// </summary>
    [Fact]
    public void Convert_ItemTileParameter_DarkAccent_ReturnsLightenWash()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            HoverOverlayBrushConverter.ItemTileParameter,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(HoverOverlayPolicy.LightenWash, brush.Color);
    }

    /// <summary>
    /// NEW-1 fix: verifies that the Sky preset (<c>#B9DCFF</c>) paired with its own dark text
    /// (<c>#17324D</c> — resolves through <see cref="SurfaceComposer.LightNeutralBase"/>), routed through
    /// <c>ConverterParameter="ItemTile"</c>, resolves to <see cref="HoverOverlayPolicy.LightSurfaceDarkenWash"/>
    /// (<c>#14000000</c>) — the SAME light-surface wash the header buttons' unparameterized path uses for
    /// this pair (<see cref="Convert_LightAccentPairedWithOwnDarkText_ReturnsLightSurfaceDarkenWash_NotLightenWash"/>).
    /// </summary>
    [Fact]
    public void Convert_ItemTileParameter_SkyAccentPairedWithOwnDarkText_ReturnsLightSurfaceDarkenWash()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush("#B9DCFF"), PanelSurfaceStyle.Ambient, AccentBrush("#17324D"), false, FallbackHoverBrush },
            typeof(Brush),
            HoverOverlayBrushConverter.ItemTileParameter,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(HoverOverlayPolicy.LightSurfaceDarkenWash, brush.Color);
    }

    /// <summary>
    /// NEW-1 fix: verifies that the header buttons' own path (no <c>ConverterParameter</c>) is unaffected
    /// by the new <c>ItemTile</c> routing — a dark accent still resolves to
    /// <see cref="HoverOverlayPolicy.DarkenWash"/>, exactly as before this fix.
    /// </summary>
    [Fact]
    public void Convert_NoParameter_DarkAccent_StillReturnsDarkenWash()
    {
        var result = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(HoverOverlayPolicy.DarkenWash, brush.Color);
    }

    /// <summary>
    /// NEW-1 fix: verifies that the SAME accent/style/text pair produces DIFFERENT cached brush instances
    /// for the header-button path (no parameter) versus the <c>ItemTile</c> path — pinning that the
    /// <c>ParameterKey</c> cache-key component actually distinguishes them instead of one clobbering the
    /// other's cache entry.
    /// </summary>
    [Fact]
    public void Convert_SameAccentDifferentParameter_ReturnsDifferentCachedInstances()
    {
        var headerResult = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            null,
            AnyCulture);
        var itemTileResult = Converter.Convert(
            new object?[] { AccentBrush(AccentPalette.Neutral), PanelSurfaceStyle.Ambient, DefaultTextBrush, false, FallbackHoverBrush },
            typeof(Brush),
            HoverOverlayBrushConverter.ItemTileParameter,
            AnyCulture);

        Assert.NotSame(headerResult, itemTileResult);
        Assert.Equal(HoverOverlayPolicy.DarkenWash, Assert.IsType<SolidColorBrush>(headerResult).Color);
        Assert.Equal(HoverOverlayPolicy.LightenWash, Assert.IsType<SolidColorBrush>(itemTileResult).Color);
    }

    private static SolidColorBrush AccentBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    private static IEnumerable<string> AllAccents()
    {
        yield return AccentPalette.Neutral;
        foreach (var preset in AccentPalette.Presets)
            yield return preset;
    }
}
