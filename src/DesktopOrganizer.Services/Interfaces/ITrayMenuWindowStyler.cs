using DesktopOrganizer.Services.Abstractions;

namespace DesktopOrganizer.Services.Interfaces;

/// <summary>
/// Applies the DWM window-level part of the tray menu style (design system 1.x,
/// docs/PLANS/design-system-1x.md §2.9 variant A, ADR-047 D-10) to one tray menu popup window — the root
/// context menu or one of its submenu drop-downs: Windows 11 small rounded corners and a border in the
/// palette's border colour. A seam so <c>TrayIconManager</c> stays free of <c>INativeApi</c> (internal) and
/// testable with a mock.
/// </summary>
public interface ITrayMenuWindowStyler
{
    /// <summary>
    /// Styles <paramref name="hwnd"/> for <paramref name="palette"/>: rounded corners + palette border colour
    /// for a palette; the system defaults (square, default border) for <see langword="null"/> (high
    /// contrast). A no-op on Windows builds without the DWM attributes (before Windows 11). Never throws.
    /// </summary>
    /// <param name="hwnd">The popup window handle of a tray menu strip. <see cref="IntPtr.Zero"/> is ignored.</param>
    /// <param name="palette">The palette in effect, or <see langword="null"/> for the system look.</param>
    void Apply(IntPtr hwnd, TrayMenuPalette? palette);
}
