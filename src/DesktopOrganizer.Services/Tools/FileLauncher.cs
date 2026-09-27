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
    public void OpenContainingFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (File.Exists(path))
            TryRun(() => Process.Start("explorer.exe", $"/select,\"{path}\""));
        else if (Directory.Exists(path))
            TryRun(() => Process.Start("explorer.exe", $"\"{path}\""));
    }

    /// <inheritdoc />
    public void ShowProperties(string path)
    {
        if (!Exists(path)) return;

        TryRun(() => nativeApi.ShowFileProperties(path));
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
