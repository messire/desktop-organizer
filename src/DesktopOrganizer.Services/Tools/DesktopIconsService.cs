using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal class DesktopIconsService(INativeApi nativeApi) : IDesktopIconsService
{
    private const int SwHide = 0;
    private const int SwShow = 5;

    /// <inheritdoc />
    public bool AreIconsVisible
    {
        get
        {
            var defView = FindDefView();
            return defView != IntPtr.Zero && nativeApi.IsWindowVisible(defView);
        }
    }

    /// <inheritdoc />
    public void SetIconsVisible(bool visible)
    {
        var defView = FindDefView();
        if (defView != IntPtr.Zero)
            nativeApi.ShowWindow(defView, visible ? SwShow : SwHide);
    }

    /// <inheritdoc />
    public void Toggle() => SetIconsVisible(!AreIconsVisible);

    /// <summary>
    /// Locates the icon container window (SHELLDLL_DefView). It usually lives under Progman,
    /// but after a WorkerW spawn it can sit under one of the WorkerW windows instead.
    /// </summary>
    private IntPtr FindDefView()
    {
        var progman = nativeApi.FindWindow("Progman", null);
        var defView = nativeApi.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return defView;

        IntPtr found = IntPtr.Zero;
        nativeApi.EnumWindows(
            (hwnd, _) =>
        {
            var child = nativeApi.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero) found = child;
            return true;
        }, IntPtr.Zero);

        return found;
    }
}
