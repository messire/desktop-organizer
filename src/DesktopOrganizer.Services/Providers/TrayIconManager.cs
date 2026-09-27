using System.IO;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
public class TrayIconManager : ITrayIconManager
{
    // MAJOR-1 fix (Phase 6 review): the ContextMenuStrip (and its ToolStripMenuItem/ToolStripSeparator
    // children) is now built exactly ONCE and handed to the adapter a single time — see EnsureContextMenuBuilt.
    // UpdateContextMenu, which after P6-3 is invoked on every ModeChanged/IsLayoutLocked/
    // CanEnterEditLayout change (i.e. on every Edit Layout toggle from any entry point), only MUTATES
    // the already-live Checked/Enabled properties of these fields from here on. Previously it
    // constructed a brand-new ContextMenuStrip on every call and handed it to
    // INotifyIconAdapter.SetContextMenu, which just overwrites NotifyIcon.ContextMenuStrip — the
    // previous strip (a ToolStripDropDown : Control, owning a native HWND + GDI resources once shown)
    // was never Dispose()'d, leaking one per toggle. Rebuilding also raced with its own Click handler:
    // a menu item's Click closure could still be dispatching (the toggle → ModeChanged → menu rebuild
    // chain runs synchronously from inside that same Click) while the strip that raised it was being
    // replaced out from under it. Mutating fixed fields in place removes both problems.
    //
    // docs/PLANS/localization.md L-7/§2.7: the same "mutate in place, never rebuild" rule extends to
    // the hot language switch — ApplyTexts (below) only ever re-labels these same five fields'
    // Text/ToolTipText in response to ILocalizationService.CultureChanged; it never touches
    // EnsureContextMenuBuilt's one-time construction. Every item also gets a stable Name (below) so
    // tests — and any future caller — can find them independently of the currently-displayed,
    // language-dependent Text.

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Settings" menu item.</summary>
    internal const string SettingsItemName = "Tray.Settings";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Edit layout" menu item.</summary>
    internal const string EditLayoutItemName = "Tray.EditLayout";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Lock layout" menu item.</summary>
    internal const string LockLayoutItemName = "Tray.LockLayout";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Show desktop icons" menu item.</summary>
    internal const string ShowDesktopIconsItemName = "Tray.ShowDesktopIcons";

    /// <summary>Stable <see cref="ToolStripItem.Name"/> of the "Exit" menu item.</summary>
    internal const string ExitItemName = "Tray.Exit";

    private readonly INotifyIconAdapter _notifyIconAdapter;
    private readonly ILocalizationService _localization;
    private ContextMenuStrip? _contextMenuStrip;
    private ToolStripMenuItem? _settingsItem;
    private ToolStripMenuItem? _editLayoutItem;
    private ToolStripMenuItem? _lockLayoutItem;
    private ToolStripMenuItem? _showDesktopIconsItem;
    private ToolStripMenuItem? _exitItem;

    // The last canEnterEditLayout snapshot UpdateContextMenu received. ApplyTexts needs it to decide
    // whether "Edit layout"'s ToolTipText should be the localized Tray_LayoutLockedTooltip text or empty
    // (docs/PLANS/localization.md §2.7) — a language switch alone must re-render the tooltip in the new
    // language without requiring the caller to invoke UpdateContextMenu again just because the language
    // changed.
    private bool _canEnterEditLayout = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconManager"/> class.
    /// </summary>
    /// <param name="notifyIconAdapter">The tray icon/context-menu adapter.</param>
    /// <param name="localization">
    /// The hot-switchable localization service (docs/PLANS/localization.md §2.7). Subscribed to for the
    /// lifetime of this instance — see <see cref="Dispose"/> for the matching unsubscribe.
    /// </param>
    public TrayIconManager(INotifyIconAdapter notifyIconAdapter, ILocalizationService localization)
    {
        _notifyIconAdapter = notifyIconAdapter;
        _localization = localization;
        _localization.CultureChanged += OnCultureChanged;
    }

    /// <inheritdoc />
    public event EventHandler? OnExitRequested;

    /// <inheritdoc />
    public event EventHandler? OnShowMainWindowRequested;

    /// <inheritdoc />
    public event EventHandler? OnToggleMainWindowVisibilityRequested;

    /// <inheritdoc />
    public event EventHandler? OnEditLayoutToggleRequested;

