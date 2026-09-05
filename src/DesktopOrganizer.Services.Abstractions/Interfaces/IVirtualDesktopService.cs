namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Thin, failure-tolerant wrapper over the documented Windows virtual desktop manager
/// (<c>IVirtualDesktopManager</c>, CLSID <c>AA509086-5CA9-4C25-8F95-589D3C07B48A</c>,
/// stable since Windows 10 1607).
/// <para>
/// This is about <b>Windows virtual desktops</b> (Task View, Win+Ctrl+Left/Right) — not about
/// physical monitors; see <see cref="IMonitorService"/> for the latter.
/// </para>
/// <para>
/// Every member degrades silently: a missing COM object, a failing HRESULT or an HWND the shell
/// does not track never surfaces as an exception. Callers get a safe answer instead
/// (see the per-member contracts below).
/// </para>
/// <para>
/// <b>Thread affinity:</b> every member must be called from an <b>STA</b> thread (in this
/// application: the WPF UI thread). The implementation talks to an apartment-threaded shell COM
/// object, the same contract that applies to the shell context menu (ADR-008); calling from a
/// thread-pool/MTA thread is a contract violation and is asserted in Debug builds. Members are safe
/// to call concurrently with <c>Dispose</c> on the implementation.
/// </para>
/// </summary>
public interface IVirtualDesktopService
{
    /// <summary>
    /// Gets a value indicating whether the virtual desktop manager COM object could be activated
    /// on this system. When <see langword="false"/> the other members are inert and callers should
    /// behave exactly as if the "follow the active virtual desktop" feature were switched off.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Determines whether the given window currently lives on the active virtual desktop.
    /// </summary>
    /// <param name="hwnd">Handle of the window to test.</param>
    /// <returns>
    /// <see langword="true"/> when the window is on the active virtual desktop <b>and</b> when the
    /// answer cannot be determined (unsupported system, failing HRESULT, untracked window).
    /// Returning <see langword="true"/> on failure is deliberate: callers use this as a guard before
    /// moving the window, so an unknown state must mean "leave the window alone".
    /// </returns>
    bool IsWindowOnCurrentDesktop(IntPtr hwnd);

    /// <summary>
    /// Attempts to move the given window onto the active virtual desktop.
    /// </summary>
    /// <param name="hwnd">Handle of the window to move.</param>
    /// <returns>
    /// <see langword="true"/> when the underlying call reported success; <see langword="false"/> on
    /// any failure (unsupported system, unknown active desktop, failing HRESULT).
    /// <b>Note:</b> a <see langword="true"/> result means the API accepted the call, not that the
    /// window visibly moved — the shell silently ignores windows it does not track (for example
    /// windows re-parented into the desktop layer).
    /// </returns>
    bool TryMoveWindowToCurrentDesktop(IntPtr hwnd);

    /// <summary>
    /// Reads the id of the virtual desktop the given window lives on.
    /// </summary>
    /// <param name="hwnd">Handle of the window to query.</param>
    /// <returns>
    /// The desktop id, or <see langword="null"/> when it cannot be established: a null handle, an
    /// unsupported system, a failing HRESULT, or a window the shell does not track (which it reports
    /// either as <c>TYPE_E_ELEMENTNOTFOUND</c> or as <c>S_OK</c> plus <c>GUID_NULL</c> — both are
    /// normalised to <see langword="null"/> here, because neither is an id you can move a window to).
    /// </returns>
    /// <remarks>
    /// A window is only tracked once it has been <b>shown</b>, and only if it did not already carry
    /// <c>WS_EX_TOOLWINDOW</c>/<c>WS_EX_NOACTIVATE</c> at that moment — see the ordering contract on
    /// <see cref="IDesktopIntegrationService.ApplyDesktopWindowStyles"/>. Querying before
    /// <c>Show()</c> legitimately yields <see langword="null"/>.
    /// </remarks>
    Guid? TryGetWindowDesktopId(IntPtr hwnd);

    /// <summary>
    /// Attempts to move the given window onto a specific virtual desktop.
    /// </summary>
    /// <param name="hwnd">Handle of the window to move. Must belong to the calling process.</param>
    /// <param name="desktopId">Id of the target virtual desktop.</param>
    /// <returns>
    /// <see langword="true"/> when the underlying call reported success; <see langword="false"/> on
    /// any failure, on a null handle and on an empty <paramref name="desktopId"/>.
    /// <b>Note:</b> exactly as with <see cref="TryMoveWindowToCurrentDesktop"/>, a
    /// <see langword="true"/> result means the API accepted the call, not that the window visibly
    /// moved — for an untracked window the shell returns <c>S_OK</c> and does nothing (ADR-016
    /// §E0.2). Verify with <see cref="TryGetWindowDesktopId"/> when the effect matters.
    /// </returns>
    bool TryMoveWindowToDesktop(IntPtr hwnd, Guid desktopId);

    /// <summary>
    /// Best-effort id of the <b>first</b> virtual desktop in shell order — what the user calls
    /// "Desktop 1".
    /// </summary>
    /// <returns>
    /// The id of the first desktop, or <see langword="null"/> whenever it cannot be established
    /// trustworthily. <see langword="null"/> means "do not guess": callers must fall back to another
    /// source rather than pick an arbitrary desktop.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The documented <c>IVirtualDesktopManager</c> exposes no desktop enumeration at all, so this
    /// is answered from a <b>read-only</b> probe of an undocumented Explorer registry value, and it
    /// is <b>cross-validated against the documented API</b> before being trusted: the registry's
    /// notion of the current desktop must agree with <c>GetWindowDesktopId</c>, and the registry's
    /// desktop list must contain that desktop. Any mismatch, missing value or malformed payload
    /// yields <see langword="null"/>. The undocumented data is thus never trusted blindly, and a
    /// future change in its layout degrades instead of misbehaving (ADR-019 §R2).
    /// </para>
    /// <para>
    /// <b>Never call this from a poll timer.</b> It reads the registry and makes a COM call; it is
    /// meant for rare events (opening the panel, toggling the setting). The result is deliberately
    /// not cached — the user can reorder desktops at any time.
    /// </para>
    /// </remarks>
    Guid? TryGetDefaultDesktopId();
}
