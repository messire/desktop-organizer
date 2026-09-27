using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Production <see cref="IApplicationStateStore"/>: implements the full read/recovery ladder
/// (ADR-024 D-7), the executable write barrier and no-op guard (D-11), and the "a failed write never
/// moves committed state" invariant (D-6 step 5) on top of <see cref="AtomicJsonFileWriter"/>,
/// <see cref="StateEnvelope"/>, and <see cref="StateMigrationRunner"/>.
/// </summary>
/// <remarks>
/// <b>Layering (D-1/D-3):</b> this type only ever manipulates <see cref="JsonObject"/> payloads. It
/// has no knowledge of panels, pages, or any other UI-layer concept — "empty structural state" at
/// this layer is a literal empty <see cref="JsonObject"/> (no payload keys at all); turning that into
/// a friendly first-run default (e.g. a single "default" page) is the UI-layer mapper's job
/// (ADR-024 §2.3, C6), not this store's.
/// </remarks>
internal sealed class JsonApplicationStateStore : IApplicationStateStore
{
    private readonly StateStorePaths _paths;
    private readonly AtomicJsonFileWriter _writer;
    private readonly IStorageLock _storageLock;
    private readonly ILegacyStateImporter _legacyImporter;
    private readonly StateMigrationRunner _migrationRunner;
    private readonly Func<IDiagnosticsLog> _diagnostics;
    private readonly object _stateLock = new();

    private bool _loaded;
    private bool _newerSchemaLockout;
    private string? _lastCommittedPayloadJson;

