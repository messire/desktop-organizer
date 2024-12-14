namespace DesktopOrganizer.Services.Models;

/// <summary>
/// Contains information about file object returned by Shell API.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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
