using System.Runtime.InteropServices;

namespace DesktopOrganizer.Tests.Resources;

public static partial class User32
{
    public const int IDI_APPLICATION = 0x7F00;

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    public static partial IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);
}