    // Guards QuarantineLoadedDocument (ADR-024 finalization C-1) so only the first call after a given
    // Load() actually copies the primary file — reset alongside _loaded in Load().
    private bool _partialLoadQuarantined;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonApplicationStateStore"/> class for
    /// production use, against the real per-user app-data storage root.
    /// </summary>
    /// <param name="storageLock">
    /// The process-lifetime advisory lock (ADR-024 D-8). <see cref="IApplicationStateStore.IsWritable"/>
    /// tracks <see cref="IStorageLock.IsHeld"/> live; this store never calls
    /// <see cref="IStorageLock.TryAcquire"/> itself — that happens once, earlier, in the intended
    /// <c>Program.Main</c> ordering (D-11), before this store is even constructed.
    /// </param>
    /// <param name="legacyImporter">Reads the five legacy <c>user.config</c> structural keys (D-7 rung 5).</param>
    /// <param name="migrations">The full migration chain, order-independent — <see cref="StateMigrationRunner"/> sorts and validates it.</param>
    /// <param name="diagnostics">
    /// Lazily-resolved diagnostics sink (ADR-023 R-12 pattern) — never materialized just because this
    /// store was constructed, only when an event actually needs to be written.
    /// </param>
    public JsonApplicationStateStore(
        IStorageLock storageLock,
        ILegacyStateImporter legacyImporter,
        IEnumerable<ISettingsMigration> migrations,
        Func<IDiagnosticsLog> diagnostics)
        : this(new StateStorePaths(), storageLock, legacyImporter, new StateMigrationRunner(migrations), diagnostics, testForceWriteFailureAt: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonApplicationStateStore"/> class. Test-only
    /// overload with an explicit storage root and migration runner — lets
    /// <c>JsonApplicationStateStoreTests</c> point at a disposable temp folder and supply a
    /// <see cref="StateMigrationRunner"/> built from fake/minimal <see cref="ISettingsMigration"/>
    /// steps, so the recovery-ladder tests stay independent of the real production migration chain
    /// (<c>V1ToV2Migration</c>/<c>V2ToV3Migration</c>) and its own dedicated golden-fixture coverage.
    /// </summary>
    /// <param name="paths">The storage root to use instead of the real per-user app-data folder.</param>
    /// <param name="storageLock">The process-lifetime advisory lock (ADR-024 D-8).</param>
    /// <param name="legacyImporter">Reads the five legacy <c>user.config</c> structural keys (D-7 rung 5).</param>
    /// <param name="migrationRunner">The migration runner to use instead of building one from the production migration chain.</param>
    /// <param name="diagnostics">Lazily-resolved diagnostics sink.</param>
    internal JsonApplicationStateStore(
        StateStorePaths paths,
        IStorageLock storageLock,
        ILegacyStateImporter legacyImporter,
        StateMigrationRunner migrationRunner,
        Func<IDiagnosticsLog> diagnostics)
        : this(paths, storageLock, legacyImporter, migrationRunner, diagnostics, testForceWriteFailureAt: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonApplicationStateStore"/> class. Test-only
    /// overload additionally exposing <see cref="AtomicJsonFileWriter"/>'s crash-injection seam, so a
    /// test can assert that a write failing at any given <see cref="StateWriteStage"/> never moves
    /// this store's committed state.
    /// </summary>
    /// <param name="paths">The storage root to use instead of the real per-user app-data folder.</param>
    /// <param name="storageLock">The process-lifetime advisory lock (ADR-024 D-8).</param>
    /// <param name="legacyImporter">Reads the five legacy <c>user.config</c> structural keys (D-7 rung 5).</param>
    /// <param name="migrationRunner">The migration runner to use instead of building one from the production migration chain.</param>
    /// <param name="diagnostics">Lazily-resolved diagnostics sink.</param>
    /// <param name="testForceWriteFailureAt">
    /// When non-<see langword="null"/>, forwarded to <see cref="AtomicJsonFileWriter"/> to inject a
    /// write failure at the given <see cref="StateWriteStage"/>.
    /// </param>
    internal JsonApplicationStateStore(
        StateStorePaths paths,
        IStorageLock storageLock,
        ILegacyStateImporter legacyImporter,
        StateMigrationRunner migrationRunner,
        Func<IDiagnosticsLog> diagnostics,
        Func<StateWriteStage, bool>? testForceWriteFailureAt)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _storageLock = storageLock ?? throw new ArgumentNullException(nameof(storageLock));
        _legacyImporter = legacyImporter ?? throw new ArgumentNullException(nameof(legacyImporter));
        _migrationRunner = migrationRunner ?? throw new ArgumentNullException(nameof(migrationRunner));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _writer = new AtomicJsonFileWriter(paths, testForceWriteFailureAt);
    }

    /// <inheritdoc />
    public bool IsWritable => _storageLock.IsHeld && !_newerSchemaLockout;

    /// <inheritdoc />
    public StateLoadResult Load()
    {
        StateLoadResult result;
        lock (_stateLock)
        {
            result = LoadCore();
            _loaded = true;
            _partialLoadQuarantined = false;
            if (result.Status == StateLoadStatus.NewerSchema) _newerSchemaLockout = true;
        }

        Diag(
            "StateLoaded",
            null,
            new KeyValuePair<string, object?>("status", result.Status.ToString()),
            new KeyValuePair<string, object?>("schemaVersionOnDisk", result.SchemaVersionOnDisk),
            new KeyValuePair<string, object?>("migrationsApplied", result.MigrationsApplied));

        return result;
    }

    /// <inheritdoc />
    public StateSaveOutcome Save(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_stateLock)
        {
            if (!_loaded)
            {
                Diag("StateWriteBarrierViolation", null, new KeyValuePair<string, object?>("caller", "Save"));
                return new StateSaveOutcome(StateSaveResult.SkippedBeforeLoad, null);
            }

            if (!IsWritable)
                return new StateSaveOutcome(StateSaveResult.SkippedReadOnly, null);

            foreach (var (key, _) in payload)
            {
                if (StateEnvelope.IsReservedKey(key))
                    throw new ArgumentException($"Payload must not contain reserved envelope key '{key}'.", nameof(payload));
            }

            var canonicalPayloadJson = payload.ToJsonString();
            if (_lastCommittedPayloadJson is not null && string.Equals(_lastCommittedPayloadJson, canonicalPayloadJson, StringComparison.Ordinal))
                return new StateSaveOutcome(StateSaveResult.SkippedNoChange, null);

            var envelope = StateEnvelope.Wrap(payload, StateEnvelope.CurrentSchemaVersion, DateTimeOffset.UtcNow, ResolveAppVersion());

            try
            {
                _writer.Write(envelope);
            }
            catch (AtomicWriteFailedException ex)
            {
                // Committed state (_lastCommittedPayloadJson) is deliberately left untouched here —
                // see IApplicationStateStore.Save remarks: a failed write must never move it, or the
                // next Save() with the same payload would wrongly be treated as SkippedNoChange even
                // though nothing actually landed on disk.
                Diag(
                    "StateSaveFailed",
                    null,
                    new KeyValuePair<string, object?>("stage", ex.Stage.ToString()),
                    new KeyValuePair<string, object?>("errorType", (ex.InnerException ?? ex).GetType().Name));
                return new StateSaveOutcome(StateSaveResult.Failed, ex.Stage);
            }

            _lastCommittedPayloadJson = canonicalPayloadJson;
            Diag(
                "StateSaved",
                null,
                new KeyValuePair<string, object?>("schemaVersion", StateEnvelope.CurrentSchemaVersion),
                new KeyValuePair<string, object?>("trigger", "immediate"));
            return new StateSaveOutcome(StateSaveResult.Saved, null);
        }
    }

