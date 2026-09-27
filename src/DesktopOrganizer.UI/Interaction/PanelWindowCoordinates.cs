using System.Windows.Media;

namespace DesktopOrganizer.UI.Interaction;

/// <summary>
/// The single px↔DIP boundary between a fence panel's live <see cref="Window"/> and the single
/// desktop-DIP space every panel model (<c>ChildWindowModel</c>, persisted geometry, the workspace-grid
/// math) lives in (release-hardening F §5 "Адаптер", ADR-037). Every read of <see cref="Window.Left"/>/
/// <see cref="Window.Top"/> as model geometry, and every write of model geometry onto a panel window,
/// is meant to go through this type — never a bare assignment — so <c>DesktopScaleModel</c> stays the
/// only place px↔DIP arithmetic happens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity on a uniform snapshot.</b> <see cref="DesktopScaleModel.WindowToDesktop"/>/
/// <see cref="DesktopScaleModel.DesktopToWindow"/> are both identity whenever the window's own current
/// DPI scale equals the snapshot's <see cref="DesktopScaleModel.UniformScale"/> (Embedded mode always;
/// TopLevel with every monitor at one scale) — so on every single-scale configuration this type reduces
/// to a bare <c>Window.Left</c>/<c>Top</c> read/write, bit-identical to 0.6.1 (plan §2 "Инвариант
/// неизменности persistence").
/// </para>
/// <para>
/// <b>Width/Height are never scaled here.</b> A panel's <c>Width</c>/<c>Height</c> is DIP-invariant
/// across this boundary (plan §4.1) — the desktop-DIP size IS the WPF DIP size on whichever monitor the
/// window currently renders on — only <c>Left</c>/<c>Top</c> ever need translation.
/// </para>
/// </remarks>
internal static class PanelWindowCoordinates
{
    /// <summary>
    /// Reads <paramref name="window"/>'s current <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c> and
    /// converts them to desktop-DIP through <paramref name="scale"/>. <c>NaN</c>/negative
    /// <c>Width</c>/<c>Height</c> (a window whose size was never explicitly assigned) is treated as
    /// <c>0</c> so a position-only read never derails the monitor lookup a non-uniform snapshot performs.
    /// </summary>
    /// <param name="window">The panel window.</param>
    /// <param name="scale">The current desktop-scale snapshot.</param>
    /// <returns>The window's bounds, in desktop-DIP.</returns>
    public static Rect ReadDesktopBounds(Window window, DesktopScaleModel scale)
        => ToDesktopBounds(window, new Rect(window.Left, window.Top, SafeExtent(window.Width), SafeExtent(window.Height)), scale);

    /// <summary>
    /// Converts an already-known window-space rectangle (e.g. one built with a caller-computed height,
    /// such as a rolled-up panel's collapsed header) into desktop-DIP, using <paramref name="window"/>'s
    /// current DPI scale.
    /// </summary>
    /// <param name="window">The panel window whose current monitor/DPI the rectangle came from.</param>
    /// <param name="windowSpaceBounds">The rectangle, in <see cref="Window.Left"/>/<see cref="Window.Top"/>'s own space.</param>
    /// <param name="scale">The current desktop-scale snapshot.</param>
    /// <returns>The rectangle, in desktop-DIP.</returns>
    public static Rect ToDesktopBounds(Window window, Rect windowSpaceBounds, DesktopScaleModel scale)
        => scale.WindowToDesktop(windowSpaceBounds, GetWindowScale(window, scale));

    /// <summary>
    /// Writes <paramref name="desktopBounds"/> onto <paramref name="window"/>'s <c>Left</c>/<c>Top</c>/
    /// <c>Width</c>/<c>Height</c>, converting the position through <paramref name="scale"/> and copying
    /// the size verbatim (DIP-invariant — see this type's remarks).
    /// </summary>
    /// <param name="window">The panel window.</param>
    /// <param name="desktopBounds">The bounds to apply, in desktop-DIP.</param>
    /// <param name="scale">The current desktop-scale snapshot.</param>
    public static void WriteDesktopBounds(Window window, Rect desktopBounds, DesktopScaleModel scale)
    {
        WriteDesktopPosition(window, desktopBounds.TopLeft, scale);
        window.Width = desktopBounds.Width;
        window.Height = desktopBounds.Height;
    }

