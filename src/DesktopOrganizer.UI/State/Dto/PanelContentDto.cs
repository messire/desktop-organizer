using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopOrganizer.UI.Models;

namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v3 <c>panels[].content</c> (ADR-024 D-9): what the panel shows and how it is arranged.
/// Mirrors <c>V2ToV3Migration.GroupPanel</c>'s <c>content</c> object field-for-field — see
/// <c>SchemaContractTests</c>. Defaults match <c>ChildWindowModel</c>'s own field initializers.
/// </summary>
internal sealed class PanelContentDto
{
    /// <summary>Member name of <see cref="PanelViewMode"/> (grid or list). Plain string — see <see cref="PanelGeometryDto.GeometryMode"/>.</summary>
    public string ViewMode { get; set; } = nameof(PanelViewMode.List);

    /// <summary>Member name of <see cref="PanelContentFilter"/> (e.g. all vs. only folders). Plain string — see <see cref="PanelGeometryDto.GeometryMode"/>.</summary>
    public string ContentFilter { get; set; } = nameof(PanelContentFilter.ShowAll);

    /// <summary>Member name of <see cref="PanelSortMode"/>. Plain string — see <see cref="PanelGeometryDto.GeometryMode"/>.</summary>
    public string SortMode { get; set; } = nameof(PanelSortMode.Manual);

    /// <summary>The mirrored folder path for a Folder Portal panel, or <see langword="null"/> for an ordinary panel.</summary>
    public string? SourceFolderPath { get; set; }

    /// <summary>The panel's item paths, in display order.</summary>
    public List<string> FilePaths { get; set; } = [];

    /// <summary>
    /// Unknown JSON keys of this object, captured on load and written back verbatim on save (ADR-024
    /// Amendment "Additive revisions"): a field added by a newer additive schema revision survives an edit
    /// by this build. Keys are written exactly as read (no naming policy). <see langword="null"/> when there
    /// are none.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; set; }
}
