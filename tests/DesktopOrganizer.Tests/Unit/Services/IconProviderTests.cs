using System.IO;
using DesktopOrganizer.Services.Enums;
using DesktopOrganizer.Services.Interfaces;
using DesktopOrganizer.Services.Models;
using Moq;
using static DesktopOrganizer.Services.Enums.FileAttribute;
using static DesktopOrganizer.Services.Enums.FileFlag;

namespace DesktopOrganizer.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="IconProvider" />.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class IconProviderTests
{
    private const string InvalidFilePath = "InvalidFilePath.txt";
    private const string ValidFilePath = "Resources/TestFile.txt";
    private const FileFlag IconFlags = Icon | ShellIconSize | UseFileAttributes;
    private const FileAttribute FileAttributes = Normal;

    private static readonly IntPtr ValidIconHandle = new (12345);
    private static readonly IntPtr InvalidIconHandle = IntPtr.Zero;

    private readonly IconProvider _iconProvider;
    private readonly Mock<IIconHandler> _iconHandlerMock;
    private readonly Mock<IIconFactory> _iconFactoryMock;

    public IconProviderTests()
    {
        _iconHandlerMock = new Mock<IIconHandler>();
        _iconFactoryMock = new Mock<IIconFactory>();
        _iconProvider = new IconProvider(_iconHandlerMock.Object, _iconFactoryMock.Object);
    }

    public static IEnumerable<object[]> GetIconTestData => new List<object[]>
    {
        new object[] { ValidFilePath, ValidIconHandle },
        new object[] { InvalidFilePath, InvalidIconHandle },
    };

    /// <summary>
    /// Ensures that GetIcon behaves correctly for various file paths.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetIconTestData))]
    public void GetIcon_BehavesCorrectly_ForVariousFilePaths(string filePath, IntPtr iconHandle)
    {
        // Arrange
        var expectedIcon = new DrawingImage();
        SetupMocks(filePath, iconHandle, expectedIcon);

        // Act
        var result = _iconProvider.GetIcon(filePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedIcon, result);
    }

    /// <summary>
    /// Ensures that GetIcon caches icons for the same file path.
    /// </summary>
    [Fact]
    public void GetIcon_CachesIcons_ForSameFilePath()
    {
        // Arrange
        var expectedIcon = new DrawingImage();
        var shellFileInfo = SetupMocks(ValidFilePath, ValidIconHandle, expectedIcon);

        // Act
        var firstResult = _iconProvider.GetIcon(ValidFilePath);
        var secondResult = _iconProvider.GetIcon(ValidFilePath);

        // Assert
        Assert.Same(firstResult, secondResult);
        _iconHandlerMock
            .Verify(iconHandler => iconHandler.GetIconHandle(ValidFilePath, ref shellFileInfo, FileAttributes, IconFlags), Times.Once);
    }

