namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// Switches the classic Win32 popup menus of this process — the shell context menu of a desktop icon
/// (ADR-040, <c>TrackPopupMenuEx</c>) and a window's system menu — between the light and the dark Windows
/// menu theme, so that in the dark theme they do not open as the only light surface next to the dark
/// application menus (design system 1.x, docs/PLANS/design-system-1x.md §2.10 / §3 phase 6.4b,
/// ADR-047 D-11).
/// </summary>
/// <remarks>
/// Backed by an undocumented Windows API (<c>uxtheme.dll</c> <c>SetPreferredAppMode</c>/
/// <c>FlushMenuThemes</c>), therefore opt-in only for Windows 10 1903 (build 18362) and later, behind a code
/// constant, and a strict no-op everywhere else. The WPF/WinForms menus of the application are drawn by the
/// application itself and are not affected. Never persisted, not a user setting.
/// </remarks>
public interface INativeMenuTheming
{
    /// <summary>
    /// Gets a value indicating whether dark native menus can be requested at all on this Windows build
    /// (feature enabled in code and build ≥ 18362). When <see langword="false"/>, <see cref="TryApply"/> is a
    /// no-op that answers <see langword="false"/>.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Requests the dark (<paramref name="dark"/> = <see langword="true"/>) or the default light native menu
    /// theme for menus opened from now on. Idempotent — repeating the last applied value makes no native
    /// call, and requesting light before dark was ever requested makes no native call either (the process
    /// already starts in the default mode). Must be called on the UI thread. Never throws.
    /// </summary>
    /// <param name="dark"><see langword="true"/> for dark native menus; <see langword="false"/> for the Windows default.</param>
    /// <returns>
    /// <see langword="true"/> when the requested mode is (now) in effect; <see langword="false"/> when unsupported
    /// or the native call could not be made (logged once).
    /// </returns>
    bool TryApply(bool dark);
}
