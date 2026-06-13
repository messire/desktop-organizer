using System.IO;

namespace DesktopOrganizer.Services.Tools;

internal class NotifyIconAdapter : INotifyIconAdapter
{
    private readonly NotifyIcon _notifyIcon = new () { Visible = true, };

    public event EventHandler DoubleClick
    {
        add => _notifyIcon.DoubleClick += value;
        remove => _notifyIcon.DoubleClick -= value;
    }

    public void SetIcon(string iconPath)
    {
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            _notifyIcon.Icon = new Icon(iconPath);
            return;
        }

        throw new FileNotFoundException("Icon file not found.", iconPath);
    }

    public void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon)
        => _notifyIcon.ShowBalloonTip(timeout, title, text, icon);

    public void SetContextMenu(ContextMenuStrip menu)
        => _notifyIcon.ContextMenuStrip = menu;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
