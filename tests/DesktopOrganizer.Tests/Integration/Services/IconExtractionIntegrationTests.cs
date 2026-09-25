using System.IO;
using System.Windows.Media.Imaging;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Providers;
using DesktopOrganizer.Services.Tools;
using DesktopOrganizer.Tests.Resources;

namespace DesktopOrganizer.Tests.Integration.Services;

/// <summary>
/// End-to-end integration tests for the real icon pipeline (<c>NativeApi</c> → <c>IconHandler</c> →
/// <c>IconProvider</c>/<c>IconFactory</c>), exercised against real files on disk — no mocks. This is
/// the regression suite for the P00 baseline audit's icon experiment
/// (docs/PLANS/workspace-grid-00-baseline.md, Part C), covering the exact scenarios it measured:
/// <c>.exe</c>, <c>.lnk</c> (both a valid and a broken target), a folder, a missing path, and a
/// <c>.url</c> file.
/// </summary>
/// <remarks>
/// Assumes <c>C:\Windows\notepad.exe</c> exists, per the P08 fix-prompt's own test matrix — true on
/// every Windows 10/11 install this project targets (manual verify if that ever changes).
/// </remarks>
[Trait(IntegrationTests, nameof(Services))]
public sealed class IconExtractionIntegrationTests : IDisposable
{
    private const string NotepadPath = @"C:\Windows\notepad.exe";

    // Manual verify (Win10/Win11): assumed present on every Windows 10/11 install this project
    // targets, same assumption the fix-prompt's own test matrix makes for NotepadPath above — used
    // only by GetIcon_DifferentExecutables_ReturnPixelDistinctIcons (M4).
    private const string ExplorerPath = @"C:\Windows\explorer.exe";

    // Reused across every "must not be our own placeholder" assertion below (M4 fix, P08 fix cycle 1,
    // 2026-09-22) — constructed directly against IconFactory, bypassing the shell entirely, so it's a
    // deterministic reference regardless of what SHGFI_USEFILEATTRIBUTES happens to resolve for any
    // particular missing-path extension on a given machine (see the UX P1 tests below, which — unlike
    // this field — deliberately DO go through the real shell for a missing path).
    private static readonly IconFactory PlaceholderFactory = new();

