using System.IO;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// ADR-009 amendment 2026-10-10 (O1): <see cref="CloudPathClassifier"/> recognizes files that live inside a
/// OneDrive-synchronized root (environment variables, covering Known Folder Move) and cloud placeholders
/// (file attributes) — pure logic, driven here by injected environment/attribute lookups.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class CloudPathClassifierTests
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>A file under <c>%OneDrive%</c> (e.g. a Desktop redirected by Known Folder Move) is a synced-folder file.</summary>
    [Fact]
    public void Classify_PathUnderOneDriveRoot_IsSyncedFolder()
    {
        var sut = Create(new() { ["OneDrive"] = @"C:\Users\u\OneDrive" });

        Assert.Equal(CloudPathKind.SyncedFolder, sut.Classify(@"C:\Users\u\OneDrive\Desktop\a.txt"));
    }

    /// <summary>Each of the three OneDrive variables counts, and the comparison ignores case and a trailing separator.</summary>
    /// <param name="variable">The environment variable holding the root.</param>
    [Theory]
    [InlineData("OneDrive")]
    [InlineData("OneDriveConsumer")]
    [InlineData("OneDriveCommercial")]
    public void Classify_AnyOneDriveVariable_CaseInsensitiveAndTrailingSeparatorTolerant(string variable)
    {
        var sut = Create(new() { [variable] = @"D:\Cloud\OneDrive - Contoso\" });

        Assert.Equal(CloudPathKind.SyncedFolder, sut.Classify(@"d:\cloud\onedrive - contoso\Desktop\a.txt"));
    }

    /// <summary>A path outside every OneDrive root is not synced.</summary>
    [Fact]
    public void Classify_PathOutsideRoots_IsNone()
    {
        var sut = Create(new() { ["OneDrive"] = @"C:\Users\u\OneDrive" });

        Assert.Equal(CloudPathKind.None, sut.Classify(@"C:\Users\u\Desktop\a.txt"));
    }

    /// <summary>A sibling folder sharing the root's name as a prefix (<c>OneDrive2</c>) is NOT inside the root.</summary>
    [Fact]
    public void Classify_SiblingWithSamePrefix_IsNone()
    {
        var sut = Create(new() { ["OneDrive"] = @"C:\Users\u\OneDrive" });

        Assert.Equal(CloudPathKind.None, sut.Classify(@"C:\Users\u\OneDrive2\a.txt"));
    }

    /// <summary>The root folder itself (dropping the OneDrive folder) counts as synced.</summary>
    [Fact]
    public void Classify_RootItself_IsSyncedFolder()
    {
        var sut = Create(new() { ["OneDrive"] = @"C:\Users\u\OneDrive" });

        Assert.Equal(CloudPathKind.SyncedFolder, sut.Classify(@"C:\Users\u\OneDrive"));
    }

    /// <summary>Unset, blank, relative or drive-root variables never match everything.</summary>
    /// <param name="value">The unusable variable value.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"OneDrive")]
    [InlineData(@"C:\")]
    public void Classify_UnusableRootVariable_IsIgnored(string value)
    {
        var sut = Create(new() { ["OneDrive"] = value });

        Assert.Equal(CloudPathKind.None, sut.Classify(@"C:\Users\u\Desktop\a.txt"));
    }

    /// <summary>The cloud-files recall attributes and OFFLINE mark a placeholder, even outside any OneDrive root.</summary>
    /// <param name="attributes">The raw attribute bits.</param>
    [Theory]
    [InlineData((int)0x00400000)]
    [InlineData((int)0x00040000)]
    [InlineData((int)FileAttributes.Offline)]
    public void Classify_PlaceholderAttributes_IsPlaceholder(int attributes)
    {
        var sut = Create(attributes: _ => (FileAttributes)attributes | FileAttributes.Archive);

        Assert.Equal(CloudPathKind.Placeholder, sut.Classify(@"C:\Users\u\Desktop\a.txt"));
    }

    /// <summary>A placeholder inside a synced root is reported as a placeholder (the more specific verdict).</summary>
    [Fact]
    public void Classify_PlaceholderUnderRoot_PrefersPlaceholder()
    {
        var sut = Create(new() { ["OneDrive"] = @"C:\Users\u\OneDrive" }, _ => RecallOnDataAccess);

        Assert.Equal(CloudPathKind.Placeholder, sut.Classify(@"C:\Users\u\OneDrive\a.txt"));
    }

    /// <summary>Ordinary attributes (hidden, readonly, archive, reparse point without recall) are not cloud markers.</summary>
    [Fact]
    public void Classify_OrdinaryAttributes_IsNone()
    {
        var sut = Create(attributes: _ => FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.ReparsePoint);

        Assert.Equal(CloudPathKind.None, sut.Classify(@"C:\Users\u\Desktop\a.txt"));
    }

    /// <summary>A missing path (attributes unreadable) falls back to the root check only and never throws.</summary>
    [Fact]
    public void Classify_UnreadableAttributes_FallsBackToRootCheck()
    {
        var sut = Create(new() { ["OneDrive"] = @"C:\Users\u\OneDrive" }, _ => null);

        Assert.Equal(CloudPathKind.SyncedFolder, sut.Classify(@"C:\Users\u\OneDrive\gone.txt"));
        Assert.Equal(CloudPathKind.None, sut.Classify(@"C:\Users\u\Desktop\gone.txt"));
    }

    /// <summary>Blank and malformed paths are <see cref="CloudPathKind.None"/>, never an exception.</summary>
    /// <param name="path">The path to classify.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\bad\0path")]
    public void Classify_BlankOrMalformed_IsNone(string? path)
    {
        var sut = Create(new() { ["OneDrive"] = @"C:\Users\u\OneDrive" });

        Assert.Equal(CloudPathKind.None, sut.Classify(path));
    }

    /// <summary>The production constructor reads real attributes: an ordinary temp file is not cloud-synced.</summary>
    [Fact]
    public void Classify_DefaultConstructor_OrdinaryTempFile_IsNone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DOrgCloud_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");
        try
        {
            Assert.Equal(CloudPathKind.None, new CloudPathClassifier().Classify(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CloudPathClassifier Create(
        Dictionary<string, string>? env = null,
        Func<string, FileAttributes?>? attributes = null)
    {
        env ??= [];
        return new CloudPathClassifier(
            name => env.TryGetValue(name, out var value) ? value : null,
            attributes ?? (_ => FileAttributes.Normal));
    }
}
