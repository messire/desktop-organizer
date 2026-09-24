using System.IO;
using DesktopOrganizer.Services.Abstractions;
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
    private const FileAttribute DirectoryAttributes = FileAttribute.Directory;

    private static readonly IntPtr ValidIconHandle = new (12345);
    private static readonly IntPtr InvalidIconHandle = IntPtr.Zero;

    private readonly IconProvider _iconProvider;
    private readonly Mock<IIconHandler> _iconHandlerMock;
    private readonly Mock<IIconFactory> _iconFactoryMock;
    private readonly Mock<IDiagnosticsLog> _diagnosticsLogMock;
    private readonly List<(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload)> _diagnosticsWrites = [];

    public IconProviderTests()
    {
        _iconHandlerMock = new Mock<IIconHandler>();
        _iconFactoryMock = new Mock<IIconFactory>();
        _diagnosticsLogMock = new Mock<IDiagnosticsLog>();
        _diagnosticsLogMock.SetupGet(l => l.IsEnabled).Returns(true);
        _diagnosticsLogMock
            .Setup(l => l.Write(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<KeyValuePair<string, object?>[]>()))
            .Callback<string, string, string?, KeyValuePair<string, object?>[]>(
                (category, name, scope, payload) => _diagnosticsWrites.Add((category, name, scope, payload)));

        _iconProvider = new IconProvider(_iconHandlerMock.Object, _iconFactoryMock.Object, _diagnosticsLogMock.Object);
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
        var provider = new IconProvider(_iconHandlerMock.Object, _iconFactoryMock.Object, _diagnosticsLogMock.Object, capacity);
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
        var provider = new IconProvider(_iconHandlerMock.Object, _iconFactoryMock.Object, _diagnosticsLogMock.Object, capacity);
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
            SetupIconHandlerForPath(directoryPath, ValidIconHandle, DirectoryAttributes);
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
                h => h.GetIconHandle(directoryPath, ref It.Ref<ShellFileInfo>.IsAny, DirectoryAttributes, IconFlags),
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

    /// <summary>
    /// Regression for the P00 baseline audit (docs/DIAGNOSTICS/icon-loading-findings.md, F-10): a
    /// failed icon retrieval used to go only to <c>Console.Error</c>, which a <c>WinExe</c> process
    /// never surfaces anywhere and which the ADR-027 JSONL log never captured. It must now reach the
    /// diagnostics log, and the payload must respect the privacy invariant — extension and exception
    /// type name only, never the path or <see cref="Exception.Message"/> (which frequently embeds it).
    /// </summary>
    [Fact]
    public void GetIcon_WritesDiagnosticsEvent_WhenIconHandlerThrows()
    {
        // Arrange
        var exceptionMessage = $"Test exception mentioning the path {ValidFilePath}";
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Throws(new InvalidOperationException(exceptionMessage));

        // Act
        var result = _iconProvider.GetIcon(ValidFilePath);

        // Assert
        _iconHandlerMock.Verify(api => api.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags), Times.Once);

        var write = Assert.Single(_diagnosticsWrites, w => w.Name == "IconRetrievalFailed");
        Assert.Equal("icon", write.Category);
        Assert.Null(write.Scope);
        Assert.Equal(".txt", GetPayloadValue<string>(write.Payload, "extension"));
        Assert.Equal(nameof(InvalidOperationException), GetPayloadValue<string>(write.Payload, "errorType"));

        // Privacy invariant: no payload value may contain the path or the raw exception message.
        Assert.DoesNotContain(write.Payload, kv => kv.Value is string s && s.Contains(ValidFilePath, StringComparison.Ordinal));
        Assert.DoesNotContain(write.Payload, kv => kv.Value is string s && s.Contains(exceptionMessage, StringComparison.Ordinal));
    }

    /// <summary>
    /// When diagnostics logging is disabled, a failure must still degrade to the placeholder without
    /// paying for payload construction (the zero-cost <see cref="IDiagnosticsLog.IsEnabled"/> seam).
    /// </summary>
    [Fact]
    public void GetIcon_DoesNotWriteDiagnosticsEvent_WhenDiagnosticsDisabled()
    {
        // Arrange
        _diagnosticsLogMock.SetupGet(l => l.IsEnabled).Returns(false);
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Throws(new InvalidOperationException("boom"));

        // Act
        _iconProvider.GetIcon(ValidFilePath);

        // Assert
        _diagnosticsLogMock.Verify(
            l => l.Write(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<KeyValuePair<string, object?>[]>()),
            Times.Never);
    }

    // ── UX P1 (P08 fix cycle 1, 2026-09-22): generic-by-extension fallback for a missing path ───────

    [Fact]
    public void GetIcon_MissingPathWithExtension_AttemptsShellLookupAsFile_ReturnsRealIconWhenAvailable()
    {
        // Arrange — SHGFI_USEFILEATTRIBUTES is already part of IconFlags; a missing path with a
        // recognized extension must now reach the shell instead of short-circuiting straight to the
        // placeholder, matching Explorer's own behavior for a vanished item.
        const string missingPath = @"C:\fake\does-not-exist.txt";
        var expectedIcon = new DrawingImage();
        var shellFileInfo = default(ShellFileInfo);

        _iconHandlerMock
            .Setup(h => h.GetIconHandle(missingPath, ref shellFileInfo, FileAttributes, IconFlags))
            .Returns(ValidIconHandle);
        // It.IsAny<IntPtr>() (not ValidIconHandle): the mocked GetIconHandle above only configures its
        // own RETURN value, not the ref ShellFileInfo.IconHandle field TryCreateBitmapSource actually
        // reads (CreateBitmap(fileInfo.IconHandle)) — that field is never populated by this simple
        // setup and stays IntPtr.Zero, same as every other test in this file that goes through the
        // success path (see SetupMocks below).
        _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(expectedIcon);

        // Act
        var result = _iconProvider.GetIcon(missingPath);

        // Assert
        Assert.Same(expectedIcon, result);
        _iconHandlerMock.Verify(h => h.GetIconHandle(missingPath, ref shellFileInfo, FileAttributes, IconFlags), Times.Once);
    }

    [Fact]
    public void GetIcon_MissingPathWithoutExtension_AttemptsShellLookupAsDirectory()
    {
        const string missingPath = @"C:\fake\does-not-exist-folder";
        var expectedIcon = new DrawingImage();
        var shellFileInfo = default(ShellFileInfo);

        _iconHandlerMock
            .Setup(h => h.GetIconHandle(missingPath, ref shellFileInfo, DirectoryAttributes, IconFlags))
            .Returns(ValidIconHandle);
        _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(expectedIcon);

        var result = _iconProvider.GetIcon(missingPath);

        Assert.Same(expectedIcon, result);
        _iconHandlerMock.Verify(h => h.GetIconHandle(missingPath, ref shellFileInfo, DirectoryAttributes, IconFlags), Times.Once);
    }

    [Fact]
    public void GetIcon_MissingPath_ShellLookupAlsoFails_FallsBackToOwnPlaceholder()
    {
        const string missingPath = @"C:\fake\does-not-exist.qqqzz";
        var placeholder = new DrawingImage();
        _iconFactoryMock.Setup(f => f.CreateBitmap()).Returns(placeholder);
        // No handler setup at all -> loose mock returns IntPtr.Zero, same as a genuinely failed lookup.

        var result = _iconProvider.GetIcon(missingPath);

        Assert.Same(placeholder, result);
        Assert.True(_iconProvider.IsPlaceholder(result));
    }

    // ── MINOR-1 (P08 fix cycle 2, 2026-09-22): missing-path fallback must never poison the cache ──────

    [Fact]
    public void GetIcon_MissingPathFallbackThenFileAppears_ServesTheRealIconNotTheStaleFallback()
    {
        // Regression: fix cycle 1's UX P1 missing-path shell fallback used to be StoreInCache'd under
        // lastWriteTimeUtc = default -- the SAME freshness key a non-freshness-sensitive extension (the
        // vast majority: .txt, .docx, .jpg, directories, ...) keeps computing for its ENTIRE session,
        // including once the file is genuinely present, since NeedsFreshnessCheck is skipped whenever
        // the path didn't exist AND is false for these extensions regardless of existence. Without this
        // fix, RefreshItemsLiveness's missing->present re-fetch (BaseViewModel) would still be served
        // this now-wrong cached fallback instead of the file's real icon. A path that did not exist at
        // retrieval time must never be cached.
        var path = Path.Combine(Path.GetTempPath(), $"IconProviderTests-appears-{Guid.NewGuid():N}.txt");
        Assert.False(File.Exists(path));

        SetupIconHandlerForPath(path, ValidIconHandle);
        _iconFactoryMock
            .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
            .Returns(() => new DrawingImage());

        try
        {
            // Act: first call while genuinely missing -> UX P1 shell fallback, must NOT be cached.
            var missingResult = _iconProvider.GetIcon(path);

            // The file now appears on disk (e.g. the user re-creates it, or a Restore brings it back).
            File.WriteAllText(path, "now exists");
            var presentResult = _iconProvider.GetIcon(path);

            // Assert — a brand-new bitmap instance (factory.CreateBitmap returns a fresh DrawingImage
            // each call), proving the second call actually re-resolved the icon instead of hitting a
            // stale cache entry from the first, missing-path call.
            Assert.NotSame(missingResult, presentResult);
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(path, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
                Times.Exactly(2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetIcon_MissingPathCalledTwice_StillNotCached_ReResolvesEachTime()
    {
        // Companion regression: confirms the "never cache a missing-path result" fix applies even when
        // the path never appears at all — repeated calls for a still-missing path must keep asking the
        // shell (cheap, no-disk-I/O SHGFI_USEFILEATTRIBUTES) rather than serving a cached instance, since
        // GetIcon_CachesIcons_ForSameFilePath already covers the existing-file caching contract
        // separately and this must NOT regress into caching missing paths too.
        const string missingPath = @"C:\fake\still-missing.txt";
        SetupIconHandlerForPath(missingPath, ValidIconHandle);
        _iconFactoryMock
            .Setup(factory => factory.CreateBitmap(It.IsAny<IntPtr>()))
            .Returns(() => new DrawingImage());

        var first = _iconProvider.GetIcon(missingPath);
        var second = _iconProvider.GetIcon(missingPath);

        Assert.NotSame(first, second);
        _iconHandlerMock.Verify(
            h => h.GetIconHandle(missingPath, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
            Times.Exactly(2));
    }

    // ── M3 (P08 fix cycle 1, 2026-09-22): IIconProvider.IsPlaceholder ────────────────────────────────

    [Fact]
    public void IsPlaceholder_PlaceholderInstance_ReturnsTrue()
    {
        var placeholder = new DrawingImage();
        _iconFactoryMock.Setup(f => f.CreateBitmap()).Returns(placeholder);

        var icon = _iconProvider.GetIcon(@"C:\fake\missing.qqqzz");

        Assert.True(_iconProvider.IsPlaceholder(icon));
    }

    [Fact]
    public void IsPlaceholder_RealResolvedIcon_ReturnsFalse()
    {
        SetupMocks(ValidFilePath, ValidIconHandle, new DrawingImage());
        var icon = _iconProvider.GetIcon(ValidFilePath);

        Assert.False(_iconProvider.IsPlaceholder(icon));
    }

    [Fact]
    public void IsPlaceholder_NullIcon_ReturnsFalse()
    {
        Assert.False(_iconProvider.IsPlaceholder(null));
    }

    [Fact]
    public void IsPlaceholder_RealResolvedIcon_DoesNotAllocateAPlaceholderBitmap()
    {
        // NIT fix (P08 fix cycle 2, 2026-09-22): checking a REAL icon must never force-allocate the
        // lazily-created placeholder bitmap just to answer the reference-equality question — a session
        // where no path has ever actually needed a placeholder should never pay for one.
        SetupMocks(ValidFilePath, ValidIconHandle, new DrawingImage());
        var icon = _iconProvider.GetIcon(ValidFilePath);

        _iconProvider.IsPlaceholder(icon);

        _iconFactoryMock.Verify(f => f.CreateBitmap(), Times.Never);
    }

    private static T GetPayloadValue<T>(KeyValuePair<string, object?>[] payload, string key)
        => (T)payload.Single(kv => kv.Key == key).Value!;

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
    private void SetupIconHandlerForPath(string filePath, IntPtr iconHandle, FileAttribute fileAttributes = FileAttributes)
        => _iconHandlerMock
            .Setup(handler => handler.GetIconHandle(filePath, ref It.Ref<ShellFileInfo>.IsAny, fileAttributes, IconFlags))
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
