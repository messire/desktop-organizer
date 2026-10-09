using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// ADR-009 amendment 2026-10-10 (O2): <see cref="ManagedStorageOrphanScanner"/> reports manifest entries whose
/// managed copy still exists but that no panel / snapshot references — read-only, never deleting or moving.
/// Runs against a real <see cref="PanelItemStorageService"/> on a throw-away storage root so the manifest is the
/// genuine article.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class ManagedStorageOrphanScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DOrgOrphan_{Guid.NewGuid():N}");
    private readonly string _sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgOrphanSrc_{Guid.NewGuid():N}");
    private readonly PanelItemStorageService _storage;
    private readonly ManagedStorageOrphanScanner _sut;

    /// <summary>Initializes a new instance of the <see cref="ManagedStorageOrphanScannerTests"/> class with a fresh storage root.</summary>
    public ManagedStorageOrphanScannerTests()
    {
        Directory.CreateDirectory(_sourceDir);
        _storage = new PanelItemStorageService(_root, new Mock<INativeApi>().Object);
        _sut = new ManagedStorageOrphanScanner(_storage);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_sourceDir);
    }

    /// <summary>An empty manifest has no orphans.</summary>
    [Fact]
    public void Scan_EmptyManifest_ReturnsEmpty()
    {
        Assert.Empty(_sut.Scan([]));
    }

    /// <summary>A moved-in file that nothing references is reported with its original path, name and a storage time.</summary>
    [Fact]
    public void Scan_UnreferencedFile_IsReportedWithOriginalPath()
    {
        var source = NewFile("Report.txt");
        var managed = _storage.MoveIn(source)!;

        var orphan = Assert.Single(_sut.Scan([]));

        Assert.Equal(managed, orphan.ManagedPath, ignoreCase: true);
        Assert.Equal(source, orphan.OriginalPath, ignoreCase: true);
        Assert.Equal("Report.txt", orphan.Name);
        Assert.False(orphan.IsDirectory);
        Assert.True(orphan.StoredAt > DateTime.UtcNow.AddMinutes(-5));
    }

    /// <summary>A folder moved in via MoveInDirectory is reported as a directory.</summary>
    [Fact]
    public void Scan_UnreferencedDirectory_IsReportedAsDirectory()
    {
        var dir = Path.Combine(_sourceDir, "Project");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        var managed = _storage.MoveInDirectory(dir)!;

        var orphan = Assert.Single(_sut.Scan([]));

        Assert.Equal(managed, orphan.ManagedPath, ignoreCase: true);
        Assert.True(orphan.IsDirectory);
        Assert.Equal("Project", orphan.Name);
    }

    /// <summary>An item a panel lists by its managed path is in use — compared case-insensitively.</summary>
    [Fact]
    public void Scan_ReferencedByManagedPath_IsNotReported()
    {
        var managed = _storage.MoveIn(NewFile("a.txt"))!;

        Assert.Empty(_sut.Scan([managed.ToUpperInvariant()]));
    }

    /// <summary>A stale panel/snapshot entry holding the ORIGINAL path would be re-bound to the item at restore, so it counts as used.</summary>
    [Fact]
    public void Scan_ReferencedByOriginalPath_IsNotReported()
    {
        var source = NewFile("a.txt");
        _storage.MoveIn(source);

        Assert.Empty(_sut.Scan([source]));
    }

    /// <summary>A path inside a moved-in folder keeps the folder in use.</summary>
    [Fact]
    public void Scan_ReferencedPathInsideManagedDirectory_IsNotReported()
    {
        var dir = Path.Combine(_sourceDir, "Project");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        var managed = _storage.MoveInDirectory(dir)!;

        Assert.Empty(_sut.Scan([Path.Combine(managed, "a.txt")]));
    }

    /// <summary>Only the unreferenced items are reported when several are managed.</summary>
    [Fact]
    public void Scan_MixedItems_ReportsOnlyUnreferenced()
    {
        var kept = _storage.MoveIn(NewFile("kept.txt"))!;
        var lost = _storage.MoveIn(NewFile("lost.txt"))!;

        var orphan = Assert.Single(_sut.Scan([kept]));

        Assert.Equal(lost, orphan.ManagedPath, ignoreCase: true);
    }

    /// <summary>A manifest entry whose managed copy is gone cannot be restored, so it is not offered.</summary>
    [Fact]
    public void Scan_ManagedCopyMissing_IsNotReported()
    {
        var managed = _storage.MoveIn(NewFile("a.txt"))!;
        File.Delete(managed);

        Assert.Empty(_sut.Scan([]));
    }

    /// <summary>Newest storage time first.</summary>
    [Fact]
    public void Scan_MultipleOrphans_NewestFirst()
    {
        var older = _storage.MoveIn(NewFile("older.txt"))!;
        var newer = _storage.MoveIn(NewFile("newer.txt"))!;
        Directory.SetCreationTimeUtc(Path.GetDirectoryName(older)!, DateTime.UtcNow.AddHours(-2));
        Directory.SetCreationTimeUtc(Path.GetDirectoryName(newer)!, DateTime.UtcNow.AddHours(-1));

        var result = _sut.Scan([]);

        Assert.Equal(["newer.txt", "older.txt"], result.Select(o => o.Name));
    }

    /// <summary>The scan never changes the storage: files and manifest stay exactly as they were.</summary>
    [Fact]
    public void Scan_IsReadOnly_LeavesStorageUntouched()
    {
        var managed = _storage.MoveIn(NewFile("a.txt"))!;
        var manifestBefore = File.ReadAllText(Path.Combine(_root, PanelItemStorageService.ManifestFileName));

        _sut.Scan([]);

        Assert.True(File.Exists(managed));
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(_root, PanelItemStorageService.ManifestFileName)));
    }

    /// <summary>A cancelled token aborts the scan.</summary>
    [Fact]
    public void Scan_Cancelled_Throws()
    {
        _storage.MoveIn(NewFile("a.txt"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _sut.Scan([], cts.Token));
    }

    /// <summary>Blank reference entries are ignored rather than matching everything.</summary>
    [Fact]
    public void Scan_BlankReferences_AreIgnored()
    {
        _storage.MoveIn(NewFile("a.txt"));

        Assert.Single(_sut.Scan([string.Empty, "   "]));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private string NewFile(string name)
    {
        var path = Path.Combine(_sourceDir, name);
        File.WriteAllText(path, "content");
        return path;
    }
}
