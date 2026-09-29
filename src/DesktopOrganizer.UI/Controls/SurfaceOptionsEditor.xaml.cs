using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.Controls;

/// <summary>
/// Shared "Surface style" (Solid/Ambient/Glass segmented control) + "Opacity" slider pair (backlog 1.x
/// wave 2 review M-G) — hosted inside <see cref="PanelColorEditor.AdvancedExtraContent"/> by both
/// <c>PanelCustomizeWindow</c>'s Appearance section and Settings' "New panel defaults" section, which used
/// to each declare this markup inline (~70 near-identical lines apiece). See this control's own XAML
/// remarks for why it is a dedicated <see cref="UserControl"/> rather than shared markup spliced into a
/// property-element (MC3093 NameScope collision).
/// </summary>
public partial class SurfaceOptionsEditor : UserControl
{
    /// <summary>Identifies the <see cref="SurfaceStyle"/> dependency property.</summary>
    public static readonly DependencyProperty SurfaceStyleProperty = DependencyProperty.Register(
        nameof(SurfaceStyle),
        typeof(PanelSurfaceStyle),
        typeof(SurfaceOptionsEditor),
        new PropertyMetadata(PanelSurfaceStyle.Solid));

    /// <summary>Identifies the <see cref="SurfaceStyleCommand"/> dependency property.</summary>
    public static readonly DependencyProperty SurfaceStyleCommandProperty = DependencyProperty.Register(
        nameof(SurfaceStyleCommand),
        typeof(ICommand),
        typeof(SurfaceOptionsEditor),
        new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="SurfaceOpacity"/> dependency property. Two-way by default.</summary>
    public static readonly DependencyProperty SurfaceOpacityProperty = DependencyProperty.Register(
        nameof(SurfaceOpacity),
        typeof(double),
        typeof(SurfaceOptionsEditor),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>
    /// Initializes a new instance of the <see cref="SurfaceOptionsEditor"/> class.
    /// </summary>
    public SurfaceOptionsEditor() => InitializeComponent();

    /// <summary>The panel/default's current surface style — drives which segmented option shows checked. One-way from the host; the actual mutation happens through <see cref="SurfaceStyleCommand"/>.</summary>
    public PanelSurfaceStyle SurfaceStyle
    {
        get => (PanelSurfaceStyle)GetValue(SurfaceStyleProperty);
        set => SetValue(SurfaceStyleProperty, value);
    }

    /// <summary>Invoked, with the clicked option as its parameter, when the user picks a surface style.</summary>
    public ICommand? SurfaceStyleCommand
    {
        get => (ICommand?)GetValue(SurfaceStyleCommandProperty);
        set => SetValue(SurfaceStyleCommandProperty, value);
    }

    /// <summary>The panel/default's surface opacity (0.35–1.0). Two-way bindable.</summary>
    public double SurfaceOpacity
    {
        get => (double)GetValue(SurfaceOpacityProperty);
        set => SetValue(SurfaceOpacityProperty, value);
    }
}