    /// <inheritdoc />
    public void QuarantineLoadedDocument(string reason)
    {
        lock (_stateLock)
        {
            if (!_loaded || _partialLoadQuarantined) return;
            _partialLoadQuarantined = true;

            // Nothing committed to disk yet (FirstRun) — there is no pre-loss primary to preserve.
            if (!File.Exists(_paths.PrimaryFilePath)) return;

            try
            {
                Directory.CreateDirectory(_paths.StateDirectory);
                var quarantinePath = _paths.NewQuarantinePath();

                // Copy, not move (unlike QuarantineFile's recovery-ladder use): the primary is not
                // corrupt and must remain the committed state until Save() actually runs — this is a
                // safety-net snapshot of it, taken before a reduced payload can overwrite it.
                File.Copy(_paths.PrimaryFilePath, quarantinePath, overwrite: false);
                Diag(
                    "StateQuarantined",
                    null,
                    new KeyValuePair<string, object?>("reason", reason),
                    new KeyValuePair<string, object?>("bytes", SafeFileLength(quarantinePath)));
            }
            catch (Exception ex)
            {
                // Best effort — same degraded-but-safe posture as QuarantineFile: if the copy fails
                // (permissions, held handle), the primary is simply left as the only surviving copy,
                // same as before this method existed.
                AppLog.Current.Error(
                    "JsonApplicationStateStore",
                    "QuarantineLoadedDocumentFailed",
                    ex,
                    new KeyValuePair<string, object?>("reason", reason));
            }
        }
    }

    /// <summary>
    /// Empty structural state at this (generic JSON) layer — a literal empty object, no payload keys.
    /// See type remarks on why this store deliberately never invents UI-specific default keys.
    /// </summary>
    private static JsonObject EmptyPayload() => new();

