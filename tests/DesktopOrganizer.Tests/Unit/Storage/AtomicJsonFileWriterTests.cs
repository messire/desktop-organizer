using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DesktopOrganizer.Services.Abstractions.Storage;
using DesktopOrganizer.Services.Storage;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Storage;

/// <summary>
/// Unit tests for <see cref="AtomicJsonFileWriter"/>: the D-6 write protocol (temp -&gt; fsync -&gt;
/// validate -&gt; atomic replace), crash injection at every stage, retry on a transient
/// <see cref="IOException"/>, orphaned-temp-file sweeping, and quarantine retention (ADR-024 D-6).
/// Uses a real temp directory per test, cleaned up in a <c>finally</c> — same convention as
/// <c>PanelItemStorageServiceTests</c>/<c>JsonLinesDiagnosticsLogTests</c>: no mocked file system,
/// since <see cref="File.Replace(string, string, string)"/>, <see cref="FileShare.None"/>, and fsync
/// are exactly the OS behavior under test.
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class AtomicJsonFileWriterTests
{
    // Fixed, not DateTimeOffset.UtcNow: Envelope(marker) is called once to build the payload actually
    // written and again later to build the "expected" value for JsonAssert.Equal — a real timestamp
    // would make those two calls structurally unequal by construction, regardless of what Write() did.
    private static readonly DateTimeOffset FixedWrittenAtUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Happy path ───────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the very first <see cref="AtomicJsonFileWriter.Write"/> call creates the primary
    /// file with the expected content and creates no backup file or leftover temp files.
    /// </summary>
    [Fact]
    public void Write_FirstWrite_CreatesPrimaryAndNoBackup()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);

            writer.Write(Envelope("v1"));

            Assert.True(File.Exists(paths.PrimaryFilePath));
            Assert.False(File.Exists(paths.BackupFilePath), "File.Move (first-ever write) must not create a .bak.");
            JsonAssert.Equal(Envelope("v1"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that a second <see cref="AtomicJsonFileWriter.Write"/> call writes the new content to the
    /// primary file and moves the previous content into the backup file.
    /// </summary>
    [Fact]
    public void Write_SecondWrite_CreatesBackupWithPreviousContent()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);

            writer.Write(Envelope("v1"));
            writer.Write(Envelope("v2"));

            JsonAssert.Equal(Envelope("v2"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.True(File.Exists(paths.BackupFilePath));
            JsonAssert.Equal(Envelope("v1"), JsonNode.Parse(File.ReadAllText(paths.BackupFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Crash injection: primary never existed before this write ───────────────

    /// <summary>
    /// Verifies that a simulated crash at any stage before the atomic replace, on a store with no
    /// pre-existing primary file, throws <see cref="AtomicWriteFailedException"/> and leaves the primary
    /// file absent with no leftover temp files.
    /// </summary>
    /// <param name="stage">The write stage at which the crash is injected.</param>
    [Theory]
    [InlineData(StateWriteStage.AfterTempWrite)]
    [InlineData(StateWriteStage.AfterFlush)]
    [InlineData(StateWriteStage.AfterValidate)]
    [InlineData(StateWriteStage.BeforeReplace)]
    public void Write_CrashBeforeReplace_NoPreexistingPrimary_LeavesPrimaryAbsent(StateWriteStage stage)
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths, s => s == stage);

            var ex = Assert.Throws<AtomicWriteFailedException>(() => writer.Write(Envelope("v1")));

            Assert.Equal(stage, ex.Stage);
            Assert.False(File.Exists(paths.PrimaryFilePath), "A crash before the atomic replace must never create the primary file.");
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that a simulated crash right after the atomic replace, on a store with no pre-existing
    /// primary file, still throws <see cref="AtomicWriteFailedException"/> but leaves a valid primary
    /// file in place, since the OS-level replace had already completed and cannot be rolled back.
    /// </summary>
    [Fact]
    public void Write_CrashAfterReplace_NoPreexistingPrimary_PrimaryStillLandsAndIsValid()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths, s => s == StateWriteStage.AfterReplace);

            var ex = Assert.Throws<AtomicWriteFailedException>(() => writer.Write(Envelope("v1")));

            Assert.Equal(StateWriteStage.AfterReplace, ex.Stage);

            // The OS-level move/replace already completed by this point — a real process death here
            // could never roll that back either, so the primary file must still be present and valid.
            Assert.True(File.Exists(paths.PrimaryFilePath));
            JsonAssert.Equal(Envelope("v1"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Crash injection: primary already existed (recovery/backup semantics) ───

    /// <summary>
    /// Verifies that a simulated crash at any stage before the atomic replace, on a store that already
    /// has a primary file, throws <see cref="AtomicWriteFailedException"/> and leaves the old primary
    /// file untouched with no backup file or leftover temp files created.
    /// </summary>
    /// <param name="stage">The write stage at which the crash is injected.</param>
    [Theory]
    [InlineData(StateWriteStage.AfterTempWrite)]
    [InlineData(StateWriteStage.AfterFlush)]
    [InlineData(StateWriteStage.AfterValidate)]
    [InlineData(StateWriteStage.BeforeReplace)]
    public void Write_CrashBeforeReplace_PreexistingPrimary_LeavesOldPrimaryUntouched(StateWriteStage stage)
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);
            writer.Write(Envelope("v1"));

            var crashingWriter = new AtomicJsonFileWriter(paths, s => s == stage);
            var ex = Assert.Throws<AtomicWriteFailedException>(() => crashingWriter.Write(Envelope("v2")));

            Assert.Equal(stage, ex.Stage);
            JsonAssert.Equal(Envelope("v1"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.False(File.Exists(paths.BackupFilePath), "A crash before the atomic replace must never create a .bak either.");
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that a simulated crash right after the atomic replace, on a store that already has a
    /// primary file, still throws <see cref="AtomicWriteFailedException"/> but leaves the new content in
    /// the primary file with the old content moved into the backup file.
    /// </summary>
    [Fact]
    public void Write_CrashAfterReplace_PreexistingPrimary_NewContentLandsAndOldBecomesBackup()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);
            writer.Write(Envelope("v1"));

            var crashingWriter = new AtomicJsonFileWriter(paths, s => s == StateWriteStage.AfterReplace);
            var ex = Assert.Throws<AtomicWriteFailedException>(() => crashingWriter.Write(Envelope("v2")));

            Assert.Equal(StateWriteStage.AfterReplace, ex.Stage);
            JsonAssert.Equal(Envelope("v2"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.True(File.Exists(paths.BackupFilePath));
            JsonAssert.Equal(Envelope("v1"), JsonNode.Parse(File.ReadAllText(paths.BackupFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Retry on a transient IOException ────────────────────────────────────

    /// <summary>
    /// Simulates an AV/indexer sharing violation right as <see cref="AtomicJsonFileWriter.Write"/>
    /// attempts <see cref="File.Replace(string, string, string)"/>, and releases it deterministically
    /// via <c>testOnRetryScheduled</c> — the same thread that calls <see cref="AtomicJsonFileWriter.Write"/>
    /// disposes the lock handle between the writer's own first and second attempts, with the retry
    /// delay itself forced to zero. No background <see cref="Task.Run(Action)"/>/<see cref="Thread.Sleep(int)"/>
    /// race against thread-pool scheduling latency (see
    /// docs/Refactoring/Testing/sta-test-conventions.md, "wall-clock тесты" — this test used to hold
    /// the lock on a background task for a fixed 120 ms window and race it against the writer's fixed
    /// 50/150 ms retry budget, which flaked under thread-pool contention from the suite's leaked
    /// timers).
    /// </summary>
    [Fact]
    public void Write_TransientIOException_RetriesAndSucceeds()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);
            writer.Write(Envelope("v1"));

            var lockHandle = new FileStream(paths.PrimaryFilePath, FileMode.Open, FileAccess.Read, FileShare.None);

            var retryWriter = new AtomicJsonFileWriter(
                paths,
                testForceFailureAt: null,
                testRetryDelay: _ => TimeSpan.Zero,
                testOnRetryScheduled: attempt =>
                {
                    if (attempt == 0) lockHandle.Dispose();
                });

            retryWriter.Write(Envelope("v2"));

            JsonAssert.Equal(Envelope("v2"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that a persistent (never released) file lock on the primary file exhausts the writer's
    /// retry budget and throws <see cref="AtomicWriteFailedException"/> with an <see cref="IOException"/>
    /// inner exception at the <see cref="StateWriteStage.BeforeReplace"/> stage, leaving the old primary
    /// file untouched with no orphaned temp files.
    /// </summary>
    [Fact]
    public void Write_PersistentIOException_FailsAfterRetriesExhausted_LeavesOldPrimaryAndNoOrphanTemp()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);
            writer.Write(Envelope("v1"));

            using var lockHandle = new FileStream(paths.PrimaryFilePath, FileMode.Open, FileAccess.Read, FileShare.None);

            var ex = Assert.Throws<AtomicWriteFailedException>(() => writer.Write(Envelope("v2")));

            Assert.Equal(StateWriteStage.BeforeReplace, ex.Stage);
            Assert.IsType<IOException>(ex.InnerException);

            lockHandle.Dispose();
            JsonAssert.Equal(Envelope("v1"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Retry on a transient UnauthorizedAccessException (M-3) ─────────────

    /// <summary>
    /// AV/Controlled-Folder-Access/ACL denial surfaces as <see cref="UnauthorizedAccessException"/> on
    /// Windows, not <see cref="IOException"/> — a read-only destination file reproduces exactly that
    /// CLR exception type via <see cref="File.Replace(string, string, string, bool)"/>. Mirrors
    /// <see cref="Write_TransientIOException_RetriesAndSucceeds"/>: the attribute is cleared
    /// deterministically via <c>testOnRetryScheduled</c>, on the same thread, between the writer's own
    /// first and second attempts — not from a background task racing a fixed 80 ms sleep against the
    /// writer's fixed retry budget (the wall-clock flake recorded in
    /// docs/Refactoring/Testing/sta-test-conventions.md, "wall-clock тесты").
    /// </summary>
    [Fact]
    public void Write_TransientUnauthorizedAccessException_RetriesAndSucceeds()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);
            writer.Write(Envelope("v1"));

            File.SetAttributes(paths.PrimaryFilePath, FileAttributes.ReadOnly);

            var retryWriter = new AtomicJsonFileWriter(
                paths,
                testForceFailureAt: null,
                testRetryDelay: _ => TimeSpan.Zero,
                testOnRetryScheduled: attempt =>
                {
                    if (attempt == 0) File.SetAttributes(paths.PrimaryFilePath, FileAttributes.Normal);
                });

            retryWriter.Write(Envelope("v2"));

            JsonAssert.Equal(Envelope("v2"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            TryClearReadOnly(paths.PrimaryFilePath);
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that a persistent (never cleared) read-only attribute on the primary file exhausts the
    /// writer's retry budget and throws <see cref="AtomicWriteFailedException"/> with an
    /// <see cref="UnauthorizedAccessException"/> inner exception at the <see cref="StateWriteStage.BeforeReplace"/>
    /// stage (not the misleading <c>AfterTempWrite</c> a prior implementation reported), leaving the old
    /// primary file untouched with no orphaned temp files.
    /// </summary>
    [Fact]
    public void Write_PersistentUnauthorizedAccessException_FailsAfterRetriesExhausted_LeavesOldPrimaryAndNoOrphanTemp()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);
            writer.Write(Envelope("v1"));

            File.SetAttributes(paths.PrimaryFilePath, FileAttributes.ReadOnly);

            var ex = Assert.Throws<AtomicWriteFailedException>(() => writer.Write(Envelope("v2")));

            // M-3: must report the stage the replace/move actually failed at (BeforeReplace), not the
            // misleading AfterTempWrite the generic last-resort catch in Write() used to report before
            // ReplaceOrMove itself caught UnauthorizedAccessException.
            Assert.Equal(StateWriteStage.BeforeReplace, ex.Stage);
            Assert.IsType<UnauthorizedAccessException>(ex.InnerException);

            TryClearReadOnly(paths.PrimaryFilePath);
            JsonAssert.Equal(Envelope("v1"), JsonNode.Parse(File.ReadAllText(paths.PrimaryFilePath)));
            Assert.Empty(TempFiles(paths));
        }
        finally
        {
            TryClearReadOnly(paths.PrimaryFilePath);
            SafeDeleteDirectory(root);
        }
    }

    // ── Orphaned temp file sweeping ──────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="AtomicJsonFileWriter.SweepOrphanedTempFiles"/> deletes leftover temp
    /// files matching the writer's temp-file pattern while leaving the primary file and unrelated files
    /// in the state directory alone.
    /// </summary>
    [Fact]
    public void SweepOrphanedTempFiles_DeletesOrphanedTempFiles_LeavesPrimaryAndUnrelatedFilesAlone()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);
            writer.Write(Envelope("v1"));

            Directory.CreateDirectory(paths.StateDirectory);
            var orphan1 = paths.NewTempFilePath();
            var orphan2 = paths.NewTempFilePath();
            File.WriteAllText(orphan1, "leftover-from-a-crash");
            File.WriteAllText(orphan2, "another-leftover");
            var unrelated = Path.Combine(paths.StateDirectory, "not-a-temp-file.txt");
            File.WriteAllText(unrelated, "leave me alone");

            writer.SweepOrphanedTempFiles();

            Assert.Empty(TempFiles(paths));
            Assert.True(File.Exists(paths.PrimaryFilePath));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that <see cref="AtomicJsonFileWriter.SweepOrphanedTempFiles"/> is a no-op and does not
    /// throw when the state directory does not exist yet (a brand-new store).
    /// </summary>
    [Fact]
    public void SweepOrphanedTempFiles_NoStateDirectoryYet_DoesNotThrow()
    {
        var (paths, root) = BuildPaths();
        try
        {
            var writer = new AtomicJsonFileWriter(paths);

            writer.SweepOrphanedTempFiles(); // must be a no-op, not throw, on a brand-new store
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── Quarantine retention ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="AtomicJsonFileWriter.EnforceQuarantineRetention"/> keeps only the five
    /// most recently written quarantine files and deletes the older ones.
    /// </summary>
    [Fact]
    public void EnforceQuarantineRetention_KeepsFiveMostRecent_DeletesOlder()
    {
        var (paths, root) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            var kept = new List<string>();
            for (var i = 0; i < 7; i++)
            {
                var path = Path.Combine(paths.StateDirectory, $"application-state.corrupt-2026010{i}-000000.json");
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i)); // i=6 is newest
                kept.Add(path);
            }

            var writer = new AtomicJsonFileWriter(paths);
            writer.EnforceQuarantineRetention();

            var remaining = Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern);
            Assert.Equal(5, remaining.Length);

            // The two oldest (index 0 and 1) must be the ones removed.
            Assert.DoesNotContain(kept[0], remaining);
            Assert.DoesNotContain(kept[1], remaining);
            foreach (var survivor in kept.Skip(2))
            {
                Assert.Contains(survivor, remaining);
            }
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    /// <summary>
    /// Verifies that <see cref="AtomicJsonFileWriter.EnforceQuarantineRetention"/> deletes nothing when
    /// five or fewer quarantine files exist.
    /// </summary>
    [Fact]
    public void EnforceQuarantineRetention_FiveOrFewer_DeletesNothing()
    {
        var (paths, root) = BuildPaths();
        try
        {
            Directory.CreateDirectory(paths.StateDirectory);
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllText(Path.Combine(paths.StateDirectory, $"application-state.corrupt-2026010{i}-000000.json"), "{}");
            }

            var writer = new AtomicJsonFileWriter(paths);
            writer.EnforceQuarantineRetention();

            Assert.Equal(3, Directory.GetFiles(paths.StateDirectory, paths.QuarantineSearchPattern).Length);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static (StateStorePaths Paths, string Root) BuildPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DOrgState_{Guid.NewGuid():N}");
        return (new StateStorePaths(root), root);
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a still-locked handle from a test failure shouldn't mask the real
            // assertion failure.
        }
    }

    private static JsonObject Envelope(string marker) =>
        StateEnvelope.Wrap(new JsonObject { ["marker"] = marker }, StateEnvelope.CurrentSchemaVersion, FixedWrittenAtUtc, "test", StateEnvelope.CurrentSchemaRevision);

    private static string[] TempFiles(StateStorePaths paths) =>
        Directory.Exists(paths.StateDirectory) ? Directory.GetFiles(paths.StateDirectory, paths.TempFileSearchPattern) : Array.Empty<string>();

    private static void TryClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (IOException)
        {
        }
    }
}
