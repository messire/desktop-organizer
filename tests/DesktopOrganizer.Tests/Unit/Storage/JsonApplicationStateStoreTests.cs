using System.IO;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Unit tests for <see cref="JsonApplicationStateStore"/>: all seven rungs of the read/recovery
/// ladder (ADR-024 D-7), the executable write barrier and no-op guard (D-11), and "a failed write
/// never moves committed state" (D-6 step 5). Every dependency is faked
/// (<see cref="FakeStorageLock"/>/<see cref="FakeLegacyImporter"/>/<see cref="FakeDiagnosticsLog"/>,
/// plus <see cref="StateMigrationRunner"/> built from trivial <see cref="FakeMigration"/> steps where
/// a migration is actually needed) so these tests are independent of the real production migration
/// chain and legacy importer, which have their own dedicated coverage.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class JsonApplicationStateStoreTests
{
    private sealed class FakeStorageLock : IStorageLock
    {
        public bool IsHeld { get; set; } = true;

        public bool TryAcquire() => IsHeld;

        public void Dispose()
        {
        }
    }

    private sealed class FakeLegacyImporter : ILegacyStateImporter
    {
        public JsonObject? Result { get; set; }

        public JsonObject? TryImport() => Result;
    }

    private sealed class FakeDiagnosticsLog : IDiagnosticsLog
    {
        public List<(string Category, string Name, string? Scope, KeyValuePair<string, object?>[] Payload)> Events { get; } = new();

        public bool IsEnabled => true;

        public void Write(string category, string name, string? scope, params KeyValuePair<string, object?>[] payload) =>
            Events.Add((category, name, scope, payload));

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeMigration : ISettingsMigration
    {
        public FakeMigration(int fromVersion, int toVersion)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
        }

        public int FromVersion { get; }

        public int ToVersion { get; }

        public JsonObject Migrate(JsonObject document)
        {
            var result = document.DeepClone() as JsonObject ?? new JsonObject();
            result["migratedTo"] = ToVersion;
            return result;
        }
    }

    /// <summary>Simulates a bug inside a registered <see cref="ISettingsMigration"/> (M-6).</summary>
    private sealed class ThrowingMigration : ISettingsMigration
    {
        public ThrowingMigration(int fromVersion, int toVersion)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
        }

        public int FromVersion { get; }

        public int ToVersion { get; }

        public JsonObject Migrate(JsonObject document) =>
            throw new InvalidOperationException("Simulated migration bug (test-only).");
    }

    private static (string Root, StateStorePaths Paths) BuildPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgState_{Guid.NewGuid():N}");
        return (root, new StateStorePaths(root));
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonApplicationStateStore BuildStore(
        StateStorePaths paths,
        FakeStorageLock? storageLock = null,
        FakeLegacyImporter? legacyImporter = null,
        StateMigrationRunner? migrationRunner = null,
        FakeDiagnosticsLog? diagnostics = null,
        Func<StateWriteStage, bool>? testForceWriteFailureAt = null) =>
        new(
            paths,
            storageLock ?? new FakeStorageLock(),
            legacyImporter ?? new FakeLegacyImporter(),
            migrationRunner ?? new StateMigrationRunner(Array.Empty<ISettingsMigration>()),
            () => diagnostics is not null ? diagnostics : NullDiagnosticsLog.Instance,
            testForceWriteFailureAt);

    private static void SeedFile(StateStorePaths paths, string path, JsonObject fullDocument)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        File.WriteAllText(path, fullDocument.ToJsonString());
    }

    private static JsonObject FullDocument(int schemaVersion, string marker) =>
        StateEnvelope.Wrap(new JsonObject { ["marker"] = marker }, schemaVersion, DateTimeOffset.UtcNow, "test");

    // ── Rung 1: Loaded ───────────────────────────────────────────────────────
    [Fact]
    public void Load_PrimaryAtCurrentSchema_ReturnsLoaded_NoMigrationsApplied()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "v3"));
            var store = BuildStore(paths);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.Loaded, result.Status);
            Assert.Equal(StateEnvelope.CurrentSchemaVersion, result.SchemaVersionOnDisk);
            Assert.Equal(0, result.MigrationsApplied);
            Assert.Equal("v3", result.Payload["marker"]!.GetValue<string>());
            Assert.Null(result.QuarantinePath);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryAtOlderSchema_MigratesAndRewritesPrimary_SecondLoadAppliesZero()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "old"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });

            var firstStore = BuildStore(paths, migrationRunner: runner);
            var firstResult = firstStore.Load();

            Assert.Equal(StateLoadStatus.Loaded, firstResult.Status);
            Assert.Equal(2, firstResult.SchemaVersionOnDisk);
            Assert.Equal(3, firstResult.MigrationsApplied);

            // S-4 (manual matrix analogue): the migrated document must have been written back to
            // primary, so a second, independent Load() sees it already at the current version.
            var onDisk = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal(StateEnvelope.CurrentSchemaVersion, onDisk!["schemaVersion"]!.GetValue<int>());

            var secondStore = BuildStore(paths, migrationRunner: new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) }));
            var secondResult = secondStore.Load();

            Assert.Equal(StateLoadStatus.Loaded, secondResult.Status);
            Assert.Equal(StateEnvelope.CurrentSchemaVersion, secondResult.SchemaVersionOnDisk);
            Assert.Equal(0, secondResult.MigrationsApplied);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Pre-migration backup (MINOR fix, fix-cycle 1, docs/PLANS/workspace-grid-migration.md §10.5):
    // a permanent, one-time snapshot of the document as it existed before an in-place migration, so a
    // rollback to an older build has something recoverable even after `.bak` gets rotated by the next
    // ordinary Save(). ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Load_PrimaryAtOlderSchema_WritesPermanentPreMigrationSnapshot()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "old"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });
            var store = BuildStore(paths, migrationRunner: runner);

            store.Load();

            var snapshotPath = paths.PreMigrationBackupPath(2);
            Assert.True(File.Exists(snapshotPath));
            var snapshot = JsonNode.Parse(File.ReadAllText(snapshotPath)) as JsonObject;
            Assert.Equal("old", snapshot!["marker"]!.GetValue<string>());
            Assert.Equal(2, snapshot["schemaVersion"]!.GetValue<int>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PreMigrationSnapshotSurvives_AfterASubsequentOrdinarySaveRotatesBak()
    {
        // The exact scenario the fix closes: before it, the only place the pre-migration document
        // survived was `.bak`, and any Save() after the migrated document became primary would rotate
        // `.bak` to the just-migrated content, permanently losing the original.
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "old"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });
            var store = BuildStore(paths, migrationRunner: runner);
            store.Load();

            store.Save(new JsonObject { ["marker"] = "new-after-migration" });

            var snapshotPath = paths.PreMigrationBackupPath(2);
            Assert.True(File.Exists(snapshotPath));
            var snapshot = JsonNode.Parse(File.ReadAllText(snapshotPath)) as JsonObject;
            Assert.Equal("old", snapshot!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryAtOlderSchema_SecondUpgradeFromSameVersion_DoesNotOverwriteExistingSnapshot()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "first-upgrade"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });
            BuildStore(paths, migrationRunner: runner).Load();

            // Simulate a second, independent v2 document later reaching the same on-disk slot (e.g. an
            // operator manually restoring an old v2 backup) and being migrated again.
            File.WriteAllText(paths.PreMigrationBackupPath(2), "should not be touched");

            var snapshotPath = paths.PreMigrationBackupPath(2);
            Assert.Equal("should not be touched", File.ReadAllText(snapshotPath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryAlreadyAtCurrentSchema_NoMigrationRan_DoesNotWritePreMigrationSnapshot()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "already-current"));
            var store = BuildStore(paths);

            store.Load();

            Assert.False(File.Exists(paths.PreMigrationBackupPath(StateEnvelope.CurrentSchemaVersion)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryMissing_BackupAtOlderSchema_WritesPermanentPreMigrationSnapshot()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(2, "old-backup"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });
            var store = BuildStore(paths, migrationRunner: runner);

            store.Load();

            var snapshotPath = paths.PreMigrationBackupPath(2);
            Assert.True(File.Exists(snapshotPath));
            var snapshot = JsonNode.Parse(File.ReadAllText(snapshotPath)) as JsonObject;
            Assert.Equal("old-backup", snapshot!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Pre-migration backup, fix-cycle 2 (MAJOR N-2, docs/PLANS/workspace-grid-migration.md §10.8):
    // (1) must respect the advisory storage lock, same as PersistAfterLoad; (2) must never leave a
    // truncated file under the permanent target name if the write is interrupted. ──────────────────
    [Fact]
    public void Load_PrimaryAtOlderSchema_StorageLockNotHeld_DoesNotWritePreMigrationSnapshot()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "old"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });
            var store = BuildStore(paths, storageLock: new FakeStorageLock { IsHeld = false }, migrationRunner: runner);

            store.Load();

            Assert.False(File.Exists(paths.PreMigrationBackupPath(2)));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryAtOlderSchema_OrphanedTempFileFromPriorCrash_DoesNotLeaveATruncatedSnapshot()
    {
        // Simulates a process that crashed mid-way through a PREVIOUS WritePreMigrationBackupIfMissing
        // attempt: an orphaned, incomplete temp file left behind under the SAME naming convention
        // AtomicJsonFileWriter's own temp files use (this method now reuses
        // StateStorePaths.NewTempFilePath()) — SweepOrphanedTempFiles (already run at the top of every
        // Load()) must clear it before this attempt writes its own, and the permanent target path must
        // never end up holding truncated content.
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "old"));
            Directory.CreateDirectory(paths.StateDirectory);
            var orphanedTemp = paths.NewTempFilePath();
            File.WriteAllText(orphanedTemp, "{\"truncat"); // deliberately incomplete/invalid JSON

            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });
            var store = BuildStore(paths, migrationRunner: runner);

            store.Load();

            Assert.False(File.Exists(orphanedTemp));
            var snapshotPath = paths.PreMigrationBackupPath(2);
            Assert.True(File.Exists(snapshotPath));
            var snapshot = JsonNode.Parse(File.ReadAllText(snapshotPath)) as JsonObject; // throws if truncated
            Assert.Equal("old", snapshot!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Rung 2: RecoveredFromBackup (primary missing) ───────────────────────
    [Fact]
    public void Load_PrimaryMissing_BackupValid_RecoversAndRewritesPrimary()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-backup"));
            var store = BuildStore(paths);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.RecoveredFromBackup, result.Status);
            Assert.Equal("from-backup", result.Payload["marker"]!.GetValue<string>());
            Assert.Null(result.QuarantinePath);
            Assert.True(File.Exists(paths.PrimaryFilePath), "Rung 2 must immediately rewrite primary from the backup.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryMissing_BackupValid_LockNotHeld_RecoversInMemoryButDoesNotWrite()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-backup"));
            var lockObj = new FakeStorageLock { IsHeld = false };
            var store = BuildStore(paths, storageLock: lockObj);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.RecoveredFromBackup, result.Status);
            Assert.Equal("from-backup", result.Payload["marker"]!.GetValue<string>());
            Assert.False(File.Exists(paths.PrimaryFilePath), "Degraded read-only session: even the internal recovery rewrite must not touch disk.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryMissing_BackupValid_WritesStateRecoveredFromBackupDiagnosticEvent()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-backup"));
            var diagnostics = new FakeDiagnosticsLog();
            var store = BuildStore(paths, diagnostics: diagnostics);

            store.Load();

            var evt = Assert.Single(diagnostics.Events, e => e.Category == "state" && e.Name == "StateRecoveredFromBackup");
            Assert.Equal("PrimaryMissing", evt.Payload.Single(p => p.Key == "reason").Value);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Rung 3: RecoveredFromBackup (primary corrupt) ───────────────────────
    [Fact]
    public void Load_PrimaryCorrupt_BackupValid_QuarantinesPrimaryAndRecovers()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.PrimaryFilePath, "{ not valid json");
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-backup"));
            var store = BuildStore(paths);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.RecoveredFromBackup, result.Status);
            Assert.Equal("from-backup", result.Payload["marker"]!.GetValue<string>());
            Assert.NotNull(result.QuarantinePath);
            Assert.True(File.Exists(result.QuarantinePath));

            // Quarantining is a plain file move — the corrupt bytes must survive completely untouched
            // (not re-encoded, not partially read), since a human may need to inspect them later.
            Assert.Equal("{ not valid json", File.ReadAllText(result.QuarantinePath!));

            // Primary must now hold the recovered (current-schema) content, not the corrupt original.
            var primaryOnDisk = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal("from-backup", primaryOnDisk!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryCorrupt_BackupValid_WritesStateRecoveredFromBackupDiagnosticEvent()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.PrimaryFilePath, "{ not valid json");
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-backup"));
            var diagnostics = new FakeDiagnosticsLog();
            var store = BuildStore(paths, diagnostics: diagnostics);

            store.Load();

            var evt = Assert.Single(diagnostics.Events, e => e.Category == "state" && e.Name == "StateRecoveredFromBackup");
            Assert.Equal("PrimaryCorrupt", evt.Payload.Single(p => p.Key == "reason").Value);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Rung 4: Quarantined ──────────────────────────────────────────────────
    [Fact]
    public void Load_PrimaryCorrupt_NoBackup_QuarantinesAndReturnsEmptyWritable()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.PrimaryFilePath, "not json at all");
            var store = BuildStore(paths);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.Quarantined, result.Status);
            Assert.Empty(result.Payload);
            Assert.NotNull(result.QuarantinePath);
            Assert.True(File.Exists(result.QuarantinePath));
            Assert.False(File.Exists(paths.PrimaryFilePath));
            Assert.True(store.IsWritable, "Rung 4 explicitly still allows writing.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryCorrupt_BackupAlsoCorrupt_QuarantinesBoth()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.PrimaryFilePath, "not json");
            File.WriteAllText(paths.BackupFilePath, "also not json");
            var store = BuildStore(paths);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.Quarantined, result.Status);
            Assert.False(File.Exists(paths.PrimaryFilePath));
            Assert.False(File.Exists(paths.BackupFilePath));

            var quarantineFiles = Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern);
            Assert.Equal(2, quarantineFiles.Length);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryMissing_BackupCorrupt_QuarantinesBackup_DoesNotFallBackToLegacy()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.BackupFilePath, "not json");
            var legacy = new FakeLegacyImporter { Result = FullDocument(1, "should-never-be-used") };
            var store = BuildStore(paths, legacyImporter: legacy);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.Quarantined, result.Status);
            Assert.Empty(result.Payload);
            Assert.False(File.Exists(paths.BackupFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Rung 5: ImportedFromLegacy ───────────────────────────────────────────
    [Fact]
    public void Load_NothingOnDisk_LegacyHasData_ImportsMigratesAndWrites()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var legacy = new FakeLegacyImporter { Result = FullDocument(1, "legacy") };
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new FakeMigration(1, 2), new FakeMigration(2, 3), new FakeMigration(3, 4), new FakeMigration(4, 5) });
            var store = BuildStore(paths, legacyImporter: legacy, migrationRunner: runner);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.ImportedFromLegacy, result.Status);
            Assert.Equal(1, result.SchemaVersionOnDisk);
            Assert.Equal(4, result.MigrationsApplied);
            Assert.Equal("legacy", result.Payload["marker"]!.GetValue<string>());
            Assert.True(File.Exists(paths.PrimaryFilePath));

            var onDisk = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal(StateEnvelope.CurrentSchemaVersion, onDisk!["schemaVersion"]!.GetValue<int>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Rung 6: FirstRun ─────────────────────────────────────────────────────
    [Fact]
    public void Load_NothingOnDisk_LegacyEmpty_ReturnsFirstRun_WritesNothing()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var store = BuildStore(paths); // FakeLegacyImporter.Result defaults to null

            var result = store.Load();

            Assert.Equal(StateLoadStatus.FirstRun, result.Status);
            Assert.Empty(result.Payload);
            Assert.Equal(0, result.SchemaVersionOnDisk);
            Assert.Equal(0, result.MigrationsApplied);
            Assert.False(File.Exists(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Rung 7: NewerSchema ──────────────────────────────────────────────────
    [Fact]
    public void Load_PrimaryNewerSchema_ReturnsEmptyPayload_NeverWritable_NeverWritesForSession()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var originalDocument = FullDocument(StateEnvelope.CurrentSchemaVersion + 1, "future");
            SeedFile(paths, paths.PrimaryFilePath, originalDocument);
            var originalBytes = File.ReadAllText(paths.PrimaryFilePath);
            var store = BuildStore(paths);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.NewerSchema, result.Status);
            Assert.Empty(result.Payload);
            Assert.Equal(StateEnvelope.CurrentSchemaVersion + 1, result.SchemaVersionOnDisk);
            Assert.Equal(originalBytes, File.ReadAllText(paths.PrimaryFilePath));
            Assert.False(store.IsWritable);

            var saveOutcome = store.Save(new JsonObject { ["anything"] = "goes" });
            Assert.Equal(StateSaveResult.SkippedReadOnly, saveOutcome.Result);
            Assert.Equal(originalBytes, File.ReadAllText(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Write barrier (D-11) ─────────────────────────────────────────────────
    [Fact]
    public void Save_BeforeLoad_ReturnsSkippedBeforeLoad_NeverWrites()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var store = BuildStore(paths);

            var outcome = store.Save(new JsonObject { ["x"] = 1 });

            Assert.Equal(StateSaveResult.SkippedBeforeLoad, outcome.Result);
            Assert.False(File.Exists(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── No-op guard ──────────────────────────────────────────────────────────
    [Fact]
    public void Save_SamePayloadTwice_SecondCallIsSkippedNoChange()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var store = BuildStore(paths);
            store.Load();

            var payload = new JsonObject { ["panels"] = new JsonArray() };
            var first = store.Save(payload);
            var second = store.Save(new JsonObject { ["panels"] = new JsonArray() });

            Assert.Equal(StateSaveResult.Saved, first.Result);
            Assert.Equal(StateSaveResult.SkippedNoChange, second.Result);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Save_ReservedEnvelopeKeyInPayload_Throws()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var store = BuildStore(paths);
            store.Load();

            Assert.Throws<ArgumentException>(() => store.Save(new JsonObject { ["schemaVersion"] = 99 }));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Storage lock: read-only degradation ─────────────────────────────────
    [Fact]
    public void Save_LockNotHeld_ReturnsSkippedReadOnly_NeverWrites()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var lockObj = new FakeStorageLock { IsHeld = false };
            var store = BuildStore(paths, storageLock: lockObj);
            store.Load();

            var outcome = store.Save(new JsonObject { ["x"] = 1 });

            Assert.Equal(StateSaveResult.SkippedReadOnly, outcome.Result);
            Assert.False(File.Exists(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── A failed write never moves committed state (D-6 step 5) ────────────
    [Fact]
    public void Save_WriteFails_DoesNotMoveCommittedState_NextSaveWithSamePayloadStillWrites()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var failOnce = true;
            var store = BuildStore(
                paths,
                testForceWriteFailureAt: stage =>
                {
                    if (stage == StateWriteStage.AfterReplace && failOnce)
                    {
                        failOnce = false;
                        return true;
                    }

                    return false;
                });
            store.Load();

            var payload = new JsonObject { ["marker"] = "same" };
            var firstOutcome = store.Save(payload);

            Assert.Equal(StateSaveResult.Failed, firstOutcome.Result);
            Assert.Equal(StateWriteStage.AfterReplace, firstOutcome.FailureStage);

            // The underlying atomic replace actually completed on disk (AfterReplace fires after the
            // OS-level rename), but the store's own committed-state baseline must not have moved —
            // otherwise this second call with the exact same payload would be wrongly treated as a
            // no-op even though the store itself never recorded a successful commit.
            var secondOutcome = store.Save(new JsonObject { ["marker"] = "same" });

            Assert.Equal(StateSaveResult.Saved, secondOutcome.Result);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Save_WriteFailsAtEarlyStage_PrimaryUnchanged()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var store = BuildStore(paths, testForceWriteFailureAt: stage => stage == StateWriteStage.AfterValidate);
            store.Load(); // FirstRun — no primary exists yet.

            var outcome = store.Save(new JsonObject { ["marker"] = "never-lands" });

            Assert.Equal(StateSaveResult.Failed, outcome.Result);
            Assert.Equal(StateWriteStage.AfterValidate, outcome.FailureStage);
            Assert.False(File.Exists(paths.PrimaryFilePath), "A crash before the atomic replace must never create the primary file.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── M-6: a migration bug must never brick startup ────────────────────────

    [Fact]
    public void Load_PrimaryOlderSchema_MigrationThrows_QuarantinesPrimary_ReturnsQuarantinedInsteadOfCrashing()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "old"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new ThrowingMigration(2, 3) });
            var store = BuildStore(paths, migrationRunner: runner);

            var result = store.Load(); // must not throw

            Assert.Equal(StateLoadStatus.Quarantined, result.Status);
            Assert.Empty(result.Payload);
            Assert.NotNull(result.QuarantinePath);
            Assert.True(File.Exists(result.QuarantinePath));
            Assert.False(File.Exists(paths.PrimaryFilePath));
            Assert.True(store.IsWritable, "A migration bug must not disable writing for the rest of the session.");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryCorrupt_BackupValid_MigrationThrows_QuarantinesBothFiles_ReturnsQuarantined()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.PrimaryFilePath, "{ not valid json");
            SeedFile(paths, paths.BackupFilePath, FullDocument(2, "from-backup"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new ThrowingMigration(2, 3) });
            var store = BuildStore(paths, migrationRunner: runner);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.Quarantined, result.Status);
            Assert.Empty(result.Payload);
            Assert.False(File.Exists(paths.PrimaryFilePath));
            Assert.False(File.Exists(paths.BackupFilePath));

            // Both the originally-corrupt primary and the backup that failed to migrate must end up
            // quarantined — neither is left on disk to keep failing every subsequent load.
            var quarantineFiles = Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern);
            Assert.Equal(2, quarantineFiles.Length);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_PrimaryMissing_BackupValid_MigrationThrows_QuarantinesBackup_ReturnsQuarantined()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(2, "from-backup"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new ThrowingMigration(2, 3) });
            var store = BuildStore(paths, migrationRunner: runner);

            var result = store.Load();

            Assert.Equal(StateLoadStatus.Quarantined, result.Status);
            Assert.Empty(result.Payload);
            Assert.False(File.Exists(paths.BackupFilePath));
            Assert.NotNull(result.QuarantinePath);
            Assert.True(store.IsWritable);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_LegacyImport_MigrationThrows_ReturnsQuarantinedEmptyPayload_NoFileToQuarantine_StillWritable()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var legacy = new FakeLegacyImporter { Result = FullDocument(1, "legacy") };
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new ThrowingMigration(1, 2), new FakeMigration(2, 3) });
            var store = BuildStore(paths, legacyImporter: legacy, migrationRunner: runner);

            var result = store.Load(); // must not throw, despite no on-disk file to fall back on

            Assert.Equal(StateLoadStatus.Quarantined, result.Status);
            Assert.Empty(result.Payload);
            Assert.Null(result.QuarantinePath); // nothing under StateDirectory backs a legacy-imported document
            Assert.False(File.Exists(paths.PrimaryFilePath));
            Assert.True(store.IsWritable);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Load_MigrationThrows_WritesStateMigrationFailedDiagnosticEvent_ErrorTypeOnlyNeverMessage()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(2, "old"));
            var runner = new StateMigrationRunner(new ISettingsMigration[] { new ThrowingMigration(2, 3) });
            var diagnostics = new FakeDiagnosticsLog();
            var store = BuildStore(paths, migrationRunner: runner, diagnostics: diagnostics);

            store.Load();

            var evt = Assert.Single(diagnostics.Events, e => e.Category == "state" && e.Name == "StateMigrationFailed");
            Assert.Equal(2, evt.Payload.Single(p => p.Key == "fromVersion").Value);
            Assert.Equal(StateEnvelope.CurrentSchemaVersion, evt.Payload.Single(p => p.Key == "toVersion").Value);

            var errorType = evt.Payload.Single(p => p.Key == "errorType").Value as string;
            Assert.Equal(nameof(InvalidOperationException), errorType);
            Assert.All(evt.Payload, kv => Assert.DoesNotContain("Simulated migration bug", kv.Value?.ToString() ?? string.Empty));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Diagnostics privacy (R-12): only ex.GetType().Name, never a path or ex.Message ──────────
    [Fact]
    public void Save_WriteFails_DiagnosticsCarryOnlyExceptionTypeName_NeverMessageOrPath()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var diagnostics = new FakeDiagnosticsLog();
            var store = BuildStore(paths, diagnostics: diagnostics, testForceWriteFailureAt: stage => stage == StateWriteStage.AfterValidate);
            store.Load();

            store.Save(new JsonObject { ["marker"] = "x" });

            var failedEvent = Assert.Single(diagnostics.Events, e => e.Name == "StateSaveFailed");
            var errorType = failedEvent.Payload.Single(kv => kv.Key == "errorType").Value as string;
            Assert.NotNull(errorType);
            Assert.DoesNotContain(paths.PrimaryFilePath, errorType, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), errorType);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── QuarantineLoadedDocument (ADR-024 finalization C-1) ─────────────────────────

    [Fact]
    public void QuarantineLoadedDocument_PrimaryExists_CopiesFullPrimaryToQuarantine_LeavesPrimaryInPlace()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "twenty-panels"));
            var store = BuildStore(paths);
            store.Load();
            var originalPrimaryContent = File.ReadAllText(paths.PrimaryFilePath);

            store.QuarantineLoadedDocument("PartialPanelLoad");

            var quarantineFiles = Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern);
            var quarantineFile = Assert.Single(quarantineFiles);
            Assert.Equal(originalPrimaryContent, File.ReadAllText(quarantineFile));

            // Copy, not move: the primary must remain exactly as it was and stay the committed state
            // until Save() actually runs.
            Assert.True(File.Exists(paths.PrimaryFilePath));
            Assert.Equal(originalPrimaryContent, File.ReadAllText(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void QuarantineLoadedDocument_ThenSave_QuarantineStillHoldsTheFullOriginalPayload_AfterPrimaryIsOverwritten()
    {
        // The core C-1 scenario: a partial load (e.g. 19 of 20 panels constructed) reports itself,
        // then the very next Save() persists the reduced state. The quarantine file must already exist,
        // holding the full original document, before that overwrite lands.
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "twenty-panels"));
            var store = BuildStore(paths);
            store.Load();

            store.QuarantineLoadedDocument("PartialPanelLoad");
            var outcome = store.Save(new JsonObject { ["marker"] = "nineteen-panels" });

            Assert.Equal(StateSaveResult.Saved, outcome.Result);
            Assert.Equal("nineteen-panels", JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath))!["marker"]!.GetValue<string>());

            var quarantineFiles = Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern);
            var quarantineFile = Assert.Single(quarantineFiles);
            Assert.Equal("twenty-panels", JsonNode.Parse(File.ReadAllText(quarantineFile))!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void QuarantineLoadedDocument_CalledTwice_OnlyQuarantinesOnce()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "v"));
            var store = BuildStore(paths);
            store.Load();

            store.QuarantineLoadedDocument("PartialPanelLoad");
            store.QuarantineLoadedDocument("PartialPanelLoad");
            store.QuarantineLoadedDocument("SomeOtherReason");

            var quarantineFiles = Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern);
            Assert.Single(quarantineFiles);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void QuarantineLoadedDocument_CalledAgainAfterANewLoad_QuarantinesAgain()
    {
        // The idempotency guard is per-Load(), not process-lifetime: a second, later Load() call (a
        // new session in the same test process) must reset it so a genuinely new partial-load report
        // can still preserve that session's own pre-loss primary.
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "v"));
            var store = BuildStore(paths);
            store.Load();
            store.QuarantineLoadedDocument("PartialPanelLoad");

            store.Load();
            store.QuarantineLoadedDocument("PartialPanelLoad");

            var quarantineFiles = Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern);
            Assert.Equal(2, quarantineFiles.Length);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void QuarantineLoadedDocument_BeforeLoad_DoesNothing_NeverThrows()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "v"));
            var store = BuildStore(paths);

            var exception = Record.Exception(() => store.QuarantineLoadedDocument("PartialPanelLoad"));

            Assert.Null(exception);
            var quarantineFiles = Directory.Exists(paths.StateDirectory)
                ? Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern)
                : [];
            Assert.Empty(quarantineFiles);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void QuarantineLoadedDocument_NoPrimaryFileExists_DoesNothing_NeverThrows()
    {
        // FirstRun case: nothing committed to disk yet, so there is nothing to preserve.
        var (root, paths) = BuildPaths();
        try
        {
            var store = BuildStore(paths);
            store.Load();

            var exception = Record.Exception(() => store.QuarantineLoadedDocument("PartialPanelLoad"));

            Assert.Null(exception);
            var quarantineFiles = Directory.Exists(paths.StateDirectory)
                ? Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern)
                : [];
            Assert.Empty(quarantineFiles);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void QuarantineLoadedDocument_WritesStateQuarantinedDiagnosticEventWithReason()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "v"));
            var diagnostics = new FakeDiagnosticsLog();
            var store = BuildStore(paths, diagnostics: diagnostics);
            store.Load();

            store.QuarantineLoadedDocument("PartialPanelLoad");

            var evt = Assert.Single(diagnostics.Events, e => e.Category == "state" && e.Name == "StateQuarantined");
            Assert.Equal("PartialPanelLoad", evt.Payload.Single(p => p.Key == "reason").Value);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }
}
