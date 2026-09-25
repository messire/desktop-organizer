using System.Windows.Media;
using DesktopOrganizer.Core;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Interfaces;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Wraps native shell operations for well-known system items.
/// Native P/Invoke is delegated to <see cref="INativeApi"/>; all public methods
/// degrade gracefully on failure (null / false / no-op).
/// </summary>
internal sealed class SystemShellService : ISystemShellService, IDisposable
{
    private const int RecycleBinPollIntervalMs = 3000;

    private readonly INativeApi _nativeApi;
    private readonly Dictionary<(SystemIconKind Kind, int SizePx, bool RecycleBinFull), ImageSource> _iconCache = new();
    private readonly System.Threading.Timer _recycleBinTimer;

    private RecycleBinState _lastRecycleBinState;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<RecycleBinState>? RecycleBinChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemShellService"/> class.
    /// </summary>
    /// <param name="nativeApi">The native P/Invoke facade this service delegates shell operations to.</param>
    public SystemShellService(INativeApi nativeApi)
    {
        _nativeApi = nativeApi;
        _lastRecycleBinState = GetRecycleBinState();
        _recycleBinTimer = new System.Threading.Timer(PollRecycleBin, null, RecycleBinPollIntervalMs, RecycleBinPollIntervalMs);
    }

    /// <inheritdoc />
    public ImageSource? GetSystemIcon(SystemIconKind kind, int sizePx)
    {
        // The Recycle Bin has two distinct shell icons (empty vs full). Its current
        // fullness is part of the cache key so a state change yields the correct icon
        // instead of returning a stale cached one. Other kinds always use false.
        var recycleBinFull = kind == SystemIconKind.RecycleBin && !GetRecycleBinState().IsEmpty;
        var key = (kind, sizePx, recycleBinFull);
        if (_iconCache.TryGetValue(key, out var cached))
            return cached;

        var pidl = IntPtr.Zero;
        try
        {
            var descriptor = SystemIconDescriptor.Table[kind];
            pidl = _nativeApi.GetKnownFolderPidl(descriptor.KnownFolderId);
            if (pidl == IntPtr.Zero) return null;

            var icon = _nativeApi.GetShellIconByPidl(pidl, sizePx);
            if (icon is not null)
                _iconCache[key] = icon;
            return icon;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                _nativeApi.FreePidl(pidl);
        }
    }

    /// <inheritdoc />
    public void OpenSystemItem(SystemIconKind kind, IntPtr ownerHwnd)
    {
        var pidl = IntPtr.Zero;
        try
        {
            var descriptor = SystemIconDescriptor.Table[kind];
            pidl = _nativeApi.GetKnownFolderPidl(descriptor.KnownFolderId);
            if (pidl == IntPtr.Zero) return;
            _nativeApi.ShellExecutePidl(pidl, ownerHwnd);
        }
        catch { /* degrade silently */ }
        finally
        {
            if (pidl != IntPtr.Zero)
                _nativeApi.FreePidl(pidl);
        }
    }

    /// <inheritdoc />
    public void ShowContextMenu(SystemIconKind kind, IntPtr ownerHwnd, int screenX, int screenY)
    {
        var pidl = IntPtr.Zero;
        try
        {
            var descriptor = SystemIconDescriptor.Table[kind];
            pidl = _nativeApi.GetKnownFolderPidl(descriptor.KnownFolderId);
            if (pidl == IntPtr.Zero) return;
            _nativeApi.ShowShellContextMenu(pidl, ownerHwnd, screenX, screenY);
        }
        catch { /* degrade silently */ }
        finally
        {
            if (pidl != IntPtr.Zero)
                _nativeApi.FreePidl(pidl);
        }
    }

    /// <inheritdoc />
    public RecycleBinState GetRecycleBinState()
    {
        try
        {
            var native = _nativeApi.QueryRecycleBin();
            return new RecycleBinState(native.ItemCount == 0, native.ItemCount, native.TotalSize);
        }
        catch
        {
            return RecycleBinState.Empty;
        }
    }

    /// <inheritdoc />
    public bool SendToRecycleBin(IReadOnlyList<string> paths, IntPtr ownerHwnd)
    {
        if (paths.Count == 0) return true;
        try
        {
            var ok = _nativeApi.SendToRecycleBin([.. paths], ownerHwnd);
            if (ok)
            {
                // Reflect the change promptly without waiting for the next poll tick.
                PollRecycleBin(null);
            }

            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose(WaitHandle) blocks until any in-flight PollRecycleBin callback has finished,
        // so it can't run concurrently with (or after) _iconCache.Clear() below. Bounded: a poll
        // in the middle of a slow SHQueryRecycleBin call must not hang app shutdown indefinitely.
        using var waitHandle = new ManualResetEvent(false);
        if (_recycleBinTimer.Dispose(waitHandle))
            waitHandle.WaitOne(TimeSpan.FromSeconds(2));

        _iconCache.Clear();
    }

    /// <summary>
    /// Polls the Recycle Bin and raises <see cref="RecycleBinChanged"/> when the state changes.
    /// Runs on a thread-pool timer; must never throw.
    /// </summary>
    private void PollRecycleBin(object? state)
    {
        if (_disposed) return;

        try
        {
            var current = GetRecycleBinState();
            if (current != _lastRecycleBinState)
            {
                _lastRecycleBinState = current;
                RecycleBinChanged?.Invoke(this, current);
            }
        }
        catch
        {
            // Timer callback must never throw.
        }
    }
}
