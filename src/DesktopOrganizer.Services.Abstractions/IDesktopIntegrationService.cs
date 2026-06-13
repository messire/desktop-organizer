namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Embeds a window into the desktop layer (below regular app windows, above the wallpaper).
/// Uses the Progman/WorkerW technique.
/// </summary>
public interface IDesktopIntegrationService
{
    /// <summary>
    /// Embeds the window identified by <paramref name="hwnd"/> into the desktop layer
    /// and removes it from the taskbar and Alt+Tab.
    /// </summary>
    /// <param name="hwnd">Handle of the window to embed.</param>
    void EmbedInDesktop(IntPtr hwnd);
}
