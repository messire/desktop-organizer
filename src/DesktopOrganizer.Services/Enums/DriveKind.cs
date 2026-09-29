namespace DesktopOrganizer.Services.Enums;

/// <summary>
/// Managed mirror of the <c>DRIVE_*</c> constants <c>GetDriveTypeW</c> returns (ADR-046,
/// <see cref="DesktopOrganizer.Services.Interfaces.IRemotePathClassifier"/>). <see cref="Fixed"/>,
/// <see cref="Removable"/>, <see cref="CdRom"/> and <see cref="RamDisk"/> keep a job on the Local lane; every
/// other kind routes it to the Remote lane (R7-14).
/// </summary>
internal enum DriveKind
{
    /// <summary>The drive type could not be determined.</summary>
    Unknown,

    /// <summary>The root path does not exist (no such drive letter).</summary>
    NoRootDirectory,

    /// <summary>A removable drive (e.g. a USB flash drive).</summary>
    Removable,

    /// <summary>A fixed local disk.</summary>
    Fixed,

    /// <summary>A mapped network drive.</summary>
    Remote,

    /// <summary>A CD/DVD-ROM drive.</summary>
    CdRom,

    /// <summary>A RAM disk.</summary>
    RamDisk,
}
