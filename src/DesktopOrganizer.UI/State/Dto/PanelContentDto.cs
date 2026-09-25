using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v3 <c>panels[].content</c> (ADR-024 D-9): what the panel shows and how it is arranged.
/// Mirrors <c>V2ToV3Migration.GroupPanel</c>'s <c>content</c> object field-for-field — see
/// <c>SchemaContractTests</c>. Defaults match <c>ChildWindowModel</c>'s own field initializers.
/// </summary>
internal sealed class PanelContentDto
{
    /// <summary>Whether the panel lays out items as a grid or a list.</summary>
    public PanelViewMode ViewMode { get; set; } = PanelViewMode.List;

    /// <summary>Which items the panel shows (e.g. all vs. only folders).</summary>
    public PanelContentFilter ContentFilter { get; set; } = PanelContentFilter.ShowAll;

    /// <summary>How the panel orders the items it shows.</summary>
    public PanelSortMode SortMode { get; set; } = PanelSortMode.Manual;

    /// <summary>The mirrored folder path for a Folder Portal panel, or <see langword="null"/> for an ordinary panel.</summary>
    public string? SourceFolderPath { get; set; }

    /// <summary>The panel's item paths, in display order.</summary>
    public List<string> FilePaths { get; set; } = [];
}
