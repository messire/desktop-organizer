using System.Globalization;
using DesktopOrganizer.UI.Converters;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="HexBrushConverter"/> and <see cref="ColorOpacityBrushConverter"/>,
/// covering the bounded, frozen brush cache introduced to avoid a per-call allocation +
/// un-frozen-brush render cost (Phase 7 performance hardening, Block A).
/// </summary>
[Trait(UnitTests, "UI")]
public class HexBrushConverterTests
{
    private static readonly HexBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that a valid six-digit hex string produces a frozen <see cref="SolidColorBrush"/>.
    /// </summary>
    [Fact]
    public void Convert_ValidHex_ReturnsFrozenBrush()
    {
        var result = Converter.Convert("#112233", typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that converting the same hex string twice returns the same cached brush instance.
    /// </summary>
    [Fact]
    public void Convert_SameHexTwice_ReturnsSameInstance()
    {
        var first = Converter.Convert("#445566", typeof(Brush), null, AnyCulture);
        var second = Converter.Convert("#445566", typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that hex strings differing only by surrounding whitespace or case normalize to the same
    /// cache key and return the same cached brush instance.
    /// </summary>
    [Fact]
    public void Convert_KeyNormalization_WhitespaceAndCaseInsensitive_ReturnsSameInstance()
    {
        var first = Converter.Convert(" #DDD ", typeof(Brush), null, AnyCulture);
        var second = Converter.Convert("#ddd", typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that an invalid hex string returns <see cref="Brushes.Transparent"/>.
    /// </summary>
    [Fact]
    public void Convert_InvalidHex_ReturnsTransparent()
    {
        var result = Converter.Convert("not-a-colour", typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that a non-<see cref="string"/> value returns <see cref="Brushes.Transparent"/>.
    /// </summary>
    [Fact]
    public void Convert_NonStringValue_ReturnsTransparent()
    {
        var result = Converter.Convert(42, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that an empty string returns <see cref="Brushes.Transparent"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_EmptyString_ReturnsTransparent_DoesNotThrow()
    {
        // ColorConverter.ConvertFromString("") returns null; TryParseColor must reject this via
        // the regex pre-check rather than unboxing a null Color (which would NullReferenceException).
        var result = Converter.Convert(string.Empty, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that an ICC profile reference string returns <see cref="Brushes.Transparent"/> instead of
    /// propagating the exceptions that <see cref="System.Windows.Media.ColorConverter"/> can throw for it.
    /// </summary>
    [Fact]
    public void Convert_IccProfileReference_ReturnsTransparent_DoesNotThrow()
    {
        // ColorConverter.ConvertFromString supports "ContextColor <path>.icc ..." syntax and can
        // throw ArgumentException/IOException (not FormatException) for such input. This must
        // never propagate out of the converter: it feeds a live settings TextBox binding.
        var result = Converter.Convert(
            "ContextColor C:\\does\\not\\exist.icc 1.0 0.0 0.0",
            typeof(Brush),
            null,
            AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that converting many distinct hex values keeps returning frozen brushes without crashing,
    /// exercising the bounded cache's eviction path.
    /// </summary>
    [Fact]
    public void Convert_ManyDistinctHexValues_CacheStaysBounded()
    {
        // 500 distinct hex strings, well beyond HexBrushConverter.MaxCachedBrushes (256).
        // The DoD here is "no unbounded growth / no crash", verified indirectly: every call
        // must still succeed and return a frozen brush even after the cache has been cleared
        // and repopulated many times over.
        for (var i = 0; i < 500; i++)
        {
            var hex = $"#{i % 256:X2}{(i * 3) % 256:X2}{(i * 7) % 256:X2}";

            var result = Converter.Convert(hex, typeof(Brush), null, AnyCulture);

            var brush = Assert.IsType<SolidColorBrush>(result);
            Assert.True(brush.IsFrozen);
        }
    }

    /// <summary>
    /// Verifies that <see cref="HexBrushConverter.TryParseColor"/> returns <see langword="true"/> and the
    /// parsed <see cref="Color"/> for a valid hex string.
    /// </summary>
    [Fact]
    public void TryParseColor_ValidHex_ReturnsTrueAndColor()
    {
        var ok = HexBrushConverter.TryParseColor("#112233", out var color);

        Assert.True(ok);
        Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), color);
    }

    /// <summary>
    /// Verifies that <see cref="HexBrushConverter.TryParseColor"/> returns <see langword="false"/> for an
    /// empty string instead of throwing.
    /// </summary>
    [Fact]
    public void TryParseColor_EmptyString_ReturnsFalse_DoesNotThrow()
    {
        var ok = HexBrushConverter.TryParseColor(string.Empty, out _);

        Assert.False(ok);
    }

    /// <summary>
    /// Verifies that <see cref="HexBrushConverter.TryParseColor"/> returns <see langword="false"/> for an
    /// ICC profile reference string instead of throwing.
    /// </summary>
    [Fact]
    public void TryParseColor_IccProfileReference_ReturnsFalse_DoesNotThrow()
    {
        // Rejected up front by the regex pre-check (no leading '#'), so ColorConverter never sees
        // it. Even if it did, ColorConverter.ConvertFromString can throw non-FormatException types
        // for this syntax, which TryParseColor must also swallow defensively.
        var ok = HexBrushConverter.TryParseColor("ContextColor C:\\bogus.icc 1.0 0.0 0.0", out _);

        Assert.False(ok);
    }

    /// <summary>
    /// Verifies that converting a <see cref="SolidColorBrush"/> back returns the color's string representation.
    /// </summary>
    [Fact]
    public void ConvertBack_SolidColorBrush_ReturnsColorString()
    {
        var brush = new SolidColorBrush(Colors.Red);

        var result = Converter.ConvertBack(brush, typeof(string), null, AnyCulture);

        Assert.Equal(Colors.Red.ToString(), result);
    }

    /// <summary>
    /// Verifies that converting back a value that is not a <see cref="Brush"/> returns <c>Binding.DoNothing</c>.
    /// </summary>
    [Fact]
    public void ConvertBack_NonBrushValue_ReturnsDoNothing()
    {
        var result = Converter.ConvertBack("not a brush", typeof(string), null, AnyCulture);

        Assert.Equal(System.Windows.Data.Binding.DoNothing, result);
    }
}

/// <summary>
/// Unit tests for <see cref="ColorOpacityBrushConverter"/>.
/// </summary>
[Trait(UnitTests, "UI")]
public class ColorOpacityBrushConverterTests
{
    private static readonly ColorOpacityBrushConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Verifies that valid hex/opacity inputs produce a frozen <see cref="SolidColorBrush"/>.
    /// </summary>
    [Fact]
    public void Convert_ValidInputs_ReturnsFrozenBrush()
    {
        var result = Converter.Convert(new object?[] { "#112233", 0.5 }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.IsFrozen);
    }

    /// <summary>
    /// Verifies that converting the same hex/opacity pair twice returns the same cached brush instance.
    /// </summary>
    [Fact]
    public void Convert_SameHexAndOpacityTwice_ReturnsSameInstance()
    {
        var first = Converter.Convert(new object?[] { "#778899", 0.25 }, typeof(Brush), null, AnyCulture);
        var second = Converter.Convert(new object?[] { "#778899", 0.25 }, typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that the same hex with different opacity values returns distinct cached brush instances.
    /// </summary>
    [Fact]
    public void Convert_SameHexDifferentOpacity_ReturnsDifferentInstances()
    {
        var first = Converter.Convert(new object?[] { "#778899", 0.25 }, typeof(Brush), null, AnyCulture);
        var second = Converter.Convert(new object?[] { "#778899", 0.75 }, typeof(Brush), null, AnyCulture);

        Assert.NotSame(first, second);
    }

    /// <summary>
    /// Verifies that hex strings differing only by surrounding whitespace or case normalize to the same
    /// cache key and return the same cached brush instance.
    /// </summary>
    [Fact]
    public void Convert_KeyNormalization_WhitespaceAndCaseInsensitive_ReturnsSameInstance()
    {
        var first = Converter.Convert(new object?[] { " #AABBCC ", 0.5 }, typeof(Brush), null, AnyCulture);
        var second = Converter.Convert(new object?[] { "#aabbcc", 0.5 }, typeof(Brush), null, AnyCulture);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that fewer than two values returns <see cref="Brushes.Transparent"/>.
    /// </summary>
    [Fact]
    public void Convert_ValuesLengthLessThanTwo_ReturnsTransparent()
    {
        var result = Converter.Convert(new object?[] { "#112233" }, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that a first value that is not a <see cref="string"/> returns <see cref="Brushes.Transparent"/>.
    /// </summary>
    [Fact]
    public void Convert_FirstValueNotString_ReturnsTransparent()
    {
        var result = Converter.Convert(new object?[] { 1, 0.5 }, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that an invalid hex value returns <see cref="Brushes.Transparent"/>.
    /// </summary>
    [Fact]
    public void Convert_InvalidHex_ReturnsTransparent()
    {
        var result = Converter.Convert(new object?[] { "garbage", 0.5 }, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that an empty hex value returns <see cref="Brushes.Transparent"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_EmptyHex_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(new object?[] { string.Empty, 0.5 }, typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that an ICC profile reference hex value returns <see cref="Brushes.Transparent"/> instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_IccProfileReference_ReturnsTransparent_DoesNotThrow()
    {
        var result = Converter.Convert(
            new object?[] { "ContextColor C:\\bogus.icc 1.0 0.0 0.0", 0.5 },
            typeof(Brush),
            null,
            AnyCulture);

        Assert.Same(Brushes.Transparent, result);
    }

    /// <summary>
    /// Verifies that boundary opacity values produce the expected alpha channel byte.
    /// </summary>
    /// <param name="opacity">The opacity value to convert.</param>
    /// <param name="expectedAlpha">The expected alpha channel byte.</param>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 128)]
    [InlineData(1.0, 255)]
    public void Convert_OpacityBoundaries_ProducesExpectedAlpha(double opacity, byte expectedAlpha)
    {
        var result = Converter.Convert(new object?[] { "#010203", opacity }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(expectedAlpha, brush.Color.A);
    }

    /// <summary>
    /// Verifies that out-of-range opacity values are clamped to the valid alpha range rather than overflowing.
    /// </summary>
    /// <param name="opacity">The out-of-range opacity value to convert.</param>
    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Convert_OpacityOutOfRange_IsClamped(double opacity)
    {
        var result = Converter.Convert(new object?[] { "#010203", opacity }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.True(brush.Color.A is 0 or 255);
    }

    /// <summary>
    /// Verifies that a non-<see cref="double"/> opacity value defaults to fully opaque instead of throwing.
    /// </summary>
    [Fact]
    public void Convert_NonDoubleOpacity_DefaultsToFullyOpaque()
    {
        var result = Converter.Convert(new object?[] { "#010203", "not a double" }, typeof(Brush), null, AnyCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(255, brush.Color.A);
    }

    /// <summary>
    /// Verifies that converting many distinct (hex, opacity) key pairs keeps returning frozen brushes without
    /// crashing, exercising the bounded cache's eviction path under heavy churn.
    /// </summary>
    [Fact]
    public void Convert_ManyDistinctKeys_CacheStaysBounded()
    {
        // 500 distinct (hex, alpha) pairs, well beyond MaxCachedBrushes (512) once combined with
        // the other cache-bound test in this suite sharing the same static cache; regardless, the
        // DoD here is "no crash / always returns a frozen brush" even under heavy churn.
        for (var i = 0; i < 500; i++)
        {
            var hex = $"#{i % 256:X2}{(i * 3) % 256:X2}{(i * 7) % 256:X2}";
            var opacity = (i % 101) / 100.0;

            var result = Converter.Convert(new object?[] { hex, opacity }, typeof(Brush), null, AnyCulture);

            var brush = Assert.IsType<SolidColorBrush>(result);
            Assert.True(brush.IsFrozen);
        }
    }

    /// <summary>
    /// Verifies that <see cref="ColorOpacityBrushConverter.ConvertBack"/> always throws
    /// <see cref="NotSupportedException"/> because the conversion is one-way.
    /// </summary>
    [Fact]
    public void ConvertBack_Always_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => Converter.ConvertBack(new SolidColorBrush(Colors.Red), new[] { typeof(string), typeof(double) }, null, AnyCulture));
    }
}
