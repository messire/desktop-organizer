using System.Windows;

namespace DesktopOrganizer.Native;

public interface INativePanelSizingService : IDisposable
{
    void Attach(nint hwnd, Func<Rect> getBounds, Action<Rect> applyBounds);
    void SetEnabled(bool enabled);
}
