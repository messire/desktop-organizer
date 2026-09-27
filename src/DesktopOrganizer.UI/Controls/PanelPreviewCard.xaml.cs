using System.Windows;
using System.Windows.Controls;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// A miniature, live-updating rendering of a panel's chrome — the "Preview" card shared by
/// <c>PanelCustomizeWindow</c> (a single panel's Appearance section) and Settings' "New panel defaults"
/// section (ADR-042). Rendered with the SAME brushes/converters a real panel uses
/// (<c>theming:PanelTheme.Accent</c>/<c>Text</c>, <c>PanelSurfaceBrush</c>, <c>PanelChromeForegroundBrush</c>)
/// over a two-tone "wallpaper" swatch, so accent/text/opacity/surface style are all visible at a glance —
/// but with vector placeholder tiles, never real shell icons (this control has no file-system access).
/// Every visual input is a plain dependency property rather than a shared ViewModel shape, because the two
/// hosts describe the SAME appearance through differently-named properties
/// (<c>PanelCustomizeViewModel.Appearance.SelectedPanelSurfaceOpacity</c> vs.
/// <c>GlobalDefaultsViewModel.DefaultOpacity</c>, etc.) — each host's own XAML binds this control's
/// properties by an explicit <c>Path</c>, rather than this control assuming a particular DataContext shape.
/// </summary>
public partial class PanelPreviewCard : UserControl
{
    /// <summary>Identifies the <see cref="AccentHex"/> dependency property.</summary>
    public static readonly DependencyProperty AccentHexProperty = DependencyProperty.Register(
        nameof(AccentHex), typeof(string), typeof(PanelPreviewCard), new PropertyMetadata("#3F7FD9"));

    /// <summary>Identifies the <see cref="TextHex"/> dependency property.</summary>
    public static readonly DependencyProperty TextHexProperty = DependencyProperty.Register(
        nameof(TextHex), typeof(string), typeof(PanelPreviewCard), new PropertyMetadata("#FFFFFF"));

    /// <summary>Identifies the <see cref="SurfaceStyle"/> dependency property.</summary>
    public static readonly DependencyProperty SurfaceStyleProperty = DependencyProperty.Register(
        nameof(SurfaceStyle), typeof(PanelSurfaceStyle), typeof(PanelPreviewCard), new PropertyMetadata(PanelSurfaceStyle.Ambient));

    /// <summary>Identifies the <see cref="SurfaceOpacity"/> dependency property.</summary>
    public static readonly DependencyProperty SurfaceOpacityProperty = DependencyProperty.Register(
        nameof(SurfaceOpacity), typeof(double), typeof(PanelPreviewCard), new PropertyMetadata(1.0));

    /// <summary>Identifies the <see cref="IconSize"/> dependency property.</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(PanelPreviewCard), new PropertyMetadata(48.0));

    /// <summary>Identifies the <see cref="PreviewTitle"/> dependency property.</summary>
    public static readonly DependencyProperty PreviewTitleProperty = DependencyProperty.Register(
        nameof(PreviewTitle), typeof(string), typeof(PanelPreviewCard), new PropertyMetadata(string.Empty));

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelPreviewCard"/> class.
    /// </summary>
    public PanelPreviewCard() => InitializeComponent();

    /// <summary>The previewed accent colour, canonical <c>#RRGGBB</c>.</summary>
    public string AccentHex
    {
        get => (string)GetValue(AccentHexProperty);
        set => SetValue(AccentHexProperty, value);
    }

    /// <summary>The previewed text colour, canonical <c>#RRGGBB</c>.</summary>
    public string TextHex
    {
        get => (string)GetValue(TextHexProperty);
        set => SetValue(TextHexProperty, value);
    }

    /// <summary>The previewed surface composition style.</summary>
    public PanelSurfaceStyle SurfaceStyle
    {
        get => (PanelSurfaceStyle)GetValue(SurfaceStyleProperty);
        set => SetValue(SurfaceStyleProperty, value);
    }

    /// <summary>The previewed surface opacity (0-1).</summary>
    public double SurfaceOpacity
    {
        get => (double)GetValue(SurfaceOpacityProperty);
        set => SetValue(SurfaceOpacityProperty, value);
    }

    /// <summary>The previewed icon size (px) — the placeholder tiles scale proportionally within a small, clamped visual range.</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>The previewed panel's title-bar text.</summary>
    public string PreviewTitle
    {
        get => (string)GetValue(PreviewTitleProperty);
        set => SetValue(PreviewTitleProperty, value);
    }
}
