using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Unit tests for <see cref="DiagnosticsLogFileNaming"/>. Pure/allocation-light — no file-system
/// access is exercised here; see <c>JsonLinesDiagnosticsLogTests</c> for the integration-level
/// coverage of retention actually deleting files on disk.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class DiagnosticsLogFileNamingTests
{
    // ── BuildFileName — shape ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.BuildFileName"/> produces the exact expected
    /// <c>gestures-yyyyMMdd-HHmmss-pid.jsonl</c> shape for a fixed timestamp and process id.
    /// </summary>
    [Fact]
    public void BuildFileName_ProducesExpectedShape()
    {
        var now = new DateTimeOffset(2026, 9, 17, 10, 15, 0, TimeSpan.Zero);

        var fileName = DiagnosticsLogFileNaming.BuildFileName(now, 12345);

        Assert.Equal("gestures-20260917-101500-12345.jsonl", fileName);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.BuildFileName"/> always starts with the
    /// <c>gestures-</c> prefix and ends with the <c>.jsonl</c> extension.
    /// </summary>
    [Fact]
    public void BuildFileName_StartsWithPrefixAndEndsWithExtension()
    {
        var fileName = DiagnosticsLogFileNaming.BuildFileName(DateTimeOffset.Now, Environment.ProcessId);

        Assert.StartsWith("gestures-", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".jsonl", fileName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.BuildFileName"/> produces distinct file names
    /// for two different process ids sharing the same timestamp.
    /// </summary>
    [Fact]
    public void BuildFileName_DifferentProcessIds_ProduceDistinctNames_ForSameTimestamp()
    {
        var now = new DateTimeOffset(2026, 9, 17, 10, 15, 0, TimeSpan.Zero);

        var first = DiagnosticsLogFileNaming.BuildFileName(now, 111);
        var second = DiagnosticsLogFileNaming.BuildFileName(now, 222);

        Assert.NotEqual(first, second);
    }

    // ── BuildFileName — sortability ─────────────────────────────────────────

    /// <summary>
    /// Verifies that ordinal string sorting of file names produced by
    /// <see cref="DiagnosticsLogFileNaming.BuildFileName"/> matches their chronological order, which
    /// <c>SelectForDeletion</c> relies on instead of parsing timestamps back out.
    /// </summary>
    [Fact]
    public void BuildFileName_OrdinalSort_MatchesChronologicalOrder()
    {
        // The fixed-width yyyyMMdd-HHmmss prefix must sort the same way ordinally as it does
        // chronologically — SelectForDeletion relies on this to avoid parsing timestamps back out.
        var earlier = DiagnosticsLogFileNaming.BuildFileName(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1);
        var later = DiagnosticsLogFileNaming.BuildFileName(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero), 1);
        var muchLater = DiagnosticsLogFileNaming.BuildFileName(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 1);

        var ordinalSorted = new[] { muchLater, earlier, later }
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([earlier, later, muchLater], ordinalSorted);
    }

    /// <summary>
    /// Verifies that ordinal sort order still holds for file names produced by
    /// <see cref="DiagnosticsLogFileNaming.BuildFileName"/> across a year boundary.
    /// </summary>
    [Fact]
    public void BuildFileName_CrossesYearBoundary_StillSortsChronologically()
    {
        var endOfYear = DiagnosticsLogFileNaming.BuildFileName(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero), 1);
        var startOfNextYear = DiagnosticsLogFileNaming.BuildFileName(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), 1);

        Assert.True(string.CompareOrdinal(endOfYear, startOfNextYear) < 0);
    }

    // ── SelectForDeletion — retention ───────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> selects only the oldest
    /// files beyond the keep count for deletion.
    /// </summary>
    [Fact]
    public void SelectForDeletion_MoreFilesThanKeep_DeletesOldestOnly()
    {
        var files = new[]
        {
            "gestures-20260101-000000-1.jsonl",
            "gestures-20260102-000000-1.jsonl",
            "gestures-20260103-000000-1.jsonl",
            "gestures-20260104-000000-1.jsonl",
        };

        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(files, keep: 2);

        Assert.Equal(
            ["gestures-20260101-000000-1.jsonl", "gestures-20260102-000000-1.jsonl"],
            toDelete);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> returns an empty result
    /// when there are fewer files than the keep count.
    /// </summary>
    [Fact]
    public void SelectForDeletion_FewerFilesThanKeep_ReturnsEmpty()
    {
        var files = new[] { "gestures-20260101-000000-1.jsonl", "gestures-20260102-000000-1.jsonl" };

        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(files, keep: 10);

        Assert.Empty(toDelete);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> returns an empty result
    /// when the file count exactly equals the keep count.
    /// </summary>
    [Fact]
    public void SelectForDeletion_ExactlyKeepCount_ReturnsEmpty()
    {
        var files = new[] { "gestures-20260101-000000-1.jsonl", "gestures-20260102-000000-1.jsonl" };

        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(files, keep: 2);

        Assert.Empty(toDelete);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> returns an empty result
    /// for an empty input file list.
    /// </summary>
    [Fact]
    public void SelectForDeletion_EmptyInput_ReturnsEmpty()
    {
        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(Array.Empty<string>(), keep: 5);

        Assert.Empty(toDelete);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> treats a negative keep
    /// count as zero, deleting every file.
    /// </summary>
    [Fact]
    public void SelectForDeletion_NegativeKeep_TreatedAsZero_DeletesEverything()
    {
        var files = new[] { "gestures-20260101-000000-1.jsonl", "gestures-20260102-000000-1.jsonl" };

        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(files, keep: -3);

        Assert.Equal(files.OrderBy(f => f, StringComparer.Ordinal), toDelete);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> with an explicit keep
    /// count of zero deletes every file.
    /// </summary>
    [Fact]
    public void SelectForDeletion_KeepZero_DeletesEverything()
    {
        var files = new[] { "gestures-20260101-000000-1.jsonl", "gestures-20260102-000000-1.jsonl" };

        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(files, keep: 0);

        Assert.Equal(2, toDelete.Count);
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> throws
    /// <see cref="ArgumentNullException"/> for a <see langword="null"/> file list.
    /// </summary>
    [Fact]
    public void SelectForDeletion_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticsLogFileNaming.SelectForDeletion(null!, keep: 1));
    }

    /// <summary>
    /// Verifies that <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> sorts by the file name
    /// portion only, so full paths sort identically to bare file names.
    /// </summary>
    [Fact]
    public void SelectForDeletion_UsesOnlyFileNamePortion_FullPathsSortTheSameAsBareNames()
    {
        var files = new[]
        {
            @"C:\Users\someone\AppData\Local\DesktopOrganizer\Logs\gestures-20260101-000000-1.jsonl",
            @"C:\Users\someone\AppData\Local\DesktopOrganizer\Logs\gestures-20260103-000000-1.jsonl",
            @"C:\Users\someone\AppData\Local\DesktopOrganizer\Logs\gestures-20260102-000000-1.jsonl",
        };

        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(files, keep: 2);

        Assert.Equal(
            [@"C:\Users\someone\AppData\Local\DesktopOrganizer\Logs\gestures-20260101-000000-1.jsonl"],
            toDelete);
    }

    /// <summary>
    /// <see cref="DiagnosticsLogFileNaming.SelectForDeletion"/> does no pattern filtering of its own —
    /// callers (e.g. <c>JsonLinesDiagnosticsLog</c>) are expected to pass only files already matched
    /// against the <c>gestures-*.jsonl</c> glob. A foreign file name participates in the plain ordinal
    /// sort like any other entry rather than being specially skipped.
    /// </summary>
    [Fact]
    public void SelectForDeletion_ForeignFileName_ParticipatesInOrdinalSortLikeAnyOtherEntry()
    {
        var files = new[]
        {
            "gestures-20260101-000000-1.jsonl",
            "gestures-20260103-000000-1.jsonl",
            "not-a-diagnostics-file.txt", // sorts before "gestures-..." ordinally ('n' < 'g'? actually 'n' > 'g')
        };

        var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(files, keep: 2);

        // Ordinal order: "gestures-20260101...", "gestures-20260103...", "not-a-diagnostics-file.txt"
        // (digits/letters compare by code point; 'g' < 'n'). Keeping the newest 2 deletes the oldest one.
        Assert.Equal(["gestures-20260101-000000-1.jsonl"], toDelete);
    }
}
