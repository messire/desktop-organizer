namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// What <see cref="IPanelItemStorageService.CopyManaged"/> did with a managed panel item.
/// </summary>
public enum ManagedCopyOutcome
{
    /// <summary>
    /// The item was duplicated into a brand-new per-item directory and a manifest entry was recorded for
    /// the copy. <see cref="ManagedCopyResult.CopyPath"/> holds the new managed path.
    /// </summary>
    Copied,

    /// <summary>The given path isn't a single managed item (outside managed storage, the storage root itself, or a bare per-item GUID directory). Nothing was touched.</summary>
    NotManaged,

    /// <summary>The managed item no longer exists on disk. Nothing was touched.</summary>
    SourceMissing,

    /// <summary>
    /// The item (a directory) exceeds the same safety cap <see cref="IPanelItemStorageService.MoveInDirectory"/>
    /// applies to a cross-volume import (file count or total size). Nothing was copied.
    /// </summary>
    TooLarge,

    /// <summary>
    /// There isn't enough free space on the destination volume for the item, with a 5% safety margin.
    /// Nothing was copied.
    /// </summary>
    InsufficientSpace,

    /// <summary>
    /// The copy could not be completed for any other reason (a locked file, missing permissions, disk
    /// full, a manifest write failure, an unexpected exception, …). Any partial destination created for
    /// this attempt is cleaned up — the source is never touched by this method.
    /// </summary>
    Failed,
}

/// <summary>
/// Result of <see cref="IPanelItemStorageService.CopyManaged"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="CopyPath">The new managed path, set only when <paramref name="Outcome"/> is <see cref="ManagedCopyOutcome.Copied"/>; otherwise <see langword="null"/>.</param>
public readonly record struct ManagedCopyResult(ManagedCopyOutcome Outcome, string? CopyPath);
