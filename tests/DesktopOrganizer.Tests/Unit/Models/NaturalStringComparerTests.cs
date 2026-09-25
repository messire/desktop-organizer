using DesktopOrganizer.Core;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="NaturalStringComparer"/> (ADR-014 Decision 4): natural-order string
/// comparison where digit runs compare as numbers of arbitrary length rather than character by
/// character.
/// </summary>
[Trait(UnitTests, nameof(Core))]
public class NaturalStringComparerTests
{
    /// <summary>
    /// Supplies a representative set of inputs (nulls, empty, purely numeric, leading-zero,
    /// mixed-case, and mixed text/digit names) used by the totality theories below.
    /// </summary>
    /// <returns>A single row wrapping the representative <c>string?[]</c> set.</returns>
    public static IEnumerable<object[]> RepresentativeStrings()
    {
        yield return new object[]
        {
            new[]
            {
                null, string.Empty, "0", "1", "01", "2", "9", "10", "10a", "a", "A", "a1", "a2",
                "a10", "file2", "file10", "File2", "File10", "b", "z", "Z",
            },
        };
    }

    // ── Digit runs compared as numbers ──────────────────────────────────────

    /// <summary>
    /// Verifies that a shorter digit run ("2") sorts before a longer one ("10") when the numeric
    /// magnitude is smaller, in both comparison directions.
    /// </summary>
    [Fact]
    public void Compare_File2VsFile10_File2SortsFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("file2", "file10") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("file10", "file2") > 0);
    }

    /// <summary>
    /// Verifies that when two strings share an identical text/digit prefix and only diverge in the
    /// trailing digit run, the comparison still follows numeric magnitude rather than character order.
    /// </summary>
    [Fact]
    public void Compare_A1B2VsA1B10_A1B2SortsFirst()
    {
        // Equal non-digit/digit prefix ("a1b"), diverging only in the trailing digit run.
        Assert.True(NaturalStringComparer.Instance.Compare("a1b2", "a1b10") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a1b10", "a1b2") > 0);
    }

    /// <summary>
    /// Verifies that purely numeric names ("10" vs "9") compare by numeric magnitude, not by the
    /// lexical order that would rank "10" before "9".
    /// </summary>
    [Fact]
    public void Compare_PurelyNumericNames_ComparedByMagnitudeNotLexically()
    {
        // Lexically "10" < "9" (both share nothing, '1' < '9'); numerically 10 > 9.
        Assert.True(NaturalStringComparer.Instance.Compare("10", "9") > 0);
        Assert.True(NaturalStringComparer.Instance.Compare("9", "10") < 0);
    }

    // ── Leading zeros ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that "1" and "01", which share the same numeric magnitude but not the same original
    /// length, are neither treated as unrelated numbers nor reported as equal — the shorter run
    /// ("1") sorts first as the documented length tie-break.
    /// </summary>
    [Fact]
    public void Compare_LeadingZeros_SameMagnitudeButNotEqual_TieBreaksByOriginalLength()
    {
        // "01" and "1" have the same numeric magnitude, so the comparer isn't allowed to treat them
        // as unrelated numbers apart by 1 — but it also must not report them as equal (that would
        // break totality once combined with a third value distinguishing them only by string form).
        // The documented tie-break is by original run length: the shorter run ("1") sorts first.
        Assert.True(NaturalStringComparer.Instance.Compare("1", "01") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("01", "1") > 0);
    }

    /// <summary>
    /// Verifies that magnitude is compared before the length tie-break: "007" (magnitude 7) still
    /// sorts before "10" (magnitude 10) even though "007" is the longer original run.
    /// </summary>
    [Fact]
    public void Compare_LeadingZeros_MagnitudeStillDominatesOverLength()
    {
        // "007" (magnitude 7) must still sort before "10" (magnitude 10), even though "007" is the
        // longer original run — magnitude is compared before the length tie-break kicks in.
        Assert.True(NaturalStringComparer.Instance.Compare("007", "10") < 0);
    }

    // ── Case-insensitivity ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that strings differing only in letter case compare as equal.
    /// </summary>
    [Fact]
    public void Compare_CaseOnlyDifference_ComparesEqual()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("A", "a"));
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("File", "file"));
    }

    /// <summary>
    /// Verifies that case-insensitive text combined with a numeric suffix ("File2" vs "file10")
    /// still orders naturally by magnitude rather than lexically.
    /// </summary>
    [Fact]
    public void Compare_CaseInsensitiveTextWithNumericSuffix_StillNaturalOrders()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("File2", "file10") < 0);
    }

    // ── Null / empty ─────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that comparing two null strings returns zero.
    /// </summary>
    [Fact]
    public void Compare_BothNull_ReturnsZero()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
    }

    /// <summary>
    /// Verifies that null and an empty string compare as equal, in both argument orders.
    /// </summary>
    [Fact]
    public void Compare_NullAndEmpty_ReturnsZero()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, string.Empty));
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(string.Empty, null));
    }

    /// <summary>
    /// Verifies that null sorts before a non-empty string, in both comparison directions.
    /// </summary>
    [Fact]
    public void Compare_NullVsNonEmpty_NullSortsFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
    }

    /// <summary>
    /// Verifies that an empty string sorts before a non-empty string, in both comparison directions.
    /// </summary>
    [Fact]
    public void Compare_EmptyVsNonEmpty_EmptySortsFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(string.Empty, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", string.Empty) > 0);
    }

    // ── Very long numeric fragments (no int/long overflow) ──────────────────

    /// <summary>
    /// Verifies that 35-digit numeric runs, far beyond <see cref="long.MaxValue"/>, compare by
    /// magnitude without throwing or wrapping around.
    /// </summary>
    [Fact]
    public void Compare_VeryLongDigitRuns_ComparedByMagnitudeWithoutOverflow()
    {
        // 35-digit numbers, far beyond long.MaxValue (19-20 digits) — must not throw or wrap around,
        // and must still order by magnitude.
        var smaller = "1" + new string('0', 34); // 10^34
        var larger = "9" + new string('0', 34); // 9 * 10^34

        Assert.True(NaturalStringComparer.Instance.Compare(smaller, larger) < 0);
        Assert.True(NaturalStringComparer.Instance.Compare(larger, smaller) > 0);
    }

    /// <summary>
    /// Verifies that two very long digit runs differing only in their last digit still compare
    /// correctly by magnitude.
    /// </summary>
    [Fact]
    public void Compare_VeryLongDigitRuns_DifferingOnlyInLastDigit_ComparedCorrectly()
    {
        var digits = new string('1', 30);
        var lower = digits + "1";
        var higher = digits + "2";

        Assert.True(NaturalStringComparer.Instance.Compare(lower, higher) < 0);
        Assert.True(NaturalStringComparer.Instance.Compare(higher, lower) > 0);
    }

    /// <summary>
    /// Verifies that leading zeros on a very long digit run are trimmed before the magnitude
    /// comparison, so a 34-character run of zeros followed by "1" still equals magnitude 1.
    /// </summary>
    [Fact]
    public void Compare_VeryLongDigitRunsWithLeadingZeros_TrimmedBeforeMagnitudeCompare()
    {
        var withLeadingZeros = "00000000000000000000000000000001"; // 34 chars, magnitude 1
        var plain = "1";

        Assert.True(NaturalStringComparer.Instance.Compare(plain, withLeadingZeros) < 0);
        Assert.True(NaturalStringComparer.Instance.Compare(withLeadingZeros, plain) > 0);
    }

    // ── Totality: antisymmetry and transitivity over a representative set ───

    /// <summary>
    /// Verifies antisymmetry: for every pair drawn from <see cref="RepresentativeStrings"/>, the sign
    /// of <c>Compare(a, b)</c> is the negation of the sign of <c>Compare(b, a)</c>.
    /// </summary>
    /// <param name="values">The representative set of strings to compare pairwise.</param>
    [Theory]
    [MemberData(nameof(RepresentativeStrings))]
    public void Compare_Antisymmetric_ForRepresentativeSet(string?[] values)
    {
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                var ab = Sign(NaturalStringComparer.Instance.Compare(a, b));
                var ba = Sign(NaturalStringComparer.Instance.Compare(b, a));
                Assert.Equal(ab, -ba);
            }
        }
    }

    /// <summary>
    /// Verifies transitivity: for every triple drawn from <see cref="RepresentativeStrings"/>, if
    /// <c>a &lt;= b</c> and <c>b &lt;= c</c> then <c>a &lt;= c</c> (and symmetrically for <c>&gt;=</c>).
    /// </summary>
    /// <param name="values">The representative set of strings to compare in every triple combination.</param>
    [Theory]
    [MemberData(nameof(RepresentativeStrings))]
    public void Compare_Transitive_ForRepresentativeSet(string?[] values)
    {
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                foreach (var c in values)
                {
                    var ab = NaturalStringComparer.Instance.Compare(a, b);
                    var bc = NaturalStringComparer.Instance.Compare(b, c);
                    var ac = NaturalStringComparer.Instance.Compare(a, c);

                    if (ab <= 0 && bc <= 0) Assert.True(ac <= 0, $"Expected {a ?? "null"} <= {c ?? "null"} given {a ?? "null"} <= {b ?? "null"} <= {c ?? "null"}");
                    if (ab >= 0 && bc >= 0) Assert.True(ac >= 0, $"Expected {a ?? "null"} >= {c ?? "null"} given {a ?? "null"} >= {b ?? "null"} >= {c ?? "null"}");
                }
            }
        }
    }

    /// <summary>
    /// Verifies reflexivity: every value drawn from <see cref="RepresentativeStrings"/> compares as
    /// equal to itself.
    /// </summary>
    /// <param name="values">The representative set of strings, each compared against itself.</param>
    [Theory]
    [MemberData(nameof(RepresentativeStrings))]
    public void Compare_Reflexive_ForRepresentativeSet(string?[] values)
    {
        foreach (var a in values)
        {
            Assert.Equal(0, NaturalStringComparer.Instance.Compare(a, a));
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static int Sign(int value) => Math.Sign(value);
}
