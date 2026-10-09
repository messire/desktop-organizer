namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Tells whether a file or folder a user is about to drop into a panel is synchronized by a cloud client
/// (OneDrive). A panel drop <i>moves</i> the item into app-managed storage (ADR-009), which takes it out of
/// the cloud client's sync scope — the UI uses this to warn the user once (ADR-009 amendment 2026-10-10).
/// Pure classification: reads environment variables and file attributes only, never touches file content
/// (so it cannot trigger a placeholder download) and never throws.
/// </summary>
public interface ICloudFolderDetector
{
    /// <summary>
    /// Classifies <paramref name="path"/>. A <see cref="CloudPathKind.Placeholder"/> verdict wins over
    /// <see cref="CloudPathKind.SyncedFolder"/>.
    /// </summary>
    /// <param name="path">A file or folder path (may not exist; a blank or malformed path is <see cref="CloudPathKind.None"/>).</param>
    /// <returns>The classification; <see cref="CloudPathKind.None"/> on any failure.</returns>
    CloudPathKind Classify(string? path);
}
