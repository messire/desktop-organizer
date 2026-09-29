namespace DesktopOrganizer.Services.Abstractions;

/// <summary>
/// The colours and highlight radius the tray context menu is drawn with (design system 1.x,
/// docs/PLANS/design-system-1x.md §2.9 variant A / §3 phase 6.4, ADR-047 D-10). Plain ARGB primitives
/// (<c>0xAARRGGBB</c>) rather than WPF/GDI colour types: this layer knows nothing about WPF, and the single
/// source of truth for the values is the UI layer's C# "Palette T" (<c>ThemePalette</c>) — the UI builds
/// this record (<c>TrayMenuPaletteFactory</c>) and hands it down through
/// <see cref="ITrayIconManager.SetMenuPalette"/>. Value equality is what makes a repeated
/// <see cref="ITrayIconManager.SetMenuPalette"/> call with the same palette a no-op.
/// </summary>
/// <param name="Background">Menu surface (<c>Theme.Surface.Flyout</c>).</param>
/// <param name="Border">1 px popup border (<c>Theme.Stroke.Card</c>).</param>
/// <param name="Text">Enabled item text (<c>Theme.Text.Primary</c>).</param>
/// <param name="TextDisabled">Disabled item text (<c>Theme.Text.Disabled</c>).</param>
/// <param name="HoverFill">
/// Opaque highlight fill of the hovered/keyboard-selected item — <c>Theme.Control.Subtle.Hover</c> already
/// composited over <paramref name="Background"/> (GDI draws it as a solid rounded rectangle).
/// </param>
/// <param name="Separator">Separator line (<c>Theme.Stroke.Divider</c>).</param>
/// <param name="Glyph">Checkmark and submenu-arrow glyphs of enabled items.</param>
/// <param name="CornerRadius">Highlight corner radius in DIPs (<c>Theme.Radius.Control</c>, 4); scaled by the menu's DPI when drawn.</param>
public sealed record TrayMenuPalette(
    uint Background,
    uint Border,
    uint Text,
    uint TextDisabled,
    uint HoverFill,
    uint Separator,
    uint Glyph,
    double CornerRadius);
