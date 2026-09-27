using System.Globalization;
using System.Windows.Media;
using DesktopOrganizer.UI.Converters;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Converters;

/// <summary>
/// Unit tests for <see cref="AccentContrastForegroundConverter"/> (ADR-042): picks black-or-white for the
/// swatch grid's selected-preset checkmark glyph, based on the accent's own WCAG relative luminance.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class AccentContrastForegroundConverterTests
{
    private static readonly AccentContrastForegroundConverter Converter = new();
    private static readonly CultureInfo AnyCulture = CultureInfo.InvariantCulture;

    /// <summary>Provides every one of the twelve <see cref="PanelColorPresets"/> as theory data.</summary>
    public static IEnumerable<object[]> AllPresets() => PanelColorPresets.All.Select(p => new object[] { p });

    /// <summary>Verifies that a dark accent (e.g. near-black) resolves to a white checkmark.</summary>
    [Fact]
    public void Convert_DarkAccent_ReturnsWhite()
    {
        var result = Converter.Convert("#101010", typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.White, result);
    }

    /// <summary>Verifies that a light accent (e.g. near-white) resolves to a black checkmark.</summary>
    [Fact]
    public void Convert_LightAccent_ReturnsBlack()
    {
        var result = Converter.Convert("#F5F5F5", typeof(Brush), null, AnyCulture);

        Assert.Same(Brushes.Black, result);
    }

    /// <summary>Verifies that unparseable/non-string input never throws and defaults to white.</summary>
    /// <param name="value">The candidate (invalid) input value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-colour")]
    [InlineData(42)]
    public void Convert_InvalidInput_NeverThrows_ReturnsWhite(object? value)
    {
        var exception = Record.Exception(() => Converter.Convert(value, typeof(Brush), null, AnyCulture));

        Assert.Null(exception);
        Assert.Same(Brushes.White, Converter.Convert(value, typeof(Brush), null, AnyCulture));
    }

    /// <summary>Verifies that <see cref="AccentContrastForegroundConverter.ConvertBack"/> is not supported.</summary>
    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => Converter.ConvertBack(Brushes.White, typeof(string), null, AnyCulture));
    }

    /// <summary>M5: the picked checkmark colour clears <see cref="ContrastCalculator.MinimumIconContrast"/> (3:1) against every one of the twelve <see cref="PanelColorPresets"/> accents.</summary>
    /// <param name="preset">The preset under test.</param>
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Convert_EveryPreset_MeetsMinimumIconContrast(PanelColorPreset preset)
    {
        var result = (SolidColorBrush)Converter.Convert(preset.AccentHex, typeof(Brush), null, AnyCulture);
        var accentColor = (Color)ColorConverter.ConvertFromString(preset.AccentHex)!;

        var contrast = ContrastCalculator.ContrastRatio(result.Color, accentColor);

        Assert.True(
            contrast >= ContrastCalculator.MinimumIconContrast,
            $"{preset.Id}: checkmark contrast {contrast:F2}:1 against {preset.AccentHex} is below MinimumIconContrast ({ContrastCalculator.MinimumIconContrast}:1).");
    }
}
