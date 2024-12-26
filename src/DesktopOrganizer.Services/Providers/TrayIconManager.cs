using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
public class TrayIconManager(INotifyIconAdapter notifyIconAdapter) : ITrayIconManager
{
    private bool _isMainWindowVisible = true;

    /// <inheritdoc />
    public event EventHandler? OnExitRequested;

    /// <inheritdoc />
    public event EventHandler? OnShowMainWindowRequested;

    /// <inheritdoc />
    public void Initialize()
    {
        notifyIconAdapter.SetIcon("Resources/app.ico");

        UpdateContextMenu(_isMainWindowVisible);
        notifyIconAdapter.DoubleClick += (_, _) => ToggleMainWindowVisibility();
    }

    /// <inheritdoc />
    public void Dispose()
        => notifyIconAdapter.Dispose();

    /// <inheritdoc />
    public void UpdateContextMenu(bool isMainWindowVisible)
    {
        var contextMenu = new ContextMenuStrip();

        contextMenu.Items.Add(
            _isMainWindowVisible ? "Hide Main Window" : "Show Main Window",
            null,
            (_, _) => ToggleMainWindowVisibility());

        contextMenu.Items.Add("Exit", null, ExitRequested);

        notifyIconAdapter.SetContextMenu(contextMenu);
    }

    private void ToggleMainWindowVisibility()
    {
        _isMainWindowVisible = !_isMainWindowVisible;
        ShowMainWindowRequested(this, EventArgs.Empty);
        UpdateContextMenu(_isMainWindowVisible);
    }

    private void ShowMainWindowRequested(object? sender, EventArgs e)
        => OnShowMainWindowRequested?.Invoke(sender, e);

    private void ExitRequested(object? sender, EventArgs e)
        => OnExitRequested?.Invoke(sender, e);
}
