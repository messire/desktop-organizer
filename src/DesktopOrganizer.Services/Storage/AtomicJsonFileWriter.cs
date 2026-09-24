using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using DesktopOrganizer.Services.Abstractions.Storage;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Implements the atomic write protocol for the versioned JSON application-state document
/// (ADR-024 D-6): serialize to a temp file in the same directory → fsync → validate by re-parsing →
/// atomic <see cref="File.Replace(string, string, string, bool)"/> (or <see cref="File.Move(string, string)"/>
/// on first run) → only then is the temp file gone and the primary file updated. Also owns the two
/// pieces of start-of-session housekeeping the write protocol implies: sweeping orphaned temp files
/// left behind by a process that died mid-write, and enforcing quarantine file retention.
/// </summary>
/// <remarks>
/// <b>This type never touches in-memory "committed" state.</b> It either returns having durably
/// replaced <see cref="StateStorePaths.PrimaryFilePath"/>, or throws <see cref="AtomicWriteFailedException"/>
/// having left the primary file exactly as it was before the call (old content, or absent — see
/// <see cref="Write"/> remarks for the one exception). Deciding what a successful/failed write means
/// for the store's own no-op guard and committed-state baseline is <see cref="JsonApplicationStateStore"/>'s
/// job, not this type's.
/// </remarks>
internal sealed class AtomicJsonFileWriter
{
    /// <summary>
    /// Maximum number of quarantine files kept under <see cref="StateStorePaths.StateDirectory"/>
    /// (ADR-024 D-4) — the oldest (by last-write time) are deleted first once this is exceeded.
    /// Matches the retention discipline in <c>JsonLinesDiagnosticsLog.EnsureDirectoryAndRetention</c>.
    /// </summary>
    private const int MaxQuarantineFiles = 5;

    private static readonly TimeSpan[] ReplaceRetryDelays =
    {
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    };

    private readonly StateStorePaths _paths;
    private readonly Func<StateWriteStage, bool>? _testForceFailureAt;
    private readonly Func<int, TimeSpan> _retryDelay;
    private readonly Action<int>? _testOnRetryScheduled;

    /// <summary>
    /// Initializes a new instance of the <see cref="AtomicJsonFileWriter"/> class for production use.
    /// </summary>
    public AtomicJsonFileWriter(StateStorePaths paths)
        : this(paths, testForceFailureAt: null)
    {
    }

    /// <summary>
    /// Test-only overload that lets a caller simulate the write protocol failing (a process death,
    /// an unhandled exception, etc.) immediately after any given <see cref="StateWriteStage"/>
    /// completes, without needing to actually crash the process or race real file-system faults.
    /// Direct analog of <c>JsonLinesDiagnosticsLog._testForceReopenFailure</c> and
    /// <c>PanelItemStorageService.forceCrossVolume</c>. <see langword="null"/> (the production path
    /// above) never injects a failure.
    /// </summary>
    /// <param name="testForceFailureAt">
    /// Invoked with each <see cref="StateWriteStage"/> as the write protocol reaches it; returning
    /// <see langword="true"/> makes <see cref="Write"/> behave exactly as if the process died at that
    /// point (throwing <see cref="AtomicWriteFailedException"/> with that stage).
    /// </param>
    /// <param name="testRetryDelay">
    /// Test-only seam replacing <see cref="ReplaceRetryDelays"/> as the sleep duration between two
    /// <see cref="ReplaceOrMove"/> attempts. <see langword="null"/> (the production path) uses the real
    /// <see cref="ReplaceRetryDelays"/> schedule. Exists so a test can pass <c>_ =&gt; TimeSpan.Zero</c>
    /// and race nothing: combined with <paramref name="testOnRetryScheduled"/> below, a transient-fault
    /// test releases the fault deterministically on the calling thread between attempts instead of
    /// racing a background task against a fixed <see cref="Thread.Sleep(int)"/> window (the wall-clock
    /// flake recorded in docs/Refactoring/Testing/sta-test-conventions.md, "wall-clock тесты").
    /// </param>
    /// <param name="testOnRetryScheduled">
    /// Test-only hook invoked synchronously, on the calling thread, with the zero-based attempt index
    /// immediately after an attempt fails with a transient <see cref="IOException"/>/
    /// <see cref="UnauthorizedAccessException"/> and before the retry delay — i.e. exactly the point
    /// where a test can lift the fault it injected (release a lock, clear a read-only attribute) so the
    /// next attempt is guaranteed to observe the fault cleared, with no dependency on wall-clock timing
    /// or a second thread's scheduling latency.
    /// </param>
    internal AtomicJsonFileWriter(
        StateStorePaths paths,
        Func<StateWriteStage, bool>? testForceFailureAt,
        Func<int, TimeSpan>? testRetryDelay = null,
        Action<int>? testOnRetryScheduled = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _testForceFailureAt = testForceFailureAt;
        _retryDelay = testRetryDelay ?? (attempt => ReplaceRetryDelays[attempt]);
        _testOnRetryScheduled = testOnRetryScheduled;
    }

