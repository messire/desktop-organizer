using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;
using static DesktopOrganizer.Services.Enums.FileAttribute;
using static DesktopOrganizer.Services.Enums.FileFlag;
using Directory = System.IO.Directory;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
internal class IconProvider : IIconProvider
{
    // Bounds the icon cache so a long session with active Folder Portals (files constantly
    // added/removed, panels opened/closed) cannot grow memory without limit. 1024 comfortably
    // covers several large panels while still capping worst-case growth. Kept internal (with the
    // explicit-capacity constructor below) so tests can exercise eviction without allocating
    // thousands of icons.
    internal const int DefaultMaxCachedIcons = 1024;

    private const FileFlag IconFlags = FileFlag.Icon | ShellIconSize | UseFileAttributes;
    private const FileAttribute FileAttributes = Normal;
    private const FileAttribute DirectoryAttributes = FileAttribute.Directory;

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
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cacheMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lruList = new();

    private ImageSource? _placeholderIcon;

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

    // Created lazily and cached for the lifetime of the provider — avoids allocating a new
    // placeholder bitmap on every cache miss for a missing/inaccessible file.
    private ImageSource PlaceholderIcon => _placeholderIcon ??= _iconFactory.CreateBitmap();

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

    /// <inheritdoc />
    public ImageSource GetIcon(string filePath)
    {
        var isFile = File.Exists(filePath);
        var exists = isFile || Directory.Exists(filePath);

        // UX P1 (P08 fix cycle 1, 2026-09-22): a permanently-missing item still deserves a real,
        // type-specific icon wherever the OS can produce one — IconFlags already includes
        // SHGFI_USEFILEATTRIBUTES, which asks the shell to resolve purely from the given attribute
        // plus the path's own extension, without ever touching the file system. This matches
        // Explorer's own behavior for a selected-but-vanished item (a missing "readme.txt" still
        // shows a text-file icon) instead of the uniform gray square, which is now strictly the last
        // resort — only when even this shell lookup itself fails (see TryRetrieveIcon). Directory-ness
        // can't be checked on disk for a path that doesn't exist, so the presence of an extension is
        // used as the best-effort heuristic: extension-less looks like a folder.
        //
        // Routed through the SAME cache-check/TryRetrieveIcon path as an existing file/directory below
        // (not a separate early-return) for a single code path, but — MINOR-1 fix, P08 fix cycle 2,
        // 2026-09-22 — its result is deliberately NOT cached (see the `exists` argument threaded through
        // to TryRetrieveIcon below). A missing path always freshness-checks to `default` (NeedsFreshnessCheck
        // is skipped whenever `!exists`, regardless of extension), and MOST extensions are not
        // freshness-sensitive to begin with (see FreshnessSensitiveExtensions) — so once the file
        // actually appears on disk, a non-freshness-sensitive extension keeps computing the SAME
        // `default` timestamp, which used to hit this now-stale cache entry and serve the fallback
        // forever, defeating RefreshItemsLiveness's missing->present re-fetch (BaseViewModel). The
        // trade-off is a fresh (no-disk-I/O, SHGFI_USEFILEATTRIBUTES) shell round-trip on every call for
        // a still-missing path rather than a wrong icon surviving the file's reappearance.
        var effectiveIsFile = exists ? isFile : !string.IsNullOrEmpty(Path.GetExtension(filePath));

        var lastWriteTimeUtc = exists && NeedsFreshnessCheck(filePath, isFile)
            ? TryGetLastWriteTimeUtc(filePath, isFile)
            : default;

        if (TryGetFreshCachedIcon(filePath, lastWriteTimeUtc, out var cachedIcon))
        {
            return cachedIcon;
        }

        return TryRetrieveIcon(filePath, effectiveIsFile, lastWriteTimeUtc, cacheable: exists) ?? PlaceholderIcon;
    }

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
    /// Attempts to retrieve the icon for the specified file path.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="isFile">
    /// Whether <paramref name="filePath"/> is a file (as opposed to a directory) — determines which
    /// <c>SHGFI_USEFILEATTRIBUTES</c> attribute is passed to the shell. Since that flag makes the
    /// shell resolve the icon purely from the given attribute and the path's extension (without
    /// touching the file system), a directory passed as <see cref="FileAttribute.Normal"/> has no
    /// extension to key off and falls back to the generic "unknown file" icon instead of a folder icon.
    /// </param>
    /// <param name="lastWriteTimeUtc">Freshness timestamp to store alongside the cached icon.</param>
    /// <param name="cacheable">
    /// Whether a successfully retrieved icon may be stored in the LRU cache (MINOR-1 fix, P08 fix
    /// cycle 2, 2026-09-22) — <see langword="false"/> for a path that did not exist at retrieval time,
    /// so a stale-but-freshness-check-exempt fallback icon is never served once the path later becomes
    /// real. See <see cref="GetIcon"/>'s remarks.
    /// </param>
    /// <returns>A <see cref="BitmapSource"/> representing the icon, or <see langword="null"/> if retrieval fails.</returns>
    private ImageSource? TryRetrieveIcon(string filePath, bool isFile, DateTime lastWriteTimeUtc, bool cacheable)
    {
        try
        {
            var fileInfo = default(ShellFileInfo);
            var fileAttributes = isFile ? FileAttributes : DirectoryAttributes;
            var iconHandle = _iconHandler.GetIconHandle(filePath, ref fileInfo, fileAttributes, IconFlags);
            return iconHandle == IntPtr.Zero ? null : TryCreateBitmapSource(filePath, lastWriteTimeUtc, cacheable, ref fileInfo);
        }
        catch (Exception ex)
        {
            if (_diagnosticsLog.IsEnabled)
            {
                // Privacy invariant: extension + exception type name only — never the path or
                // ex.Message (which frequently embeds the path itself).
                _diagnosticsLog.Write(
                    IconDiagnostics.Category,
                    "IconRetrievalFailed",
                    null,
                    new KeyValuePair<string, object?>("extension", IconDiagnostics.SafeExtension(filePath)),
                    new KeyValuePair<string, object?>("errorType", ex.GetType().Name));
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to create bitmap source from the specified file icon.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="lastWriteTimeUtc">Freshness timestamp to store alongside the cached icon.</param>
    /// <param name="cacheable">Whether the created icon may be stored in the LRU cache — see <see cref="TryRetrieveIcon"/>.</param>
    /// <param name="fileInfo">The shell file information structure containing icon details.</param>
    /// <returns>A <see cref="BitmapSource"/> representing the icon, or <see langword="null"/> if creation fails.</returns>
    private ImageSource TryCreateBitmapSource(string filePath, DateTime lastWriteTimeUtc, bool cacheable, ref ShellFileInfo fileInfo)
    {
        try
        {
            var imageSource = _iconFactory.CreateBitmap(fileInfo.IconHandle);
            if (cacheable)
            {
                StoreInCache(filePath, imageSource, lastWriteTimeUtc);
            }

            return imageSource;
        }
        finally
        {
            _iconHandler.DestroyIconHandle(fileInfo.IconHandle);
        }
    }

    /// <summary>
    /// Looks up a cache entry and returns it only if the file has not changed since it was cached.
    /// A stale entry (different <c>LastWriteTimeUtc</c>) is evicted so the caller re-reads the icon.
    /// A fresh hit is moved to the front of the LRU list.
    /// </summary>
    private bool TryGetFreshCachedIcon(string filePath, DateTime lastWriteTimeUtc, out ImageSource icon)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(filePath, out var node))
            {
                if (node.Value.LastWriteTimeUtc == lastWriteTimeUtc)
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    icon = node.Value.Icon;
                    return true;
                }

                _lruList.Remove(node);
                _cacheMap.Remove(filePath);
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
    private void StoreInCache(string filePath, ImageSource icon, DateTime lastWriteTimeUtc)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(filePath, out var existing))
            {
                _lruList.Remove(existing);
                _cacheMap.Remove(filePath);
            }

            var node = _lruList.AddFirst(new CacheEntry(filePath, icon, lastWriteTimeUtc));
            _cacheMap[filePath] = node;

            while (_cacheMap.Count > _maxCachedIcons && _lruList.Last is { } leastRecentlyUsed)
            {
                _lruList.RemoveLast();
                _cacheMap.Remove(leastRecentlyUsed.Value.FilePath);
            }
        }
    }

    private sealed record CacheEntry(string FilePath, ImageSource Icon, DateTime LastWriteTimeUtc);
}
