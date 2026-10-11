using System.IO;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Computes every on-disk path the versioned JSON application-state store touches (ADR-024 D-4):
/// the primary document, its <c>.bak</c>, per-write temp files, quarantine files, and the
/// process-lifetime advisory lock. A single source of truth for these paths so
/// <see cref="AtomicJsonFileWriter"/>, <see cref="JsonApplicationStateStore"/>, and tests never
/// duplicate the naming scheme.
/// </summary>
/// <remarks>
/// The advisory lock (<see cref="LockFilePath"/>) deliberately lives at the storage
/// <b>root</b> (<c>.storage-lock</c> directly under <see cref="StorageRoot"/> — Inno channel
/// <c>%LOCALAPPDATA%\DesktopOrganizer\</c>, Store channel <c>%USERPROFILE%\DesktopOrganizer\</c>), one level above
/// <see cref="StateDirectory"/> (<c>...\DesktopOrganizer\State\</c>) — see ADR-024 D-8: it
/// conceptually covers both this store's <c>State\</c> directory and
/// <c>PanelItems\.manifest.json</c>, the other surface named by ADR-023 R-15.
/// </remarks>
internal sealed class StateStorePaths
{
    private const string StateFolderName = "State";
    private const string PrimaryFileName = "application-state.json";
    private const string BackupFileName = PrimaryFileName + ".bak";
    private const string LockFileName = ".storage-lock";
    private const string TempFilePrefix = PrimaryFileName + ".tmp-";
    private const string QuarantineFileStem = "application-state.corrupt-";
    private const string QuarantineFileSuffix = ".json";
    private const string PreMigrationBackupFileStem = "application-state.premigration-v";
    private const string PreMigrationBackupFileSuffix = ".json";

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStorePaths"/> class, using the real
    /// per-user app-data folder as storage root (<see cref="AppStorageRoot.Default"/> — Debug and
    /// Release builds resolve to different folders, see <see cref="AppStorageRoot"/>).
    /// </summary>
    public StateStorePaths()
        : this(AppStorageRoot.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStorePaths"/> class with an explicit
    /// storage root. Internal — used by tests to point at a disposable temp folder instead of the
    /// real per-user app-data location (mirrors <c>PanelItemStorageService</c>'s internal
    /// root-override constructor).
    /// </summary>
    /// <param name="storageRoot">
    /// The root folder the state store, and everything else under the storage root
    /// (e.g. <c>PanelItems</c>), lives under.
    /// </param>
    internal StateStorePaths(string storageRoot)
    {
        StorageRoot = storageRoot;
        StateDirectory = Path.Combine(storageRoot, StateFolderName);
        PrimaryFilePath = Path.Combine(StateDirectory, PrimaryFileName);
        BackupFilePath = Path.Combine(StateDirectory, BackupFileName);
        LockFilePath = Path.Combine(storageRoot, LockFileName);
    }

    /// <summary>Root of all DesktopOrganizer per-user app-data storage (<see cref="AppStorageRoot.Default"/>: Inno channel <c>%LOCALAPPDATA%\DesktopOrganizer</c>, Store channel <c>%USERPROFILE%\DesktopOrganizer</c>).</summary>
    public string StorageRoot { get; }

    /// <summary>Folder the versioned JSON document lives under (<c>...\DesktopOrganizer\State</c>).</summary>
    public string StateDirectory { get; }

    /// <summary>The primary, currently-committed document.</summary>
    public string PrimaryFilePath { get; }

    /// <summary>
    /// The previous primary, preserved by <see cref="File.Replace(string, string, string)"/> as part
    /// of the atomic write protocol (ADR-024 D-6). Never written to directly by this store.
    /// </summary>
    public string BackupFilePath { get; }

    /// <summary>
    /// The process-lifetime advisory lock file (ADR-024 D-8), one level above <see cref="StateDirectory"/> —
    /// see type remarks.
    /// </summary>
    public string LockFilePath { get; }

    /// <summary>
    /// Glob pattern (for <see cref="Directory.GetFiles(string, string)"/>) matching every orphaned
    /// temp file a crashed write could have left behind under <see cref="StateDirectory"/>.
    /// </summary>
    public string TempFileSearchPattern => TempFilePrefix + "*";

    /// <summary>
    /// Glob pattern (for <see cref="Directory.GetFiles(string, string)"/>) matching every quarantine
    /// file under <see cref="StateDirectory"/>.
    /// </summary>
    public string QuarantineSearchPattern => QuarantineFileStem + "*" + QuarantineFileSuffix;

    /// <summary>
    /// Builds a fresh, guaranteed-unique temp file path in <see cref="StateDirectory"/> for a single
    /// write attempt (<c>application-state.json.tmp-{guid}</c> — ADR-024 D-4). Deliberately in the
    /// same directory as <see cref="PrimaryFilePath"/>: <see cref="File.Replace(string, string, string)"/>
    /// is only atomic when source and destination share a volume, and same-directory is the simplest
    /// way to guarantee that.
    /// </summary>
    public string NewTempFilePath() => PrimaryFilePath + $".tmp-{Guid.NewGuid():N}";

    /// <summary>
    /// Builds a fresh, collision-free quarantine file path
    /// (<c>application-state.corrupt-{yyyyMMdd-HHmmss}.json</c> — ADR-024 D-4, carrying forward the
    /// convention from the pre-Phase-4 <c>WindowManager.BackupPendingCorruptStateIfAny</c>). A
    /// single <see cref="JsonApplicationStateStore.Load"/> call can quarantine both the primary and
    /// the backup file within the same wall-clock second (D-7 rung 4), so a numeric suffix is
    /// appended on collision — same discipline as
    /// <c>PanelItemStorageService.ResolveCollisionFreeName</c>.
    /// </summary>
    public string NewQuarantinePath()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var basePath = Path.Combine(StateDirectory, $"{QuarantineFileStem}{stamp}{QuarantineFileSuffix}");
        if (!File.Exists(basePath)) return basePath;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(StateDirectory, $"{QuarantineFileStem}{stamp}-{suffix}{QuarantineFileSuffix}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Builds the deterministic, permanent pre-migration snapshot path for a document that was found
    /// on disk at <paramref name="fromSchemaVersion"/> and is about to be migrated and persisted over
    /// (<c>application-state.premigration-v{fromSchemaVersion}.json</c>) — MINOR fix (fix-cycle 1,
    /// docs/PLANS/workspace-grid-migration.md §10.5). Unlike <see cref="NewQuarantinePath"/> (always
    /// unique, timestamped), this is deterministic by design: the same <paramref name="fromSchemaVersion"/>
    /// always maps to the same path, so <see cref="JsonApplicationStateStore"/> can cheaply check
    /// "have I already preserved a v{N}-&gt;current migration's input?" with a plain
    /// <see cref="File.Exists(string)"/> before writing, and never overwrite an existing snapshot — the
    /// whole point is a copy that survives every subsequent ordinary <c>Save()</c>, unlike <c>.bak</c>
    /// (which the very next save rotates) or a quarantine file (capped by
    /// <see cref="AtomicJsonFileWriter.EnforceQuarantineRetention"/>'s 5-file retention).
    /// </summary>
    /// <param name="fromSchemaVersion">The schema version the on-disk document was found at before migration.</param>
    public string PreMigrationBackupPath(int fromSchemaVersion) =>
        Path.Combine(StateDirectory, $"{PreMigrationBackupFileStem}{fromSchemaVersion}{PreMigrationBackupFileSuffix}");
}
