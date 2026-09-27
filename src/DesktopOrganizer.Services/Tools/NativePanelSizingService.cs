using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <summary>
/// Installs the window hook of <see cref="NativePanelSizingService"/> on <paramref name="hwnd"/>.
/// </summary>
/// <param name="hwnd">Window to hook.</param>
/// <param name="hook">Hook procedure; the very same instance must be used to remove it again.</param>
/// <returns>
/// A token whose <see cref="IDisposable.Dispose"/> removes the hook, or <see langword="null"/> when the
/// window cannot be hooked (no <see cref="HwndSource"/> — destroyed/foreign/not yet created HWND).
/// </returns>
/// <remarks>
/// Exists as a seam: the production implementation goes through <see cref="HwndSource"/>, which needs a
/// real window, while tests substitute an installer that captures the delegate and invokes it directly —
/// the same technique used for <see cref="NativeWindowProc"/> in <c>MonitorServiceNativeApiTests</c> and
/// for the WH_MOUSE_LL callback in <c>DesktopGestureServiceTests</c>.
/// </remarks>
internal delegate IDisposable? SizingHookInstaller(IntPtr hwnd, HwndSourceHook hook);

/// <summary>
/// Translates the messages of the system sizing loop (<c>WM_NCHITTEST</c>, <c>WM_ENTERSIZEMOVE</c>,
/// <c>WM_SIZING</c>, <c>WM_EXITSIZEMOVE</c>, <c>WM_CAPTURECHANGED</c>, <c>WM_DPICHANGED</c>[<c>_AFTERPARENT</c>]
/// plus <c>WM_MOUSEACTIVATE</c>) into calls on an <see cref="INativePanelSizingClient"/>.
/// See ADR-022 / <c>docs/PLANS/phase-2-native-panel-sizing.md</c> §1.2.
/// </summary>
/// <remarks>
/// <para>
/// Zero geometry, zero snap, zero DIP: the service only decodes messages and hands out physical pixels
/// (D-2/D-3). All P/Invoke goes through <see cref="INativeApi"/> per the project invariant.
/// </para>
/// <para>
/// Every message is handled inside a <c>try</c>/<c>catch</c>: an exception must never leave a window
/// procedure — same invariant as <see cref="MonitorService"/>'s <c>WndProcImpl</c>. On failure the message
/// is reported as unhandled, so <c>DefWindowProc</c>/WPF keep their default behaviour.
/// </para>
/// <para>
/// MANUAL VERIFY (Win10/Win11): everything downstream of <c>DefWindowProc</c>'s modal sizing loop —
/// live-resize on a layered window (R-1), focus staying with the foreground app (R-2), the real
/// coordinate space of <c>WM_SIZING</c> for a <c>WorkerW</c>-embedded panel (R-3) and whether
/// <c>WM_DPICHANGED_AFTERPARENT</c> ever arrives (R-7) — is not observable headless.
/// </para>
/// </remarks>
internal sealed class NativePanelSizingService : INativePanelSizingService
{
    // ── Messages (single place, plan §1.2) ──────────────────────────────────────────────────────
    private const int WmMouseActivate = 0x0021;
    private const int WmSizing = 0x0214;
    private const int WmCaptureChanged = 0x0215;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmNcHitTest = 0x0084;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDpiChangedAfterParent = 0x02E3;

    // ── WM_NCHITTEST results (WinUser.h HT*) ────────────────────────────────────────────────────
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    // ── WM_SIZING wParam (WinUser.h WMSZ_*) ─────────────────────────────────────────────────────
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

    /// <summary>WinUser.h <c>MA_NOACTIVATE</c> — activate nothing, deliver the click (R-2).</summary>
    private const int MaNoActivate = 3;

    /// <summary>Return value of a handled <c>WM_SIZING</c> (<c>TRUE</c>).</summary>
    private const int MessageHandled = 1;

