using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Wraps <see cref="FileSystemWatcher"/> to monitor a single folder for
/// file-name and directory-name changes with debouncing and service-file filtering.
/// On buffer overflow or watcher error, raises <see cref="Changed"/> with
/// <see cref="FolderChangeKind.ResyncRequired"/> so consumers can perform a full resync.
/// </summary>
internal sealed class FolderWatcher : IFolderWatcher
{
    /// <summary>The <c>category</c> value for this type's diagnostics events.</summary>
    private const string DiagnosticsCategory = "folderWatcher";

    /// <summary>Debounce window: the watcher waits this long after the last raw FSW event before firing.</summary>
    private const int DebounceMs = 300;

    /// <summary>Service files to ignore unconditionally (case-insensitive).</summary>
    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "thumbs.db",
    };

    private readonly IDiagnosticsLog _diagnosticsLog;
    private readonly object _debounceLock = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private FolderChangeEventArgs? _pendingArgs;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderWatcher"/> class without diagnostics logging
    /// (used directly by <c>FolderWatcherTests</c> and any other caller that does not need a
    /// <see cref="IDiagnosticsLog"/> wired in).
    /// </summary>
    public FolderWatcher()
        : this(NullDiagnosticsLog.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderWatcher"/> class. Called by
    /// <see cref="FolderWatcherFactory"/>, which owns the real <see cref="IDiagnosticsLog"/> from DI.
    /// </summary>
    /// <param name="diagnosticsLog">The diagnostics sink to write watcher lifecycle/error events to.</param>
    internal FolderWatcher(IDiagnosticsLog diagnosticsLog)
    {
        _diagnosticsLog = diagnosticsLog ?? NullDiagnosticsLog.Instance;
    }

    /// <inheritdoc/>
    public event EventHandler<FolderChangeEventArgs>? Changed;

    /// <inheritdoc/>
    public void Start(string folderPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        _watcher = new FileSystemWatcher(folderPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
            IncludeSubdirectories = false,
        };

        // Subscribe before enabling raising events: otherwise an FS event landing in the gap
        // between construction and subscription is silently dropped.
        _watcher.Created += OnCreated;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;

        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingArgs = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the file at <paramref name="fullPath"/> should be
    /// ignored (service files such as desktop.ini, Thumbs.db, or hidden files).
    /// </summary>
    /// <param name="fullPath">Full path of the file or directory the change event refers to.</param>
    internal static bool ShouldIgnore(string fullPath)
    {
        var fileName = System.IO.Path.GetFileName(fullPath);
        if (IgnoredFileNames.Contains(fileName))
            return true;

        try
        {
            // Ignore hidden files/directories. For deleted entries the file no longer exists,
            // so GetAttributes will throw — in that case we allow the event through.
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.Hidden) != 0)
                return true;
        }
        catch (Exception)
        {
            // File already gone (deleted) — don't suppress the Deleted event.
        }

        return false;
    }

    /// <summary>
    /// Schedules (or resets) the debounce timer. The last <paramref name="args"/> wins;
    /// for ResyncRequired the timer fires immediately so that the consumer can recover.
    /// </summary>
    private void Schedule(FolderChangeEventArgs args)
    {
        FolderChangeEventArgs? toFireImmediately = null;

        lock (_debounceLock)
        {
            if (_disposed) return;

            _pendingArgs = args;

            if (args.Kind == FolderChangeKind.ResyncRequired)
            {
                // Fire immediately without debounce so the panel can recover ASAP. Captured here and
                // raised after the lock is released below (see RaiseChanged's remarks) rather than
                // inline — this method runs on the FileSystemWatcher's own callback thread, and a
                // subscriber invoked while still holding _debounceLock could deadlock against a
                // concurrent Stop()/Dispose() call on the UI thread.
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                toFireImmediately = _pendingArgs;
                _pendingArgs = null;
            }
            else if (_debounceTimer is null)
            {
                _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, DebounceMs, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(DebounceMs, Timeout.Infinite);
            }
        }

        if (toFireImmediately is not null)
            RaiseChanged(toFireImmediately);
    }

    private void OnDebounceElapsed(object? state)
    {
        FolderChangeEventArgs? args;

        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            args = _pendingArgs;
            _pendingArgs = null;
        }

        if (args is not null)
            RaiseChanged(args);
    }

    /// <summary>
    /// Invokes every <see cref="Changed"/> subscriber, individually and outside <see cref="_debounceLock"/>.
    /// </summary>
    /// <remarks>
    /// Two hazards this closes (docs/Refactoring/Testing/sta-test-conventions.md, "known remaining
    /// hazards" → now fixed here):
    /// <list type="bullet">
    /// <item>
    /// <b>Deadlock.</b> This method (via <see cref="OnDebounceElapsed"/>) used to run entirely inside
    /// <c>lock (_debounceLock)</c>. The only production subscriber
    /// (<c>ChildWindowViewModel.OnFolderChanged</c>) marshals onto the UI thread via
    /// <c>UiDispatch.TryInvoke</c>, which blocks the calling (thread-pool timer) thread until the UI
    /// thread services it. If the UI thread concurrently called <see cref="Stop"/>/<see cref="Dispose"/>
    /// — which also takes <see cref="_debounceLock"/> — the UI thread would block on the lock while the
    /// timer thread blocked waiting for the UI thread's dispatcher to pump: classic deadlock. Firing
    /// outside the lock means <see cref="Stop"/> can always take <see cref="_debounceLock"/> immediately;
    /// the pending args have already been captured into a local by that point regardless.
    /// </item>
    /// <item>
    /// <b>Unhandled exception on a thread-pool thread.</b> A throwing subscriber previously escaped
    /// <see cref="OnDebounceElapsed"/> uncaught — on the <see cref="System.Threading.Timer"/> callback
    /// thread that is an unhandled exception with no <c>Dispatcher.UnhandledException</c> equivalent to
    /// catch it, and .NET terminates the whole process. Each subscriber now runs in its own
    /// <c>try</c>/<c>catch</c>: one subscriber's bug is recorded to <see cref="IDiagnosticsLog"/> and
    /// neither stops delivery to any other subscriber nor stops this watcher — the next debounced event
    /// is scheduled and delivered normally. This intentionally catches every <see cref="Exception"/>,
    /// including <see cref="OperationCanceledException"/>: <c>UiDispatch.TryInvoke</c> already swallows
    /// the one <see cref="OperationCanceledException"/> its own contract documents (a race with
    /// dispatcher shutdown) and never lets it propagate here, so this catch only ever sees a genuine bug
    /// in the subscriber's own code — never UiDispatch's shutdown-race semantics.
    /// </item>
    /// </list>
    /// </remarks>
    private void RaiseChanged(FolderChangeEventArgs args)
    {
        var handler = Changed;
        if (handler is null) return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<FolderChangeEventArgs>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                if (_diagnosticsLog.IsEnabled)
                {
                    _diagnosticsLog.Write(
                        DiagnosticsCategory,
                        "SubscriberThrew",
                        null,
                        new KeyValuePair<string, object?>("errorType", ex.GetType().Name));
                }
            }
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        Schedule(new FolderChangeEventArgs(FolderChangeKind.Created, e.FullPath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        Schedule(new FolderChangeEventArgs(FolderChangeKind.Deleted, e.FullPath));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        Schedule(new FolderChangeEventArgs(FolderChangeKind.Renamed, e.FullPath, e.OldFullPath));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or OS-level watcher error — signal consumers to resync.
        Schedule(new FolderChangeEventArgs(FolderChangeKind.ResyncRequired, _watcher?.Path ?? string.Empty));
    }
}
