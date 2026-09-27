using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Storage;

/// <inheritdoc cref="IStateRestoreCommand" />
internal sealed class StateRestoreCommand : IStateRestoreCommand
{
    private readonly StateStorePaths _paths;
    private readonly StateMigrationRunner _migrationRunner;
    private readonly AtomicJsonFileWriter _writer;
    private readonly Func<IDiagnosticsLog> _diagnostics;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateRestoreCommand"/> class for production use,
    /// against the real per-user app-data storage root.
    /// </summary>
    /// <param name="migrations">The full migration chain used to bring a restored candidate up to the current schema version.</param>
    /// <param name="diagnostics">Lazily-resolved diagnostics sink.</param>
    public StateRestoreCommand(IEnumerable<ISettingsMigration> migrations, Func<IDiagnosticsLog> diagnostics)
        : this(new StateStorePaths(), new StateMigrationRunner(migrations), diagnostics)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateRestoreCommand"/> class. Test-only overload
    /// with an explicit storage root and migration runner — mirrors
    /// <see cref="JsonApplicationStateStore"/>'s own test constructor, letting tests point at a
    /// disposable temp folder and a minimal/fake migration chain.
    /// </summary>
    /// <param name="paths">The storage root to use instead of the real per-user app-data folder.</param>
    /// <param name="migrationRunner">The migration runner to use instead of building one from the production migration chain.</param>
    /// <param name="diagnostics">Lazily-resolved diagnostics sink.</param>
    internal StateRestoreCommand(StateStorePaths paths, StateMigrationRunner migrationRunner, Func<IDiagnosticsLog> diagnostics)
        : this(paths, migrationRunner, diagnostics, testForceWriteFailureAt: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateRestoreCommand"/> class. Test-only overload
    /// additionally exposing <see cref="AtomicJsonFileWriter"/>'s crash-injection seam — mirrors
    /// <see cref="JsonApplicationStateStore"/>'s own equivalent constructor — so a test can assert what
    /// <see cref="Restore"/> does when the write of the new primary file itself fails (M-4: this must
    /// never leave the on-disk primary file missing or the old primary's content lost).
    /// </summary>
    /// <param name="paths">The storage root to use instead of the real per-user app-data folder.</param>
    /// <param name="migrationRunner">The migration runner to use instead of building one from the production migration chain.</param>
    /// <param name="diagnostics">Lazily-resolved diagnostics sink.</param>
    /// <param name="testForceWriteFailureAt">
    /// When non-<see langword="null"/>, forwarded to <see cref="AtomicJsonFileWriter"/> to inject a
    /// write failure at the given <see cref="StateWriteStage"/>.
    /// </param>
    internal StateRestoreCommand(
        StateStorePaths paths,
        StateMigrationRunner migrationRunner,
        Func<IDiagnosticsLog> diagnostics,
        Func<StateWriteStage, bool>? testForceWriteFailureAt)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _migrationRunner = migrationRunner ?? throw new ArgumentNullException(nameof(migrationRunner));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _writer = new AtomicJsonFileWriter(paths, testForceWriteFailureAt);
    }

