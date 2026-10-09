namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Desktop-integration primitives for application windows: the extended window styles that keep a
/// window out of the taskbar and out of the focus chain, re-parenting a window into the desktop
/// layer (below regular app windows, above the wallpaper) via the Progman/WorkerW technique, and
/// parking a plain top-level window in the Z-order slot directly above the shell desktop window.
/// <para>
/// The concerns are deliberately separate members. Fence panels (<c>ChildWindow</c>) want the styles
/// plus desktop-layer parenting and call <see cref="EmbedInDesktop"/>; the system icons sidebar
/// wants the styles only (<see cref="ApplyDesktopWindowStyles"/>) — because <c>WorkerW</c> is a
/// single layer shared by every Windows virtual desktop and anything parented into it is
/// unconditionally visible on all of them (ADR-019) — and re-asserts its behind-everything placement
/// on a timer through <see cref="SendToBackOfZOrder"/> instead. Where no <c>WorkerW</c> exists (Windows 11
/// 24H2+, ADR-034) fence panels stay top-level and are held in the desktop band by
/// <see cref="ConstrainWindowPosChangingToDesktopBand"/> + <see cref="KeepInDesktopBand"/> — no timer.
/// </para>
/// </summary>
public interface IDesktopIntegrationService
{
    /// <summary>
    /// Embeds the window identified by <paramref name="hwnd"/> into the desktop layer
    /// and removes it from the taskbar and Alt+Tab.
    /// </summary>
    /// <param name="hwnd">Handle of the window to embed.</param>
    /// <returns>
    /// Where the window ended up — see <see cref="DesktopEmbedResult"/>. Callers that only need the
    /// side effect (panel creation) may ignore it; shell-restart recovery (ADR-034) uses it to tell a
    /// not-yet-ready <c>WorkerW</c> (retry) from the Windows 11 24H2+ top-level layout (stop retrying).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Equivalent to <see cref="ApplyDesktopWindowStyles"/> followed by re-parenting into
    /// <c>WorkerW</c>. The styles are applied unconditionally, even when the <c>WorkerW</c> lookup
    /// fails, so the taskbar/Alt+Tab guarantee does not depend on the shell trick succeeding.
    /// </para>
    /// <para>
    /// Idempotent: when the window's parent already is the current <c>WorkerW</c>, <c>SetParent</c> is
    /// skipped (<see cref="DesktopEmbedResult.AlreadyEmbedded"/>), so recovery can call this repeatedly
    /// on every surviving panel without re-parenting churn.
    /// </para>
    /// </remarks>
    DesktopEmbedResult EmbedInDesktop(IntPtr hwnd);

    /// <summary>
    /// Returns whether <paramref name="hwnd"/> still names a live window (<c>IsWindow</c>).
    /// </summary>
    /// <param name="hwnd">Handle to test. <see cref="IntPtr.Zero"/> answers <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the window exists. Never throws.</returns>
    /// <remarks>
    /// Used by shell-restart recovery (ADR-034) to tell a panel whose HWND survived the restart (re-embed
    /// it) from one that was destroyed together with its dead <c>WorkerW</c> parent (rebuild it from its
    /// model). A handle value can in theory be recycled by the system for an unrelated window; callers
    /// combine this with their own bookkeeping rather than trusting it alone.
    /// </remarks>
    bool IsWindowAlive(IntPtr hwnd);

    /// <summary>
    /// Runs the same <c>WorkerW</c> lookup <see cref="EmbedInDesktop"/> uses, without touching any window
    /// of ours (no styles, no <c>SetParent</c>), and returns what it found.
    /// </summary>
    /// <returns>
    /// The top-level <c>WorkerW</c> panels would be embedded into (classic layout: Windows 10, Windows 11 up
    /// to 23H2), or <see cref="IntPtr.Zero"/> when there is none (Windows 11 24H2+, or Explorer not ready).
    /// Never throws.
    /// </returns>
    /// <remarks>
    /// Used by <see cref="IDesktopScaleProvider"/> to <i>predict</i> the <see cref="DesktopLayerMode"/> (and the
    /// DPI of the layer) before any panel exists; once a panel was embedded, its real root window wins.
    /// Like <see cref="EmbedInDesktop"/> it sends <c>0x052C</c> to <c>Progman</c> and enumerates top-level
    /// windows, so it is not meant for hot paths.
    /// </remarks>
    IntPtr FindDesktopLayerHost();

