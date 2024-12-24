namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal partial class NativeApi: INativeApi
{
    /// <inheritdoc />
    public IntPtr GetFileInfo(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags)
        => NativeMethods.ShGetFileInfo(filePath, fileAttributes, ref fileInfo, fileInfoSize, flags);

    /// <inheritdoc />
    public bool DestroyIcon(IntPtr iconHandle)
        => NativeMethods.DestroyIconByHandle(iconHandle);

    private static partial class NativeMethods
    {
        [LibraryImport("Shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr ShGetFileInfo(string filePath, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

        [LibraryImport("User32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyIconByHandle(IntPtr iconHandle);
    }
}