    /// <inheritdoc />
    public StateRestoreResult Restore()
    {
        var candidatePath = FindCandidate();
        if (candidatePath is null)
        {
            return new StateRestoreResult(
                StateRestoreOutcome.NoCandidateFound,
                null,
                null,
                "No .bak file and no quarantine file found — nothing to restore from.");
        }

        if (!TryValidateCandidate(candidatePath, out var migratedDocument, out var validationError))
        {
            return new StateRestoreResult(
                StateRestoreOutcome.CandidateInvalid,
                candidatePath,
                null,
                $"Candidate '{Path.GetFileName(candidatePath)}' failed validation ({validationError}) — nothing was changed.");
        }

        // M-4: write the new primary FIRST, quarantine-for-history only AFTER that succeeds. The old
        // order (quarantine the existing primary — a destructive File.Move — before ever attempting
        // the write) meant a failed write left the user with no primary file at all: the just-moved
        // primary was gone, and nothing had landed in its place. Writing first means a failed write
        // leaves the previous primary exactly where it was (Write() never moves committed state on
        // failure — same invariant AtomicJsonFileWriter guarantees for JsonApplicationStateStore.Save),
        // and — because the primary already existed — AtomicJsonFileWriter.Write's own
        // File.Replace(tempPath, primary, backupFilePath) rotates that previous primary into .bak as
        // an intrinsic, already-atomic part of the write itself, with no separate destructive step of
        // this command's own needed at all.
        var primaryExistedBeforeWrite = File.Exists(_paths.PrimaryFilePath);

        try
        {
            _writer.Write(migratedDocument);
        }
        catch (Exception ex)
        {
            Diag(
                "StateRestoreFailed",
                new KeyValuePair<string, object?>("errorType", ex.GetType().Name));
            return new StateRestoreResult(
                StateRestoreOutcome.Failed,
                candidatePath,
                null,
                $"Restoring from '{Path.GetFileName(candidatePath)}' failed while writing the new primary file: {ex.GetType().Name}. The previous primary file, if any, was left unchanged.");
        }

        // The write succeeded. If a primary existed before, File.Replace already durably preserved it
        // as .bak (see remarks above) — copy (never move: .bak must keep describing "one generation
        // behind the current primary" for the next write's own atomic-replace backup, not freeze at
        // the pre-restore primary) that content into a dedicated, timestamped quarantine file so the
        // pre-restore primary remains inspectable on its own, exactly as this command promised before
        // this fix existed.
        var quarantinedPrimaryPath = primaryExistedBeforeWrite ? QuarantinePreviousPrimaryAfterWrite() : null;

        Diag("StateRecoveredFromBackup", new KeyValuePair<string, object?>("reason", "ManualRestoreCommand"));

        var restoredMessage = quarantinedPrimaryPath is null
            ? $"Restored from '{Path.GetFileName(candidatePath)}'. No previous primary file existed."
            : $"Restored from '{Path.GetFileName(candidatePath)}'. The previous primary file was quarantined as '{Path.GetFileName(quarantinedPrimaryPath)}'.";

        return new StateRestoreResult(
            StateRestoreOutcome.Restored,
            candidatePath,
            quarantinedPrimaryPath,
            restoredMessage);
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Prefers <c>.bak</c>; falls back to the newest quarantine file by last-write time. Exactly one
    /// candidate is ever returned — see <see cref="IStateRestoreCommand"/> remarks.
    /// </summary>
    private string? FindCandidate()
    {
        if (File.Exists(_paths.BackupFilePath)) return _paths.BackupFilePath;

        if (!Directory.Exists(_paths.StateDirectory)) return null;

        string[] quarantineFiles;
        try
        {
            quarantineFiles = Directory.GetFiles(_paths.StateDirectory, _paths.QuarantineSearchPattern);
        }
        catch (Exception)
        {
            return null;
        }

        return quarantineFiles.Length == 0
            ? null
            : quarantineFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    /// <summary>
    /// Parses <paramref name="candidatePath"/> and runs it through the full migration chain up to the
    /// current schema version. <see langword="false"/> — with <paramref name="migratedDocument"/> left
    /// as an empty envelope-only placeholder never used by the caller — for anything short of a fully
    /// successful migration: not valid JSON, no valid <c>schemaVersion</c>, a gap in the migration
    /// chain, or a candidate newer than this build's current schema version (this command never
    /// attempts to interpret a schema it does not fully understand, mirroring ADR-024 D-7 rung 7).
    /// </summary>
    private bool TryValidateCandidate(string candidatePath, out JsonObject migratedDocument, out string validationError)
    {
        migratedDocument = new JsonObject();
        validationError = string.Empty;

        JsonObject parsed;
        try
        {
            var text = File.ReadAllText(candidatePath);
            if (JsonNode.Parse(text) is not JsonObject parsedObject)
            {
                validationError = "not a JSON object";
                return false;
            }

            parsed = parsedObject;
        }
        catch (Exception ex)
        {
            validationError = $"failed to parse as JSON ({ex.GetType().Name})";
            return false;
        }

        if (!StateEnvelope.TryGetSchemaVersion(parsed, out _))
        {
            validationError = "missing or invalid schemaVersion";
            return false;
        }

        try
        {
            var (migrated, _) = _migrationRunner.MigrateToCurrent(parsed, StateEnvelope.CurrentSchemaVersion);
            migratedDocument = migrated;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            validationError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Copies <see cref="StateStorePaths.BackupFilePath"/> — which, by the time this is called,
    /// <see cref="AtomicJsonFileWriter.Write"/>'s own <see cref="File.Replace(string, string, string, bool)"/>
    /// has already durably repointed at the pre-restore primary's content (see the call site's remarks)
    /// — into a fresh, timestamped quarantine file, for history/manual inspection. Best-effort: if the
    /// copy itself fails (permissions, disk full), the restore has still fully succeeded (the new
    /// primary is on disk and correct) — this only loses the convenience quarantine copy, never data
    /// that was not already durably preserved as <c>.bak</c>.
    /// </summary>
    private string? QuarantinePreviousPrimaryAfterWrite()
    {
        if (!File.Exists(_paths.BackupFilePath)) return null;

        var quarantinePath = _paths.NewQuarantinePath();
        try
        {
            Directory.CreateDirectory(_paths.StateDirectory);
            File.Copy(_paths.BackupFilePath, quarantinePath, overwrite: false);
            Diag("StateQuarantined", new KeyValuePair<string, object?>("bytes", SafeFileLength(quarantinePath)));
            return quarantinePath;
        }
        catch (Exception ex)
        {
            AppLog.Current.Error("StateRestoreCommand", "QuarantinePreviousPrimaryFailed", ex);
            return null;
        }
    }

    private void Diag(string name, params KeyValuePair<string, object?>[] payload)
    {
        try
        {
            _diagnostics().Write("state", name, null, payload);
        }
        catch (Exception)
        {
            // Diagnostics must never affect this command's own outcome.
        }
    }
}