    private readonly INativeApi _nativeApi;
    private readonly SizingHookInstaller _installer;
    private readonly Dictionary<IntPtr, PanelSizingAttachment> _attachments = new();
    private readonly object _gate = new();
    private readonly IAppLog? _appLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativePanelSizingService"/> class hooking real
    /// windows through <see cref="HwndSource"/>. This is the constructor DI resolves.
    /// </summary>
    /// <param name="nativeApi">Native API façade (all P/Invoke lives behind it).</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    public NativePanelSizingService(INativeApi nativeApi, IAppLog? appLog = null)
        : this(nativeApi, InstallHwndSourceHook, appLog)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NativePanelSizingService"/> class with a custom hook
    /// installer. Test-only: lets a test capture the hook delegate and drive it directly, since
    /// <see cref="HwndSource.FromHwnd"/> requires a real WPF window. Not visible to DI, which only
    /// considers public constructors.
    /// </summary>
    /// <param name="nativeApi">Native API façade.</param>
    /// <param name="installer">Hook installer to use instead of <see cref="HwndSource"/>.</param>
    /// <param name="appLog">See <see cref="Log"/>.</param>
    internal NativePanelSizingService(INativeApi nativeApi, SizingHookInstaller installer, IAppLog? appLog = null)
    {
        _nativeApi = nativeApi;
        _installer = installer;
        _appLog = appLog;
    }

    /// <summary>
    /// The log this instance (and its nested <see cref="PanelSizingAttachment"/> attachments) write
    /// failures to: the one explicitly supplied, falling back to the process-wide ambient
    /// <see cref="AppLog.Current"/> for tests that construct this type directly without a log.
    /// </summary>
    private IAppLog Log => _appLog ?? AppLog.Current;