    /// <inheritdoc />
    public event EventHandler? OnLockLayoutToggleRequested;

    /// <inheritdoc />
    public event EventHandler? ToggleDesktopIconsRequested;

    /// <summary>
    /// Absolute path of the tray icon, anchored at the application's own install directory
    /// (<see cref="AppContext.BaseDirectory"/>). It used to be the relative <c>Resources/app.ico</c>,
    /// which .NET resolves against the process's <em>current working directory</em> — correct only
    /// when the app is started from its install folder (installer's post-install launch, Start-menu
    /// shortcut). Started from anywhere else — the HKCU <c>Run</c> autostart entry at logon, a
    /// terminal, Win+R — the file was "not found" and startup aborted with
    /// "DesktopOrganizer failed to start: Icon file not found".
    /// </summary>
    internal static string TrayIconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");

    /// <inheritdoc />
    public void Initialize()
    {
        _notifyIconAdapter.SetIcon(TrayIconPath);

        // Settings window starts hidden (app lives in the tray) — see ADR-002. Edit layout/Lock layout
        // start unchecked/enabled — the caller (App.StartApp) immediately refreshes this with the real
        // ILayoutEditModeService snapshot right after wiring the new events (P6-3), same as this method
        // has always been re-invoked to reflect state changes rather than only ever called once.
        UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        _notifyIconAdapter.DoubleClick += (_, _) => ToggleMainWindowVisibility();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _localization.CultureChanged -= OnCultureChanged;
        _notifyIconAdapter.Dispose();
        _contextMenuStrip?.Dispose();
        _contextMenuStrip = null;
        _settingsItem = null;
        _editLayoutItem = null;
        _lockLayoutItem = null;
        _showDesktopIconsItem = null;
        _exitItem = null;
    }

    /// <inheritdoc />
    public void UpdateContextMenu(bool isMainWindowVisible, bool isEditLayoutChecked, bool canEnterEditLayout, bool isLockLayoutChecked)
    {
        // Lazily builds+hands off the strip on the very first call (from either Initialize or a caller
        // that invokes UpdateContextMenu directly), and is a no-op on every call after that — see the
        // MAJOR-1 remarks on the fields above.
        EnsureContextMenuBuilt();

        // D-9 #1 — the guaranteed, WS_EX_NOACTIVATE-independent entry point for the global layout edit
        // mode. CheckOnClick is deliberately false: this layer doesn't own the checked state (see
        // OnEditLayoutToggleRequested's remarks) — the item's Checked/Enabled are refreshed from a fresh
        // snapshot on every real state change instead of the ToolStripMenuItem flipping its own Checked
        // locally.
        _editLayoutItem!.Checked = isEditLayoutChecked;

        // D-3: entering Edit Layout is refused while the layout is locked — disable the item rather
        // than let a click silently no-op. D-9 #1 (plan D-9 §1, MINOR-8 fix-cycle round 2): explain
        // WHY it's disabled via a tooltip rather than leaving a mysteriously-greyed-out item.
        _canEnterEditLayout = canEnterEditLayout;
        _editLayoutItem.Enabled = canEnterEditLayout;
        _editLayoutItem.ToolTipText = canEnterEditLayout ? string.Empty : _localization.GetString("Tray_LayoutLockedTooltip");

        // D-3: "Lock layout" itself must stay clickable at all times — it's the only way to unlock again.
        _lockLayoutItem!.Checked = isLockLayoutChecked;
    }

    /// <inheritdoc />
    public void SetDesktopIconsVisible(bool visible)
    {
        EnsureContextMenuBuilt();
        _showDesktopIconsItem!.Checked = visible;
    }

