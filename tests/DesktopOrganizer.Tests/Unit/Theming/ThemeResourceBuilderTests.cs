using System.Windows;
using System.Windows.Media;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="ThemeResourceBuilder"/> (docs/PLANS/design-system-1x.md §2.1/§3 phase 6.1,
/// ADR-047 D-1/D-2/D-3): every colour key is a <b>frozen</b> brush (the built dictionary is swapped
/// wholesale, never mutated — freezing is both a perf win and, for <see cref="AppThemeService"/>'s "add new,
/// then remove old" swap, a correctness requirement no earlier consumer can accidentally invalidate),
/// Light/Dark actually differ, and HighContrast pulls from <see cref="SystemColors"/>. Pure C# — no STA
/// needed, <see cref="ResourceDictionary"/>/<see cref="SolidColorBrush"/> construction does not require a
/// UI thread.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ThemeResourceBuilderTests
{
    /// <summary>Every <see cref="ThemeKeys.ColorKeys"/> entry resolves to an already-frozen <see cref="Brush"/>.</summary>
    /// <param name="theme">The theme to build.</param>
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.HighContrast)]
    public void Build_EveryColorKey_IsAFrozenBrush(AppTheme theme)
    {
        var dictionary = ThemeResourceBuilder.Build(theme, messageFontSize: 12);

        foreach (var key in ThemeKeys.ColorKeys)
        {
            var brush = Assert.IsAssignableFrom<Brush>(dictionary[key]);
            Assert.True(brush.IsFrozen, $"{theme}/{key}: brush is not frozen.");
        }
    }

    /// <summary>Light and Dark are actually distinct — <see cref="ThemeKeys.SurfaceWindow"/> differs between them.</summary>
    [Fact]
    public void Build_LightAndDark_ProduceDifferentSurfaceWindowColor()
    {
        var light = ThemeResourceBuilder.Build(AppTheme.Light, messageFontSize: 12);
        var dark = ThemeResourceBuilder.Build(AppTheme.Dark, messageFontSize: 12);

        var lightColor = ((SolidColorBrush)light[ThemeKeys.SurfaceWindow]).Color;
        var darkColor = ((SolidColorBrush)dark[ThemeKeys.SurfaceWindow]).Color;

        Assert.NotEqual(lightColor, darkColor);
    }

    /// <summary>High contrast maps straight onto the matching <see cref="SystemColors"/> brush singletons — see <see cref="ThemeResourceBuilder"/>'s own HC branch.</summary>
    [Fact]
    public void Build_HighContrast_UsesSystemColorsBrushesDirectly()
    {
        var dictionary = ThemeResourceBuilder.Build(AppTheme.HighContrast, messageFontSize: 12);

        Assert.Same(SystemColors.WindowBrush, dictionary[ThemeKeys.SurfaceWindow]);
        Assert.Same(SystemColors.WindowTextBrush, dictionary[ThemeKeys.TextPrimary]);
        Assert.Same(SystemColors.HighlightBrush, dictionary[ThemeKeys.AccentFill]);
        Assert.Same(SystemColors.HighlightTextBrush, dictionary[ThemeKeys.TextOnAccent]);
        Assert.Same(SystemColors.GrayTextBrush, dictionary[ThemeKeys.TextDisabled]);
    }

    /// <summary>The five <c>Font.Size.*</c> keys match <see cref="TypeRamp.FromMessageFontSize"/> for the same base size, in every theme.</summary>
    /// <param name="theme">The theme to build.</param>
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.HighContrast)]
    public void Build_TypographyKeys_MatchTypeRampForGivenMessageFontSize(AppTheme theme)
    {
        var dictionary = ThemeResourceBuilder.Build(theme, messageFontSize: 12);
        var ramp = TypeRamp.FromMessageFontSize(12);

        Assert.Equal(ramp.Caption, Assert.IsType<double>(dictionary[ThemeKeys.FontSizeCaption]));
        Assert.Equal(ramp.Body, Assert.IsType<double>(dictionary[ThemeKeys.FontSizeBody]));
        Assert.Equal(ramp.BodyStrong, Assert.IsType<double>(dictionary[ThemeKeys.FontSizeBodyStrong]));
        Assert.Equal(ramp.Subtitle, Assert.IsType<double>(dictionary[ThemeKeys.FontSizeSubtitle]));
        Assert.Equal(ramp.Title, Assert.IsType<double>(dictionary[ThemeKeys.FontSizeTitle]));
    }

    /// <summary>Every <see cref="ThemeKeys.GeometryKeys"/> entry has the same value across Light/Dark/HighContrast.</summary>
    [Fact]
    public void Build_GeometryKeys_AreThemeIndependent()
    {
        var light = ThemeResourceBuilder.Build(AppTheme.Light, messageFontSize: 12);
        var dark = ThemeResourceBuilder.Build(AppTheme.Dark, messageFontSize: 12);
        var highContrast = ThemeResourceBuilder.Build(AppTheme.HighContrast, messageFontSize: 12);

        foreach (var key in ThemeKeys.GeometryKeys)
        {
            Assert.Equal(light[key], dark[key]);
            Assert.Equal(light[key], highContrast[key]);
        }
    }
}
