namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v6 <c>pages</c> (ADR-043, docs/PLANS/page-display-names.md §2/§3.2): the ordered list of
/// pages — each a stable <see cref="PageStateDto.Id"/> plus an optional display
/// <see cref="PageStateDto.Name"/> — plus the currently active page id. <see cref="CurrentPageId"/>
/// travels with <see cref="Items"/> in this single object rather than as two independent keys, so the
/// "<c>CurrentPageId</c> ∈ ids" invariant is always written atomically together (ADR-024 §0.2).
/// </summary>
internal sealed class PagesStateDto
{
    /// <summary>The ordered list of pages. Schema v6 no longer writes a parallel <c>ids</c> array — this is the single source of truth.</summary>
    public List<PageStateDto> Items { get; set; } = [new() { Id = "default" }];

    /// <summary>The identifier of the currently active page. Written consistent with <see cref="Items"/>; not validated on load.</summary>
    public string CurrentPageId { get; set; } = "default";
}
