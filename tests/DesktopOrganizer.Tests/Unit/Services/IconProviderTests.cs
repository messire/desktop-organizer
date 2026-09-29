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

    /// <summary>
    /// Initializes a new instance of the <see cref="IconProviderTests"/> class.
    /// </summary>
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

    /// <summary>
    /// Gets the file-path/icon-handle pairs exercised by
    /// <see cref="GetIcon_BehavesCorrectly_ForVariousFilePaths"/>.
    /// </summary>
    public static IEnumerable<object[]> GetIconTestData => new List<object[]>
    {
        new object[] { ValidFilePath, ValidIconHandle },
        new object[] { InvalidFilePath, InvalidIconHandle },
    };

    /// <summary>
    /// Ensures that GetIcon behaves correctly for various file paths.
    /// </summary>
    /// <param name="filePath">The file path passed to <see cref="IconProvider.GetIcon(string, int)"/>.</param>
    /// <param name="iconHandle">The icon handle the mocked icon handler is set up to return for that path.</param>
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

    /// <summary>
    /// Verifies that a missing path with a recognized extension is still resolved through a shell lookup
    /// (as a file) and serves the real icon when the shell manages to produce one.
    /// </summary>
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

    /// <summary>
    /// Verifies that a missing path without an extension is resolved through a shell lookup as a
    /// directory, using <see cref="FileAttribute.Directory"/> instead of the file attribute set.
    /// </summary>
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

    /// <summary>
    /// Verifies that when the shell lookup for a missing path with an unrecognized extension also fails,
    /// <see cref="IconProvider.GetIcon(string, int)"/> falls back to its own placeholder bitmap.
    /// </summary>
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

    /// <summary>
    /// Verifies that a missing-path fallback result is never cached: once the file appears on disk, the
    /// next call re-resolves and serves the real icon instead of the stale fallback.
    /// </summary>
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

    /// <summary>
    /// Verifies that repeated calls for a path that remains missing never get cached either: each call
    /// re-resolves through the icon handler instead of serving a cached instance.
    /// </summary>
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

    /// <summary>
    /// Verifies that <see cref="IconProvider.IsPlaceholder"/> returns <see langword="true"/> for the icon
    /// instance <see cref="IconProvider.GetIcon(string, int)"/> itself hands back as its own placeholder.
    /// </summary>
    [Fact]
    public void IsPlaceholder_PlaceholderInstance_ReturnsTrue()
    {
        var placeholder = new DrawingImage();
        _iconFactoryMock.Setup(f => f.CreateBitmap()).Returns(placeholder);

        var icon = _iconProvider.GetIcon(@"C:\fake\missing.qqqzz");

        Assert.True(_iconProvider.IsPlaceholder(icon));
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.IsPlaceholder"/> returns <see langword="false"/> for a genuinely
    /// resolved icon.
    /// </summary>
    [Fact]
    public void IsPlaceholder_RealResolvedIcon_ReturnsFalse()
    {
        SetupMocks(ValidFilePath, ValidIconHandle, new DrawingImage());
        var icon = _iconProvider.GetIcon(ValidFilePath);

        Assert.False(_iconProvider.IsPlaceholder(icon));
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.IsPlaceholder"/> returns <see langword="false"/> for a
    /// <see langword="null"/> icon rather than throwing.
    /// </summary>
    [Fact]
    public void IsPlaceholder_NullIcon_ReturnsFalse()
    {
        Assert.False(_iconProvider.IsPlaceholder(null));
    }

    /// <summary>
    /// Verifies that checking a real, non-placeholder icon never forces the lazily-created placeholder
    /// bitmap to be allocated just to answer the reference-equality question.
    /// </summary>
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

    // ── release-polish 1.0: high-resolution fallback chain + size-aware cache ─────────────────────────

    /// <summary>
    /// Verifies that an existing path is resolved through the <c>IShellItemImageFactory</c> stage first, at the
    /// snapped pixel size, without touching the two fallback stages, and that the bitmap handle is released.
    /// </summary>
    [Fact]
    public void GetIcon_ExistingPath_UsesShellItemStageFirst_AtSnappedSize_AndReleasesBitmap()
    {
        var path = CreateTempFile("stage1");
        try
        {
            var expected = new DrawingImage();
            var bitmapHandle = SetupShellItemStage(path, 72, expected);

            var result = _iconProvider.GetIcon(path, 70); // 70 px snaps up to the 72 px bucket

            Assert.Same(expected, result);
            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(path, 72), Times.Once);
            _iconHandlerMock.Verify(h => h.DestroyBitmapHandle(bitmapHandle), Times.Once);
            _iconHandlerMock.Verify(h => h.GetSystemImageListIconHandle(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<FileAttribute>(), It.IsAny<FileFlag>()),
                Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that when the shell-item stage produces nothing, the system image list stage is used at the same
    /// size and its <c>HICON</c> is destroyed after conversion; the generic by-extension lookup is not reached.
    /// </summary>
    [Fact]
    public void GetIcon_ShellItemStageFails_FallsBackToSystemImageList_AndDestroysIcon()
    {
        var path = CreateTempFile("stage2");
        try
        {
            var expected = new DrawingImage();
            var iconHandle = new IntPtr(4242);
            _iconHandlerMock.Setup(h => h.GetSystemImageListIconHandle(path, 48)).Returns(iconHandle);
            _iconFactoryMock.Setup(f => f.CreateBitmap(iconHandle)).Returns(expected);

            var result = _iconProvider.GetIcon(path, 48);

            Assert.Same(expected, result);
            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(path, 48), Times.Once);
            _iconHandlerMock.Verify(h => h.DestroyIconHandle(iconHandle), Times.Once);
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<FileAttribute>(), It.IsAny<FileFlag>()),
                Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a bitmap whose pixels cannot be read is still released, and the chain moves on.
    /// </summary>
    [Fact]
    public void GetIcon_ShellItemPixelsUnreadable_ReleasesBitmap_AndFallsBack()
    {
        var path = CreateTempFile("unreadable");
        try
        {
            var bitmapHandle = new IntPtr(777);
            _iconHandlerMock.Setup(h => h.GetShellItemBitmap(path, 48)).Returns(bitmapHandle);
            _iconHandlerMock.Setup(h => h.TryReadBitmapPixels(bitmapHandle, out It.Ref<NativeBitmapPixels>.IsAny)).Returns(false);

            _iconProvider.GetIcon(path, 48);

            _iconHandlerMock.Verify(h => h.DestroyBitmapHandle(bitmapHandle), Times.Once);
            _iconHandlerMock.Verify(h => h.GetSystemImageListIconHandle(path, 48), Times.Once);
            _iconFactoryMock.Verify(f => f.CreateBitmap(It.IsAny<NativeBitmapPixels>(), It.IsAny<int>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that an exception inside the shell-item stage is logged privacy-safely with its stage token,
    /// the bitmap is still released, and the icon is served by the next stage instead of surfacing the error.
    /// </summary>
    [Fact]
    public void GetIcon_ShellItemStageThrows_LogsStage_ReleasesBitmap_AndFallsBack()
    {
        var path = CreateTempFile("throws", ".exe");
        try
        {
            var bitmapHandle = SetupShellItemStage(path, 48, new DrawingImage());
            _iconFactoryMock
                .Setup(f => f.CreateBitmap(It.IsAny<NativeBitmapPixels>(), It.IsAny<int>()))
                .Throws(new InvalidOperationException($"boom {path}"));
            var fallbackIcon = new DrawingImage();
            var iconHandle = new IntPtr(99);
            _iconHandlerMock.Setup(h => h.GetSystemImageListIconHandle(path, 48)).Returns(iconHandle);
            _iconFactoryMock.Setup(f => f.CreateBitmap(iconHandle)).Returns(fallbackIcon);

            var result = _iconProvider.GetIcon(path, 48);

            Assert.Same(fallbackIcon, result);
            _iconHandlerMock.Verify(h => h.DestroyBitmapHandle(bitmapHandle), Times.Once);

            var write = Assert.Single(_diagnosticsWrites, w => w.Name == "IconRetrievalFailed");
            Assert.Equal("shellItem", GetPayloadValue<string>(write.Payload, "stage"));
            Assert.Equal(".exe", GetPayloadValue<string>(write.Payload, "extension"));
            Assert.Equal(nameof(InvalidOperationException), GetPayloadValue<string>(write.Payload, "errorType"));
            Assert.DoesNotContain(write.Payload, kv => kv.Value is string s && s.Contains(path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies the full fallback order for an existing path: shell item → system image list → by-extension
    /// lookup, each attempted exactly once when every earlier stage fails.
    /// </summary>
    [Fact]
    public void GetIcon_AllHighResolutionStagesFail_TriesStagesInOrder_ThenByExtensionLookup()
    {
        var path = CreateTempFile("order");
        try
        {
            var calls = new List<string>();
            _iconHandlerMock
                .Setup(h => h.GetShellItemBitmap(path, It.IsAny<int>()))
                .Callback(() => calls.Add("shellItem"))
                .Returns(IntPtr.Zero);
            _iconHandlerMock
                .Setup(h => h.GetSystemImageListIconHandle(path, It.IsAny<int>()))
                .Callback(() => calls.Add("imageList"))
                .Returns(IntPtr.Zero);
            _iconHandlerMock
                .Setup(h => h.GetIconHandle(path, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
                .Callback(() => calls.Add("fileAttributes"))
                .Returns(ValidIconHandle);
            var expected = new DrawingImage();
            _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(expected);

            var result = _iconProvider.GetIcon(path, 96);

            Assert.Same(expected, result);
            Assert.Equal(new[] { "shellItem", "imageList", "fileAttributes" }, calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a missing path keeps the pre-1.0 behavior: only the by-extension lookup, never the two
    /// high-resolution stages (both need the item to exist), regardless of the requested size.
    /// </summary>
    [Fact]
    public void GetIcon_MissingPath_NeverTriesHighResolutionStages()
    {
        const string missingPath = @"C:\fake\missing-high-res.lnk";
        SetupIconHandlerForPath(missingPath, ValidIconHandle);
        _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(new DrawingImage());

        _iconProvider.GetIcon(missingPath, 256);

        _iconHandlerMock.Verify(h => h.GetShellItemBitmap(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _iconHandlerMock.Verify(h => h.GetSystemImageListIconHandle(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _iconHandlerMock.Verify(
            h => h.GetIconHandle(missingPath, ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
            Times.Once);
    }

    /// <summary>
    /// Verifies that the cache key includes the (snapped) size: the same path at two sizes is fetched and cached
    /// twice, and each size is served from its own entry afterwards.
    /// </summary>
    [Fact]
    public void GetIcon_SamePathAtDifferentSizes_IsCachedPerSize()
    {
        var path = CreateTempFile("per-size");
        try
        {
            var small = new DrawingImage();
            var large = new DrawingImage();
            SetupShellItemStage(path, 16, small);
            SetupShellItemStage(path, 96, large);

            var first16 = _iconProvider.GetIcon(path, 16);
            var first96 = _iconProvider.GetIcon(path, 96);
            var second16 = _iconProvider.GetIcon(path, 16);
            var second96 = _iconProvider.GetIcon(path, 96);

            Assert.Same(small, first16);
            Assert.Same(large, first96);
            Assert.Same(first16, second16);
            Assert.Same(first96, second96);
            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(path, 16), Times.Once);
            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(path, 96), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that two requested sizes inside the same bucket share one cache entry (one shell round-trip).
    /// </summary>
    [Fact]
    public void GetIcon_SizesInTheSameBucket_ShareOneCacheEntry()
    {
        var path = CreateTempFile("bucket");
        try
        {
            SetupShellItemStage(path, 48, new DrawingImage());

            var first = _iconProvider.GetIcon(path, 41);
            var second = _iconProvider.GetIcon(path, 48);

            Assert.Same(first, second);
            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(path, It.IsAny<int>()), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that the size-less overload requests <see cref="IconPixelSize.Default"/> pixels.
    /// </summary>
    [Fact]
    public void GetIcon_WithoutSize_RequestsDefaultPixelSize()
    {
        var path = CreateTempFile("default-size");
        try
        {
            var expected = new DrawingImage();
            SetupShellItemStage(path, IconPixelSize.Default, expected);

            var result = _iconProvider.GetIcon(path);

            Assert.Same(expected, result);
            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(path, IconPixelSize.Default), Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that the shell-item stage result for a freshness-sensitive file is re-read after the file's
    /// last-write time changes (freshness applies per size entry too).
    /// </summary>
    [Fact]
    public void GetIcon_ShellItemStage_RereadsFreshnessSensitiveFileAfterChange()
    {
        var path = CreateTempFile("fresh-hires", ".lnk");
        try
        {
            SetupShellItemStage(path, 64, new DrawingImage());

            _iconProvider.GetIcon(path, 64);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            _iconProvider.GetIcon(path, 64);

            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(path, 64), Times.Exactly(2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// M4: UNC classification — plain and extended UNC forms skip the item-opening stages; local, extended-length
    /// local and device paths do not.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    /// <param name="expected">Whether it is UNC.</param>
    [Theory]
    [InlineData(@"\\server\share\file.lnk", true)]
    [InlineData(@"//server/share/file.lnk", true)]
    [InlineData(@"\\?\UNC\server\share\file.lnk", true)]
    [InlineData(@"\\.\UNC\server\share\file.lnk", true)]
    [InlineData(@"\\?\C:\Users\me\file.lnk", false)]
    [InlineData(@"\\.\C:\Users\me\file.lnk", false)]
    [InlineData(@"C:\Users\me\file.lnk", false)]
    [InlineData(@"relative\file.lnk", false)]
    [InlineData("", false)]
    [InlineData(@"\", false)]
    public void IsUncPath_ClassifiesPaths(string path, bool expected)
        => Assert.Equal(expected, IconProvider.IsUncPath(path));

    /// <summary>
    /// M4: offline and cloud-placeholder ("recall") attributes skip the item-opening stages; ordinary attributes and
    /// the "invalid" value (-1) do not.
    /// </summary>
    /// <param name="attributes">Raw attribute bits.</param>
    /// <param name="expected">Whether the content counts as remote.</param>
    [Theory]
    [InlineData(0x00001000, true)] // FILE_ATTRIBUTE_OFFLINE
    [InlineData(0x00040000, true)] // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData(0x00400000, true)] // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    [InlineData(0x00400020, true)] // archive + recall on data access (a typical OneDrive online-only file)
    [InlineData(0x00000020, false)] // archive
    [InlineData(0x00000010, false)] // directory
    [InlineData(0x00080000, false)] // FILE_ATTRIBUTE_PINNED (always keep on this device)
    [InlineData(-1, false)]
    public void HasRemoteContentAttributes_ClassifiesAttributes(int attributes, bool expected)
        => Assert.Equal(expected, IconProvider.HasRemoteContentAttributes((System.IO.FileAttributes)attributes));

    /// <summary>
    /// M4: an ordinary local file keeps the high-resolution stages.
    /// </summary>
    [Fact]
    public void ShouldSkipItemStages_LocalFile_IsFalse()
    {
        var path = CreateTempFile("local");
        try
        {
            Assert.False(IconProvider.ShouldSkipItemStages(path, isFile: true));
            Assert.False(IconProvider.ShouldSkipItemStages(Path.GetTempPath(), isFile: false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// M4 end to end: an existing item marked OFFLINE never reaches the item-opening stages (shell item image /
    /// system image list) — it goes straight to the attribute-based stage — so the UI thread cannot block on a
    /// content recall.
    /// </summary>
    [Fact]
    public void GetIcon_OfflineItem_SkipsItemStages_UsesAttributeStage()
    {
        var path = CreateTempFile("offline");
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | System.IO.FileAttributes.Offline);
            Assert.True(IconProvider.ShouldSkipItemStages(path, isFile: true));
            var iconHandle = new IntPtr(4242);
            SetupIconHandlerForPath(path, iconHandle);
            var expected = new DrawingImage();
            _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(expected);

            var result = _iconProvider.GetIcon(path, 96);

            Assert.Same(expected, result);
            _iconHandlerMock.Verify(h => h.GetShellItemBitmap(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
            _iconHandlerMock.Verify(h => h.GetSystemImageListIconHandle(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }
        finally
        {
            File.SetAttributes(path, System.IO.FileAttributes.Normal);
            File.Delete(path);
        }
    }

    // ── ADR-046: TryPeekCachedIcon/TryPeekTypeIcon/GetTypeIcon/PlaceholderIcon ────────────────────────

    /// <summary>A path already resolved by <see cref="IconProvider.GetIcon(string, int)"/> can be peeked back without touching the icon handler again.</summary>
    [Fact]
    public void TryPeekCachedIcon_AfterGetIcon_HitsWithoutTouchingHandler()
    {
        var path = CreateTempFile("peek-hit");
        try
        {
            var expectedIcon = new DrawingImage();
            SetupMocks(path, ValidIconHandle, expectedIcon);
            _iconProvider.GetIcon(path, 32);
            _iconHandlerMock.Invocations.Clear();

            var hit = _iconProvider.TryPeekCachedIcon(path, 32, out var icon);

            Assert.True(hit);
            Assert.Same(expectedIcon, icon);
            _iconHandlerMock.Verify(
                h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<FileAttribute>(), It.IsAny<FileFlag>()),
                Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The same path at a different (snapped) pixel size is a cache miss — sizes are part of the key.</summary>
    [Fact]
    public void TryPeekCachedIcon_DifferentBucket_Misses()
    {
        var path = CreateTempFile("peek-miss-bucket");
        try
        {
            SetupMocks(path, ValidIconHandle, new DrawingImage());
            _iconProvider.GetIcon(path, 32);

            var hit = _iconProvider.TryPeekCachedIcon(path, 256, out var icon);

            Assert.False(hit);
            Assert.Null(icon);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A path never resolved at all is a cache miss, without ever touching the icon handler (no fs/shell access for a pure peek).</summary>
    [Fact]
    public void TryPeekCachedIcon_NeverResolved_MissesWithoutTouchingHandler()
    {
        var hit = _iconProvider.TryPeekCachedIcon(@"C:\never-queried.txt", 32, out var icon);

        Assert.False(hit);
        Assert.Null(icon);
        _iconHandlerMock.Verify(
            h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<FileAttribute>(), It.IsAny<FileFlag>()),
            Times.Never);
    }

    /// <summary><see cref="IconProvider.GetTypeIcon"/> caches by lower-cased extension, independent of the exact path/case that first resolved it.</summary>
    [Fact]
    public void GetTypeIcon_CachesPerExtension_RegardlessOfPathOrCase()
    {
        var expectedIcon = new DrawingImage();
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Returns(ValidIconHandle);

        // Not ValidIconHandle: GetIconHandle's `ref` out-value can't be set through this mock (see the
        // GetIcon_BehavesCorrectly_ForVariousFilePaths/InvalidFilePath remark above) — TryRetrieveIcon
        // always calls CreateBitmap with the ref struct's own (never-actually-set) IconHandle field.
        _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(expectedIcon);

        var first = _iconProvider.GetTypeIcon("a.TXT", isDirectory: false);
        var second = _iconProvider.GetTypeIcon("completely/different/b.txt", isDirectory: false);

        Assert.Same(expectedIcon, first);
        Assert.Same(first, second);
        _iconHandlerMock.Verify(
            h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags),
            Times.Once);
    }

    /// <summary>A directory's type icon is cached under a key ("&lt;dir&gt;") distinct from any file extension's cache entry.</summary>
    [Fact]
    public void GetTypeIcon_Directory_CachesSeparatelyFromFiles()
    {
        var fileIcon = new DrawingImage();
        var dirIcon = new DrawingImage();

        // GetIconHandle's `ref` out-value can't be set through this mock (see the sibling test's remark
        // above), so CreateBitmap can't be told apart by its IntPtr argument either — a small piece of
        // per-call state recorded by GetIconHandle's own Callback distinguishes the two paths instead.
        ImageSource? nextIcon = null;
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Callback(() => nextIcon = fileIcon)
            .Returns(ValidIconHandle);
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, DirectoryAttributes, IconFlags))
            .Callback(() => nextIcon = dirIcon)
            .Returns(ValidIconHandle);
        _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(() => nextIcon!);

        var file = _iconProvider.GetTypeIcon("a.txt", isDirectory: false);
        var directory = _iconProvider.GetTypeIcon("SomeFolder", isDirectory: true);

        Assert.Same(fileIcon, file);
        Assert.Same(dirIcon, directory);
    }

    /// <summary>A failed type-icon resolution returns the placeholder but is NOT cached — a later call retries instead of sticking the whole extension with the placeholder forever.</summary>
    [Fact]
    public void GetTypeIcon_Failure_ReturnsPlaceholderAndIsNotCached()
    {
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<FileAttribute>(), IconFlags))
            .Returns(InvalidIconHandle);
        var placeholder = new DrawingImage();
        _iconFactoryMock.Setup(f => f.CreateBitmap()).Returns(placeholder);

        var first = _iconProvider.GetTypeIcon("fails.abc", isDirectory: false);
        var second = _iconProvider.GetTypeIcon("fails.abc", isDirectory: false);

        Assert.Same(placeholder, first);
        Assert.Same(placeholder, second);
        _iconHandlerMock.Verify(
            h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, It.IsAny<FileAttribute>(), IconFlags),
            Times.Exactly(2));
    }

    /// <summary>Before any <see cref="IconProvider.GetTypeIcon"/> call for a given extension, <see cref="IconProvider.TryPeekTypeIcon"/> misses.</summary>
    [Fact]
    public void TryPeekTypeIcon_BeforeGetTypeIcon_Misses()
        => Assert.Null(_iconProvider.TryPeekTypeIcon("a.txt", isDirectory: false));

    /// <summary>After <see cref="IconProvider.GetTypeIcon"/> resolves an extension, <see cref="IconProvider.TryPeekTypeIcon"/> hits for any path with that extension.</summary>
    [Fact]
    public void TryPeekTypeIcon_AfterGetTypeIcon_Hits()
    {
        var expectedIcon = new DrawingImage();
        _iconHandlerMock
            .Setup(h => h.GetIconHandle(It.IsAny<string>(), ref It.Ref<ShellFileInfo>.IsAny, FileAttributes, IconFlags))
            .Returns(ValidIconHandle);
        _iconFactoryMock.Setup(f => f.CreateBitmap(It.IsAny<IntPtr>())).Returns(expectedIcon);

        _iconProvider.GetTypeIcon("a.txt", isDirectory: false);
        var peeked = _iconProvider.TryPeekTypeIcon("unrelated/b.txt", isDirectory: false);

        Assert.Same(expectedIcon, peeked);
    }

    /// <summary>Concurrent first-time access to <c>PlaceholderIcon</c> from multiple threads still publishes exactly one shared instance (ADR-046 C-2).</summary>
    [Fact]
    public void PlaceholderIcon_AccessedConcurrently_PublishesExactlyOneInstance()
    {
        IIconProvider provider = _iconProvider;
        _iconFactoryMock.Setup(f => f.CreateBitmap()).Returns(() => new DrawingImage());

        var results = new ImageSource[16];
        Parallel.For(0, results.Length, i => results[i] = provider.PlaceholderIcon);

        Assert.All(results, r => Assert.Same(results[0], r));
    }

    // ── helpers ──────────────────────────────────────────────────────────────
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

    /// <summary>
    /// Configures the shell-item stage (stage 1) to succeed for <paramref name="filePath"/> at exactly
    /// <paramref name="size"/> pixels, producing <paramref name="icon"/>. Each size gets its own fake bitmap
    /// handle and its own pixel buffer (identified by its width), so per-size behavior is observable.
    /// </summary>
    /// <returns>The fake <c>HBITMAP</c> handed out for this size.</returns>
    private IntPtr SetupShellItemStage(string filePath, int size, ImageSource icon)
    {
        var bitmapHandle = new IntPtr(100_000 + size);
        var pixels = new NativeBitmapPixels(size, size, new byte[size * size * 4]);

        _iconHandlerMock.Setup(h => h.GetShellItemBitmap(filePath, size)).Returns(bitmapHandle);
        _iconHandlerMock.Setup(h => h.TryReadBitmapPixels(bitmapHandle, out pixels)).Returns(true);
        _iconFactoryMock.Setup(f => f.CreateBitmap(It.Is<NativeBitmapPixels>(p => p.Width == size), size)).Returns(icon);
        return bitmapHandle;
    }

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