    private readonly string _tempDir;
    private readonly IconProvider _iconProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="IconExtractionIntegrationTests"/> class, creating a
    /// throwaway temp directory and a real (non-mocked) <see cref="IconProvider"/> backed by the real
    /// <see cref="NativeApi"/>.
    /// </summary>
    public IconExtractionIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DOrgIconExtraction_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var nativeApi = new NativeApi();
        var diagnosticsLog = NullDiagnosticsLog.Instance;
        _iconProvider = new IconProvider(new IconHandler(nativeApi, diagnosticsLog), new IconFactory(), diagnosticsLog);
    }

    /// <summary>
    /// Deletes the temp directory created for this test instance, best-effort.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a still-locked handle from a test failure shouldn't mask the real
            // assertion failure.
        }
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a real, existing executable returns a
    /// frozen, non-transparent bitmap that is distinct from the pipeline's own placeholder icon.
    /// </summary>
    [Fact]
    public void GetIcon_RealExecutable_ReturnsOpaqueFrozenIcon()
    {
        Assert.True(File.Exists(NotepadPath), $"Test assumption: '{NotepadPath}' must exist on this machine.");

        var icon = (BitmapSource)_iconProvider.GetIcon(NotepadPath);

        Assert.True(icon.IsFrozen, "Icons leaving the icon pipeline must be Freeze()-d (ADR-012 Decision 3).");
        Assert.True(CountOpaquePixels(icon) > 0, "A real executable's icon must not be blank/placeholder.");

        // M4 fix (P08 fix cycle 1): opaque>0 alone also passes for our OWN opaque placeholder (since
        // aa6760c) — assert it's not that reference at all, not just "some opaque bitmap".
        Assert.NotSame(PlaceholderFactory.CreateBitmap(), icon);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a real, existing folder returns a frozen,
    /// non-transparent bitmap that is distinct from the pipeline's own placeholder icon.
    /// </summary>
    [Fact]
    public void GetIcon_RealFolder_ReturnsOpaqueFrozenIcon()
    {
        var icon = (BitmapSource)_iconProvider.GetIcon(_tempDir);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0);
        Assert.NotSame(PlaceholderFactory.CreateBitmap(), icon);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a <c>.lnk</c> shortcut pointing at a real
    /// existing target returns a frozen, non-transparent bitmap, distinct from the placeholder icon.
    /// </summary>
    [Fact]
    public void GetIcon_ShortcutToRealTarget_ReturnsOpaqueFrozenIcon()
    {
        Assert.True(File.Exists(NotepadPath), $"Test assumption: '{NotepadPath}' must exist on this machine.");

        var shortcutPath = Path.Combine(_tempDir, "notepad.lnk");
        ShellLinkTestHelper.CreateShortcut(shortcutPath, NotepadPath);
        Assert.True(File.Exists(shortcutPath));

        var icon = (BitmapSource)_iconProvider.GetIcon(shortcutPath);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0);
        Assert.NotSame(PlaceholderFactory.CreateBitmap(), icon);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a <c>.lnk</c> shortcut whose target no
    /// longer exists returns the shell's own opaque fallback icon, never this pipeline's placeholder
    /// (P00 baseline H12).
    /// </summary>
    [Fact]
    public void GetIcon_ShortcutToMissingTarget_ReturnsOsFallbackIcon_NotPlaceholder()
    {
        // The .lnk file itself exists — target resolution (and the fallback icon for a broken one) is
        // the shell's job, not IconProvider's; P00 baseline H12 measured this as a real, non-transparent
        // OS fallback icon (opaquePx=820/1024), never our own placeholder.
        var shortcutPath = Path.Combine(_tempDir, "broken.lnk");
        var missingTarget = Path.Combine(_tempDir, $"does-not-exist-{Guid.NewGuid():N}.exe");
        ShellLinkTestHelper.CreateShortcut(shortcutPath, missingTarget);
        Assert.True(File.Exists(shortcutPath));

        var icon = (BitmapSource)_iconProvider.GetIcon(shortcutPath);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0);

        // M4 fix: the test's own name promises "not placeholder" but never actually asserted it —
        // opaque>0 also holds for our placeholder (since aa6760c). This is the assertion that was missing.
        Assert.NotSame(PlaceholderFactory.CreateBitmap(), icon);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for an <c>.url</c> internet shortcut file
    /// returns a frozen, non-transparent bitmap, distinct from the placeholder icon.
    /// </summary>
    [Fact]
    public void GetIcon_UrlFile_ReturnsOpaqueFrozenIcon()
    {
        var urlPath = Path.Combine(_tempDir, "example.url");
        File.WriteAllText(urlPath, "[InternetShortcut]\r\nURL=https://example.com/\r\n");

        var icon = (BitmapSource)_iconProvider.GetIcon(urlPath);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0);
        Assert.NotSame(PlaceholderFactory.CreateBitmap(), icon);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a nonexistent path never returns a fully
    /// transparent bitmap (regression for the P00 baseline defect where the placeholder rendered
    /// before its <c>DrawingContext</c> closed).
    /// </summary>
    [Fact]
    public void GetIcon_MissingPath_ReturnsOpaquePlaceholder_NotTransparent()
    {
        // Regression for the P00 baseline defect: this used to return a fully transparent 16x16
        // bitmap (IconFactory.CreatePlaceHolder rendering before its DrawingContext closed).
        // NOTE (UX P1, P08 fix cycle 1, 2026-09-22): since the generic-by-extension fallback below was
        // added, a MISSING path with a recognized extension like ".lnk" is now likely resolved to a
        // real generic shell icon rather than falling all the way through to our own placeholder — see
        // GetIcon_MissingPath_RecognizedExtension_ReturnsGenericIcon_NotOwnPlaceholder below for that
        // behavior specifically. This assertion (opaque, frozen) still holds either way, so it's kept
        // as a coarser smoke test rather than removed.
        var missingPath = Path.Combine(_tempDir, $"missing-{Guid.NewGuid():N}.lnk");

        var icon = (BitmapSource)_iconProvider.GetIcon(missingPath);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0, "Placeholder for a missing path must not be fully transparent.");
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> returns the exact same cached bitmap instance
    /// when called twice for the same existing file path.
    /// </summary>
    [Fact]
    public void GetIcon_SamePathTwice_ReturnsCachedInstance()
    {
        Assert.True(File.Exists(NotepadPath), $"Test assumption: '{NotepadPath}' must exist on this machine.");

        var first = _iconProvider.GetIcon(NotepadPath);
        var second = _iconProvider.GetIcon(NotepadPath);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> never caches the result for a still-missing
    /// path: two successive calls re-resolve independently, and both must still be valid, frozen,
    /// non-transparent bitmaps even though they are not required to be the same instance (MINOR-1 fix,
    /// P08 fix cycle 2).
    /// </summary>
    [Fact]
    public void GetIcon_MissingPathTwice_NeverCachesTheFallback_BothCallsStillValid()
    {
        // MINOR-1 fix (P08 fix cycle 2, 2026-09-22): this used to assert Assert.Same(first, second) —
        // ".lnk" is a recognized extension, so on real Windows this resolves via SHGFI_USEFILEATTRIBUTES
        // to a real generic shortcut icon (UX P1, fix cycle 1), NOT our own PlaceholderIcon singleton —
        // and that real-shell-resolved icon used to get StoreInCache'd under lastWriteTimeUtc = default,
        // making a second call for the same still-missing path return the exact same cached instance.
        // That caching was itself the MINOR-1 defect: it would have gone on to serve this SAME stale
        // instance even after the path became a real file on disk (see IconProviderTests for the unit
        // regression covering that exact transition). A missing path must never be cached at all, so two
        // calls now legitimately re-resolve independently — both must still be valid, non-transparent,
        // frozen icons; they are simply no longer required (or expected) to be the SAME instance.
        var missingPath = Path.Combine(_tempDir, $"missing-{Guid.NewGuid():N}.lnk");

        var first = (BitmapSource)_iconProvider.GetIcon(missingPath);
        var second = (BitmapSource)_iconProvider.GetIcon(missingPath);

        Assert.NotSame(first, second);
        Assert.True(first.IsFrozen);
        Assert.True(second.IsFrozen);
        Assert.True(CountOpaquePixels(first) > 0);
        Assert.True(CountOpaquePixels(second) > 0);
    }

    // ── UX P1 (P08 fix cycle 1, 2026-09-22): generic-by-extension fallback for a missing path ───────

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a missing path with a recognized extension
    /// resolves a real, type-specific shell icon via <c>SHGFI_USEFILEATTRIBUTES</c> rather than falling
    /// back to this pipeline's own placeholder (UX P1, P08 fix cycle 1).
    /// </summary>
    [Fact]
    public void GetIcon_MissingPath_RecognizedExtension_ReturnsGenericIcon_NotOwnPlaceholder()
    {
        // A missing item with a recognized extension must get a real, type-specific icon via
        // SHGFI_USEFILEATTRIBUTES (matching Explorer's own behavior for a selected-but-vanished item),
        // never the uniform gray square — the placeholder is now strictly a last resort for when even
        // that shell lookup itself fails.
        var missingPath = Path.Combine(_tempDir, $"missing-{Guid.NewGuid():N}.txt");

        var icon = (BitmapSource)_iconProvider.GetIcon(missingPath);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0);
        Assert.NotSame(PlaceholderFactory.CreateBitmap(), icon);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a missing path with an unrecognized
    /// extension never throws and always returns a valid, frozen, non-transparent bitmap, regardless
    /// of whether Windows synthesizes a generic icon or the pipeline falls back to its own placeholder.
    /// </summary>
    [Fact]
    public void GetIcon_MissingPath_UnrecognizedExtension_StillReturnsValidFrozenIcon()
    {
        // ".qqqzz" almost certainly has no shell association. Windows may still synthesize a generic
        // "unknown file type" icon for it via SHGFI_USEFILEATTRIBUTES, or the lookup may fail outright
        // and fall back to our own placeholder — the fix-prompt explicitly allows either outcome here
        // (unlike a *recognized* extension, see the test above, which must NOT be our placeholder).
        // This only asserts the contract that actually matters: never a silent exception, always a
        // valid, frozen, non-transparent bitmap either way.
        var missingPath = Path.Combine(_tempDir, $"missing-{Guid.NewGuid():N}.qqqzz");

        var icon = (BitmapSource)_iconProvider.GetIcon(missingPath);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0);
    }

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> for a missing, extension-less path treats it as
    /// a folder (best-effort heuristic) and still returns a valid, frozen, non-transparent bitmap.
    /// </summary>
    [Fact]
    public void GetIcon_MissingFolderLikePath_NoExtension_AttemptsGenericFolderIcon()
    {
        // An extension-less missing path is treated as a folder (best-effort heuristic — directory-ness
        // can't be checked on disk for a path that no longer exists) rather than unconditionally
        // falling back to the placeholder.
        var missingPath = Path.Combine(_tempDir, $"missing-folder-{Guid.NewGuid():N}");

        var icon = (BitmapSource)_iconProvider.GetIcon(missingPath);

        Assert.True(icon.IsFrozen);
        Assert.True(CountOpaquePixels(icon) > 0);
    }

    // ── M4 (P08 fix cycle 1, 2026-09-22): prove distinct executables actually get distinct icons ─────

    /// <summary>
    /// Verifies that <see cref="IconProvider.GetIcon"/> returns pixel-distinct bitmaps for two
    /// different executables, ruling out an accidental "same generic icon for every <c>.exe</c>"
    /// regression (M4, P08 fix cycle 1).
    /// </summary>
    [Fact]
    public void GetIcon_DifferentExecutables_ReturnPixelDistinctIcons()
    {
        Assert.True(File.Exists(NotepadPath), $"Test assumption: '{NotepadPath}' must exist on this machine.");
        Assert.True(File.Exists(ExplorerPath), $"Test assumption: '{ExplorerPath}' must exist on this machine.");

        var notepadIcon = (BitmapSource)_iconProvider.GetIcon(NotepadPath);
        var explorerIcon = (BitmapSource)_iconProvider.GetIcon(ExplorerPath);

        // opaquePixelCount>0 for both would pass even if the pipeline accidentally handed back the same
        // generic icon for every .exe — compare actual pixel content, not just "is something drawn".
        Assert.False(
            GetPbgra32Pixels(notepadIcon).SequenceEqual(GetPbgra32Pixels(explorerIcon)),
            "notepad.exe and explorer.exe must not resolve to pixel-identical icons.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts <paramref name="bitmap"/> to a known-layout (Pbgra32) byte buffer so pixel content can
    /// be compared/inspected deterministically regardless of the source bitmap's native pixel format.
    /// Shared by <see cref="CountOpaquePixels"/> (opacity only) and
    /// <see cref="GetIcon_DifferentExecutables_ReturnPixelDistinctIcons"/> (full content comparison) —
    /// NIT dedup, P08 fix cycle 1.
    /// </summary>
    private static byte[] GetPbgra32Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, destinationPalette: null, alphaThreshold: 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>
    /// Counts pixels with a non-zero alpha channel.
    /// </summary>
    private static int CountOpaquePixels(BitmapSource bitmap)
    {
        var pixels = GetPbgra32Pixels(bitmap);

        var opaqueCount = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) opaqueCount++;
        }

        return opaqueCount;
    }
}
