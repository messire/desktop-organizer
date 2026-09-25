using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopOrganizer.Tests.Resources;

/// <summary>
/// Creates real Windows shortcut (<c>.lnk</c>) files for icon-extraction integration tests, via
/// late-bound <c>WScript.Shell</c> COM automation — the same mechanism the P00 baseline audit's icon
/// experiment used (there via PowerShell) to build realistic <c>.lnk</c> fixtures. Reflection-based
/// (<see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/>) rather than
/// the C# <c>dynamic</c> keyword specifically to avoid adding a <c>Microsoft.CSharp</c> package
/// reference to the test project for a single helper. This is scripting automation against a
/// pre-existing Windows shell object, not new P/Invoke surface — <c>NativeApi</c>/<c>INativeApi</c>
/// are untouched.
/// </summary>
internal static class ShellLinkTestHelper
{
    /// <summary>
    /// Creates a <c>.lnk</c> at <paramref name="shortcutPath"/> pointing at <paramref name="targetPath"/>.
    /// <paramref name="targetPath"/> is written as-is and is <b>not</b> required to exist — this is
    /// what lets callers build a "shortcut with a missing target" fixture (see
    /// <c>IconExtractionIntegrationTests</c>'s broken-target case).
    /// </summary>
    /// <param name="shortcutPath">The full path, including <c>.lnk</c> extension, of the shortcut file to create.</param>
    /// <param name="targetPath">The path the shortcut should point at; not validated or required to exist.</param>
    public static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM automation is unavailable in this environment.");

        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Failed to instantiate WScript.Shell.");

        try
        {
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath])
                ?? throw new InvalidOperationException("WScript.Shell.CreateShortcut returned null.");

            var shortcutType = shortcut.GetType();
            try
            {
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }
}
