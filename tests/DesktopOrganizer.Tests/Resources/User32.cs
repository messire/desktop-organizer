using System.Runtime.InteropServices;

namespace DesktopOrganizer.Tests.Resources;

/// <summary>
/// Provides access to user32.dll functions for testing purposes.
/// This static partial class contains methods and constants that interact with the
/// Windows API, specifically from the User32 library.
/// </summary>
public static partial class User32
{
    /// <summary>
    /// The resource identifier for the default application icon.
    /// </summary>
    public const int IdiApplication = 0x7F00;

    /// <summary>
    /// Loads an icon resource from the user32.dll library.
    /// </summary>
    /// <param name="hInstance">A handle to the instance of the module.</param>
    /// <param name="lpIconName">The identifier of the icon to load (provides predefined icons like <see cref="IdiApplication" />).</param>
    /// <returns>A handle to the icon.</returns>
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    public static partial IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

    /// <summary>
    /// Destroys an icon handle to free associated resources.
    /// </summary>
    /// <param name="hIcon">A handle to the icon to be destroyed.</param>
    /// <returns>
    /// <c>true</c> if the function succeeds; otherwise, <c>false</c>. Use <c>Marshal.GetLastWin32Error</c> to get more error information.
    /// </returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);
}
