using DesktopOrganizer.Services.Enums;

namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Decides, without any network I/O, whether a path's own root — or, for a <c>.lnk</c>, its target's root
/// — is a network share, so <c>StaIconLoadQueue</c> can route the job to its Remote lane (ADR-046 D-2)
/// before touching it.
/// </summary>
internal interface IRemotePathClassifier
{
    /// <summary>
    /// Classifies <paramref name="path"/>'s own root. Pure string work plus a cached
    /// <c>GetDriveTypeW</c> lookup per drive letter (TTL 60 s). The lookup itself may block on the network for
    /// a disconnected mapped letter (R7-14), so it runs off the caller's thread and the caller waits for it
    /// only for a short bound; a letter whose lookup has not answered in time is classified remote. Safe from
    /// any thread.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    /// <returns>
    /// <c>"\\server\share"</c> for a UNC path (including <c>\\?\UNC\...</c>); <c>"Z:"</c> for a drive letter
    /// NOT known to be local — <see cref="DriveKind.Remote"/>, <see cref="DriveKind.NoRootDirectory"/>,
    /// <see cref="DriveKind.Unknown"/>, or a lookup still pending past the bound; <see langword="null"/> for a
    /// local drive (<see cref="DriveKind.Fixed"/>, <see cref="DriveKind.Removable"/>,
    /// <see cref="DriveKind.CdRom"/>, <see cref="DriveKind.RamDisk"/>) or a path with no drive/UNC root. A
    /// mapped letter and the UNC path it maps to yield two different keys (deliberate, review L5).
    /// </returns>
    string? GetOwnRemoteRoot(string path);

    /// <summary>
    /// For an existing <c>.lnk</c> file, classifies its RAW stored target — the exact bytes
    /// <c>IShellLinkW::GetPath(SLGP_RAWPATH)</c> returns after <c>IPersistFile::Load(STGM_READ)</c>, with
    /// environment variables expanded, WITHOUT resolving the shortcut (no network, no shell "fix broken
    /// shortcut" prompt). Must run on the icon worker's own STA thread (COM).
    /// </summary>
    /// <param name="shortcutPath">Full path of an existing <c>.lnk</c> file.</param>
    /// <returns>
    /// <see cref="GetOwnRemoteRoot"/> of the raw target, or <see langword="null"/> when the target is
    /// local, unknown, or the link stores a PIDL with no path at all.
    /// </returns>
    string? GetShortcutTargetRemoteRoot(string shortcutPath);
}
