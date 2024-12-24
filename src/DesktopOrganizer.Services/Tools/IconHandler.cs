namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
/// <remarks>
/// This class uses native methods that are not CLS-compliant.
/// </remarks>
internal class IconHandler(INativeApi nativeApi) : IIconHandler
{
    /// <inheritdoc />
    public IntPtr GetIconHandle(string filePath, ref ShellFileInfo fileInfo, FileAttribute fileAttributes, FileFlag iconFlags)
    {
        var iconHandle = nativeApi.GetFileInfo(filePath, (uint)fileAttributes, ref fileInfo, (uint)Marshal.SizeOf(fileInfo), (uint)iconFlags);
        if (iconHandle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Failed to retrieve icon handle for: {filePath}");
        }

        return iconHandle;
    }

    /// <inheritdoc />
    public void DestroyIconHandle(IntPtr iconHandle)
    {
        if (iconHandle == IntPtr.Zero || nativeApi.DestroyIcon(iconHandle)) return;
        Console.Error.WriteLine($"Failed to destroy icon handle: {iconHandle}");
    }
}