    private void EnsureContextMenuBuilt()
    {
        if (_contextMenuStrip is not null)
        {
            return;
        }

        var contextMenu = new ContextMenuStrip();

        // Always "Settings" — clicking it is an explicit "open the window" action, never a hide.
        // (Double-clicking the tray icon is the separate toggle gesture — see ToggleMainWindowVisibility.)
        var settingsItem = new ToolStripMenuItem { Name = SettingsItemName };
        settingsItem.Click += ShowMainWindowRequested;
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        var editLayoutItem = new ToolStripMenuItem { Name = EditLayoutItemName, CheckOnClick = false };
        editLayoutItem.Click += EditLayoutToggleRequested;
        contextMenu.Items.Add(editLayoutItem);

        var lockLayoutItem = new ToolStripMenuItem { Name = LockLayoutItemName, CheckOnClick = false };
        lockLayoutItem.Click += LockLayoutToggleRequested;
        contextMenu.Items.Add(lockLayoutItem);

        // D-6: same event-plus-refresh shape as Edit layout/Lock layout above — CheckOnClick is
        // deliberately false because this item's checked state must also track the desktop double-click
        // gesture, a second entry point this menu item's own click can never see (see
        // ToggleDesktopIconsRequested's and SetDesktopIconsVisible's remarks).
        var showDesktopIconsItem = new ToolStripMenuItem { Name = ShowDesktopIconsItemName, CheckOnClick = false };
        showDesktopIconsItem.Click += DesktopIconsToggleRequested;
        contextMenu.Items.Add(showDesktopIconsItem);

        contextMenu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem { Name = ExitItemName };
        exitItem.Click += ExitRequested;
        contextMenu.Items.Add(exitItem);

        _settingsItem = settingsItem;
        _editLayoutItem = editLayoutItem;
        _lockLayoutItem = lockLayoutItem;
        _showDesktopIconsItem = showDesktopIconsItem;
        _exitItem = exitItem;
        _contextMenuStrip = contextMenu;

        // docs/PLANS/localization.md L-7/§2.7: set the initial (startup-culture) Text/ToolTipText right
        // after construction — ApplyTexts is otherwise only ever invoked in response to
        // ILocalizationService.CultureChanged, which never fires just from constructing the service.
        ApplyTexts();

        // Assigned to the adapter exactly once — subsequent UpdateContextMenu calls mutate these
        // items in place instead of building (and handing off) a new strip.
        _notifyIconAdapter.SetContextMenu(contextMenu);
    }

    /// <summary>
    /// Re-labels the five tray menu items (and the disabled "Edit layout" tooltip, if currently shown)
    /// to <see cref="ILocalizationService.CurrentCulture"/>'s text — in place, on the very same
    /// <see cref="ToolStripMenuItem"/> instances <see cref="EnsureContextMenuBuilt"/> created once
    /// (docs/PLANS/localization.md L-7: the strip itself is never rebuilt or re-handed to the adapter).
    /// </summary>
    private void ApplyTexts()
    {
        _settingsItem!.Text = _localization.GetString("Tray_Settings");
        _editLayoutItem!.Text = _localization.GetString("Common_EditLayout");
        _editLayoutItem.ToolTipText = _canEnterEditLayout ? string.Empty : _localization.GetString("Tray_LayoutLockedTooltip");
        _lockLayoutItem!.Text = _localization.GetString("Common_LockLayout");
        _showDesktopIconsItem!.Text = _localization.GetString("Tray_ShowDesktopIcons");
        _exitItem!.Text = _localization.GetString("Tray_Exit");
    }

    private void OnCultureChanged(object? sender, CultureChangedEventArgs e)
    {
        // Guard rather than assert: EnsureContextMenuBuilt only runs lazily on the first
        // Initialize/UpdateContextMenu/SetDesktopIconsVisible call, so a culture change that lands
        // before any of those (unlikely in production — App.StartApp applies the language before
        // InitTrayIcon — but not impossible in a test double) must not NRE on the not-yet-built items.
        if (_contextMenuStrip is not null)
        {
            ApplyTexts();
        }
    }

    private void ToggleMainWindowVisibility()
        => OnToggleMainWindowVisibilityRequested?.Invoke(this, EventArgs.Empty);

    private void ShowMainWindowRequested(object? sender, EventArgs e)
        => OnShowMainWindowRequested?.Invoke(sender, e);

    private void ExitRequested(object? sender, EventArgs e)
        => OnExitRequested?.Invoke(sender, e);

    private void EditLayoutToggleRequested(object? sender, EventArgs e)
        => OnEditLayoutToggleRequested?.Invoke(sender, e);

    private void LockLayoutToggleRequested(object? sender, EventArgs e)
        => OnLockLayoutToggleRequested?.Invoke(sender, e);

    private void DesktopIconsToggleRequested(object? sender, EventArgs e)
        => ToggleDesktopIconsRequested?.Invoke(sender, e);
}
