using System.Diagnostics;
using System.Globalization;
using DesktopOrganizer.Services.Abstractions;
using Microsoft.Win32;

namespace DesktopOrganizer.Services.Tools;

/// <inheritdoc cref="IVirtualDesktopService"/>
/// <remarks>
/// <para>
/// Uses only the <b>documented</b> shell interface <c>IVirtualDesktopManager</c>
/// (<c>shobjidl_core.h</c>, CLSID <c>AA509086-5CA9-4C25-8F95-589D3C07B48A</c>,
/// IID <c>A5CD92FF-29BE-454C-8D04-D82879FB3F1B</c>), whose IID has been stable since
/// Windows 10 1607. The undocumented <c>IVirtualDesktopPinnedApps</c> (true per-window pinning)
/// is deliberately NOT used: its IID changes between Windows builds, which would require a
/// build-to-IID table and would break on OS updates. See ADR-016.
/// </para>
/// <para>
/// COM activation is intentionally kept inside this class rather than behind <c>INativeApi</c>:
/// the project invariant "P/Invoke only behind INativeApi" covers P/Invoke, and this type itself
/// (via <see cref="IVirtualDesktopService"/>) is the abstraction boundary for the COM object.
/// The one plain P/Invoke this class needs — <c>GetForegroundWindow</c> — does go through
/// <see cref="INativeApi"/>.
/// </para>
/// <para>
/// Everything degrades silently. A system without virtual desktop support, a failing HRESULT or
/// an HWND the shell does not track must never produce an exception on the UI thread.
/// </para>
/// <para>
/// <b>Threading.</b> Shell COM is apartment-threaded, so every public member must be called from an
/// STA thread — the same contract <c>NativeApi.ShowShellContextMenu</c> enforces (ADR-008). The
/// contract is asserted in Debug builds. In addition, the whole COM call is made under
/// <c>_gate</c>: the RCW must not be dropped by a concurrent <see cref="Dispose"/> while a call is
/// in flight.
/// </para>
/// </remarks>
internal sealed class VirtualDesktopService(INativeApi nativeApi) : IVirtualDesktopService, IDisposable
{
    /// <summary>Explorer's virtual desktop key. Undocumented, read-only.</summary>
    private const string VirtualDesktopsKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <summary>Per-session variant of the key; holds the live "current desktop" on Win11 22H2+.</summary>
    private const string SessionVirtualDesktopsKeyFormat =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{0}\VirtualDesktops";

    /// <summary>REG_BINARY, N*16 bytes: the desktop GUIDs, in shell order.</summary>
    private const string DesktopOrderValueName = "VirtualDesktopIDs";

    /// <summary>REG_BINARY, 16 bytes: the GUID of the active desktop.</summary>
    private const string CurrentDesktopValueName = "CurrentVirtualDesktop";

    private const int GuidByteCount = 16;

    /// <summary>CLSID_VirtualDesktopManager.</summary>
    private static readonly Guid VirtualDesktopManagerClsid = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private readonly object _gate = new();

    private IVirtualDesktopManager? _manager;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// The documented virtual desktop manager interface. Declared with <see cref="PreserveSigAttribute"/>
    /// so failing HRESULTs come back as return values instead of exceptions.
    /// </summary>
    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        /// <summary>Indicates whether the provided top-level window is on the active virtual desktop.</summary>
        /// <param name="topLevelWindow">Handle of the top-level window to query.</param>
        /// <param name="onCurrentDesktop">Receives a non-zero value when the window is on the active virtual desktop.</param>
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

        /// <summary>Retrieves the identifier of the virtual desktop hosting the provided top-level window.</summary>
        /// <param name="topLevelWindow">Handle of the top-level window to query.</param>
        /// <param name="desktopId">Receives the identifier of the virtual desktop hosting the window.</param>
        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        /// <summary>Moves the provided top-level window to the specified virtual desktop.</summary>
        /// <param name="topLevelWindow">Handle of the top-level window to move.</param>
        /// <param name="desktopId">Identifier of the destination virtual desktop.</param>
        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    /// <inheritdoc />
    public bool IsSupported
    {
        get
        {
            AssertSta();
            lock (_gate)
            {
                return GetManagerLocked() is not null;
            }
        }
    }

