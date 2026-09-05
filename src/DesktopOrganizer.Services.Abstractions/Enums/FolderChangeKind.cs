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
