using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.Services.Diagnostics;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc />
/// <param name="nativeApi">Native API façade (all P/Invoke lives behind it).</param>
/// <param name="appLog">
/// Log for the rare "Explorer did not answer" diagnostic; falls back to the ambient
/// <see cref="AppLog.Current"/> when <see langword="null"/>.
/// </param>
internal class DesktopIntegrationService(INativeApi nativeApi, IAppLog? appLog = null) : IDesktopIntegrationService
{
    /// <summary>
    /// Upper bound, in milliseconds, on how long the calling (UI) thread waits for Explorer to answer the
    /// <c>0x052C</c> WorkerW-spawn message. A hung Explorer (not pumping for ~5 s) is abandoned at once
    /// by <c>SMTO_ABORTIFHUNG</c>; this caps a merely slow one. Either way the lookup answers "no host".
    /// </summary>
    internal const uint ProgmanMsgTimeoutMs = 1000;

    /// <summary>
    /// Upper bound on how many windows <see cref="ResolveDesktopBandPlacement"/> walks up from <c>Progman</c>
    /// (ADR-034 amendment 2026-10-09). Unlike <see cref="MaxDesktopBandWalk"/> the run legitimately contains
    /// every fence panel of this process — visible and hidden (other pages) — plus the sidebar, so the cap is
    /// generous; it exists only so a corrupted/racing sibling list can never loop unboundedly inside a
    /// window procedure. Exceeding it answers <see cref="DesktopBandPlacementKind.WalkCapExceeded"/>, which leaves
    /// the Z-order alone and is logged once per session (ADR-audit review cycle 1, G2).
    /// </summary>
    internal const int MaxPanelBandWalk = 512;

    private const uint ProgmanMsg = 0x052C;
    private const int GwlExStyle = -20;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    /// <summary><c>SWP_NOZORDER</c> — the request does not change the Z-order.</summary>
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary><c>SWP_SHOWWINDOW</c> — the request shows the window (<c>ShowWindow</c> goes through it).</summary>
    private const uint SwpShowWindow = 0x0040;

    /// <summary><c>SWP_HIDEWINDOW</c> — the request hides the window; its Z-order is irrelevant then.</summary>
    private const uint SwpHideWindow = 0x0080;

    /// <summary><c>WS_EX_TOPMOST</c> — the start of the always-on-top run ends the desktop-band walk.</summary>
    private const uint WsExTopmost = 0x00000008;

    /// <summary>
    /// <c>SWP_NOOWNERZORDER</c> — do not reorder the owner window. WPF implements
    /// <c>ShowInTaskbar="False"</c> with a hidden owner window, and without this flag every
    /// <see cref="SendToBackOfZOrder"/> call drags that owner down into the bottom band along with
    /// the panel. Measured on Windows 11 build 10.0.26200: without the flag the owner lands directly
    /// above <c>Progman</c> and pushes the panel one slot higher than intended, and combined with the
    /// <see cref="ResolveInsertAfterForBottom"/> lookup below the two windows then swap places on
    /// every tick (an endless Z-order churn 3.3 times a second). With the flag the owner is left
    /// exactly where it was and the panel lands deterministically one slot above <c>Progman</c>.
    /// </summary>
    private const uint SwpNoOwnerZOrder = 0x0200;

    /// <summary>
    /// <c>GW_HWNDPREV</c> for <c>GetWindow</c>: the sibling immediately <b>above</b> the given window
    /// in the Z-order (the sibling list is ordered top-to-bottom, so "previous" means higher).
    /// </summary>
    private const int GwHwndPrev = 3;

    /// <summary>
    /// Class name of the wallpaper/desktop-band windows Explorer keeps next to <c>Progman</c>.
    /// </summary>
    private const string WorkerWClass = "WorkerW";

    /// <summary>
    /// Upper bound on how many consecutive <c>WorkerW</c> siblings
    /// <see cref="ResolveInsertAfterForBottom"/> will walk past before giving up. Explorer keeps at
    /// most a couple of them, so the loop normally exits on its first or second iteration; the cap
    /// exists purely so a corrupted/racing sibling list (Explorer restarting mid-walk) can never turn
    /// this timer-driven lookup into an unbounded loop on the UI thread.
    /// </summary>
    private const int MaxDesktopBandWalk = 8;

