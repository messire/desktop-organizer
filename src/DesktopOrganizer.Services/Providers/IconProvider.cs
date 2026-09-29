using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using static DesktopOrganizer.Services.Enums.FileAttribute;
using static DesktopOrganizer.Services.Enums.FileFlag;
using Directory = System.IO.Directory;
using IoFileAttributes = System.IO.FileAttributes;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
internal class IconProvider : IIconProvider
{
    // Bounds the icon cache so a long session with active Folder Portals (files constantly
    // added/removed, panels opened/closed) cannot grow memory without limit. 1024 comfortably
    // covers several large panels while still capping worst-case growth. Kept internal (with the
    // explicit-capacity constructor below) so tests can exercise eviction without allocating
    // thousands of icons. Entries are keyed by (path, snapped pixel size), so the same file shown at
    // two sizes counts twice.

    /// <summary>
    /// The default maximum number of icons kept in the cache before least-recently-used eviction
    /// kicks in. See the remarks above for why this bound exists.
    /// </summary>
    internal const int DefaultMaxCachedIcons = 1024;

    // Last-resort stage for an existing path, and the ONLY stage for a missing one (see GetIcon's
    // remarks): resolve purely by extension/attribute, never touching the file system.
    // SHGFI_SHELLICONSIZE caps this at the 32 px shell icon — which is why it is no longer the
    // primary path for existing items.
    private const FileFlag IconFlags = FileFlag.Icon | ShellIconSize | UseFileAttributes;
    private const FileAttribute FileAttributes = Normal;
    private const FileAttribute DirectoryAttributes = FileAttribute.Directory;

    // Values of the closed-set "stage" payload field of IconRetrievalFailed (privacy-safe tokens).
    private const string ShellItemStage = "shellItem";
    private const string ImageListStage = "imageList";
    private const string FileAttributesStage = "fileAttributes";

    // ADR-046 D-5: _typeIconCache key for a directory (an actual directory NAME can legally contain a
    // dot, so this must be a token distinct from any real lower-cased extension, not e.g. string.Empty —
    // which is itself the legitimate key for an extension-less FILE).
    private const string DirectoryTypeKey = "<dir>";

