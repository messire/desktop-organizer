using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Providers;

/// <inheritdoc />
public class TrayIconManager(INotifyIconAdapter notifyIconAdapter) : ITrayIconManager
{
    // Settings window starts hidden (app lives in the tray) — see ADR-002.
    private bool _isMainWindowVisible;

    /// <inheritdoc />
    public event EventHandler? OnExitRequested;

    /// <inheritdoc />
    public event EventHandler? OnShowMainWindowRequested;

    /// <inheritdoc />
    public event EventHandler? OnToggleDesktopIconsRequested;

    /// <inheritdoc />
    public event EventHandler? OnCaptureDesktopRequested;

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
            _isMainWindowVisible ? "Скрыть настройки" : "Настройки",
            null,
            (_, _) => ToggleMainWindowVisibility());

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Скрыть / показать значки рабочего стола", null,
            (_, _) => OnToggleDesktopIconsRequested?.Invoke(this, EventArgs.Empty));
        contextMenu.Items.Add("Собрать значки рабочего стола", null,
            (_, _) => OnCaptureDesktopRequested?.Invoke(this, EventArgs.Empty));
        contextMenu.Items.Add(new ToolStripSeparator());

        contextMenu.Items.Add("Выход", null, ExitRequested);

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