    /// <summary>
    /// Applies the desktop-window extended styles (<c>WS_EX_TOOLWINDOW</c> | <c>WS_EX_NOACTIVATE</c>)
    /// <b>without</b> re-parenting the window into the <c>WorkerW</c> desktop layer.
    /// </summary>
    /// <param name="hwnd">Handle of the window to style. Existing extended style bits are preserved.</param>
    /// <remarks>
    /// <para>
    /// Touches nothing but the target window: no <c>0x052C</c> to <c>Progman</c>, no
    /// <c>EnumWindows</c>, no <c>SetParent</c>. Cheap and idempotent.
    /// </para>
    /// <para>
    /// <b>Call this AFTER <c>Show()</c>, never from <c>OnSourceInitialized</c>.</b> Measured on
    /// Windows 11 build 10.0.26200 (ADR-019 §R0.5): a top-level window that already carries
    /// <c>WS_EX_TOOLWINDOW</c> or <c>WS_EX_NOACTIVATE</c> at the moment it is first shown is never
    /// registered with the shell's virtual desktop manager — <c>GetWindowDesktopId</c> answers
    /// <c>S_OK</c> + <c>GUID_NULL</c> forever after, and <c>MoveWindowToDesktop</c> becomes a silent
    /// no-op. Applying the very same bits one instruction after <c>Show()</c> keeps the window
    /// tracked and movable. Callers that care about virtual desktop placement (see
    /// <see cref="IVirtualDesktopService"/>) therefore must not apply these styles early.
    /// </para>
    /// </remarks>
    void ApplyDesktopWindowStyles(IntPtr hwnd);

    /// <summary>
    /// Places the window identified by <paramref name="hwnd"/> directly above the shell desktop
    /// window (<c>Progman</c>) in the Z-order, so it sits above the wallpaper and the desktop icons
    /// but behind every ordinary application window. Position, size, activation, focus and the
    /// window's owner are all left untouched.
    /// </summary>
    /// <param name="hwnd">Handle of the window to push back. <see cref="IntPtr.Zero"/> is a no-op.</param>
    /// <remarks>
    /// <para>
    /// Replaces the <c>Topmost="True"</c> trade-off originally accepted in ADR-019: the system icons
    /// sidebar is no longer a <c>WorkerW</c> child (which used to give it its "behind everything"
    /// placement for free), so the desired back-of-the-stack position has to be re-asserted by hand.
    /// </para>
    /// <para>
    /// The anchor is <c>Progman</c> and not the <c>HWND_BOTTOM</c> pseudo-handle, for the reason
    /// ADR-001 gave when it rejected exactly that shortcut: <c>HWND_BOTTOM</c> only guarantees "below
    /// every other window", which is not the same as "above the wallpaper" — <c>Progman</c> renders
    /// the wallpaper and parents the desktop icons, and it shares that same bottom band. Anchoring to
    /// it explicitly makes the resulting layer a property of the call instead of a race.
    /// <c>HWND_BOTTOM</c> survives only as the fallback for a missing <c>Progman</c>.
    /// </para>
    /// <para>
    /// <b>A single call is not enough.</b> Windows re-shuffles the Z-order of ordinary top-level
    /// windows on every activation, so callers must repeat this periodically for as long as the
    /// window is alive (see <c>SystemIconsPanelManager</c>'s Z-order timer). The call stays cheap: it
    /// resolves the anchor with a <c>FindWindow</c> + <c>GetWindow</c> pair (no <c>0x052C</c> message,
    /// no <c>EnumWindows</c>, no <c>SetParent</c> — nothing that leaves the process synchronously) and
    /// skips the <c>SetWindowPos</c> entirely once the window already holds the slot, which is the
    /// steady state.
    /// </para>
    /// <para>Degrades silently: any failure is swallowed, nothing is thrown at the UI thread.</para>
    /// <para>
    /// <b>Manual verify (Win10/Win11).</b> Handle-level Z-order is asserted by tests and was measured
    /// on Windows 11 build 10.0.26200, but "one slot above <c>Progman</c>" is not by itself proof that
    /// the panel is <i>painted</i> above the wallpaper — that has to be confirmed with human eyes, on
    /// both Windows 10 and Windows 11, and with a third-party wallpaper engine running (those tools
    /// insert their own renderer into the same band).
    /// </para>
    /// </remarks>
    void SendToBackOfZOrder(IntPtr hwnd);

