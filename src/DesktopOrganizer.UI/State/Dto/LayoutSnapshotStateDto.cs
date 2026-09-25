namespace DesktopOrganizer.UI.State.Dto;

/// <summary>
/// Schema-v3 entry of <c>layoutSnapshots</c> (ADR-024 D-9): a named, point-in-time capture of the
/// whole panel set. <see cref="Panels"/> holds real, typed panel objects — the schema-v1 JSON-string
/// encoding (<c>PanelJsonItems</c>) was unpacked once and for all by <c>V1ToV2Migration</c> and never
/// exists past schema v2.
/// </summary>
internal sealed class LayoutSnapshotStateDto
{
    /// <summary>The snapshot's stable identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The user-facing name given to this snapshot.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When this snapshot was captured — LOCAL time (<see cref="DateTime.Now"/>), not UTC; persisted
    /// verbatim and displayed without conversion.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>The full panel set captured by this snapshot.</summary>
    public List<PanelStateDto> Panels { get; set; } = [];
}
