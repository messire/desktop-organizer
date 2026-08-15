namespace DesktopOrganizer.Core.Models;

/// <summary>
/// Natural-order (a.k.a. "alphanumeric") string comparer: digit runs are compared as numbers of
/// arbitrary length, so <c>"file2"</c> sorts before <c>"file10"</c> — unlike a plain ordinal or
/// culture comparison, which would put <c>"file10"</c> first because it compares <c>'1'</c> against
/// <c>'2'</c> character-by-character.
/// </summary>
/// <remarks>
/// Pure, file-system-free, allocation-light (segment boundaries are located with
/// <see cref="ReadOnlySpan{T}"/> slices, no per-call substring allocation) and independent of any
/// P/Invoke — see ADR-014 Decision 4: a native <c>StrCmpLogicalW</c> call would give byte-for-byte
/// Explorer parity, but this project's invariant confines P/Invoke to <c>INativeApi</c>, and this
/// comparer runs O(n log n) times per panel rebuild and must stay a plain, unit-testable function.
/// <para>
/// Segmentation: each input is walked left-to-right and split into consecutive runs of digit
/// characters and runs of non-digit characters (per <see cref="char.IsDigit(char)"/>), with each
/// string's own run boundaries found independently (a run never spans a digit/non-digit switch, but
/// nothing requires the two inputs' runs to line up in "shape"). The two runs found at the same
/// position are then compared as:
/// <list type="bullet">
/// <item>both digit runs: as numbers of arbitrary length, ignoring leading zeros (never parsed into
/// a bounded numeric type, so no overflow regardless of digit count) — see
/// <see cref="CompareNumericRuns"/>;</item>
/// <item>both non-digit runs: <see cref="StringComparison.CurrentCultureIgnoreCase"/>;</item>
/// <item>one digit run / one non-digit run (the two inputs diverge in "shape" at that position):
/// falls back to comparing the raw runs with <see cref="StringComparison.CurrentCultureIgnoreCase"/>
/// — not natural-order-meaningful, but still deterministic, so the comparer stays a total order
/// instead of throwing or special-casing.</item>
/// </list>
/// </para>
/// <para>
/// <see langword="null"/> and <see cref="string.Empty"/> are treated identically: both compare as
/// "less than" any non-empty string, and equal to each other. Panel item names are never actually
/// <see langword="null"/> in practice, but the comparer accepts it so callers don't need a
/// null-check first.
/// </para>
/// </remarks>
public sealed class NaturalStringComparer : IComparer<string?>
{
    /// <summary>
    /// Shared stateless instance. This comparer holds no per-call/per-instance state, so a single
    /// instance is reused everywhere instead of allocating one per sort.
    /// </summary>
    public static readonly NaturalStringComparer Instance = new();

    private NaturalStringComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (string.IsNullOrEmpty(x) && string.IsNullOrEmpty(y)) return 0;
        if (string.IsNullOrEmpty(x)) return -1;
        if (string.IsNullOrEmpty(y)) return 1;

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            var xIsDigit = char.IsDigit(x[i]);
            var yIsDigit = char.IsDigit(y[j]);

            var xStart = i;
            while (i < x.Length && char.IsDigit(x[i]) == xIsDigit) i++;
            var xRun = x.AsSpan(xStart, i - xStart);

            var yStart = j;
            while (j < y.Length && char.IsDigit(y[j]) == yIsDigit) j++;
            var yRun = y.AsSpan(yStart, j - yStart);

            var runCmp = xIsDigit && yIsDigit
                ? CompareNumericRuns(xRun, yRun)
                : xRun.CompareTo(yRun, StringComparison.CurrentCultureIgnoreCase);

            if (runCmp != 0) return runCmp;
        }

        // The common prefix matched entirely on both sides (run by run); whichever string still has
        // leftover characters sorts after the other — the same rule a plain prefix comparison uses.
        return (x.Length - i).CompareTo(y.Length - j);
    }

    /// <summary>
    /// Compares two runs of digit characters as numbers of arbitrary length: magnitude first
    /// (ignoring leading zeros — <c>"007"</c> and <c>"7"</c> have the same magnitude), then, only if
    /// the magnitudes are equal, a tie-break on the original (untrimmed) run length. The tie-break
    /// keeps the comparer total for inputs that differ only in leading zeros — e.g. <c>"01"</c> vs
    /// <c>"1"</c>: equal magnitude, so the shorter original run (<c>"1"</c>) sorts first.
    /// </summary>
    private static int CompareNumericRuns(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        var xTrimmed = TrimLeadingZeros(x);
        var yTrimmed = TrimLeadingZeros(y);

        // Same-length digit-only spans compare correctly with a plain ordinal comparison, because
        // '0'..'9' are contiguous and monotonically increasing — this is what lets magnitude be
        // compared without ever parsing the run into a bounded numeric type (so arbitrarily long
        // digit runs, e.g. 30+ digits, never overflow).
        if (xTrimmed.Length != yTrimmed.Length) return xTrimmed.Length.CompareTo(yTrimmed.Length);

        var magnitudeCmp = xTrimmed.CompareTo(yTrimmed, StringComparison.Ordinal);
        return magnitudeCmp != 0 ? magnitudeCmp : x.Length.CompareTo(y.Length);
    }

    /// <summary>
    /// Strips leading <c>'0'</c> characters, always leaving at least one character behind (so
    /// <c>"000"</c> trims to <c>"0"</c>, not the empty span).
    /// </summary>
    private static ReadOnlySpan<char> TrimLeadingZeros(ReadOnlySpan<char> digits)
    {
        var start = 0;
        while (start < digits.Length - 1 && digits[start] == '0') start++;
        return digits[start..];
    }
}