    /// <summary>
    /// Ensures the icon cache never grows past its configured capacity: pushing 3x capacity
    /// distinct paths through the provider still leaves at most <c>maxCachedIcons</c> icons cached,
    /// observed indirectly via the icon handler being re-invoked for evicted paths.
    /// </summary>
    [Fact]
    public void GetIcon_EvictsOldestEntries_WhenCacheExceedsCapacity()
    {
        // Arrange
        const int capacity = 2;
        var provider = new IconProvider(_iconHandlerMock.Object, _iconFactoryMock.Object, capacity);
        var paths = Enumerable.Range(0, 3).Select(index => CreateTempFile($"cache-evict-{index}")).ToArray();

        try
        {
            foreach (var path in paths)
            {
                SetupIconHandlerForPath(path, ValidIconHandle);
            }

            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
                .Returns(() => new DrawingImage());

            // Act: fill the cache beyond capacity (3 distinct paths, capacity 2), then request the
            // oldest path again.
            foreach (var path in paths)
            {
                provider.GetIcon(path);
            }

            provider.GetIcon(paths[0]);

            // Assert: the oldest path (paths[0]) was evicted by the time paths[2] was inserted, so
            // requesting it again re-fetched the handle (called more than once overall).
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(paths[0], ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
                Times.AtLeast(2));
        }
        finally
        {
            foreach (var path in paths)
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Ensures LRU ordering: touching an older entry moves it to the front, so a subsequent
    /// insertion evicts the entry that was *not* touched instead.
    /// </summary>
    [Fact]
    public void GetIcon_TouchingEntry_PromotesItInLruOrder()
    {
        // Arrange
        const int capacity = 2;
        var provider = new IconProvider(_iconHandlerMock.Object, _iconFactoryMock.Object, capacity);
        var pathA = CreateTempFile("lru-a");
        var pathB = CreateTempFile("lru-b");
        var pathC = CreateTempFile("lru-c");

        try
        {
            foreach (var path in new[] { pathA, pathB, pathC })
            {
                SetupIconHandlerForPath(path, ValidIconHandle);
            }

            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
                .Returns(() => new DrawingImage());

            // Act: A, B fill the cache (capacity 2); touching A promotes it; C then evicts B (not A).
            provider.GetIcon(pathA);
            provider.GetIcon(pathB);
            provider.GetIcon(pathA);
            provider.GetIcon(pathC);

            // A should still be cached (handler called once); B should have been evicted and
            // re-fetched if requested again.
            provider.GetIcon(pathA);
            provider.GetIcon(pathB);

            // Assert
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(pathA, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
                Times.Once);
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(pathB, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
                Times.AtLeast(2));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
            File.Delete(pathC);
        }
    }

    /// <summary>
    /// Ensures a freshness-sensitive file (e.g. <c>.ico</c>, which carries its icon embedded in the
    /// file itself) whose <c>LastWriteTimeUtc</c> changed since it was cached is re-read instead of
    /// served from the (now stale) cache entry.
    /// </summary>
    [Fact]
    public void GetIcon_RereadsIcon_WhenFreshnessSensitiveFileLastWriteTimeChanges()
    {
        // Arrange
        var path = CreateTempFile("freshness", ".ico");
        try
        {
            SetupIconHandlerForPath(path, ValidIconHandle);
            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
                .Returns(() => new DrawingImage());

            // Act
            _iconProvider.GetIcon(path);

            // Force a distinct, coarser-grained last-write-time (file system timestamp resolution
            // can be as coarse as ~2s on some volumes).
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            _iconProvider.GetIcon(path);

            // Assert
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(path, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
                Times.Exactly(2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Ensures a directory's cache entry is NOT invalidated by a changed <c>LastWriteTimeUtc</c>:
    /// directories are excluded from the freshness check (see <c>NeedsFreshnessCheck</c>) because
    /// <c>Directory.GetLastWriteTimeUtc</c> changes on every child add/remove — exactly what happens
    /// on a Folder Portal resync — which would otherwise defeat the cache in its hottest path.
    /// </summary>
    [Fact]
    public void GetIcon_DoesNotRereadFolderIcon_WhenDirectoryLastWriteTimeChanges()
    {
        // Arrange
        var directoryPath = Path.Combine(Path.GetTempPath(), $"IconProviderTests-dir-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directoryPath);
        try
        {
            SetupIconHandlerForPath(directoryPath, ValidIconHandle);
            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
                .Returns(() => new DrawingImage());

            // Act
            _iconProvider.GetIcon(directoryPath);

            // Simulate a Folder Portal resync child add/remove, which bumps the directory's
            // LastWriteTimeUtc even though the folder's own icon has not changed.
            File.WriteAllText(Path.Combine(directoryPath, "child.txt"), "test");
            _iconProvider.GetIcon(directoryPath);

            // Assert: the icon handle is fetched only once — the cache hit is honored regardless of
            // the directory's timestamp having changed.
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(directoryPath, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
                Times.Once);
        }
        finally
        {
            System.IO.Directory.Delete(directoryPath, recursive: true);
        }
    }

    /// <summary>
    /// Ensures cache lookups are case-insensitive on the file path, matching Windows filesystem
    /// semantics and the rest of the codebase's path-comparison convention.
    /// </summary>
    [Fact]
    public void GetIcon_CachesIcon_ForSamePathWithDifferentCasing()
    {
        // Arrange
        var path = CreateTempFile("casing");
        var upperCasePath = path.ToUpperInvariant();
        try
        {
            SetupIconHandlerForPath(path, ValidIconHandle);
            var expectedIcon = new DrawingImage();
            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
                .Returns(expectedIcon);

            // Act
            var firstResult = _iconProvider.GetIcon(path);
            var secondResult = _iconProvider.GetIcon(upperCasePath);

            // Assert
            Assert.Same(firstResult, secondResult);
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(path, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
                Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Ensures the placeholder icon is created (via <see cref="IIconFactory.CreateBitmap()"/>) only
    /// once, even across multiple cache misses, instead of on every miss.
    /// </summary>
    [Fact]
    public void PlaceholderIcon_IsCreatedOnce_AcrossMultipleCacheMisses()
    {
        // Arrange
        var placeholder = new DrawingImage();
        _iconFactoryMock.Setup(factory => factory.CreateBitmap()).Returns(placeholder);

        // Act
        var first = _iconProvider.GetIcon(InvalidFilePath);
        var second = _iconProvider.GetIcon("AnotherMissingFile.txt");

        // Assert
        Assert.Same(placeholder, first);
        Assert.Same(placeholder, second);
        _iconFactoryMock.Verify(factory => factory.CreateBitmap(), Times.Once);
    }

    [Fact]
    public void GetIcon_ThrowsException_WhenIconHandlerFails()
    {
        // Arrange
        var exceptionMessage = "Test exception";
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Throws(new InvalidOperationException(exceptionMessage));

        using var consoleOutput = new StringWriter();
        var originalOutput = Console.Error;
        Console.SetError(consoleOutput);

        try
        {
            // Act
            var result = _iconProvider.GetIcon(ValidFilePath);

            // Assert
            _iconHandlerMock.Verify(api => api.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags), Times.Once);
            var output = consoleOutput.ToString();
            Assert.Contains($"Error retrieving icon for '{ValidFilePath}': {exceptionMessage}", output);
        }
        finally
        {
            Console.SetError(originalOutput);
        }
    }

    /// <summary>
    /// Creates a real, uniquely-named temp file so <c>File.Exists</c>/<c>GetLastWriteTimeUtc</c>
    /// checks in <see cref="IconProvider"/> observe genuine filesystem state instead of relying on
    /// mocked I/O (which the provider does not go through for existence/freshness checks).
    /// </summary>
    private static string CreateTempFile(string suffix, string extension = ".txt")
    {
        var path = Path.Combine(Path.GetTempPath(), $"IconProviderTests-{suffix}-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "test");
        return path;
    }

    /// <summary>
    /// Configures the icon handler mock to return <paramref name="iconHandle"/> for any call
    /// against <paramref name="filePath"/>, regardless of the <c>ref</c> <see cref="ShellFileInfo"/>
    /// value passed in.
    /// </summary>
    private void SetupIconHandlerForPath(string filePath, IntPtr iconHandle)
        => _iconHandlerMock
            .Setup(handler => handler.GetIconHandle(filePath, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Returns(iconHandle);

    private ShellFileInfo SetupMocks(string filePath, IntPtr iconHandle, DrawingImage expectedIcon)
    {
        var shellFileInfo = default(ShellFileInfo);

        _iconHandlerMock
            .Setup(handler => handler.GetIconHandle(filePath, ref shellFileInfo, FileAttributes, IconFlags))
            .Returns(iconHandle);

        if (iconHandle == ValidIconHandle)
        {
            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
                .Returns(expectedIcon);
        }
        else
        {
            _iconFactoryMock
                .Setup(factory => factory.CreateBitmap())
                .Returns(expectedIcon);
        }

        return shellFileInfo;
    }
}