    // FILE_ATTRIBUTE_RECALL_ON_OPEN / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (cloud-files placeholders:
    // OneDrive "online-only" and friends). Not members of System.IO.FileAttributes, hence the raw values.
    private const IoFileAttributes RecallOnOpen = (IoFileAttributes)0x00040000;
    private const IoFileAttributes RecallOnDataAccess = (IoFileAttributes)0x00400000;
    private const IoFileAttributes RemoteContentAttributes = IoFileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    // Only these carry an icon embedded in/derived from the file itself (rather than a shared
    // shell-type icon), so only they can actually go stale when the file's *content* changes.
    // Everything else — folders in particular — would otherwise fail the freshness check on
    // almost every lookup: LastWriteTimeUtc for a directory changes on any child add/remove, which
    // is exactly what happens during a Folder Portal resync, defeating the cache in its hottest path.
    private static readonly HashSet<string> FreshnessSensitiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".ico", ".lnk", ".cur", ".url" };

    private readonly IIconHandler _iconHandler;
    private readonly IIconFactory _iconFactory;
    private readonly IDiagnosticsLog _diagnosticsLog;
    private readonly int _maxCachedIcons;
    private readonly object _cacheLock = new();

    // Dictionary + LinkedList implements an O(1) LRU: the list's head is the most recently used
    // entry, the tail is the eviction candidate. A plain lock (rather than ConcurrentDictionary)
    // is used because maintaining LRU order requires atomic "read + reorder", and calls are
    // predominantly from the UI thread anyway.
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cacheMap = new(CacheKeyComparer.Instance);
    private readonly LinkedList<CacheEntry> _lruList = new();

    // Separate cache: IIconProvider.GetTypeIcon (ADR-046 D-5) is stage 3 ONLY, keyed by lower-case
    // extension or the closed "<dir>" token — never touches the file system, so a ConcurrentDictionary
    // (no LRU/eviction policy needed) is enough. Distinct from _cacheMap: keeping GetIcon's own
    // (path, size) cache and its byte-for-byte-unchanged body untouched is CLAUDE.md/plan C-2.
    private readonly ConcurrentDictionary<string, ImageSource> _typeIconCache = new(StringComparer.Ordinal);

    private ImageSource? _placeholderIcon;
    private bool _placeholderIconInitialized;
    private object? _placeholderIconLock;

    /// <summary>
    /// Initializes a new instance of the <see cref="IconProvider"/> class with the default cache capacity.
    /// </summary>
    /// <param name="iconHandler">Native icon handle retrieval.</param>
    /// <param name="iconFactory">Bitmap/placeholder factory.</param>
    /// <param name="diagnosticsLog">
    /// Structured diagnostics sink (ADR-027) icon-load/retrieval failures are reported to, replacing
    /// the <c>Console.Error.WriteLine</c> calls the P00 baseline audit found silently discarded by
    /// the <c>WinExe</c> process.
    /// </param>
    public IconProvider(IIconHandler iconHandler, IIconFactory iconFactory, IDiagnosticsLog diagnosticsLog)
        : this(iconHandler, iconFactory, diagnosticsLog, DefaultMaxCachedIcons)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IconProvider"/> class with an explicit cache
    /// capacity. Internal — used by tests to exercise LRU eviction with a small, deterministic bound.
    /// </summary>
    /// <param name="iconHandler">Native icon handle retrieval.</param>
    /// <param name="iconFactory">Bitmap/placeholder factory.</param>
    /// <param name="diagnosticsLog">Structured diagnostics sink — see the three-argument constructor.</param>
    /// <param name="maxCachedIcons">Maximum number of icon cache entries retained before LRU eviction.</param>
    internal IconProvider(IIconHandler iconHandler, IIconFactory iconFactory, IDiagnosticsLog diagnosticsLog, int maxCachedIcons)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedIcons);

        _iconHandler = iconHandler;
        _iconFactory = iconFactory;
        _diagnosticsLog = diagnosticsLog;
        _maxCachedIcons = maxCachedIcons;
    }

    // Created lazily and cached for the lifetime of the provider — avoids allocating a new placeholder
    // bitmap on every cache miss for a missing/inaccessible file. Thread-safe (ADR-046 C-2): the
    // background icon-load queue's worker threads and the UI thread can both reach this concurrently.
    // LazyInitializer's ref-bool overload guarantees exactly one CreateBitmap() result is ever published,
    // even if two threads race into this property before the first call has finished.
    private ImageSource PlaceholderIcon =>
        LazyInitializer.EnsureInitialized(ref _placeholderIcon, ref _placeholderIconInitialized, ref _placeholderIconLock, () => _iconFactory.CreateBitmap())!;

    /// <inheritdoc />
    /// <remarks>
    /// NIT fix (P08 fix cycle 2, 2026-09-22): compares against the backing <see cref="_placeholderIcon"/>
    /// field directly, never the <see cref="PlaceholderIcon"/> property. The property lazily allocates a
    /// new 32×32 <c>RenderTargetBitmap</c> (<see cref="IIconFactory.CreateBitmap()"/>) on first access —
    /// calling it here for every real, already-resolved icon would force that allocation on the UI thread
    /// even in a session where no path has ever actually needed a placeholder yet. When
    /// <see cref="_placeholderIcon"/> is still <see langword="null"/>, no placeholder has ever been
    /// produced, so <paramref name="icon"/> trivially cannot be one.
    /// </remarks>
    public bool IsPlaceholder(ImageSource? icon) => icon is not null && _placeholderIcon is not null && ReferenceEquals(icon, _placeholderIcon);

    /// <summary>
    /// Explicit interface implementation so it does not collide with the pre-existing private
    /// <see cref="PlaceholderIcon"/> property above, which every <see cref="GetIcon(string, int)"/> call
    /// site keeps using directly.
    /// </summary>
    ImageSource IIconProvider.PlaceholderIcon => PlaceholderIcon;

    /// <inheritdoc />
    public ImageSource GetIcon(string filePath) => GetIcon(filePath, IconPixelSize.Default);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Existing path — fallback chain (high-resolution icons, release-polish 1.0).</b> Each stage runs only
    /// if the previous one produced nothing; every failure is logged (extension + error type/HRESULT, never
    /// the path) and degrades to the next stage instead of surfacing:
    /// <list type="number">
    /// <item><c>IShellItemImageFactory::GetImage(SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK)</c> at the snapped
    /// pixel size — the file's own icon exactly as Explorer resolves it (shortcut target, embedded exe icon,
    /// custom folder icon), rendered at the requested size. No shortcut-arrow overlay (Fences shows none
    /// either).</item>
    /// <item><c>SHGetFileInfo(SHGFI_SYSICONINDEX)</c> WITHOUT <c>SHGFI_USEFILEATTRIBUTES</c> + the system image
    /// list closest to the size — still the item's own icon, but limited to the image lists' fixed sizes.</item>
    /// <item>The pre-1.0 <c>SHGFI_USEFILEATTRIBUTES | SHGFI_SHELLICONSIZE</c> lookup — a generic 32 px type
    /// icon, still better than the placeholder.</item>
    /// </list>
    /// Whichever stage succeeded is cached under (path, snapped size). Stages 1 and 2 are skipped for a UNC
    /// path and for an offline/cloud-placeholder item (<see cref="ShouldSkipItemStages"/>) — opening those can
    /// block the UI thread for seconds.
    /// </para>
    /// <para>
    /// <b>Missing path.</b> UX P1 (P08 fix cycle 1, 2026-09-22): a permanently-missing item still deserves a
    /// real, type-specific icon wherever the OS can produce one — IconFlags already includes
    /// SHGFI_USEFILEATTRIBUTES, which asks the shell to resolve purely from the given attribute plus the
    /// path's own extension, without ever touching the file system. This matches Explorer's own behavior for
    /// a selected-but-vanished item (a missing "readme.txt" still shows a text-file icon) instead of the
    /// uniform gray square, which is now strictly the last resort — only when even this shell lookup itself
    /// fails (see TryRetrieveIcon). Directory-ness can't be checked on disk for a path that doesn't exist, so
    /// the presence of an extension is used as the best-effort heuristic: extension-less looks like a folder.
    /// The two high-resolution stages above are skipped for a missing path — both need the item to exist.
    /// </para>
    /// <para>
    /// Routed through the SAME cache check as an existing file/directory (not a separate early-return) for a
    /// single code path, but — MINOR-1 fix, P08 fix cycle 2, 2026-09-22 — its result is deliberately NOT
    /// cached. A missing path always freshness-checks to `default` (NeedsFreshnessCheck is skipped whenever
    /// `!exists`, regardless of extension), and MOST extensions are not freshness-sensitive to begin with (see
    /// FreshnessSensitiveExtensions) — so once the file actually appears on disk, a non-freshness-sensitive
    /// extension keeps computing the SAME `default` timestamp, which used to hit this now-stale cache entry
    /// and serve the fallback forever, defeating RefreshItemsLiveness's missing->present re-fetch
    /// (BaseViewModel). The trade-off is a fresh (no-disk-I/O, SHGFI_USEFILEATTRIBUTES) shell round-trip on
    /// every call for a still-missing path rather than a wrong icon surviving the file's reappearance.
    /// </para>
    /// <para>
    /// Threading: called on the UI (STA) thread by the panel view models. The native stages need COM
    /// initialized on the calling thread (the WPF UI thread is STA) and some third-party icon handlers are
    /// STA-only; a stage that fails for that reason just degrades to the next one. The cache itself is
    /// lock-protected.
    /// </para>
    /// </remarks>
    public ImageSource GetIcon(string filePath, int pixelSize)
    {
        var size = IconPixelSize.Snap(pixelSize);
        var isFile = File.Exists(filePath);
        var exists = isFile || Directory.Exists(filePath);
        var effectiveIsFile = exists ? isFile : !string.IsNullOrEmpty(Path.GetExtension(filePath));

        var lastWriteTimeUtc = exists && NeedsFreshnessCheck(filePath, isFile)
            ? TryGetLastWriteTimeUtc(filePath, isFile)
            : default;

        var key = new CacheKey(filePath, size);
        if (TryGetFreshCachedIcon(key, lastWriteTimeUtc, out var cachedIcon))
        {
            return cachedIcon;
        }

        // M4: stages 1-2 open the item through its shell handler on the UI thread — for a UNC path or a
        // cloud/offline placeholder that can block for seconds (unreachable share, content recall). Such an
        // item goes straight to the file-system-free stage 3.
        var icon = exists && !ShouldSkipItemStages(filePath, isFile)
            ? TryShellItemImage(filePath, size)
              ?? TrySystemImageList(filePath, size)
              ?? TryRetrieveIcon(filePath, effectiveIsFile)
            : TryRetrieveIcon(filePath, effectiveIsFile);

        if (icon is null)
        {
            return PlaceholderIcon;
        }

        // Only a path that existed at retrieval time is cached — see the MINOR-1 remarks above.
        if (exists)
        {
            StoreInCache(key, icon, lastWriteTimeUtc);
        }

        return icon;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ADR-046: cache-only, no freshness check — unlike <see cref="GetIcon(string, int)"/>'s own lookup,
    /// a hit here is returned even if the file has since changed; the queued <see cref="GetIcon(string, int)"/>
    /// call re-validates once it actually runs. Safe from any thread: reuses <see cref="GetIcon(string, int)"/>'s
    /// own <see cref="_cacheLock"/>.
    /// </remarks>
    public bool TryPeekCachedIcon(string filePath, int pixelSize, [NotNullWhen(true)] out ImageSource? icon)
    {
        var key = new CacheKey(filePath, IconPixelSize.Snap(pixelSize));

        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                icon = node.Value.Icon;
                return true;
            }
        }

        icon = null;
        return false;
    }

    /// <inheritdoc />
    public ImageSource? TryPeekTypeIcon(string filePath, bool isDirectory)
        => _typeIconCache.TryGetValue(TypeIconKey(filePath, isDirectory), out var icon) ? icon : null;

    /// <inheritdoc />
    /// <remarks>
    /// Reuses <see cref="TryRetrieveIcon"/> — the exact stage-3 <c>SHGFI_USEFILEATTRIBUTES</c> lookup
    /// <see cref="GetIcon(string, int)"/> itself falls back to — but stores the result in the SEPARATE
    /// <see cref="_typeIconCache"/> instead of the <c>(path, size)</c> cache (ADR-046 C-2). A failure is
    /// NOT cached, matching <see cref="GetIcon(string, int)"/>'s own MINOR-1 rationale: a transient failure
    /// must not permanently stick a whole extension with the placeholder.
    /// </remarks>
    public ImageSource GetTypeIcon(string filePath, bool isDirectory)
    {
        var key = TypeIconKey(filePath, isDirectory);
        if (_typeIconCache.TryGetValue(key, out var cached)) return cached;

        var icon = TryRetrieveIcon(filePath, isFile: !isDirectory);
        if (icon is null) return PlaceholderIcon;

        _typeIconCache[key] = icon;
        return icon;
    }

    /// <summary>
    /// Decides whether the two item-opening stages (<see cref="TryShellItemImage"/>, <see cref="TrySystemImageList"/>)
    /// must be skipped for an EXISTING item because they could block the UI thread (M4, release-polish 1.0 review):
    /// a UNC path (see <see cref="IsUncPath"/>) or an item whose attributes mark its content as remote
    /// (see <see cref="HasRemoteContentAttributes"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately cheap: the UNC test is pure string work, and the attribute read is one local metadata call that
    /// does not recall a placeholder's content. A UNC path is decided before any attribute read (that read would go
    /// over the network too).
    /// </para>
    /// <para>
    /// <b>Known limitation (follow-up).</b> Only the item's OWN path and attributes are checked: a local <c>.lnk</c>
    /// whose target is an unreachable share, and a mapped network drive letter (<c>Z:\...</c>), still go through
    /// stage 1 — resolving the target is exactly the costly part; and the existence check at the top of
    /// <see cref="GetIcon(string, int)"/> itself still makes one metadata round-trip to a UNC share. The complete fix
    /// is to fetch high-resolution icons on a dedicated background STA thread (generic icon first, swapped in when
    /// ready); tracked as a follow-up. Until then this predicate removes the two most common multi-second stalls
    /// (the shell handler opening the item / recalling its content).
    /// </para>
    /// </remarks>
    /// <param name="filePath">Path of an existing file or folder.</param>
    /// <param name="isFile">Whether the path is a file (as opposed to a directory).</param>
    /// <returns><see langword="true"/> when only the attribute-based stage may run.</returns>
    internal static bool ShouldSkipItemStages(string filePath, bool isFile)
    {
        if (IsUncPath(filePath)) return true;

        try
        {
            var attributes = isFile ? File.GetAttributes(filePath) : new DirectoryInfo(filePath).Attributes;
            return HasRemoteContentAttributes(attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Vanished in between, access denied, odd path: the item stages would fail the same way — skipping
            // them only saves the attempt.
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a UNC path: <c>\\server\share\...</c> (or <c>//server/share/...</c>), and the
    /// extended forms <c>\\?\UNC\...</c> / <c>\\.\UNC\...</c>. A local extended-length or device path
    /// (<c>\\?\C:\...</c>, <c>\\.\C:\...</c>) is not UNC.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    /// <returns><see langword="true"/> for a UNC path.</returns>
    internal static bool IsUncPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2) return false;
        if (!IsSeparator(path[0]) || !IsSeparator(path[1])) return false;

        // "\\?\" or "\\.\" prefix: UNC only when followed by "UNC\".
        if (path.Length >= 4 && (path[2] == '?' || path[2] == '.') && IsSeparator(path[3]))
        {
            return path.Length >= 8
                   && path.AsSpan(4, 3).Equals("UNC", StringComparison.OrdinalIgnoreCase)
                   && IsSeparator(path[7]);
        }

        return true;

        static bool IsSeparator(char c) => c is '\\' or '/';
    }

    /// <summary>
    /// Whether <paramref name="attributes"/> mark the item's content as not locally available:
    /// <c>FILE_ATTRIBUTE_OFFLINE</c>, <c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c> or <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>
    /// (an online-only OneDrive/cloud-files placeholder, an HSM-migrated file).
    /// </summary>
    /// <param name="attributes">The item's attributes.</param>
    /// <returns><see langword="true"/> when opening the item may recall content from a remote store.</returns>
    internal static bool HasRemoteContentAttributes(IoFileAttributes attributes)
        => (int)attributes != -1 && (attributes & RemoteContentAttributes) != 0;

    /// <summary>
    /// Determines whether <paramref name="filePath"/> needs a filesystem freshness check at all.
    /// Only files whose extension is known to carry an icon embedded in the file itself (and can
    /// therefore go stale without any change under a *different* path) pay for a
    /// <see cref="TryGetLastWriteTimeUtc"/> call. Everything else — directories in particular —
    /// uses a stable <see langword="default"/> timestamp, so cache lookups are honest hits instead
    /// of missing on every directory whose child list changed (e.g. every Folder Portal resync).
    /// </summary>
    private static bool NeedsFreshnessCheck(string filePath, bool isFile)
        => isFile && FreshnessSensitiveExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>Cache key for <see cref="TryPeekTypeIcon"/>/<see cref="GetTypeIcon"/>: <see cref="DirectoryTypeKey"/> for a directory, else the lower-case extension.</summary>
    private static string TypeIconKey(string filePath, bool isDirectory) => isDirectory ? DirectoryTypeKey : Path.GetExtension(filePath).ToLowerInvariant();

    /// <summary>
    /// Reads the last-write timestamp used as a freshness check for cache entries. Failures (e.g.
    /// a race where the file disappears between the existence check and this call) degrade to a
    /// default timestamp. Note this is not fully safe against staleness: if a previous cache write
    /// also stored <see langword="default"/> (e.g. from an earlier failed read, or because the
    /// entry was never freshness-checked), the comparison in
    /// <see cref="TryGetFreshCachedIcon"/> will consider the entry fresh and return the
    /// (potentially outdated) cached icon rather than forcing a re-read.
    /// </summary>
    private DateTime TryGetLastWriteTimeUtc(string filePath, bool isFile)
    {
        try
        {
            return isFile
                ? File.GetLastWriteTimeUtc(filePath)
                : Directory.GetLastWriteTimeUtc(filePath);
        }
        catch (Exception ex)
        {
            if (_diagnosticsLog.IsEnabled)
            {
                // Privacy invariant: extension + exception type name only — never the path or
                // ex.Message (which frequently embeds the path itself).
                _diagnosticsLog.Write(
                    IconDiagnostics.Category,
                    "IconFreshnessCheckFailed",
                    null,
                    new KeyValuePair<string, object?>("extension", IconDiagnostics.SafeExtension(filePath)),
                    new KeyValuePair<string, object?>("errorType", ex.GetType().Name));
            }

            return default;
        }
    }

    /// <summary>
    /// Stage 1: the item's own icon through <c>IShellItemImageFactory</c>, at <paramref name="size"/> pixels.
    /// The <c>HBITMAP</c> is always released (<see cref="IIconHandler.DestroyBitmapHandle"/>) — after its
    /// pixels were copied into the frozen bitmap, or on any failure.
    /// </summary>
    /// <param name="filePath">Path of an existing file or folder.</param>
    /// <param name="size">Snapped pixel size.</param>
    /// <returns>A frozen bitmap, or <see langword="null"/> when this stage failed.</returns>
    private ImageSource? TryShellItemImage(string filePath, int size)
    {
        var bitmapHandle = IntPtr.Zero;
        try
        {
            bitmapHandle = _iconHandler.GetShellItemBitmap(filePath, size);
            if (bitmapHandle == IntPtr.Zero) return null;

            // The shell may hand back a bigger frame (SIIGBF_BIGGERSIZEOK); the factory shrinks it to exactly
            // `size`, so the cache never holds a 256 px frame for a 72 px bucket (M2).
            return _iconHandler.TryReadBitmapPixels(bitmapHandle, out var pixels)
                ? _iconFactory.CreateBitmap(pixels, size)
                : null;
        }
        catch (Exception ex)
        {
            WriteRetrievalFailure(filePath, ShellItemStage, ex);
            return null;
        }
        finally
        {
            _iconHandler.DestroyBitmapHandle(bitmapHandle);
        }
    }

    /// <summary>
    /// Stage 2: the item's own system icon index, extracted from the system image list closest to
    /// <paramref name="size"/>. The <c>HICON</c> is always destroyed after conversion (or on failure).
    /// </summary>
    /// <param name="filePath">Path of an existing file or folder.</param>
    /// <param name="size">Snapped pixel size.</param>
    /// <returns>A frozen bitmap, or <see langword="null"/> when this stage failed.</returns>
    private ImageSource? TrySystemImageList(string filePath, int size)
    {
        var iconHandle = IntPtr.Zero;
        try
        {
            iconHandle = _iconHandler.GetSystemImageListIconHandle(filePath, size);
            return iconHandle == IntPtr.Zero ? null : _iconFactory.CreateBitmap(iconHandle);
        }
        catch (Exception ex)
        {
            WriteRetrievalFailure(filePath, ImageListStage, ex);
            return null;
        }
        finally
        {
            _iconHandler.DestroyIconHandle(iconHandle);
        }
    }

    /// <summary>
    /// Stage 3 (and the only stage for a missing path): the generic type icon by extension/attribute via
    /// <c>SHGFI_USEFILEATTRIBUTES</c>. The <c>HICON</c> is always destroyed after conversion.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="isFile">
    /// Whether <paramref name="filePath"/> is a file (as opposed to a directory) — determines which
    /// <c>SHGFI_USEFILEATTRIBUTES</c> attribute is passed to the shell. Since that flag makes the
    /// shell resolve the icon purely from the given attribute and the path's extension (without
    /// touching the file system), a directory passed as <see cref="FileAttribute.Normal"/> has no
    /// extension to key off and falls back to the generic "unknown file" icon instead of a folder icon.
    /// </param>
    /// <returns>A <see cref="BitmapSource"/> representing the icon, or <see langword="null"/> if retrieval fails.</returns>
    private ImageSource? TryRetrieveIcon(string filePath, bool isFile)
    {
        var fileInfo = default(ShellFileInfo);
        try
        {
            var fileAttributes = isFile ? FileAttributes : DirectoryAttributes;
            var iconHandle = _iconHandler.GetIconHandle(filePath, ref fileInfo, fileAttributes, IconFlags);
            return iconHandle == IntPtr.Zero ? null : _iconFactory.CreateBitmap(fileInfo.IconHandle);
        }
        catch (Exception ex)
        {
            WriteRetrievalFailure(filePath, FileAttributesStage, ex);
            return null;
        }
        finally
        {
            _iconHandler.DestroyIconHandle(fileInfo.IconHandle);
        }
    }

    /// <summary>
    /// Writes <c>IconRetrievalFailed</c> for an exception thrown by one fallback stage.
    /// </summary>
    /// <param name="filePath">The path — only its <see cref="IconDiagnostics.SafeExtension"/> is written.</param>
    /// <param name="stage">Closed-set stage token.</param>
    /// <param name="ex">The exception — only its type name is written.</param>
    private void WriteRetrievalFailure(string filePath, string stage, Exception ex)
    {
        if (!_diagnosticsLog.IsEnabled) return;

        // Privacy invariant: extension + exception type name + closed-set stage only — never the path
        // or ex.Message (which frequently embeds the path itself).
        _diagnosticsLog.Write(
            IconDiagnostics.Category,
            "IconRetrievalFailed",
            null,
            new KeyValuePair<string, object?>("extension", IconDiagnostics.SafeExtension(filePath)),
            new KeyValuePair<string, object?>("errorType", ex.GetType().Name),
            new KeyValuePair<string, object?>("stage", stage));
    }

    /// <summary>
    /// Looks up a cache entry and returns it only if the file has not changed since it was cached.
    /// A stale entry (different <c>LastWriteTimeUtc</c>) is evicted so the caller re-reads the icon.
    /// A fresh hit is moved to the front of the LRU list.
    /// </summary>
    private bool TryGetFreshCachedIcon(CacheKey key, DateTime lastWriteTimeUtc, out ImageSource icon)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                if (node.Value.LastWriteTimeUtc == lastWriteTimeUtc)
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    icon = node.Value.Icon;
                    return true;
                }

                _lruList.Remove(node);
                _cacheMap.Remove(key);
            }
        }

        icon = null!;
        return false;
    }

    /// <summary>
    /// Inserts/refreshes a cache entry at the front of the LRU list, evicting the least-recently-used
    /// entries once the configured capacity is exceeded. Eviction only drops the cache's own
    /// reference — an <see cref="ImageSource"/> still referenced by a live <c>FileItem</c> stays
    /// alive via that reference; this bounds the cache, not the icons already shown in the UI.
    /// </summary>
    private void StoreInCache(CacheKey key, ImageSource icon, DateTime lastWriteTimeUtc)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var existing))
            {
                _lruList.Remove(existing);
                _cacheMap.Remove(key);
            }

            var node = _lruList.AddFirst(new CacheEntry(key, icon, lastWriteTimeUtc));
            _cacheMap[key] = node;

            while (_cacheMap.Count > _maxCachedIcons && _lruList.Last is { } leastRecentlyUsed)
            {
                _lruList.RemoveLast();
                _cacheMap.Remove(leastRecentlyUsed.Value.Key);
            }
        }
    }

    /// <summary>
    /// Cache key: the path (compared case-insensitively — Windows path semantics) plus the snapped pixel
    /// size, since the same file shown by a 16 px list panel and a 96 px grid panel needs two bitmaps.
    /// </summary>
    /// <param name="FilePath">The item path as passed to <see cref="GetIcon(string, int)"/>.</param>
    /// <param name="Size">The snapped pixel size (<see cref="IconPixelSize.Snap"/>).</param>
    private readonly record struct CacheKey(string FilePath, int Size);

    private sealed record CacheEntry(CacheKey Key, ImageSource Icon, DateTime LastWriteTimeUtc);

    /// <summary>
    /// Equality for <see cref="CacheKey"/>: ordinal-ignore-case path, exact size.
    /// </summary>
    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        /// <summary>The shared instance.</summary>
        public static readonly CacheKeyComparer Instance = new();

        /// <inheritdoc />
        public bool Equals(CacheKey x, CacheKey y)
            => x.Size == y.Size && StringComparer.OrdinalIgnoreCase.Equals(x.FilePath, y.FilePath);

        /// <inheritdoc />
        public int GetHashCode(CacheKey obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FilePath), obj.Size);
    }
}
