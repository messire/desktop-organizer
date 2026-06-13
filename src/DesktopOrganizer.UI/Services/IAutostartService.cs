namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Controls whether the application launches automatically when Windows starts.
/// </summary>
public interface IAutostartService
{
    /// <summary>
    /// Gets or sets whether the application starts with Windows (per-user).
    /// </summary>
    bool IsEnabled { get; set; }
}