    /// <summary>
    /// Fence-panel counterpart of <see cref="SendToBackOfZOrder"/> for the Windows 11 24H2+ layout ("mode T",
    /// ADR-034/ADR-037): when — and only when — <paramref name="hwnd"/> is really a top-level window
    /// (<c>GetAncestor(GA_ROOT) == hwnd</c>, decided per call from the fact, never from the OS version), moves it
    /// to the top of the <b>desktop band</b>: directly below the first foreign application window above
    /// <c>Progman</c>. A panel embedded into <c>WorkerW</c> ("mode E") is left completely untouched.
    /// </summary>
    /// <param name="hwnd">Handle of the panel window. <see cref="IntPtr.Zero"/> is a no-op.</param>
    /// <remarks>
    /// <para>
    /// The desktop band is the contiguous run of windows directly above <c>Progman</c> that belong to the
    /// desktop layer: Explorer's <c>WorkerW</c> siblings and this process's own desktop windows (fence panels
    /// and the system icons sidebar — <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c>). A panel already inside
    /// that run is left where it is (zero <c>SetWindowPos</c> calls), which is what keeps N panels and the
    /// sidebar's own one-slot-above-<c>Progman</c> timer (ADR-020) from endlessly swapping places.
    /// </para>
    /// <para>
    /// Event-driven, not periodic: called after a panel is shown and after shell-restart recovery; the
    /// in-between protection is <see cref="ConstrainWindowPosChangingToDesktopBand"/>. Same flags as
    /// <see cref="SendToBackOfZOrder"/> (<c>SWP_NOACTIVATE | SWP_NOOWNERZORDER</c>). Never throws.
    /// </para>
    /// </remarks>
    void KeepInDesktopBand(IntPtr hwnd);

    /// <summary>
    /// <c>WM_WINDOWPOSCHANGING</c> filter for a fence panel (ADR-034 amendment 2026-10-09): when a top-level
    /// panel is about to be raised (any Z-order change, or being shown), rewrites the proposed
    /// <c>hwndInsertAfter</c> in place so the panel lands at the top of the desktop band instead of on top of
    /// the application windows — or cancels the Z-order part (<c>SWP_NOZORDER</c>) when the panel already is
    /// inside the band. Position, size, activation and visibility of the request are never changed.
    /// </summary>
    /// <param name="hwnd">The panel window receiving the message.</param>
    /// <param name="windowPos">The message's <c>lParam</c> (pointer to <c>WINDOWPOS</c>).</param>
    /// <remarks>
    /// Runs inside the panel's window procedure: requests that carry <c>SWP_NOZORDER</c> and no
    /// <c>SWP_SHOWWINDOW</c> (every move/resize frame) return after one structure read, hiding requests are
    /// ignored, and a <c>WorkerW</c>-child panel (mode E) is never touched. Only cheap, message-free reads are
    /// made (<c>FindWindow</c>, <c>GetWindow</c>, <c>GetWindowLongPtr</c>, <c>GetClassName</c>,
    /// <c>GetWindowThreadProcessId</c>). Never throws.
    /// </remarks>
    void ConstrainWindowPosChangingToDesktopBand(IntPtr hwnd, IntPtr windowPos);
}
