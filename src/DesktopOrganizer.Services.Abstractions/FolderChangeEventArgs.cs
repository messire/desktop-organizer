namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Describes the kind of change that occurred inside a watched folder.
/// </summary>
public enum FolderChangeKind
{
    /// <summary>A file or directory was created.</summary>
    Created,

    /// <summary>A file or directory was deleted.</summary>
    Deleted,

    /// <summary>A file or directory was renamed or moved.</summary>
    Renamed,

    /// <summary>
    /// The watcher's internal buffer overflowed or an error occurred;
    /// the consumer must perform a full resync of the folder contents.
    /// </summary>
    ResyncRequired,
}

/// <summary>
/// Provides data for the <see cref="IFolderWatcher.Changed"/> event.
/// </summary>
public sealed class FolderChangeEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of <see cref="FolderChangeEventArgs"/>.
    /// </summary>
    /// <param name="kind">The kind of change that occurred.</param>
    /// <param name="path">The full path of the affected file or directory.</param>
    /// <param name="oldPath">
    /// The previous full path, populated only when <paramref name="kind"/> is
    /// <see cref="FolderChangeKind.Renamed"/>.
    /// </param>
    public FolderChangeEventArgs(FolderChangeKind kind, string path, string? oldPath = null)
    {
        Kind = kind;
        Path = path;
        OldPath = oldPath;
    }

    /// <summary>Gets the kind of change that occurred.</summary>
    public FolderChangeKind Kind { get; }

    /// <summary>Gets the full path of the affected file or directory.</summary>
    public string Path { get; }

    /// <summary>
    /// Gets the previous full path of the item.
    /// This property is only meaningful when <see cref="Kind"/> is
    /// <see cref="FolderChangeKind.Renamed"/>.
    /// </summary>
    public string? OldPath { get; }
}
