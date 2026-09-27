using System.IO;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal sealed class FileItemRenameService : IFileItemRenameService
{
    private readonly IPanelItemStorageService _itemStorage;
    private readonly IAppLog? _appLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileItemRenameService"/> class.
    /// </summary>
    /// <param name="itemStorage">
    /// Owns the app-managed storage folder; consulted to decide whether a rename must go through
    /// <see cref="IPanelItemStorageService.RenameManaged"/> instead of a plain file-system move.
    /// </param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public FileItemRenameService(IPanelItemStorageService itemStorage, IAppLog? appLog = null)
    {
        _itemStorage = itemStorage;
        _appLog = appLog;
    }

    /// <summary>
    /// The log this instance writes failures to: the one explicitly supplied, falling back to the
    /// process-wide ambient <see cref="AppLog.Current"/> for tests that construct this type directly
    /// without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public RenameResult Rename(string path, string newName)
    {
        if (!FileNameValidation.IsValid(newName))
            return new RenameResult(RenameOutcome.InvalidName, null);

        try
        {
            if (_itemStorage.IsManaged(path))
            {
                // The storage service owns its own root and manifest — it alone is allowed to
                // rename inside it. It already re-validates existence/collision internally and
                // never throws, and reports the specific outcome (NotFound, NameTaken, Failed, …)
                // itself — nothing to re-derive here, just forward it.
                return _itemStorage.RenameManaged(path, newName);
            }

            var isDirectory = Directory.Exists(path);
            var isFile = !isDirectory && File.Exists(path);
            if (!isDirectory && !isFile) return new RenameResult(RenameOutcome.NotFound, null);

            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentDir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parentDir)) return new RenameResult(RenameOutcome.Failed, null);

            var newPath = Path.Combine(parentDir, newName);

            // Exact same path (same case) — nothing to do, report success as a no-op.
            if (string.Equals(newPath, fullPath, StringComparison.Ordinal))
                return new RenameResult(RenameOutcome.Renamed, fullPath);

            // A case-only rename (e.g. "Report.pdf" -> "report.pdf") must not be rejected as a
            // collision against itself — File/Directory.Exists is case-insensitive on Windows and
            // would otherwise always find "the file" at its own new (differently-cased) path.
            var sameIgnoringCase = string.Equals(newPath, fullPath, StringComparison.OrdinalIgnoreCase);

            if (!sameIgnoringCase && (File.Exists(newPath) || Directory.Exists(newPath)))
                return new RenameResult(RenameOutcome.NameTaken, null);

            if (isDirectory) Directory.Move(fullPath, newPath);
            else File.Move(fullPath, newPath);

            return new RenameResult(RenameOutcome.Renamed, newPath);
        }
        catch (Exception ex)
        {
            // Never throws out of this service — any unexpected failure (locked file, permissions,
            // disk full, a race where the file vanished between the existence check and the move,
            // …) is reported as Failed instead. The file/folder is left as-is: either the move above
            // never ran, or it threw before completing (File.Move/Directory.Move don't leave a
            // partially-renamed item behind on failure). newName is deliberately not logged: unlike
            // path (a full path, scrubbed to <path:.ext> by LogScrubber), it's a bare file/folder name
            // with no path prefix to scrub, so logging it verbatim would leak it into the log file.
            Log.Error("FileItemRenameService", "RenameFailed", ex, new KeyValuePair<string, object?>("path", path));
            return new RenameResult(RenameOutcome.Failed, null);
        }
    }
}
