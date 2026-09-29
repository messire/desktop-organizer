namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// A single item's icon request, submitted to an <see cref="IIconLoadGroup"/> (ADR-046). Carries no
/// file-system/shell work of its own — resolving <see cref="Path"/> happens entirely on the queue's own
/// background worker.
/// </summary>
/// <param name="Path">Item path as tracked by the caller. Matched back to results ordinal-ignore-case.</param>
/// <param name="PixelSize">
/// Requested physical pixel size; the queue snaps it to a shell-friendly bucket
/// (<see cref="IconPixelSize.Snap"/>) before using it as (part of) the cache/dedup key.
/// </param>
public sealed record IconLoadRequest(string Path, int PixelSize);
