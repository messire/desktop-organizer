namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Switches the DWM-drawn window frame (title bar and border) of an application window between the
/// light and the dark Windows style, so a dark-themed window does not sit under a light caption
/// (design system 1.x, docs/PLANS/design-system-1x.md §2.1/§3 phase 6.1, ADR-047 D-2).
/// </summary>
/// <remarks>
/// Intended for the chrome window family only (settings window, Customize, dialogs) — never for panels,
/// which are borderless and live in the desktop layer. Callers apply it on <c>SourceInitialized</c> and
/// again whenever the application theme changes.
/// </remarks>
public interface IWindowFrameTheming
{
    /// <summary>
    /// Gets a value indicating whether the running Windows build supports the immersive dark-mode frame
    /// attribute at all (Windows 10 1809, build 17763, or later). When <see langword="false"/>,
    /// <see cref="TryApplyImmersiveDarkMode"/> is a no-op that answers <see langword="false"/>.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Sets <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (attribute 20; 19 on Windows 10 builds before 20H1) on
    /// <paramref name="hwnd"/>. Idempotent — repeating the call with the same value changes nothing.
    /// Never throws.
    /// </summary>
    /// <param name="hwnd">Top-level window handle. <see cref="IntPtr.Zero"/> answers <see langword="false"/>.</param>
    /// <param name="dark"><see langword="true"/> for the dark frame, <see langword="false"/> for the light (default) one.</param>
    /// <returns>
    /// <see langword="true"/> when DWM accepted the attribute; <see langword="false"/> when the build does not
    /// support it, the handle is zero/invalid, or the call failed (the failure is logged, rate-limited per window).
    /// </returns>
    bool TryApplyImmersiveDarkMode(IntPtr hwnd, bool dark);
}
