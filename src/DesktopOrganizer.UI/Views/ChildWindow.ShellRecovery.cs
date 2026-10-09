using System.ComponentModel;
using System.Windows.Interop;
using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Views;

/// <summary>
/// Anti-conflict partial (release-hardening plan §4 / §8, package C): the panel side of Explorer
/// restart recovery (ADR-034), kept out of <c>ChildWindow.xaml.cs</c> so it never collides with other
/// packages editing that file. <c>WindowManager.RecoverDesktopLayer</c> is the only caller.
/// </summary>
/// <remarks>
/// <para>
/// Two shell layouts are handled (release-hardening F0 spike). Classic (Windows 10, Windows 11 up to
/// 23H2): the panel is a <c>WorkerW</c> child, and when Explorer dies its <c>WorkerW</c> — and with it
/// the panel's HWND — is destroyed; the panel is then rebuilt from its model. Windows 11 24H2+: no
/// <c>WorkerW</c> is found, the panel is top-level, survives the restart, and only needs its styles
/// and bottom-of-Z-order placement re-asserted.
/// </para>
/// <para>
/// A window destroyed from outside never goes through WPF's <c>WM_CLOSE</c> path, so
/// <c>OnClosing</c> — which releases every subscription and disposes the view model — never runs
/// for it. <see cref="TearDownAfterExternalDestroy"/> runs it explicitly, exactly once.
/// </para>
/// </remarks>
public partial class ChildWindow
{
    // The HWND this window was created with, captured on SourceInitialized. Kept separately because
    // WPF may already report IntPtr.Zero (or keep reporting the stale value) after the window was
    // destroyed from outside; either way the captured value is what IsWindow must be asked about.
    private IntPtr _shellRecoveryHwnd;
    private bool _shellRecoveryTrackingAttached;

    // Set once OnClosing has run (observed via the Closing event it raises last), so the explicit
    // tear-down below never releases/disposes a second time.
    private bool _shellRecoveryClosingObserved;

    /// <summary>
    /// Starts tracking this window's native handle for shell-restart recovery. Called by
    /// <c>WindowManager</c> right after <see cref="Initialize"/> and before the first <c>Show()</c>.
    /// Idempotent.
    /// </summary>
    internal void AttachShellRecoveryTracking()
    {
        if (_shellRecoveryTrackingAttached) return;
        _shellRecoveryTrackingAttached = true;

        SourceInitialized += OnSourceInitializedForShellRecovery;
        Closing += OnClosingForShellRecovery;

        // Already shown before tracking was attached (not the case today, but cheap to cover).
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) _shellRecoveryHwnd = handle;
    }

    /// <summary>
    /// One recovery step for this panel after an Explorer restart (ADR-034). Never touches the model
    /// and never persists anything.
    /// </summary>
    /// <returns>
    /// <see cref="PanelShellRecoveryOutcome.NotCreated"/> when the panel never had a native window;
    /// <see cref="PanelShellRecoveryOutcome.Destroyed"/> when its window died with the shell (the caller
    /// rebuilds it from the model); otherwise where the surviving window ended up.
    /// </returns>
    internal PanelShellRecoveryOutcome RecoverAfterShellRestart()
    {
        var current = new WindowInteropHelper(this).Handle;
        var tracked = _shellRecoveryHwnd != IntPtr.Zero ? _shellRecoveryHwnd : current;

        if (tracked == IntPtr.Zero) return PanelShellRecoveryOutcome.NotCreated;

        // Either WPF already dropped/changed the handle, or the OS no longer knows it.
        if (current != tracked || !_desktopIntegration.IsWindowAlive(tracked))
            return PanelShellRecoveryOutcome.Destroyed;

        var result = _desktopIntegration.EmbedInDesktop(tracked);

        // release-hardening F (ADR-037, plan §3.2/§9): re-report the fact of this panel's root window
        // after a shell-restart re-embed — the desktop layer mode itself may have changed (classic WorkerW
        // shell restarted into a Windows 11 24H2+ top-level layout, or vice versa).
        //
        // M10 (release-hardening 1.0 review): this runs once PER PANEL inside WindowManager.RecoverDesktopLayer's
        // own loop, so on its own it would rebuild the desktop-scale snapshot (and could raise
        // IDesktopScaleProvider.Changed) after every single panel in that loop — a subscriber reacting to
        // that mid-loop could write geometry against a panel further along in the SAME loop that has
        // already been torn down (destroyed, not yet re-embedded/rebuilt). ShellRecoveryCoordinator wraps
        // the WHOLE RecoverDesktopLayer call in one IDesktopScaleProvider.BeginBatch scope, so every one
        // of these per-panel calls only marks the snapshot dirty; the actual rebuild (and at most one
        // Changed) happens exactly once, after every panel in the attempt is already done.
        if (result is DesktopEmbedResult.Reparented or DesktopEmbedResult.AlreadyEmbedded or DesktopEmbedResult.NoDesktopLayer)
            _desktopScale.ReportPanelWindow(tracked);

        switch (result)
        {
            case DesktopEmbedResult.Reparented:
            case DesktopEmbedResult.AlreadyEmbedded:
                return PanelShellRecoveryOutcome.Embedded;

            case DesktopEmbedResult.NoDesktopLayer:
                // Top-level panel (Windows 11 24H2+, or a WorkerW that does not exist yet): the new
                // shell's windows may now sit above it — put it back at the top of the desktop band
                // (ADR-034 amendment 2026-10-09: band-aware, so N panels and the sidebar's own
                // one-slot-above-Progman timer never swap places). Styles were already re-applied by
                // EmbedInDesktop itself.
                _desktopIntegration.KeepInDesktopBand(tracked);
                return PanelShellRecoveryOutcome.TopLevel;

            default:
                return PanelShellRecoveryOutcome.Destroyed;
        }
    }

    /// <summary>
    /// Runs the ordinary close-time clean-up (<c>OnClosing</c>: abort gestures, detach hooks and
    /// subscriptions, sync the file list into the model, dispose the view model) for a window whose
    /// HWND was destroyed from outside. Exactly once; a no-op if <c>OnClosing</c> already ran.
    /// </summary>
    internal void TearDownAfterExternalDestroy()
    {
        if (_shellRecoveryClosingObserved) return;
        _shellRecoveryClosingObserved = true;

        SourceInitialized -= OnSourceInitializedForShellRecovery;
        Closing -= OnClosingForShellRecovery;

        OnClosing(new CancelEventArgs());
    }

    private void OnSourceInitializedForShellRecovery(object? sender, EventArgs e)
        => _shellRecoveryHwnd = new WindowInteropHelper(this).Handle;

    private void OnClosingForShellRecovery(object? sender, CancelEventArgs e)
        => _shellRecoveryClosingObserved = true;
}
