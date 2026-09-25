using System.Windows.Media;
using DesktopOrganizer.UI.Controls;
using DesktopOrganizer.UI.Models;
using DesktopOrganizer.UI.Theming;

namespace DesktopOrganizer.Tests.Unit.Controls;

/// <summary>
/// Unit tests for <see cref="PanelChrome"/>'s <see cref="System.Windows.DependencyProperty"/> metadata
/// (docs/PLANS/phase-5-ambient-accent-ui.md §1 D-8, §7.1 item 7). Read statically via
/// <see cref="System.Windows.DependencyProperty.DefaultMetadata"/> — mirrors
/// <see cref="Theming.PanelThemeMetadataTests"/>'s approach (C2): no visual tree, no
/// <see cref="System.Windows.FrameworkElement"/> instance, no STA thread required (this test project
/// cannot construct live WPF visual objects). Accessing any static member of <see cref="PanelChrome"/>
/// runs its static constructor (<see cref="System.Windows.FrameworkElement.DefaultStyleKeyProperty"/>'s
/// <c>OverrideMetadata</c> call) — that call registers metadata only, it does not require a Dispatcher.
/// </summary>
[Trait(UnitTests, "UI")]
public class PanelChromeMetadataTests
{
    [Fact]
    public void TitleProperty_IsStringType_DefaultsToEmpty()
    {
        Assert.Equal(typeof(string), PanelChrome.TitleProperty.PropertyType);
        Assert.Equal(string.Empty, PanelChrome.TitleProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void TitleEditorProperty_IsObjectType_DefaultsToNull()
    {
        Assert.Equal(typeof(object), PanelChrome.TitleEditorProperty.PropertyType);
        Assert.Null(PanelChrome.TitleEditorProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void HeaderActionsProperty_IsObjectType_DefaultsToNull()
    {
        Assert.Equal(typeof(object), PanelChrome.HeaderActionsProperty.PropertyType);
        Assert.Null(PanelChrome.HeaderActionsProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void SemanticIconProperty_IsStringType_DefaultsToEmpty()
    {
        Assert.Equal(typeof(string), PanelChrome.SemanticIconProperty.PropertyType);
        Assert.Equal(string.Empty, PanelChrome.SemanticIconProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void AccentBrushProperty_IsBrushType_DefaultsToTransparent()
    {
        Assert.Equal(typeof(Brush), PanelChrome.AccentBrushProperty.PropertyType);
        Assert.Same(Brushes.Transparent, PanelChrome.AccentBrushProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void SurfaceBrushProperty_IsBrushType_DefaultsToTransparent()
    {
        Assert.Equal(typeof(Brush), PanelChrome.SurfaceBrushProperty.PropertyType);
        Assert.Same(Brushes.Transparent, PanelChrome.SurfaceBrushProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void SurfaceStyleProperty_IsPanelSurfaceStyleType_DefaultsToAmbient()
    {
        Assert.Equal(typeof(PanelSurfaceStyle), PanelChrome.SurfaceStyleProperty.PropertyType);
        Assert.Equal(PanelSurfaceStyle.Ambient, PanelChrome.SurfaceStyleProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void VisualStateProperty_IsPanelVisualStateType_DefaultsToIdle()
    {
        Assert.Equal(typeof(PanelVisualState), PanelChrome.VisualStateProperty.PropertyType);
        Assert.Equal(PanelVisualState.Idle, PanelChrome.VisualStateProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void IsLayoutEditingProperty_IsBoolType_DefaultsToFalse()
    {
        Assert.Equal(typeof(bool), PanelChrome.IsLayoutEditingProperty.PropertyType);
        Assert.Equal(false, PanelChrome.IsLayoutEditingProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void IsRolledUpProperty_IsBoolType_DefaultsToFalse()
    {
        Assert.Equal(typeof(bool), PanelChrome.IsRolledUpProperty.PropertyType);
        Assert.Equal(false, PanelChrome.IsRolledUpProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void ItemCountProperty_IsIntType_DefaultsToZero()
    {
        Assert.Equal(typeof(int), PanelChrome.ItemCountProperty.PropertyType);
        Assert.Equal(0, PanelChrome.ItemCountProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void HeaderHeightProperty_IsDoubleType_DefaultsToPanelMetricsHeaderHeight()
    {
        Assert.Equal(typeof(double), PanelChrome.HeaderHeightProperty.PropertyType);
        Assert.Equal(PanelMetrics.HeaderHeight, PanelChrome.HeaderHeightProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void AllProperties_OwnerTypeIsPanelChrome()
    {
        Assert.Equal(typeof(PanelChrome), PanelChrome.TitleProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.TitleEditorProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.HeaderActionsProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.SemanticIconProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.AccentBrushProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.SurfaceBrushProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.SurfaceStyleProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.VisualStateProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.IsLayoutEditingProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.IsRolledUpProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.ItemCountProperty.OwnerType);
        Assert.Equal(typeof(PanelChrome), PanelChrome.HeaderHeightProperty.OwnerType);
    }

    [Fact]
    public void TemplatePartNames_MatchTheirDocumentedPART_Prefix()
    {
        Assert.Equal("PART_Header", PanelChrome.PartHeader);
        Assert.Equal("PART_AccentIndicator", PanelChrome.PartAccentIndicator);
        Assert.Equal("PART_AccentIndicatorHost", PanelChrome.PartAccentIndicatorHost);
        Assert.Equal("PART_ContentHost", PanelChrome.PartContentHost);
        Assert.Equal("PART_InteractionOverlay", PanelChrome.PartInteractionOverlay);

        // ADR-030 "Reference-Style Layered Panel Chrome" (docs/PLANS/reference-panel-chrome.md §3)
        Assert.Equal("PART_HitCatcher", PanelChrome.PartHitCatcher);
        Assert.Equal("PART_Glow", PanelChrome.PartGlow);
        Assert.Equal("PART_GlowFallback", PanelChrome.PartGlowFallback);
        Assert.Equal("PART_SurfaceLayer", PanelChrome.PartSurfaceLayer);
        Assert.Equal("PART_SurfaceHighlight", PanelChrome.PartSurfaceHighlight);
        Assert.Equal("PART_NeutralStroke", PanelChrome.PartNeutralStroke);
        Assert.Equal("PART_AccentStroke", PanelChrome.PartAccentStroke);
    }
}