    /// <inheritdoc />
    public IDisposable Attach(IntPtr hwnd, INativePanelSizingClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            lock (_gate)
            {
                if (_attachments.ContainsKey(hwnd))
                {
                    // Two live hooks on one panel would double every client call; refuse, but never
                    // throw — a second Attach is a wiring bug, not a reason to kill the panel.
                    Log.Warn(
                        "NativePanelSizingService",
                        "AttachIgnoredAlreadyAttached",
                        new KeyValuePair<string, object?>("hwnd", hwnd.ToInt64()));
                    return NoOpToken.Instance;
                }

                var attachment = new PanelSizingAttachment(this, hwnd, client);
                var registration = _installer(hwnd, attachment.HandleMessage);
                if (registration is null)
                {
                    Log.Warn(
                        "NativePanelSizingService",
                        "AttachUnavailableNoHwndSource",
                        new KeyValuePair<string, object?>("hwnd", hwnd.ToInt64()));
                    return NoOpToken.Instance;
                }

                attachment.SetRegistration(registration);
                _attachments[hwnd] = attachment;
                return attachment;
            }
        }
        catch (Exception ex)
        {
            // Native sizing degrades to "unavailable" (the legacy Thumb path still exists) instead of
            // taking the panel down with it.
            Log.Error("NativePanelSizingService", "AttachFailed", ex);
            return NoOpToken.Instance;
        }
    }

    private static IDisposable? InstallHwndSourceHook(IntPtr hwnd, HwndSourceHook hook)
    {
        var source = HwndSource.FromHwnd(hwnd);
        if (source is null) return null;

        source.AddHook(hook);
        return new HookRegistration(source, hook);
    }

    /// <summary>Signed X coordinate packed into the low word of a message <c>lParam</c>.</summary>
    private static int LoWord(IntPtr value) => unchecked((short)(value.ToInt64() & 0xFFFF));

    /// <summary>Signed Y coordinate packed into the high word of a message <c>lParam</c>.</summary>
    private static int HiWord(IntPtr value) => unchecked((short)((value.ToInt64() >> 16) & 0xFFFF));

    /// <summary>
    /// Maps a <c>WM_SIZING</c> <c>wParam</c> (<c>WMSZ_*</c>) to an edge. Mirrors
    /// <c>NativeSizingEdgeMap.FromWmsz</c> in the UI layer, which cannot be referenced from here
    /// (UI depends on Services, not the other way round); both tables are pinned by their own tests.
    /// Any value outside the documented 1..8 yields <see cref="NativeSizingEdge.None"/>.
    /// </summary>
    private static NativeSizingEdge EdgeFromWmsz(int wParam) => wParam switch
    {
        WmszLeft => NativeSizingEdge.Left,
        WmszRight => NativeSizingEdge.Right,
        WmszTop => NativeSizingEdge.Top,
        WmszTopLeft => NativeSizingEdge.TopLeft,
        WmszTopRight => NativeSizingEdge.TopRight,
        WmszBottom => NativeSizingEdge.Bottom,
        WmszBottomLeft => NativeSizingEdge.BottomLeft,
        WmszBottomRight => NativeSizingEdge.BottomRight,
        _ => NativeSizingEdge.None,
    };

    /// <summary>
    /// Maps an edge to its <c>WM_NCHITTEST</c> result. Never returns <c>HTCAPTION</c> (D-5): that would
    /// hand panel dragging to the system modal loop and switch off anti-overlap (ADR-017) and magnetic
    /// snapping (ADR-011), which are WPF gestures.
    /// </summary>
    private static int HitTestResult(NativeSizingEdge edge) => edge switch
    {
        NativeSizingEdge.Left => HtLeft,
        NativeSizingEdge.Right => HtRight,
        NativeSizingEdge.Top => HtTop,
        NativeSizingEdge.TopLeft => HtTopLeft,
        NativeSizingEdge.TopRight => HtTopRight,
        NativeSizingEdge.Bottom => HtBottom,
        NativeSizingEdge.BottomLeft => HtBottomLeft,
        NativeSizingEdge.BottomRight => HtBottomRight,
        _ => 0,
    };

    private void Detach(IntPtr hwnd, PanelSizingAttachment attachment)
    {
        lock (_gate)
        {
            if (_attachments.TryGetValue(hwnd, out var current) && ReferenceEquals(current, attachment))
                _attachments.Remove(hwnd);
        }
    }

    /// <summary>
    /// One hooked panel: owns the per-gesture state machine and the hook registration.
    /// </summary>
    /// <remarks>
    /// State is deliberately tiny — the edge seeded by the last sizing <c>WM_NCHITTEST</c>, and whether a
    /// sizing cycle is currently running and was accepted by the client. Everything else (session,
    /// snapping, persistence) belongs to the client.
    /// </remarks>
    internal sealed class PanelSizingAttachment : IDisposable
    {
        private readonly NativePanelSizingService _owner;
        private readonly IntPtr _hwnd;
        private readonly INativePanelSizingClient _client;

        private IDisposable? _registration;
        private bool _disposed;

        private NativeSizingEdge _lastSizingHit;
        private NativeSizingEdge _cycleEdge;
        private bool _cycleActive;
        private bool _cycleAccepted;

        /// <summary>
        /// Initializes a new instance of the <see cref="PanelSizingAttachment"/> class.
        /// </summary>
        /// <param name="owner">The service that owns this attachment (used for shared sizing/DPI helpers).</param>
        /// <param name="hwnd">The panel's window handle.</param>
        /// <param name="client">The sizing policy this attachment forwards hook messages to.</param>
        internal PanelSizingAttachment(NativePanelSizingService owner, IntPtr hwnd, INativePanelSizingClient client)
        {
            _owner = owner;
            _hwnd = hwnd;
            _client = client;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Drop the cycle without notifying: a panel disposing mid-gesture is torn down by
            // ChildWindow.OnClosing, which aborts its own resize session explicitly (plan §1.5).
            _cycleActive = false;
            _cycleAccepted = false;

            _registration?.Dispose();
            _registration = null;
            _owner.Detach(_hwnd, this);
        }

        /// <summary>
        /// The hook procedure (<see cref="HwndSourceHook"/>). Runs inside the panel's window procedure —
        /// and, for the whole sizing cycle, inside <c>DefWindowProc</c>'s nested modal message loop.
        /// Never throws.
        /// </summary>
        /// <param name="hwnd">Window that received the message.</param>
        /// <param name="msg">Message id.</param>
        /// <param name="wParam">Message <c>wParam</c>.</param>
        /// <param name="lParam">Message <c>lParam</c>.</param>
        /// <param name="handled">Set to <see langword="true"/> to suppress further processing.</param>
        /// <returns>The message result when <paramref name="handled"/> is set, otherwise zero.</returns>
        internal IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_disposed) return IntPtr.Zero;

            try
            {
                switch (msg)
                {
                    case WmNcHitTest:
                        return OnNcHitTest(hwnd, lParam, ref handled);

                    case WmMouseActivate:
                        // R-2: the panel is WS_EX_NOACTIVATE and must never steal focus — not even when
                        // DefWindowProc is about to enter the sizing loop on WM_NCLBUTTONDOWN.
                        handled = true;
                        return new IntPtr(MaNoActivate);

                    case WmEnterSizeMove:
                        OnEnterSizeMove(hwnd);
                        return IntPtr.Zero;

                    case WmSizing:
                        return OnSizing(wParam, lParam, ref handled);

                    case WmExitSizeMove:
                        OnExitSizeMove(hwnd);
                        return IntPtr.Zero;

                    case WmCaptureChanged:
                        OnCaptureChanged(lParam);
                        return IntPtr.Zero;

                    case WmDpiChanged:
                    case WmDpiChangedAfterParent:
                        OnDpiChanged(hwnd, msg, wParam, lParam);
                        return IntPtr.Zero;

                    default:
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                // Hard invariant: nothing escapes into the window procedure. Reporting the message as
                // unhandled leaves DefWindowProc/WPF in charge, which is always a valid fallback.
                handled = false;
                _owner.Log.Error(
                    "NativePanelSizingService",
                    "HookError",
                    ex,
                    new KeyValuePair<string, object?>("msg", msg));
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Stores the hook registration token so it can be disposed alongside this attachment.
        /// </summary>
        /// <param name="registration">The hook registration to own and dispose together with this attachment.</param>
        internal void SetRegistration(IDisposable registration) => _registration = registration;

        private IntPtr OnNcHitTest(IntPtr hwnd, IntPtr lParam, ref bool handled)
        {
            if (!_owner._nativeApi.GetWindowRect(hwnd, out var windowRect))
                return IntPtr.Zero;

            var point = new NativePointPx(LoWord(lParam), HiWord(lParam));
            var dpiScale = _owner._nativeApi.GetDpiScaleForWindow(hwnd);
            var edge = _client.HitTest(point, windowRect, dpiScale);

            if (edge == NativeSizingEdge.None)
            {
                // Not a border: leave it to DefWindowProc (HTCLIENT) so WPF keeps its own hit testing.
                return IntPtr.Zero;
            }

            // Seed for the next WM_ENTERSIZEMOVE. Only sizing hits are remembered, and only outside a
            // running cycle: the modal loop keeps hit-testing while dragging, and a hit that lands in
            // the client area mid-gesture must not overwrite the edge the gesture started from.
            if (!_cycleActive) _lastSizingHit = edge;

            handled = true;
            return new IntPtr(HitTestResult(edge));
        }

        private void OnEnterSizeMove(IntPtr hwnd)
        {
            // The edge is seeded from the last sizing WM_NCHITTEST: the loop can only be entered from
            // WM_NCLBUTTONDOWN on an HT*-sizing code, so that hit is the one under the cursor at
            // button-down. It stays a seed, never a contract — the authoritative edge of every single
            // WM_SIZING is that message's own wParam, and the client cross-checks the two (plan §1.2).
            _cycleActive = true;
            _cycleEdge = _lastSizingHit;
            _cycleAccepted = false;

            if (!_owner._nativeApi.GetWindowRect(hwnd, out var windowRect))
            {
                _owner.Log.Warn("NativePanelSizingService", "EnterSizeMoveGetWindowRectFailed");
                return;
            }

            var dpiScale = _owner._nativeApi.GetDpiScaleForWindow(hwnd);

            // Fail-safe ordering: the cycle counts as accepted only after the client says so, so a
            // throwing client leaves it rejected instead of half-started.
            if (_client.SizeMoveEntered(_cycleEdge, windowRect, dpiScale))
                _cycleAccepted = true;
        }

        private IntPtr OnSizing(IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Not our cycle (client refused it, or WM_SIZING arrived without WM_ENTERSIZEMOVE, e.g. a
            // programmatic SetWindowPos): do not touch the rectangle at all.
            if (!_cycleActive || !_cycleAccepted) return IntPtr.Zero;

            var edge = EdgeFromWmsz((int)wParam);

            // Undocumented value: keep the gesture alive on the remembered edge rather than reporting
            // None, which the client would have to treat as a broken gesture.
            if (edge == NativeSizingEdge.None) edge = _cycleEdge;

            var proposed = _owner._nativeApi.ReadRect(lParam);
            var adjusted = _client.ProposeBounds(edge, proposed);
            _owner._nativeApi.WriteRect(lParam, adjusted);

            handled = true;
            return new IntPtr(MessageHandled);
        }

        private void OnExitSizeMove(IntPtr hwnd)
        {
            if (!EndCycle(out var accepted) || !accepted) return;

            if (_owner._nativeApi.GetWindowRect(hwnd, out var finalRect))
            {
                _client.SizeMoveExited(finalRect);
                return;
            }

            // No trustworthy geometry at the end of the gesture (window already gone). Reporting an
            // all-zero rectangle would invite the client to persist it, so this is reported as an abort:
            // the session still ends, nothing bogus can be saved.
            _owner.Log.Warn("NativePanelSizingService", "ExitSizeMoveGetWindowRectFailed");
            _client.SizeMoveAborted();
        }

        private void OnCaptureChanged(IntPtr lParam)
        {
            // lParam is the window GAINING capture. The system sizing loop releases capture itself right
            // before WM_EXITSIZEMOVE (lParam == 0), and may capture on our own behalf when the loop
            // starts (lParam == this window) — treating either as "capture stolen" would abort every
            // normal resize. Only a foreign window taking over (menu, Alt+Tab, Explorer restart) is one.
            // MANUAL VERIFY (Win10/Win11): plan §6.2 row 28.
            if (lParam == IntPtr.Zero || lParam == _hwnd) return;

            if (!EndCycle(out var accepted) || !accepted) return;

            _client.SizeMoveAborted();
        }

        private void OnDpiChanged(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            double newScale;
            NativeRectPx suggestedRect;

            if (msg == WmDpiChanged && lParam != IntPtr.Zero)
            {
                // WM_DPICHANGED: HIWORD(wParam) is the new Y-axis DPI (identical to the X-axis one),
                // lParam points at the suggested window rectangle.
                var dpi = unchecked((ushort)((wParam.ToInt64() >> 16) & 0xFFFF));
                newScale = dpi > 0 ? DesktopScaleModel.ScaleFromDpi(dpi) : _owner._nativeApi.GetDpiScaleForWindow(hwnd);
                suggestedRect = _owner._nativeApi.ReadRect(lParam);
            }
            else
            {
                // WM_DPICHANGED_AFTERPARENT carries NO payload: its wParam/lParam are unused, so reading
                // a RECT out of lParam there would dereference a garbage pointer. The child has to ask
                // for both values itself.
                newScale = _owner._nativeApi.GetDpiScaleForWindow(hwnd);
                _owner._nativeApi.GetWindowRect(hwnd, out suggestedRect);
            }

            // handled stays false: geometry of a DPI change is the system's business (D-6), this path is
            // observational only. MANUAL VERIFY (Win10/Win11): R-7 — a WorkerW-embedded child may never
            // receive either message; IMonitorService.MonitorsChanged is the source of truth.
            _client.DpiChanged(newScale, suggestedRect);
        }

        /// <summary>
        /// Closes the current cycle exactly once.
        /// </summary>
        /// <param name="accepted">Whether the client had accepted the cycle on <c>WM_ENTERSIZEMOVE</c>.</param>
        /// <returns><see langword="true"/> when a cycle was running (so a terminal notification is due).</returns>
        private bool EndCycle(out bool accepted)
        {
            accepted = _cycleAccepted;
            var wasActive = _cycleActive;

            _cycleActive = false;
            _cycleAccepted = false;
            _cycleEdge = NativeSizingEdge.None;
            _lastSizingHit = NativeSizingEdge.None;

            return wasActive;
        }
    }

    /// <summary>Token returned when nothing was hooked; disposing it must be harmless.</summary>
    private sealed class NoOpToken : IDisposable
    {
        internal static readonly NoOpToken Instance = new();

        public void Dispose()
        {
            // Nothing was installed.
        }
    }

    /// <summary>Removes the hook from the <see cref="HwndSource"/> it was added to. Idempotent.</summary>
    private sealed class HookRegistration(HwndSource source, HwndSourceHook hook) : IDisposable
    {
        private bool _removed;

        public void Dispose()
        {
            if (_removed) return;
            _removed = true;

            // RemoveHook on an already-disposed HwndSource is a no-op, but a disposed source can throw
            // on access; the token must stay safe to dispose from OnClosing either way.
            try
            {
                source.RemoveHook(hook);
            }
            catch (Exception ex)
            {
                AppLog.Current.Error("NativePanelSizingService", "RemoveHookFailed", ex);
            }
        }
    }
}
