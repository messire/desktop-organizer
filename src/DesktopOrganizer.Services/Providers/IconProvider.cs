using System.IO;
using DesktopOrganizer.Services.Abstractions;
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
    public IconProvider(IIconHandler iconHandler, IIconFactory iconFactory)
        : this(iconHandler, iconFactory, DefaultMaxCachedIcons)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IconProvider"/> class with an explicit cache
    /// capacity. Internal — used by tests to exercise LRU eviction with a small, deterministic bound.
    /// </summary>
    /// <param name="iconHandler">Native icon handle retrieval.</param>
    /// <param name="iconFactory">Bitmap/placeholder factory.</param>
    /// <param name="maxCachedIcons">Maximum number of icon cache entries retained before LRU eviction.</param>
    internal IconProvider(IIconHandler iconHandler, IIconFactory iconFactory, int maxCachedIcons)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedIcons);

        _iconHandler = iconHandler;
        _iconFactory = iconFactory;
        _maxCachedIcons = maxCachedIcons;
    }

    // Created lazily and cached for the lifetime of the provider — avoids allocating a new
    // placeholder bitmap on every cache miss for a missing/inaccessible file.
    private ImageSource PlaceholderIcon => _placeholderIcon ??= _iconFactory.CreateBitmap();

    /// <inheritdoc />
    public ImageSource GetIcon(string filePath)
    {
        var isFile = File.Exists(filePath);
        if (!isFile && !Directory.Exists(filePath))
        {
            return PlaceholderIcon;
        }

        var lastWriteTimeUtc = NeedsFreshnessCheck(filePath, isFile)
            ? TryGetLastWriteTimeUtc(filePath, isFile)
            : default;

        if (TryGetFreshCachedIcon(filePath, lastWriteTimeUtc, out var cachedIcon))
        {
            return cachedIcon;
        }

        return TryRetrieveIcon(filePath, isFile, lastWriteTimeUtc) ?? PlaceholderIcon;
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
    private static DateTime TryGetLastWriteTimeUtc(string filePath, bool isFile)
    {
        try
        {
            return isFile
                ? File.GetLastWriteTimeUtc(filePath)
                : Directory.GetLastWriteTimeUtc(filePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading last write time for '{filePath}': {ex.Message}");
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
    /// <returns>A <see cref="BitmapSource"/> representing the icon, or <see langword="null"/> if retrieval fails.</returns>
    private ImageSource? TryRetrieveIcon(string filePath, bool isFile, DateTime lastWriteTimeUtc)
    {
        try
        {
            var fileInfo = default(ShellFileInfo);
            var fileAttributes = isFile ? FileAttributes : DirectoryAttributes;
            var iconHandle = _iconHandler.GetIconHandle(filePath, ref fileInfo, fileAttributes, IconFlags);
            return iconHandle == IntPtr.Zero ? null : TryCreateBitmapSource(filePath, lastWriteTimeUtc, ref fileInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error retrieving icon for '{filePath}': {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Attempts to create bitmap source from the specified file icon.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="lastWriteTimeUtc">Freshness timestamp to store alongside the cached icon.</param>
    /// <param name="fileInfo">The shell file information structure containing icon details.</param>
    /// <returns>A <see cref="BitmapSource"/> representing the icon, or <see langword="null"/> if creation fails.</returns>
    private ImageSource TryCreateBitmapSource(string filePath, DateTime lastWriteTimeUtc, ref ShellFileInfo fileInfo)
    {
        try
        {
            var imageSource = _iconFactory.CreateBitmap(fileInfo.IconHandle);
            StoreInCache(filePath, imageSource, lastWriteTimeUtc);
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
