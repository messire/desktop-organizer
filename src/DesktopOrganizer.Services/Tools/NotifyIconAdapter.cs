using System.IO;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Windows Forms <see cref="NotifyIcon"/>-backed <see cref="INotifyIconAdapter"/>.
/// </summary>
internal class NotifyIconAdapter : INotifyIconAdapter
{
    private readonly NotifyIcon _notifyIcon = new () { Visible = true, };

    // Tracks the GDI icon handle currently assigned to the tray icon so it can be disposed once
    // it is no longer referenced by NotifyIcon — SetIcon previously leaked one native icon handle
    // per call.
    private Icon? _currentIcon;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler DoubleClick
    {
        add => _notifyIcon.DoubleClick += value;
        remove => _notifyIcon.DoubleClick -= value;
    }

    /// <inheritdoc/>
    public void SetIcon(string iconPath)
    {
        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
        {
            throw new FileNotFoundException("Icon file not found.", iconPath);
        }

        Icon newIcon;
        try
        {
            // File.Exists above is only a best-effort check — the file can still disappear or be
            // mid-write by the time the constructor reads it, which throws IOException rather than
            // the FileNotFoundException this method's contract documents. Normalize that race to
            // the documented exception type instead of letting an unrelated exception escape.
            newIcon = new Icon(iconPath);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw new FileNotFoundException("Icon file not found or could not be read.", iconPath, ex);
        }

        var previousIcon = _currentIcon;

        // Assign the new icon before disposing the previous one, so NotifyIcon never
        // momentarily holds a reference to an already-destroyed handle.
        _notifyIcon.Icon = newIcon;
        _currentIcon = newIcon;
        previousIcon?.Dispose();
    }

    /// <inheritdoc/>
    public void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon)
        => _notifyIcon.ShowBalloonTip(timeout, title, text, icon);

    /// <inheritdoc/>
    public void SetContextMenu(ContextMenuStrip menu)
        => _notifyIcon.ContextMenuStrip = menu;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
