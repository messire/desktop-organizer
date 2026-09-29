namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Immutable snapshot of the Windows colour-mode preferences the application chrome follows
/// (design system 1.x, docs/PLANS/design-system-1x.md §2.1/§3 phase 6.1, ADR-047 D-2). Read from
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>, the system high-contrast flag,
/// the live Win32 system colours and the system message font; never persisted (CN-1 = A: no application
/// setting, G-1).
/// </summary>
/// <remarks>
/// Value equality is what <see cref="ISystemThemeProvider.Changed"/> compares — the event is raised only
/// when at least one of the values actually differs from the previous snapshot. The last two members exist
/// for change detection only: a switch from one high-contrast theme to another (Aquatic → Desert) leaves
/// the three flags untouched and changes only the system colours, and a text-size/system-font change
/// (<c>SPI_SETNONCLIENTMETRICS</c>) changes only the message font.
/// </remarks>
/// <param name="AppsUseLightTheme">
/// "Choose your app mode" (<c>AppsUseLightTheme</c>): <see langword="true"/> = light. Drives the settings
/// window, Customize, dialogs, WPF menus and the DWM title bar. An absent value reads as
/// <see langword="true"/> (the Windows default).
/// </param>
/// <param name="SystemUsesLightTheme">
/// "Choose your Windows mode" (<c>SystemUsesLightTheme</c>): <see langword="true"/> = light. Drives the
/// tray menu (the taskbar/native tray menus follow this mode, not the app mode). An absent value falls
/// back to <paramref name="AppsUseLightTheme"/>.
/// </param>
/// <param name="HighContrast">
/// Whether a high-contrast theme is active (<c>SPI_GETHIGHCONTRAST</c> / <c>HCF_HIGHCONTRASTON</c>). When
/// <see langword="true"/> consumers should use <c>SystemColors</c> and ignore the two light/dark flags.
/// Defaults to <see langword="false"/> so a two-flag snapshot (tests, fakes) stays a valid construction.
/// </param>
/// <param name="SystemColorsFingerprint">
/// An opaque, deterministic fingerprint of the Win32 system colours the high-contrast palette is built
/// from (<c>GetSysColor</c> of <c>COLOR_WINDOW</c>, <c>COLOR_WINDOWTEXT</c>, <c>COLOR_HIGHLIGHT</c>,
/// <c>COLOR_HIGHLIGHTTEXT</c>, <c>COLOR_BTNFACE</c>, <c>COLOR_BTNTEXT</c>, <c>COLOR_GRAYTEXT</c>,
/// <c>COLOR_MENU</c>, <c>COLOR_3DLIGHT</c>). Only meaningful for equality — a different value means the
/// system palette changed and <c>SystemColors</c>-based resources must be rebuilt. 0 = unknown/not read
/// (the default, so snapshots built without it keep comparing equal).
/// </param>
/// <param name="MessageFontSize">
/// The system message font size in DIPs (<c>NONCLIENTMETRICS.lfMessageFont</c> at the system DPI — the
/// quantity WPF calls <c>SystemFonts.MessageFontSize</c>). A change means the type ramp must be recomputed.
/// 0 = unknown/not read (the default).
/// </param>
public sealed record SystemThemeSnapshot(
    bool AppsUseLightTheme,
    bool SystemUsesLightTheme,
    bool HighContrast = false,
    int SystemColorsFingerprint = 0,
    double MessageFontSize = 0)
{
    /// <summary>
    /// Gets the snapshot used when nothing could be read: light app mode, light Windows mode, no high
    /// contrast, unknown system colours and font — the Windows defaults.
    /// </summary>
    public static SystemThemeSnapshot Default { get; } = new(AppsUseLightTheme: true, SystemUsesLightTheme: true, HighContrast: false);
}
