namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Result of <see cref="IPanelItemsUninstallRestorer.RestoreAll"/>.
/// </summary>
/// <param name="RestoredCount">Number of managed items successfully moved out of managed storage.</param>
/// <param name="FailedCount">
/// Number of managed items that could not be moved (left untouched, still inside managed storage).
/// Zero means the run was fully successful.
/// </param>
/// <param name="Message">
/// English, human-readable summary of what happened — meant for the console/diagnostics log, never
/// localized (this command runs from the uninstaller, not an interactive user session).
/// </param>
public readonly record struct PanelItemsRestoreResult(int RestoredCount, int FailedCount, string Message)
{
    /// <summary>Gets a value indicating whether every managed item was successfully moved (<see cref="FailedCount"/> is zero).</summary>
    public bool Success => FailedCount == 0;
}

/// <summary>
/// Implements the <c>--restore-panel-items</c> command-line switch: the uninstall-time counterpart to
/// <see cref="IPanelItemStorageService"/>'s live drag&amp;drop Move semantics. Because dropped files are
/// <b>moved</b> (not copied) into <c>%LOCALAPPDATA%\DesktopOrganizer\PanelItems</c>, simply deleting that
/// folder on uninstall (which the installer never did in the first place — see the type this replaces
/// stranding user files) would destroy files the user never asked to delete. This command instead
/// returns every managed item to the user's desktop before the folder is ever removed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per item, in order of preference:</b> (1) its original location, if the recorded original
/// directory still exists, is on a fixed local drive, and nothing already occupies that exact path;
/// otherwise (2) the current user's desktop (<see cref="Environment.SpecialFolder.DesktopDirectory"/>),
/// under a collision-free Explorer-style name. Items physically present under managed storage but
/// missing from the manifest (e.g. a manifest write that failed mid-way) are treated the same as
/// case (2) — they have no known original location, so they always go to the desktop.
/// </para>
/// <para>
/// <b>Never loses data.</b> Cross-volume moves copy first, verify the copy, and only then delete the
/// source — the same discipline <see cref="IPanelItemStorageService"/> itself uses. A per-item failure
/// (locked file, permissions, disk full) is logged and skipped; it never aborts the rest of the run,
/// and the failed item is simply left in managed storage rather than lost.
/// </para>
/// <para>
/// <b>Cleanup only on full success.</b> The <c>PanelItems</c> folder and its manifest are deleted only
/// when every item was successfully moved (<see cref="PanelItemsRestoreResult.Success"/>). This command
/// never touches <c>State\</c>, <c>Logs\</c>, or <c>application-state.json</c> — see CLAUDE.md's frozen
/// persistence contour.
/// </para>
/// <para>
/// Standalone, like <see cref="DesktopOrganizer.Services.Abstractions.Storage.IStateRestoreCommand"/>: invoked once, from <c>Program.Main</c>, in
/// response to the <c>--restore-panel-items</c> command-line switch (wired into the installer's
/// <c>[UninstallRun]</c>, run while the app is already closed and before <c>{app}</c>'s files are
/// removed) — never part of the normal application startup path.
/// </para>
/// </remarks>
public interface IPanelItemsUninstallRestorer
{
    /// <summary>
    /// Attempts the recovery described in the type remarks. Never throws — every per-item failure is
    /// folded into <see cref="PanelItemsRestoreResult.FailedCount"/> instead.
    /// </summary>
    PanelItemsRestoreResult RestoreAll();
}
