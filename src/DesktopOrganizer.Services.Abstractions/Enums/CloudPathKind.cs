namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// How <see cref="ICloudFolderDetector.Classify"/> sees a path with respect to cloud synchronization
/// (ADR-009 amendment 2026-10-10).
/// </summary>
/// <remarks>
/// <c>default(CloudPathKind)</c> is <see cref="None"/> ("not synchronized"), so a loose test double
/// returning the default value never makes a caller show a cloud warning.
/// </remarks>
public enum CloudPathKind
{
    /// <summary>Nothing indicates that the path is synchronized by a cloud client.</summary>
    None,

    /// <summary>
    /// The path lies inside a OneDrive-synchronized root (<c>%OneDrive%</c>, <c>%OneDriveConsumer%</c>,
    /// <c>%OneDriveCommercial%</c>) — including a Desktop moved there by Known Folder Move.
    /// </summary>
    SyncedFolder,

    /// <summary>
    /// The item is a cloud placeholder: its attributes say its data is not (fully) local
    /// (<c>RECALL_ON_DATA_ACCESS</c>, <c>RECALL_ON_OPEN</c>, <c>OFFLINE</c>).
    /// </summary>
    Placeholder,
}
