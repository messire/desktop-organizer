using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Anti-conflict partial (ADR-audit WP-D, ADR-034 amendment 2026-10-09): keeps a <b>top-level</b> fence panel
/// (Windows 11 24H2+ "mode T", where no <c>WorkerW</c> exists and <c>EmbedInDesktop</c> answers
/// <c>NoDesktopLayer</c>) in the desktop band — above the wallpaper/icons, below every application window.
/// A panel embedded into <c>WorkerW</c> (mode E) is untouched: applicability is decided per call by the service
/// from the fact (<c>GA_ROOT</c>), never from the OS version. <c>WindowManager</c> attaches this right after
/// <see cref="AttachShellRecoveryTracking"/>, before the first <c>Show()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two event-driven parts, no timer and no global hook:
/// </para>
/// <list type="number">
/// <item><c>WM_WINDOWPOSCHANGING</c> filter on the panel's own <see cref="HwndSource"/>: any attempt to raise
/// the panel (<c>ShowWindow</c>'s <c>HWND_TOP</c>, activation during inline rename, …) is redirected into the
/// band — or its Z-order half vetoed when the panel already is in the band — before it happens, so the panel
/// never flashes on top. Move/resize frames (<c>SWP_NOZORDER</c>) cost one structure read.</item>
/// <item>A deferred <see cref="IDesktopIntegrationService.KeepInDesktopBand"/> after every transition to
/// visible (first show, re-creation, page switch): a freshly created window is linked at the top of the
/// Z-order without any <c>WM_WINDOWPOSCHANGING</c>, and this is the safety net for a show whose Z-order the
/// filter could not redirect. Idempotent — zero <c>SetWindowPos</c> calls when already in place.</item>
/// </list>
/// <para>
/// Other windows being activated above the panel need no handling at all — that only moves them, never the
/// panel, which is exactly the "desktop layer" behaviour wanted. Win+D is therefore unaffected.
/// </para>
/// </remarks>
public partial class ChildWindow
{
    /// <summary><c>WM_WINDOWPOSCHANGING</c>.</summary>
    private const int WmWindowPosChanging = 0x0046;

    private bool _zOrderKeeperAttached;
    private HwndSource? _zOrderHookSource;
    private HwndSourceHook? _zOrderHook;

    /// <summary>
    /// Starts keeping this panel in the desktop band while it is top-level. Called by <c>WindowManager</c>
    /// right after <see cref="Initialize"/>/<see cref="AttachShellRecoveryTracking"/> and before the first
    /// <c>Show()</c>. Idempotent.
    /// </summary>
    internal void AttachDesktopZOrderKeeper()
    {
        if (_zOrderKeeperAttached) return;
        _zOrderKeeperAttached = true;

        SourceInitialized += OnSourceInitializedForZOrder;
        IsVisibleChanged += OnIsVisibleChangedForZOrder;
        Closed += OnClosedForZOrder;

        // Already has a handle (not the case today, but cheap to cover).
        InstallZOrderHook(new WindowInteropHelper(this).Handle);
    }

    private void OnSourceInitializedForZOrder(object? sender, EventArgs e)
        => InstallZOrderHook(new WindowInteropHelper(this).Handle);

    private void InstallZOrderHook(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _zOrderHookSource is not null) return;

        try
        {
            var source = HwndSource.FromHwnd(hwnd);
            if (source is null) return;

            _zOrderHook = ZOrderWindowProc;
            source.AddHook(_zOrderHook);
            _zOrderHookSource = source;
        }
        catch (Exception ex)
        {
            // No hook = the pre-fix behaviour (the deferred re-assert below still runs) — never fatal.
            _zOrderHook = null;
            DesktopZOrderDiagnostics.Shared.WarnOnce("ZOrderHookInstallFailed", ex);
        }
    }

    /// <summary>
    /// Window-procedure hook. Minimal work, never throws, never marks the message handled — WPF's own
    /// processing of <c>WM_WINDOWPOSCHANGING</c> runs unchanged on the (possibly rewritten) structure.
    /// </summary>
    private IntPtr ZOrderWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmWindowPosChanging) return IntPtr.Zero;

        try
        {
            _desktopIntegration.ConstrainWindowPosChangingToDesktopBand(hwnd, lParam);
        }
        catch (Exception ex)
        {
            // The service already swallows; this covers the call boundary inside a window procedure. No log entry
            // is written here — the first failure of the session is posted to the dispatcher (one interlocked
            // exchange per call after that).
            DesktopZOrderDiagnostics.Shared.DeferWindowProcFailure(Dispatcher, ex);
        }

        return IntPtr.Zero;
    }

    private void OnIsVisibleChangedForZOrder(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        // Deferred: IsVisibleChanged can be raised before WPF's ShowWindow has actually run; at Loaded priority
        // the native show is done and the panel's real Z-order slot can be read.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ReassertDesktopBandAfterShow));
    }

    private void ReassertDesktopBandAfterShow()
    {
        if (!IsVisible) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            _desktopIntegration.KeepInDesktopBand(hwnd);
        }
        catch (Exception ex)
        {
            // Cosmetic at worst; never surface on the UI thread. Logged once per session.
            DesktopZOrderDiagnostics.Shared.WarnOnce("ZOrderReassertFailed", ex);
        }
    }

    private void OnClosedForZOrder(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitializedForZOrder;
        IsVisibleChanged -= OnIsVisibleChangedForZOrder;
        Closed -= OnClosedForZOrder;

        var source = _zOrderHookSource;
        var hook = _zOrderHook;
        _zOrderHookSource = null;
        _zOrderHook = null;
        if (source is null || hook is null) return;

        try
        {
            source.RemoveHook(hook);
        }
        catch (Exception ex)
        {
            // A source already disposed with its window may throw — the hook died with it anyway. Logged once
            // per session so an unexpected failure mode is still visible.
            DesktopZOrderDiagnostics.Shared.WarnOnce("ZOrderHookRemoveFailed", ex);
        }
    }
}