    /// <summary>
    /// Writes <paramref name="desktopTopLeft"/> onto <paramref name="window"/>'s <c>Left</c>/<c>Top"</c>
    /// only, leaving <c>Width</c>/<c>Height</c> untouched.
    /// </summary>
    /// <param name="window">The panel window.</param>
    /// <param name="desktopTopLeft">The top-left to apply, in desktop-DIP.</param>
    /// <param name="scale">The current desktop-scale snapshot.</param>
    public static void WriteDesktopPosition(Window window, Point desktopTopLeft, DesktopScaleModel scale)
    {
        var position = ToWindowPosition(window, desktopTopLeft, scale);
        window.Left = position.X;
        window.Top = position.Y;
    }

    /// <summary>
    /// Converts a desktop-DIP point into the <c>Left</c>/<c>Top</c> value <paramref name="window"/> must
    /// be given to appear there, without writing it.
    /// </summary>
    /// <param name="window">The panel window (supplies the current DPI scale).</param>
    /// <param name="desktopPosition">The point, in desktop-DIP.</param>
    /// <param name="scale">The current desktop-scale snapshot.</param>
    /// <returns>The corresponding <c>Window.Left</c>/<c>Top</c> value.</returns>
    public static Point ToWindowPosition(Window window, Point desktopPosition, DesktopScaleModel scale)
        => scale.DesktopToWindow(new Rect(desktopPosition, default(Size)), GetWindowScale(window, scale));

    /// <summary>
    /// The window's current DPI scale (<c>s_w</c>) — the ONLY allowed use of
    /// <see cref="VisualTreeHelper.GetDpi(Visual)"/> for desktop-layer geometry (plan §4 point 3).
    /// </summary>
    /// <remarks>
    /// F4 (release-hardening 1.0 review): <paramref name="scale"/>'s <see cref="DesktopScaleModel.UniformScale"/>
    /// is used ONLY if the <see cref="VisualTreeHelper.GetDpi(Visual)"/> query itself throws, or returns a
    /// non-positive/non-finite <see cref="DpiScale.DpiScaleX"/> — genuinely rare paths this method guards
    /// defensively, not the common case. A window with no <see cref="PresentationSource"/> yet (not yet
    /// shown — e.g. <c>ChildWindow.Initialize</c>, before <c>Show()</c>) does NOT hit either of those:
    /// <see cref="VisualTreeHelper.GetDpi(Visual)"/> neither throws nor returns zero for it — it returns
    /// the PROCESS's own DPI-awareness context (typically 1.0/96, unless the process's manifest/
    /// <c>Program.Main</c> configured a different one), a perfectly valid, positive, finite value that
    /// this method then returns AS-IS. That process DPI can legitimately differ from
    /// <paramref name="scale"/>'s <see cref="DesktopScaleModel.UniformScale"/> (e.g. a non-uniform,
    /// mixed-DPI TopLevel snapshot has no single "the" scale at all) — callers must not assume an
    /// unattached window's result equals <paramref name="scale"/>'s fallback value. Never throws.
    /// </remarks>
    /// <param name="window">The window to query.</param>
    /// <param name="scale">The current desktop-scale snapshot (supplies the fallback for the rare failure paths above).</param>
    /// <returns>
    /// The window's current DPI scale (or, for an unattached window, the process DPI — see remarks); the
    /// snapshot's uniform scale only if the query itself throws or returns an invalid value.
    /// </returns>
    internal static double GetWindowScale(Window window, DesktopScaleModel scale)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            return dpi.DpiScaleX > 0 && double.IsFinite(dpi.DpiScaleX) ? dpi.DpiScaleX : scale.UniformScale;
        }
        catch
        {
            return scale.UniformScale;
        }
    }

    private static double SafeExtent(double value) => double.IsFinite(value) && value > 0 ? value : 0d;
}
