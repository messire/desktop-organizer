using DesktopOrganizer.Services.Abstractions;
using DesktopOrganizer.UI.Lifecycle;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Lifecycle;

/// <summary>
/// Unit tests for <see cref="TrayMenuPaletteFactory"/> (design system 1.x, docs/PLANS/design-system-1x.md
/// §2.1/§2.9, phase 6.4, T-1/T-11): the tray palette is a faithful projection of <see cref="ThemePalette"/>
/// (the synchronisation test between the UI's source of truth and the ARGB primitives the Services-side
/// renderer draws with), its text pairs clear WCAG AA, and the resolution rule follows the Windows MODE,
/// high contrast and the dark-theme gate.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class TrayMenuPaletteFactoryTests
{
    /// <summary>Both tray palettes with their source chrome palettes.</summary>
    public static TheoryData<string, TrayMenuPalette, ThemePalette> Pairs => new()
    {
        { "Light", TrayMenuPaletteFactory.Light, ThemePalette.Light },
        { "Dark", TrayMenuPaletteFactory.Dark, ThemePalette.Dark },
    };

    /// <summary>Every tray slot equals the matching <see cref="ThemePalette"/> token (the WPF menus' own mapping).</summary>
    /// <param name="label">Palette label (test output only).</param>
    /// <param name="tray">The tray palette.</param>
    /// <param name="source">The chrome palette it must be built from.</param>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Palette_IsInSyncWithThemePalette(string label, TrayMenuPalette tray, ThemePalette source)
    {
        Assert.False(string.IsNullOrEmpty(label));
        Assert.Equal(TrayMenuPaletteFactory.ToArgb(source.SurfaceFlyout), tray.Background);
        Assert.Equal(TrayMenuPaletteFactory.ToArgb(source.StrokeCard), tray.Border);
        Assert.Equal(TrayMenuPaletteFactory.ToArgb(source.TextPrimary), tray.Text);
        Assert.Equal(TrayMenuPaletteFactory.ToArgb(source.TextDisabled), tray.TextDisabled);
        Assert.Equal(TrayMenuPaletteFactory.ToArgb(ContrastCalculator.CompositeOverOpaque(source.ControlSubtleHover, source.SurfaceFlyout)), tray.HoverFill);
        Assert.Equal(TrayMenuPaletteFactory.ToArgb(source.StrokeDivider), tray.Separator);
        Assert.Equal(TrayMenuPaletteFactory.ToArgb(source.TextPrimary), tray.Glyph);
        Assert.Equal(4d, tray.CornerRadius);
        Assert.Equal(tray, TrayMenuPaletteFactory.Create(source));
    }

    /// <summary>The hover fill is opaque (the renderer fills it as a solid GDI shape) and visibly differs from the surface.</summary>
    /// <param name="label">Palette label (test output only).</param>
    /// <param name="tray">The tray palette.</param>
    /// <param name="source">The chrome palette (unused beyond the theory shape).</param>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void HoverFill_IsOpaque_AndDistinctFromBackground(string label, TrayMenuPalette tray, ThemePalette source)
    {
        Assert.NotNull(source);
        Assert.True(tray.HoverFill >> 24 == 0xFF, $"{label}: hover fill must be opaque");
        Assert.NotEqual(tray.Background, tray.HoverFill);
    }

    /// <summary>
    /// Review L-4: every colour slot is opaque (<c>0xFF</c> alpha) — the renderer paints each one as a solid
    /// GDI brush/pen or <see cref="System.Windows.Forms.TextRenderer"/> colour straight onto the popup, with
    /// nothing underneath to blend a translucent token against (the WPF tokens' alpha must be pre-composited
    /// by the factory).
    /// </summary>
    /// <param name="label">Palette label (test output only).</param>
    /// <param name="tray">The tray palette.</param>
    /// <param name="source">The chrome palette (unused beyond the theory shape).</param>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void EverySlot_IsOpaque(string label, TrayMenuPalette tray, ThemePalette source)
    {
        Assert.NotNull(source);
        (string Slot, uint Argb)[] slots =
        [
            (nameof(tray.Background), tray.Background),
            (nameof(tray.Border), tray.Border),
            (nameof(tray.Text), tray.Text),
            (nameof(tray.TextDisabled), tray.TextDisabled),
            (nameof(tray.Separator), tray.Separator),
            (nameof(tray.Glyph), tray.Glyph),
            (nameof(tray.HoverFill), tray.HoverFill),
        ];

        foreach (var (slot, argb) in slots)
        {
            Assert.True(argb >> 24 == 0xFF, $"{label}.{slot} = 0x{argb:X8} must be opaque");
        }
    }

    /// <summary>
    /// Contrast (§2.12): text on the surface and on the hover fill ≥ 4.5:1; disabled text on the surface ≥ 3:1;
    /// the checkmark/chevron glyph on the hover fill ≥ 3:1.
    /// </summary>
    /// <param name="label">Palette label (test output only).</param>
    /// <param name="tray">The tray palette.</param>
    /// <param name="source">The chrome palette (unused beyond the theory shape).</param>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Palette_MeetsContrast(string label, TrayMenuPalette tray, ThemePalette source)
    {
        Assert.NotNull(source);
        AssertContrast(label, "Text/Background", tray.Text, tray.Background, 4.5);
        AssertContrast(label, "Text/HoverFill", tray.Text, tray.HoverFill, 4.5);
        AssertContrast(label, "TextDisabled/Background", tray.TextDisabled, tray.Background, 3.0);
        AssertContrast(label, "Glyph/HoverFill", tray.Glyph, tray.HoverFill, 3.0);
    }

    /// <summary>ARGB packing round-trips every channel.</summary>
    [Fact]
    public void ToArgb_PacksChannels()
        => Assert.Equal(0x80102030u, TrayMenuPaletteFactory.ToArgb(Color.FromArgb(0x80, 0x10, 0x20, 0x30)));

    /// <summary>
    /// Resolution matrix: high contrast (either source) → null; gate closed → always light; gate open → the
    /// Windows MODE (<c>SystemUsesLightTheme</c>) decides, not the app mode.
    /// </summary>
    /// <param name="appTheme">Resolved application theme.</param>
    /// <param name="appsLight">AppsUseLightTheme.</param>
    /// <param name="systemLight">SystemUsesLightTheme.</param>
    /// <param name="snapshotHighContrast">The snapshot's own high-contrast flag.</param>
    /// <param name="gate">Dark-theme gate.</param>
    /// <param name="expected">"Light", "Dark" or "System" (null palette).</param>
    [Theory]
    [InlineData(AppTheme.HighContrast, true, true, false, true, "System")]
    [InlineData(AppTheme.Light, true, false, true, true, "System")]
    [InlineData(AppTheme.Light, true, false, false, false, "Light")]
    [InlineData(AppTheme.Light, false, false, false, false, "Light")]
    [InlineData(AppTheme.Light, true, false, false, true, "Dark")]
    [InlineData(AppTheme.Dark, false, false, false, true, "Dark")]
    [InlineData(AppTheme.Dark, false, true, false, true, "Light")]
    [InlineData(AppTheme.Light, true, true, false, true, "Light")]
    public void Resolve_FollowsSystemModeHighContrastAndGate(AppTheme appTheme, bool appsLight, bool systemLight, bool snapshotHighContrast, bool gate, string expected)
    {
        var snapshot = new SystemThemeSnapshot(appsLight, systemLight, snapshotHighContrast);

        var palette = TrayMenuPaletteFactory.Resolve(appTheme, snapshot, gate);

        var expectedPalette = expected switch
        {
            "Light" => TrayMenuPaletteFactory.Light,
            "Dark" => TrayMenuPaletteFactory.Dark,
            _ => null,
        };
        Assert.Equal(expectedPalette, palette);
    }

    private static void AssertContrast(string label, string pair, uint foreground, uint background, double minimum)
    {
        var ratio = ContrastCalculator.ContrastRatio(ToColor(foreground), ToColor(background));
        Assert.True(ratio >= minimum, $"{label} {pair}: {ratio:F2} < {minimum}");
    }

    private static Color ToColor(uint argb)
        => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
