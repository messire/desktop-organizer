using System.IO;
using System.Xml.Linq;

namespace DesktopOrganizer.Tests.Unit.Localization;

/// <summary>
/// Phase 6.0 of docs/PLANS/design-system-1x.md (T-5, "храповик"): a COUNT-ONLY-DECREASES gate over
/// "стены текста" (§1.2/§2.12) — RESX values over 100 characters in EITHER <c>Strings.resx</c> (English)
/// or <c>Strings.ru.resx</c> (Russian, which the plan's own §1.2 inventory notes is consistently longer for
/// the same key). §2.12's own EVENTUAL target (≤ 140 en / ≤ 170 ru for <c>*_Description</c>, ≤ 200/240 for
/// <c>*_Message*</c>, ≤ 160/190 for tooltips) is a 6.1+ concern once <c>design:ux-copy</c> actually shortens
/// these strings — THIS phase changes no RESX value (6.0's own DoD forbids it), so the gate here is the
/// simpler, uniform 100-character mark the plan's own §1.2 inventory already used to COUNT today's known
/// offenders (19 en / 30 ru at the plan's own HEAD snapshot, <c>4e7268b</c>; this repository's HEAD has since
/// moved on with unrelated backlog-1.x work that touched several of the same strings, so the baselines below
/// are re-measured against the CURRENT actual RESX content, not copied from the plan text — see this class's
/// own remarks for why that is the correct baseline for a ratchet).
/// </summary>
/// <remarks>
/// A ratchet's baseline must always be "what the repository actually contains right now", not a stale
/// number from the planning document that proposed it — copying the plan's own already-out-of-date count
/// here would make this gate immediately, spuriously red (or silently permissive) the moment it lands,
/// through no fault of any offending change. A future phase (6.1+) that shortens one of today's offenders
/// below 100 characters should shrink the corresponding baseline constant in the SAME commit.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Localization))]
public class ResxTextLengthGuardTests
{
    /// <summary>§1.2's own "wall of text" mark — shared by both locales' gates below.</summary>
    private const int LengthThreshold = 100;

    /// <summary>Current, known-debt baseline for <c>Strings.resx</c> (English) — see this class's own remarks.</summary>
    private const int NeutralBaseline = 17;

    /// <summary>Current, known-debt baseline for <c>Strings.ru.resx</c> (Russian) — see this class's own remarks.</summary>
    private const int RussianBaseline = 26;

    private static readonly Dictionary<string, string> Neutral = LoadResx("Strings.resx");
    private static readonly Dictionary<string, string> Russian = LoadResx("Strings.ru.resx");

    /// <summary>Fails if a NEW <c>Strings.resx</c> (English) value over <see cref="LengthThreshold"/> characters appears beyond <see cref="NeutralBaseline"/>.</summary>
    [Fact]
    public void KnownDebt_NeutralResxWallsOfText_DoesNotIncreaseBeyondBaseline()
    {
        AssertWithinBaseline(Neutral, "Strings.resx", NeutralBaseline);
    }

    /// <summary>Fails if a NEW <c>Strings.ru.resx</c> value over <see cref="LengthThreshold"/> characters appears beyond <see cref="RussianBaseline"/>.</summary>
    [Fact]
    public void KnownDebt_RussianResxWallsOfText_DoesNotIncreaseBeyondBaseline()
    {
        AssertWithinBaseline(Russian, "Strings.ru.resx", RussianBaseline);
    }

    // --- helpers -------------------------------------------------------------------------------------

    /// <summary>Asserts <paramref name="resx"/>'s over-<see cref="LengthThreshold"/> value count has not grown past <paramref name="baseline"/>, listing every offending key and its length on failure.</summary>
    private static void AssertWithinBaseline(Dictionary<string, string> resx, string fileLabel, int baseline)
    {
        var offenders = resx
            .Where(pair => pair.Value.Length > LengthThreshold)
            .OrderByDescending(pair => pair.Value.Length)
            .Select(pair => $"{pair.Key} ({pair.Value.Length} chars)")
            .ToList();

        var message = $"{fileLabel}: {offenders.Count} value(s) over {LengthThreshold} characters (baseline {baseline}) — docs/PLANS/design-system-1x.md T-5:\n" +
            string.Join('\n', offenders);
        Assert.True(offenders.Count <= baseline, message);
    }

    /// <summary>
    /// Same "read straight off disk" convention <see cref="ResxConsistencyTests"/>'s own identically-named
    /// private helper already uses — duplicated here (rather than shared) per this codebase's existing
    /// per-fixture convention for small, self-contained RESX-loading helpers.
    /// </summary>
    /// <param name="fileName">The RESX file name under <c>src/DesktopOrganizer.Localization</c>.</param>
    private static Dictionary<string, string> LoadResx(string fileName)
    {
        var repoRoot = RepoRootLocator.Find();

        var path = Path.Combine(repoRoot, "src", "DesktopOrganizer.Localization", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"RESX file not found at '{path}'.", path);

        var document = XDocument.Load(path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var data in document.Root!.Elements("data"))
        {
            var name = data.Attribute("name")?.Value;
            if (name is null) continue;

            result[name] = data.Element("value")?.Value ?? string.Empty;
        }

        return result;
    }
}
