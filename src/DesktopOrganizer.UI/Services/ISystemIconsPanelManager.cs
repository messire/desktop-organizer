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
    /// Applies the persisted system icons panel state (ADR-024 C6:
    /// <c>DesktopOrganizer.UI.State.IApplicationStateService.SystemIconsPanel</c>) onto <see cref="Model"/>.
    /// Falls back to built-in defaults when no state was ever persisted.
    /// </summary>
    void Load();

    /// <summary>
    /// Requests that <see cref="Model"/>'s current state be persisted as part of the shared
    /// application-state document (ADR-024 C6).
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
