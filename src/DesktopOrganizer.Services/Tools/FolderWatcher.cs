using System.IO;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Wraps <see cref="FileSystemWatcher"/> to monitor a single folder for
/// file-name and directory-name changes with debouncing and service-file filtering.
/// On buffer overflow or watcher error, raises <see cref="Changed"/> with
/// <see cref="FolderChangeKind.ResyncRequired"/> so consumers can perform a full resync.
/// </summary>
internal sealed class FolderWatcher : IFolderWatcher
{
    /// <summary>Debounce window: the watcher waits this long after the last raw FSW event before firing.</summary>
    private const int DebounceMs = 300;

    /// <summary>Service files to ignore unconditionally (case-insensitive).</summary>
    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini",
        "thumbs.db",
    };

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private FolderChangeEventArgs? _pendingArgs;
    private readonly object _debounceLock = new();
    private bool _disposed;

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
        lock (_debounceLock)
        {
            if (_disposed) return;

            _pendingArgs = args;

            if (args.Kind == FolderChangeKind.ResyncRequired)
            {
                // Fire immediately without debounce so the panel can recover ASAP.
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                FirePending();
                return;
            }

            if (_debounceTimer is null)
            {
                _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, DebounceMs, Timeout.Infinite);
            }
            else
            {
                _debounceTimer.Change(DebounceMs, Timeout.Infinite);
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            FirePending();
        }
    }

    private void FirePending()
    {
        var args = _pendingArgs;
        _pendingArgs = null;
        if (args is not null)
            Changed?.Invoke(this, args);
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
