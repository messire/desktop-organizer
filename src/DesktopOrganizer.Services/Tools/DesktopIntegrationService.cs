using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
internal class DesktopIntegrationService(INativeApi nativeApi) : IDesktopIntegrationService
{
    private const uint ProgmanMsg   = 0x052C;
    private const int  GwlExStyle   = -20;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    /// <inheritdoc />
    public void EmbedInDesktop(IntPtr hwnd)
    {
        var workerW = GetOrCreateWorkerW();
        if (workerW == IntPtr.Zero) return;

        nativeApi.SetParent(hwnd, workerW);
        ApplyDesktopWindowStyles(hwnd);
    }

    /// <summary>
    /// Finds or creates the WorkerW window that sits between the wallpaper and desktop icons.
    /// </summary>
    private IntPtr GetOrCreateWorkerW()
    {
        var progman = nativeApi.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // Sending 0x052C to Progman makes Windows spawn a WorkerW between
        // the wallpaper and the icon layer (undocumented but widely used).
        nativeApi.SendMessage(progman, ProgmanMsg, IntPtr.Zero, IntPtr.Zero);

        IntPtr workerW = IntPtr.Zero;

        nativeApi.EnumWindows((hwnd, _) =>
        {
            // WorkerW is the top-level window whose child is SHELLDLL_DefView
            var defView = nativeApi.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                workerW = nativeApi.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);

            return true; // continue enumeration
        }, IntPtr.Zero);

        return workerW;
    }

    /// <summary>
    /// Adds WS_EX_TOOLWINDOW and WS_EX_NOACTIVATE to the window's extended style.
    /// — TOOLWINDOW: removes from taskbar and Alt+Tab
    /// — NOACTIVATE:  clicks on the panel don't steal focus from the active app
    /// </summary>
    private void ApplyDesktopWindowStyles(IntPtr hwnd)
    {
        var current = (uint)nativeApi.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var updated = current | WsExToolWindow | WsExNoActivate;
        nativeApi.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr((long)updated));
    }
}