    /// <summary>
    /// Serializes <paramref name="envelope"/> (already wrapped with the reserved envelope keys — see
    /// <see cref="StateEnvelope.Wrap"/>) and atomically makes it the new primary document.
    /// </summary>
    /// <remarks>
    /// On any failure the temp file created for this attempt is deleted before
    /// <see cref="AtomicWriteFailedException"/> is thrown — <b>except</b> when the failure is
    /// injected at/after <see cref="StateWriteStage.AfterReplace"/>: by that point
    /// <see cref="File.Replace(string, string, string, bool)"/> (or <see cref="File.Move(string, string)"/>)
    /// has already consumed the temp file and the primary file on disk is already the new content —
    /// there is nothing left to delete, and the primary file must not be rolled back (a real crash at
    /// that point in production could never roll back a completed OS-level rename either).
    /// </remarks>
    /// <exception cref="AtomicWriteFailedException">
    /// The write protocol did not complete. <see cref="AtomicWriteFailedException.Stage"/> identifies
    /// where.
    /// </exception>
    public void Write(JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        Directory.CreateDirectory(_paths.StateDirectory);
        var tempPath = _paths.NewTempFilePath();
        var expectedKeyCount = envelope.Count;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            WriteAndFlushTemp(tempPath, bytes);
            ValidateTemp(tempPath, expectedKeyCount);
            ReplaceOrMove(tempPath);
        }
        catch (AtomicWriteFailedException ex)
        {
            if (ex.Stage != StateWriteStage.AfterReplace)
                TryDeleteTempFile(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            // Nothing below should let an un-wrapped exception escape, but this is a last-resort net
            // so a bug here still surfaces as the documented failure contract rather than an opaque
            // exception type callers aren't prepared for.
            TryDeleteTempFile(tempPath);
            throw new AtomicWriteFailedException(StateWriteStage.AfterTempWrite, "Unexpected failure during atomic write.", ex);
        }
    }

    /// <summary>
    /// Deletes every orphaned <c>*.tmp-*</c> file under <see cref="StateStorePaths.StateDirectory"/> —
    /// left behind by a process that died between creating a temp file and the atomic replace that
    /// would have consumed it. Meant to be called once, early, per <see cref="JsonApplicationStateStore.Load"/>
    /// (ADR-024 D-6/R-1). Best-effort: a single file that can't be deleted (e.g. still held by
    /// another process) is skipped rather than failing the whole sweep.
    /// </summary>
    public void SweepOrphanedTempFiles()
    {
        if (!Directory.Exists(_paths.StateDirectory)) return;

        string[] orphans;
        try
        {
            orphans = Directory.GetFiles(_paths.StateDirectory, _paths.TempFileSearchPattern);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AtomicJsonFileWriter: failed to enumerate orphaned temp files: {ex.GetType().Name}");
            return;
        }

        foreach (var orphan in orphans)
        {
            try
            {
                File.Delete(orphan);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AtomicJsonFileWriter: failed to delete orphaned temp file: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Deletes the oldest quarantine files under <see cref="StateStorePaths.StateDirectory"/> beyond
    /// <see cref="MaxQuarantineFiles"/> (ADR-024 D-4). Meant to be called once, early, per
    /// <see cref="JsonApplicationStateStore.Load"/>, mirroring
    /// <c>JsonLinesDiagnosticsLog.EnsureDirectoryAndRetention</c>.
    /// </summary>
    public void EnforceQuarantineRetention()
    {
        if (!Directory.Exists(_paths.StateDirectory)) return;

        string[] existing;
        try
        {
            existing = Directory.GetFiles(_paths.StateDirectory, _paths.QuarantineSearchPattern);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AtomicJsonFileWriter: failed to enumerate quarantine files: {ex.GetType().Name}");
            return;
        }

        if (existing.Length <= MaxQuarantineFiles) return;

        var toDelete = existing
            .OrderBy(File.GetLastWriteTimeUtc)
            .Take(existing.Length - MaxQuarantineFiles);

        foreach (var path in toDelete)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AtomicJsonFileWriter: failed to delete old quarantine file: {ex.GetType().Name}");
            }
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AtomicJsonFileWriter: failed to clean up temp file after a failed write: {ex.GetType().Name}");
        }
    }

    private bool ShouldFail(StateWriteStage stage) => _testForceFailureAt?.Invoke(stage) == true;

    /// <summary>Step 1 + step 2 of D-6: write the payload bytes to the temp file, then a real fsync.</summary>
    private void WriteAndFlushTemp(string tempPath, byte[] bytes)
    {
        try
        {
            using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);

            if (ShouldFail(StateWriteStage.AfterTempWrite))
                throw new AtomicWriteFailedException(StateWriteStage.AfterTempWrite, "Test-injected failure after temp write, before flush.");

            stream.Flush(flushToDisk: true);
        }
        catch (AtomicWriteFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AtomicWriteFailedException(StateWriteStage.AfterTempWrite, "Failed writing/flushing the temp file.", ex);
        }

        if (ShouldFail(StateWriteStage.AfterFlush))
            throw new AtomicWriteFailedException(StateWriteStage.AfterFlush, "Test-injected failure after flush.");
    }

