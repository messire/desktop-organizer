using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Diagnostics;

/// <summary>
/// Structured JSONL diagnostics sink for gesture lifecycle events (resize/drag) and other
/// troubleshooting instrumentation. Backed by a single background writer thread so the calling
/// (UI) thread is never blocked on file I/O.
/// </summary>
/// <remarks>
/// <b>Non-blocking producer:</b> <see cref="Write"/> only serializes the event to a string and
/// enqueues it on a bounded queue; it never opens a file, never blocks, and never throws back into
/// the caller. All file-system access — directory creation, retention cleanup, opening/rotating the
/// log file, and every write — happens on a single dedicated background thread.
/// <para>
/// <b>Backpressure:</b> if the queue is full, <see cref="Write"/> drops the event and increments an
/// internal counter; the next time the writer thread actually flushes to disk, it first emits a
/// single synthetic <c>DiagnosticsDropped</c> event carrying the number of events lost since the
/// last flush, then resets the counter to zero. The producer itself never blocks waiting for room.
/// </para>
/// <para>
/// <b>Privacy invariant:</b> this type only ever writes what its <see cref="DiagnosticEvent"/>
/// inputs give it. It never itself reads or logs panel titles, file names, or file paths — callers
/// are responsible for keeping payloads to opaque identifiers, geometry, and enum/bool state; see
/// <see cref="DiagnosticEvent"/> remarks.
/// </para>
/// <para>
/// <b>Shutdown budget:</b> <see cref="Flush"/> waits up to <see cref="FlushWaitTimeout"/> and
/// <see cref="Dispose"/>'s thread join waits up to <see cref="ShutdownJoinTimeout"/>; a caller that
/// flushes and then disposes (the normal app-exit sequence) can therefore block for up to the sum of
/// both — currently ~3 seconds — in the worst case (e.g. a stalled disk).
/// </para>
/// </remarks>
public sealed class JsonLinesDiagnosticsLog : IDiagnosticsLog
{
    private const string FilePattern = "gestures-*.jsonl";
    private const int RotationBackupCount = 2;
    private const int FlushBatchSize = 64;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownJoinTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan FlushWaitTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly int NewLineByteLength = Encoding.UTF8.GetByteCount(Environment.NewLine);

    private readonly string _directory;
    private readonly int _maxFiles;
    private readonly long _maxBytes;
    private readonly BlockingCollection<string> _queue;
    private readonly Thread _writerThread;
    private readonly ManualResetEventSlim _flushRequested = new(initialState: false);
    private readonly ManualResetEventSlim _flushCompleted = new(initialState: false);
    private readonly object _flushLock = new();
    private readonly ManualResetEventSlim? _testWriterStallGate;
    private readonly Func<bool>? _testForceReopenFailure;

    private long _sequence;
    private long _droppedCount;
    private string _currentFilePath = string.Empty;

