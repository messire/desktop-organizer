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
    /// Event triggered when double-clicking the tray icon — round-robin switch to the next page
    /// (backlog-1x-master-plan.md §1.1, ADR-044). This layer has no visibility into <c>IPageManager</c>
    /// (that type lives in DesktopOrganizer.UI, above this one in the layering, same reasoning as
    /// <see cref="OnEditLayoutToggleRequested"/>'s remarks) — the subscriber is expected to compute and
    /// switch to the next page itself. A single page (nothing to cycle to) is the subscriber's decision
    /// to make into a no-op; this layer always raises the event on every double-click regardless of how
    /// many pages exist.
    /// </summary>
    event EventHandler? OnNextPageRequested;

    /// <summary>
    /// Event triggered when a page is clicked in the tray context menu's "Pages" submenu
    /// (backlog-1x-master-plan.md §2, ADR-044). The argument is the clicked page's identifier. This
    /// layer has no visibility into <c>IPageManager</c> (see <see cref="OnNextPageRequested"/>'s
    /// remarks) — the subscriber is expected to switch to it (<c>IPageManager.SwitchTo</c>) after
    /// confirming the id still refers to a known page (the submenu can be briefly stale between the
    /// click and the next <see cref="SetPages"/> call).
    /// </summary>
    event EventHandler<string>? OnPageSelected;

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
    /// Event triggered when the checkable "Show desktop icons" tray menu item is clicked. Mirrors
    /// <see cref="OnEditLayoutToggleRequested"/>'s "tray reports intent, UI decides" shape even though
    /// <c>IDesktopIconsService</c> is itself reachable from this layer: the item is one of three entry
    /// points (tray, the settings window's General toggle, the desktop double-click gesture) that change
    /// the very same visibility, so the checkmark never flips its own state locally on click — the
    /// subscriber calls <c>IDesktopIconsService.Toggle()</c>, and the checkmark is then refreshed through
    /// <see cref="SetDesktopIconsVisible"/> from <c>IDesktopIconsService.VisibilityChanged</c>, the same
    /// way every other mirror of the state is.
    /// </summary>
    event EventHandler? ToggleDesktopIconsRequested;

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

    /// <summary>
    /// Sets the "Show desktop icons" item's checkmark to <paramref name="visible"/>. A standalone method
    /// rather than another <see cref="UpdateContextMenu"/> parameter (D-6): desktop icon visibility is
    /// process-wide UI state, not layout-edit state, and — unlike Edit layout/Lock layout, which only ever
    /// change from tray clicks or the settings window — it also changes from the desktop double-click
    /// gesture, a caller with no reason to also resupply the three unrelated layout-edit snapshot values
    /// <see cref="UpdateContextMenu"/> requires. The value is a snapshot: the caller invokes this once at
    /// startup and then from an <c>IDesktopIconsService.VisibilityChanged</c> handler, so the checkmark
    /// follows every entry point (tray item, settings window's General toggle, double-click gesture,
    /// Explorer-restart recovery) without any of them having to know about the tray.
    /// </summary>
    /// <param name="visible"><see langword="true"/> if desktop icons are currently visible.</param>
    void SetDesktopIconsVisible(bool visible);

    /// <summary>
    /// (Re)builds the tray context menu's "Pages" submenu contents from the given snapshot
    /// (backlog-1x-master-plan.md §2, ADR-044) — the same "caller-supplied snapshot" shape as
    /// <see cref="UpdateContextMenu"/> (see its remarks): this layer never reads <c>IPageManager</c>
    /// itself. The submenu's own top item (a "Next page" entry that raises the same
    /// <see cref="OnNextPageRequested"/> as the tray icon's double-click) and its separator are built
    /// once and never touched here; only the per-page entries below them are (re)built. Calling this
    /// again with the same sequence of (<see cref="TrayPageEntry.Id"/>, <see cref="TrayPageEntry.DisplayName"/>)
    /// pairs as the previous call mutates only each existing item's checkmark in place — the
    /// <c>ToolStripMenuItem</c> instances are not recreated. Any other change (a page added,
    /// removed, reordered, or renamed) disposes the previous per-page items and builds fresh ones.
    /// Callers are expected to invoke this again whenever the page list's composition, order, names, or
    /// the active page changes, and once at startup.
    /// </summary>
    /// <param name="pages">The full, ordered snapshot of every page to show, in menu order.</param>
    void SetPages(IReadOnlyList<TrayPageEntry> pages);

    /// <summary>
    /// Restyles the tray context menu (design system 1.x, docs/PLANS/design-system-1x.md §2.9 variant A,
    /// ADR-047 D-10) — the same "caller-supplied snapshot" shape as <see cref="SetPages"/>: this layer
    /// never reads the Windows theme or the UI's palette itself. A non-<see langword="null"/>
    /// <paramref name="palette"/> switches the menu (the root strip AND every submenu drop-down, including
    /// "Pages") to the Windows 11-styled owner-drawn renderer in those colours; <see langword="null"/>
    /// (high contrast) switches it to the stock system renderer, which draws in the system colours. The
    /// menu strip itself is never rebuilt or re-handed to the tray icon (MAJOR-1) — only its renderer
    /// changes. A call with a palette equal to the one already applied is a no-op; a call while the
    /// menu is open is applied the next time the menu opens. Callers are expected to invoke this once at
    /// startup and again on every Windows theme / high-contrast change, on the UI thread.
    /// </summary>
    /// <param name="palette">The colours to draw with, or <see langword="null"/> for the system (high-contrast) look.</param>
    void SetMenuPalette(TrayMenuPalette? palette);
}