    /// <inheritdoc />
    public bool IsWindowOnCurrentDesktop(IntPtr hwnd)
    {
        AssertSta();

        if (hwnd == IntPtr.Zero) return true;

        // The COM call itself runs under the gate: releasing the RCW from a concurrent Dispose()
        // while the call is in flight would be a use-after-free.
        lock (_gate)
        {
            var manager = GetManagerLocked();
            if (manager is null) return true;

            try
            {
                // A non-zero HRESULT means the shell does not track this window (for example a
                // window re-parented into the desktop layer) — report "on the current desktop" so
                // callers leave it alone.
                return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var onCurrent) != 0 || onCurrent != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <inheritdoc />
    public bool TryMoveWindowToCurrentDesktop(IntPtr hwnd)
    {
        AssertSta();

        if (hwnd == IntPtr.Zero) return false;

        // Composed from the two primitives below rather than duplicating the COM calls. The gate is
        // therefore taken twice instead of once; that is deliberate and safe. Monitor is reentrant,
        // and if Dispose lands in between, the second call simply finds no manager and answers
        // false — the operation degrades, which is exactly the documented contract.
        var current = TryGetCurrentDesktopId();
        return current is not null && TryMoveWindowToDesktop(hwnd, current.Value);
    }

    /// <inheritdoc />
    public Guid? TryGetWindowDesktopId(IntPtr hwnd)
    {
        AssertSta();

        if (hwnd == IntPtr.Zero) return null;

        lock (_gate)
        {
            var manager = GetManagerLocked();
            if (manager is null) return null;

            try
            {
                // Two distinct "not tracked" answers exist and both are normalised to null:
                // TYPE_E_ELEMENTNOTFOUND (0x8002802B) for a window the shell never registered, and
                // S_OK + GUID_NULL for one it registered and then lost (a window re-parented into
                // the desktop layer, or one that already carried WS_EX_TOOLWINDOW/WS_EX_NOACTIVATE
                // when it was first shown — see ADR-019 §R0.5). Neither is a usable desktop id.
                if (manager.GetWindowDesktopId(hwnd, out var desktopId) != 0) return null;

                return desktopId == Guid.Empty ? null : desktopId;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public bool TryMoveWindowToDesktop(IntPtr hwnd, Guid desktopId)
    {
        AssertSta();

        if (hwnd == IntPtr.Zero || desktopId == Guid.Empty) return false;

        lock (_gate)
        {
            var manager = GetManagerLocked();
            if (manager is null) return false;

            try
            {
                // Copied into a local: the COM signature takes the id by ref, and handing it the
                // parameter itself would let a misbehaving callee mutate the caller's value.
                var target = desktopId;
                return manager.MoveWindowToDesktop(hwnd, ref target) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public Guid? TryGetDefaultDesktopId()
    {
        AssertSta();

        try
        {
            var order = ReadDesktopOrderFromRegistry();
            if (order is null || order.Length == 0) return null;

            var registryCurrent = ReadCurrentDesktopFromRegistry();
            if (registryCurrent is null) return null;

            // The documented answer, used purely as a yardstick for the undocumented data.
            var documentedCurrent = TryGetCurrentDesktopId();
            if (documentedCurrent is null) return null;

            // Cross-validation. Both halves must hold, otherwise this build stores something other
            // than what we assume and the whole probe is discarded rather than half-trusted:
            //   1. the registry's idea of "current" agrees with the shell's;
            //   2. the registry's desktop list actually contains that desktop.
            if (registryCurrent.Value != documentedCurrent.Value) return null;
            if (Array.IndexOf(order, documentedCurrent.Value) < 0) return null;

            return order[0];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drops the cached COM object. Safe to call more than once, and safe to call concurrently with
    /// an in-flight call.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> call <see cref="Marshal.FinalReleaseComObject"/>: that forcibly
    /// kills the RCW, and any reference another thread still holds would turn into an
    /// <see cref="InvalidComObjectException"/> at the worst possible moment. Dropping the field and
    /// letting the RCW finalizer release the underlying interface makes the race safe by
    /// construction; the manager is a single cheap shell object, so there is nothing to reclaim
    /// eagerly.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _manager = null;

            // Keep _initialized set so a call arriving after Dispose does not re-activate the
            // COM object; it simply degrades to "unsupported".
            _initialized = true;
        }
    }

    /// <summary>
    /// Shell COM is apartment-threaded (ADR-008): calling it from a pool/MTA thread would silently
    /// marshal through a proxy or fail. Debug-only, exactly like
    /// <c>NativeApi.ShowShellContextMenu</c>.
    /// </summary>
    private static void AssertSta() =>
        System.Diagnostics.Debug.Assert(
            System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA,
            "VirtualDesktopService must be called on an STA thread.");

    private static void ReleaseQuietly(object comObject)
    {
        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // Releasing a dead RCW must not take the app down.
        }
    }

    // ── Undocumented, read-only registry probe (see ADR-019 §R2) ─────────────────────────────────
    //
    // Explorer keeps the ordered list of virtual desktops in HKCU. There is no documented way to
    // enumerate desktops at all, so this is the only source for "which one is Desktop 1".
    // (The key/value names this block reads are the registry constants at the top of the class.)
    //
    // Everything here is READ-ONLY and nothing here is trusted on its own: TryGetDefaultDesktopId
    // above validates the result against the documented IVirtualDesktopManager before using it, and
    // discards it entirely on any disagreement. That is what separates this from the rejected
    // undocumented-COM option (ADR-016 §E-B): no floating IID, no build-to-IID table, no mutation of
    // shell state, and a wrong assumption is *detected* rather than acted upon.

    /// <summary>
    /// Reads the ordered desktop list, or <see langword="null"/> when the value is missing, empty or
    /// not a whole number of GUIDs.
    /// </summary>
    /// <remarks>
    /// The bytes are stored in the same mixed-endian layout <see cref="Guid"/> itself uses, so no
    /// reordering is needed. A payload whose length is not a multiple of 16 means the layout is not
    /// what this code assumes, which is treated as "unknown" rather than parsed optimistically.
    /// </remarks>
    private static Guid[]? ReadDesktopOrderFromRegistry()
    {
        var raw = ReadBinaryValue(VirtualDesktopsKeyPath, DesktopOrderValueName);
        if (raw is null || raw.Length == 0 || raw.Length % GuidByteCount != 0) return null;

        var order = new Guid[raw.Length / GuidByteCount];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = new Guid(raw.AsSpan(i * GuidByteCount, GuidByteCount));
        }

        return order;
    }

    /// <summary>
    /// Reads the registry's idea of the active desktop, preferring the per-session location
    /// (Win11 22H2+) and falling back to the legacy one (Win10, and Win11 builds that still mirror
    /// it there).
    /// </summary>
    private static Guid? ReadCurrentDesktopFromRegistry()
    {
        var sessionKeyPath = string.Format(
            CultureInfo.InvariantCulture,
            SessionVirtualDesktopsKeyFormat,
            GetCurrentSessionId());

        return ToGuid(ReadBinaryValue(sessionKeyPath, CurrentDesktopValueName))
               ?? ToGuid(ReadBinaryValue(VirtualDesktopsKeyPath, CurrentDesktopValueName));
    }

    /// <summary>
    /// The Terminal Services session id of this process, or <c>-1</c> when it cannot be read (which
    /// simply makes the per-session key path miss and hands over to the legacy fallback).
    /// </summary>
    private static int GetCurrentSessionId()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Reads a REG_BINARY value, answering <see langword="null"/> on anything unexpected.</summary>
    private static byte[]? ReadBinaryValue(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as byte[];
        }
        catch
        {
            // Missing key, denied access, a value of the wrong kind — all mean "no answer".
            return null;
        }
    }

    /// <summary>Converts a 16-byte payload to a GUID; any other length is rejected.</summary>
    private static Guid? ToGuid(byte[]? raw) =>
        raw is { Length: GuidByteCount } ? new Guid(raw) : null;

    /// <summary>
    /// The id of the desktop the user is currently looking at, or <see langword="null"/> when it
    /// cannot be established.
    /// </summary>
    /// <remarks>
    /// The documented API exposes no "get current desktop id" call, so it is read from the
    /// foreground window, which is by definition on the desktop the user is looking at. At logon /
    /// autostart there may be no foreground window at all, and <c>Progman</c> — a plausible
    /// foreground window on an empty desktop — is not tracked; both degrade to
    /// <see langword="null"/>.
    /// </remarks>
    private Guid? TryGetCurrentDesktopId()
    {
        try
        {
            var foreground = nativeApi.GetForegroundWindow();
            return foreground == IntPtr.Zero ? null : TryGetWindowDesktopId(foreground);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Activates the manager on first use and caches the result (including the failure case,
    /// so a system without virtual desktop support is probed only once).
    /// Must be called while holding <c>_gate</c>.
    /// </summary>
    private IVirtualDesktopManager? GetManagerLocked()
    {
        if (_initialized) return _manager;

        _initialized = true;

        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(VirtualDesktopManagerClsid);
            if (type is null) return null;

            instance = Activator.CreateInstance(type);
            _manager = instance as IVirtualDesktopManager;

            if (_manager is null && instance is not null)
            {
                // Activation succeeded but the object does not expose the interface: release the
                // orphaned __ComObject instead of leaking it until the next GC.
                ReleaseQuietly(instance);
            }
        }
        catch
        {
            // No virtual desktop support on this SKU/build — stay unsupported, stay silent.
            if (_manager is null && instance is not null)
            {
                ReleaseQuietly(instance);
            }

            _manager = null;
        }

        return _manager;
    }
}
