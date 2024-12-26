namespace DesktopOrganizer.Services.Interfaces;

public interface INotifyIconAdapter: IDisposable
{
    void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon);
    public void SetIcon(string iconPath);
    void SetContextMenu(ContextMenuStrip menu);
    event EventHandler DoubleClick;
}

