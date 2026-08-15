using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Services;

/// <summary>
/// Manages the lifecycle, persistence, and desktop embedding of the system icons panel.
/// </summary>
public interface ISystemIconsPanelManager
{
    /// <summary>
    /// The live model shared between the panel window and the settings UI.
    /// </summary>
    SystemIconsPanelModel Model { get; }

    /// <summary>
    /// Deserialises the panel state from <see cref="IAppSettings.SystemIconsPanel"/>.
    /// Falls back to built-in defaults when the stored JSON is absent or malformed.
    /// </summary>
    void Load();

    /// <summary>
    /// Serialises <see cref="Model"/> into <see cref="IAppSettings.SystemIconsPanel"/> and calls
    /// <see cref="IAppSettings.Save"/>.
    /// </summary>
    void Save();

    /// <summary>
    /// Creates or closes the <c>SystemIconsPanelWindow</c> according to <see cref="SystemIconsPanelModel.IsEnabled"/>.
    /// Must be called on the UI thread.
    /// </summary>
    void Apply();

    /// <summary>
    /// Applies a change of <see cref="SystemIconsPanelModel.ShowOnAllVirtualDesktops"/> alone.
    /// </summary>
    /// <remarks>
    /// That setting only governs the virtual desktop poll timer / anchor placement, so it must not
    /// go through <see cref="Apply"/>: recreating the panel window for it would produce a visible
    /// flicker. When no panel window exists this falls back to <see cref="Apply"/>. Must be called
    /// on the UI thread.
    /// </remarks>
    void ApplyVirtualDesktopSetting();
}
