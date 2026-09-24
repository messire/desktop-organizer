using System.IO;
using System.Text.Json.Nodes;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Diagnostics;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Unit tests for <see cref="StateRestoreCommand"/> — the <c>--restore-state-backup</c> manual
/// recovery path (ADR-024 §7 rollback scenario C). Every scenario is exercised against a real
/// disposable temp directory, same convention as <c>JsonApplicationStateStoreTests</c>/
/// <c>AtomicJsonFileWriterTests</c>.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class StateRestoreCommandTests
{
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

    private static (string Root, StateStorePaths Paths) BuildPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgRestore_{Guid.NewGuid():N}");
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

    private static StateRestoreCommand BuildCommand(
        StateStorePaths paths,
        FakeDiagnosticsLog? diagnostics = null,
        Func<StateWriteStage, bool>? testForceWriteFailureAt = null) =>
        new(
            paths,
            new StateMigrationRunner(Array.Empty<ISettingsMigration>()),
            () => diagnostics is not null ? diagnostics : NullDiagnosticsLog.Instance,
            testForceWriteFailureAt);

    private static JsonObject FullDocument(int schemaVersion, string marker) =>
        StateEnvelope.Wrap(new JsonObject { ["marker"] = marker }, schemaVersion, DateTimeOffset.UtcNow, "test");

    private static void SeedFile(StateStorePaths paths, string path, JsonObject fullDocument)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        File.WriteAllText(path, fullDocument.ToJsonString());
    }

    // ── No candidate ─────────────────────────────────────────────────────────

    [Fact]
    public void Restore_NoBakAndNoQuarantine_ReturnsNoCandidateFound_TouchesNothing()
    {
        var (root, paths) = BuildPaths();
        try
        {
            var command = BuildCommand(paths);

            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.NoCandidateFound, result.Outcome);
            Assert.Null(result.CandidatePath);
            Assert.Null(result.QuarantinedPrimaryPath);
            Assert.False(File.Exists(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Valid .bak ───────────────────────────────────────────────────────────

    [Fact]
    public void Restore_ValidBak_NoExistingPrimary_RestoresAsPrimary_NoQuarantine()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));
            var command = BuildCommand(paths);

            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.Restored, result.Outcome);
            Assert.Equal(paths.BackupFilePath, result.CandidatePath);
            Assert.Null(result.QuarantinedPrimaryPath);

            Assert.True(File.Exists(paths.PrimaryFilePath));
            var written = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.NotNull(written);
            Assert.Equal("from-bak", written!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_ValidBak_ExistingBadPrimary_QuarantinesOldPrimary_NeverDeletesIt()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.PrimaryFilePath, "{ not valid json");
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));

            var command = BuildCommand(paths);
            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.Restored, result.Outcome);
            Assert.NotNull(result.QuarantinedPrimaryPath);
            Assert.True(File.Exists(result.QuarantinedPrimaryPath));
            Assert.Contains("not valid json", File.ReadAllText(result.QuarantinedPrimaryPath!));

            // The new primary is the restored .bak content, not the corrupt bytes.
            var written = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal("from-bak", written!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Falls back to newest quarantine file when there is no .bak ─────────────

    [Fact]
    public void Restore_NoBak_PicksNewestQuarantineFile()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            var older = Path.Combine(paths.StateDirectory, "application-state.corrupt-20260101-000000.json");
            var newer = Path.Combine(paths.StateDirectory, "application-state.corrupt-20260101-000001.json");

            File.WriteAllText(older, FullDocument(StateEnvelope.CurrentSchemaVersion, "older").ToJsonString());
            File.WriteAllText(newer, FullDocument(StateEnvelope.CurrentSchemaVersion, "newer").ToJsonString());
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            var command = BuildCommand(paths);
            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.Restored, result.Outcome);
            Assert.Equal(newer, result.CandidatePath);

            var written = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal("newer", written!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_BakPresent_PrefersBakOverQuarantineFiles()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            var quarantine = Path.Combine(paths.StateDirectory, "application-state.corrupt-20260101-000000.json");
            File.WriteAllText(quarantine, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-quarantine").ToJsonString());
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));

            var command = BuildCommand(paths);
            var result = command.Restore();

            Assert.Equal(paths.BackupFilePath, result.CandidatePath);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Invalid candidate: nothing changes ──────────────────────────────────────

    [Fact]
    public void Restore_BakIsNotValidJson_ReturnsCandidateInvalid_LeavesExistingPrimaryUntouched()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "good-primary"));
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.BackupFilePath, "{ this is not json");

            var command = BuildCommand(paths);
            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.CandidateInvalid, result.Outcome);
            Assert.Equal(paths.BackupFilePath, result.CandidatePath);
            Assert.Null(result.QuarantinedPrimaryPath);

            // The existing (good, in this test) primary is completely untouched.
            var stillThere = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal("good-primary", stillThere!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_BakMissingSchemaVersion_ReturnsCandidateInvalid()
    {
        var (root, paths) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(paths.BackupFilePath, new JsonObject { ["marker"] = "no-version" }.ToJsonString());

            var command = BuildCommand(paths);
            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.CandidateInvalid, result.Outcome);
            Assert.False(File.Exists(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_BakNewerThanCurrentSchema_ReturnsCandidateInvalid_NeverGuessesADowngrade()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion + 1, "from-future"));

            var command = BuildCommand(paths);
            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.CandidateInvalid, result.Outcome);
            Assert.False(File.Exists(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    [Fact]
    public void Restore_Success_WritesStateRecoveredFromBackupDiagnosticEvent()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));
            var diagnostics = new FakeDiagnosticsLog();
            var command = BuildCommand(paths, diagnostics);

            command.Restore();

            Assert.Contains(diagnostics.Events, e => e.Category == "state" && e.Name == "StateRecoveredFromBackup");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── M-4: write-before-quarantine ordering ───────────────────────────────

    /// <summary>
    /// The whole point of M-4: if the write of the new primary fails, the existing primary must never
    /// have been destructively moved out of the way first. Before the fix, <c>QuarantineExistingPrimary</c>
    /// ran (a plain <see cref="File.Move(string, string, bool)"/>) before <c>_writer.Write</c>, so a
    /// write failure here left the user with <em>no</em> primary file at all.
    /// </summary>
    [Fact]
    public void Restore_WriteFails_LeavesExistingPrimaryCompletelyUnchanged_NoDataLoss()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "good-primary"));
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));

            var command = BuildCommand(paths, testForceWriteFailureAt: stage => stage == StateWriteStage.BeforeReplace);

            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.Failed, result.Outcome);
            Assert.Null(result.QuarantinedPrimaryPath);

            Assert.True(File.Exists(paths.PrimaryFilePath), "A failed restore write must never leave the primary file missing.");
            var stillThere = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal("good-primary", stillThere!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_WriteFails_NoPreexistingPrimary_LeavesPrimaryAbsent_NeverCreatesPartialFile()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));

            var command = BuildCommand(paths, testForceWriteFailureAt: stage => stage == StateWriteStage.AfterValidate);

            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.Failed, result.Outcome);
            Assert.False(File.Exists(paths.PrimaryFilePath));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_WriteFails_WritesStateRestoreFailedDiagnosticEvent_NeverStateRecoveredFromBackup()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));
            var diagnostics = new FakeDiagnosticsLog();

            var command = BuildCommand(paths, diagnostics, testForceWriteFailureAt: stage => stage == StateWriteStage.BeforeReplace);

            command.Restore();

            // MINOR-4: a failed restore must never be reported under the "something was recovered"
            // event name, and "RestoreCommandFailed" is not part of StateRecoveredFromBackup's
            // documented closed reason set.
            Assert.Contains(diagnostics.Events, e => e.Category == "state" && e.Name == "StateRestoreFailed");
            Assert.DoesNotContain(diagnostics.Events, e => e.Name == "StateRecoveredFromBackup");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void Restore_Success_ExistingPrimary_NewBakDescribesOnlyOneGenerationBack()
    {
        var (root, paths) = BuildPaths();
        try
        {
            SeedFile(paths, paths.PrimaryFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "old-primary"));
            SeedFile(paths, paths.BackupFilePath, FullDocument(StateEnvelope.CurrentSchemaVersion, "from-bak"));

            var command = BuildCommand(paths);
            var result = command.Restore();

            Assert.Equal(StateRestoreOutcome.Restored, result.Outcome);

            // The new primary is the restored content...
            var primary = JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)) as JsonObject;
            Assert.Equal("from-bak", primary!["marker"]!.GetValue<string>());

            // ...and .bak now describes exactly one generation behind the new primary (the old
            // primary, rotated there by AtomicJsonFileWriter's own File.Replace semantics), not frozen
            // at whatever it held before the restore.
            var bak = JsonNode.Parse(File.ReadAllText(paths.BackupFilePath)) as JsonObject;
            Assert.Equal("old-primary", bak!["marker"]!.GetValue<string>());

            // The pre-restore primary is also preserved separately, for history, as a quarantine file.
            Assert.NotNull(result.QuarantinedPrimaryPath);
            var quarantined = JsonNode.Parse(File.ReadAllText(result.QuarantinedPrimaryPath!)) as JsonObject;
            Assert.Equal("old-primary", quarantined!["marker"]!.GetValue<string>());
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }
}