    /// <summary>
    /// <c>HWND_TOP</c> — for a non-topmost window: the top of the non-topmost run. Used when everything
    /// non-topmost above <c>Progman</c> already belongs to the desktop band.
    /// </summary>
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    /// <summary>This process's id — the desktop band contains only OUR desktop windows (plus Explorer's <c>WorkerW</c>).</summary>
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    // SetWindowPos: place the window just above the shell desktop window, changing nothing else.
    private static readonly IntPtr HwndBottom = new (1);

    /// <summary>
    /// Once-per-session gate for the Z-order diagnostics (ADR-audit review cycle 1, G2; ADR-034 amendment
    /// 2026-10-09): every swallowed failure and every "no decision" anomaly is written once — never per tick or
    /// per window message. The service is a DI singleton, so its lifetime is the session.
    /// </summary>
    private readonly AppLogEventThrottle _onceLog = new (limitPerEvent: 1, announceSuppression: false);

    /// <summary>
    /// <see cref="DeferredBandDiagnostics"/> bits recorded inside the <c>WM_WINDOWPOSCHANGING</c> filter, which
    /// must not write a log entry itself; written by the next <see cref="KeepInDesktopBand"/> call (UI thread,
    /// outside any window procedure).
    /// </summary>
    private int _deferredBandDiagnostics;

    /// <summary>The last exception the window-procedure filter swallowed, for the deferred entry.</summary>
    private Exception? _deferredFilterFailure;

    /// <summary>Anomalies the window-procedure filter records for deferred logging.</summary>
    [Flags]
    private enum DeferredBandDiagnostics
    {
        /// <summary>Nothing recorded.</summary>
        None = 0,

        /// <summary>The filter found no <c>Progman</c>.</summary>
        ProgmanMissing = 1,

        /// <summary>The filter's walk hit <see cref="MaxPanelBandWalk"/>.</summary>
        WalkCapExceeded = 2,

        /// <summary>The filter swallowed an exception (<see cref="_deferredFilterFailure"/>).</summary>
        FilterFailed = 4,
    }

    /// <inheritdoc />
    public DesktopEmbedResult EmbedInDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return DesktopEmbedResult.InvalidWindow;

        // Applied unconditionally, before the WorkerW lookup below: the WorkerW handle can fail to
        // resolve (e.g. Explorer hasn't spawned it yet, or a shell version where the Progman 0x052C
        // trick no longer creates one) without that affecting whether this window is still hidden
        // from the taskbar and Alt+Tab — the two concerns are independent, and callers rely on the
        // Alt+Tab/taskbar guarantee even when desktop-layer parenting itself isn't available.
        ApplyDesktopWindowStyles(hwnd);

        // Windows 11 24H2+ (measured on build 26200, release-hardening F0 spike): the lookup below
        // finds no top-level WorkerW behind the SHELLDLL_DefView host, so panels stay top-level there.
        // That is the expected steady state on those builds, not a failure — the caller gets
        // NoDesktopLayer and decides what it means for it (ADR-034).
        var workerW = GetOrCreateWorkerW();
        if (workerW == IntPtr.Zero) return DesktopEmbedResult.NoDesktopLayer;

        // Idempotency for shell-restart recovery (ADR-034): re-embedding a panel that already is a
        // child of the current WorkerW must not re-parent it again. GA_PARENT rather than GetParent,
        // because WPF panels are WS_POPUP windows with a hidden owner and GetParent reports the OWNER
        // of a popup, never its parent.
        if (nativeApi.GetAncestorParent(hwnd) == workerW) return DesktopEmbedResult.AlreadyEmbedded;

        // SetParent's own return value is not relied on (it only names the PREVIOUS parent): the
        // outcome is verified by re-reading the real parent with the same GA_PARENT query as above.
        nativeApi.SetParent(hwnd, workerW);
        if (nativeApi.GetAncestorParent(hwnd) == workerW) return DesktopEmbedResult.Reparented;

