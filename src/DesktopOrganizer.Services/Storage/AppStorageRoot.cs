using System.IO;

namespace DesktopOrganizer.Services.Storage;

/// <summary>
/// Single source of truth for the per-user app-data root every persistent surface lives under:
/// the versioned JSON state store (<c>State\</c>, ADR-024), moved-in panel items
/// (<c>PanelItems\</c>, ADR-009/012), the diagnostics log (<c>Logs\</c>, ADR-027) and the
/// process-lifetime storage lock (<c>.storage-lock</c>, ADR-024 D-8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Debug and Release builds use different roots</b> (ADR-024 amendment 2026-09-25). A Debug
/// build is by definition a developer run from the IDE; a Release build is what the installer ships
/// as the user's working application. Before this split both wrote to the same
/// <c>%LOCALAPPDATA%\DesktopOrganizer</c>, so every experimental panel, page and layout created
/// while developing showed up in the installed application (and vice versa), and a development run
/// could rotate away the working <c>.bak</c>. Deciding by build configuration rather than by a
/// runtime flag keeps the working application's storage unreachable from a developer build without
/// any flag to remember or forget.
/// </para>
/// <para>
/// The single-instance guard (ADR-023) is deliberately <em>not</em> split by root: it stays one
/// instance per user regardless of build configuration, so a developer build never runs alongside
/// the installed application and both never race for the desktop.
/// </para>
/// </remarks>
public static class AppStorageRoot
{
    /// <summary>
    /// Folder name (under <c>%LOCALAPPDATA%</c>) a Release build — i.e. the installed, working
    /// application — stores everything in. The installer's <c>[UninstallDelete]</c> section does
    /// <b>not</b> touch this folder — it only ever removes <c>{app}</c> (the program files) — so
    /// <c>State\</c>/<c>Logs\</c> here are always left in place across an uninstall, and
    /// <c>PanelItems\</c> is instead emptied out beforehand by the <c>--restore-panel-items</c>
    /// command-line switch (see <c>PanelItemsUninstallRestorer</c>, wired into <c>[UninstallRun]</c>)
    /// rather than deleted wholesale, since files under it were moved there, not copied. See the
    /// README's own "Uninstall" section.
    /// </summary>
    public const string ReleaseFolderName = "DesktopOrganizer";

    /// <summary>
    /// Folder name (under <c>%LOCALAPPDATA%</c>) a Debug build — a developer run — stores
    /// everything in. Isolated from <see cref="ReleaseFolderName"/> so development never touches the
    /// working application's data.
    /// </summary>
    public const string DebugFolderName = "DesktopOrganizer.Dev";

#if DEBUG
    /// <summary>Folder name in effect for this build (Debug ⇒ <see cref="DebugFolderName"/>).</summary>
    public const string FolderName = DebugFolderName;
#else
    /// <summary>Folder name in effect for this build (Release ⇒ <see cref="ReleaseFolderName"/>).</summary>
    public const string FolderName = ReleaseFolderName;
#endif

    /// <summary>
    /// Gets the absolute storage root for this build: <c>%LOCALAPPDATA%\</c><see cref="FolderName"/>.
    /// Tests never use this — every store has an internal root-override constructor pointing at a
    /// disposable temp folder.
    /// </summary>
    public static string Default =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName);
}
