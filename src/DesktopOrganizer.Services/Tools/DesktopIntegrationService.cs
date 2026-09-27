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

    private const uint ProgmanMsg = 0x052C;
    private const int GwlExStyle = -20;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

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

    // SetWindowPos: place the window just above the shell desktop window, changing nothing else.
    private static readonly IntPtr HwndBottom = new (1);

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
        catch
        {
            // Degrade silently — same policy as the rest of this service.
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
