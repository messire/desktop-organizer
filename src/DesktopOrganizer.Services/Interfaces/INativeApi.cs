namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Utilities provided access to native functions via P/Invoke.
/// </summary>
internal interface INativeApi
{
    /// <summary>
    /// Retrieves file information, including its icon, using Shell API.
    /// </summary>
    /// <param name="filePath">Path to file or folder.</param>
    /// <param name="fileAttributes">File attributes.</param>
    /// <param name="fileInfo">Structure to receive file information.</param>
    /// <param name="fileInfoSize">Size of the <see cref="ShellFileInfo"/> structure.</param>
    /// <param name="flags">Flags that specify file information to retrieve.</param>
    /// <returns>Handle to the icon if successful; otherwise, <see cref="IntPtr.Zero"/>.</returns>
    IntPtr GetFileInfo(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    /// <summary>
    /// Destroys icon and releases its resources.
    /// </summary>
    /// <param name="iconHandle">Handle to icon to be destroyed.</param>
    /// <returns>
    /// <see langword="true"/> if icon was successfully destroyed; otherwise, <see langword="false"/>.
    /// </returns>
    bool DestroyIcon(IntPtr iconHandle);
}
