using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
public class TrayIconManager(INotifyIconAdapter notifyIconAdapter) : ITrayIconManager
{
    /// <inheritdoc />
    public event EventHandler? OnExitRequested;

    /// <inheritdoc />
    public event EventHandler? OnShowMainWindowRequested;

    /// <inheritdoc />
    public event EventHandler? OnToggleMainWindowVisibilityRequested;

    /// <inheritdoc />
    public void Initialize()
    {
        notifyIconAdapter.SetIcon("Resources/app.ico");

        // Settings window starts hidden (app lives in the tray) — see ADR-002.
        UpdateContextMenu(isMainWindowVisible: false);
        notifyIconAdapter.DoubleClick += (_, _) => ToggleMainWindowVisibility();
    }

    /// <inheritdoc />
    public void Dispose()
        => notifyIconAdapter.Dispose();

    /// <inheritdoc />
    public void UpdateContextMenu(bool isMainWindowVisible)
    {
        var contextMenu = new ContextMenuStrip();

        // Always "Settings" — clicking it is an explicit "open the window" action, never a hide.
        // (Double-clicking the tray icon is the separate toggle gesture — see ToggleMainWindowVisibility.)
        contextMenu.Items.Add("Settings", null, ShowMainWindowRequested);

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, ExitRequested);

        notifyIconAdapter.SetContextMenu(contextMenu);
    }

    private void ToggleMainWindowVisibility()
        => OnToggleMainWindowVisibilityRequested?.Invoke(this, EventArgs.Empty);

    private void ShowMainWindowRequested(object? sender, EventArgs e)
        => OnShowMainWindowRequested?.Invoke(sender, e);

    private void ExitRequested(object? sender, EventArgs e)
        => OnExitRequested?.Invoke(sender, e);
}
