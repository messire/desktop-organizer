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
/// on a timer through <see cref="SendToBackOfZOrder"/> instead.
/// </para>
/// </summary>
public interface IDesktopIntegrationService
{
    /// <summary>
    /// Embeds the window identified by <paramref name="hwnd"/> into the desktop layer
    /// and removes it from the taskbar and Alt+Tab.
    /// </summary>
    /// <param name="hwnd">Handle of the window to embed.</param>
    /// <remarks>
    /// Equivalent to <see cref="ApplyDesktopWindowStyles"/> followed by re-parenting into
    /// <c>WorkerW</c>. The styles are applied unconditionally, even when the <c>WorkerW</c> lookup
    /// fails, so the taskbar/Alt+Tab guarantee does not depend on the shell trick succeeding.
    /// </remarks>
    void EmbedInDesktop(IntPtr hwnd);

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
}
