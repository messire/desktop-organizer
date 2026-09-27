// "Color" is ambiguous between System.Drawing.Color and System.Windows.Media.Color — both are global
// usings in this project (GlobalUsings.cs). This file only ever deals with the WinForms/GDI colour type.
using Color = System.Drawing.Color;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Pure hex/<see cref="Color"/>/<c>COLORREF</c> conversions used by <see cref="ColorPickerService"/>
/// (docs/PLANS/color-presets-and-size-step.md §4.1). Kept separate from <see cref="ColorPickerService"/>
/// itself so this logic — unlike actually showing a modal dialog — is fully unit-testable.
/// </summary>
internal static class ColorDialogColor
{
    private const int MaxCustomColors = 16;

    /// <summary>
    /// Parses <paramref name="hex"/> as <c>#RGB</c>, <c>#RRGGBB</c>, or <c>#AARRGGBB</c> (alpha, if
    /// present, is ignored — the result is always fully opaque). Never throws.
    /// </summary>
    /// <param name="hex">The candidate hex colour string.</param>
    /// <param name="color">The parsed, fully opaque colour on success; <see cref="Color.Empty"/> otherwise.</param>
    public static bool TryParseHex(string? hex, out Color color)
    {
        color = Color.Empty;

        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var trimmed = hex.Trim();
        var hasRecognizedLength = trimmed.Length is 4 or 7 or 9;
        if (!hasRecognizedLength || trimmed[0] != '#')
            return false;

        var digits = trimmed[1..];
        foreach (var c in digits)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        byte r, g, b;
        switch (digits.Length)
        {
            case 3:
                r = ExpandNibble(digits[0]);
                g = ExpandNibble(digits[1]);
                b = ExpandNibble(digits[2]);
                break;
            case 6:
                r = ParseByte(digits, 0);
                g = ParseByte(digits, 2);
                b = ParseByte(digits, 4);
                break;
            case 8: // #AARRGGBB — alpha ignored, result is always opaque.
                r = ParseByte(digits, 2);
                g = ParseByte(digits, 4);
                b = ParseByte(digits, 6);
                break;
            default:
                return false;
        }

        color = Color.FromArgb(r, g, b);
        return true;
    }

    /// <summary>Converts <paramref name="color"/> to a Win32 <c>COLORREF</c> (<c>0x00BBGGRR</c>).</summary>
    /// <param name="color">The colour to convert.</param>
    public static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    /// <summary>Converts <paramref name="color"/> to canonical uppercase <c>#RRGGBB</c>.</summary>
    /// <param name="color">The colour to convert.</param>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Converts up to 16 hex colour strings into <c>COLORREF</c> values for
    /// <see cref="ColorDialog.CustomColors"/>. Entries beyond the 16th, and entries that fail to parse, are
    /// silently dropped (the latter without counting against the 16-slot limit). A <see langword="null"/>
    /// <paramref name="hexColors"/> (MINOR-2 fix, review round — <see cref="ColorPickerService"/>'s own
    /// interface parameter is non-nullable, but this is the boundary of an interface any caller can
    /// implement/mock) returns an empty array instead of throwing.
    /// </summary>
    /// <param name="hexColors">The candidate hex colour strings, in seed order.</param>
    public static int[] BuildCustomColors(IReadOnlyList<string>? hexColors)
    {
        if (hexColors is null)
            return Array.Empty<int>();

        var result = new List<int>(MaxCustomColors);

        foreach (var hex in hexColors)
        {
            if (result.Count >= MaxCustomColors)
                break;

            if (TryParseHex(hex, out var color))
                result.Add(ToColorRef(color));
        }

        return result.ToArray();
    }

    private static byte ExpandNibble(char c) => (byte)(HexNibbleValue(c) * 17);

    private static byte ParseByte(string digits, int index) =>
        (byte)((HexNibbleValue(digits[index]) << 4) | HexNibbleValue(digits[index + 1]));

    private static int HexNibbleValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => (c - 'a') + 10,
        >= 'A' and <= 'F' => (c - 'A') + 10,
        _ => throw new FormatException($"'{c}' is not a hex digit."),
    };
}
