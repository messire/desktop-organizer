using System.IO;
using DesktopOrganizer.Services.Tools;

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
/// <b>Two distribution channels, two parents</b> (Microsoft Store channel, decision D-1=A). A process
/// with package identity (MSIX / Microsoft Store) keeps the whole root under
/// <c>%USERPROFILE%\DesktopOrganizer</c> (<c>.Dev</c> for Debug) instead of <c>%LOCALAPPDATA%</c>: a
/// packaged app's <c>%LOCALAPPDATA%</c> is virtualised into the package and is wiped on uninstall or
/// "Reset", which would destroy the files users moved into <c>PanelItems\</c>. The Inno channel (no
/// identity) is unchanged. Only the <em>parent folder</em> differs: the layout beneath the root, the
/// file formats and the schema are identical in both channels, and a given install only ever uses one
/// of them. If an identity-carrying process cannot resolve <c>%USERPROFILE%</c> (empty), the root
/// degrades to the <c>%LOCALAPPDATA%</c> location rather than a root with an empty prefix.
/// </para>
/// <para>
/// <b>Documented exception to "P/Invoke only behind <c>INativeApi</c>".</b> The identity probe
/// (<c>GetCurrentPackageFullName</c>) is declared in <c>NativeApi.cs</c> like every other native call, but
/// its cached result is read statically (<c>NativeApi.HasPackageIdentity</c>) from exactly one place
/// outside that file — this class — because the root is needed before DI exists
/// (<c>Program.Main</c>'s crash/restore paths, <c>App.CrashLogDirectory</c>, the default constructors of
/// the stores). For the same reason this class also exposes the raw probe code
/// (<see cref="PackageProbeResultCode"/>) as a pre-DI facade for the one diagnostic log line. Everything
/// else asks <c>IAppPackageContext</c>. <c>PackageProbeUsageGuardTests</c> pins these facts.
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
    /// Folder name a Release build — i.e. the installed, working application — stores everything in
    /// (under <c>%LOCALAPPDATA%</c> for the Inno channel, under <c>%USERPROFILE%</c> for the Store
    /// channel). The installer's <c>[UninstallDelete]</c> section does
    /// <b>not</b> touch this folder — it only ever removes <c>{app}</c> (the program files) — so
    /// <c>State\</c>/<c>Logs\</c> here are always left in place across an uninstall, and
    /// <c>PanelItems\</c> is instead emptied out beforehand by the <c>--restore-panel-items</c>
    /// command-line switch (see <c>PanelItemsUninstallRestorer</c>, wired into <c>[UninstallRun]</c>)
    /// rather than deleted wholesale, since files under it were moved there, not copied. See the
    /// README's own "Uninstall" section. A Store (MSIX) uninstall has no such hook: the folder and its
    /// <c>PanelItems\</c> are simply left in the user profile.
    /// </summary>
    public const string ReleaseFolderName = "DesktopOrganizer";

    /// <summary>
    /// Folder name a Debug build — a developer run — stores everything in (same parents as
    /// <see cref="ReleaseFolderName"/>). Isolated from <see cref="ReleaseFolderName"/> so development
    /// never touches the working application's data.
    /// </summary>
    public const string DebugFolderName = "DesktopOrganizer.Dev";

#if DEBUG
    /// <summary>Folder name in effect for this build (Debug ⇒ <see cref="DebugFolderName"/>).</summary>
    public const string FolderName = DebugFolderName;

    /// <summary>Whether this is a Debug build (selects the <c>.Dev</c> root).</summary>
    internal const bool IsDebugBuild = true;
#else
    /// <summary>Folder name in effect for this build (Release ⇒ <see cref="ReleaseFolderName"/>).</summary>
    public const string FolderName = ReleaseFolderName;

    /// <summary>Whether this is a Debug build (selects the <c>.Dev</c> root).</summary>
    internal const bool IsDebugBuild = false;
