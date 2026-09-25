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
    /// <summary>The panel's stable identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The panel's display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The identifier of the page this panel belongs to.</summary>
    public string PageId { get; set; } = "default";

    /// <summary>The panel's position, size, monitor binding and window-state fields.</summary>
    public PanelGeometryDto Geometry { get; set; } = new();

    /// <summary>The panel's accent/text color, surface style/opacity and icon-size fields.</summary>
    public PanelAppearanceDto Appearance { get; set; } = new();

    /// <summary>What the panel shows and how it is arranged.</summary>
    public PanelContentDto Content { get; set; } = new();
}
