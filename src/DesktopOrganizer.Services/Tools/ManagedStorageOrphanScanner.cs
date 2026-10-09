using System.IO;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
/// <remarks>
/// Pure read-only pass over <see cref="IPanelItemStorageService.GetManifestEntries"/> plus a file-system
/// existence check per entry — safe to run on a background thread. The "referenced" test is built once from
/// the caller's path set (every path plus all of its ancestors, so "equals or lies inside" is a set lookup),
/// then each manifest entry is checked in O(1).
/// </remarks>
internal sealed class ManagedStorageOrphanScanner : IManagedStorageOrphanScanner
{
    private readonly IPanelItemStorageService _storage;

    /// <summary>Initializes a new instance of the <see cref="ManagedStorageOrphanScanner"/> class.</summary>
    /// <param name="storage">Source of the manifest entries.</param>
    public ManagedStorageOrphanScanner(IPanelItemStorageService storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <inheritdoc />
    public IReadOnlyList<ManagedStorageOrphan> Scan(IReadOnlyCollection<string> referencedPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referencedPaths);

        // referencedOrInside: each referenced path and all its ancestors - an entry in here is equal to or
        // contains a referenced path. exact: the referenced paths themselves, matched against the recorded
        // ORIGINAL path (stale references that stale-path healing re-binds to the managed copy).
        var referencedOrContaining = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in referencedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var full = Normalize(path);
            if (full is null) continue;

            exact.Add(full);
            var current = full;
            while (!string.IsNullOrEmpty(current) && referencedOrContaining.Add(current))
                current = Path.GetDirectoryName(current);
        }

        var orphans = new List<ManagedStorageOrphan>();
        foreach (var (managedPath, originalPath) in _storage.GetManifestEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var managed = Normalize(managedPath);
            if (managed is null) continue;
            if (referencedOrContaining.Contains(managed)) continue;

            var original = Normalize(originalPath);
            if (original is not null && exact.Contains(original)) continue;

            var isDirectory = Directory.Exists(managed);
            if (!isDirectory && !File.Exists(managed)) continue;

            orphans.Add(new ManagedStorageOrphan(
                managed,
                originalPath,
                Path.GetFileName(managed),
                isDirectory,
                GetStoredAtUtc(managed, isDirectory)));
        }

        orphans.Sort((a, b) => b.StoredAt.CompareTo(a.StoredAt));
        return orphans;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Length == 0 ? null : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The per-item GUID directory is created when the item is moved in, so its creation time is the best
    /// "stored at" stamp (a moved file keeps its own creation time). A legacy flat-layout item falls back to its own.
    /// </summary>
    private static DateTime GetStoredAtUtc(string managed, bool isDirectory)
    {
        try
        {
            var itemDir = Path.GetDirectoryName(managed);
            if (!string.IsNullOrEmpty(itemDir)
                && Guid.TryParseExact(Path.GetFileName(itemDir), "N", out _)
                && Directory.Exists(itemDir))
            {
                return Directory.GetCreationTimeUtc(itemDir);
            }

            return isDirectory ? Directory.GetCreationTimeUtc(managed) : File.GetCreationTimeUtc(managed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
