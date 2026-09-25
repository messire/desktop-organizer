using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Tests.Unit.Diagnostics;

/// <summary>
/// Integration-style unit tests for <see cref="JsonLinesDiagnosticsLog"/>: exercises the real
/// background writer thread against a temp directory (cleaned up in a <c>finally</c> per test, per
/// project convention — see <c>PanelItemStorageServiceTests</c>).
/// </summary>
[Trait(UnitTests, nameof(DesktopOrganizer.Services))]
public class JsonLinesDiagnosticsLogTests
{
    // ── IsEnabled ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="JsonLinesDiagnosticsLog.IsEnabled"/> always returns
    /// <see langword="true"/> for the real (non-null) sink.
    /// </summary>
    [Fact]
    public void IsEnabled_AlwaysReturnsTrue()
    {
        var dir = NewTempDirectory();
        using var sut = new JsonLinesDiagnosticsLog(dir);

        Assert.True(sut.IsEnabled);

        SafeDeleteDirectory(dir);
    }

    // ── One line per event, valid JSON shape ────────────────────────────────

    /// <summary>
    /// Verifies that each <see cref="JsonLinesDiagnosticsLog.Write"/> call produces exactly one JSON
    /// line on disk, with the expected <c>timestamp</c>/<c>seq</c>/<c>category</c>/<c>name</c>/
    /// <c>scope</c>/<c>payload</c> fields.
    /// </summary>
    [Fact]
    public void Write_OneEventPerLine_EachLineIsValidJsonWithExpectedFields()
    {
        var dir = NewTempDirectory();
        try
        {
            using (var sut = new JsonLinesDiagnosticsLog(dir))
            {
                sut.Write(
                    "gesture",
                    "ResizeStarted",
                    "abcd1234",
                    new KeyValuePair<string, object?>("direction", "Right"),
                    new KeyValuePair<string, object?>("boundsWidth", 100.5));
                sut.Write("session", "SessionExiting", null);
                sut.Flush();

                var lines = ReadSessionFileLines(dir);

                Assert.Equal(2, lines.Count);

                var first = lines[0];
                Assert.Equal(JsonValueKind.String, first.GetProperty("timestamp").ValueKind);
                Assert.True(first.GetProperty("seq").GetInt64() > 0);
                Assert.Equal("gesture", first.GetProperty("category").GetString());
                Assert.Equal("ResizeStarted", first.GetProperty("name").GetString());
                Assert.Equal("abcd1234", first.GetProperty("scope").GetString());
                Assert.Equal("Right", first.GetProperty("payload").GetProperty("direction").GetString());
                Assert.Equal(100.5, first.GetProperty("payload").GetProperty("boundsWidth").GetDouble());

                var second = lines[1];
                Assert.Equal("session", second.GetProperty("category").GetString());
                Assert.Equal("SessionExiting", second.GetProperty("name").GetString());
                Assert.Equal(JsonValueKind.Null, second.GetProperty("scope").ValueKind);
                Assert.Equal(JsonValueKind.Object, second.GetProperty("payload").ValueKind);
                Assert.Empty(second.GetProperty("payload").EnumerateObject());
            }
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Sequence monotonicity ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <c>seq</c> field written by <see cref="JsonLinesDiagnosticsLog.Write"/>
    /// strictly increases across many successive events.
    /// </summary>
    [Fact]
    public void Write_MultipleEvents_SequenceIsMonotonicallyIncreasing()
    {
        var dir = NewTempDirectory();
        try
        {
            using (var sut = new JsonLinesDiagnosticsLog(dir))
            {
                for (var i = 0; i < 20; i++)
                    sut.Write("gesture", "ResizeDelta", "abcd1234", new KeyValuePair<string, object?>("i", i));

                sut.Flush();

                var sequences = ReadSessionFileLines(dir).Select(l => l.GetProperty("seq").GetInt64()).ToList();

                Assert.Equal(20, sequences.Count);
                for (var i = 1; i < sequences.Count; i++)
                    Assert.True(sequences[i] > sequences[i - 1], $"seq must strictly increase: {sequences[i - 1]} -> {sequences[i]}");
            }
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Flush makes writes readable ──────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="JsonLinesDiagnosticsLog.Flush"/> makes every prior
    /// <see cref="JsonLinesDiagnosticsLog.Write"/> call readable on disk.
    /// </summary>
    [Fact]
    public void Flush_MakesAllPriorWritesReadableOnDisk()
    {
        var dir = NewTempDirectory();
        try
        {
            using (var sut = new JsonLinesDiagnosticsLog(dir))
            {
                const int eventCount = 30;
                for (var i = 0; i < eventCount; i++)
                    sut.Write("gesture", "ResizeDelta", "abcd1234", new KeyValuePair<string, object?>("i", i));

                sut.Flush();

                var lines = ReadSessionFileLines(dir);
                Assert.Equal(eventCount, lines.Count);
            }
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Verifies that <see cref="JsonLinesDiagnosticsLog.Flush"/> does not throw and completes quickly
    /// when there is nothing pending to flush.
    /// </summary>
    [Fact]
    public void Flush_CalledWithNothingPending_DoesNotThrow_AndCompletesQuickly()
    {
        var dir = NewTempDirectory();
        try
        {
            using var sut = new JsonLinesDiagnosticsLog(dir);
            var stopwatch = Stopwatch.StartNew();

            var exception = Record.Exception(sut.Flush);

            Assert.Null(exception);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Flush took too long: {stopwatch.Elapsed}");
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that calling <see cref="JsonLinesDiagnosticsLog.Dispose"/> twice is idempotent and
    /// never throws.
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_IsIdempotent_DoesNotThrow()
    {
        var dir = NewTempDirectory();
        try
        {
            var sut = new JsonLinesDiagnosticsLog(dir);
            sut.Write("session", "SessionExiting", null);

            sut.Dispose();
            var exception = Record.Exception(sut.Dispose);

            Assert.Null(exception);
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Verifies that <see cref="JsonLinesDiagnosticsLog.Dispose"/> completes within a bounded time and
    /// never hangs, even with a backlog of pending writes.
    /// </summary>
    [Fact]
    public void Dispose_CompletesWithinABoundedTime_NeverHangs()
    {
        var dir = NewTempDirectory();
        try
        {
            var sut = new JsonLinesDiagnosticsLog(dir);
            for (var i = 0; i < 100; i++)
                sut.Write("gesture", "ResizeDelta", "abcd1234", new KeyValuePair<string, object?>("i", i));

            var stopwatch = Stopwatch.StartNew();
            sut.Dispose();

            // Mirrors the type's own ShutdownJoinTimeout (2s) contract — generous upper bound so this
            // isn't flaky under CI load, while still proving shutdown doesn't hang indefinitely.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took too long: {stopwatch.Elapsed}");
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Verifies that <see cref="JsonLinesDiagnosticsLog.Write"/> called after
    /// <see cref="JsonLinesDiagnosticsLog.Dispose"/> does not throw and does not grow the log file.
    /// </summary>
    [Fact]
    public void Write_AfterDispose_DoesNotThrow_AndDoesNotGrowTheFile()
    {
        var dir = NewTempDirectory();
        try
        {
            var sut = new JsonLinesDiagnosticsLog(dir);
            sut.Write("session", "SessionStarted", null);
            sut.Flush();
            var linesBeforeDispose = ReadSessionFileLines(dir).Count;

            sut.Dispose();

            var exception = Record.Exception(() => sut.Write("session", "SessionExiting", null));
            Assert.Null(exception);

            var linesAfterDispose = ReadSessionFileLines(dir).Count;
            Assert.Equal(linesBeforeDispose, linesAfterDispose);
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Verifies that <see cref="JsonLinesDiagnosticsLog.Flush"/> called after
    /// <see cref="JsonLinesDiagnosticsLog.Dispose"/> does not throw.
    /// </summary>
    [Fact]
    public void Flush_AfterDispose_DoesNotThrow()
    {
        var dir = NewTempDirectory();
        try
        {
            var sut = new JsonLinesDiagnosticsLog(dir);
            sut.Dispose();

            var exception = Record.Exception(sut.Flush);

            Assert.Null(exception);
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Directory creation ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the background writer thread creates the target directory if it does not already
    /// exist at construction time.
    /// </summary>
    [Fact]
    public void Construction_TargetDirectoryDoesNotExist_IsCreatedByTheWriterThread()
    {
        var dir = NewTempDirectory();
        Assert.False(Directory.Exists(dir));
        try
        {
            using var sut = new JsonLinesDiagnosticsLog(dir);
            sut.Flush(); // forces the writer thread past its one-time directory/retention setup

            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Retention ────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that construction enforces retention against pre-existing log files, deleting the
    /// oldest ones beyond <c>maxFiles</c>.
    /// </summary>
    [Fact]
    public void Construction_EnforcesRetention_DeletesOldestPreExistingFilesBeyondMaxFiles()
    {
        var dir = NewTempDirectory();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gestures-20200101-000000-1.jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(dir, "gestures-20200102-000000-1.jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(dir, "gestures-20200103-000000-1.jsonl"), string.Empty);

        try
        {
            using var sut = new JsonLinesDiagnosticsLog(dir, maxFiles: 2);
            sut.Flush(); // forces the writer thread past EnsureDirectoryAndRetention, which runs before it

            var remaining = Directory.GetFiles(dir, "gestures-*.jsonl").Select(Path.GetFileName).ToList();

            Assert.DoesNotContain("gestures-20200101-000000-1.jsonl", remaining);
            Assert.Contains("gestures-20200102-000000-1.jsonl", remaining);
            Assert.Contains("gestures-20200103-000000-1.jsonl", remaining);
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Backpressure: queue overflow drops records without blocking the producer ─

    /// <summary>
    /// Uses the internal <c>testWriterStallGate</c> constructor overload to hold the background writer
    /// thread before it ever drains the queue — modeling "the writer is stalled" deterministically
    /// (real disk I/O is normally far faster than the producer, so this can't be reproduced reliably
    /// by racing real timing). With the writer parked, every <c>Write</c> call is a pure in-memory
    /// <c>TryAdd</c> against the bounded queue: the first <c>queueCapacity</c> calls succeed, the rest
    /// are dropped — and none of them block, proving the non-blocking-producer contract holds even
    /// under sustained backpressure.
    /// </summary>
    [Fact]
    public void Write_QueueFull_DropsExcessRecords_ProducerNeverBlocks_AndDropIsReportedOnNextFlush()
    {
        var dir = NewTempDirectory();
        using var stallGate = new ManualResetEventSlim(initialState: false);
        const int queueCapacity = 5;
        const int totalWritten = 8;
        const int expectedDropped = totalWritten - queueCapacity;

        try
        {
            var sut = new JsonLinesDiagnosticsLog(dir, maxFiles: 10, maxBytes: 10L * 1024 * 1024, queueCapacity, stallGate);
            try
            {
                var stopwatch = Stopwatch.StartNew();
                for (var i = 0; i < totalWritten; i++)
                    sut.Write("gesture", "ResizeDelta", "abcd1234", new KeyValuePair<string, object?>("i", i));
                stopwatch.Stop();

                // The producer must never block waiting for queue room, even with the writer fully
                // parked and the queue already at capacity.
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Write() blocked the producer: {stopwatch.Elapsed}");
                Assert.Equal(expectedDropped, sut.DroppedCount);

                // Release the writer: it opens the file, drains the surviving queueCapacity events,
                // and — because drops occurred — prepends a DiagnosticsDropped marker on its next flush.
                stallGate.Set();
                sut.Flush();

                Assert.Equal(0, sut.DroppedCount); // reset once reported

                var lines = ReadSessionFileLines(dir);
                var realEvents = lines.Where(l => l.GetProperty("name").GetString() == "ResizeDelta").ToList();
                var dropMarkers = lines.Where(l => l.GetProperty("name").GetString() == "DiagnosticsDropped").ToList();

                Assert.Equal(queueCapacity, realEvents.Count);
                var marker = Assert.Single(dropMarkers);
                Assert.Equal("session", marker.GetProperty("category").GetString());
                Assert.Equal(expectedDropped, marker.GetProperty("payload").GetProperty("count").GetInt64());
            }
            finally
            {
                stallGate.Set(); // safety net: never leave the writer thread parked if an assertion throws
                sut.Dispose();
            }
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Verifies that <see cref="JsonLinesDiagnosticsLog.DroppedCount"/> stays zero when the queue never
    /// overflows.
    /// </summary>
    [Fact]
    public void DroppedCount_NoOverflow_StaysZero()
    {
        var dir = NewTempDirectory();
        try
        {
            using var sut = new JsonLinesDiagnosticsLog(dir);
            sut.Write("gesture", "ResizeDelta", "abcd1234");
            sut.Flush();

            Assert.Equal(0, sut.DroppedCount);
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Argument validation ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the <see cref="JsonLinesDiagnosticsLog"/> constructor throws
    /// <see cref="ArgumentNullException"/> for a <see langword="null"/> directory.
    /// </summary>
    [Fact]
    public void Constructor_NullDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new JsonLinesDiagnosticsLog(null!));
    }

    /// <summary>
    /// Verifies that the <see cref="JsonLinesDiagnosticsLog"/> constructor throws
    /// <see cref="ArgumentOutOfRangeException"/> for a non-positive <c>maxFiles</c>.
    /// </summary>
    [Fact]
    public void Constructor_NonPositiveMaxFiles_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JsonLinesDiagnosticsLog(NewTempDirectory(), maxFiles: 0));
    }

    /// <summary>
    /// Verifies that the <see cref="JsonLinesDiagnosticsLog"/> constructor throws
    /// <see cref="ArgumentOutOfRangeException"/> for a non-positive <c>maxBytes</c>.
    /// </summary>
    [Fact]
    public void Constructor_NonPositiveMaxBytes_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JsonLinesDiagnosticsLog(NewTempDirectory(), maxBytes: 0));
    }

    /// <summary>
    /// Verifies that the <see cref="JsonLinesDiagnosticsLog"/> constructor throws
    /// <see cref="ArgumentOutOfRangeException"/> for a non-positive <c>queueCapacity</c>.
    /// </summary>
    [Fact]
    public void Constructor_NonPositiveQueueCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JsonLinesDiagnosticsLog(NewTempDirectory(), queueCapacity: 0));
    }

    // ── Rotation by maxBytes ─────────────────────────────────────────────────

    /// <summary>
    /// <c>maxBytes: 1</c> guarantees any written line pushes <c>currentBytes</c> past the threshold,
    /// so every <see cref="JsonLinesDiagnosticsLog.Flush"/> call that follows a write rotates
    /// deterministically — independent of the exact serialized line length. Drives three rotations and
    /// checks the full shift: current → <c>-1</c>, <c>-1</c> → <c>-2</c>, oldest <c>-2</c> deleted, and
    /// the base path stays usable for further writes after each rotation.
    /// </summary>
    [Fact]
    public void Write_ExceedsMaxBytes_RotatesGenerationsAndKeepsWritingOnFreshBasePath()
    {
        var dir = NewTempDirectory();
        try
        {
            using var sut = new JsonLinesDiagnosticsLog(dir, maxFiles: 10, maxBytes: 1);

            // Flush with nothing written yet: forces the writer thread past startup (so the base
            // session file exists) without tripping rotation (currentBytes is still 0).
            sut.Flush();
            var sessionFile = Assert.Single(Directory.GetFiles(dir, "gestures-*.jsonl"));
            var baseNoExt = Path.GetFileNameWithoutExtension(sessionFile);
            var backup1 = Path.Combine(dir, $"{baseNoExt}-1.jsonl");
            var backup2 = Path.Combine(dir, $"{baseNoExt}-2.jsonl");

            sut.Write("gesture", "Event1", null);
            sut.Flush(); // rotation #1: base(Event1) -> -1

            Assert.True(File.Exists(backup1));
            Assert.False(File.Exists(backup2));
            Assert.Equal("Event1", Assert.Single(ReadJsonLines(backup1)).GetProperty("name").GetString());

            sut.Write("gesture", "Event2", null);
            sut.Flush(); // rotation #2: -1(Event1) -> -2; base(Event2) -> -1

            Assert.Equal("Event1", Assert.Single(ReadJsonLines(backup2)).GetProperty("name").GetString());
            Assert.Equal("Event2", Assert.Single(ReadJsonLines(backup1)).GetProperty("name").GetString());

            sut.Write("gesture", "Event3", null);
            sut.Flush(); // rotation #3: -2(Event1) deleted; -1(Event2) -> -2; base(Event3) -> -1

            Assert.Equal("Event2", Assert.Single(ReadJsonLines(backup2)).GetProperty("name").GetString());
            Assert.Equal("Event3", Assert.Single(ReadJsonLines(backup1)).GetProperty("name").GetString());

            // Exactly three files on disk: the (fresh, empty) base session file plus the two backups —
            // Event1's content is gone for good, matching RotationBackupCount == 2.
            var allFiles = Directory.GetFiles(dir, "gestures-*.jsonl");
            Assert.Equal(3, allFiles.Length);
            Assert.Contains(sessionFile, allFiles);
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Degraded mode: reopening the file after rotation fails (M-1) ─────────

    /// <summary>
    /// Uses the internal <c>testForceReopenFailure</c> hook to deterministically fail the reopen call
    /// <see cref="JsonLinesDiagnosticsLog"/> makes immediately after rotating the current session file
    /// out (see the type's <c>RotateIfNeeded</c> remarks on M-1), without needing to race real
    /// file-locking. The writer thread must survive: subsequent <c>Write</c>/<c>Flush</c>/<c>Dispose</c>
    /// calls must not throw, and the thread must not resurrect a session file once degraded.
    /// </summary>
    [Fact]
    public void RotateIfNeeded_ReopenAfterRotationFails_DegradesGracefullyWithoutCrashingWriterThread()
    {
        var dir = NewTempDirectory();
        try
        {
            var sut = new JsonLinesDiagnosticsLog(
                dir,
                maxFiles: 10,
                maxBytes: 1,
                queueCapacity: 4096,
                testWriterStallGate: null,
                testForceReopenFailure: () => true);
            try
            {
                sut.Flush(); // past startup, before any rotation
                var sessionFile = Assert.Single(Directory.GetFiles(dir, "gestures-*.jsonl"));

                sut.Write("gesture", "Event1", null);
                var flushException = Record.Exception(() => sut.Flush());
                Assert.Null(flushException);

                // Rotation moved the base file to -1 (content preserved), but the forced reopen
                // failure means the writer degrades to "no file" mode: the base path is not recreated.
                var baseNoExt = Path.GetFileNameWithoutExtension(sessionFile);
                var backup1 = Path.Combine(dir, $"{baseNoExt}-1.jsonl");
                Assert.True(File.Exists(backup1));
                Assert.Equal("Event1", Assert.Single(ReadJsonLines(backup1)).GetProperty("name").GetString());
                Assert.False(File.Exists(sessionFile));

                // Further writes must be silently absorbed (degraded mode), never throw, and never
                // resurrect the base session file.
                var secondException = Record.Exception(() =>
                {
                    sut.Write("gesture", "Event2", null);
                    sut.Flush();
                });
                Assert.Null(secondException);
                Assert.False(File.Exists(sessionFile));
            }
            finally
            {
                var disposeException = Record.Exception(sut.Dispose);
                Assert.Null(disposeException);
            }
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Write() ∥ Dispose() race (C-2) ────────────────────────────────────────

    /// <summary>
    /// Regression test for C-2: several threads call <c>Write</c> in a tight loop while the main thread
    /// concurrently calls <c>Dispose</c>. <c>Write</c>'s catch around <c>TryAdd</c>
    /// (<see cref="InvalidOperationException"/> once <c>CompleteAdding</c> has run, or
    /// <see cref="ObjectDisposedException"/> once the queue itself has been disposed) must absorb both
    /// races; no exception may ever escape back into a caller — typically a UI-thread gesture handler.
    /// </summary>
    [Fact]
    public void Write_ConcurrentWithDispose_NeverThrowsBackIntoTheCaller()
    {
        var dir = NewTempDirectory();
        try
        {
            var sut = new JsonLinesDiagnosticsLog(dir);
            var startGate = new ManualResetEventSlim(initialState: false);
            var writerExceptions = new ConcurrentBag<Exception>();

            var writerThreads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                startGate.Wait();
                for (var i = 0; i < 500; i++)
                {
                    try
                    {
                        sut.Write("gesture", "ResizeDelta", "scope", new KeyValuePair<string, object?>("i", i));
                    }
                    catch (Exception ex)
                    {
                        writerExceptions.Add(ex);
                    }
                }
            })).ToList();

            foreach (var thread in writerThreads) thread.Start();
            startGate.Set();

            // Race Dispose() against the writer threads rather than waiting for them to finish first —
            // the whole point of this test is exercising the overlap window, not the end state.
            var disposeException = Record.Exception(sut.Dispose);

            foreach (var thread in writerThreads) thread.Join();

            Assert.Null(disposeException);
            Assert.Empty(writerExceptions);
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── Dispose() with a stalled writer thread (C-1) ──────────────────────────

    /// <summary>
    /// Regression test for C-1: if the writer thread doesn't exit within <c>ShutdownJoinTimeout</c>,
    /// <c>Dispose()</c> must still return (not hang indefinitely) and must not throw. This test holds
    /// the writer thread parked forever via the <c>testWriterStallGate</c> hook — it intentionally
    /// never calls <c>stallGate.Set()</c>, so the writer thread (and the gate, queue, and flush handles
    /// it may still touch) is deliberately leaked for the remaining lifetime of the test process; this
    /// mirrors the documented, deliberate tradeoff of the C-1 fix itself (see
    /// <see cref="JsonLinesDiagnosticsLog"/>'s <c>Dispose</c> remarks) — a caller must never hang
    /// waiting for a stalled disk/writer thread at shutdown, even at the cost of a small, bounded leak.
    /// This test must not attempt to release the gate or assert anything about the leaked thread.
    /// </summary>
    [Fact]
    public void Dispose_WriterThreadStalledPastShutdownTimeout_ReturnsPromptlyWithoutThrowing()
    {
        var dir = NewTempDirectory();
        var stallGate = new ManualResetEventSlim(initialState: false); // deliberately never Set()
        try
        {
            var sut = new JsonLinesDiagnosticsLog(dir, maxFiles: 10, maxBytes: 10L * 1024 * 1024, queueCapacity: 4096, stallGate);

            var stopwatch = Stopwatch.StartNew();
            var exception = Record.Exception(sut.Dispose);
            stopwatch.Stop();

            Assert.Null(exception);

            // Generous upper bound above the ~1.5s ShutdownJoinTimeout so this isn't flaky under CI
            // load, while still proving Dispose() doesn't hang indefinitely waiting for the stalled
            // thread.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took too long: {stopwatch.Elapsed}");
        }
        finally
        {
            SafeDeleteDirectory(dir);

            // Deliberately do NOT Set() or Dispose() stallGate: the writer thread it gates is parked
            // forever by design in this test and may still touch it.
        }
    }

    // ── Flush() under continuous write load (M-3) ─────────────────────────────

    /// <summary>
    /// Regression test for M-3: a producer thread hammering <c>Write</c> must never starve a concurrent
    /// <c>Flush()</c> request indefinitely. <c>RunLoop</c> bounds how much of the queue it drains
    /// per outer iteration (<c>FlushBatchSize</c>) before re-checking <c>_flushRequested</c>, so
    /// <c>Flush()</c> must return well inside its own <c>FlushWaitTimeout</c> budget even while the
    /// producer keeps going.
    /// </summary>
    [Fact]
    public void Flush_WhileProducerWritesContinuously_ReturnsWithinBoundedTime()
    {
        var dir = NewTempDirectory();
        try
        {
            using var sut = new JsonLinesDiagnosticsLog(dir);
            using var stopSignal = new CancellationTokenSource();

            var producer = new Thread(() =>
            {
                var i = 0;
                while (!stopSignal.IsCancellationRequested)
                    sut.Write("gesture", "ResizeDelta", "scope", new KeyValuePair<string, object?>("i", i++));
            })
            { IsBackground = true };
            producer.Start();

            Thread.Sleep(20); // let the producer get ahead of the writer thread before racing Flush()

            var stopwatch = Stopwatch.StartNew();
            sut.Flush();
            stopwatch.Stop();

            stopSignal.Cancel();
            producer.Join();

            // FlushWaitTimeout is 1.5s; a healthy writer thread under continuous load must service a
            // Flush() request well inside that budget, with a modest margin for CI scheduling noise.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Flush took too long under load: {stopwatch.Elapsed}");
        }
        finally
        {
            SafeDeleteDirectory(dir);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static string NewTempDirectory() => Path.Combine(Path.GetTempPath(), $"DOrgDiagLog_{Guid.NewGuid():N}");

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a still-locked handle shouldn't mask the real assertion failure.
        }
    }

    /// <summary>
    /// Reads and parses every JSONL line from the (single) session file in <paramref name="directory"/>
    /// — while <c>JsonLinesDiagnosticsLog</c>'s own writer thread may still have the file open for
    /// append. Opens with <see cref="FileShare.ReadWrite"/> rather than <see cref="File.ReadAllLines(string)"/>'s
    /// default <see cref="FileShare.Read"/>-only request, matching how any tool that tails an
    /// actively-written log file should open it. Retries transient <see cref="IOException"/>s with a
    /// short backoff on top, as a safety net against antivirus/indexing briefly locking a freshly
    /// created file under %TEMP% (the same class of transient lock <c>PanelItemStorageServiceTests</c>'
    /// C1 regression test documents).
    /// </summary>
    private static List<JsonElement> ReadSessionFileLines(string directory)
    {
        var file = Assert.Single(Directory.GetFiles(directory, "gestures-*.jsonl"));
        return ReadJsonLines(file);
    }

    /// <summary>
    /// Same retry/sharing semantics as <see cref="ReadSessionFileLines"/>, but for an explicit file
    /// path — used once rotation is involved and more than one <c>gestures-*.jsonl</c> file can exist
    /// in the directory at once.
    /// </summary>
    private static List<JsonElement> ReadJsonLines(string file)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var lines = new List<JsonElement>();
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(JsonDocument.Parse(line).RootElement.Clone());
                }

                return lines;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }
}
