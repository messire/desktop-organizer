using System.Windows.Controls;

namespace DesktopOrganizer.UI.Views.Settings;

/// <summary>
/// "Layout &amp; snapping" settings section: grid position snapping, drag magnet, icon-grid resize
/// snapping, lock layout, and the normal-mode move/resize gesture policy.
/// </summary>
public partial class LayoutSnappingSection : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="LayoutSnappingSection"/> class.</summary>
    public LayoutSnappingSection() => InitializeComponent();
}
