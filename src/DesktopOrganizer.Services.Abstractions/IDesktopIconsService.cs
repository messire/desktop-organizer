namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Hides or shows the real desktop icons (the shell <c>SHELLDLL_DefView</c> layer).
/// </summary>
public interface IDesktopIconsService
{
    /// <summary>
    /// Whether the desktop icons are currently visible.
    /// </summary>
    bool AreIconsVisible { get; }

    /// <summary>
    /// Shows or hides the desktop icons.
    /// </summary>
    /// <param name="visible"><see langword="true"/> to show, <see langword="false"/> to hide.</param>
    void SetIconsVisible(bool visible);

    /// <summary>
    /// Toggles desktop icon visibility.
    /// </summary>
    void Toggle();
}
