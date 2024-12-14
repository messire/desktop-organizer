namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Handles native icon operations, including retrieving and destroying icon handles.
/// </summary>
internal interface IIconHandler
{
    /// <summary>
    /// Retrieves the icon handle for the specified file path using Shell API.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <param name="fileInfo">The structure to receive file information.</param>
    /// <param name="fileAttributes">File attributes.</param>
    /// <param name="iconFlags">The icon flags.</param>
    /// <returns>A handle to the icon, or <see cref="IntPtr.Zero"/> if retrieval fails.</returns>
    IntPtr GetIconHandle(string filePath, ref ShellFileInfo fileInfo, FileAttribute fileAttributes, FileFlag iconFlags);

    /// <summary>
    /// Safely destroys the icon handle to release resources.
    /// </summary>
    /// <param name="iconHandle">The handle to the icon to be destroyed.</param>
    void DestroyIconHandle(IntPtr iconHandle);
}
