using System.Globalization;

namespace DesktopOrganizer.UI.Models;

/// <summary>Why <see cref="IntegerInputParser.TryParse"/> could not produce a value.</summary>
public enum IntegerInputError
{
    /// <summary>Parsing succeeded; <c>value</c> is valid.</summary>
    None,

    /// <summary>The input was <see langword="null"/>, empty, or whitespace-only.</summary>
    Empty,

    /// <summary>The input was not a plain non-negative ASCII-digit integer, or it overflowed <see cref="int"/>.</summary>
    NotAnInteger,

    /// <summary>The input parsed to an integer, but it falls outside the caller-supplied range.</summary>
    OutOfRange,
}

/// <summary>
/// Pure, WPF-free parsing/formatting helpers for <c>IntegerInputBox</c> (docs/PLANS/color-presets-and-size-step.md
/// §3.3). Deliberately narrower than <see cref="int.TryParse(string?, out int)"/>: no sign, no thousands
/// separator, no decimal point, no exponent, no non-ASCII digit — a size input box only ever needs a plain
/// small non-negative whole number.
/// </summary>
public static class IntegerInputParser
{
    /// <summary>
    /// Parses <paramref name="text"/> as a plain non-negative ASCII-digit integer (leading/trailing
    /// whitespace is trimmed first; leading zeros are allowed) and checks it against
    /// <c>[<paramref name="minimum"/>, <paramref name="maximum"/>]</c>. Never throws.
    /// </summary>
    /// <param name="text">The raw text to parse.</param>
    /// <param name="minimum">Inclusive lower bound of the accepted range.</param>
    /// <param name="maximum">Inclusive upper bound of the accepted range.</param>
    /// <param name="value">The parsed value on <see cref="IntegerInputError.None"/>; <c>0</c> otherwise.</param>
    public static IntegerInputError TryParse(string? text, int minimum, int maximum, out int value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(text))
            return IntegerInputError.Empty;

        var trimmed = text.Trim();

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return IntegerInputError.NotAnInteger;

        if (parsed < minimum || parsed > maximum)
            return IntegerInputError.OutOfRange;

        value = parsed;
        return IntegerInputError.None;
    }

    /// <summary>
    /// <see langword="true"/> when every character of <paramref name="chunk"/> is an ASCII digit
    /// (<c>'0'</c>-<c>'9'</c>) and <paramref name="chunk"/> is non-empty. Used to reject a
    /// <c>PreviewTextInput</c>/paste fragment before it ever reaches the <c>TextBox</c>'s text.
    /// </summary>
    /// <param name="chunk">The candidate input fragment (a single keystroke or a pasted string).</param>
    public static bool IsAllowedInputChunk(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return false;

        foreach (var c in chunk)
        {
            if (c < '0' || c > '9')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Formats <paramref name="value"/> for display: rounds to the nearest integer (ties away from zero)
    /// and renders it with <see cref="CultureInfo.InvariantCulture"/> (no thousands separator, regardless
    /// of the current UI culture). <c>NaN</c>/±<c>Infinity</c> map to <see cref="string.Empty"/>.
    /// </summary>
    /// <param name="value">The value to format — typically a size property that may hold a legacy fractional value.</param>
    public static string FormatForDisplay(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return string.Empty;

        return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }
}
