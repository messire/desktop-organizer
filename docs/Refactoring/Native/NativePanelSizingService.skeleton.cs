using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopOrganizer.Native;

/// <summary>
/// Skeleton only. Production version must centralize constants/PInvoke in the project's existing native adapter.
/// </summary>
public sealed class NativePanelSizingService : IDisposable
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_SIZING = 0x0214;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_DPICHANGED = 0x02E0;

    private HwndSource? _source;

    public void Attach(nint hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // 1. convert screen point to window-relative DIPs/device pixels
                // 2. return HTLEFT/HTRIGHT/HTTOP/HTBOTTOM/corners when inside resize margin
                break;
            case WM_ENTERSIZEMOVE:
                // begin interaction state
                break;
            case WM_SIZING:
                // read RECT, apply managed ResizeGeometryCalculator/grid rules, write RECT back
                break;
            case WM_EXITSIZEMOVE:
                // final snap + persist
                break;
            case WM_DPICHANGED:
                // accept/normalize suggested rect according to monitor policy
                break;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_source is not null)
            _source.RemoveHook(WndProc);
        _source = null;
    }
}
