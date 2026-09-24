using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v3 <c>panels[].content</c> (ADR-024 D-9): what the panel shows and how it is arranged.
/// Mirrors <c>V2ToV3Migration.GroupPanel</c>'s <c>content</c> object field-for-field — see
/// <c>SchemaContractTests</c>. Defaults match <c>ChildWindowModel</c>'s own field initializers.
/// </summary>
internal sealed class PanelContentDto
{
    public PanelViewMode ViewMode { get; set; } = PanelViewMode.List;

    public PanelContentFilter ContentFilter { get; set; } = PanelContentFilter.ShowAll;

    public PanelSortMode SortMode { get; set; } = PanelSortMode.Manual;

    public string? SourceFolderPath { get; set; }

    public List<string> FilePaths { get; set; } = [];
}
