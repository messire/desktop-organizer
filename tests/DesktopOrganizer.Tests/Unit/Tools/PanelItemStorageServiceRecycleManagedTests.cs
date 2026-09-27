using System.IO;
using System.Text.Json;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;
using Moq;

namespace DesktopOrganizer.Tests.Unit.Tools;

/// <summary>
/// Unit tests for <see cref="PanelItemStorageService.RecycleManagedItem"/> — "Delete" chosen in a managed item's
/// native shell context menu (M5a, release-polish 1.0 review): the item must go to the Recycle Bin from its
/// ORIGINAL location, so a later "Restore" from the Recycle Bin puts it somewhere the user can see instead of back
/// into the hidden per-item storage directory.
/// </summary>
/// <remarks>
/// Real, throw-away storage root and source folder; the Recycle Bin (<see cref="INativeApi.RecycleItems"/>) is
/// mocked — a callback deleting the passed paths stands for a successful recycle.
/// </remarks>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public sealed class PanelItemStorageServiceRecycleManagedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DOrgRecycleMgd_{Guid.NewGuid():N}");
    private readonly string _sourceDir = Path.Combine(Path.GetTempPath(), $"DOrgRecycleMgdSrc_{Guid.NewGuid():N}");
    private readonly Mock<INativeApi> _nativeMock = new();
    private readonly List<string[]> _recycleCalls = [];
    private readonly PanelItemStorageService _sut;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelItemStorageServiceRecycleManagedTests"/> class.
    /// </summary>
    public PanelItemStorageServiceRecycleManagedTests()
    {
        Directory.CreateDirectory(_sourceDir);
        _sut = new PanelItemStorageService(_root, _nativeMock.Object);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SafeDeleteDirectory(_root);
        SafeDeleteDirectory(_sourceDir);
    }

    /// <summary>
    /// The item is moved back to where it came from and recycled from THERE (never from the storage folder); the
    /// manifest entry and the per-item directory are released.
    /// </summary>
    [Fact]
    public void RecycleManagedItem_RecyclesFromOriginalLocation()
    {
        SetupRecycle(deletes: true);
        var source = NewSourceFile("Invoice.pdf");
        var managed = _sut.MoveIn(source)!;
        var itemDir = Path.GetDirectoryName(managed)!;

        var result = _sut.RecycleManagedItem(managed);

        Assert.Equal(ManagedRecycleKind.Recycled, result.Kind);
        var call = Assert.Single(_recycleCalls);
        Assert.Equal(source, Assert.Single(call), ignoreCase: true);
        Assert.False(File.Exists(source));
        Assert.False(Directory.Exists(itemDir));
        Assert.Empty(ReadManifest());
    }

    /// <summary>
    /// A managed FOLDER takes the same route.
    /// </summary>
    [Fact]
    public void RecycleManagedItem_Folder_RecyclesFromOriginalLocation()
    {
        SetupRecycle(deletes: true);
        var sourceFolder = Path.Combine(_sourceDir, "Photos");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "a.jpg"), "x");
        var managed = _sut.MoveInDirectory(sourceFolder)!;

        var result = _sut.RecycleManagedItem(managed);

        Assert.Equal(ManagedRecycleKind.Recycled, result.Kind);
        Assert.Equal(sourceFolder, Assert.Single(Assert.Single(_recycleCalls)), ignoreCase: true);
        Assert.Empty(ReadManifest());
    }

    /// <summary>
    /// The shell did not recycle it (cancelled, "delete permanently?" declined): the item must not be lost from the
    /// panel — it is moved back into managed storage and reported <see cref="ManagedRecycleKind.Kept"/> with its new
    /// managed path, whose recorded original path is still the item's original location.
    /// </summary>
    [Fact]
    public void RecycleManagedItem_ShellDeclines_MovesItemBackIntoStorage()
    {
        SetupRecycle(deletes: false);
        var source = NewSourceFile("Keep.txt");
        var managed = _sut.MoveIn(source)!;

        var result = _sut.RecycleManagedItem(managed);

        Assert.Equal(ManagedRecycleKind.Kept, result.Kind);
        Assert.NotNull(result.Path);
        Assert.True(_sut.IsManaged(result.Path!));
        Assert.True(File.Exists(result.Path));
        Assert.False(File.Exists(source));
        Assert.Equal(source, Assert.Single(ReadManifest()).Value, ignoreCase: true);
    }

    /// <summary>
    /// An item that cannot be restored (a legacy item with no recorded original path) is recycled where it is —
    /// exactly what the shell's own Delete would have done. Nothing is lost.
    /// </summary>
    [Fact]
    public void RecycleManagedItem_NoOriginalPath_FallsBackToRecyclingFromStorage()
    {
        SetupRecycle(deletes: true);
        var itemDir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(itemDir);
        var legacy = Path.Combine(itemDir, "legacy.txt");
        File.WriteAllText(legacy, "x");

        var result = _sut.RecycleManagedItem(legacy);

        Assert.Equal(ManagedRecycleKind.Recycled, result.Kind);
        Assert.Equal(legacy, Assert.Single(Assert.Single(_recycleCalls)), ignoreCase: true);
    }

    /// <summary>
    /// The fallback path reports <see cref="ManagedRecycleKind.Kept"/> with the unchanged path when even that
    /// recycle did not happen.
    /// </summary>
    [Fact]
    public void RecycleManagedItem_NoOriginalPath_ShellDeclines_KeepsItemInPlace()
    {
        SetupRecycle(deletes: false);
        var itemDir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(itemDir);
        var legacy = Path.Combine(itemDir, "legacy.txt");
        File.WriteAllText(legacy, "x");

        var result = _sut.RecycleManagedItem(legacy);

        Assert.Equal(ManagedRecycleKind.Kept, result.Kind);
        Assert.Equal(legacy, result.Path);
        Assert.True(File.Exists(legacy));
    }

    /// <summary>
    /// A path outside managed storage is never touched and never reaches the Recycle Bin.
    /// </summary>
    [Fact]
    public void RecycleManagedItem_NotManaged_DoesNothing()
    {
        var outside = NewSourceFile("outside.txt");

        var result = _sut.RecycleManagedItem(outside);

        Assert.Equal(ManagedRecycleKind.NotManaged, result.Kind);
        Assert.Empty(_recycleCalls);
        Assert.True(File.Exists(outside));
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; must not mask the real assertion failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private void SetupRecycle(bool deletes)
        => _nativeMock
            .Setup(n => n.RecycleItems(It.IsAny<string[]>(), It.IsAny<IntPtr>()))
            .Callback<string[], IntPtr>((paths, _) =>
            {
                _recycleCalls.Add(paths);
                if (!deletes) return;

                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
            })
            .Returns(deletes);

    private string NewSourceFile(string name)
    {
        var path = Path.Combine(_sourceDir, name);
        File.WriteAllText(path, "content");
        return path;
    }

    private Dictionary<string, string> ReadManifest()
    {
        var manifestPath = Path.Combine(_root, PanelItemStorageService.ManifestFileName);
        if (!File.Exists(manifestPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath)) ?? [];
        return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
    }
}
