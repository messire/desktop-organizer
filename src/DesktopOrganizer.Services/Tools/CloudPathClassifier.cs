using System.IO;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
/// <remarks>
/// <para>
/// Two independent signals, no P/Invoke: (1) the path lies under a OneDrive root named by
/// <c>%OneDrive%</c>/<c>%OneDriveConsumer%</c>/<c>%OneDriveCommercial%</c> — which also covers Known Folder
/// Move (Desktop redirected into OneDrive); (2) the item is a cloud placeholder per
/// <see cref="File.GetAttributes(string)"/> (<c>RECALL_ON_DATA_ACCESS</c> 0x400000, <c>RECALL_ON_OPEN</c>
/// 0x40000, <c>OFFLINE</c>) — the same raw values <c>IconProvider</c> uses to avoid hydrating files.
/// Reading attributes is metadata-only and never recalls content.
/// </para>
/// <para>
/// Not covered: a cloud reparse point (tag-based) that carries none of those attributes — telling it apart
/// would need <c>GetFileAttributesEx</c>/<c>FSCTL_GET_REPARSE_POINT</c> through <c>INativeApi</c>; the
/// recall attributes already cover every real placeholder, and a synced file without them sits under a
/// OneDrive root anyway.
/// </para>
/// </remarks>
internal sealed class CloudPathClassifier : ICloudFolderDetector
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes PlaceholderMask = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private static readonly string[] RootVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    private readonly Func<string, FileAttributes?> _getAttributes;
    private readonly string[] _roots;

    /// <summary>Initializes a new instance of the <see cref="CloudPathClassifier"/> class over the real environment and file system.</summary>
    public CloudPathClassifier()
        : this(Environment.GetEnvironmentVariable, TryGetAttributes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudPathClassifier"/> class with injected lookups (tests).
    /// </summary>
    /// <param name="getEnvironmentVariable">Environment variable lookup; <see langword="null"/> when unset.</param>
    /// <param name="getAttributes">File/folder attribute lookup; <see langword="null"/> when unreadable.</param>
    internal CloudPathClassifier(Func<string, string?> getEnvironmentVariable, Func<string, FileAttributes?> getAttributes)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(getAttributes);

        _getAttributes = getAttributes;
        _roots = ResolveRoots(getEnvironmentVariable);
    }

    /// <inheritdoc />
    public CloudPathKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return CloudPathKind.None;

        try
        {
            var attributes = _getAttributes(path);
            if (attributes is { } value && (value & PlaceholderMask) != 0) return CloudPathKind.Placeholder;

            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var root in _roots)
            {
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return CloudPathKind.SyncedFolder;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // A malformed or inaccessible path is simply "not known to be synced".
        }

        return CloudPathKind.None;
    }

    private static string[] ResolveRoots(Func<string, string?> getEnvironmentVariable)
    {
        var roots = new List<string>();
        foreach (var variable in RootVariables)
        {
            var value = getEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) continue;

            try
            {
                if (!Path.IsPathRooted(value)) continue;

                var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // A bare drive root ("C:") would claim the whole drive — never a OneDrive root.
                if (full.Length <= 2 || roots.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;

                roots.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Unusable variable value — ignored.
            }
        }

        return [.. roots];
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
