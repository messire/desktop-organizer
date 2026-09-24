namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v3 <c>pages</c> (ADR-024 D-9): the ordered list of page identifiers plus the currently
/// active one. <c>CurrentPageId</c> travels with <see cref="Ids"/> in this single object rather than
/// as two independent keys, so the "<c>CurrentPageId</c> ∈ <c>Ids</c>" invariant is always written
/// atomically together (ADR-024 §0.2).
/// </summary>
internal sealed class PagesStateDto
{
    public List<string> Ids { get; set; } = ["default"];

    public string CurrentPageId { get; set; } = "default";
}