    // volatile: Write() and Dispose() run on different threads with no other synchronization between
    // them, so the writer must observe a Dispose() from Write() as soon as it happens (see C-2).
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonLinesDiagnosticsLog"/> class and starts its
    /// background writer thread. No file-system access happens on the calling thread — directory
    /// creation, retention cleanup, and opening the first log file all happen on the writer thread
    /// itself.
    /// </summary>
    /// <param name="directory">The folder diagnostics log files are written to.</param>
    /// <param name="maxFiles">Maximum number of session log files retained (oldest deleted first).</param>
    /// <param name="maxBytes">Size threshold at which the current session's file is rotated.</param>
    /// <param name="queueCapacity">
    /// Bounded capacity of the producer→writer queue. Once full, new events are dropped (see type
    /// remarks) rather than blocking the caller.
    /// </param>
    public JsonLinesDiagnosticsLog(string directory, int maxFiles = 10, long maxBytes = 10L * 1024 * 1024, int queueCapacity = 4096)
        : this(directory, maxFiles, maxBytes, queueCapacity, testWriterStallGate: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonLinesDiagnosticsLog"/> class. Test-only
    /// overload that lets a caller pause the background writer thread immediately after it starts —
    /// before it ever drains the queue — via <paramref name="testWriterStallGate"/>. Exists solely so
    /// backpressure/drop behavior (see the type remarks) can be exercised deterministically without
    /// racing real disk I/O timing; mirrors the <c>forceCrossVolume</c> test hook pattern used by
    /// <c>PanelItemStorageService</c>.
    /// </summary>
    /// <param name="directory">The folder diagnostics log files are written to.</param>
    /// <param name="maxFiles">Maximum number of session log files retained (oldest deleted first).</param>
    /// <param name="maxBytes">Size threshold at which the current session's file is rotated.</param>
    /// <param name="queueCapacity">Bounded capacity of the producer→writer queue.</param>
    /// <param name="testWriterStallGate">
    /// When non-<see langword="null"/>, the writer thread waits on this gate right after starting, before
    /// draining the queue for the first time.
    /// </param>
    internal JsonLinesDiagnosticsLog(string directory, int maxFiles, long maxBytes, int queueCapacity, ManualResetEventSlim? testWriterStallGate)
        : this(directory, maxFiles, maxBytes, queueCapacity, testWriterStallGate, testForceReopenFailure: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonLinesDiagnosticsLog"/> class. Test-only
    /// overload that additionally lets a caller force every post-rotation file reopen (see
    /// <see cref="RotateIfNeeded"/>) to fail, deterministically exercising the M-1 degraded-mode path
    /// (a rotation hiccup must never silently kill the writer thread) without needing to race real
    /// file-system locking. Mirrors <c>_testWriterStallGate</c>'s pattern of a test-only hook that
    /// no-ops (<see langword="null"/>) on the production path.
    /// </summary>
    /// <param name="directory">The folder diagnostics log files are written to.</param>
    /// <param name="maxFiles">Maximum number of session log files retained (oldest deleted first).</param>
    /// <param name="maxBytes">Size threshold at which the current session's file is rotated.</param>
    /// <param name="queueCapacity">Bounded capacity of the producer→writer queue.</param>
    /// <param name="testWriterStallGate">
    /// When non-<see langword="null"/>, the writer thread waits on this gate right after starting, before
    /// draining the queue for the first time.
    /// </param>
    /// <param name="testForceReopenFailure">
    /// When non-<see langword="null"/>, invoked on every post-rotation file reopen; a <see langword="true"/>
    /// result forces that reopen attempt to fail, exercising the degraded-mode path.
    /// </param>
    internal JsonLinesDiagnosticsLog(string directory, int maxFiles, long maxBytes, int queueCapacity, ManualResetEventSlim? testWriterStallGate, Func<bool>? testForceReopenFailure)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));