    private static string ResolveAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
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
    /// Reads and parses <paramref name="path"/> as JSON, returning the <b>full</b> document —
    /// envelope keys included — for <see cref="StateMigrationRunner.MigrateToCurrent"/> to consume
    /// directly. <see langword="false"/> if the file is missing, not valid JSON, or has no valid
    /// top-level <c>schemaVersion"</c> — any of which counts as "corrupt" for the read/recovery
    /// ladder (ADR-024 D-7).
    /// </summary>
    private static bool TryReadFullDocument(string path, out JsonObject document, out int schemaVersion)
    {
        document = new JsonObject();
        schemaVersion = 0;

        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text) is not JsonObject parsed) return false;
            if (!StateEnvelope.TryGetSchemaVersion(parsed, out schemaVersion)) return false;

            document = parsed;
            return true;
        }
        catch (Exception)
        {
            document = new JsonObject();
            schemaVersion = 0;
            return false;
        }
    }

    /// <summary>Must be called under <see cref="_stateLock"/>.</summary>
    private StateLoadResult LoadCore()
    {
        _writer.SweepOrphanedTempFiles();
        _writer.EnforceQuarantineRetention();

        var primaryExists = File.Exists(_paths.PrimaryFilePath);
        var backupExists = File.Exists(_paths.BackupFilePath);

        if (primaryExists)
        {
            if (TryReadFullDocument(_paths.PrimaryFilePath, out var primaryDocument, out var primarySchemaVersion))
            {
                // Rung 7: this build is older than whatever last wrote the primary file. Never
                // migrate downward, never touch the file — see StateLoadStatus.NewerSchema remarks.
                if (primarySchemaVersion > StateEnvelope.CurrentSchemaVersion)
                    return new StateLoadResult(StateLoadStatus.NewerSchema, EmptyPayload(), primarySchemaVersion, 0, null);

                // Rung 1: primary parses and is at a version this build understands. If a migration
                // actually ran, the upgraded document must be written back — otherwise a second Load()
                // would still see the old on-disk schemaVersion and re-apply the same migrations
                // every single time a process starts (violating "migrationsApplied == 0 on the next
                // load" once a document is already current). If nothing changed, there is nothing to
                // write — disk already matches in substance.
                if (!TryMigrate(primaryDocument, out var migrated, out var applied, out var migrationFailure))
                    return HandleMigrationFailure(migrationFailure!, primarySchemaVersion, _paths.PrimaryFilePath);

                if (applied > 0) WritePreMigrationBackupIfMissing(primaryDocument, primarySchemaVersion);
                PersistAfterLoad(migrated, forcePersist: applied > 0);
                return new StateLoadResult(StateLoadStatus.Loaded, migrated, primarySchemaVersion, applied, null);
            }

            // Primary exists but is corrupt/unreadable.
            if (backupExists && TryReadFullDocument(_paths.BackupFilePath, out var backupDocument, out var backupSchemaVersion))
            {
                // Rung 3: primary corrupt, backup valid — quarantine primary, recover from backup.
                var quarantinePath = QuarantineFile(_paths.PrimaryFilePath);
                if (!TryMigrate(backupDocument, out var migrated, out var applied, out var migrationFailure))
                    return HandleMigrationFailure(migrationFailure!, backupSchemaVersion, _paths.BackupFilePath);

                if (applied > 0) WritePreMigrationBackupIfMissing(backupDocument, backupSchemaVersion);
                PersistAfterLoad(migrated, forcePersist: true);
                Diag("StateRecoveredFromBackup", null, new KeyValuePair<string, object?>("reason", "PrimaryCorrupt"));
                return new StateLoadResult(StateLoadStatus.RecoveredFromBackup, migrated, backupSchemaVersion, applied, quarantinePath);
            }

            // Rung 4: primary corrupt, backup unusable (corrupt) or absent — quarantine whatever
            // exists, return empty state, writing is still allowed. Deliberately does NOT fall back
            // to legacy import — see StateLoadStatus.Quarantined remarks.
            var quarantinedPrimary = QuarantineFile(_paths.PrimaryFilePath);
            if (backupExists) QuarantineFile(_paths.BackupFilePath);
            return new StateLoadResult(StateLoadStatus.Quarantined, EmptyPayload(), 0, 0, quarantinedPrimary);
        }

        if (backupExists)
        {
            if (TryReadFullDocument(_paths.BackupFilePath, out var backupDocument, out var backupSchemaVersion))
            {
                // Rung 2: primary missing, backup valid — recover from backup, rewrite primary.
                if (!TryMigrate(backupDocument, out var migrated, out var applied, out var migrationFailure))
                    return HandleMigrationFailure(migrationFailure!, backupSchemaVersion, _paths.BackupFilePath);

                if (applied > 0) WritePreMigrationBackupIfMissing(backupDocument, backupSchemaVersion);
                PersistAfterLoad(migrated, forcePersist: true);
                Diag("StateRecoveredFromBackup", null, new KeyValuePair<string, object?>("reason", "PrimaryMissing"));
                return new StateLoadResult(StateLoadStatus.RecoveredFromBackup, migrated, backupSchemaVersion, applied, null);
            }

            // Primary absent, backup present but corrupt: a .bak only ever exists because a primary
            // was successfully written at least once before, so — same as rung 4 — a JSON document
            // has "ever existed" here and legacy must not be resurrected (D-7 rung 4 rationale).
            var quarantinedBackup = QuarantineFile(_paths.BackupFilePath);
            return new StateLoadResult(StateLoadStatus.Quarantined, EmptyPayload(), 0, 0, quarantinedBackup);
        }

        // Neither primary nor backup exists.
        var legacyDocument = TryImportLegacy();
        if (legacyDocument is not null)
        {
            // Rung 5: import legacy user.config structural state as schema v1, migrate up.
            // ILegacyStateImporter.TryImport() already returns a full document (schemaVersion == 1
            // plus envelope metadata, see LegacySettingsImporter), so no separate fromVersion is
            // needed — but fall back to the synthetic legacy version defensively if an implementation
            // ever omits it, rather than trusting an implicit default of 0.
            if (!StateEnvelope.TryGetSchemaVersion(legacyDocument, out var legacySchemaVersion))
                legacySchemaVersion = StateEnvelope.LegacySchemaVersion;

            // No on-disk primary/backup file backs this document (we're in the "neither exists"
            // branch) — there is nothing under StateDirectory to quarantine if migration fails here.
            if (!TryMigrate(legacyDocument, out var migrated, out var applied, out var migrationFailure))
                return HandleMigrationFailure(migrationFailure!, legacySchemaVersion, primaryToQuarantine: null);

            PersistAfterLoad(migrated, forcePersist: true);
            Diag("StateLegacyImported", null, new KeyValuePair<string, object?>("migrationsApplied", applied));
            return new StateLoadResult(StateLoadStatus.ImportedFromLegacy, migrated, legacySchemaVersion, applied, null);
        }

        // Rung 6: nothing on disk, nothing worth importing from legacy either.
        return new StateLoadResult(StateLoadStatus.FirstRun, EmptyPayload(), 0, 0, null);
    }

    private JsonObject? TryImportLegacy()
    {
        try
        {
            return _legacyImporter.TryImport();
        }
        catch (Exception ex)
        {
            // ILegacyStateImporter.TryImport() promises never to throw; defend anyway rather than
            // letting a misbehaving implementation take the whole load down.
            AppLog.Current.Error("JsonApplicationStateStore", "LegacyImporterThrew", ex);
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="fullDocument"/> (envelope + payload, as read off disk or produced by the
    /// legacy importer) through <see cref="StateMigrationRunner.MigrateToCurrent"/> and strips the
    /// envelope back off the result, since every <see cref="StateLoadResult.Payload"/> this store
    /// hands back is payload-only (envelope keys stripped — see that record's remarks).
    /// </summary>
    private (JsonObject Payload, int Applied) MigrateIfNeeded(JsonObject fullDocument)
    {
        var (migratedFull, applied) = _migrationRunner.MigrateToCurrent(fullDocument, StateEnvelope.CurrentSchemaVersion);
        return (StateEnvelope.StripEnvelope(migratedFull), applied);
    }

    /// <summary>
    /// M-6: wraps <see cref="MigrateIfNeeded"/> so a bug inside a registered
    /// <see cref="ISettingsMigration.Migrate"/> implementation — or a genuine gap/downgrade the chain
    /// only discovers at this specific document's version, despite <see cref="StateMigrationRunner"/>'s
    /// own constructor-time chain validation — can never escape <see cref="Load"/> unhandled. Without
    /// this, such an exception would propagate straight out of <see cref="Load"/>, past every rung of
    /// the read/recovery ladder this type exists to provide, crash the process on startup, and — since
    /// a read that merely throws changes nothing on disk — do so identically on every subsequent
    /// launch, permanently bricking the app instead of degrading to an empty, writable state the way
    /// every other unreadable-document case on this ladder already does.
    /// </summary>
    private bool TryMigrate(JsonObject fullDocument, out JsonObject payload, out int applied, out Exception? failure)
    {
        try
        {
            (payload, applied) = MigrateIfNeeded(fullDocument);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            payload = EmptyPayload();
            applied = 0;
            failure = ex;
            return false;
        }
    }

    /// <summary>
    /// Produces the <see cref="StateLoadStatus.Quarantined"/> fallback <see cref="StateLoadResult"/>
    /// for a migration that threw (M-6): best-effort quarantines whichever on-disk file(s) actually
    /// held the document that failed to migrate (same <see cref="QuarantineFile"/> the "primary/backup
    /// corrupt" rungs already use — <paramref name="secondaryToQuarantine"/> lets rung 3 additionally
    /// sweep up the backup once the already-quarantined primary stops being a valid
    /// <see cref="StateLoadResult.QuarantinePath"/> to report), reports the failure via a dedicated
    /// diagnostic event carrying only the exception's type name (never <see cref="Exception.Message"/>
    /// or a file path — same privacy discipline as <c>StateSaveFailed</c>), and returns an empty,
    /// still-writable structural state so the process starts rather than crashing.
    /// </summary>
    private StateLoadResult HandleMigrationFailure(Exception failure, int fromVersion, string? primaryToQuarantine, string? secondaryToQuarantine = null)
    {
        Diag(
            "StateMigrationFailed",
            null,
            new KeyValuePair<string, object?>("fromVersion", fromVersion),
            new KeyValuePair<string, object?>("toVersion", StateEnvelope.CurrentSchemaVersion),
            new KeyValuePair<string, object?>("errorType", failure.GetType().Name));

        string? quarantinePath = null;
        if (primaryToQuarantine is not null && File.Exists(primaryToQuarantine))
            quarantinePath = QuarantineFile(primaryToQuarantine);

        if (secondaryToQuarantine is not null && File.Exists(secondaryToQuarantine))
            QuarantineFile(secondaryToQuarantine);

        return new StateLoadResult(StateLoadStatus.Quarantined, EmptyPayload(), fromVersion, 0, quarantinePath);
    }

    /// <summary>
    /// Rewrites the primary file with <paramref name="migratedPayload"/> as part of recovery/import
    /// (D-7 rungs 2/3/5, always) or after an in-place migration (rung 1, only when
    /// <paramref name="forcePersist"/> is <see langword="true"/> — the caller passes that whenever at
    /// least one migration actually ran; if nothing changed, the on-disk primary already matches and
    /// there is nothing to write). Gated on <see cref="IStorageLock.IsHeld"/>: if the advisory lock
    /// isn't held, no write happens anywhere in this store, including these internal recovery
    /// rewrites (ADR-024 D-8's degraded read-only session applies uniformly). On success (or when
    /// nothing needed writing), sets the no-op guard baseline to match what the caller now holds in
    /// memory; on a skipped/failed write, the baseline is left unset so the next real
    /// <see cref="Save"/> call is never mistaken for a no-op.
    /// </summary>
    private void PersistAfterLoad(JsonObject migratedPayload, bool forcePersist)
    {
        if (!forcePersist)
        {
            // Already at current schema and nothing was migrated — disk already matches in substance
            // (envelope metadata like writtenAtUtc/appVersion may differ, but that's not payload).
            _lastCommittedPayloadJson = migratedPayload.ToJsonString();
            return;
        }

        if (!_storageLock.IsHeld) return; // degraded read-only: leave the baseline unset.

        var envelope = StateEnvelope.Wrap(migratedPayload, StateEnvelope.CurrentSchemaVersion, DateTimeOffset.UtcNow, ResolveAppVersion());
        try
        {
            _writer.Write(envelope);
            _lastCommittedPayloadJson = migratedPayload.ToJsonString();
        }
        catch (AtomicWriteFailedException ex)
        {
            Diag(
                "StateSaveFailed",
                null,
                new KeyValuePair<string, object?>("stage", ex.Stage.ToString()),
                new KeyValuePair<string, object?>("errorType", (ex.InnerException ?? ex).GetType().Name));
        }
    }

    private string QuarantineFile(string sourcePath)
    {
        var quarantinePath = _paths.NewQuarantinePath();
        try
        {
            Directory.CreateDirectory(_paths.StateDirectory);
            File.Move(sourcePath, quarantinePath, overwrite: false);
            Diag("StateQuarantined", null, new KeyValuePair<string, object?>("bytes", SafeFileLength(quarantinePath)));
            return quarantinePath;
        }
        catch (Exception ex)
        {
            // Best effort: if the corrupt file can't even be moved (permissions, held handle), leave
            // it where it is. The caller still proceeds with an empty/recovered in-memory state; the
            // file simply keeps failing to parse on every subsequent load, which is a safe (if noisy)
            // degraded outcome rather than a crash. The source file name is deliberately not logged —
            // it's a fixed, known pattern (a StateStorePaths-generated name), but omitted anyway to
            // match AtomicJsonFileWriter's own posture of never logging file names from this contour.
            AppLog.Current.Error("JsonApplicationStateStore", "QuarantineFileFailed", ex);
            return sourcePath;
        }
    }

    /// <summary>
    /// Writes a one-time, permanent snapshot of <paramref name="preMigrationDocument"/> (envelope
    /// included, verbatim as read off disk) before this process persists the newly migrated version
    /// over it — MINOR fix (fix-cycle 1, docs/PLANS/workspace-grid-migration.md §10.5). Without this,
    /// the only place the pre-migration bytes survive is <c>.bak</c> (rung 1) or a quarantine file
    /// (rung 3) — and the very next ordinary <see cref="Save"/> call after startup (any user action)
    /// overwrites <c>.bak</c> with the just-migrated content, permanently losing the one document an
    /// older build (post-rollback) would actually be able to read. Deterministic, idempotent target
    /// path (<see cref="StateStorePaths.PreMigrationBackupPath"/>): the same <paramref name="fromVersion"/>
    /// always maps to the same file, and this method never overwrites an existing one — the first
    /// migration away from a given on-disk version is the only one worth preserving; a later
    /// resurrection of the same version (e.g. a restored `.bak`) does not get a fresh snapshot, but the
    /// original one, taken further back, is still the most complete artifact available.
    /// </summary>
    /// <remarks>
    /// Best-effort, like <see cref="QuarantineFile"/>/<see cref="QuarantineLoadedDocument"/>: a failure
    /// here (permissions, full disk) must not affect <see cref="Load"/>'s own outcome — the in-memory
    /// migrated document is still returned and still gets persisted normally.
    /// <para>
    /// <b>MAJOR N-2, fix-cycle 2 (docs/PLANS/workspace-grid-migration.md §10.8).</b> Two defects fixed
    /// here, both present since this method was introduced in fix-cycle 1:
    /// </para>
    /// <para>
    /// (1) <b>No <see cref="IStorageLock.IsHeld"/> guard.</b> Unlike <see cref="PersistAfterLoad"/> —
    /// which this method is always called immediately before, for the exact same migrated document —
    /// this method used to write unconditionally, regardless of whether the advisory storage lock
    /// (ADR-024 D-8) is held. A second, read-only process (lock lost to another instance) would still
    /// create this side-channel snapshot on disk, violating the same "a read-only session writes
    /// nothing, anywhere" invariant <see cref="PersistAfterLoad"/> already upholds for the primary/backup
    /// rewrite. Fixed with the identical <c>!_storageLock.IsHeld</c> early-return.
    /// </para>
    /// <para>
    /// (2) <b>Non-atomic <c>File.Exists</c> + <c>File.WriteAllText</c>.</b> A process that died between
    /// those two calls left a truncated file permanently under the final, deterministic target path —
    /// and because the very next <see cref="Load"/> call's <c>File.Exists</c> check would then see that
    /// truncated file and skip ever rewriting it, the corruption was unrecoverable for the lifetime of
    /// the installation (this deterministic path, unlike <see cref="StateStorePaths.NewQuarantinePath"/>, is never
    /// retried under a new name). Fixed: the document is written to a fresh, uniquely-named temp file
    /// first (<see cref="StateStorePaths.NewTempFilePath"/> — the SAME naming convention
    /// <see cref="AtomicJsonFileWriter"/>'s own temp files use, so an orphaned temp file left behind by a
    /// crash here is swept by the existing <see cref="AtomicJsonFileWriter.SweepOrphanedTempFiles"/> call
    /// at the top of every <see cref="LoadCore"/>, exactly like any other stray write-in-progress file),
    /// then renamed into place with <see cref="File.Move(string,string,bool)"/>, <c>overwrite: false</c>.
    /// A crash between the temp write and the rename leaves an orphaned temp file (swept next launch)
    /// and, critically, the final target path still simply does not exist — never a half-written file
    /// under the permanent name. If another writer (or an earlier attempt within the same process) wins
    /// the race and creates the target first, <see cref="File.Move(string,string,bool)"/> throws
    /// <see cref="IOException"/> ("already exists"), which is caught and treated as success (first write
    /// wins) rather than an error.
    /// </para>
    /// </remarks>
    private void WritePreMigrationBackupIfMissing(JsonObject preMigrationDocument, int fromVersion)
    {
        // fromVersion <= 0 only happens for the legacy-import path (no prior on-disk JSON document
        // exists to preserve this way — user.config itself stays frozen/untouched, ADR-024 D-10).
        if (fromVersion <= 0) return;

        // N-2(1): same degraded-read-only posture as PersistAfterLoad — a session that doesn't hold the
        // advisory storage lock must never write to the storage directory through ANY path, including
        // this best-effort side-channel snapshot.
        if (!_storageLock.IsHeld) return;

        var path = _paths.PreMigrationBackupPath(fromVersion);
        if (File.Exists(path)) return;

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(_paths.StateDirectory);
            tempPath = _paths.NewTempFilePath();
            File.WriteAllText(tempPath, preMigrationDocument.ToJsonString());

            // N-2(2): atomic rename into place — see remarks above. overwrite:false surfaces a race
            // against a concurrent/earlier writer as IOException rather than silently truncating it.
            File.Move(tempPath, path, overwrite: false);
            tempPath = null; // moved successfully — nothing left for the finally block to clean up.
            Diag("StatePreMigrationBackupWritten", null, new KeyValuePair<string, object?>("fromVersion", fromVersion));
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer already produced the file since the File.Exists check above — first write
            // wins; this is not a failure.
        }
        catch (Exception ex)
        {
            AppLog.Current.Error(
                "JsonApplicationStateStore",
                "PreMigrationBackupWriteFailed",
                ex,
                new KeyValuePair<string, object?>("fromVersion", fromVersion));
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // Best-effort cleanup only — an orphaned temp file here is swept by
                    // AtomicJsonFileWriter.SweepOrphanedTempFiles at the top of the next Load().
                }
            }
        }
    }

    private void Diag(string name, string? scope, params KeyValuePair<string, object?>[] payload)
    {
        try
        {
            _diagnostics().Write("state", name, scope, payload);
        }
        catch (Exception)
        {
            // Diagnostics must never affect the store's own outcome — IDiagnosticsLog.Write already
            // promises never to throw, but the lazily-resolved Func<IDiagnosticsLog> itself could.
        }
    }
}
