namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Outcome of an <see cref="IFileItemRenameService.Rename"/> attempt.
/// </summary>
public enum RenameOutcome
{
    /// <summary>
    /// The file or folder was renamed on disk (and, for a managed item, the manifest entry was
    /// updated to match — see <see cref="IPanelItemStorageService.RenameManaged"/>).
    /// </summary>
    Renamed,

    /// <summary>
    /// The requested new name failed validation (empty/whitespace, contains a character
    /// <see cref="System.IO.Path.GetInvalidFileNameChars"/> disallows, is <c>"."</c>/<c>".."</c>,
    /// ends with a trailing space or dot, or is a reserved Windows device name such as <c>CON</c>
    /// or <c>COM1</c>, with or without an extension). Nothing on disk was touched.
    /// </summary>
    InvalidName,

    /// <summary>
    /// A file or folder already exists at the resolved destination. Nothing on disk was touched —
    /// unlike Explorer's own dialog offering a numeric suffix, this service never silently renames
    /// under a different name than the one requested; the caller must ask the user again.
    /// </summary>
    NameTaken,

    /// <summary><c>path</c> in <see cref="IFileItemRenameService.Rename"/> doesn't point at an existing file or folder. Nothing on disk was touched.</summary>
    NotFound,

    /// <summary>
    /// Reserved for a source that categorically cannot be renamed. Not currently produced by the
    /// built-in <c>FileItemRenameService</c> implementation — kept for forward compatibility with
    /// callers/implementations that may need to reject a rename outright (e.g. a read-only source).
    /// </summary>
    NotSupported,

    /// <summary>
    /// The rename could not be completed for any other reason (locked file, missing permissions,
    /// disk full, a managed-item manifest write failure, an unexpected exception, …). Nothing on
    /// disk was touched — a rename either fully succeeds or leaves the original file/folder exactly
    /// as it was; there is no partial/interrupted state a caller needs to reconcile.
    /// </summary>
    Failed,
}

/// <summary>
/// Result of an <see cref="IFileItemRenameService.Rename"/> attempt.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="NewPath">
/// The resulting full path, set only when <paramref name="Outcome"/> is
/// <see cref="RenameOutcome.Renamed"/>; <see langword="null"/> for every other outcome.
/// </param>
public readonly record struct RenameResult(RenameOutcome Outcome, string? NewPath);

/// <summary>
/// The single point of mutation for a panel item's name: renaming is always a real, physical
/// rename of the underlying file or folder (there is no separate, disk-independent display name to
/// mutate instead — see ADR-013 Decision 4). Dispatches based on ownership:
/// <list type="bullet">
/// <item>
/// A managed item (<see cref="IPanelItemStorageService.IsManaged"/> is <see langword="true"/>) is
/// renamed through <see cref="IPanelItemStorageService.RenameManaged"/> — only that service is
/// allowed to touch its own storage root and manifest; this service never does so directly.
/// </item>
/// <item>
/// Everything else (a regular desktop file/folder, or an item inside a Folder Portal's live
/// mirrored directory) is renamed with a plain <see cref="System.IO.File.Move(string, string)"/> /
/// <see cref="System.IO.Directory.Move(string, string)"/> within the same parent directory.
/// </item>
/// </list>
/// Implementations must never throw — every failure mode is reported through
/// <see cref="RenameResult.Outcome"/>, and on any outcome other than
/// <see cref="RenameOutcome.Renamed"/> the file/folder at <c>path</c> is left exactly as it was.
/// </summary>
public interface IFileItemRenameService
{
    /// <summary>
    /// Attempts to rename the file or folder currently at <paramref name="path"/> to
    /// <paramref name="newName"/> (a bare name — no directory component — within the same parent
    /// directory <paramref name="path"/> currently lives in).
    /// </summary>
    /// <param name="path">The current full path of a panel item.</param>
    /// <param name="newName">The desired new bare name, including the extension for a file.</param>
    RenameResult Rename(string path, string newName);
}