        _directory = directory;
        _maxFiles = maxFiles;
        _maxBytes = maxBytes;
        _queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), queueCapacity);
        _testWriterStallGate = testWriterStallGate;
        _testForceReopenFailure = testForceReopenFailure;

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "DesktopOrganizer.DiagnosticsWriter",
        };
        _writerThread.Start();
    }

    /// <inheritdoc />
    public bool IsEnabled => !_disposed;

    /// <summary>
    /// Test-only: current count of events dropped since the last flush (mirrors
    /// <c>PanelItemStorageService.ManifestDiskReadCount</c>'s pattern of exposing an internal counter
    /// for assertions instead of inferring it indirectly).
    /// </summary>
    internal long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc />
    public void Write(string category, string name, string? scope, params KeyValuePair<string, object?>[] payload)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(name);

        if (_disposed) return;

        var diagnosticEvent = new DiagnosticEvent(
            DateTimeOffset.Now,
            Interlocked.Increment(ref _sequence),
            category,
            name,
            scope,
            payload ?? Array.Empty<KeyValuePair<string, object?>>());

        string line;
        try
        {
            line = DiagnosticEventJsonWriter.ToJsonLine(diagnosticEvent);
        }
        catch (Exception ex)
        {
            // Serialization must never throw back into gesture-handling code; drop this one event.
            Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to serialize '{category}/{name}': {ex.Message}");
            return;
        }

        try
        {
            // TryAdd with no timeout never blocks: it returns false immediately once the bounded
            // queue is full, which is exactly the backpressure behavior this type promises the caller.
            if (!_queue.TryAdd(line))
                Interlocked.Increment(ref _droppedCount);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // A concurrent Dispose() raced past the `_disposed` check above: CompleteAdding() makes
            // TryAdd throw InvalidOperationException, and — once the queue has actually been disposed
            // — ObjectDisposedException. Either way, Write() must never throw back into the caller
            // (typically a UI-thread gesture handler); treat the event as dropped.
            Interlocked.Increment(ref _droppedCount);
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (_disposed) return;

        try
        {
            lock (_flushLock)
            {
                _flushCompleted.Reset();
                _flushRequested.Set();
                if (!_flushCompleted.Wait(FlushWaitTimeout))
                    Trace.WriteLine("JsonLinesDiagnosticsLog: Flush() timed out waiting for the writer thread; it may be stalled or have exited abnormally.");
            }
        }
        catch (ObjectDisposedException)
        {
            // A concurrent Dispose() raced past the `_disposed` check above and tore down the flush
            // handles between the check and here; nothing left to flush. Mirrors Write()'s "never
            // throws back into the caller" contract (see C-2).
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // No new events can be enqueued past this point; the writer thread drains whatever is left,
        // performs a final flush, and exits its loop — see WriterLoop's IsCompleted check.
        _queue.CompleteAdding();

        // Bounded wait mirroring SystemShellService.Dispose(): app shutdown must never hang
        // indefinitely on the writer thread (e.g. a slow/contended disk).
        var joined = _writerThread.Join(ShutdownJoinTimeout);
        if (!joined)
        {
            // The writer thread is still alive and may still touch _queue/_flushRequested/
            // _flushCompleted (e.g. it's blocked inside a slow disk write, or sitting in its
            // finally block about to call _flushCompleted.Set()). Disposing those objects here would
            // race an in-flight access on that thread and throw ObjectDisposedException on a
            // non-threadpool background thread, which crashes the whole process (see C-1). Leaking
            // three small synchronization/collection objects for the remaining life of the process is
            // far preferable, so deliberately skip disposing them in this branch.
            Trace.WriteLine("JsonLinesDiagnosticsLog: writer thread did not exit within the shutdown timeout; leaking its synchronization objects rather than risking a crash on the still-running thread.");
            return;
        }

        _queue.Dispose();
        _flushRequested.Dispose();
        _flushCompleted.Dispose();
    }

    private static (StreamWriter Writer, long Bytes) OpenAppend(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream) { AutoFlush = false };
        return (writer, stream.Length);
    }

    private static bool TryWriteLine(StreamWriter writer, string line, ref long currentBytes)
    {
        try
        {
            writer.WriteLine(line);
            currentBytes += Encoding.UTF8.GetByteCount(line) + NewLineByteLength;
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to write log line: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Shifts <c>{base}-1.jsonl</c> → <c>{base}-2.jsonl</c> (dropping the oldest generation beyond
    /// <see cref="RotationBackupCount"/>), then moves the current session file to <c>{base}-1.jsonl</c>,
    /// freeing up <paramref name="basePath"/> for a fresh writer.
    /// </summary>
    private static void RotateFiles(string basePath)
    {
        var dir = Path.GetDirectoryName(basePath)!;
        var nameNoExt = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);

        var oldestBackup = Path.Combine(dir, $"{nameNoExt}-{RotationBackupCount}{ext}");
        if (File.Exists(oldestBackup)) File.Delete(oldestBackup);

        for (var generation = RotationBackupCount - 1; generation >= 1; generation--)
        {
            var source = Path.Combine(dir, $"{nameNoExt}-{generation}{ext}");
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(dir, $"{nameNoExt}-{generation + 1}{ext}");
            File.Move(source, destination, overwrite: true);
        }

        var firstBackup = Path.Combine(dir, $"{nameNoExt}-1{ext}");
        if (File.Exists(basePath)) File.Move(basePath, firstBackup, overwrite: true);
    }

    /// <summary>
    /// Runs on the single dedicated background thread for the lifetime of this instance. Owns every
    /// piece of file-system state (directory, retention, the open <see cref="StreamWriter"/>,
    /// rotation) so the producer thread(s) never touch disk. Must never let an exception escape —
    /// an unhandled exception on a background thread would crash the whole process, which would be
    /// a far worse outcome than losing diagnostics.
    /// </summary>
    private void WriterLoop()
    {
        // Test-only: lets a test hold the writer thread here — before it ever opens a file or drains
        // the queue — to deterministically exercise queue-full backpressure/drop behavior. Null (the
        // production path) skips straight through.
        _testWriterStallGate?.Wait();

        StreamWriter? writer = null;
        var currentBytes = 0L;

        try
        {
            EnsureDirectoryAndRetention();
            var opened = OpenNewSessionFile();
            writer = opened.Writer;
            currentBytes = opened.Bytes;
        }
        catch (Exception ex)
        {
            // Degraded mode: keep draining/discarding the queue below rather than crashing the
            // writer thread, so a permissions/disk issue never takes down the whole process.
            Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to initialize log file: {ex.Message}");
        }

        try
        {
            RunLoop(ref writer, ref currentBytes);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"JsonLinesDiagnosticsLog: writer loop failed: {ex.Message}");
        }
        finally
        {
            try
            {
                (writer, currentBytes) = FlushAndRotate(writer, currentBytes);
                writer?.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to close log file: {ex.Message}");
            }

            try
            {
                // A Flush()/Dispose() call could be waiting on this even if the loop exited
                // abnormally. Guarded by its own try/catch (defense in depth on top of Dispose()'s
                // conditional-disposal fix, see C-1): if Dispose()'s Join() already timed out and the
                // caller went ahead and disposed these handles anyway (e.g. a future change reverts
                // that fix, or a test disposes the handle directly), Set() must not be allowed to
                // throw off this background thread and crash the process.
                _flushCompleted.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void RunLoop(ref StreamWriter? writer, ref long currentBytes)
    {
        var pendingSinceFlush = 0;
        var flushStopwatch = Stopwatch.StartNew();

        while (true)
        {
            var got = _queue.TryTake(out var line, PollInterval);
            if (got && writer is not null && TryWriteLine(writer, line!, ref currentBytes))
                pendingSinceFlush++;

            // Drain anything else already sitting in the queue before honoring a flush request below.
            // Without this, a producer that enqueues a burst of events and then immediately calls
            // Flush() (same thread, program order — see Write()/Flush()) could have that Flush() return
            // after only the single item taken above was written, leaving the rest of the burst still
            // queued: Flush()'s "waits for the flush to complete" contract (IDiagnosticsLog.Flush) means
            // everything enqueued strictly before the call, not just the next queue item.
            //
            // Bounded to FlushBatchSize per outer iteration: a continuously-writing producer could
            // otherwise keep this inner loop draining forever (the queue never actually running dry),
            // starving a pending Flush() request indefinitely. Capping it here — combined with
            // dueForBatchFlush below re-checking after every FlushBatchSize items written in total —
            // guarantees a Flush() request is serviced within at most a couple of batches, not only
            // once a live producer happens to pause.
            var batchDrained = 0;
            while (got && batchDrained < FlushBatchSize && _queue.TryTake(out var extraLine))
            {
                batchDrained++;
                if (writer is not null && TryWriteLine(writer, extraLine, ref currentBytes))
                    pendingSinceFlush++;
            }

            var flushRequested = _flushRequested.IsSet;
            var dueForTimedFlush = pendingSinceFlush > 0 && flushStopwatch.Elapsed >= FlushInterval;
            var dueForBatchFlush = pendingSinceFlush >= FlushBatchSize;

            if (flushRequested || dueForTimedFlush || dueForBatchFlush)
            {
                (writer, currentBytes) = FlushAndRotate(writer, currentBytes);
                pendingSinceFlush = 0;
                flushStopwatch.Restart();
            }

            if (flushRequested)
            {
                _flushRequested.Reset();
                _flushCompleted.Set();
            }

            if (!got && _queue.IsCompleted) return;
        }
    }

    /// <summary>
    /// Deletes pre-existing session files beyond <see cref="_maxFiles"/>, keeping the most recent
    /// ones. Runs once, before <see cref="OpenNewSessionFile"/> creates this session's own file — so
    /// retention accounting only ever sees files from prior sessions. Consequently the directory can
    /// briefly (and, in a short-lived session, for its whole lifetime) hold <c>_maxFiles + 1</c> files
    /// once the new session file is created: this is intentional, not an off-by-one bug — reordering
    /// would require deleting the oldest prior file based on a file that doesn't exist yet.
    /// </summary>
    private void EnsureDirectoryAndRetention()
    {
        Directory.CreateDirectory(_directory);

        try
        {
            var existing = Directory.GetFiles(_directory, FilePattern);
            var toDelete = DiagnosticsLogFileNaming.SelectForDeletion(existing, _maxFiles);
            foreach (var path in toDelete)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to delete old log '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"JsonLinesDiagnosticsLog: retention cleanup failed: {ex.Message}");
        }
    }

    private (StreamWriter Writer, long Bytes) OpenNewSessionFile()
    {
        _currentFilePath = Path.Combine(_directory, DiagnosticsLogFileNaming.BuildFileName(DateTimeOffset.Now, Environment.ProcessId));
        return OpenAppend(_currentFilePath);
    }

    /// <summary>
    /// If there were any events dropped (queue-full backpressure) since the last flush, writes a
    /// single synthetic <c>DiagnosticsDropped</c> marker recording the count, then flushes the
    /// stream to disk and rotates it if it has grown past <see cref="_maxBytes"/>. No-op (beyond
    /// resetting the drop counter) when <paramref name="writer"/> is <see langword="null"/> — the
    /// degraded, no-file mode entered when initialization failed.
    /// </summary>
    private (StreamWriter? Writer, long Bytes) FlushAndRotate(StreamWriter? writer, long currentBytes)
    {
        if (writer is null)
        {
            Interlocked.Exchange(ref _droppedCount, 0);
            return (null, currentBytes);
        }

        var dropped = Interlocked.Exchange(ref _droppedCount, 0);
        if (dropped > 0)
            WriteDropMarker(writer, dropped, ref currentBytes);

        try
        {
            writer.Flush();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to flush log file: {ex.Message}");
        }

        return RotateIfNeeded(writer, currentBytes);
    }

    private void WriteDropMarker(StreamWriter writer, long dropped, ref long currentBytes)
    {
        try
        {
            var markerEvent = new DiagnosticEvent(
                DateTimeOffset.Now,
                Interlocked.Increment(ref _sequence),
                "session",
                "DiagnosticsDropped",
                null,
                new KeyValuePair<string, object?>[] { new("count", dropped) });

            var line = DiagnosticEventJsonWriter.ToJsonLine(markerEvent);
            TryWriteLine(writer, line, ref currentBytes);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to write drop marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Rotates the current session file once it has grown past <see cref="_maxBytes"/>, then reopens
    /// a fresh append-mode writer at the same base path. Never lets an exception escape — including
    /// from reopening the file after rotation (e.g. a locked/held file due to AV or a tailing tool) —
    /// so a single rotation hiccup degrades to <c>(null, 0L)</c> (the same "no-file" degraded mode
    /// <see cref="FlushAndRotate"/> already handles for startup failures) rather than propagating out
    /// of the writer thread, which would silently kill it for the rest of the session (see M-1).
    /// </summary>
    private (StreamWriter? Writer, long Bytes) RotateIfNeeded(StreamWriter writer, long currentBytes)
    {
        if (currentBytes < _maxBytes) return (writer, currentBytes);

        try
        {
            writer.Flush();
            writer.Dispose();
            RotateFiles(_currentFilePath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"JsonLinesDiagnosticsLog: rotation failed: {ex.Message}");
        }

        try
        {
            // Test-only: lets a test deterministically exercise the "reopen after rotation fails"
            // degraded path (M-1) without needing to race real file-system locking. Null (the
            // production path) skips straight through to the real OpenAppend call below.
            if (_testForceReopenFailure?.Invoke() == true)
                throw new IOException("Test-injected: simulated failure reopening the diagnostics log file after rotation (see testForceReopenFailure hook).");

            // Best effort: reopen a fresh append-mode writer at the same base path even if the
            // rotation itself failed above, so a single rotation hiccup doesn't silently stop
            // diagnostics for the rest of the session.
            return OpenAppend(_currentFilePath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"JsonLinesDiagnosticsLog: failed to reopen log file after rotation: {ex.Message}");
            return (null, 0L);
        }
    }
}
