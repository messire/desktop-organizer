using DesktopOrganizer.Services.Abstractions;
using DrawingColor = System.Drawing.Color;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="ColorDialogColor"/> (docs/PLANS/color-presets-and-size-step.md §4.1, T-7).
/// <see cref="ColorPickerService"/> itself is not unit-tested here — it shows a real modal dialog, so its
/// behaviour is covered by manual verify (§11) instead.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class ColorDialogColorTests
{
    /// <summary>Verifies the 3-digit shorthand form parses and expands each nibble.</summary>
    [Fact]
    public void TryParseHex_ShortForm_ParsesAndExpands()
    {
        var success = ColorDialogColor.TryParseHex("#3F7", out var color);

        Assert.True(success);
        Assert.Equal(0x33, color.R);
        Assert.Equal(0xFF, color.G);
        Assert.Equal(0x77, color.B);
    }

    /// <summary>Verifies the 6-digit RRGGBB form parses directly.</summary>
    [Fact]
    public void TryParseHex_SixDigitForm_Parses()
    {
        var success = ColorDialogColor.TryParseHex("#3F7FD9", out var color);

        Assert.True(success);
        Assert.Equal(0x3F, color.R);
        Assert.Equal(0x7F, color.G);
        Assert.Equal(0xD9, color.B);
    }

    /// <summary>Verifies the 8-digit AARRGGBB form parses and always ignores/discards the alpha byte.</summary>
    [Fact]
    public void TryParseHex_EightDigitArgbForm_IgnoresAlpha()
    {
        var success = ColorDialogColor.TryParseHex("#803F7FD9", out var color);

        Assert.True(success);
        Assert.Equal(0x3F, color.R);
        Assert.Equal(0x7F, color.G);
        Assert.Equal(0xD9, color.B);
        Assert.Equal(255, color.A);
    }

    /// <summary>Verifies <see langword="null"/>, empty, whitespace, and malformed input all fail to parse.</summary>
    /// <param name="input">The candidate malformed/junk input.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-colour")]
    [InlineData("#GGG")]
    [InlineData("#12345")] // 5 digits — not a recognized length
    [InlineData("3F7FD9")] // missing '#'
    public void TryParseHex_JunkOrNull_ReturnsFalse(string? input)
    {
        var success = ColorDialogColor.TryParseHex(input, out var color);

        Assert.False(success);
        Assert.Equal(DrawingColor.Empty, color);
    }

    /// <summary>Verifies <see cref="ColorDialogColor.ToColorRef"/> produces the Win32 <c>0x00BBGGRR</c> layout.</summary>
    [Fact]
    public void ToColorRef_Azure_MatchesExpectedColorRef()
    {
        var color = DrawingColor.FromArgb(0x3F, 0x7F, 0xD9);

        Assert.Equal(0x00D97F3F, ColorDialogColor.ToColorRef(color));
    }

    /// <summary>Verifies <see cref="ColorDialogColor.ToHex"/> renders uppercase <c>#RRGGBB</c>.</summary>
    [Fact]
    public void ToHex_ReturnsUppercaseRrggbb()
    {
        var color = DrawingColor.FromArgb(0x3f, 0x7f, 0xd9);

        Assert.Equal("#3F7FD9", ColorDialogColor.ToHex(color));
    }

    /// <summary>Verifies more than 16 seed colours are truncated to 16.</summary>
    [Fact]
    public void BuildCustomColors_TruncatesToSixteen()
    {
        var hexColors = Enumerable.Range(0, 20).Select(i => $"#{i:X2}{i:X2}{i:X2}").ToList();

        var result = ColorDialogColor.BuildCustomColors(hexColors);

        Assert.Equal(16, result.Length);
    }

    /// <summary>Verifies invalid entries are skipped without counting against the 16-slot limit.</summary>
    [Fact]
    public void BuildCustomColors_SkipsInvalidEntries_WithoutCountingAgainstLimit()
    {
        var hexColors = new[] { "#3F7FD9", "not-a-colour", "#7A5BE7", null!, "#D95F59" };

        var result = ColorDialogColor.BuildCustomColors(hexColors);

        Assert.Equal(3, result.Length);
        Assert.Equal(ColorDialogColor.ToColorRef(DrawingColor.FromArgb(0x3F, 0x7F, 0xD9)), result[0]);
        Assert.Equal(ColorDialogColor.ToColorRef(DrawingColor.FromArgb(0x7A, 0x5B, 0xE7)), result[1]);
        Assert.Equal(ColorDialogColor.ToColorRef(DrawingColor.FromArgb(0xD9, 0x5F, 0x59)), result[2]);
    }

    /// <summary>Verifies an empty input list produces an empty array.</summary>
    [Fact]
    public void BuildCustomColors_EmptyList_ReturnsEmptyArray()
    {
        Assert.Empty(ColorDialogColor.BuildCustomColors(Array.Empty<string>()));
    }

    /// <summary>
    /// MINOR-2 fix (review round): a <see langword="null"/> list — <see cref="IColorPickerService.PickColor"/>'s
    /// own <c>customColors</c> parameter is non-nullable, but this is the boundary of an interface any
    /// caller can implement/mock — returns an empty array instead of throwing.
    /// </summary>
    [Fact]
    public void BuildCustomColors_NullList_ReturnsEmptyArray_DoesNotThrow()
    {
        var exception = Record.Exception(() => ColorDialogColor.BuildCustomColors(null));

        Assert.Null(exception);
        Assert.Empty(ColorDialogColor.BuildCustomColors(null));
    }
}
