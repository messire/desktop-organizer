namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Snapshot of the Recycle Bin contents across all drives.
/// </summary>
/// <param name="IsEmpty">Whether the Recycle Bin currently has no items.</param>
/// <param name="ItemCount">Number of items in the Recycle Bin.</param>
/// <param name="TotalSize">Total size in bytes of items in the Recycle Bin.</param>
public readonly record struct RecycleBinState(bool IsEmpty, long ItemCount, long TotalSize)
{
    /// <summary>An empty / unknown Recycle Bin state (safe fallback).</summary>
    public static readonly RecycleBinState Empty = new(true, 0, 0);
}
