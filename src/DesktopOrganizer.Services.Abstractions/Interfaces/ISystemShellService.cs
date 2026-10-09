using System.Windows.Media;
using DesktopOrganizer.Core;

namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Facade over native shell operations for well-known system items
/// (This PC, Control Panel, Recycle Bin, Network, User folder).
/// All operations degrade gracefully: a broken/unavailable item yields a
/// <see langword="null"/> icon or a no-op rather than throwing.
/// </summary>
public interface ISystemShellService
{
    /// <summary>
    /// Raised when the Recycle Bin state may have changed (polled).
    /// </summary>
    event EventHandler<RecycleBinState>? RecycleBinChanged;

    /// <summary>
    /// Returns the native shell icon for the given system item, or <see langword="null"/> on failure.
    /// </summary>
    /// <param name="kind">The system item kind.</param>
    /// <param name="sizePx">Requested icon size in pixels (small vs large is chosen from this).</param>
    ImageSource? GetSystemIcon(SystemIconKind kind, int sizePx);

    /// <summary>
    /// Opens (shell-executes) the given system item. No-op on failure.
    /// </summary>
    /// <param name="kind">The system item kind.</param>
    /// <param name="ownerHwnd">Owner window handle for the shell operation.</param>
    void OpenSystemItem(SystemIconKind kind, IntPtr ownerHwnd);

    /// <summary>
    /// Shows the native shell context menu for the given system item at the supplied screen coordinates.
    /// No-op on failure.
    /// </summary>
    /// <param name="kind">The system item kind.</param>
    /// <param name="ownerHwnd">Owner window handle (should have input focus for the menu to work).</param>
    /// <param name="screenX">Menu X position in physical screen pixels.</param>
    /// <param name="screenY">Menu Y position in physical screen pixels.</param>
    void ShowContextMenu(SystemIconKind kind, IntPtr ownerHwnd, int screenX, int screenY);

    /// <summary>
    /// Queries the Recycle Bin state across all drives. Returns <see cref="RecycleBinState.Empty"/> on failure.
    /// </summary>
    RecycleBinState GetRecycleBinState();

    /// <summary>
    /// Moves the given file/folder paths to the Recycle Bin (allow-undo). An item that cannot go to the Recycle Bin
    /// (too large, drive without one) is never deleted permanently without the shell asking first
    /// (<c>FOF_WANTNUKEWARNING</c>, ADR-035). Returns <see langword="false"/> on failure or when the user declines.
    /// </summary>
    /// <param name="paths">Full paths to delete.</param>
    /// <param name="ownerHwnd">Owner window handle for any confirmation UI.</param>
    bool SendToRecycleBin(IReadOnlyList<string> paths, IntPtr ownerHwnd);
}
