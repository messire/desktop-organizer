using DesktopOrganizer.UI.Services;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PageCycling.Next"/> — the pure decision function behind the tray icon's
/// double-click round-robin page switch (backlog-1x-master-plan.md §1.3, ADR-044).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.UI))]
public class PageCyclingTests
{
    /// <summary>Verifies that three pages cycle a → b → c → a.</summary>
    /// <param name="current">The currently active page passed to <see cref="PageCycling.Next"/>.</param>
    /// <param name="expected">The page <see cref="PageCycling.Next"/> is expected to return.</param>
    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "c")]
    [InlineData("c", "a")]
    public void Next_ThreePages_CyclesInOrder_AndWrapsFromLastToFirst(string current, string expected)
    {
        var result = PageCycling.Next(["a", "b", "c"], current);

        Assert.Equal(expected, result);
    }

    /// <summary>A single page has nothing to cycle to — the tray's double-click gesture must no-op.</summary>
    [Fact]
    public void Next_SinglePage_ReturnsNull()
    {
        var result = PageCycling.Next(["default"], "default");

        Assert.Null(result);
    }

    /// <summary>An empty page list has nothing to cycle to either.</summary>
    [Fact]
    public void Next_EmptyList_ReturnsNull()
    {
        var result = PageCycling.Next([], "default");

        Assert.Null(result);
    }

    /// <summary>
    /// When <c>current</c> is not present in <c>pages</c> at all (e.g. a stale/deleted page id), the
    /// first page is chosen rather than throwing or returning <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Next_CurrentNotInList_ReturnsFirstPage()
    {
        var result = PageCycling.Next(["a", "b", "c"], "unknown-stale-id");

        Assert.Equal("a", result);
    }

    /// <summary>
    /// Page identifiers are opaque ids, compared ordinally — a differently-cased id is treated as
    /// "not present", not folded to the same entry.
    /// </summary>
    [Fact]
    public void Next_CurrentDiffersOnlyByCase_IsTreatedAsNotPresent_ReturnsFirstPage()
    {
        var result = PageCycling.Next(["a", "b"], "A");

        Assert.Equal("a", result);
    }

    /// <summary>Blank/empty entries in <c>pages</c> are skipped rather than treated as real pages.</summary>
    [Fact]
    public void Next_BlankEntriesInList_AreSkipped()
    {
        var result = PageCycling.Next(["a", string.Empty, "b"], "a");

        Assert.Equal("b", result);
    }

    /// <summary>Two real pages plus a blank entry is still only two pages to cycle between.</summary>
    [Fact]
    public void Next_OnlyOneNonBlankPage_ReturnsNull()
    {
        var result = PageCycling.Next(["a", string.Empty], "a");

        Assert.Null(result);
    }

    /// <summary>A <see langword="null"/> <c>pages</c> argument is rejected rather than NRE-ing internally.</summary>
    [Fact]
    public void Next_NullPages_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PageCycling.Next(null!, "a"));
    }

    /// <summary>A <see langword="null"/> <c>current</c> argument is rejected rather than NRE-ing internally.</summary>
    [Fact]
    public void Next_NullCurrent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PageCycling.Next(["a"], null!));
    }
}
