namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Launches files and folders through the Windows shell.
/// </summary>
public interface IFileLauncher
{
    /// <summary>
    /// Opens the file or folder with its default shell action (double-click behaviour).
    /// </summary>
    /// <param name="path">Full path of the file or folder.</param>
    void Open(string path);

    /// <summary>
    /// Shows the native Windows shell context menu for the file or folder — the same menu Explorer shows
    /// for it (including "Open with", "Send to" and third-party shell extensions) — at the given screen
    /// position, and runs the command the user picks. Holding Shift adds the shell's extended verbs.
    /// </summary>
    /// <remarks>
    /// Must be called on the UI (STA) thread; blocks in the menu's modal loop until the menu closes and the
    /// chosen command has been handed to the shell. Never throws — every failure degrades to
    /// <see cref="ShellContextMenuOutcome.NotShown"/>. The shell's "Rename" is never invoked (reported as
    /// <see cref="ShellContextMenuOutcome.RenameRequested"/>); its "Delete" is reported as
    /// <see cref="ShellContextMenuOutcome.DeleteRequested"/> instead of being invoked when
    /// <paramref name="interceptDelete"/> is set.
    /// </remarks>
    /// <param name="path">Full path of an existing file or folder.</param>
    /// <param name="ownerHwnd">The window the menu is opened from (fallback owner for the menu).</param>
    /// <param name="screenX">Horizontal screen coordinate of the menu, in physical pixels.</param>
    /// <param name="screenY">Vertical screen coordinate of the menu, in physical pixels.</param>
    /// <param name="interceptDelete">
    /// <see langword="true"/> for an app-managed item: the caller handles "Delete" itself (see
    /// <see cref="IPanelItemStorageService.RecycleManagedItem"/>). <see langword="false"/> lets the shell delete
    /// the item as Explorer would.
    /// </param>
    /// <returns>How the menu ended, plus the invoked command's canonical verb; see <see cref="ShellContextMenuResult"/>.</returns>
    ShellContextMenuResult ShowContextMenu(string path, IntPtr ownerHwnd, int screenX, int screenY, bool interceptDelete);
}
