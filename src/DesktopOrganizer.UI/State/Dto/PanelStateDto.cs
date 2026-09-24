namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v4 shape of a single panel (ADR-024 D-9): <c>id</c>/<c>title</c>/<c>pageId</c> at the top
/// level, everything else grouped into <see cref="Geometry"/>/<see cref="Appearance"/>/
/// <see cref="Content"/> — the grouping PHASE-4's target format specifies. Used both for the
/// top-level <c>panels</c> array and for every panel inside a <c>layoutSnapshots[].panels</c> entry
/// (ADR-024 R-11: both places use exactly this same shape). The only place UI types
/// (<c>DesktopOrganizer.UI.Models.ChildWindowModel</c>) meet the storage schema is
/// <see cref="PanelStateMapper"/> — this DTO itself carries no UI-type dependency beyond the two
/// enums re-used from <c>DesktopOrganizer.UI.Models</c> for <see cref="Dto.PanelContentDto"/>.
/// </summary>
internal sealed class PanelStateDto
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string PageId { get; set; } = "default";

    public PanelGeometryDto Geometry { get; set; } = new();

    public PanelAppearanceDto Appearance { get; set; } = new();

    public PanelContentDto Content { get; set; } = new();
}
