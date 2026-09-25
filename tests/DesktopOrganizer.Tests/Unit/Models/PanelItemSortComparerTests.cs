using System.IO;
using DesktopOrganizer.Core;

namespace DesktopOrganizer.Tests.Unit.Models;

/// <summary>
/// Unit tests for <see cref="PanelItemSortComparer.ByName"/> (ADR-014 Decision 4): directories
/// before files, then <see cref="FileItem.DisplayName"/> via <see cref="NaturalStringComparer"/>,
/// then <see cref="FileItem.Path"/> as a total-order tie-break.
/// </summary>
[Trait(UnitTests, nameof(Core))]
public class PanelItemSortComparerTests
{
    // ── Directories before files ────────────────────────────────────────────

    /// <summary>
    /// Verifies that a directory always sorts before a file, even when the directory's display name
    /// is alphabetically later than the file's.
    /// </summary>
    [Fact]
    public void Compare_DirectoryVsFile_DirectorySortsFirst_EvenWhenAlphabeticallyLater()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"DOrgTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // The directory's name ("ZZZ_dir") is alphabetically AFTER the file's name
            // ("AAA_file.txt") — directory-first must still win over name order.
            var directoryItem = new FileItem(dir, null, displayName: "ZZZ_dir");
            var fileItem = new FileItem(@"C:\Somewhere\AAA_file.txt", null);

            Assert.True(PanelItemSortComparer.ByName.Compare(directoryItem, fileItem) < 0);
            Assert.True(PanelItemSortComparer.ByName.Compare(fileItem, directoryItem) > 0);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    /// <summary>
    /// Verifies that comparing two directories falls through to natural display-name order once the
    /// directory-vs-file rule no longer distinguishes them.
    /// </summary>
    [Fact]
    public void Compare_TwoDirectories_FallsThroughToNameOrder()
    {
        var dirA = Path.Combine(Path.GetTempPath(), $"DOrgTest_{Guid.NewGuid():N}");
        var dirB = Path.Combine(Path.GetTempPath(), $"DOrgTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            var itemA = new FileItem(dirA, null, displayName: "Alpha");
            var itemB = new FileItem(dirB, null, displayName: "Beta");

            Assert.True(PanelItemSortComparer.ByName.Compare(itemA, itemB) < 0);
        }
        finally
        {
            Directory.Delete(dirA);
            Directory.Delete(dirB);
        }
    }

    // ── Tie-break by Path when DisplayName matches ──────────────────────────

    /// <summary>
    /// Verifies that two items sharing the same display name but different paths are ordered
    /// deterministically by <see cref="FileItem.Path"/> (ordinal, case-insensitive), and that
    /// reversing the comparison flips the sign exactly.
    /// </summary>
    [Fact]
    public void Compare_SameDisplayName_TieBreaksByPath_Deterministically()
    {
        var itemA = new FileItem(@"C:\PanelA\Guid1\Report.docx", null, displayName: "Report.docx");
        var itemB = new FileItem(@"C:\PanelA\Guid2\Report.docx", null, displayName: "Report.docx");

        var cmp = PanelItemSortComparer.ByName.Compare(itemA, itemB);
        Assert.NotEqual(0, cmp);

        // Path tie-break is OrdinalIgnoreCase, so "Guid1" < "Guid2" (matches the plain string
        // ordering of the full paths) — and the result must be stable/deterministic, not just
        // "any non-zero value": the reverse comparison must produce the exact opposite sign.
        var expectedSign = Math.Sign(string.Compare(itemA.Path, itemB.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedSign, Math.Sign(cmp));

        var reverseCmp = PanelItemSortComparer.ByName.Compare(itemB, itemA);
        Assert.Equal(-Math.Sign(cmp), Math.Sign(reverseCmp));
    }

    /// <summary>
    /// Verifies that two items with the same display name and the same path compare as equal.
    /// </summary>
    [Fact]
    public void Compare_SameDisplayNameAndSamePath_ReturnsZero()
    {
        var itemA = new FileItem(@"C:\PanelA\Report.docx", null);
        var itemB = new FileItem(@"C:\PanelA\Report.docx", null);

        Assert.Equal(0, PanelItemSortComparer.ByName.Compare(itemA, itemB));
    }

    // ── Sorts by DisplayName, not by Name ────────────────────────────────────

    /// <summary>
    /// Verifies that ordering is driven by <see cref="FileItem.DisplayName"/> (the cosmetic override),
    /// not by the on-disk <see cref="FileItem.Name"/>, when the two disagree.
    /// </summary>
    [Fact]
    public void Compare_SortsByDisplayName_NotByRawName()
    {
        // itemA's on-disk Name ("ZZZ.txt") is alphabetically LAST, but its cosmetic DisplayName
        // ("AAA.txt") is alphabetically FIRST. itemB has no override, so its DisplayName equals its
        // Name ("MMM.txt"). If the comparer used Name, itemB would sort first; using DisplayName,
        // itemA must sort first.
        var itemA = new FileItem(@"C:\Panel\Guid1\ZZZ.txt", null, displayName: "AAA.txt");
        var itemB = new FileItem(@"C:\Panel\MMM.txt", null);

        Assert.Equal("ZZZ.txt", itemA.Name);
        Assert.Equal("AAA.txt", itemA.DisplayName);
        Assert.Equal("MMM.txt", itemB.Name);
        Assert.Equal("MMM.txt", itemB.DisplayName);

        Assert.True(PanelItemSortComparer.ByName.Compare(itemA, itemB) < 0);
        Assert.True(PanelItemSortComparer.ByName.Compare(itemB, itemA) > 0);
    }

    // ── End-to-end sort ordering ─────────────────────────────────────────────

    /// <summary>
    /// Verifies the end-to-end sort order on a mixed set of directories and files: directories first,
    /// each group then ordered naturally by display name.
    /// </summary>
    [Fact]
    public void Sort_MixedDirectoriesAndFiles_DirectoriesFirstThenNaturalOrderByDisplayName()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), $"DOrgTest_{Guid.NewGuid():N}");
        var dir2 = Path.Combine(Path.GetTempPath(), $"DOrgTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        try
        {
            var folderB = new FileItem(dir2, null, displayName: "Zebra");
            var folderA = new FileItem(dir1, null, displayName: "Apple");
            var file10 = new FileItem(@"C:\Panel\file10.txt", null);
            var file2 = new FileItem(@"C:\Panel\file2.txt", null);

            var items = new[] { file10, folderB, file2, folderA };
            Array.Sort(items, PanelItemSortComparer.ByName);

            Assert.Equal(new[] { folderA, folderB, file2, file10 }, items);
        }
        finally
        {
            Directory.Delete(dir1);
            Directory.Delete(dir2);
        }
    }
}
