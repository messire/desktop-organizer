namespace DesktopOrganizer.UI.ViewModels;

/// <summary>
/// Dataless sentinel item appended after <see cref="PanelColorEditorViewModel.Presets"/> in
/// <see cref="PanelColorEditorViewModel.SwatchItems"/> — represents the 13th "Custom…"
/// cell so it renders inside the SAME <see cref="System.Windows.Controls.WrapPanel"/> flow, Tab stop, and
/// arrow-key cycle as the 12 preset swatches, instead of a separate sibling control the grid's own wrap
/// layout cannot see (release-polish-1.0 visual review item 1). Carries no state of its own — its
/// <c>DataTemplate</c> (declared in <c>PanelColorEditor.xaml</c>'s <c>ItemsControl.Resources</c>) reaches
/// <see cref="PanelColorEditorViewModel.PickCustomAccentCommand"/> via <c>ElementName=Root</c> (this
/// UserControl's own <c>DataContext</c>), not through this marker.
/// </summary>
public sealed class CustomSwatchSlotMarker
{
}