#endif

    /// <summary>Name of the managed moved-in files folder directly under the root.</summary>
    private const string PanelItemsFolderName = "PanelItems";

    /// <summary>Name of the diagnostics log folder directly under the root.</summary>
    private const string LogsFolderName = "Logs";

    private static readonly Lazy<(string Root, bool UnderUserProfile)> DefaultLocation = new(
        () => ResolveLocation(NativeApi.HasPackageIdentity, IsDebugBuild, Environment.GetFolderPath),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the absolute storage root for this process, resolved once: Inno channel
    /// <c>%LOCALAPPDATA%\</c><see cref="FolderName"/>, Store channel (package identity)
    /// <c>%USERPROFILE%\</c><see cref="FolderName"/>. Tests never use this — every store has an internal
    /// root-override constructor pointing at a disposable temp folder.
    /// </summary>
    public static string Default => DefaultLocation.Value.Root;

    /// <summary>
    /// Gets a value indicating whether <see cref="Default"/> really lives under <c>%USERPROFILE%</c> (the
    /// Store-channel location, which survives an uninstall) rather than under <c>%LOCALAPPDATA%</c>. This is
    /// <see langword="false"/> for the Inno channel <em>and</em> for a process with package identity whose
    /// <c>%USERPROFILE%</c> could not be resolved and therefore fell back to <c>%LOCALAPPDATA%</c>: that
    /// folder is virtualised into the package and is wiped on uninstall, so nothing may claim otherwise
    /// (the first-run README says the folder survives an uninstall).
    /// </summary>
    public static bool IsPackagedLocation => DefaultLocation.Value.UnderUserProfile;

    /// <summary>
    /// Gets the raw result code of the package-identity probe (<c>GetCurrentPackageFullName</c>), for the
    /// one-time diagnostic log line in <c>Program.Main</c>. A pre-DI facade over the cached probe so the UI
    /// layer never touches <c>NativeApi</c> directly; the decision itself is read through
    /// <c>IAppPackageContext</c>.
    /// </summary>
    public static int PackageProbeResultCode => NativeApi.PackageProbeResultCode;

    /// <summary>Gets the folder moved-in panel items live in: <c>PanelItems</c> directly under <see cref="Default"/>.</summary>
    public static string PanelItemsDirectory => Path.Combine(Default, PanelItemsFolderName);

    /// <summary>Gets the diagnostics log folder: <c>Logs</c> directly under <see cref="Default"/>.</summary>
    public static string LogsDirectory => Path.Combine(Default, LogsFolderName);

    /// <summary>
    /// Pure root resolution: the folder-name table (build configuration) under the parent the channel
    /// selects (identity ⇒ <c>%USERPROFILE%</c>, otherwise <c>%LOCALAPPDATA%</c>).
    /// </summary>
    /// <param name="isPackaged">Whether the process has package identity.</param>
    /// <param name="isDebug">Whether this is a Debug build (<c>.Dev</c> folder name).</param>
    /// <param name="getFolder">Special-folder lookup (<see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> in production).</param>
    /// <returns>The absolute storage root.</returns>
    internal static string Resolve(bool isPackaged, bool isDebug, Func<Environment.SpecialFolder, string> getFolder)
        => ResolveLocation(isPackaged, isDebug, getFolder).Root;

    /// <summary>
    /// Pure root resolution that also reports whether the root ended up under <c>%USERPROFILE%</c>: the
    /// packaged channel only counts as a surviving location when the profile folder was actually resolved.
    /// </summary>
    /// <param name="isPackaged">Whether the process has package identity.</param>
    /// <param name="isDebug">Whether this is a Debug build (<c>.Dev</c> folder name).</param>
    /// <param name="getFolder">Special-folder lookup (<see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> in production).</param>
    /// <returns>
    /// The absolute storage root, and <see langword="true"/> only when it lives under <c>%USERPROFILE%</c>
    /// (packaged <em>and</em> a non-empty profile folder).
    /// </returns>
    internal static (string Root, bool UnderUserProfile) ResolveLocation(bool isPackaged, bool isDebug, Func<Environment.SpecialFolder, string> getFolder)
    {
        ArgumentNullException.ThrowIfNull(getFolder);

        var folderName = isDebug ? DebugFolderName : ReleaseFolderName;
        var profile = isPackaged ? getFolder(Environment.SpecialFolder.UserProfile) : null;
        if (!string.IsNullOrWhiteSpace(profile))
            return (Path.Combine(profile, folderName), true);

        // A packaged process with no resolvable profile degrades to the Inno location: a root with an
        // empty prefix (a relative path) would be far worse than a virtualised one.
        return (Path.Combine(getFolder(Environment.SpecialFolder.LocalApplicationData), folderName), false);
    }
}
