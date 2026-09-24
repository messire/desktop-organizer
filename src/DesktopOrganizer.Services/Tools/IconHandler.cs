using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
/// <remarks>
/// This class uses native methods that are not CLS-compliant.
/// </remarks>
internal class IconHandler(INativeApi nativeApi, IDiagnosticsLog diagnosticsLog) : IIconHandler
{
    /// <inheritdoc />
    public IntPtr GetIconHandle(string filePath, ref ShellFileInfo fileInfo, FileAttribute fileAttributes, FileFlag iconFlags)
    {
        var iconHandle = nativeApi.GetFileInfo(filePath, (uint)fileAttributes, ref fileInfo, (uint)Marshal.SizeOf(fileInfo), (uint)iconFlags);
        if (iconHandle == IntPtr.Zero && diagnosticsLog.IsEnabled)
        {
            // Privacy invariant (docs/Refactoring/Testing/diagnostics-log-events.md): never the
            // file path itself — only its extension (bounded, sanitized) and the closed-set
            // attribute flag actually passed to the shell.
            diagnosticsLog.Write(
                IconDiagnostics.Category,
                "IconHandleFailed",
                null,
                new KeyValuePair<string, object?>("extension", IconDiagnostics.SafeExtension(filePath)),
                new KeyValuePair<string, object?>("attributes", fileAttributes.ToString()));
        }

        return iconHandle;
    }

    /// <inheritdoc />
    public void DestroyIconHandle(IntPtr iconHandle)
    {
        if (iconHandle == IntPtr.Zero || nativeApi.DestroyIcon(iconHandle)) return;

        if (diagnosticsLog.IsEnabled)
        {
            diagnosticsLog.Write(IconDiagnostics.Category, "IconDestroyFailed", null);
        }
    }
}
