namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Managing the application icon in the system tray.
/// </summary>
public interface ITrayIconManager : IDisposable
{
    /// <summary>
    /// Event triggered when the main application window should be shown/activated (never hidden).
    /// Raised by the tray context menu's "Settings" item, which is an explicit "open" action.
    /// </summary>
    event EventHandler? OnShowMainWindowRequested;

    /// <summary>
    /// Event triggered when the main application window's visibility should be toggled — shown if
    /// hidden, hidden if currently visible. Raised by double-clicking the tray icon.
    /// </summary>
    event EventHandler? OnToggleMainWindowVisibilityRequested;

    /// <summary>
    /// Event triggered when the application is requested to exit.
    /// </summary>
    event EventHandler? OnExitRequested;

    /// <summary>
    /// Event triggered when the checkable "Edit layout" tray menu item is clicked — the guaranteed,
    /// <c>WS_EX_NOACTIVATE</c>-independent entry point for toggling the global layout edit mode
    /// (docs/PLANS/phase-6-edit-layout-ux.md §1 D-9 #1). This project layer has no visibility into
    /// <c>ILayoutEditModeService</c> (that type lives in DesktopOrganizer.UI, above this one in the
    /// layering) — the subscriber is expected to translate this into a call to
    /// <c>ILayoutEditModeService.Toggle(LayoutModeChangeSource.Tray)</c> and refresh the menu via
    /// <see cref="UpdateContextMenu"/> afterwards so the checkmark reflects the new state.
    /// </summary>
    event EventHandler? OnEditLayoutToggleRequested;

    /// <summary>
    /// Event triggered when the checkable "Lock layout" tray menu item is clicked. The subscriber is
    /// expected to flip <c>ILayoutEditModeService.IsLayoutLocked</c> and refresh the menu via
    /// <see cref="UpdateContextMenu"/> afterwards.
    /// </summary>
    event EventHandler? OnLockLayoutToggleRequested;

    /// <summary>
    /// Initializes the system tray icon.
    /// </summary>
    void Initialize();

    /// <summary>
    /// (Re)builds the tray context menu, entirely from the state passed in — this layer never reads
    /// application-global state itself (see <see cref="OnEditLayoutToggleRequested"/>'s remarks), so
    /// every checkable item's checked/enabled state is a caller-supplied snapshot. Callers are expected
    /// to invoke this again whenever any of that state changes (e.g. on
    /// <c>ILayoutEditModeService.ModeChanged</c>/<c>PropertyChanged</c>), not just once at startup.
    /// </summary>
    /// <param name="isMainWindowVisible">Indicates whether the main window is currently visible.</param>
    /// <param name="isEditLayoutChecked">Whether the "Edit layout" item's checkmark should be shown (mirrors <c>ILayoutEditModeService.IsEditing</c>).</param>
    /// <param name="canEnterEditLayout">Whether the "Edit layout" item should be enabled (mirrors <c>ILayoutEditModeService.CanEnterEditLayout</c>; disabled while the layout is locked).</param>
    /// <param name="isLockLayoutChecked">Whether the "Lock layout" item's checkmark should be shown (mirrors <c>ILayoutEditModeService.IsLayoutLocked</c>).</param>
    void UpdateContextMenu(bool isMainWindowVisible, bool isEditLayoutChecked, bool canEnterEditLayout, bool isLockLayoutChecked);
}
