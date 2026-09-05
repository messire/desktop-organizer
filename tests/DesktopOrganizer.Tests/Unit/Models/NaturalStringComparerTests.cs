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
    private static int Sign(int value) => Math.Sign(value);

    // ── Digit runs compared as numbers ──────────────────────────────────────

    [Fact]
    public void Compare_File2VsFile10_File2SortsFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("file2", "file10") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("file10", "file2") > 0);
    }

    [Fact]
    public void Compare_A1B2VsA1B10_A1B2SortsFirst()
    {
        // Equal non-digit/digit prefix ("a1b"), diverging only in the trailing digit run.
        Assert.True(NaturalStringComparer.Instance.Compare("a1b2", "a1b10") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a1b10", "a1b2") > 0);
    }

    [Fact]
    public void Compare_PurelyNumericNames_ComparedByMagnitudeNotLexically()
    {
        // Lexically "10" < "9" (both share nothing, '1' < '9'); numerically 10 > 9.
        Assert.True(NaturalStringComparer.Instance.Compare("10", "9") > 0);
        Assert.True(NaturalStringComparer.Instance.Compare("9", "10") < 0);
    }

    // ── Leading zeros ────────────────────────────────────────────────────────

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

    [Fact]
    public void Compare_LeadingZeros_MagnitudeStillDominatesOverLength()
    {
        // "007" (magnitude 7) must still sort before "10" (magnitude 10), even though "007" is the
        // longer original run — magnitude is compared before the length tie-break kicks in.
        Assert.True(NaturalStringComparer.Instance.Compare("007", "10") < 0);
    }

    // ── Case-insensitivity ───────────────────────────────────────────────────

    [Fact]
    public void Compare_CaseOnlyDifference_ComparesEqual()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("A", "a"));
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("File", "file"));
    }

    [Fact]
    public void Compare_CaseInsensitiveTextWithNumericSuffix_StillNaturalOrders()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("File2", "file10") < 0);
    }

    // ── Null / empty ─────────────────────────────────────────────────────────

    [Fact]
    public void Compare_BothNull_ReturnsZero()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
    }

    [Fact]
    public void Compare_NullAndEmpty_ReturnsZero()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, string.Empty));
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(string.Empty, null));
    }

    [Fact]
    public void Compare_NullVsNonEmpty_NullSortsFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
    }

    [Fact]
    public void Compare_EmptyVsNonEmpty_EmptySortsFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(string.Empty, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", string.Empty) > 0);
    }

    // ── Very long numeric fragments (no int/long overflow) ──────────────────

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

    [Fact]
    public void Compare_VeryLongDigitRuns_DifferingOnlyInLastDigit_ComparedCorrectly()
    {
        var digits = new string('1', 30);
        var lower = digits + "1";
        var higher = digits + "2";

        Assert.True(NaturalStringComparer.Instance.Compare(lower, higher) < 0);
        Assert.True(NaturalStringComparer.Instance.Compare(higher, lower) > 0);
    }

    [Fact]
    public void Compare_VeryLongDigitRunsWithLeadingZeros_TrimmedBeforeMagnitudeCompare()
    {
        var withLeadingZeros = "00000000000000000000000000000001"; // 34 chars, magnitude 1
        var plain = "1";

        Assert.True(NaturalStringComparer.Instance.Compare(plain, withLeadingZeros) < 0);
        Assert.True(NaturalStringComparer.Instance.Compare(withLeadingZeros, plain) > 0);
    }

    // ── Totality: antisymmetry and transitivity over a representative set ───

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

    [Theory]
    [MemberData(nameof(RepresentativeStrings))]
    public void Compare_Reflexive_ForRepresentativeSet(string?[] values)
    {
        foreach (var a in values)
        {
            Assert.Equal(0, NaturalStringComparer.Instance.Compare(a, a));
        }
    }
}
