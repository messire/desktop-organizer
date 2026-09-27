using System.Diagnostics;
using System.IO;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal class FileLauncher(INativeApi nativeApi) : IFileLauncher
{
    /// <inheritdoc />
    public void Open(string path)
    {
        if (!Exists(path)) return;

        TryRun(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
    }

    /// <inheritdoc />
    public ShellContextMenuResult ShowContextMenu(string path, IntPtr ownerHwnd, int screenX, int screenY, bool interceptDelete)
    {
        if (!Exists(path)) return new ShellContextMenuResult(ShellContextMenuOutcome.NotShown, null);

        try
        {
            return nativeApi.ShowShellContextMenuForPath(path, ownerHwnd, screenX, screenY, interceptDelete);
        }
        catch (Exception ex)
        {
            // INativeApi already degrades silently; this is belt-and-suspenders for the UI thread.
            // Type name only — never ex.Message, which may embed the path.
            AppLog.Current.Warn(
                "FileLauncher",
                "ShellContextMenuFailed",
                new KeyValuePair<string, object?>("exception", ex.GetType().Name),
                new KeyValuePair<string, object?>("extension", Path.GetExtension(path)));
            return new ShellContextMenuResult(ShellContextMenuOutcome.NotShown, null);
        }
    }

    private static bool Exists(string path)
        => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    /// <summary>
    /// Runs a shell action, swallowing failures (file moved, access denied, user cancelled, etc.)
    /// so a single bad item never crashes the panel.
    /// </summary>
    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Current.Error("FileLauncher", "ActionFailed", ex);
        }
    }
}
