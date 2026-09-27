using System.Windows;
using DesktopOrganizer.UI.Interaction;

namespace DesktopOrganizer.UI.Models;

/// <summary>
/// The single point of translation between the screen-DIP coordinate space every
/// <see cref="DesktopOrganizer.Services.Abstractions.IMonitorService"/> query
/// (<see cref="DesktopOrganizer.Services.Abstractions.MonitorInfo.Bounds"/>/<see cref="DesktopOrganizer.Services.Abstractions.MonitorInfo.WorkArea"/>)
/// is always expressed in, and the "window space" every model/persisted panel geometry lives in for a
/// panel window re-parented into <c>WorkerW</c> (<c>DesktopIntegrationService.EmbedInDesktop</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>release-hardening F redefinition (ADR-037).</b> Since F3, "window space" here means
/// <b>desktop-DIP</b> — the single px↔DIP space <c>DesktopScaleModel</c>/<c>IDesktopScaleProvider</c>
/// define (docs/PLANS/release-hardening-1.0-F-dpi.md §2), not literally <c>Window.Left</c>/<c>Top</c>'s
/// own space (which, on a mixed-DPI TopLevel desktop layer, this window's <see cref="PanelWindowCoordinates"/>
/// adapter may now translate into something else). This type's own translation — screen-DIP ↔
/// desktop-DIP, <see cref="Offset"/> — is unaffected and stays orthogonal to that px↔DIP boundary: every
/// consumer listed below already passed its result straight into <c>PanelGeometryWriter</c>/the geometry
/// adapter, which is where the desktop-DIP↔window-Left/Top translation now happens.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// <b>Open question this type isolates (ADR-028 «Открытые вопросы», Вопрос 2;
/// docs/PLANS/workspace-grid-00-baseline.md «Пространства координат» п. 3; orchestrator decision
/// OQ-1 = A, docs/PLANS/workspace-grid-03-04-integration.md §1.1/§8).</b> After
/// <c>SetParent(hwnd, WorkerW)</c> it is NOT documented whether <c>Window.Left</c>/<c>Top</c> stay in
/// the same coordinate space as <c>GetWindowRect</c> (and therefore
/// <see cref="DesktopOrganizer.Services.Abstractions.MonitorInfo"/>'s DIP rectangles), or are shifted
/// by some constant (e.g. <c>SM_XVIRTUALSCREEN</c>/<c>SM_YVIRTUALSCREEN</c> on a multi-monitor desktop
/// with a monitor placed left of or above the primary). Until MV-1 (manual verify, below) proves
/// otherwise, <see cref="Offset"/> defaults to <c>(0,0)</c> — this type is then a pure identity
/// mapping, byte-for-byte the assumption every existing geometry writer already made before this type
/// existed (<c>GridSnap</c> quantises from an absolute zero and has never compared the two spaces).
/// </para>
/// <para>
/// <b>Stage S scope (this commit).</b> <see cref="CalibrateOnce"/> exists so a future, separate commit
/// can turn on calibration by wiring exactly one call site (<c>ChildWindow.OnSourceInitialized</c>,
/// immediately after <c>IDesktopIntegrationService.EmbedInDesktop</c> and before
/// <c>NativePanelResizeController.AttachTo</c>) — Stage S/P03/P04 deliberately do NOT call it from any
/// runtime code path. Every other consumer of this type goes through
/// <see cref="ToWindowSpace(System.Windows.Point)"/>/<see cref="ToScreenSpace(System.Windows.Point)"/>
/// (and their <see cref="System.Windows.Rect"/> overloads) and is written so it behaves identically no
/// matter what <see cref="Offset"/> is — the uncertainty of Вопрос 2 lives in exactly one place.
/// </para>
/// <para>
/// <b>Manual verify (Win10/Win11), MV-1 — blocking gate before <see cref="CalibrateOnce"/> is ever
/// invoked from runtime code (docs/PLANS/workspace-grid-03-04-integration.md §7).</b> On a monitor
/// with negative coordinates (a secondary monitor placed left of or above the primary): create a
/// <see cref="PanelGeometryMode.GridAligned"/> panel there, read its <c>Window.Left</c>/<c>Top</c>, and
/// compare against the same panel's <c>GetWindowRect</c>-derived screen position
/// (<see cref="DesktopOrganizer.Services.Abstractions.IMonitorService.GetWindowRectDip"/>). Any
/// difference IS the correct <see cref="Offset"/> for that monitor configuration.
/// </para>
/// <para>
/// Prior art: the exact same "isolate an undocumented offset behind one property, default to zero"
/// shape already exists one abstraction layer down, for the WM_SIZING coordinate space
/// (<c>NativeSizingOriginResolver</c>/<c>NativePanelResizeController._originOffsetPx</c>).
/// </para>
/// </remarks>
public static class DesktopCoordinateSpace
{
    private static bool _calibrated;

