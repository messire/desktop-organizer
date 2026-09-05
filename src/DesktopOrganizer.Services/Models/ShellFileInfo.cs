namespace DesktopOrganizer.Services.Models;

/// <summary>
/// Contains information about file object returned by Shell API.
/// </summary>
// Explicitly Unicode: every consumer binds the "W" entry points (SHGetFileInfoW), and the fixed
// char buffers below are UTF-16 by definition. CharSet.Auto happens to resolve to Unicode on
// net8.0-windows, but stating it removes the implicit dependency on that resolution.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct ShellFileInfo
{
    /// <summary>
    /// Handle to icon representing file.
    /// </summary>
    public IntPtr IconHandle;

    /// <summary>
    /// Index of icon image within system image list.
    /// </summary>
    public int IconIndex;

    /// <summary>
    /// Attributes of file object.
    /// </summary>
    public uint Attributes;

    /// <summary>
    /// Display name of file or folder as null-terminated string.
    /// </summary>
    public fixed char DisplayName[260];

    /// <summary>
    /// Type name of file or folder as null-terminated string.
    /// </summary>
    public fixed char TypeName[80];
}
