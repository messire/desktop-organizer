using System.Windows;
using System.Windows.Media;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="PanelChromeForegroundPolicy"/>: the dark-surface tokens stay byte-identical to
/// what this app already shipped, the light-surface tokens are actually readable/visible against
/// <see cref="SurfaceComposer.LightNeutralBase"/>, and <see cref="PanelChromeForegroundPolicy.Resolve"/>
/// switches token sets on the SAME rule <see cref="SurfaceComposer.ResolveNeutralBase"/> itself uses
/// (ADR-042: the panel's own text colour's <see cref="PanelThemeMode"/>, via
/// <see cref="PanelThemeRules.IsLightSurface"/> — the accent is not read at all).
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelChromeForegroundPolicyTests
{
    /// <summary>The app's own default text colour (<c>Color.Text.Primary</c>, <c>#DDDDDD</c>) — resolves <see cref="PanelThemeMode.Dark"/>.</summary>
    private static readonly Color DefaultText = Color.FromRgb(0xDD, 0xDD, 0xDD);

    /// <summary>A representative ADR-042 <see cref="PanelColorPreset.LightModeTextHex"/> (Blue's own) — resolves <see cref="PanelThemeMode.Light"/>.</summary>
    private static readonly Color LightModeText = Color.FromRgb(0x17, 0x2F, 0x4B);

    /// <summary>A placeholder accent — irrelevant to <see cref="PanelChromeForegroundPolicy.Resolve"/>'s result (ADR-042); used only to satisfy the method's signature.</summary>
    private static readonly Color PlaceholderAccent = Color.FromRgb(0x3F, 0x7F, 0xD9);

    /// <summary>Every dark-surface token is byte-identical to the fixed hex literal it replaces (Colors.xaml's <c>Color.Text.*</c>/<c>Color.ScrollBar.*</c>).</summary>
    /// <param name="kind">The chrome-foreground kind under test.</param>
    /// <param name="a">Expected alpha channel.</param>
    /// <param name="r">Expected red channel.</param>
    /// <param name="g">Expected green channel.</param>
    /// <param name="b">Expected blue channel.</param>
    [Theory]
    [InlineData(PanelChromeForegroundKind.Primary, 0xFF, 0xDD, 0xDD, 0xDD)]
    [InlineData(PanelChromeForegroundKind.Secondary, 0xFF, 0xAA, 0xAA, 0xAA)]
    [InlineData(PanelChromeForegroundKind.Muted, 0xFF, 0x8A, 0x8A, 0x8A)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumb, 0x66, 0x78, 0x80, 0x88)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbBorder, 0x40, 0x7F, 0x8A, 0x90)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbHover, 0x80, 0x91, 0x9B, 0xA0)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbPressed, 0xA0, 0xA8, 0xB0, 0xB4)]
    [InlineData(PanelChromeForegroundKind.Border, 0x33, 0xFF, 0xFF, 0xFF)]
    public void Resolve_DarkModeText_MatchesShippedLegacyToken(PanelChromeForegroundKind kind, byte a, byte r, byte g, byte b)
    {
        var resolved = PanelChromeForegroundPolicy.Resolve(PlaceholderAccent, DefaultText, kind);

        Assert.Equal(Color.FromArgb(a, r, g, b), resolved);
    }

    /// <summary>A LIGHT-MODE text colour resolves to the light-surface token set for every kind, never the dark one.</summary>
    /// <param name="kind">The chrome-foreground kind under test.</param>
    [Theory]
    [InlineData(PanelChromeForegroundKind.Primary)]
    [InlineData(PanelChromeForegroundKind.Secondary)]
    [InlineData(PanelChromeForegroundKind.Muted)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumb)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbBorder)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbHover)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbPressed)]
    [InlineData(PanelChromeForegroundKind.Border)]
    public void Resolve_LightModeText_NeverReturnsTheDarkToken(PanelChromeForegroundKind kind)
    {
        var lightResolved = PanelChromeForegroundPolicy.Resolve(PlaceholderAccent, LightModeText, kind);
        var darkResolved = PanelChromeForegroundPolicy.Resolve(PlaceholderAccent, DefaultText, kind);

        Assert.NotEqual(darkResolved, lightResolved);
    }

    /// <summary>
    /// ADR-042: <see cref="PanelChromeForegroundPolicy.Resolve"/> ignores its <c>accent</c> parameter
    /// entirely — two wildly different accents, paired with the SAME text colour, always resolve to the
    /// same token.
    /// </summary>
    /// <param name="kind">The chrome-foreground kind under test.</param>
    [Theory]
    [InlineData(PanelChromeForegroundKind.Primary)]
    [InlineData(PanelChromeForegroundKind.Border)]
    public void Resolve_IgnoresAccent_SameTextDifferentAccentsResolveSameToken(PanelChromeForegroundKind kind)
    {
        var withAccentA = PanelChromeForegroundPolicy.Resolve(Color.FromRgb(0xB9, 0xDC, 0xFF), DefaultText, kind);
        var withAccentB = PanelChromeForegroundPolicy.Resolve(ParseColor(AccentPalette.Neutral), DefaultText, kind);

        Assert.Equal(withAccentA, withAccentB);
    }

    /// <summary>
    /// MINOR-4: the "Border" kind's light token is a translucent BLACK (unlike
    /// <see cref="PanelChromeForegroundPolicy.DarkBorder"/>'s translucent WHITE) — byte-identical to the
    /// documented <c>#1F000000</c>, so a future accidental edit is caught here.
    /// </summary>
    [Fact]
    public void LightBorder_IsTranslucentBlack()
    {
        Assert.Equal(Color.FromArgb(0x1F, 0x00, 0x00, 0x00), PanelChromeForegroundPolicy.LightBorder);
        Assert.Equal(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), PanelChromeForegroundPolicy.DarkBorder);
    }

    /// <summary>
    /// The light-surface scrollbar thumb's three "visible" states (resting/hover/pressed — the border is
    /// deliberately subtler, see the policy's own remarks) clear <see cref="ContrastCalculator.MinimumIconContrast"/>
    /// against <see cref="SurfaceComposer.LightNeutralBase"/> — the light base a <see cref="PanelThemeMode.Light"/>
    /// panel actually composites the scrollbar over.
    /// </summary>
    /// <param name="kind">The scrollbar-thumb chrome-foreground kind under test.</param>
    [Theory]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumb)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbHover)]
    [InlineData(PanelChromeForegroundKind.ScrollBarThumbPressed)]
    public void LightScrollBarState_MeetsMinimumIconContrastAgainstLightNeutralBase(PanelChromeForegroundKind kind)
    {
        var thumbColor = PanelChromeForegroundPolicy.Resolve(PlaceholderAccent, LightModeText, kind);

        var composited = ContrastCalculator.CompositeOverOpaque(thumbColor, SurfaceComposer.LightNeutralBase);
        var contrast = ContrastCalculator.ContrastRatio(composited, SurfaceComposer.LightNeutralBase);

        Assert.True(
            contrast >= ContrastCalculator.MinimumIconContrast,
            $"{kind}: contrast {contrast:F2}:1 against LightNeutralBase is below MinimumIconContrast ({ContrastCalculator.MinimumIconContrast}:1).");
    }

    /// <summary>
    /// The light-surface text tokens (Primary/Secondary=Muted) clear <see cref="ContrastCalculator.MinimumTextContrast"/>
    /// against <see cref="SurfaceComposer.LightNeutralBase"/> — both are opaque, so no wallpaper compositing applies.
    /// </summary>
    /// <param name="kind">The text chrome-foreground kind under test.</param>
    [Theory]
    [InlineData(PanelChromeForegroundKind.Primary)]
    [InlineData(PanelChromeForegroundKind.Secondary)]
    public void LightTextToken_MeetsMinimumTextContrastAgainstLightNeutralBase(PanelChromeForegroundKind kind)
    {
        var textColor = PanelChromeForegroundPolicy.Resolve(PlaceholderAccent, LightModeText, kind);

        var contrast = ContrastCalculator.ContrastRatio(textColor, SurfaceComposer.LightNeutralBase);

        Assert.True(
            contrast >= ContrastCalculator.MinimumTextContrast,
            $"{kind}: contrast {contrast:F2}:1 against LightNeutralBase is below MinimumTextContrast ({ContrastCalculator.MinimumTextContrast}:1).");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // Loads the REAL compiled Colors.xaml (same "read once from the real ResourceDictionary, don't
    // hand-copy the literal" convention AmbientAccentContrastAuditTests'/HoverOverlayPolicyTests' own
    // LoadColorChromeHighlightTop already establishes) and compares every Color.Text.*/Color.ScrollBar.*
    // token against PanelChromeForegroundPolicy's own Dark* fields — so a future edit to either side that
    // silently drifts from the other is caught here, not just by-comment.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Verifies <see cref="PanelChromeForegroundPolicy.DarkPrimary"/>/<see cref="PanelChromeForegroundPolicy.DarkSecondary"/>/<see cref="PanelChromeForegroundPolicy.DarkMuted"/> stay byte-identical to Colors.xaml's own <c>Color.Text.Primary/Secondary/Muted</c>.</summary>
    [Fact]
    public void DarkTextTokens_MatchColorsXaml()
    {
        var colors = LoadColorsXaml();

        Assert.Equal((Color)colors["Color.Text.Primary"], PanelChromeForegroundPolicy.DarkPrimary);
        Assert.Equal((Color)colors["Color.Text.Secondary"], PanelChromeForegroundPolicy.DarkSecondary);
        Assert.Equal((Color)colors["Color.Text.Muted"], PanelChromeForegroundPolicy.DarkMuted);
    }

    /// <summary>Verifies <see cref="PanelChromeForegroundPolicy"/>'s <c>DarkScrollBarThumb*</c> fields stay byte-identical to Colors.xaml's own <c>Color.ScrollBar.Thumb*</c>.</summary>
    [Fact]
    public void DarkScrollBarTokens_MatchColorsXaml()
    {
        var colors = LoadColorsXaml();

        Assert.Equal((Color)colors["Color.ScrollBar.Thumb"], PanelChromeForegroundPolicy.DarkScrollBarThumb);
        Assert.Equal((Color)colors["Color.ScrollBar.ThumbBorder"], PanelChromeForegroundPolicy.DarkScrollBarThumbBorder);
        Assert.Equal((Color)colors["Color.ScrollBar.ThumbHover"], PanelChromeForegroundPolicy.DarkScrollBarThumbHover);
        Assert.Equal((Color)colors["Color.ScrollBar.ThumbPressed"], PanelChromeForegroundPolicy.DarkScrollBarThumbPressed);
    }

    /// <summary>Verifies <see cref="PanelChromeForegroundPolicy.DarkBorder"/> stays byte-identical to Colors.xaml's own <c>Color.Border.Default</c>.</summary>
    [Fact]
    public void DarkBorderToken_MatchesColorsXaml()
    {
        var colors = LoadColorsXaml();

        Assert.Equal((Color)colors["Color.Border.Default"], PanelChromeForegroundPolicy.DarkBorder);
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>
    /// Loads the REAL <c>Colors.xaml</c> resource dictionary from the compiled UI assembly — declared
    /// last (private, StyleCop's public-before-private method ordering).
    /// </summary>
    private static ResourceDictionary LoadColorsXaml()
    {
        return StaTestRunner.Run(() =>
        {
            WpfApplicationHost.EnsureApplication();

            return new ResourceDictionary
            {
                Source = new Uri("/DesktopOrganizer.UI;component/Resources/Styles/Colors.xaml", UriKind.Relative),
            };
        });
    }
}