        // Re-parenting did not take. A window that died in between is reported as such (recovery
        // rebuilds it from its model); a live one simply stayed top-level — exactly the NoDesktopLayer
        // situation, so the caller keeps it above the desktop band instead of assuming it is embedded.
        return nativeApi.IsWindow(hwnd) ? DesktopEmbedResult.NoDesktopLayer : DesktopEmbedResult.InvalidWindow;
    }

    /// <inheritdoc />
    public bool IsWindowAlive(IntPtr hwnd)
    {
        try
        {
            return nativeApi.IsWindow(hwnd);
        }
        catch
        {
            // Same degrade-silently policy as the rest of this service; "not alive" makes the caller
            // rebuild the panel from its model, which is always safe.
            return false;
        }
    }

    /// <inheritdoc />
    public IntPtr FindDesktopLayerHost()
    {
        try
        {
            return GetOrCreateWorkerW();
        }
        catch
        {
            // Same degrade-silently policy as the rest of this service; "no layer" predicts the
            // top-level mode, which the scale provider corrects as soon as a panel reports its real root.
            return IntPtr.Zero;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Adds WS_EX_TOOLWINDOW and WS_EX_NOACTIVATE to the window's extended style:
    /// — TOOLWINDOW: removes from taskbar and Alt+Tab
    /// — NOACTIVATE: clicks on the panel don't steal focus from the active app
    /// Existing bits are preserved (read-modify-write), so the call is idempotent and safe to make
    /// on a window that has already been styled.
    /// </remarks>
    public void ApplyDesktopWindowStyles(IntPtr hwnd)
    {
        var current = (uint)nativeApi.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var updated = current | WsExToolWindow | WsExNoActivate;
        nativeApi.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr((long)updated));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One <c>SetWindowPos(hwnd, &lt;window directly above the desktop band&gt;, …, SWP_NOMOVE |
    /// SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER)</c>, preceded by a cheap <c>FindWindow</c> +
    /// <c>GetWindow</c> walk to resolve that anchor (see <see cref="ResolveInsertAfterForBottom"/>).
    /// </para>
    /// <para>
    /// <c>SWP_NOACTIVATE</c> matters as much as the Z-order argument itself: without it the call would
    /// steal activation from whatever the user is working in, once per timer tick. Failure is
    /// swallowed on purpose — this runs off a UI-thread timer, and a window that briefly stays too
    /// high in the Z-order is a cosmetic problem, not a reason to surface an exception.
    /// </para>
    /// </remarks>
    public void SendToBackOfZOrder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var insertAfter = ResolveInsertAfterForBottom();

            // Steady state: the panel already occupies the slot directly above the desktop band, so
            // the anchor lookup answers with the panel's own handle and asking Windows to "insert it
            // after itself" is both meaningless and undefined. Skipping is also what makes the 300 ms
            // timer nearly free — once the panel has settled, the overwhelmingly common tick costs a
            // couple of lookups and no Z-order write at all.
            if (insertAfter == hwnd) return;

            if (nativeApi.SetWindowPos(
                    hwnd,
                    insertAfter,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder))
            {
                return;
            }

            // The anchor is resolved by a separate lookup, so it can be destroyed in the window
            // between GetWindow and SetWindowPos (the app owning it closed at exactly that moment),
            // which makes the whole call fail and leaves the panel wherever it was — possibly on top
            // of everything. One conservative retry against HWND_BOTTOM covers that race; anything
            // beyond it is unnecessary, because the next timer tick re-resolves a fresh anchor.
            if (insertAfter != HwndBottom)
            {
                nativeApi.SetWindowPos(
                    hwnd,
                    HwndBottom,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            }
        }
        catch (Exception ex)
        {
            // Degrade — same policy as the rest of this service; logged once per session (this runs off a
            // 300 ms timer, so a persistent failure must not write an entry per tick).
            WarnFailureOnce("SendToBackFailed", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Also writes the diagnostics the <c>WM_WINDOWPOSCHANGING</c> filter
    /// (<see cref="ConstrainWindowPosChangingToDesktopBand"/>) recorded but may not log itself — each anomaly
    /// once per session.
    /// </remarks>
    public void KeepInDesktopBand(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (!IsTopLevelWindow(hwnd)) return;

            var placement = ResolveDesktopBandPlacement(hwnd);
            WarnNoDecisionOnce(placement.Kind);
            if (placement.Kind != DesktopBandPlacementKind.InsertAfter) return;

            nativeApi.SetWindowPos(
                hwnd,
                placement.InsertAfter,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }
        catch (Exception ex)
        {
            // Degrade — a panel that stays too high until the next show is cosmetic. Logged once per session.
            WarnFailureOnce("DesktopBandKeepFailed", ex);
        }
        finally
        {
            FlushDeferredBandDiagnostics();
        }
    }

    /// <inheritdoc />
    public void ConstrainWindowPosChangingToDesktopBand(IntPtr hwnd, IntPtr windowPos)
    {
        if (hwnd == IntPtr.Zero || windowPos == IntPtr.Zero) return;

        try
        {
            if (!nativeApi.TryReadWindowPosZOrder(windowPos, out var requestedInsertAfter, out var flags)) return;

            // Hot path first: move/resize frames carry SWP_NOZORDER and must cost nothing more than this read.
            if ((flags & SwpHideWindow) != 0) return;
            var changesZOrder = (flags & SwpNoZOrder) == 0;
            var shows = (flags & SwpShowWindow) != 0;
            if (!changesZOrder && !shows) return;

            // Mode E (a WorkerW child) is never touched: the decision is the fact of THIS window's root, read
            // at message time — which also covers SetParent's own internal re-positioning, made after the new
            // parent is already linked.
            if (!IsTopLevelWindow(hwnd)) return;

            var placement = ResolveDesktopBandPlacement(hwnd);
            switch (placement.Kind)
            {
                case DesktopBandPlacementKind.InBand:
                    // Already in the band: veto the Z-order half of the request (activation, ShowWindow's
                    // HWND_TOP) and keep everything else — panels never churn among themselves.
                    if (changesZOrder) nativeApi.WriteWindowPosZOrder(windowPos, requestedInsertAfter, flags | SwpNoZOrder);
                    return;

                case DesktopBandPlacementKind.InsertAfter:
                    // Already exactly what we want (e.g. KeepInDesktopBand's own SetWindowPos): leave it.
                    if (changesZOrder && requestedInsertAfter == placement.InsertAfter) return;

                    // Redirect the raise into the band. SWP_NOZORDER is cleared for a show request so the panel
                    // appears directly in its final slot (no one-frame flash on top); SWP_NOOWNERZORDER keeps
                    // WPF's hidden owner window where it is (ADR-020, SWP_NOOWNERZORDER section).
                    nativeApi.WriteWindowPosZOrder(
                        windowPos,
                        placement.InsertAfter,
                        (flags & ~SwpNoZOrder) | SwpNoOwnerZOrder);
                    return;

                default:
                    // No decision (no Progman, walk cap): leave the system's proposal alone. Inside a window
                    // procedure nothing is logged — the anomaly is only flagged for KeepInDesktopBand to write.
                    DeferNoDecision(placement.Kind);
                    return;
            }
        }
        catch (Exception ex)
        {
            // Runs inside a window procedure — never let anything escape, and never write a log entry here
            // (formatting/enqueueing on every message of a persistent failure). Two field writes; the entry is
            // written by the next KeepInDesktopBand.
            Volatile.Write(ref _deferredFilterFailure, ex);
            Interlocked.Or(ref _deferredBandDiagnostics, (int)DeferredBandDiagnostics.FilterFailed);
        }
    }

    /// <summary>
    /// Resolves the <c>hWndInsertAfter</c> argument that places a panel directly above the whole
    /// desktop band — i.e. above the wallpaper and the desktop icons, below every ordinary
    /// application window.
    /// </summary>
    /// <returns>
    /// The window to insert after, or <c>HWND_BOTTOM</c> as a fallback. May legitimately answer with
    /// the caller's own window once it occupies that slot; detecting and skipping that steady state
    /// is the caller's job (see <see cref="SendToBackOfZOrder"/>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// ADR-001 rejected a blind <c>HWND_BOTTOM</c> precisely because it only means "below every other
    /// window", which is not the same as "above the wallpaper": <c>Progman</c> — which on current
    /// Windows renders both the wallpaper and (as the parent of <c>SHELLDLL_DefView</c>) the desktop
    /// icons — lives in that same bottom band, and a window that lost the race to it would be
    /// invisible, with the timer faithfully holding it there. Anchoring to <c>Progman</c> explicitly
    /// makes the outcome a property of the call rather than of whoever reordered last.
    /// </para>
    /// <para>
    /// <c>GW_HWNDPREV</c> and not <c>GW_HWNDNEXT</c>: <c>SetWindowPos</c>'s <c>hWndInsertAfter</c> is
    /// the window that must end up <i>above</i> ours, and the sibling list runs top-to-bottom, so the
    /// window that has to stay above us while <c>Progman</c> drops below us is <c>Progman</c>'s
    /// <c>GW_HWNDPREV</c>. Passing <c>Progman</c> itself would ask for the slot <i>below</i> it.
    /// </para>
    /// <para>
    /// The walk past consecutive <c>WorkerW</c> siblings is what makes this safe across shell
    /// versions. On some Windows 10 builds Explorer keeps a real, visible <c>WorkerW</c> that paints
    /// the wallpaper <i>above</i> <c>Progman</c> in the Z-order, so a single <c>GW_HWNDPREV</c> step
    /// would answer with the wallpaper window itself and place the panel <i>under</i> the wallpaper —
    /// invisible. Worse, the next tick would then see the panel in that same slot, hit the
    /// already-correct short-circuit and confirm the broken state forever. Skipping the whole run of
    /// <c>WorkerW</c> siblings targets the top of the desktop band instead of one arbitrary member
    /// of it. On builds without those siblings (Windows 11 10.0.26200, measured) the loop exits on
    /// its first iteration and the result is identical to the single-step lookup.
    /// </para>
    /// <para>
    /// Empirically measured on Windows 11 build 10.0.26200 with an off-screen
    /// <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c> probe window: this lands the window at exactly one
    /// slot above <c>Progman</c> and stays there across repeated calls. On that build the window
    /// manager additionally clamps <c>Progman</c> to the very bottom (even an explicit
    /// <c>SetWindowPos(probe, Progman, …)</c> could not push a window below it), which is why the
    /// previous <c>HWND_BOTTOM</c> implementation happened to work there — but that clamp is
    /// undocumented shell behaviour, not a contract, so the explicit anchor stays.
    /// </para>
    /// <para>
    /// Fallbacks return <c>HWND_BOTTOM</c> rather than <c>HWND_TOP</c>: if <c>Progman</c> cannot be
    /// found, or the desktop band turns out to be the topmost run of siblings (no <c>GW_HWNDPREV</c>
    /// above it), the worst outcome of <c>HWND_BOTTOM</c> is a panel hidden behind the wallpaper,
    /// whereas <c>HWND_TOP</c> would park the panel on top of every application window the user is
    /// working in.
    /// </para>
    /// </remarks>
    private IntPtr ResolveInsertAfterForBottom()
    {
        var progman = FindProgman();
        if (progman == IntPtr.Zero) return HwndBottom;

        var band = progman;

        for (var i = 0; i < MaxDesktopBandWalk; i++)
        {
            var above = nativeApi.GetWindow(band, GwHwndPrev);
            if (above == IntPtr.Zero) return HwndBottom;

            if (!string.Equals(nativeApi.GetWindowClassName(above), WorkerWClass, StringComparison.OrdinalIgnoreCase))
                return above;

            band = above;
        }

        return HwndBottom;
    }

    /// <summary>
    /// Whether <paramref name="hwnd"/> is a top-level window right now (mode T): <c>GA_ROOT</c> is the window
    /// itself. A <c>WorkerW</c> child (mode E) has Explorer's top-level window as its root; a failed query
    /// (<see cref="IntPtr.Zero"/>) counts as "not top-level", i.e. "do nothing".
    /// </summary>
    private bool IsTopLevelWindow(IntPtr hwnd)
    {
        var root = nativeApi.GetRootWindow(hwnd);
        return root != IntPtr.Zero && root == hwnd;
    }

    /// <summary>
    /// Walks up from <c>Progman</c> through the desktop band (see <see cref="IsDesktopBandMember"/>) and decides
    /// where <paramref name="hwnd"/> belongs.
    /// </summary>
    /// <returns>
    /// <see cref="DesktopBandPlacementKind.InBand"/> when <paramref name="hwnd"/> is met inside the run (stay put);
    /// <see cref="DesktopBandPlacementKind.InsertAfter"/> with the first foreign window above the run — or
    /// <c>HWND_TOP</c> when the run reaches the always-on-top windows or the top of the list — otherwise;
    /// <see cref="DesktopBandPlacementKind.ProgmanMissing"/> / <see cref="DesktopBandPlacementKind.WalkCapExceeded"/>
    /// when <c>Progman</c> is missing or the walk hit its cap; <see cref="DesktopBandPlacementKind.Unknown"/> when
    /// <paramref name="hwnd"/> is <c>Progman</c> itself. The three "no decision" kinds leave the Z-order alone.
    /// </returns>
    /// <remarks>
    /// Landing at the TOP of the band (just below the first application window) rather than in the single
    /// slot above <c>Progman</c> is deliberate: N panels each claiming that one slot would swap places on
    /// every call (the churn ADR-020 measured for the hidden owner window) and would also fight the sidebar's
    /// 300 ms timer for it. A panel placed this way is, by construction, inside the run on the next call —
    /// idempotent.
    /// </remarks>
    private DesktopBandPlacement ResolveDesktopBandPlacement(IntPtr hwnd)
    {
        var progman = FindProgman();
        if (progman == IntPtr.Zero) return DesktopBandPlacement.ProgmanMissing;
        if (progman == hwnd) return DesktopBandPlacement.Unknown;

        var current = progman;
        for (var i = 0; i < MaxPanelBandWalk; i++)
        {
            var above = nativeApi.GetWindow(current, GwHwndPrev);
            if (above == IntPtr.Zero) return DesktopBandPlacement.At(HwndTop);
            if (above == hwnd) return DesktopBandPlacement.InBand;

            var exStyle = (uint)nativeApi.GetWindowLongPtr(above, GwlExStyle).ToInt64();
            if ((exStyle & WsExTopmost) != 0) return DesktopBandPlacement.At(HwndTop);
            if (!IsDesktopBandMember(above, exStyle)) return DesktopBandPlacement.At(above);

            current = above;
        }

        return DesktopBandPlacement.WalkCapExceeded;
    }

    /// <summary>
    /// Records a "no decision" anomaly met inside the <c>WM_WINDOWPOSCHANGING</c> filter for deferred logging —
    /// one interlocked OR, nothing else (the filter runs inside a window procedure).
    /// </summary>
    private void DeferNoDecision(DesktopBandPlacementKind kind)
    {
        var bit = kind switch
        {
            DesktopBandPlacementKind.ProgmanMissing => DeferredBandDiagnostics.ProgmanMissing,
            DesktopBandPlacementKind.WalkCapExceeded => DeferredBandDiagnostics.WalkCapExceeded,
            _ => DeferredBandDiagnostics.None,
        };

        if (bit != DeferredBandDiagnostics.None) Interlocked.Or(ref _deferredBandDiagnostics, (int)bit);
    }

    /// <summary>
    /// Writes what <see cref="ConstrainWindowPosChangingToDesktopBand"/> recorded since the last call. Called from
    /// <see cref="KeepInDesktopBand"/> only — the UI thread, outside any window procedure. Never throws.
    /// </summary>
    private void FlushDeferredBandDiagnostics()
    {
        var pending = (DeferredBandDiagnostics)Interlocked.Exchange(ref _deferredBandDiagnostics, 0);
        if (pending == DeferredBandDiagnostics.None) return;

        if ((pending & DeferredBandDiagnostics.ProgmanMissing) != 0) WarnNoDecisionOnce(DesktopBandPlacementKind.ProgmanMissing);
        if ((pending & DeferredBandDiagnostics.WalkCapExceeded) != 0) WarnNoDecisionOnce(DesktopBandPlacementKind.WalkCapExceeded);

        if ((pending & DeferredBandDiagnostics.FilterFailed) != 0)
        {
            WarnFailureOnce("DesktopBandFilterFailed", Interlocked.Exchange(ref _deferredFilterFailure, null));
        }
    }

    /// <summary>
    /// Writes a "no decision" anomaly once per session: no <c>Progman</c> (Explorer down — the panels stay
    /// wherever the system puts them until it is back) or a sibling list longer than
    /// <see cref="MaxPanelBandWalk"/> (corrupted/racing). Any other kind is ignored.
    /// </summary>
    private void WarnNoDecisionOnce(DesktopBandPlacementKind kind)
    {
        switch (kind)
        {
            case DesktopBandPlacementKind.ProgmanMissing:
                _onceLog.Warn(appLog ?? AppLog.Current, nameof(DesktopIntegrationService), "DesktopBandProgmanMissing");
                break;
            case DesktopBandPlacementKind.WalkCapExceeded:
                _onceLog.Warn(
                    appLog ?? AppLog.Current,
                    nameof(DesktopIntegrationService),
                    "DesktopBandWalkCapExceeded",
                    new KeyValuePair<string, object?>("maxWalk", MaxPanelBandWalk));
                break;
        }
    }

    /// <summary>Writes a swallowed Z-order failure once per session per <paramref name="evt"/>.</summary>
    private void WarnFailureOnce(string evt, Exception? ex)
        => _onceLog.Warn(
            appLog ?? AppLog.Current,
            nameof(DesktopIntegrationService),
            evt,
            new KeyValuePair<string, object?>("errorType", ex?.GetType().Name));

    /// <summary>
    /// Desktop-band membership: Explorer's <c>WorkerW</c> wallpaper windows, and this process's own desktop
    /// windows — fence panels and the sidebar, recognisable by the <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c>
    /// pair <see cref="ApplyDesktopWindowStyles"/> gives them. Our other windows (Settings, dialogs, WPF's
    /// hidden owner windows) do not carry <c>WS_EX_NOACTIVATE</c> and end the run, so a panel is never lifted
    /// above the Settings window.
    /// </summary>
    private bool IsDesktopBandMember(IntPtr window, uint exStyle)
    {
        const uint desktopStyles = WsExToolWindow | WsExNoActivate;
        if ((exStyle & desktopStyles) == desktopStyles && nativeApi.GetWindowProcessId(window) == OwnProcessId)
            return true;

        return string.Equals(nativeApi.GetWindowClassName(window), WorkerWClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Single lookup of the shell desktop window, shared by <see cref="GetOrCreateWorkerW"/> and
    /// <see cref="ResolveInsertAfterForBottom"/> so the class name literal exists in one place only.
    /// Deliberately does <b>not</b> cache: Explorer can be restarted at any time, after which the old
    /// handle is stale, and <c>FindWindow</c> on a fixed class name is a cheap in-kernel lookup with
    /// none of the cross-process cost of <c>GetOrCreateWorkerW</c>'s <c>SendMessage</c>/
    /// <c>EnumWindows</c> pair (which is why only this half is reused from the Z-order timer path).
    /// </summary>
    private IntPtr FindProgman() => nativeApi.FindWindow("Progman", null);

    /// <summary>
    /// Finds or creates the WorkerW window that sits between the wallpaper and desktop icons.
    /// </summary>
    /// <returns>The WorkerW, or <see cref="IntPtr.Zero"/> when there is none or Explorer did not answer.</returns>
    private IntPtr GetOrCreateWorkerW()
    {
        var progman = FindProgman();
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // Sending 0x052C to Progman makes Windows spawn a WorkerW between
        // the wallpaper and the icon layer (undocumented but widely used).
        // M9 (release-hardening review): never a plain SendMessage — this runs on the UI thread from
        // FindDesktopLayerHost on every WM_SETTINGCHANGE/WM_DISPLAYCHANGE and during Explorer-restart
        // recovery, and a hung Explorer would freeze the whole app. Unanswered = "no host": the panels
        // stay top-level for now and the next lookup tries again.
        if (!nativeApi.SendMessageTimeout(progman, ProgmanMsg, IntPtr.Zero, IntPtr.Zero, ProgmanMsgTimeoutMs, out _))
        {
            (appLog ?? AppLog.Current).Warn(
                nameof(DesktopIntegrationService),
                "ProgmanMessageTimedOut",
                new KeyValuePair<string, object?>("timeoutMs", ProgmanMsgTimeoutMs));
            return IntPtr.Zero;
        }

        IntPtr workerW = IntPtr.Zero;

        nativeApi.EnumWindows(
            (hwnd, _) =>
        {
            // WorkerW is the top-level window whose child is SHELLDLL_DefView
            var defView = nativeApi.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                workerW = nativeApi.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);

            return true; // continue enumeration
        }, IntPtr.Zero);

        return workerW;
    }
}
