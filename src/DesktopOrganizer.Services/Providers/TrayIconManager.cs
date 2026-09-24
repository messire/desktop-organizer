using System.IO;
using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
public class TrayIconManager(INotifyIconAdapter notifyIconAdapter) : ITrayIconManager
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
    private ContextMenuStrip? _contextMenuStrip;
    private ToolStripMenuItem? _editLayoutItem;
    private ToolStripMenuItem? _lockLayoutItem;

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

    /// <summary>
    /// Absolute path of the tray icon, anchored at the application's own install directory
    /// (<see cref="AppContext.BaseDirectory"/>). It used to be the relative <c>Resources/app.ico</c>,
    /// which .NET resolves against the process's <em>current working directory</em> — correct only
    /// when the app is started from its install folder (installer's post-install launch, Start-menu
    /// shortcut). Started from anywhere else — the HKCU <c>Run</c> autostart entry at logon, a
    /// terminal, Win+R — the file was "not found" and startup aborted with
    /// "DesktopOrganizer failed to start: Icon file not found."
    /// </summary>
    internal static string TrayIconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");

    /// <inheritdoc />
    public void Initialize()
    {
        notifyIconAdapter.SetIcon(TrayIconPath);

        // Settings window starts hidden (app lives in the tray) — see ADR-002. Edit layout/Lock layout
        // start unchecked/enabled — the caller (App.StartApp) immediately refreshes this with the real
        // ILayoutEditModeService snapshot right after wiring the new events (P6-3), same as this method
        // has always been re-invoked to reflect state changes rather than only ever called once.
        UpdateContextMenu(isMainWindowVisible: false, isEditLayoutChecked: false, canEnterEditLayout: true, isLockLayoutChecked: false);
        notifyIconAdapter.DoubleClick += (_, _) => ToggleMainWindowVisibility();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        notifyIconAdapter.Dispose();
        _contextMenuStrip?.Dispose();
        _contextMenuStrip = null;
        _editLayoutItem = null;
        _lockLayoutItem = null;
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
        _editLayoutItem.Enabled = canEnterEditLayout;
        _editLayoutItem.ToolTipText = canEnterEditLayout ? string.Empty : "Layout is locked";

        // D-3: "Lock layout" itself must stay clickable at all times — it's the only way to unlock again.
        _lockLayoutItem!.Checked = isLockLayoutChecked;
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
        contextMenu.Items.Add("Settings", null, ShowMainWindowRequested);
        contextMenu.Items.Add(new ToolStripSeparator());

        var editLayoutItem = new ToolStripMenuItem("Edit layout") { CheckOnClick = false };
        editLayoutItem.Click += EditLayoutToggleRequested;
        contextMenu.Items.Add(editLayoutItem);

        var lockLayoutItem = new ToolStripMenuItem("Lock layout") { CheckOnClick = false };
        lockLayoutItem.Click += LockLayoutToggleRequested;
        contextMenu.Items.Add(lockLayoutItem);

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, ExitRequested);

        _editLayoutItem = editLayoutItem;
        _lockLayoutItem = lockLayoutItem;
        _contextMenuStrip = contextMenu;

        // Assigned to the adapter exactly once — subsequent UpdateContextMenu calls mutate these
        // items in place instead of building (and handing off) a new strip.
        notifyIconAdapter.SetContextMenu(contextMenu);
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
}
