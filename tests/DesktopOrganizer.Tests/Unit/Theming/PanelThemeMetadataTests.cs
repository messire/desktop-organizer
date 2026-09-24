using System.Windows;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Theming;

/// <summary>
/// Unit tests for <see cref="PanelTheme"/>'s attached <see cref="DependencyProperty"/> metadata
/// (docs/PLANS/phase-5-ambient-accent-ui.md §1 D-7). Read statically via
/// <see cref="DependencyProperty.DefaultMetadata"/> (not <see cref="DependencyProperty.GetMetadata(Type)"/>,
/// which requires a <see cref="DependencyObject"/>-derived <c>forType</c> — <see cref="PanelTheme"/> is a
/// static class, not one) — no visual tree, no <see cref="FrameworkElement"/>, no STA thread required
/// (this test project cannot construct live WPF visual objects).
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelThemeMetadataTests
{
    [Fact]
    public void AccentProperty_IsBrushType()
    {
        Assert.Equal(typeof(Brush), PanelTheme.AccentProperty.PropertyType);
    }

    [Fact]
    public void AccentProperty_OwnerTypeIsPanelTheme()
    {
        Assert.Equal(typeof(PanelTheme), PanelTheme.AccentProperty.OwnerType);
    }

    [Fact]
    public void AccentProperty_Inherits()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.AccentProperty.DefaultMetadata;

        Assert.True(metadata.Inherits);
    }

    [Fact]
    public void AccentProperty_DefaultValue_IsFrozenNeutralAccentBrush()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.AccentProperty.DefaultMetadata;

        var brush = Assert.IsType<SolidColorBrush>(metadata.DefaultValue);
        Assert.True(brush.IsFrozen);
        Assert.Equal(HexBrushConverterColor(AccentPalette.Neutral), brush.Color);
    }

    [Fact]
    public void SurfaceStyleProperty_IsPanelSurfaceStyleType()
    {
        Assert.Equal(typeof(PanelSurfaceStyle), PanelTheme.SurfaceStyleProperty.PropertyType);
    }

    [Fact]
    public void SurfaceStyleProperty_DefaultIsAmbient()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.SurfaceStyleProperty.DefaultMetadata;

        Assert.Equal(PanelSurfaceStyle.Ambient, metadata.DefaultValue);
    }

    [Fact]
    public void SurfaceStyleProperty_Inherits()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.SurfaceStyleProperty.DefaultMetadata;

        Assert.True(metadata.Inherits);
    }

    [Fact]
    public void VisualStateProperty_IsPanelVisualStateType()
    {
        Assert.Equal(typeof(PanelVisualState), PanelTheme.VisualStateProperty.PropertyType);
    }

    [Fact]
    public void VisualStateProperty_DefaultIsIdle()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.VisualStateProperty.DefaultMetadata;

        Assert.Equal(PanelVisualState.Idle, metadata.DefaultValue);
    }

    [Fact]
    public void VisualStateProperty_Inherits()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.VisualStateProperty.DefaultMetadata;

        Assert.True(metadata.Inherits);
    }

    [Fact]
    public void IsLayoutEditingProperty_IsBoolType()
    {
        Assert.Equal(typeof(bool), PanelTheme.IsLayoutEditingProperty.PropertyType);
    }

    [Fact]
    public void IsLayoutEditingProperty_OwnerTypeIsPanelTheme()
    {
        Assert.Equal(typeof(PanelTheme), PanelTheme.IsLayoutEditingProperty.OwnerType);
    }

    [Fact]
    public void IsLayoutEditingProperty_DefaultIsFalse()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.IsLayoutEditingProperty.DefaultMetadata;

        Assert.Equal(false, metadata.DefaultValue);
    }

    [Fact]
    public void IsLayoutEditingProperty_Inherits()
    {
        var metadata = (FrameworkPropertyMetadata)PanelTheme.IsLayoutEditingProperty.DefaultMetadata;

        Assert.True(metadata.Inherits);
    }

    [Fact]
    public void GetSetIsLayoutEditing_RoundTripsOnPlainDependencyObject()
    {
        var target = new DependencyObject();

        PanelTheme.SetIsLayoutEditing(target, true);

        Assert.True(PanelTheme.GetIsLayoutEditing(target));
    }

    [Fact]
    public void GetSetAccent_RoundTripsOnPlainDependencyObject()
    {
        var target = new DependencyObject();
        var brush = new SolidColorBrush(Colors.Red);
        brush.Freeze();

        PanelTheme.SetAccent(target, brush);

        Assert.Same(brush, PanelTheme.GetAccent(target));
    }

    [Fact]
    public void GetSetSurfaceStyle_RoundTripsOnPlainDependencyObject()
    {
        var target = new DependencyObject();

        PanelTheme.SetSurfaceStyle(target, PanelSurfaceStyle.Glass);

        Assert.Equal(PanelSurfaceStyle.Glass, PanelTheme.GetSurfaceStyle(target));
    }

    [Fact]
    public void GetSetVisualState_RoundTripsOnPlainDependencyObject()
    {
        var target = new DependencyObject();

        PanelTheme.SetVisualState(target, PanelVisualState.Hover);

        Assert.Equal(PanelVisualState.Hover, PanelTheme.GetVisualState(target));
    }

    private static Color HexBrushConverterColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