    /// <summary>
    /// <c>screenDip - windowDip</c>. <c>(0,0)</c> — the default, uncalibrated value — means the two
    /// spaces are identical (today's behavior). The ONLY place ADR-028 Вопрос 2's uncertainty lives.
    /// </summary>
    public static Vector Offset { get; private set; } = new(0d, 0d);

    /// <summary>
    /// <see langword="true"/> while <see cref="Offset"/> is exactly <c>(0,0)</c> — the default,
    /// uncalibrated state that reproduces today's behavior exactly.
    /// </summary>
    public static bool IsIdentity => Offset.X == 0d && Offset.Y == 0d;

    /// <summary>Converts a screen-DIP point (e.g. <c>MonitorInfo.WorkArea.TopLeft</c>) into <c>Window.Left</c>/<c>Top</c>'s space.</summary>
    /// <param name="screenDip">The point, in screen-DIP coordinates.</param>
    public static Point ToWindowSpace(Point screenDip) => new(screenDip.X - Offset.X, screenDip.Y - Offset.Y);

    /// <summary>Rect overload of <see cref="ToWindowSpace(System.Windows.Point)"/> — size is unaffected by a pure translation.</summary>
    /// <param name="screenDip">The rectangle, in screen-DIP coordinates.</param>
    public static Rect ToWindowSpace(Rect screenDip) =>
        screenDip.IsEmpty ? screenDip : new Rect(ToWindowSpace(screenDip.TopLeft), screenDip.Size);

    /// <summary>Converts a <c>Window.Left</c>/<c>Top</c>-space point back into screen DIP.</summary>
    /// <param name="windowDip">The point, in <c>Window.Left</c>/<c>Top</c>'s coordinate space.</param>
    public static Point ToScreenSpace(Point windowDip) => new(windowDip.X + Offset.X, windowDip.Y + Offset.Y);

    /// <summary>Rect overload of <see cref="ToScreenSpace(System.Windows.Point)"/> — size is unaffected by a pure translation.</summary>
    /// <param name="windowDip">The rectangle, in <c>Window.Left</c>/<c>Top</c>'s coordinate space.</param>
    public static Rect ToScreenSpace(Rect windowDip) =>
        windowDip.IsEmpty ? windowDip : new Rect(ToScreenSpace(windowDip.TopLeft), windowDip.Size);

    /// <summary>
    /// Calibrates <see cref="Offset"/> from one observed (screen, window) coordinate pair for the same
    /// physical point. Idempotent — a second call is a no-op unless <see cref="ResetForTests"/> ran in
    /// between — calibration is meant to happen exactly once, right after a panel's HWND is first
    /// reparented into WorkerW. <b>Not called anywhere in Stage S/P03/P04 runtime code</b> (OQ-1 = A);
    /// reserved for a follow-up commit gated behind MV-1 above.
    /// </summary>
    /// <param name="screenTopLeftDip">The point's position as reported by <c>GetWindowRect</c> (screen DIP).</param>
    /// <param name="windowTopLeftDip">The same point's position as reported by <c>Window.Left</c>/<c>Top</c>.</param>
    public static void CalibrateOnce(Point screenTopLeftDip, Point windowTopLeftDip)
    {
        if (_calibrated) return;

        Offset = screenTopLeftDip - windowTopLeftDip;
        _calibrated = true;
    }

    /// <summary>
    /// Test-only hook that restores the default, uncalibrated <c>(0,0)</c> identity state. This type is
    /// process-wide static state (mirroring <c>CalibrateOnce</c>'s own "exactly once per process"
    /// contract), so tests that exercise calibration must call this in teardown to avoid leaking state
    /// into unrelated tests (the test assembly disables parallelization — see <c>AssemblyInfo.cs</c> —
    /// so this is safe without extra locking).
    /// </summary>
    internal static void ResetForTests()
    {
        Offset = new Vector(0d, 0d);
        _calibrated = false;
    }
}