    /// <summary>
    /// Step 3 of D-6: re-open the just-flushed temp file and re-parse it as JSON, confirming it
    /// carries a valid <c>schemaVersion</c> and that every key written is still present — a cheap
    /// structural sanity check against a truncated/corrupted write, not a full deep-equality check.
    /// </summary>
    private void ValidateTemp(string tempPath, int expectedKeyCount)
    {
        JsonObject? parsed;
        try
        {
            using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            parsed = JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception ex)
        {
            throw new AtomicWriteFailedException(StateWriteStage.AfterValidate, "Temp file failed to re-parse as JSON.", ex);
        }

        if (parsed is null || !StateEnvelope.HasValidSchemaVersion(parsed))
            throw new AtomicWriteFailedException(StateWriteStage.AfterValidate, "Temp file failed validation: missing/invalid schemaVersion.");

        if (parsed.Count != expectedKeyCount)
            throw new AtomicWriteFailedException(StateWriteStage.AfterValidate, "Temp file failed validation: payload keys are not all present.");

        if (ShouldFail(StateWriteStage.AfterValidate))
            throw new AtomicWriteFailedException(StateWriteStage.AfterValidate, "Test-injected failure after validate.");
    }

    /// <summary>
    /// Step 4 of D-6: atomically replace the primary file with the validated temp file (or move it
    /// into place, on a first-ever write where no primary exists yet), retrying a transient
    /// <see cref="IOException"/> (AV/indexer holding a handle) or <see cref="UnauthorizedAccessException"/>
    /// (Controlled Folder Access, a restrictive AV product, or an ACL momentarily denying access —
    /// M-3: functionally the same transient "something else has this path locked/blocked right now"
    /// condition as <see cref="IOException"/>, just a different CLR exception type on Windows, so it
    /// gets the identical retry treatment) up to <see cref="ReplaceRetryDelays"/>'s length times.
    /// </summary>
    private void ReplaceOrMove(string tempPath)
    {
        if (ShouldFail(StateWriteStage.BeforeReplace))
            throw new AtomicWriteFailedException(StateWriteStage.BeforeReplace, "Test-injected failure before replace.");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(_paths.PrimaryFilePath))
                    File.Replace(tempPath, _paths.PrimaryFilePath, _paths.BackupFilePath, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, _paths.PrimaryFilePath);

                break;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceRetryDelays.Length)
            {
                _testOnRetryScheduled?.Invoke(attempt);
                Thread.Sleep(_retryDelay(attempt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stage is deliberately still BeforeReplace, not AfterTempWrite: the replace/move
                // itself is what failed, and callers (StateWriteStage's own diagnostics — M-3) must
                // see that accurately rather than a misleading earlier stage.
                throw new AtomicWriteFailedException(StateWriteStage.BeforeReplace, "Replace/move onto the primary file failed after retries.", ex);
            }
        }

        if (ShouldFail(StateWriteStage.AfterReplace))
            throw new AtomicWriteFailedException(StateWriteStage.AfterReplace, "Test-injected failure after replace.");
    }
}
